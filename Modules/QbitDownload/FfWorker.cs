using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Нативный GPU ffmpeg-воркер на Windows-хосте (NVENC) ─────────────────────────
// NVENC в Docker/WSL2 не работает (§Y), поэтому тяжёлый транскод уходит на хостовую
// службу ffmpeg-worker. Данные идут через ОБЩИЙ ДИСК (тома /downloads, /qdl-hls) —
// по HTTP только управление: короткие stateless-запросы (запуск/статус/kill) с
// ретраями. Воркер недоступен → прозрачный фолбэк на локальный CPU-ffmpeg
// (поведение как до внедрения воркера). Обрыв HTTP не рвёт транскод: ffmpeg на
// хосте продолжает писать на диск, poll просто ретраится.

/// <summary>Запущенный ffmpeg: локальный процесс или удалённый джоб воркера.</summary>
interface IFfJob
{
    bool HasExited { get; }
    int ExitCode { get; }        // валиден после завершения; -1 = аварийный (воркер потерян/kill)
    long OutTimeUs { get; }      // прогресс из -progress pipe:1 (микросекунды)
    string StderrTail { get; }
    Task WaitForExitAsync();
    void Kill();
}

static class FfJob
{
    /// <summary>
    /// Запуск ffmpeg: воркер жив → удалённый джоб с nvenc-аргументами (args(true)),
    /// иначе локальный процесс с CPU-аргументами (args(false)) — nvenc-args в
    /// контейнерный ffmpeg не попадают никогда.
    /// </summary>
    public static IFfJob Start(Func<bool, List<string>> args, string type, bool preferRemote = true)
    {
        if (preferRemote && FfWorker.IsAlive())
        {
            var remote = RemoteFfJob.TryStart(args(true), type);
            if (remote != null) return remote;
            FfWorker.MarkDead();   // submit не прошёл (сеть/429) → не долбить до следующего health
        }
        return LocalFfJob.Start(args(false));
    }

    public static IFfJob StartLocal(List<string> args) => LocalFfJob.Start(args);
}

/// <summary>Локальный ffmpeg в контейнере — поведение как до воркера (CPU).</summary>
sealed class LocalFfJob : IFfJob
{
    Process _p;
    long _outUs;
    int _exit = -1;
    volatile bool _exited;
    readonly StringBuilder _err = new StringBuilder();
    readonly object _errLock = new object();
    readonly TaskCompletionSource _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    const int ErrCap = 4096;

    public bool HasExited => _exited;
    public int ExitCode => _exit;
    public long OutTimeUs => Interlocked.Read(ref _outUs);
    public string StderrTail { get { lock (_errLock) return _err.ToString(); } }
    public Task WaitForExitAsync() => _tcs.Task;

    public static LocalFfJob Start(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ModInit.conf.ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var job = new LocalFfJob();
        var p = Process.Start(psi);
        job._p = p;

        _ = Task.Run(async () =>
        {
            try
            {
                var errTask = job.ReadErrAsync(p);
                string line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    // -progress pipe:1: out_time_ms= на самом деле МИКРОсекунды (грабля ffmpeg)
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                        line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.Substring(12), out long us) && us > 0)
                            Interlocked.Exchange(ref job._outUs, us);
                    }
                }
                await p.WaitForExitAsync();
                await errTask;
            }
            catch { }
            try { job._exit = p.ExitCode; } catch { }
            job._exited = true;
            job._tcs.TrySetResult();
        });
        return job;
    }

    async Task ReadErrAsync(Process p)
    {
        try
        {
            string line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
            {
                lock (_errLock)
                {
                    _err.AppendLine(line);
                    if (_err.Length > ErrCap)
                        _err.Remove(0, _err.Length - ErrCap);
                }
            }
        }
        catch { }
    }

    public void Kill()
    {
        try
        {
            if (_p != null && !_p.HasExited)
            {
                _p.Kill(entireProcessTree: true);
                _p.WaitForExit(3000);
            }
        }
        catch { }
    }
}

