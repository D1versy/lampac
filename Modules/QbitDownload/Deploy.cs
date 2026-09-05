using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

/// <summary>
/// Бесшовный редеплой (qdl 2.110): роли экземпляра при blue/green-выкатке.
///
/// Механика (канон — медиасервер claude/04, разбор — claude/06): lampac живёт в двух цветах
/// (контейнеры lampac-blue / lampac-green), Caddy держит LAN-порт 9118 и шлёт трафик тому,
/// кто отвечает 200 на GET /qdl/deploy/ready. Кто «назван активным» — решает файл
/// {cachePath}/deploy/active (одно слово blue|green|none) на именованном томе qdl-data;
/// кто РЕАЛЬНО ведёт фоновую работу — файловая аренда {cachePath}/deploy/lock (FileShare.None
/// на Linux = flock LOCK_EX, том ext4, ядро одно → взаимоисключение гарантировано, смерть
/// процесса освобождает). 🔴 Lock-файл никогда не открывать на чтение: любое открытие
/// берёт LOCK_SH и ломает захват — данные лежат в отдельных файлах.
///
/// Состояния:
///   • legacy  — переменной D1V_COLOR нет (реплика tv2, тесты, старый одиночный запуск):
///               всё ниже выключено, Loaded() ведёт себя как раньше, ready = 200.
///   • standby — стартовали, но назван другой цвет: общий init без таймеров и one-shot'ов,
///               записи JsonStore на диск закрыты, один проход прогрева без сохранения
///               состояния прогрева; ready = 503. Названный цвет пробует аренду каждые 200 мс.
///   • active  — назван и аренда взята → Promote(): забыть РАМ-копии сторов БЕЗ флаша
///               (диск только что записал предыдущий экземпляр), перечитать, включить записи,
///               затем сегодняшний холодный старт модуля (ModInit.Activate). При свежем маркере
///               чистой передачи уборка после падения (.part, reap воркера) пропускается.
///   • frozen  — были активны, назвали другого → Freeze() в два шага. Шаг 1: ready = 503,
///               но записи ещё включены и аренда у нас — ждём, пока Caddy перестанет слать
///               (тишина 1.5 с, кап 5 с). Шаг 2: Draining, таймеры off, дождаться воркеров
///               закачек, убить свои HLS-сессии, флаш, маркер чистой передачи, записи off,
///               закрыть /nws, отдать аренду. Запросы в полёте продолжают отдаваться.
///               Frozen → active снова, если цвет вернули (откат за секунды).
///
/// Почему записи закрываются только на шаге 2, а не сразу: JsonStore пишет документы целиком
/// из РАМ, и «принять запись, которую не сможем сохранить» хуже, чем секунду обслуживать как
/// полностью активный — пока аренда у нас, второй экземпляр всё равно не пишет.
/// </summary>
public static class Deploy
{
    public enum Mode { Legacy, Standby, Active, Frozen }

    public const string Blue = "blue", Green = "green", None = "none";

    /// <summary>Свой цвет из D1V_COLOR; null = legacy.</summary>
    public static string Color { get; private set; }
    public static bool Enabled => Color != null;

    static volatile Mode _mode = Mode.Legacy;
    public static Mode Current => _mode;

    /// <summary>Что отвечает /qdl/deploy/ready (200/503) — по нему Caddy выбирает апстрим.</summary>
    public static bool Ready => _mode == Mode.Legacy || _mode == Mode.Active;

    /// <summary>Сохранять ли состояние прогревов (catalog/music/online-warm.json): только у ведущего.</summary>
    public static bool WarmSavesAllowed => !Enabled || _mode == Mode.Active;

    /// <summary>Шаг 2 заморозки: рабочие циклы (закачки, транскод, проходы охоты) не берут новое.</summary>
    public static volatile bool Draining;
    static CancellationTokenSource _drainCts = new();
    public static CancellationToken DrainToken => _drainCts.Token;

    /// <summary>Холодный старт (нет свежего маркера чистой передачи) → уборка после падения.</summary>
    public static bool ColdStart { get; private set; } = true;
    public static DateTime PromotedAt { get; private set; } = DateTime.MinValue;

