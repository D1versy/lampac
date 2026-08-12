using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Npgsql;
using Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Хелс-чеки внешних сервисов: GET /qdl/health (qdl 2.39) ──
// Питает экран «Хелс-чеки» в настройках Lampa (qdl.js, виден только при куке qdl_unlock=1).
// Три принципа:
//   1. «Выключено» ≠ «отвалилось». Пустая строка конфига у нас всюду означает киллсвитч —
//      такой сервис отдаётся как off (⏸), а не как сбой.
//   2. Секреты не светим. detail — это HTTP-код, версия или ИМЯ типа исключения; message
//      исключения не берём никогда (там всплывают хосты, порты и куски строк подключения).
//   3. Дёшево. Живые пробы идут параллельно с таймаутом 2-3 с, вердикты, которые модуль
//      и так держит (ffworker, канарейки поиска, авторизация jut), берём из памяти/диска
//      без сети. Ответ целиком кешируется на healthCacheSeconds, чтобы открытый экран
//      не превращался в долбёжку внешних API.
// Дорогая полная диагностика живёт отдельно и сюда не тащится: /qdl/jut/diag (реальные
// запросы к jut.su и платные прокси-выходы) и /qdl/diag/search?dry=1 (прогон канареек 5-40 с).
//
// Базовый тип объявлен в Controller.cs — в partial-классе он указывается один раз
// (иначе CS0246, модуль молча не грузится и ВСЕ /qdl/* отдают 404).
public partial class QbitController
{
    static readonly HttpClient _healthHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan   // таймаут per-probe через CTS
    };

    static JArray _healthCache;
    static DateTime _healthAt = DateTime.MinValue;
    static readonly SemaphoreSlim _healthGate = new SemaphoreSlim(1, 1);

    [HttpGet, AllowAnonymous]
    [Route("qdl/health")]
    async public Task<ActionResult> Health()
    {
        int ttl = Math.Max(5, ModInit.conf?.healthCacheSeconds ?? 30);
        if (!HealthFresh(ttl))
        {
            await _healthGate.WaitAsync();
            try
            {
                if (!HealthFresh(ttl))   // двойная проверка: пока ждали гейт, сосед мог всё собрать
                {
                    _healthCache = await BuildHealth();
                    _healthAt = DateTime.UtcNow;
                }
            }
            finally { _healthGate.Release(); }
        }

        var body = new JObject { ["at"] = _healthAt, ["services"] = _healthCache ?? new JArray() };
        SetHeadersNoCache();
        return ContentTo(body.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    static bool HealthFresh(int ttl) => _healthCache != null && (DateTime.UtcNow - _healthAt).TotalSeconds <= ttl;

    #region сборка отчёта
    async static Task<JArray> BuildHealth()
    {
        var conf = ModInit.conf;
        string self = $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}";
        string cubMirror = CoreInit.conf.cub?.mirror ?? "cub.rip";
        string tmdbKey = CoreInit.conf.cub?.api_key ?? "";

        var tasks = new List<Task<JObject>>
        {
            // ── Инфраструктура ──
            ProbeQbit(),
            // через СВОЙ прокси /ts: одной пробой проверяем и модуль TorrServer, и контейнер
            ProbeHttp("torrserver", "TorrServer", GrpInfra, self + "/ts/echo", strict200: true),
            Task.Run(ProbeFfWorker),
            ProbeOptionalHttp("flaresolverr", "FlareSolverr", GrpInfra, conf?.healthFlaresolverrUrl),
            ProbePg("pg-bitmagnet", "Postgres bitmagnet (DHT-индекс)", conf?.bitmagnetConnection),
            ProbePg("pg-index", "Postgres свой индекс", conf?.localIndexConnection),
            ProbeOptionalHttp("ipcam", "IPCamLive (регистратор)", GrpInfra, conf?.liveUrl),

            // ── Метаданные ──
            ProbeHttp("tmdb-api", "TMDB API", GrpMeta, "https://api.themoviedb.org/3/configuration?api_key=" + tmdbKey, strict200: true),
            ProbeHttp("tmdb-img", "TMDB картинки", GrpMeta, "https://image.tmdb.org/t/p/w92/"),
            ProbeHttp("cub", "CUB каталог (" + cubMirror + ")", GrpMeta, "https://tmdb." + cubMirror + "/3/movie/550?api_key=" + tmdbKey, strict200: true),

            // ── Поиск раздач ──
            ProbeOptionalHttp("jacred-webapi", "JacRed webapi (чужой индекс)", GrpSearch, conf?.healthJacredWebApi),

            // ── jut.su ──
            ProbeJutHost(),
            ProbeOptionalHttp("shikimori", "Shikimori", GrpJut,
                JutOn && !string.IsNullOrWhiteSpace(conf?.jutShikimoriHost) ? NoSlash(conf.jutShikimoriHost) + "/api/animes?limit=1" : null),
            ProbeOptionalHttp("anilist", "AniList GraphQL", GrpJut, JutOn ? conf?.jutAniListUrl : null),
        };

        var arr = new JArray((await Task.WhenAll(tasks)).Where(x => x != null));
        AddSearchChecks(arr);   // бесплатно: состояние канареек SearchMonitor
        arr.Add(ProbeJutAuth());
        return arr;
    }

    const string GrpInfra = "Инфраструктура";
    const string GrpMeta = "Метаданные";
    const string GrpSearch = "Поиск раздач";
    const string GrpJut = "jut.su";

    // JutOn (вкладка jut.su включена) объявлен в JutSu.cs — тот же partial-класс
    static string NoSlash(string s) => (s ?? "").TrimEnd('/');

    internal static JObject Svc(string id, string name, string group, string status, long ms, string detail)
        => new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["group"] = group,
            ["status"] = status,
            ["ms"] = ms,
            ["detail"] = detail
        };

    // Только имя типа: message исключений содержит хосты/порты, а у Npgsql — куски строки подключения
    static string ShortErr(Exception ex) => (ex is OperationCanceledException or TaskCanceledException)
        ? "таймаут" : ex.GetType().Name;
    #endregion

    #region живые пробы
    /// <summary>
    /// Любой ответ &lt;500 = хост жив (пробы без ключей штатно ловят 401/403/404).
    /// strict200 — там, где 200 достижим и означает «действительно работает».
    /// </summary>
    async static Task<JObject> ProbeHttp(string id, string name, string group, string url, int timeoutMs = 2500, bool strict200 = false)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var resp = await _healthHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            int code = (int)resp.StatusCode;
            bool ok = strict200 ? resp.IsSuccessStatusCode : code < 500;
            return Svc(id, name, group, ok ? "ok" : "fail", sw.ElapsedMilliseconds, "http " + code);
        }
        catch (Exception ex) { return Svc(id, name, group, "fail", sw.ElapsedMilliseconds, ShortErr(ex)); }
    }

    static Task<JObject> ProbeOptionalHttp(string id, string name, string group, string url)
        => string.IsNullOrWhiteSpace(url)
            ? Task.FromResult(Svc(id, name, group, "off", 0, "не настроено"))
            : ProbeHttp(id, name, group, url);

    /// <summary>
    /// qBittorrent — настоящая проба через общий SID-стек (Qbit()). Кап 3 с поверх timeoutSeconds
    /// конфига: экран не должен ждать 20 с, если демон лёг. Отставший таск дотлевает в фоне.
    /// </summary>
    async static Task<JObject> ProbeQbit()
    {
        var sw = Stopwatch.StartNew();
        var work = Task.Run(async () =>
        {
            using var c = await Qbit();
            return await c.GetStringAsync("/api/v2/app/version");
        });

        if (await Task.WhenAny(work, Task.Delay(3000)) != work)
            return Svc("qbit", "qBittorrent", GrpInfra, "fail", 3000, "таймаут");

        try { return Svc("qbit", "qBittorrent", GrpInfra, "ok", sw.ElapsedMilliseconds, (await work)?.Trim()); }
        catch (Exception ex) { return Svc("qbit", "qBittorrent", GrpInfra, "fail", sw.ElapsedMilliseconds, ShortErr(ex)); }
    }

    async static Task<JObject> ProbePg(string id, string name, string conn)
    {
        if (string.IsNullOrWhiteSpace(conn)) return Svc(id, name, GrpInfra, "off", 0, "не настроено");

        var sw = Stopwatch.StartNew();
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(conn) { Timeout = 2, CommandTimeout = 3 };
            using var cts = new CancellationTokenSource(3000);
            await using var db = new NpgsqlConnection(csb.ConnectionString);
            await db.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("select 1", db);
            await cmd.ExecuteScalarAsync(cts.Token);
            return Svc(id, name, GrpInfra, "ok", sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) { return Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, ShortErr(ex)); }
    }

    /// <summary>ffmpeg-воркер: берём готовый вердикт IsAlive() (кеш alive 30с/dead 15с) — сеть только на промахе.</summary>
    static JObject ProbeFfWorker()
    {
        if (!FfWorker.Enabled) return Svc("ffworker", "ffmpeg-worker (NVENC)", GrpInfra, "off", 0, "не настроено, транскод на CPU");

        var sw = Stopwatch.StartNew();
        bool alive = FfWorker.IsAlive();
        return Svc("ffworker", "ffmpeg-worker (NVENC)", GrpInfra, alive ? "ok" : "fail", sw.ElapsedMilliseconds,
            alive ? "NVENC доступен" : "недоступен → CPU-фолбэк");
    }

    async static Task<JObject> ProbeJutHost()
    {
        if (!JutOn) return Svc("jut-host", "jut.su", GrpJut, "off", 0, "вкладка выключена");

        var r = await ProbeHttp("jut-host", "jut.su", GrpJut, JutNet.Host);
        try
        {
            var (mode, _) = JutProxyFallback.State("jutsu");
            if (!string.IsNullOrEmpty(mode) && mode != "none")
                r["detail"] = (r.Value<string>("detail") ?? "") + ", прокси-фолбэк: " + mode;
        }
        catch { }
        return r;
    }

    /// <summary>Авторизация на jut.su — бесплатно, из канарейки транспорта (куки протухли → сайт молча режет ссылки).</summary>
    static JObject ProbeJutAuth()
    {
        if (!JutOn) return Svc("jut-auth", "jut.su: авторизация", GrpJut, "off", 0, "вкладка выключена");
        if (string.IsNullOrWhiteSpace(ModInit.conf?.jutUserId))
            return Svc("jut-auth", "jut.su: авторизация", GrpJut, "off", 0, "куки не заданы");

        bool? ok = JutNet.AuthOk;
        return ok == null
            ? Svc("jut-auth", "jut.su: авторизация", GrpJut, "off", 0, "ещё не проверялась")
            : Svc("jut-auth", "jut.su: авторизация", GrpJut, ok == true ? "ok" : "fail", 0,
                  ok == true ? "куки приняты" : "куки протухли — сайт подменяет ссылки");
    }
    #endregion

    #region поиск раздач (ноль сети — состояние канареек SearchMonitor)
    // Живой прогон индексатора здесь НЕ делаем: это 5-40 с и удары по трекерам на каждое
    // открытие экрана. Мониторинг и так гоняет канарейки по расписанию — берём его вердикты.
    internal static void AddSearchChecks(JArray arr) => AddSearchChecks(arr, LoadDiagState(), DateTime.UtcNow);

    internal static void AddSearchChecks(JArray arr, JObject st, DateTime now)
    {
        if ((ModInit.conf?.searchMonitorIntervalMinutes ?? 0) <= 0)
        {
            arr.Add(Svc("indexer", "Индексатор (канарейки)", GrpSearch, "off", 0, "мониторинг поиска выключен"));
            return;
        }

        var checks = st?["checks"] as JObject;
        var last = (st?["runs"] as JArray)?.OfType<JObject>().LastOrDefault();
        var at = last?.Value<DateTime?>("at");
        string when = at.HasValue
            ? $"прогон {(int)Math.Max(0, (now - at.Value.ToUniversalTime()).TotalMinutes)} мин назад"
            : "прогонов ещё не было";

        if (checks == null || checks["indexer"] == null)
        {
            arr.Add(Svc("indexer", "Индексатор (канарейки)", GrpSearch, "off", 0, when));
            return;
        }

        arr.Add(Svc("indexer", "Индексатор (канарейки)", GrpSearch, StateOf(checks["indexer"]), 0, when));

        foreach (var p in checks.Properties().Where(p => p.Name.StartsWith("tracker:")))
            arr.Add(Svc(p.Name, "Трекер " + p.Name.Substring("tracker:".Length), GrpSearch,
                StateOf(p.Value), 0, "по выдаче канареек"));

        if (checks["stars"] != null)
            arr.Add(Svc("stars", "Умная выдача (⭐)", GrpSearch, StateOf(checks["stars"]), 0, "по выдаче канареек"));
    }

    static string StateOf(JToken c) => (c?.Value<string>("state") ?? "ok") == "ok" ? "ok" : "fail";
    #endregion
}