/// <summary>
/// Джоб на хостовом воркере. Фоновый poll GET /jobs/{id} — он же keepalive
/// (без poll'а воркер убьёт джоб watchdog-ом: hls 60с / mp4 300с).
/// Обрыв HTTP → backoff-ретраи, состояние остаётся running (ffmpeg на хосте жив
/// и пишет на общий диск); недоступность дольше дедлайна → считаем джоб убитым.
/// </summary>
sealed class RemoteFfJob : IFfJob
{
    string _id;
    string _type;
    long _outUs;
    int _exit = -1;
    volatile bool _exited;
    volatile string _errTail = "";
    readonly TaskCompletionSource _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasExited => _exited;
    public int ExitCode => _exit;
    public long OutTimeUs => Interlocked.Read(ref _outUs);
    public string StderrTail => _errTail;
    public Task WaitForExitAsync() => _tcs.Task;

    /// <summary>null = не удалось отправить (сеть/занят) → вызывающий падает на локальный CPU.</summary>
    public static RemoteFfJob TryStart(List<string> args, string type)
    {
        string id = Guid.NewGuid().ToString("N");   // клиентский GUID → ретрай POST идемпотентен
        var body = new JObject
        {
            ["id"] = id,
            ["type"] = type,
            ["args"] = new JArray(args),
            ["clientBoot"] = FfWorker.BootId
        };
        var resp = FfWorker.PostJob(body);
        if (resp == null) return null;

        var job = new RemoteFfJob { _id = id, _type = type };
        job.StartPolling();
        Console.WriteLine("[QbitDownload] ffworker job " + id + " (" + type + ") запущен, pid=" + resp.Value<int?>("pid"));
        return job;
    }

    void StartPolling()
    {
        // интервал заметно меньше orphan-таймаута воркера; дедлайн — таймаут + запас
        int intervalMs = _type == "hls" ? 5000 : 2000;
        int deadlineMs = _type == "hls" ? 90_000 : 360_000;

        _ = Task.Run(async () =>
        {
            DateTime? unreachableSince = null;
            while (!_exited)
            {
                await Task.Delay(intervalMs);
                if (_exited) break;

                var st = FfWorker.GetJob(_id);
                if (st == null)   // сеть/таймаут — ffmpeg на хосте скорее всего жив, ретраим
                {
                    unreachableSince ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - unreachableSince.Value).TotalMilliseconds > deadlineMs)
                        Finish(-1, "ffworker недоступен > " + deadlineMs / 1000 + "с — джоб считается убитым watchdog-ом");
                    continue;
                }
                unreachableSince = null;

                if (st.Value<int?>("_http") == 404)   // воркер перезапускался — джоб потерян
                {
                    Finish(-1, "ffworker: джоб потерян (рестарт воркера)");
                    continue;
                }

                long us = st.Value<long?>("outTimeUs") ?? 0;
                if (us > 0) Interlocked.Exchange(ref _outUs, us);

                string state = st.Value<string>("state");
                if (state != null && state != "running")
                    Finish(state == "done" ? 0 : (st.Value<int?>("exitCode") ?? -1),
                           st.Value<string>("stderrTail") ?? "");
            }
        });
    }

    void Finish(int exit, string err)
    {
        _exit = exit;
        if (!string.IsNullOrEmpty(err)) _errTail = err;
        _exited = true;
        _tcs.TrySetResult();
    }

    public void Kill()
    {
        FfWorker.KillJob(_id, deleteOutput: _type == "mp4");
        Finish(-1, "killed");   // poll-цикл увидит _exited и завершится
    }
}

/// <summary>HTTP-клиент воркера: health-кэш, короткие запросы с ретраями.</summary>
static class FfWorker
{
    /// <summary>GUID запуска модуля — воркер по нему добивает сирот прошлых запусков (reap).</summary>
    public static readonly string BootId = Guid.NewGuid().ToString("N");