    /// <summary>Первые минуты после promote: сторож усыновления HLS (чужой ffmpeg мог ещё дописывать).</summary>
    public static bool InHandoffWindow => Enabled && (DateTime.UtcNow - PromotedAt) < TimeSpan.FromMinutes(3);

    static Action _activate, _deactivate;
    static Timer _poll;
    static FileStream _lease;
    static readonly object _lock = new();
    static int _ticking;
    static bool _fastPoll;
    static volatile bool _freezing;
    static DateTime _since = DateTime.UtcNow;
    static string _lastNamed;
    static volatile string _warmCatalog = "pending", _warmMusic = "pending", _warmOnline = "pending";
    static string _lastFreezeLog;

    // тайминги — internal, чтобы тесты могли ужать ожидания
    internal static TimeSpan PollPeriod = TimeSpan.FromSeconds(2);
    internal static TimeSpan LeaseRetry = TimeSpan.FromMilliseconds(200);
    internal static TimeSpan QuietFor = TimeSpan.FromSeconds(1.5);
    internal static TimeSpan QuietCap = TimeSpan.FromSeconds(5);
    internal static TimeSpan WorkersWait = TimeSpan.FromSeconds(10);
    internal static TimeSpan HandoffFresh = TimeSpan.FromMinutes(10);
    internal static bool StandbyWarm = true;   // тесты выключают: прогрев ходит в сеть

