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
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Хелс-чеки: GET /qdl/health (qdl 2.44) ───────────────────────────────────────
// Питает экран «Хелс-чеки» в настройках Lampa (qdl.js, виден только по праву «действия»).
//
// 🔥 Модель сменилась в 2.44. Было: на каждое открытие экрана дёргали корень каждого хоста и
// считали успехом любой ответ <500 — 400/403/404 рисовались зелёным, и экран показывал ✅
// у сервисов, которые не работают (владелец поймал это на «TMDB картинки: ✅ http 400»).
// Проба «хост ответил» в принципе не может доказать, что сервис РАБОТАЕТ: у AniList боевой
// путь POST, Shikimori требует свой User-Agent, картинка TMDB существует только по конкретному
// пути. Стало:
//
//   • ВНЕШНЕЕ — пассивно. Никаких запросов ради экрана. Исход каждого реального обращения
//     пишут боевые чокпоинты в HealthState (JutNet.Run, AuthAlarm, JutShikiSearch,
//     JutAniListCover, CatalogWarmup.Fetch, FetchTmdbPoster, FetchIndexer, FfWorker.IsAlive).
//     Отвалилось — красное; заработало — само зеленеет.
//   • СВОЁ — живьём. Контейнеры в своей сети стоят единицы миллисекунд, и их проверка не
//     является «походом наружу»: qBittorrent, TorrServer, Postgres, FlareSolverr, IPCamLive.
//   • Четыре состояния: ok / warn / fail / off. warn — «работает, но не своим путём или с
//     ошибками»; off — «не настроено» или «нет данных». Красное значит ровно «сломано».
//
// Принципы, которые остались прежними:
//   1. «Выключено» ≠ «отвалилось»: пустая строка конфига — киллсвитч, такой сервис off (⏸).
//   2. Секреты не светим: detail — HTTP-код, версия или ИМЯ типа исключения, никогда message
//      (там всплывают хосты, порты и куски строк подключения).
//
// Дорогая полная диагностика живёт отдельно: /qdl/jut/diag и /qdl/diag/search?dry=1.
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
    async public Task<ActionResult> Health(int fresh = 0)
    {
        int ttl = Math.Max(5, ModInit.conf?.healthCacheSeconds ?? 30);

        // «↻ Обновить» обязана обходить кеш, иначе кнопка обманывает. Кламп 5 с обязателен:
        // без него зажатая кнопка пульта = поток проб по локальной сети и POST во FlareSolverr.
        if (fresh == 1 && (DateTime.UtcNow - _healthAt).TotalSeconds >= 5)
            ttl = 0;

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
        var arr = new JArray();
        var now = DateTime.UtcNow;
        int flap = Math.Max(5, ModInit.conf?.healthFlapWindowMinutes ?? 60);

        try
        {
            string self = $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}";

            // ── Своё хозяйство: живые пробы (локальная сеть, единицы миллисекунд) ──
            var tasks = new List<Task<JObject>>
            {
                Guard("qbit", "qBittorrent", GrpInfra, ProbeQbit),
                // через СВОЙ прокси /ts: одной пробой проверяем и модуль TorrServer, и контейнер
                Guard("torrserver", "TorrServer", GrpInfra,
                      () => ProbeHttp("torrserver", "TorrServer", GrpInfra, self + "/ts/echo")),
                Guard("ffworker", "ffmpeg-worker (NVENC)", GrpInfra, () => Task.Run(() => ProbeFfWorker(now, flap))),
                Guard("flaresolverr", "FlareSolverr", GrpInfra, ProbeFlaresolverr),
                Guard("pg-bitmagnet", "Postgres bitmagnet (DHT-индекс)", GrpInfra,
                      () => ProbePg("pg-bitmagnet", "Postgres bitmagnet (DHT-индекс)", ModInit.conf?.bitmagnetConnection)),
                Guard("pg-index", "Postgres свой индекс", GrpInfra,
                      () => ProbePg("pg-index", "Postgres свой индекс", ModInit.conf?.localIndexConnection)),
                Guard("ipcam", "IPCamLive (регистратор)", GrpInfra, ProbeIpcam),
                // xsmart-proxy — такой же свой контейнер в сети media. Спрашиваем его ручку здоровья,
                // а не портал: сессию она не трогает (логин из фона роняет подписку — см. ProbeXsmart).
                Guard("xsmart", "XSMART (портал)", GrpInfra, ProbeXsmart)
            };

            foreach (var o in await Task.WhenAll(tasks))
                if (o != null) arr.Add(o);

            // ── Внешнее: только наблюдения, ноль сети ──
            AddPassiveChecks(arr, now, flap);
        }
        catch (Exception ex)
        {
            // Отчёт обязан доехать хотя бы частично: раньше исключение здесь роняло весь
            // /qdl/health в 500, и экран показывал «недоступен» вместо списка со сбоем.
            arr.Add(Svc("health-self", "Сборка отчёта", GrpInfra, "fail", 0, ShortErr(ex)));
        }

        try { AddSearchChecks(arr); }   // бесплатно: состояние канареек SearchMonitor
        catch (Exception ex) { arr.Add(Svc("searchmon", "Мониторинг поиска", GrpSearch, "fail", 0, ShortErr(ex))); }

        return arr;
    }

    const string GrpInfra = "Инфраструктура";
    const string GrpMeta = "Метаданные";
    const string GrpSearch = "Поиск раздач";
    const string GrpJut = "jut.su";
    const string GrpMusic = "Музыка";

    // JutOn (вкладка jut.su включена) объявлен в JutSu.cs — тот же partial-класс
    static string NoSlash(string s) => (s ?? "").TrimEnd('/');

    internal static JObject Svc(string id, string name, string group, string status, long ms, string detail, bool quiet = false)
    {
        var o = new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["group"] = group,
            ["status"] = status,
            ["ms"] = ms,
            ["detail"] = detail
        };
        // quiet — «проблема производная»: строка красится, но в сводку «Проблемы» не тащится.
        // Один протухший планировщик иначе даёт десяток одинаковых ⚠️ и топит настоящую причину.
        if (quiet) o["quiet"] = true;
        return o;
    }

    static string ShortErr(Exception ex) => HealthState.ShortErr(ex);

    /// <summary>Одна упавшая проба не должна ронять весь отчёт.</summary>
    async static Task<JObject> Guard(string id, string name, string group, Func<Task<JObject>> probe)
    {
        try { return await probe(); }
        catch (Exception ex) { return Svc(id, name, group, "fail", 0, ShortErr(ex)); }
    }
    #endregion

    #region внешние сервисы — пассивные наблюдения (ноль сети)
    internal static void AddPassiveChecks(JArray arr, DateTime now, int flap)
    {
        // 🔥 Подписываем строку ТЕМ ЖЕ хостом, куда реально ходим (qdl 2.65). Показывали
        // cub.mirror — и это врало: mirror (cub.best) живёт в подстановке cub_domain в бандл
        // (LampaWeb/ApiController.cs) и в imagetmdb./apitmdb. у DLNA/BaseENG, а ряды каталога
        // идут через CubProxy на tmdb.<cub.domain> (= tmdb.cub.red), потому что GetDomain режет
        // /cub/tmdb.<что угодно>/… по ПЕРВОЙ точке. Владелец открыл в браузере живой cub.best,
        // увидел 200 и не понял претензии — строку надо уметь проверить копипастой.
        string cubHost = "tmdb.cub.red";
        try { cubHost = "tmdb." + (CoreInit.conf.cub?.domain ?? "cub.red"); } catch { }

        // ── Репликация ── только на реплике: возраст последнего успешного манифеста, блокировки
        // удаления, недоступность дома или своего qBit.
        // 🔴 HealthState.Ids.Replica писался из ReplicaSync с самого начала, но строки для него
        // здесь не было — вердикты копились в реестре и не показывались никому. Дом получает тот
        // же образ, и вечно-серая строка на его экране была бы шумом, поэтому гейт по роли.
        if (QbitController.ReplicaMode)
            AddPassiveRow(arr, HealthState.Ids.Replica, "Репликация", GrpInfra, now, flap);

        // ── Метаданные ── наблюдаются прогревом каталога (CatalogWarmup.Fetch) и качалкой постеров
        AddPassiveRow(arr, HealthState.Ids.TmdbApi, "TMDB API", GrpMeta, now, flap);
        AddPassiveRow(arr, HealthState.Ids.TmdbImg, "TMDB картинки", GrpMeta, now, flap);
        AddPassiveRow(arr, HealthState.Ids.Cub, "CUB каталог (" + cubHost + ")", GrpMeta, now, flap);

        // ── Номер страницы в рядах каталога (qdl 2.112, §DI/§DO) ─────────────────────────────
        // Строка отдельная от «CUB каталог» намеренно: та отвечает «апстрим жив», а при
        // отравленной записи апстрим как раз здоров — врёт ЗАПИСЬ КЕША, и её видно только по
        // тому, что в теле номер страницы не тот, который просили. Данные собирает прогрев
        // каталога: он ходит по реальным клиентским ключам и потому видит и HIT-ы, которых
        // сторож в контроллере CubProxy не видит принципиально.
        var cp = CatalogWarmup.PageHealthSnapshot();
        var (cpStatus, cpDetail) = CubPageVerdict(cp, now);
        arr.Add(Svc(HealthState.Ids.CubPage, "CUB: номер страницы", GrpMeta, cpStatus, 0, cpDetail));

        // ── Поиск раздач ── живые поиски пользователя + канарейки идут через FetchIndexer
        AddPassiveRow(arr, HealthState.Ids.Indexer, "Индексатор (живые поиски)", GrpSearch, now, flap);

        // ── jut.su ── вкладка выключена = киллсвитч, а не сбой
        string jutOff = JutOn ? null : "вкладка выключена";
        AddPassiveRow(arr, HealthState.Ids.JutHost, "jut.su", GrpJut, now, flap, jutOff);
        AddPassiveRow(arr, HealthState.Ids.JutAuth, "jut.su: авторизация", GrpJut, now, flap,
            jutOff ?? (string.IsNullOrWhiteSpace(ModInit.conf?.jutUserId) ? "куки не заданы" : null));
        AddPassiveRow(arr, HealthState.Ids.Shikimori, "Shikimori", GrpJut, now, flap, jutOff);
        AddPassiveRow(arr, HealthState.Ids.AniList, "AniList", GrpJut, now, flap, jutOff);

        // ── Музыка ── вердикт целиком из состояния прогрева (music-warm.json), ноль сети.
        // Строка отвечает на вопрос «полки живые и тёплые?», а не «жив ли внешний провайдер»:
        // до провайдеров мы не ходим, до них ходит модуль Music.
        var mw = MusicWarm.HealthSnapshot();
        var (mwStatus, mwDetail) = MusicWarmVerdict(mw, now);
        arr.Add(Svc(HealthState.Ids.MusicWarm, "Музыка (полки)", GrpMusic, mwStatus, mw.Value<int?>("ms") ?? 0, mwDetail));

        arr.Add(CubExtRow());
    }

    /// <summary>
    /// Полнота вендора витрины CUB (qdl 2.88). До 2.88 неполный вендор был НЕВИДИМ: промах молча
    /// редиректил клиента на cub.best, и понять это можно было только замером трафика. Теперь
    /// промах дотягивается сервером — а строка показывает, сколько раз пришлось, чтобы «подстраховка
    /// работает» не превратилось в «вендор не прогоняли полгода».
    /// ⚠️ MP4 скринсейверов гитигнорятся (109 МБ) — на свежей машине тут будет полный некомплект,
    /// пока не прогонят scripts/vendor-cub-extensions.ps1.
    /// </summary>
    static JObject CubExtRow()
    {
        const string id = "cub-ext", name = "Витрина CUB (вендор)";

        try
        {
            // 🔴 Полные имена System.IO.*: модуль компилируется в РАНТАЙМЕ отдельным компилятором,
            // без implicit usings из .csproj — сборка решения проходит, а контейнер падает
            // на «Имя Directory не существует». Проверено этой же правкой.
            string dir = ModInit.conf?.cubExtPath ?? "/lampac/wwwroot/cubext";
            string list = System.IO.Path.Combine(dir, "list.json");

            if (!System.IO.File.Exists(list))
                return Svc(id, name, GrpInfra, "fail", 0, "нет list.json — витрина пустая, прогнать vendor-cub-extensions.ps1");

            int themes = CountFiles(System.IO.Path.Combine(dir, "theme"), "*.css");
            int savers = CountFiles(System.IO.Path.Combine(dir, "screensaver"), "*.mp4");
            int plugins = CountFiles(System.IO.Path.Combine(dir, "plugin"), "*.js");
            int fetched = CubExtFetched;

            string detail = $"тем {themes}, заставок {savers}, плагинов {plugins}" +
                            (fetched > 0 ? $"; дотянуто с upstream: {fetched}" : "");

            // Ноль MP4 — это ровно тот случай «свежий клон, вендор не прогоняли».
            string status = savers == 0 || themes == 0 ? "warn" : (fetched > 0 ? "warn" : "ok");

            return Svc(id, name, GrpInfra, status, 0, detail);
        }
        catch (Exception ex) { return Svc(id, name, GrpInfra, "fail", 0, ShortErr(ex)); }
    }

    static int CountFiles(string dir, string mask)
    {
        try { return System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, mask).Length : 0; }
        catch { return 0; }
    }

    static void AddPassiveRow(JArray arr, string id, string name, string group, DateTime now, int flap, string offReason = null)
    {
        if (offReason != null) { arr.Add(Svc(id, name, group, "off", 0, offReason)); return; }

        var (status, detail) = HealthState.Verdict(HealthState.Get(id, now, flap), now, flap);
        arr.Add(Svc(id, name, group, status, 0, detail));
    }
    #endregion

    #region своё хозяйство — живые пробы
    /// <summary>Строгая проба: успех — только 2xx. Мягкой ветки «любой код &lt;500» больше нет.</summary>
    async static Task<JObject> ProbeHttp(string id, string name, string group, string url, int timeoutMs = 2500)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var resp = await _healthHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            int code = (int)resp.StatusCode;
            return Svc(id, name, group, resp.IsSuccessStatusCode ? "ok" : "fail", sw.ElapsedMilliseconds, "http " + code);
        }
        catch (Exception ex) { return Svc(id, name, group, "fail", sw.ElapsedMilliseconds, ShortErr(ex)); }
    }

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

    /// <summary>
    /// ffmpeg-воркер. IsAlive() сам пишет наблюдение в реестр (кеш alive 30 с / dead 15 с), поэтому
    /// вердикт берём оттуда: строка учитывает и пробу с экрана, и реальные транскоды горячего пути.
    /// </summary>
    static JObject ProbeFfWorker(DateTime now, int flap)
    {
        if (!FfWorker.Enabled) return Svc("ffworker", "ffmpeg-worker (NVENC)", GrpInfra, "off", 0, "не настроено, транскод на CPU");

        var sw = Stopwatch.StartNew();
        FfWorker.IsAlive();
        sw.Stop();

        var (status, detail) = HealthState.Verdict(HealthState.Get(HealthState.Ids.FfWorker, now, flap), now, flap);
        return Svc("ffworker", "ffmpeg-worker (NVENC)", GrpInfra, status, sw.ElapsedMilliseconds,
                   status == "ok" ? "NVENC доступен" : detail);   // при ok возраст не нужен: пробу только что сделали
    }

    /// <summary>
    /// FlareSolverr. Боевой интерфейс — POST /v1, поэтому и проверяем его: GET на корень отдаёт
    /// «FlareSolverr is ready» даже когда внутри мёртв Chrome — ровно тот ложный зелёный, от
    /// которого уходим. sessions.list браузер не поднимает. Старые сборки команды не знают —
    /// для них честный фолбэк с пометкой, что проверка неполная.
    /// </summary>
    async static Task<JObject> ProbeFlaresolverr()
    {
        const string id = "flaresolverr", name = "FlareSolverr";
        string url = ModInit.conf?.healthFlaresolverrUrl;
        if (string.IsNullOrWhiteSpace(url)) return Svc(id, name, GrpInfra, "off", 0, "не настроено");
        url = NoSlash(url);

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(2500);
            using var content = new StringContent("{\"cmd\":\"sessions.list\"}", Encoding.UTF8, "application/json");
            using var resp = await _healthHttp.PostAsync(url + "/v1", content, cts.Token);
            string txt = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    var j = JObject.Parse(txt);
                    if (j.Value<string>("status") == "ok")
                    {
                        // Число сессий — это и есть датчик утечки, и раньше он был слепым: вердикт
                        // был зелёным при любом N. Боевой случай §BW: 37 живых Chrome'ов, память в
                        // потолок mem_limit, cgroup убивает дочерние процессы — а /health отвечает,
                        // потому что жив питоновский вебсервер, и autoheal не срабатывает никогда.
                        // Норма — ОДНА сессия: имя стабильное, больше одной быть не должно.
                        int sessions = (j["sessions"] as JArray)?.Count ?? 0;
                        string state = sessions > 10 ? "fail" : sessions > 3 ? "warn" : "ok";
                        string note = "сессий: " + sessions + (state == "ok" ? "" : " (норма 1 — копятся, см. §BW)");
                        return Svc(id, name, GrpInfra, state, sw.ElapsedMilliseconds, note);
                    }
                }
                catch { }
                return Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, "ответ без status=ok");
            }

            // 404/405 — сборка без sessions.list. Тогда хотя бы «процесс жив», но честно как warn.
            using var cts2 = new CancellationTokenSource(2000);
            using var root = await _healthHttp.GetAsync(url + "/", cts2.Token);
            string body = await root.Content.ReadAsStringAsync(cts2.Token);
            sw.Stop();
            return root.IsSuccessStatusCode && body.Contains("FlareSolverr", StringComparison.OrdinalIgnoreCase)
                ? Svc(id, name, GrpInfra, "warn", sw.ElapsedMilliseconds, "процесс жив, но sessions.list не поддержан — проверка неполная")
                : Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, "http " + (int)resp.StatusCode);
        }
        catch (Exception ex) { return Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, ShortErr(ex)); }
    }

    /// <summary>
    /// IPCamLive: список камер — самая дешёвая ручка регистратора (SQLite, ничего не запускает).
    /// ⚠️ Ни start-стрима, ни превью не трогаем — оба поднимают ffmpeg. И не зовём LiveApiJson:
    /// он инстансный, ест слот _liveGate, а Live.cs вообще не залинкован в проект тестов.
    /// </summary>
    async static Task<JObject> ProbeIpcam()
    {
        const string id = "ipcam", name = "IPCamLive (регистратор)";
        string url = ModInit.conf?.liveUrl;
        if (string.IsNullOrWhiteSpace(url)) return Svc(id, name, GrpInfra, "off", 0, "не настроено");

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(2500);
            using var resp = await _healthHttp.GetAsync(NoSlash(url) + "/api/cameras/", cts.Token);
            string txt = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
                return Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, "http " + (int)resp.StatusCode);

            int n = JArray.Parse(txt).OfType<JObject>().Count(o => (o.Value<int?>("id") ?? 0) > 0);
            return n > 0
                ? Svc(id, name, GrpInfra, "ok", sw.ElapsedMilliseconds, "камер: " + n)
                : Svc(id, name, GrpInfra, "warn", sw.ElapsedMilliseconds, "регистратор отвечает, но камер нет");
        }
        catch (Exception ex) { return Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, ShortErr(ex)); }
    }

    /// <summary>
    /// XSMART — свой контейнер xsmart-proxy в сети media. Спрашиваем ЕГО ручку здоровья: она
    /// отвечает из памяти процесса и наружу не ходит вообще.
    ///
    /// 🔴 Логин отсюда невозможен по построению, и это принципиально: каждый логин в портал
    /// РОТИРУЕТ key_check, а XSMART считает такое «устройством пользуются вдвоём» и роняет
    /// подписку до Free. Поэтому фоновый путь сервиса не логинится никогда (xsmart/service/
    /// CONTRACT.md §3.3), а /xsmart/health сессию не трогает — только показывает.
    ///
    /// 🔴 Судить по HTTP-коду здесь НЕЛЬЗЯ: ручка намеренно всегда 200 (иначе autoheal крутил бы
    /// полностью исправный контейнер каждый раз, когда лежит сам портал). Вердикт — по полям.
    /// </summary>
    internal async static Task<JObject> ProbeXsmart()
    {
        const string id = "xsmart", name = "XSMART (портал)";

        // ⚠️ Сырой конфиг, а не XsmartNet.Api: тот подставляет дефолтный адрес вместо пустого, и
        // явный киллсвитч (пустая строка на реплике, где раздел выключен профилем compose)
        // превратился бы в пробу в никуда — вечное красное вместо честного ⏸.
        // ⚠️ xsmartEnable здесь не смотрим сознательно: он гасит только скачивание и слежение,
        // а онлайн-раздел живёт целиком в контейнере и работает независимо от него.
        string url = ModInit.conf?.xsmartApi;
        if (string.IsNullOrWhiteSpace(url)) return Svc(id, name, GrpInfra, "off", 0, "не настроено");

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(2500);
            using var resp = await _healthHttp.GetAsync(NoSlash(url) + "/xsmart/health", cts.Token);
            string txt = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
                return Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, "http " + (int)resp.StatusCode);

            var (status, detail) = XsmartVerdict(JObject.Parse(txt));
            return Svc(id, name, GrpInfra, status, sw.ElapsedMilliseconds, detail);
        }
        catch (Exception ex) { return Svc(id, name, GrpInfra, "fail", sw.ElapsedMilliseconds, ShortErr(ex)); }
    }

    /// <summary>
    /// Вердикт по телу /xsmart/health. Чистая функция: живая проба только приносит JSON.
    /// Порядок проверок = порядок важности, первое совпадение и есть вердикт.
    /// </summary>
    /// <summary>
    /// Вердикт по состоянию прогрева полок «Музыки» (MusicWarm.HealthSnapshot).
    /// Чистая функция: ни одного запроса, всё уже посчитал тик прогрева.
    /// Порядок проверок = порядок важности, первое совпадение и есть вердикт.
    /// 🔴 «Выключено» и «греть некуда» — это off, а не fail: отсутствие работы не авария.
    /// </summary>
    /// <summary>
    /// Вердикт по аудиту номера страницы (qdl 2.112). Порядок правил = важность, поля читаются
    /// защищённо теми же MwInt/MwDate: снимок собирается прогревом и не обязан быть полным.
    ///
    /// Пороги: 1–2 расхождения — это одна-две залежавшиеся записи, они дохнут сами за TTL;
    /// 3 и больше за один обход — уже не случайность, апстрим сыплет и стоит вмешаться.
    /// Специально не делаем fail на единичном случае: привычка игнорировать красное дороже.
    /// </summary>
    internal static (string status, string detail) CubPageVerdict(JObject b, DateTime now)
    {
        if (b == null) return ("off", "состояния нет");

        var at = MwDate(b, "at");
        if (at == null) return ("off", "обхода каталога ещё не было");

        int periodMin = Math.Max(5, MwInt(b, "periodMin", 15));
        if ((now - at.Value).TotalMinutes > periodMin * 3)
            return ("off", "прогрев каталога не ходил " + HealthState.Ago(now - at.Value));

        int checkedRows = MwInt(b, "checked", 0);
        if (checkedRows == 0) return ("off", "рядов в обходе не было");

        int bad = MwInt(b, "bad", 0);
        string tail = " · рядов " + checkedRows + " · обход " + HealthState.Ago(now - at.Value);

        if (bad == 0)
            return ("ok", "страница совпала на всех" + tail);

        // Светим сами адреса: §DI показал, что запись кеша называет свой файл заголовками
        // X-StatiCache-Bucket/Id, и тогда снос точечный, а не покос всех рядов.
        var samples = (b["samples"] as JArray)?.Select(x => (string)x).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        string list = samples != null && samples.Length > 0 ? " · " + string.Join(" · ", samples) : "";

        return (bad >= 3 ? "fail" : "warn",
            "чужая страница в " + bad + " " + HealthState.Plural(bad, "записи", "записях", "записях") + tail + list);
    }

    internal static (string status, string detail) MusicWarmVerdict(JObject b, DateTime now)
    {
        if (b == null) return ("off", "состояния нет");
        if (!MwBool(b, "enabled")) return ("off", "прогрев музыки выключен");

        // ⚠️ Поля читаются ЗАЩИЩЁННО (MwInt/MwBool/MwDate): снимок приходит из файла на диске,
        // а он переживает падения по питанию и откаты образа. Битое поле не имеет права уронить
        // весь /qdl/health — экран настроек погас бы целиком из-за одной строки.
        var lastRun = MwDate(b, "lastRun");
        if (lastRun == null) return ("off", "прогонов ещё не было");

        // Хост берётся только с живого клиента: обложки в ответе подписываются хостом запроса,
        // и выдуманный 127.0.0.1 запёкся бы в общий кеш полок (см. шапку MusicWarm.cs).
        if (MwInt(b, "hosts", 0) == 0) return ("off", "«Музыку» ещё никто не открывал — греть некуда");

        int periodMin = Math.Max(5, MwInt(b, "periodMin", 20));
        var lastOk = MwDate(b, "lastOkAt");
        int fails = MwInt(b, "fails", 0);

        // Три пропущенных прогона подряд — это уже не «моргнуло»
        if (lastOk == null || (now - lastOk.Value).TotalMinutes > periodMin * 3)
            return ("fail", lastOk == null
                ? "удачных прогревов ещё не было" + (fails > 0 ? " · сбоев подряд " + fails : "")
                : "последний удачный прогрев " + HealthState.Ago(now - lastOk.Value));

        int shelves = MwInt(b, "shelves", 0);
        if (shelves == 0) return ("fail", "все полки пусты — провайдеры не отвечают");

        string tail = " · полок " + shelves + " · home " + MwInt(b, "ms", 0) + " мс · " + HealthState.Ago(now - lastOk.Value);

        // warming — провайдер не уложился в 2-секундный бюджет /music/home; клиент по этому флагу
        // делает до трёх дозапросов, то есть цена видна человеку, а не только нам.
        if (MwBool(b, "warming"))
            return ("warn", "провайдер не укладывается в бюджет 2 с" + tail);

        // Светим ИМЕНА полок, а не тексты ошибок: имя проверяется копипастой в /music/section?id=
        var empty = (b["empty"] as JArray)?.Select(x => (string)x).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        if (empty != null && empty.Length > 0)
            return ("warn", "пусто: " + string.Join(", ", empty) + tail);

        return ("ok", "полок " + shelves + " · home " + MwInt(b, "ms", 0) + " мс · прогрев " + HealthState.Ago(now - lastOk.Value));
    }

    static int MwInt(JObject b, string key, int def)
        => b?[key] is JValue v && v.Type is JTokenType.Integer or JTokenType.Float ? Convert.ToInt32(v.Value) : def;

    static bool MwBool(JObject b, string key)
        => b?[key] is JValue v && v.Type == JTokenType.Boolean && (bool)v.Value;

    static DateTime? MwDate(JObject b, string key)
    {
        if (b?[key] is not JValue v) return null;
        if (v.Type == JTokenType.Date) return Convert.ToDateTime(v.Value);
        if (v.Type == JTokenType.String && DateTime.TryParse((string)v.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
            return d;
        return null;
    }

    internal static (string status, string detail) XsmartVerdict(JObject b)
    {
        if (b == null) return ("fail", "пустой ответ");
        if (b.Value<bool?>("ok") == false) return ("fail", "сервис сообщает о сбое");

        string ver = b.Value<string>("version");
        string tail = string.IsNullOrEmpty(ver) ? "" : " · v" + ver;

        if (b.Value<bool?>("configured") == false)
        {
            // Это ИМЕНА переменных окружения (XSMART_UUID и соседи), а не их значения — светить можно.
            var miss = (b["missing"] as JArray)?.Select(x => (string)x).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            return ("fail", miss != null && miss.Length > 0 ? "не настроены креды: " + string.Join(", ", miss) : "не настроены креды");
        }

        if (b["session"] is not JObject s) return ("fail", "ответ без session");

        if (s.Value<bool?>("banned") == true) return ("fail", "устройство заблокировано XSMART");

        // Последний поход наверх. 🔴 Это ЕДИНСТВЕННЫЙ надёжный датчик живости портала в теле:
        // lastError сбрасывается в null на каждом успешном запросе, то есть непустой = «прямо
        // сейчас не отвечает». Соблазнительный на вид session.consecutiveFailures сюда не годится:
        // сервис инкрементит его и тут же обнуляет на втором сбое (переезд на другую реплику), а
        // упавший ЛОГИН до него не доходит вовсе — снаружи поле бывает только 0 или 1, причём
        // ровно наоборот: устойчивый сбой даёт 0, а один моргнувший запрос — 1.
        var up = b["upstream"] as JObject;
        string err = up?.Value<string>("lastError");
        bool broken = !string.IsNullOrEmpty(err);

        if (s.Value<bool?>("authorized") != true)
        {
            // 🔴 «Сессии нет» само по себе НЕ сбой: её поднимает только живой зритель, фон не
            // логинится никогда (логин ротирует key_check → портал роняет подписку до Free).
            // Но ровно так же выглядит и ЛЁГШИЙ портал: логин не проходит → keyHash пуст. Отличаем
            // по статистике: пока наверх не ходили ни разу, её просто нет.
            bool everWentUp = broken || up?["lastOkAt"]?.Type is not (null or JTokenType.Null)
                                     || up?["lastErrorAt"]?.Type is not (null or JTokenType.Null);
            if (!everWentUp) return ("ok", "сессии нет — поднимет первый зритель" + tail);

            if (!broken) return ("ok", "сессия сброшена — поднимет следующий запрос" + tail);

            // 404 от контентной ручки = «ключ больше не признают»; сервис сам перелогинится
            // следующим же вызовом, красить это в сбой было бы враньём.
            return err == "http 404"
                ? ("warn", "ключ протух — сессия встанет следующим запросом" + tail)
                : ("fail", "сессия не поднимается: портал не отвечает" + XsmartErrNote(up) + tail);
        }

        string tier = s.Value<string>("tier");
        if (!string.IsNullOrEmpty(tier) && !string.Equals(tier, "Premium", StringComparison.OrdinalIgnoreCase))
            return ("warn", "тариф " + tier + " — ожидается Premium (портал понизил подписку)" + tail);

        if (broken)
            return ("warn", "последний запрос наверх упал" + XsmartErrNote(up) + XsmartErrAge(up) + tail);

        int logins = up?.Value<int?>("logins") ?? 0;
        // Норма — один логин на весь срок жизни процесса. Порог с запасом: законный релогин бывает
        // (протух ключ, переезд на другую реплику портала), а вот их череда — уже симптом.
        if (logins > 3)
            return ("warn", "перелогинов с рестарта: " + logins + " — сессия хлопает, key_check ротируется" + tail);

        int age = s.Value<int?>("ageSec") ?? 0;
        return ("ok", (string.IsNullOrEmpty(tier) ? "авторизован" : tier)
                    + (age > 0 ? " · сессия " + HealthState.Ago(TimeSpan.FromSeconds(age)) : "")
                    + (logins > 1 ? " · логинов: " + logins : "") + tail);
    }

    /// <summary>
    /// Последняя ошибка апстрима — только если она из закрытого списка кодов. Принцип файла:
    /// наружу код или имя, никогда message (у сервиса туда попадает текст исключения fetch с хостами).
    /// </summary>
    static string XsmartErrNote(JObject up)
    {
        string e = up?.Value<string>("lastError");
        if (string.IsNullOrEmpty(e)) return "";
        bool safe = e == "timeout" || e == "не JSON"
                 || (e.Length == 8 && e.StartsWith("http ", StringComparison.Ordinal) && e.Skip(5).All(char.IsDigit));
        return safe ? " (" + e + ")" : "";
    }

    /// <summary>Возраст последнего сбоя: по нему видно «упало сейчас» против «висит со вчера».</summary>
    static string XsmartErrAge(JObject up)
    {
        long? ms = up?.Value<long?>("lastErrorAt");   // epoch мс, как отдаёт Date.now() сервиса
        if (ms == null || ms <= 0) return "";
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).UtcDateTime;
        return " · " + HealthState.Ago(DateTime.UtcNow - at);
    }
    #endregion

    #region поиск раздач (ноль сети — состояние канареек SearchMonitor)
    // Живой прогон индексатора здесь НЕ делаем: это 5-40 с и удары по трекерам на каждое
    // открытие экрана. Мониторинг и так гоняет канарейки по расписанию — берём его вердикты.
    internal static void AddSearchChecks(JArray arr) => AddSearchChecks(arr, LoadDiagState(), DateTime.UtcNow);

    internal static void AddSearchChecks(JArray arr, JObject st, DateTime now)
    {
        int interval = ModInit.conf?.searchMonitorIntervalMinutes ?? 0;
        if (interval <= 0)
        {
            arr.Add(Svc("searchmon", "Мониторинг поиска", GrpSearch, "off", 0, "мониторинг поиска выключен"));
            return;
        }

        var checks = st?["checks"] as JObject;
        var last = (st?["runs"] as JArray)?.OfType<JObject>().LastOrDefault();
        var at = last?.Value<DateTime?>("at")?.ToUniversalTime();

        if (at == null || checks == null || checks.Count == 0)
        {
            arr.Add(Svc("searchmon", "Мониторинг поиска", GrpSearch, "off", 0, "прогонов ещё не было"));
            return;
        }

        // Протухание. Фактор 250 %, а не 200 %: тик штатно пропускается на разогреве после старта
        // и когда занят общий _watchGate, и при интервале 3 ч один законный пропуск не должен
        // красить экран. Замороженный планировщик — это ⚠️ «данные неизвестны», а не ❌ сервиса.
        int stalePct = Math.Max(120, ModInit.conf?.healthMonitorStalePercent ?? 250);
        bool stale = (now - at.Value).TotalMinutes > interval * stalePct / 100.0;
        string when = "прогон " + HealthState.Ago(now - at.Value);

        arr.Add(Svc("searchmon", "Мониторинг поиска", GrpSearch, stale ? "warn" : "ok", 0,
            stale ? when + " при интервале " + interval + " мин — данные ниже устарели" : when));

        AddSearchRow(arr, checks["indexer"], "indexer", "Индексатор (канарейки)", stale);

        foreach (var p in checks.Properties().Where(p => p.Name.StartsWith("tracker:")))
            AddSearchRow(arr, p.Value, p.Name, "Трекер " + p.Name.Substring("tracker:".Length), stale);

        if (checks["stars"] != null)
            AddSearchRow(arr, checks["stars"], "stars", "Умная выдача (⭐)", stale);
    }

    static void AddSearchRow(JArray arr, JToken c, string id, string name, bool stale)
    {
        if (c == null) return;
        var (status, detail) = SearchRowStatus(c);

        // Мониторинг стоит → вердикт ниже относится к неизвестно какому прошлому. Не врём «ok»,
        // но и в «Проблемы» не тащим: причина одна, и она уже есть отдельной строкой searchmon.
        if (stale && status == "ok")
            arr.Add(Svc(id, name, GrpSearch, "warn", 0, "данные устарели — мониторинг не прогонялся", quiet: true));
        else
            arr.Add(Svc(id, name, GrpSearch, status, 0, detail, quiet: stale));
    }

    /// <summary>
    /// 🔥 Сырой вердикт последнего прогона, а НЕ поле state.
    /// state — это состояние машины УВЕДОМЛЕНИЙ: оно переключается только после needStreak
    /// провалов подряд и вне кулдауна (12 ч), поэтому реально сломанный поиск оставался зелёным
    /// до половины суток. streak же инкрементируется на КАЖДОМ провале и обнуляется на КАЖДОМ
    /// успехе (SearchMonitor.EvalCheck) — то есть streak>0 ⇔ последний прогон провалился.
    /// Прежний StateOf вдобавок был fail-open: отсутствующее поле трактовалось как «ok».
    /// </summary>
    internal static (string status, string detail) SearchRowStatus(JToken c)
    {
        if (c == null) return ("off", "нет данных");

        int? streak = c.Value<int?>("streak");
        if (streak == null) return ("off", "нет данных");
        if (streak > 0)
            return ("fail", "последний прогон канареек провалился · " + streak + " "
                          + HealthState.Plural(streak.Value, "прогон", "прогона", "прогонов") + " подряд");

        return ("ok", "по выдаче канареек");
    }
    #endregion
}