    static readonly HttpClient _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan   // таймаут per-request через CTS
    };

    static volatile bool _alive;
    static DateTime _checked = DateTime.MinValue;
    static readonly object _hlock = new object();

    static string Url => (ModInit.conf?.ffworkerUrl ?? "").TrimEnd('/');
    public static bool Enabled => !string.IsNullOrEmpty(Url);

    /// <summary>
    /// Воркер жив и NVENC доступен. Кэш: alive 30с, dead 15с (быстрее заметить возврат),
    /// чтобы не долбить /health на каждый сегмент.
    /// </summary>
    public static bool IsAlive()
    {
        if (!Enabled) return false;
        if (FreshCache()) return _alive;
        lock (_hlock)
        {
            if (FreshCache()) return _alive;
            var h = Request(HttpMethod.Get, "/health", null, 1500);
            bool alive = h != null && h.Value<int?>("_http") == 200
                         && h.Value<bool?>("ok") == true && h.Value<bool?>("nvenc") == true;
            if (alive != _alive)
                Console.WriteLine("[QbitDownload] ffworker " + (alive ? "доступен (NVENC)" : "недоступен → CPU-фолбэк"));
            _alive = alive;
            _checked = DateTime.UtcNow;
            return _alive;
        }
    }

    static bool FreshCache() => (DateTime.UtcNow - _checked).TotalSeconds < (_alive ? 30 : 15);

    public static void MarkDead() { _alive = false; _checked = DateTime.UtcNow; }

    /// <summary>POST /jobs c одним ретраем (тот же id → дубль невозможен). null = фолбэк на CPU.</summary>
    public static JObject PostJob(JObject body)
    {
        int timeout = Math.Max(1000, ModInit.conf.ffworkerTimeoutMs);
        var r = Request(HttpMethod.Post, "/jobs", body, timeout)
                ?? Request(HttpMethod.Post, "/jobs", body, timeout);
        if (r == null) return null;
        if (r.Value<int?>("_http") != 200)
        {
            Console.WriteLine("[QbitDownload] ffworker POST /jobs: http=" + r.Value<int?>("_http") + " " + r.Value<string>("error"));
            return null;   // 429 busy / 400 / 500 → локальный CPU
        }
        return r;
    }

    /// <summary>GET /jobs/{id}: null = сетевая ошибка (ретраится снаружи), _http=404 = джоб потерян.</summary>
    public static JObject GetJob(string id)
        => Request(HttpMethod.Get, "/jobs/" + id, null, 3000);

    public static void KillJob(string id, bool deleteOutput)
    {
        string path = "/jobs/" + id + (deleteOutput ? "?deleteOutput=1" : "");
        for (int i = 0; i < 3; i++)
            if (Request(HttpMethod.Delete, path, null, 2000) != null)
                return;
        // DELETE не дошёл — воркер добьёт джоб watchdog-ом за ≤60с/300с (keepalive прекратился)
        Console.WriteLine("[QbitDownload] ffworker DELETE " + id + " не дошёл — добьёт watchdog");
    }

    /// <summary>Добить сирот прошлых запусков lampac (fire-and-forget из ModInit).</summary>
    public static void ReapOrphans()
    {
        if (!Enabled) return;
        _ = Task.Run(() =>
        {
            var r = Request(HttpMethod.Post, "/jobs/reap", new JObject { ["clientBoot"] = BootId }, 3000);
            if (r != null && r.Value<int?>("killed") > 0)
                Console.WriteLine("[QbitDownload] ffworker reap: убито сирот " + r.Value<int?>("killed"));
        });
    }

    /// <summary>Короткий HTTP-запрос. null = сеть/таймаут; иначе JSON-ответ + поле _http (код).</summary>
    static JObject Request(HttpMethod method, string path, JObject body, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var req = new HttpRequestMessage(method, Url + path);
            req.Headers.TryAddWithoutValidation("X-Auth-Token", ModInit.conf?.ffworkerToken ?? "");
            if (body != null)
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

            using var resp = _http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
            string txt = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            JObject j;
            try { j = string.IsNullOrWhiteSpace(txt) ? new JObject() : JObject.Parse(txt); }
            catch { j = new JObject(); }
            j["_http"] = (int)resp.StatusCode;
            return j;
        }
        catch { return null; }
    }
}