    static string StateDir => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "deploy");
    internal static string ActivePath => Path.Combine(StateDir, "active");
    internal static string LockPath => Path.Combine(StateDir, "lock");
    internal static string HandoffPath => Path.Combine(StateDir, "handoff-clean");

    #region старт и роли

    /// <summary>
    /// Точка входа из ModInit.Loaded(). activate — сегодняшний старт модуля (one-shot'ы, таймеры,
    /// восстановление очередей), deactivate — остановка таймеров + флаш (тело Dispose).
    /// </summary>
    public static void Start(Action activate, Action deactivate, string colorOverride = null)
    {
        _activate = activate;
        _deactivate = deactivate;
        _since = DateTime.UtcNow;
        Color = NormColor(colorOverride ?? Environment.GetEnvironmentVariable("D1V_COLOR"));

        if (Color == null)
        {
            _mode = Mode.Legacy;
            ColdStart = true;
            activate();
            return;
        }

        try { Directory.CreateDirectory(StateDir); } catch { }
        _lastNamed = ReadNamed() ?? Blue;
        Console.WriteLine($"[QbitDownload] deploy: цвет {Color}, назван {_lastNamed}");

        lock (_lock)
        {
            if (_lastNamed == Color && TryLease()) Promote();
            else EnterStandby();
        }

        _poll?.Dispose();
        _poll = new Timer(_ => Tick(), null, PollPeriod, PollPeriod);
    }

    static void EnterStandby()
    {
        _mode = Mode.Standby;
        JsonStore.WritesEnabled = false;
        Console.WriteLine("[QbitDownload] deploy: режим standby — фон выключен, записи на диск закрыты, прогрев без сохранения");
        if (StandbyWarm) KickStandbyWarm();
        else _warmCatalog = _warmMusic = _warmOnline = "done";
    }

    /// <summary>Один опрос файла active; идемпотентен, повторный вход не допускается.</summary>
    internal static void Tick()
    {
        if (!Enabled) return;
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try
        {
            string named = ReadNamed();
            if (named == null) return;   // сбой чтения или мусор — без изменений
            if (named != _lastNamed)
            {
                Console.WriteLine($"[QbitDownload] deploy: назван {named} (был {_lastNamed})");
                _lastNamed = named;
            }
            bool me = named == Color;

            lock (_lock)
            {
                switch (_mode)
                {
                    case Mode.Standby:
                    case Mode.Frozen:
                        if (me && !_freezing)
                        {
                            if (TryLease()) { Promote(); SetFastPoll(false); }
                            else SetFastPoll(true);   // аренда ещё у предыдущего — добиваем по 200 мс
                        }
                        else SetFastPoll(false);
                        break;

                    case Mode.Active:
                        if (!me && !_freezing)
                        {
                            _freezing = true;
                            _ = Task.Run(FreezeAsync);
                        }
                        break;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] deploy tick: " + ex.Message); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    static void SetFastPoll(bool on)
    {
        if (_fastPoll == on || _poll == null) return;
        _fastPoll = on;
        try { _poll.Change(on ? LeaseRetry : PollPeriod, on ? LeaseRetry : PollPeriod); } catch { }
    }

    /// <summary>Стать ведущим. Зовётся под _lock, аренда уже у нас.</summary>
    static void Promote()
    {
        bool clean = false;
        try
        {
            if (File.Exists(HandoffPath))
            {
                clean = (DateTime.UtcNow - File.GetLastWriteTimeUtc(HandoffPath)) < HandoffFresh;
                File.Delete(HandoffPath);
            }
        }
        catch { }
        ColdStart = !clean;

        Draining = false;
        _drainCts = new CancellationTokenSource();

        // 🔴 Забыть БЕЗ флаша: диск только что записал предыдущий экземпляр, а наша РАМ-копия
        // (standby или наш же прежний active до отката) устарела. ResetForConfigReload здесь
        // нельзя — он сперва флашит и затёр бы чужие файлы.
        try { JsonStore.ForgetAllNoFlush(); } catch { }
        try { DownloadWants.ResetNoFlush(); } catch { }
        try { Perms.ResetForConfigReload(); } catch { }
        try { Groups.ResetForConfigReload(); } catch { }
        try { QbitController.JutIdxReset(); } catch { }
        try { QbitController.DropListCache(); } catch { }
        try { QbitController.DropProgressCache(); } catch { }
        try { QbitController.SeriesIndexDrop(); } catch { }
        try { HealthState.Reload(); } catch { }
        try { CatalogWarmup.Reload(); } catch { }
        try { MusicWarm.Reload(); } catch { }
        try { OnlineWarm.Reset(); } catch { }

        JsonStore.WritesEnabled = true;
        _mode = Mode.Active;
        PromotedAt = DateTime.UtcNow;
        Console.WriteLine($"[QbitDownload] deploy: PROMOTE → active ({(clean ? "чистая передача: .part/reap пропущены" : "холодный старт: уборка после падения")})");

        try { _activate?.Invoke(); }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] deploy activate: " + ex); }
    }

    /// <summary>Заморозка в два шага (см. шапку). Идёт в фоне: ждёт тишины и воркеров.</summary>
    static async Task FreezeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // ── шаг 1: перестать быть «готовым», но обслуживать как активный, пока Caddy не заметит ──
            _mode = Mode.Frozen;
            var t0 = DateTime.UtcNow;
            while (DateTime.UtcNow - t0 < QuietCap)
            {
                if (DateTime.UtcNow - LastRequestAt >= QuietFor) break;
                await Task.Delay(100);
            }
            long quietMs = (long)sw.Elapsed.TotalMilliseconds;

            // ── шаг 2: фон off, потоки в полёте — доиграть, состояние — на диск, аренду — отдать ──
            Draining = true;
            try { _drainCts.Cancel(); } catch { }
            try { _deactivate?.Invoke(); } catch (Exception ex) { Console.WriteLine("[QbitDownload] deploy deactivate: " + ex.Message); }

            var t1 = DateTime.UtcNow;
            while (QbitController.GrabWorkersBusy && DateTime.UtcNow - t1 < WorkersWait)
                await Task.Delay(100);
            bool workersLeft = QbitController.GrabWorkersBusy;

            int hls = 0;
            try { hls = QbitController.KillAllHls(); } catch { }

            // воркеры могли дописать маркеры уже после первого флаша в Deactivate — повторяем
            try { HealthState.FlushIfDirty(); } catch { }
            try { DownloadWants.Flush(); } catch { }
            try { JsonStore.Flush(); } catch { }

            try { File.WriteAllText(HandoffPath, Color + " " + DateTime.UtcNow.ToString("o")); } catch { }

            JsonStore.WritesEnabled = false;
            CloseNws();
            ReleaseLease();

            _lastFreezeLog = $"тишина {quietMs} мс, воркеры {(workersLeft ? "НЕ дождались" : "остановлены")}, HLS убито {hls}, всего {sw.ElapsedMilliseconds} мс";
            Console.WriteLine("[QbitDownload] deploy: FREEZE → frozen (" + _lastFreezeLog + "); потоки в полёте: " + Interlocked.Read(ref Shared.Startup.Inflight));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] deploy freeze: " + ex); }
        finally { _freezing = false; }
    }

    static void CloseNws()
    {
        try
        {
            var nws = Shared.Startup.Nws;
            if (nws == null) return;
            var all = nws.AllConnections();
            if (all == null || all.Count == 0) return;
            int n = all.Count;
            foreach (var kv in all)
            {
                var c = kv.Value;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        if (c.Socket.State == System.Net.WebSockets.WebSocketState.Open)
                            await c.Socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, "deploy", cts.Token);
                    }
                    catch { }
                });
            }
            Console.WriteLine("[QbitDownload] deploy: закрываю /nws — соединений " + n + " (клиенты переподключатся к новому цвету)");
        }
        catch { }
    }

    #endregion

    #region аренда и файл active

    static bool TryLease()
    {
        if (_lease != null) return true;
        try
        {
            Directory.CreateDirectory(StateDir);
            _lease = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static void ReleaseLease()
    {
        try { _lease?.Dispose(); } catch { }
        _lease = null;
    }

    public static bool HoldsLease => _lease != null;

    static string NormColor(string s)
    {
        s = s?.Trim().ToLowerInvariant();
        return s == Blue || s == Green ? s : null;
    }

    /// <summary>blue|green|none; нет файла → blue; мусор или сбой чтения → null (= без изменений).</summary>
    internal static string ReadNamed()
    {
        try
        {
            if (!File.Exists(ActivePath)) return Blue;
            string s = File.ReadAllText(ActivePath).Trim().ToLowerInvariant();
            if (s == None) return None;
            return NormColor(s);
        }
        catch { return null; }
    }

    /// <summary>Назвать активный цвет: атомарно (tmp + rename) и сразу опросить.</summary>
    public static bool WriteNamed(string color)
    {
        string c = color?.Trim().ToLowerInvariant();
        if (c != Blue && c != Green && c != None) return false;
        try
        {
            Directory.CreateDirectory(StateDir);
            string tmp = ActivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, c + "\n");
            File.Move(tmp, ActivePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] deploy: не записался active — " + ex.Message);
            return false;
        }
        Tick();
        return true;
    }

    #endregion

    #region прогрев дежурного и статус

    static void KickStandbyWarm()
    {
        _ = Task.Run(async () =>
        {
            _warmCatalog = "running";
            try { await CatalogWarmup.Tick(); } catch (Exception ex) { Console.WriteLine("[QbitDownload] deploy warm catalog: " + ex.Message); }
            _warmCatalog = "done";

            _warmMusic = "running";
            try { await MusicWarm.Tick(); } catch (Exception ex) { Console.WriteLine("[QbitDownload] deploy warm music: " + ex.Message); }
            _warmMusic = "done";

            _warmOnline = "running";
            try { await OnlineWarm.Tick(); } catch (Exception ex) { Console.WriteLine("[QbitDownload] deploy warm online: " + ex.Message); }
            _warmOnline = "done";

            Console.WriteLine("[QbitDownload] deploy: прогрев дежурного завершён");
        });
    }

    /// <summary>
    /// Готовность дежурного: каталог и музыка. Прогрев «Онлайн» в готовность НЕ входит — он
    /// намеренно медленный (пауза onlineWarmPaceMs между карточками, минуты) и продолжает греть
    /// уже под трафиком; ждать его значило бы держать деплой 5-10 минут.
    /// </summary>
    public static bool WarmDone => _warmCatalog == "done" && _warmMusic == "done";

    static DateTime LastRequestAt
    {
        get
        {
            long t = Interlocked.Read(ref Shared.Startup.LastRequestTicks);
            return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
        }
    }

    public static JObject Status()
    {
        var warm = new JObject
        {
            ["catalog"] = Enabled ? _warmCatalog : "n/a",
            ["music"] = Enabled ? _warmMusic : "n/a",
            ["online"] = Enabled ? _warmOnline : "n/a",
            ["done"] = !Enabled || WarmDone
        };
        var last = LastRequestAt;
        return new JObject
        {
            ["color"] = Color,
            ["named"] = Enabled ? _lastNamed : null,
            ["mode"] = _mode.ToString().ToLowerInvariant(),
            ["ready"] = Ready,
            ["lease"] = HoldsLease,
            ["draining"] = Draining,
            ["coldStart"] = ColdStart,
            ["inflight"] = Interlocked.Read(ref Shared.Startup.Inflight),
            ["lastRequestAgoMs"] = last == DateTime.MinValue ? -1 : (long)(DateTime.UtcNow - last).TotalMilliseconds,
            ["transcodeActive"] = QbitController.TranscodeActive,
            ["hls"] = QbitController.HlsRunningCount,
            ["workers"] = QbitController.GrabWorkersBusy,
            ["warm"] = warm,
            ["since"] = _since,
            ["promotedAt"] = PromotedAt == DateTime.MinValue ? null : (DateTime?)PromotedAt,
            ["lastFreeze"] = _lastFreezeLog
        };
    }

    #endregion

    /// <summary>Только для тестов: отдать аренду, снять таймер, вернуть legacy.</summary>
    internal static void ResetForTests()
    {
        lock (_lock)
        {
            _poll?.Dispose(); _poll = null;
            ReleaseLease();
            Color = null;
            _mode = Mode.Legacy;
            Draining = false;
            _drainCts = new CancellationTokenSource();
            ColdStart = true;
            PromotedAt = DateTime.MinValue;
            _freezing = false;
            _fastPoll = false;
            _lastNamed = null;
            _lastFreezeLog = null;
            _warmCatalog = _warmMusic = _warmOnline = "pending";
            JsonStore.WritesEnabled = true;
        }
    }

    /// <summary>Только для тестов: дождаться конца фоновой заморозки.</summary>
    internal static async Task WaitFreezeForTests(int timeoutMs = 20000)
    {
        var t0 = DateTime.UtcNow;
        while (_freezing && (DateTime.UtcNow - t0).TotalMilliseconds < timeoutMs)
            await Task.Delay(20);
    }
}

public partial class QbitController
{
    /// <summary>200 только у ведущего (или в legacy). Его зовёт Caddy до 6 раз в секунду — дёшево.</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/deploy/ready")]
    public ActionResult DeployReady()
    {
        HttpContext.Response.Headers["Cache-Control"] = "no-store";
        if (Deploy.Ready)
            return Content(Deploy.Current.ToString().ToLowerInvariant(), "text/plain");
        HttpContext.Response.StatusCode = 503;
        return Content(Deploy.Current.ToString().ToLowerInvariant(), "text/plain");
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/deploy/status")]
    public ActionResult DeployStatus()
    {
        HttpContext.Response.Headers["Cache-Control"] = "no-store";
        return ContentTo(Deploy.Status().ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    /// <summary>
    /// Назвать активный цвет (переключение и откат). Только «свой» вызов: заголовок lcrqpasswd
    /// с рут-паролем (RequestInfo → IsLocalRequest), и никогда через edge — снаружи маркер.
    /// Скрипт деплоя зовёт его по диагностическому loopback-порту нужного контейнера.
    /// </summary>
    [HttpPost, AllowAnonymous]
    [Route("qdl/deploy/name")]
    public ActionResult DeployName(string color)
    {
        HttpContext.Response.Headers["Cache-Control"] = "no-store";
        if (requestInfo == null || !requestInfo.IsLocalRequest)
            return StatusCode(403, new { success = false, error = "нужен lcrqpasswd" });
        string edge = CoreInit.conf?.d1v?.edgeHeader;
        if (!string.IsNullOrEmpty(edge) && HttpContext.Request.Headers.ContainsKey(edge))
            return StatusCode(403, new { success = false, error = "external" });
        if (!Deploy.Enabled)
            return Json(new { success = false, error = "legacy-режим: цвета нет" });
        if (!Deploy.WriteNamed(color))
            return BadRequest(new { success = false, error = "color: blue|green|none" });
        return Json(new { success = true, named = Deploy.ReadNamed(), status = Deploy.Status() });
    }
}
