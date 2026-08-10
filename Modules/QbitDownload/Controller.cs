using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

public partial class QbitController : BaseController
{
    static readonly Regex _hashRx = new Regex("^([0-9a-fA-F]{40}|[0-9A-Za-z]{32})$", RegexOptions.Compiled);
    static bool ValidHash(string h) => !string.IsNullOrEmpty(h) && _hashRx.IsMatch(h);

    #region qBittorrent client (cookie auth, разделяемая SID-сессия)
    // Раньше каждый вызов Qbit() создавал свой HttpClient и делал полный POST /auth/login — на КАЖДЫЙ
    // qdl-запрос, включая каждый Range-seek плеера. Теперь handler-стек (пул TCP-соединений + кука QBT_SID)
    // общий и живёт между запросами; Qbit() отдаёт лёгкую обёртку, которую вызывающие по-прежнему
    // диспозят через using (диспозится только обёртка: disposeHandler=false).
    static HttpClientHandler _qbitPool;                 // общий CookieContainer (QBT_SID) + пул соединений
    static QbitAuthHandler _qbitAuth;                   // 401/403 → re-login → повтор запроса (ровно 1 раз)
    static string _qbitGen;                             // host|user|pass — пересоздание стека при смене init.conf
    static DateTime _qbitSidAt = DateTime.MinValue;     // когда логинились; session timeout qBit по умолчанию 60 мин
    static readonly SemaphoreSlim _qbitGate = new SemaphoreSlim(1, 1);
    static readonly TimeSpan QbitSidTtl = TimeSpan.FromMinutes(30);

    static async Task<HttpClient> Qbit()
    {
        string gen = ModInit.conf.qbitHost + "|" + ModInit.conf.qbitUser + "|" + ModInit.conf.qbitPass;
        if (_qbitAuth == null || _qbitGen != gen || DateTime.UtcNow - _qbitSidAt > QbitSidTtl)
        {
            await _qbitGate.WaitAsync();
            try
            {
                if (_qbitAuth == null || _qbitGen != gen)
                {
                    // старый стек не диспозим: у параллельного запроса он может быть в полёте; отдаём GC (смена конфига редка)
                    _qbitPool = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
                    _qbitAuth = new QbitAuthHandler(_qbitPool);
                    _qbitGen = gen;
                    _qbitSidAt = DateTime.MinValue;
                }
                if (DateTime.UtcNow - _qbitSidAt > QbitSidTtl)
                    await QbitLogin(_qbitPool);
            }
            finally { _qbitGate.Release(); }
        }

        var c = new HttpClient(_qbitAuth, disposeHandler: false)
        {
            BaseAddress = new Uri(ModInit.conf.qbitHost),
            Timeout = TimeSpan.FromSeconds(ModInit.conf.timeoutSeconds)
        };
        // qBittorrent CSRF: Referer должен совпадать с хостом WebUI
        c.DefaultRequestHeaders.Referrer = new Uri(ModInit.conf.qbitHost);
        return c;
    }

    // Полный логин; зовётся только под _qbitGate. Кука пишется в общий CookieContainer стека.
    // Бэкофф: qBit лежит → логины из очереди на гейт падали бы ПОСЛЕДОВАТЕЛЬНО по timeoutSeconds
    // каждый (каскад вместо параллельного fail-fast, как было раньше). После неудачи ~8 с
    // отвечаем отказом сразу.
    static DateTime _qbitLoginFailAt = DateTime.MinValue;
    static readonly TimeSpan QbitLoginBackoff = TimeSpan.FromSeconds(8);

    static async Task QbitLogin(HttpClientHandler pool)
    {
        if (DateTime.UtcNow - _qbitLoginFailAt < QbitLoginBackoff)
            throw new Exception("qbit auth failed (backoff)");
        try
        {
            await QbitLoginCore(pool);
        }
        catch
        {
            _qbitLoginFailAt = DateTime.UtcNow;
            throw;
        }
    }

    static async Task QbitLoginCore(HttpClientHandler pool)
    {
        using var c = new HttpClient(pool, disposeHandler: false)
        {
            BaseAddress = new Uri(ModInit.conf.qbitHost),
            Timeout = TimeSpan.FromSeconds(ModInit.conf.timeoutSeconds)
        };
        c.DefaultRequestHeaders.Referrer = new Uri(ModInit.conf.qbitHost);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", ModInit.conf.qbitUser),
            new KeyValuePair<string, string>("password", ModInit.conf.qbitPass)
        });
        var resp = await c.PostAsync("/api/v2/auth/login", form);
        string login = (await resp.Content.ReadAsStringAsync())?.Trim();

        // Успех = 2xx + выставлена сессионная кука. qBit v5 отдаёт 204 + QBT_SID (тело пустое),
        // старые версии — 200 + "Ok.". Неверные креды: 403 (v5) или 200 + "Fails." без куки.
        bool hasSid = false;
        foreach (Cookie ck in pool.CookieContainer.GetCookies(new Uri(ModInit.conf.qbitHost)))
            if (ck.Name.StartsWith("QBT_SID", StringComparison.OrdinalIgnoreCase)) { hasSid = true; break; }

        if (!resp.IsSuccessStatusCode || (!hasSid && login != "Ok."))
            throw new Exception("qbit auth failed");

        _qbitSidAt = DateTime.UtcNow;
    }

    // Прозрачный re-login: SID протух (рестарт qBit, смена session timeout) → 401/403 → логин → повтор 1 раз.
    sealed class QbitAuthHandler : DelegatingHandler
    {
        public QbitAuthHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Content != null)
                await req.Content.LoadIntoBufferAsync();   // иначе повтор не сможет переслать POST-форму

            var resp = await base.SendAsync(req, ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized && resp.StatusCode != HttpStatusCode.Forbidden)
                return resp;
            if ((req.RequestUri?.AbsolutePath ?? "").EndsWith("/auth/login"))
                return resp;   // сам логин не ретраим (иначе рекурсия)
            resp.Dispose();

            await _qbitGate.WaitAsync(ct);
            try
            {
                // параллельный запрос мог уже перелогиниться, пока мы ждали гейт — не логинимся дважды
                if (DateTime.UtcNow - _qbitSidAt > TimeSpan.FromSeconds(5))
                {
                    _qbitSidAt = DateTime.MinValue;
                    await QbitLogin(_qbitPool);
                }
            }
            finally { _qbitGate.Release(); }

            var retry = new HttpRequestMessage(req.Method, req.RequestUri) { Content = req.Content };
            foreach (var h in req.Headers) retry.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await base.SendAsync(retry, ct);
        }
    }
    #endregion

    #region qdl.js (клиентский плагин Lampa)
    [HttpGet, AllowAnonymous]
    [Route("qdl.js")]
    public ActionResult Plugin()
    {
        string js = FileCache.ReadAllText($"{ModInit.modpath}/plugins/qdl.js", "qdl.js")
            .Replace("{localhost}", host);

        // /qdl.js?v={cacheVersion}: ?v меняется каждым рестартом, а задеплоить новый qdl.js без
        // рестарта нельзя (код в образе) → versioned-URL кэшируем навсегда, 163 КБ не перекачиваются
        // при каждом запуске. Легаси-запрос без ?v — прежний no-cache.
        if (HttpContext.Request.Query.ContainsKey("v"))
            HttpContext.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
        else
            SetHeadersNoCache();
        return ContentTo(js, "application/javascript; charset=utf-8");
    }
    #endregion

    #region /d1vision/hosts.json — OTA-список хостов + бренд для клиентских оболочек
    // Клиенты (D1Vision mac/ios, LAMPA-App android, Tizen-виджет) после успешного старта кэшируют
    // этот список нативно и на следующем запуске ДОБАВЛЯЮТ его к своему зашитому bootstrap-списку
    // (OTA только дополняет bootstrap — защита от «окирпичивания» при опечатке в конфиге).
    // Значения — brand/clientHosts из init.conf (секция QbitDownload), меняются без пересборки.
    // Канонический документ: E:\Media-server\claude\08-clients.md.
    // Дефолт здесь, а не в ModuleConf: populate-мердж init.conf ДОПОЛНЯЕТ преинициализированные
    // коллекции (дубли). Distinct — страховка на случай дублей уже в самом init.conf.
    static readonly List<string> defaultClientHosts = new List<string>
    {
        "http://192.168.87.24:9118",
        "https://tv.d1versy.com:9443",
        "https://tv2.d1versy.com:9443"
    };

    [HttpGet, AllowAnonymous]
    [Route("d1vision/hosts.json")]
    public ActionResult D1VisionHosts()
    {
        SetHeadersNoCache();
        // Фильтруем null/пустые (в init.conf может оказаться "clientHosts": [null] или "") и чужие
        // адреса — иначе мусор уедет клиентам в hosts[]; если после фильтра пусто — падаем на дефолты.
        // Форма ответа не меняется: отсеянный хост просто не попадает в массив (старые mac/iOS-сборки
        // читают тот же {ver,brand,hosts}).
        var hosts = (ModInit.conf.clientHosts ?? Enumerable.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).Where(IsOurClientHost).Distinct().ToList();
        if (hosts.Count == 0)
            hosts = defaultClientHosts;
        var payload = new JObject
        {
            ["ver"] = 1,
            ["brand"] = ModInit.conf.brand ?? "D1Vision",
            ["hosts"] = new JArray(hosts)
        };
        return ContentTo(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    // Наш ли адрес: домен проекта или приватный IP. Те же правила, по которым клиенты решают, кому
    // предъявлять ключ периметра (Android D1VAuth.isOurHost, Tizen loader.js) — чужой хост, попавший
    // в clientHosts, стал бы для клиента «своим» и получил бы ключ.
    static bool IsOurClientHost(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            string h = (u.Host ?? "").ToLowerInvariant();
            if (h.Length == 0) return false;
            if (h == "d1versy.com" || h.EndsWith(".d1versy.com")) return true;
            return IsPrivateHost(u);
        }
        catch { return false; }
    }
    #endregion

    #region /d1vision/apps — раздача бинарных билдов клиентов (OTA app updates)
    // Мини-стат-сервер поверх смонтированного тома clientBuildsPath: отдаёт и манифесты
    // (manifest.json у Android, appcast.xml у Sparkle/Mac), и сами бинари (APK/DMG). Один
    // роут на всё: publish-скрипт кладёт файл в client-builds/<platform>/, клиент качает его
    // отсюда. Путь защищён ConfinedCombine (traversal), большие файлы — с Range (докачка
    // апдейтером). Канон: E:\Media-server\claude\08-clients.md.
    [HttpGet, AllowAnonymous]
    [Route("d1vision/apps/{platform}/{**file}")]
    public ActionResult D1VisionAppBuild(string platform, string file)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(file))
            return NotFound();

        string baseDir = ModInit.conf.clientBuildsPath ?? "/client-builds";
        string full = ConfinedCombine(baseDir, $"{platform}/{file}");
        if (full == null || !System.IO.File.Exists(full))
            return NotFound();

        // Манифесты (json/xml) должны быть всегда свежими; бинари версионированы именем — можно кэшировать.
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext == ".json" || ext == ".xml")
            SetHeadersNoCache();

        return PhysicalFile(full, MimeType(full), enableRangeProcessing: true);
    }
    #endregion

    #region /qdl/search — раздачи через нативный индексатор Lampa (правильный фильм + все трекеры)
    [HttpGet, AllowAnonymous]
    [Route("qdl/search")]
    async public Task<ActionResult> Search(string query, string title = null, string title_original = null,
                                           int year = 0, int is_serial = -1, int season = 0, string apikey = null,
                                           string tmdb_id = null)
    {
        var sorted = await SearchScored(query, title, title_original, year, is_serial, season, apikey, tmdb_id);
        return ContentTo(sorted.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    // Весь пайплайн поиска (проходы индексатора + bitmagnet + дедуп + скоринг) — статический:
    // переиспользуется фоновыми контурами (EpisodeHunter, предложение переключения в CheckWatches).
    // tmdb_id опционален: без него просто не работает источник bitmagnet, остальное как раньше.
    static async Task<JArray> SearchScored(string query, string title, string title_original,
                                           int year, int is_serial, int season, string apikey,
                                           string tmdb_id = null)
    {
        string search = !string.IsNullOrWhiteSpace(query) ? query
                      : !string.IsNullOrWhiteSpace(title) ? title : title_original;
        if (string.IsNullOrWhiteSpace(search))
            return new JArray();

        // фоновые вызовы приходят без клиентского jackett_key — подставляем серверный (если задан)
        if (string.IsNullOrWhiteSpace(apikey)) apikey = ModInit.conf.indexerApikey;

        // Проход 1 — с типом от TMDB (movie→1 / tv→2): точная, хорошо ранжированная выдача (как было).
        // Проход 2 — ШИРОКИЙ (is_serial=0, ветка «всё подряд» JackettApi): ровно то, что находит нативный
        // «Смотреть через торрент» — опрашивает ВСЕ трекеры, включая аниме (AniLibria/AnimeLayer/Anifilm)
        // и всё, что узкие ветки «фильм/сериал» пропускают. Аниме TMDB отдаёт как media_type='tv' →
        // is_serial=2 его теряет, а «через торрент» находит именно широкой веткой. Мержим «и ту, и свою»
        // выдачу с дедупом по btih/parselink. См. claude/06 §A2.
        // ⚠️ Широкий проход идёт БЕЗ года — намеренно. Удалённый индекс, получив ПАРУ
        // «title + year», требует, чтобы год нашёлся и в самой раздаче. По отдельности
        // каждый параметр безобиден, а вместе они режут выдачу: у «Великий расхититель
        // гробниц» (корейский дунхуа 2026) из трёх существующих раздач доходила ОДНА —
        // две с rutracker (одна с 29 сидами) отваливались, потому что года в их названиях нет.
        // Смысл широкого прохода в том, чтобы ловить всё, что теряют узкие ветки, поэтому
        // сужать его ещё и годом неправильно. Релевантность добирает наш отсев по имени.
        var passes = new List<Task<JArray>> { FetchIndexer(query, title, title_original, year, is_serial, apikey) };
        if (is_serial >= 1)
            passes.Add(FetchIndexer(query, title, title_original, 0, 0, apikey));

        int indexerPasses = passes.Count;

        // Проход 3 — локальный индекс bitmagnet по TMDB id (точное совпадение, мусор невозможен).
        // Идёт параллельно с трекерами и его сбой не влияет на них: FetchBitmagnet сам глушит
        // исключения и отдаёт пустой список.
        var bitmagnetPass = FetchBitmagnet(tmdb_id, is_serial);
        passes.Add(bitmagnetPass);

        // Проход 4 — НАШ индекс: всё, что когда-либо отдавали все источники вместе.
        // Идёт ПОСЛЕДНИМ в дедупе (см. ниже) — живые источники всегда побеждают, индекс
        // добавляет только то, чего сейчас не отдал никто. Именно это и делает его страховкой
        // на случай смерти чужого удалённого хоста, не меняя выдачу в обычной ситуации.
        string queryNorm = Shared.Services.Utilities.SearchNameTo.Convert(!string.IsNullOrWhiteSpace(title) ? title : query) ?? "";
        var localIndexPass = FetchLocalIndex(tmdb_id, queryNorm, year, is_serial);
        passes.Add(localIndexPass);

        var all = await Task.WhenAll(passes);

        // FetchIndexer возвращает null именно на СБОЕ (не на пустой выдаче) — если развалились
        // все проходы к индексатору, это не «раздач нет», а «индексатор недоступен».
        // bitmagnet в счёт не идёт: он дополнительный и никогда не возвращает null.
        if (all.Take(indexerPasses).All(a => a == null))
            Console.WriteLine($"[QbitDownload] поиск «{search}»: все проходы индексатора провалились"
                            + (bitmagnetPass.Result.Count > 0 ? $" — выдачу спас bitmagnet ({bitmagnetPass.Result.Count})" : " — клиенту уйдёт пустой список"));

        var result = new JArray();
        var seen = new HashSet<string>();
        foreach (var arr in all)
        {
            if (arr == null) continue;
            foreach (var t in arr)
            {
                string mag = t.Value<string>("magnet");
                string link = t.Value<string>("parselink");
                string dedupe = !string.IsNullOrWhiteSpace(mag) ? MagnetHash(mag) : link;   // дедуп по btih / parselink
                if (!string.IsNullOrEmpty(dedupe) && !seen.Add(dedupe)) continue;
                result.Add(t);
            }
        }

        // Умный порядок: релевантность (имя/год/тип/сезон/полнота/свежесть) доминирует над сидами;
        // ⭐ rec + why у лучшей прошедшей гейты. Kill-switch searchScoring → старая сортировка по сидам.
        if (ModInit.conf.searchScoring)
        {
            var ctx = new ScoreCtx
            {
                titleNorm = Shared.Services.Utilities.SearchNameTo.Convert(!string.IsNullOrWhiteSpace(title) ? title : query),
                originalNorm = Shared.Services.Utilities.SearchNameTo.Convert(title_original),
                year = year,
                isSerial = is_serial >= 2,
                wantSeason = season,
                preferredQuality = ModInit.conf.preferredQuality
            };
            var sorted = TorrentScoring.SortAndMark(result, ctx, ModInit.conf.recommendMinSeeds);
            // fire-and-forget: пользователь не должен ждать индекс. Пишем ПОСЛЕ отсева —
            // мусор и чужие тайтлы в базу не попадают by design.
            IndexStoreAsync(sorted, tmdb_id, ctx.titleNorm, year, is_serial);
            return sorted;
        }

        // самые «живые» раздачи сверху (надёжнее докачиваются)
        return new JArray(result.OrderByDescending(x => x.Value<int?>("sid") ?? 0));
    }

    // один запрос к нативному индексатору Lampa (jackett-совместимый) с полным TMDB-контекстом.
    // Возвращает нормализованные раздачи; дедуп/сортировку/мерж проходов делает SearchScored.
    static async Task<JArray> FetchIndexer(string query, string title, string title_original, int year, int is_serial, string apikey)
    {
        string search = !string.IsNullOrWhiteSpace(query) ? query
                      : !string.IsNullOrWhiteSpace(title) ? title : title_original;

        var sb = new StringBuilder();
        sb.Append($"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}/api/v2.0/indexers/all/results");
        sb.Append("?apikey=").Append(HttpUtility.UrlEncode(apikey ?? ""));
        sb.Append("&Query=").Append(HttpUtility.UrlEncode(search ?? ""));
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("&title=").Append(HttpUtility.UrlEncode(title));
        if (!string.IsNullOrWhiteSpace(title_original)) sb.Append("&title_original=").Append(HttpUtility.UrlEncode(title_original));
        if (year > 0) sb.Append("&year=").Append(year);
        if (is_serial >= 0) sb.Append("&is_serial=").Append(is_serial);

        string raw = await Http.Get(sb.ToString(), timeoutSeconds: 40);

        // Раньше любой сбой индексатора молча превращался в пустой список, неотличимый от
        // «ничего не нашлось», и наверх уходило «Раздачи не найдены». Теперь возвращаем null
        // (= сбой) и пишем причину: Http.Get отдаёт null на любом не-200 и на таймауте, а
        // JacRed умеет ответить 200 с текстом («apikey», «typesearch == null»), который не JSON.
        if (string.IsNullOrEmpty(raw))
        {
            Console.WriteLine($"[QbitDownload] индексатор не ответил (не-200/таймаут): «{search}» is_serial={is_serial}");
            return null;
        }

        var result = new JArray();
        try
        {
            var arr = JObject.Parse(raw)["Results"] as JArray;
            if (arr != null)
            {
                foreach (var t in arr)
                {
                    string mag = t.Value<string>("MagnetUri");
                    string link = t.Value<string>("Link");
                    if (string.IsNullOrWhiteSpace(mag) && string.IsNullOrWhiteSpace(link)) continue;   // нечего качать

                    string ttl = t.Value<string>("Title") ?? "";
                    var it = new JObject
                    {
                        ["title"] = ttl,
                        ["magnet"] = mag,
                        ["parselink"] = link,
                        ["tracker"] = t.Value<string>("Tracker"),
                        ["sid"] = t.Value<int?>("Seeders") ?? 0,
                        ["size"] = HumanSize(t.Value<long?>("Size") ?? 0),
                        ["quality"] = QualityFromTitle(ttl),
                        ["codec"] = CodecFromTitle(ttl),
                        // мета для скоринга/охоты (раньше выбрасывалась)
                        ["pir"] = t.Value<int?>("Peers") ?? 0,
                        ["date"] = t.Value<string>("PublishDate"),
                        ["sizeBytes"] = t.Value<long?>("Size") ?? 0
                    };
                    if (t["Category"] is JArray catArr)
                        it["cats"] = catArr;
                    if (t["Info"] is JObject info)
                        it["info"] = info;   // только typesearch=red; в jackett-режиме отсутствует
                    result.Add(it);
                }
            }
        }
        catch (System.Exception ex)
        {
            // тело не JSON — почти всегда осмысленный текст от JacRed, его и показываем
            Console.WriteLine($"[QbitDownload] индексатор отдал не-JSON ({ex.GetType().Name}): «{search}» → {raw.Substring(0, System.Math.Min(120, raw.Length))}");
            return null;
        }
        return result;
    }

    static string HumanSize(long b)
    {
        if (b <= 0) return "";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double s = b; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return (i >= 3 ? s.ToString("0.0") : s.ToString("0")) + " " + u[i];
    }
    static int QualityFromTitle(string t)
    {
        var m = Regex.Match(t ?? "", "(2160|1080|720|480)p?", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    // кодек из названия раздачи: hevc/av1 браузер без транскода не декодирует (см. §Y).
    // Регексы строже кодек-строки _noiseRx: «голое» 265 или AV12/flav1 матчиться не должны
    static readonly Regex _codecHevcRx = new(@"(?i)(?<![a-z0-9])(?:[xh]\.?\s?265|hevc)", RegexOptions.Compiled);
    static readonly Regex _codecAv1Rx = new(@"(?i)(?<![a-z0-9])av1(?![0-9])", RegexOptions.Compiled);
    static readonly Regex _codecAvcRx = new(@"(?i)(?<![a-z0-9])(?:[xh]\.?\s?264|avc)(?![a-z])", RegexOptions.Compiled);
    static string CodecFromTitle(string t)
    {
        t = t ?? "";
        if (_codecHevcRx.IsMatch(t)) return "hevc";
        if (_codecAv1Rx.IsMatch(t)) return "av1";
        if (_codecAvcRx.IsMatch(t)) return "h264";
        return null;
    }
    #endregion

    #region /qdl/add — добавить magnet/.torrent в qBittorrent (резолв parselink при необходимости)
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/add")]
    async public Task<ActionResult> Add(string magnet = null, string parselink = null, string title = null, string query = null,
                                        string title_original = null, int year = 0, int is_serial = -1, int season = 0)
    {
        try
        {
            // link: настоящий "magnet:?...", либо URL-резолвер JacRed (parselink).
            // Резолвер может отдать: 302→magnet (rutracker/kinozal/nnm), magnet в теле, или .torrent-файл.
            string link = !string.IsNullOrWhiteSpace(magnet) ? magnet : parselink;
            string origLink = link;                  // исходный указатель на раздачу (для слежения)
            byte[] torrentFile = null;
            const long MaxBytes = 10L * 1024 * 1024;

            if (!string.IsNullOrWhiteSpace(link) && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                // SSRF-защита: ходим ТОЛЬКО на собственный JacRed-резолвер (loopback/наш listen-хост:порт).
                // Публичные трекеры дают готовый magnet (сюда не попадают). См. claude/06 §A,§J.
                if (!Uri.TryCreate(link, UriKind.Absolute, out var startUri) || !IsSelfResolver(startUri))
                    return Json(new { success = false, error = "bad link" });

                using var rh = new HttpClientHandler { AllowAutoRedirect = false };
                using var rc = new HttpClient(rh) { Timeout = TimeSpan.FromSeconds(15) };

                HttpResponseMessage resp = null;
                try
                {
                    var current = startUri;
                    for (int hop = 0; hop < 5; hop++)        // следуем редиректам (302→magnet и т.п.)
                    {
                        resp?.Dispose();
                        resp = await rc.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);

                        int code = (int)resp.StatusCode;
                        var loc = resp.Headers.Location;
                        if (code < 300 || code >= 400 || loc == null) break;   // терминальный ответ

                        var next = loc.IsAbsoluteUri ? loc : new Uri(resp.RequestMessage?.RequestUri ?? current, loc);
                        if (next.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                        { link = next.OriginalString; resp.Dispose(); resp = null; break; }

                        if (!IsSelfResolver(next)) { resp.Dispose(); resp = null; break; }   // наружу не ходим
                        current = next;
                    }

                    if (resp != null && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (resp.Content.Headers.ContentLength > MaxBytes)
                            return Json(new { success = false, error = "too big" });
                        try { await resp.Content.LoadIntoBufferAsync(MaxBytes); }
                        catch { return Json(new { success = false, error = "too big" }); }

                        byte[] data = await resp.Content.ReadAsByteArrayAsync();
                        if (LooksLikeTorrent(data))
                        {
                            torrentFile = data;
                        }
                        else
                        {
                            string b = Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
                            var m = Regex.Match(b ?? "", "magnet:\\?[^\"'\\s<]+");
                            if (m.Success) link = m.Value;
                            else
                            {
                                Console.WriteLine("[QbitDownload] resolve failed: " + (b ?? "").Trim());
                                return Json(new { success = false, error = "resolve failed" });
                            }
                        }
                    }
                }
                finally { resp?.Dispose(); }
            }

            using var c = await Qbit();
            MultipartFormDataContent content;
            string usedMagnet = null;

            if (torrentFile != null)
            {
                var fc = new ByteArrayContent(torrentFile);
                fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                content = new MultipartFormDataContent
                {
                    { fc, "torrents", "file.torrent" },
                    { new StringContent(ModInit.conf.downloadsPath), "savepath" },
                    { new StringContent(ModInit.conf.category), "category" }
                };
            }
            else
            {
                usedMagnet = link;
                if (string.IsNullOrWhiteSpace(usedMagnet) || !usedMagnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = false, error = "no magnet" });
                content = new MultipartFormDataContent
                {
                    { new StringContent(usedMagnet), "urls" },
                    { new StringContent(ModInit.conf.downloadsPath), "savepath" },
                    { new StringContent(ModInit.conf.category), "category" }
                };
            }

            var r = await c.PostAsync("/api/v2/torrents/add", content);
            string body = (await r.Content.ReadAsStringAsync())?.Trim() ?? "";

            // qBit-ответы зависят от версии (проверено эмпирически на v5/linuxserver):
            //   новый торрент → 200 + {"success_count":1,"added_torrent_ids":[..]} (старые: "Ok." / 204)
            //   дубликат      → 409 + "Conflict"
            bool ok = false, duplicate = false;
            if ((int)r.StatusCode == 409 || body.Equals("Conflict", StringComparison.OrdinalIgnoreCase))
            {
                duplicate = true; ok = true;          // уже в загрузках — это успех
            }
            else if (r.IsSuccessStatusCode)
            {
                if (body == "Ok." || body.Length == 0) ok = true;
                else if (body.StartsWith("{"))
                {
                    try
                    {
                        var j = JObject.Parse(body);
                        int success = j.Value<int?>("success_count") ?? 0;
                        int pending = j.Value<int?>("pending_count") ?? 0;
                        int dup = j.Value<int?>("duplicate_count") ?? 0;
                        duplicate = dup > 0;
                        ok = success > 0 || pending > 0 || duplicate;
                    }
                    catch { ok = false; }
                }
            }

            string hash = "";
            if (usedMagnet != null)
            {
                var hm = Regex.Match(usedMagnet, "btih:([0-9a-fA-F]{40}|[0-9a-zA-Z]{32})", RegexOptions.IgnoreCase);
                if (hm.Success) hash = hm.Groups[1].Value.ToLower();
            }

            // Пользователь мог нажать «Скачать» на раздаче, которую охота уже качает ДОНОРОМ (она же
            // топ-1 выдачи, то есть самый вероятный клик). qBit отвечает дубликатом и категорию не меняет:
            // загрузка осталась бы невидимой в гриде, качала одну серию и была бы снята с файлами уборкой
            // доноров. Промоутим её в основную и стираем донорские записи (инцидент 2026-07-25, «Укрытие»).
            if (ok && !string.IsNullOrEmpty(hash))
            {
                try
                {
                    JArray wl; lock (_watchLock) { wl = LoadWatch(); }
                    if (await PromoteIfDonor(c, hash, wl.OfType<JObject>(), query ?? hash))
                        lock (_watchLock) { SaveWatch(wl); }
                }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] add: promote donor: " + ex.Message); }
            }

            // сохраняем исходный указатель на раздачу — нужен для слежения за сериалом (пере-резолв),
            // плюс TMDB-контекст поиска (ctx) — фундамент охоты за сериями и переключения раздачи
            if (ok && !string.IsNullOrEmpty(hash) && !string.IsNullOrWhiteSpace(origLink))
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "links"));
                    var lj = new JObject { ["link"] = origLink, ["query"] = query };
                    if (!string.IsNullOrWhiteSpace(query) || !string.IsNullOrWhiteSpace(title_original) || year > 0 || is_serial >= 0)
                        lj["ctx"] = new JObject
                        {
                            ["title"] = query,                    // клиент шлёт query = название карточки
                            ["title_original"] = title_original,
                            ["year"] = year,
                            ["is_serial"] = is_serial,
                            ["season"] = season
                        };
                    System.IO.File.WriteAllText(LinkPath(hash), lj.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { }
            }

            return Json(new { success = ok, duplicate, hash, body });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] add: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
    #endregion

    #region /qdl/list — список загрузок (категория lampa)
    [HttpGet, AllowAnonymous]
    [Route("qdl/list")]
    async public Task<ActionResult> List()
    {
        try
        {
            using var c = await Qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}&sort=added_on&reverse=true");

            var watched = new HashSet<string>();
            var donorHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in LoadWatch())
            {
                var wh = w.Value<string>("hash"); if (!string.IsNullOrEmpty(wh)) watched.Add(wh);
                if (w["donors"] is JArray ds)   // страховка: доноры и так в другой категории qBit
                    foreach (var d in ds) { var dh = d.Value<string>("hash"); if (!string.IsNullOrEmpty(dh)) donorHashes.Add(dh); }
            }

            // снапшот штампов активности один на запрос (Touch пишет из фоновых потоков — не дёргать файл на каждый элемент)
            JObject act; lock (_activityLock) act = ActivityLoad();
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var result = new JArray();
            foreach (var t in JArray.Parse(raw))
            {
                string h = t.Value<string>("hash") ?? "";
                if (donorHashes.Contains(h)) continue;   // раздачи-доноры (охота) — не карточки «Загрузок»
                double prog = t.Value<double?>("progress") ?? 0;
                long addedOn = t.Value<long?>("added_on") ?? 0;
                var item = new JObject
                {
                    ["hash"] = h,
                    ["name"] = t.Value<string>("name"),
                    ["progress"] = prog,
                    ["state"] = t.Value<string>("state"),
                    ["size"] = t.Value<long?>("size") ?? 0,
                    ["save_path"] = t.Value<string>("save_path"),
                    ["content_path"] = t.Value<string>("content_path"),
                    ["has_poster"] = ValidHash(h) && System.IO.File.Exists(PosterPath(h)),
                    ["watched"] = watched.Contains(h),
                    ["added"] = addedOn,
                    ["activity"] = CardActivity(addedOn, t.Value<long?>("completion_on") ?? 0, prog, ActivityStored(act, h), nowUnix)
                };
                if (ValidHash(h) && System.IO.File.Exists(MetaPath(h)))
                {
                    try { item["meta"] = JObject.Parse(System.IO.File.ReadAllText(MetaPath(h))); } catch { }
                }
                result.Add(item);
            }

            // локальные файлы (транскоды в MP4): торрент удалён, файл остался — ключ тот же infohash
            try
            {
                string localDir = Path.Combine(ModInit.conf.cachePath, "local");
                if (Directory.Exists(localDir))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var it in result) seen.Add(it.Value<string>("hash") ?? "");
                    // подписки jut.su — один раз на весь список, не на карточку
                    var jutWatched = JutWatchedSlugs();
                    foreach (var lf in Directory.GetFiles(localDir, "*.json"))
                    {
                        string h = Path.GetFileNameWithoutExtension(lf);
                        if (!ValidHash(h) || seen.Contains(h)) continue;   // оверлей при живом торренте сюда не попадёт (hash уже в списке)
                        JObject loc = LoadLocal(h);
                        var lfs = LocalFiles(loc);
                        lfs.RemoveAll(f => !System.IO.File.Exists(f.path));
                        if (lfs.Count == 0) continue;
                        long lsize = 0; foreach (var f in lfs) lsize += f.size;
                        string cpath = loc.Value<string>("dir") ?? lfs[0].path;
                        var item = new JObject
                        {
                            ["hash"] = h,
                            ["name"] = loc.Value<string>("name") ?? Path.GetFileName(lfs[0].path),
                            ["progress"] = 1.0,
                            ["state"] = "local",
                            ["local"] = true,
                            ["size"] = loc.Value<long?>("size") ?? lsize,
                            ["save_path"] = lfs.Count == 1 ? Path.GetDirectoryName(lfs[0].path) : loc.Value<string>("dir"),
                            ["content_path"] = cpath,
                            ["has_poster"] = System.IO.File.Exists(PosterPath(h)),
                            ["watched"] = false,
                            ["added"] = loc.Value<long?>("added") ?? MarkerFallbackAdded(lf)
                        };
                        // jut.su: пробрасываем slug/сезон и РЕАЛЬНЫЙ статус подписки.
                        // Без этого «Загрузки» не знают, что карточка из jut.su, и пункт
                        // «Следить за новыми сериями» неоткуда было показать: торрентная
                        // ветка гейтится по !local, а jut-карточка ровно локальная.
                        // Сезон намеренно НЕ пробрасываем: маркер один на весь тайтл, а серии
                        // в нём могут быть из разных сезонов — какой сезон «следить» решает
                        // сервер (берёт последний вышедший) при вызове /qdl/jut/watch.
                        string jslug = (loc["jut"] as JObject)?.Value<string>("slug");
                        if (!string.IsNullOrEmpty(jslug))
                        {
                            item["jut"] = new JObject { ["slug"] = jslug };
                            item["watched"] = jutWatched.Contains(jslug);
                        }
                        // без Touch activity == added → финализированный транскод позицию не меняет (§AG)
                        item["activity"] = Math.Max(item.Value<long?>("added") ?? 0, ActivityStored(act, h));
                        if (System.IO.File.Exists(MetaPath(h)))
                            try { item["meta"] = JObject.Parse(System.IO.File.ReadAllText(MetaPath(h))); } catch { }
                        result.Add(item);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] list local: " + ex.Message); }

            // сироты в activity.json (карточка удалена мимо PurgeCache) — здесь единственное место с полным списком живых
            try
            {
                var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in result) live.Add((it.Value<string>("hash") ?? "").ToLowerInvariant());
                ActivityPrune(live, nowUnix);
            }
            catch { }

            // единый порядок по актуальности последней загрузки (новое сверху): новая серия/докачка
            // поднимает карточку; фолбэк и тай-брейк — прежняя дата добавления
            var ordered = new JArray(result
                .OrderByDescending(x => x.Value<long?>("activity") ?? x.Value<long?>("added") ?? 0)
                .ThenByDescending(x => x.Value<long?>("added") ?? 0));
            return ContentTo(ordered.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] list: " + ex);
            return Json(new { error = "internal error" });
        }
    }
    #endregion

    #region /qdl/files — файлы торрента (для сериалов/мультифайла)
    [HttpGet, AllowAnonymous]
    [Route("qdl/files")]
    async public Task<ActionResult> Files(string hash)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            // локальный транскод: файлы маркера в том же формате ответа, что qBit files.
            // Оверлей (торрент жив) идёт обычным qBit-путём — клиент видит торрент-список,
            // а подмена на mp4 происходит в ResolveFile при воспроизведении.
            var loc = LoadLocal(hash);
            if (loc != null && !LocalIsOverlay(loc))
            {
                var arr = new JArray();
                foreach (var f in LocalFiles(loc))
                {
                    if (!System.IO.File.Exists(f.path)) continue;
                    arr.Add(new JObject
                    {
                        ["index"] = f.index,
                        ["name"] = f.name,
                        ["size"] = f.size > 0 ? f.size : new FileInfo(f.path).Length,
                        ["progress"] = 1.0,
                        ["priority"] = 1
                    });
                }
                return ContentTo(arr.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
            }

            using var c = await Qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
            // оверлей-сирота: торрент удалили извне, а маркер остался → фолбэк на файлы маркера
            if (loc != null && string.IsNullOrWhiteSpace(raw?.Trim('[', ']', ' ')))
            {
                var arr = new JArray();
                foreach (var f in LocalFiles(loc))
                    if (System.IO.File.Exists(f.path))
                        arr.Add(new JObject { ["index"] = f.index, ["name"] = f.name, ["size"] = f.size, ["progress"] = 1.0, ["priority"] = 1 });
                if (arr.Count > 0)
                    return ContentTo(arr.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
            }
            return ContentTo(raw ?? "[]", "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] files: " + ex);
            return Json(new { error = "internal error" });
        }
    }
    #endregion

    #region /qdl/stream — отдать файл с диска D с поддержкой перемотки (оффлайн-плеер)
    [HttpGet, AllowAnonymous]
    [Route("qdl/stream")]
    async public Task<ActionResult> Stream(string hash, int index = -1)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            string full = await ResolveFileCached(hash, index);   // на хите — ноль обращений к qBit (важно: плеер шлёт Range-seek'и очередями)
            if (full == null) return NotFound();
            return PhysicalFile(full, MimeType(full), enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] stream: " + ex);
            return Json(new { error = "internal error" });
        }
    }

    // ── Кеш резолва hash+index → путь на диске ──
    // Каждый холодный резолв = 2 GET к qBit API; без кеша это платил каждый /qdl/stream (включая
    // каждый Range-seek), /qdl/audio и каждый HLS-рестарт. TTL короткий + File.Exists-гард на хите;
    // негативные результаты не кешируем (файл мог ещё докачиваться и вот-вот появится).
    static readonly ConcurrentDictionary<string, (string path, DateTime at)> _resolveCache = new();
    static readonly TimeSpan ResolveTtl = TimeSpan.FromMinutes(5);

    static async Task<string> ResolveFileCached(string hash, int index, HttpClient c = null)
    {
        string key = hash.ToLowerInvariant() + ":" + index;
        if (_resolveCache.TryGetValue(key, out var e))
        {
            if (DateTime.UtcNow - e.at < ResolveTtl && System.IO.File.Exists(e.path)) return e.path;
            _resolveCache.TryRemove(key, out _);
        }

        string p;
        if (c != null) p = await ResolveFile(c, hash, index);
        else { using var qc = await Qbit(); p = await ResolveFile(qc, hash, index); }

        if (p != null) _resolveCache[key] = (p, DateTime.UtcNow);
        return p;
    }

    // Маппинг hash+index→путь изменился (удаление, оверлей-mp4, re-grab/SWITCH, замещение донора) —
    // снять все записи хеша; кеш ffprobe-дорожек по снятым путям тоже.
    static void DropResolveCache(string hash)
    {
        string pre = (hash ?? "").ToLowerInvariant() + ":";
        foreach (var kv in _resolveCache)
        {
            if (!kv.Key.StartsWith(pre, StringComparison.Ordinal)) continue;
            if (_resolveCache.TryRemove(kv.Key, out var e) && e.path != null)
                _probeCache.TryRemove(e.path, out _);
        }
    }

    // Находит локальный путь к видеофайлу торрента (index<0 → самый большой). null если нет.
    static async Task<string> ResolveFile(HttpClient c, string hash, int index)
    {
        // локальный (не-торрент) файл — транскод: путь хранится в маркере, qBit не спрашиваем.
        // Оверлей — идём в qBit (индексы клиента = торрент-индексы), подмена на mp4 ниже.
        var loc = LoadLocal(hash);
        var lfs = loc != null ? LocalFiles(loc) : null;
        if (loc != null && !LocalIsOverlay(loc))
            return PickLocal(lfs, index)?.path;

        string he = HttpUtility.UrlEncode(hash);

        string infoRaw = await c.GetStringAsync($"/api/v2/torrents/info?hashes={he}");
        var info = JArray.Parse(infoRaw);
        if (info.Count == 0) return PickLocal(lfs ?? new List<LocalFile>(), index)?.path;   // оверлей-сирота: торрент удалён извне
        string savePath = info[0].Value<string>("save_path") ?? ModInit.conf.downloadsPath;
        string contentPath = info[0].Value<string>("content_path");

        string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={he}");
        var files = JArray.Parse(filesRaw);
        if (files.Count == 0) return PickLocal(lfs ?? new List<LocalFile>(), index)?.path;

        JToken file = null;
        if (index >= 0)
            foreach (var f in files)
                if ((f.Value<int?>("index") ?? -1) == index) { file = f; break; }
        if (file == null)
        {
            long max = -1;
            foreach (var f in files) { long s = f.Value<long?>("size") ?? 0; if (s > max) { max = s; file = f; } }
        }
        if (file == null) return null;

        string rel = file.Value<string>("name");

        // оверлей: серия уже транскожена → отдаём mp4-копию вместо торрент-оригинала (HEVC)
        var ov = OverlayFor(lfs, rel);
        if (ov != null) return ov.path;

        string full = null;
        if (files.Count == 1 && !string.IsNullOrEmpty(contentPath) && System.IO.File.Exists(contentPath))
            full = contentPath;
        if (full == null) full = ConfinedCombine(savePath, rel);
        if (full == null || !System.IO.File.Exists(full))
            full = ConfinedCombine(ModInit.conf.downloadsPath, rel);
        if (full == null || !System.IO.File.Exists(full)) return null;
        return full;
    }

    // Безопасная сборка пути: выкидываем .. / . / пустые сегменты, канонизируем и проверяем,
    // что результат строго внутри baseDir (защита от path traversal в file.name).
    static string ConfinedCombine(string baseDir, string rel)
    {
        if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(rel)) return null;

        var parts = rel.Replace('\\', '/').Split('/');
        var clean = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == "..") continue;
            clean.Add(p);
        }
        if (clean.Count == 0) return null;

        string baseFull = Path.GetFullPath(baseDir);
        string candidate = Path.GetFullPath(Path.Combine(baseFull, string.Join("/", clean)));

        string prefix = baseFull.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, cmp)) return null;

        return candidate;
    }

    static string MimeType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".mp4":
            case ".m4v": return "video/mp4";
            case ".mkv": return "video/x-matroska";
            case ".avi": return "video/x-msvideo";
            case ".ts": return "video/mp2t";
            case ".webm": return "video/webm";
            case ".mov": return "video/quicktime";
            // клиентские билды/манифесты (OTA app updates)
            case ".apk": return "application/vnd.android.package-archive";
            case ".dmg": return "application/x-apple-diskimage";
            case ".wgt":
            case ".exe":
            case ".msi":
            case ".nupkg":
            case ".zip": return "application/octet-stream";
            case ".xml": return "application/xml; charset=utf-8";
            case ".json": return "application/json; charset=utf-8";
            default: return "application/octet-stream";
        }
    }
    #endregion

    #region /qdl/delete — удалить загрузку (опционально с файлами)
    [HttpGet, AllowAnonymous]
    [Route("qdl/delete")]
    async public Task<ActionResult> Delete(string hash, bool deleteFiles = false)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            // локальный транскод: удаляем файлы + маркер + все следы (в qBit его уже нет)
            var loc = LoadLocal(hash);
            if (loc != null && !LocalIsOverlay(loc))
            {
                // jut.su: подписка живёт в ОТДЕЛЬНОМ файле, о котором PurgeCache не знает.
                // Не снять её здесь — и при автоскачивании следующая серия молча пересоздаст
                // карточку и папку: «удалил, а оно вернулось».
                string jutSlug = (loc["jut"] as JObject)?.Value<string>("slug");
                if (!string.IsNullOrEmpty(jutSlug)) JutForgetOnDelete(jutSlug);

                if (deleteFiles) DeleteLocalFiles(loc);
                try { using var c2 = await Qbit(); await DeleteDonorsOf(c2, hash); } catch { }   // хвосты охоты, если были
                DropHlsCache(hash);
                DropResolveCache(hash);
                PurgeCache(hash);   // маркер local/<hash>.json удалит тоже
                return Json(new { success = true });
            }

            using var c = await Qbit();
            // папку основной запоминаем ДО её удаления: каскад по донорам идёт после, а без этого пути
            // донор, сидящий в той же папке, снёс бы файлы вместе с собой (проверять было бы не с чем)
            string mainContentPath = (await QbitTorrentInfo(c, hash))?.Value<string>("content_path");
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")
            });
            var r = await c.PostAsync("/api/v2/torrents/delete", form);
            if (r.IsSuccessStatusCode)
            {
                if (loc != null && deleteFiles) DeleteLocalFiles(loc);   // оверлей: mp4-копии удаляем вместе с торрентом
                await DeleteDonorsOf(c, hash, mainContentPath);   // каскад: раздачи-доноры этой загрузки (охота) — с файлами
                DropHlsCache(hash);
                DropResolveCache(hash);
                PurgeCache(hash);
            }
            return Json(new { success = r.IsSuccessStatusCode });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] delete: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
    #endregion

    #region /qdl/save — сохранить метаданные TMDB + закэшировать постер локально (SSD)
    // Один клиент на ВСЕ скачивания постеров: healPoster (qdl.js) дёргается на каждый рендер грида
    // для каждой битой карточки — «new HttpClient на запрос» жёг бы сокеты. AllowAutoRedirect
    // оставляем включённым ОСОЗНАННО: наш /tmdb/img при сбое апстрима отвечает 302 на image.tmdb.org
    // (TmdbProxy/Controller.cs) — фолбэк на прямой TMDB достаётся бесплатно.
    static readonly HttpClient _posterHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    // Антистампед. Раньше промах стоил 0 мс (см. ниже про молчаливый пропуск), теперь — реальный
    // round-trip, а heal стреляет по каждой битой карточке на каждый рендер: без гарда холодный кеш
    // на 60 карточек = 60 параллельных self-запросов в свой же Kestrel по 20 с каждый.
    static readonly ConcurrentDictionary<string, byte> _posterInFlight = new();

    // Троттл лога, одна строка на хэш в 10 минут. Молчаливый пропуск (if без else + голый catch) —
    // ровно то, из-за чего поломка постеров в qdl 2.15 прожила сутки незамеченной; но и заливать
    // stdout строкой на карточку на каждый рендер нельзя.
    static readonly ConcurrentDictionary<string, DateTime> _posterLoggedAt = new();
    static readonly TimeSpan PosterLogCooldown = TimeSpan.FromMinutes(10);

    static void LogPosterOnce(string hash, string why)
    {
        var now = DateTime.UtcNow;
        if (_posterLoggedAt.TryGetValue(hash, out var was) && now - was < PosterLogCooldown)
            return;
        _posterLoggedAt[hash] = now;
        Console.WriteLine($"[QbitDownload] poster {hash}: {why}");
    }

    static readonly Regex _tmdbImgRx = new Regex(@"(?:/tmdb/img/|image\.tmdb\.org/)(t/p/[^?#]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Белый список: суффикс уходит в /tmdb/img/{*suffix} дословно, а тот форвардит его апстриму.
    // Без проверки клиент мог бы гонять наш прокси по произвольному пути TMDB (в т.ч. через «..»).
    static readonly Regex _tmdbPathOkRx = new Regex(@"^t/p/[A-Za-z0-9][A-Za-z0-9_.\-]*/[A-Za-z0-9_\-]+\.(?:jpg|png|webp)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Относительный путь картинки TMDB («t/p/w500/abc.jpg») из того, что прислал клиент, — или из
    /// меты на диске. null = это не картинка TMDB (её разберёт ветка внешнего постера).
    ///
    /// ЗАЧЕМ ПАРСИТЬ, А НЕ БРАТЬ URL КАК ЕСТЬ: с qdl 2.15 proxy_tmdb форсится в true каждую загрузку
    /// (lampainit-invc.js) → Lampa.TMDB.image отдаёт НЕ адрес TMDB, а НАШ прокси:
    /// «{localhost}/tmdb/img/t/p/w500/…?account_email=&amp;uid=», где {localhost} подставляется при отдаче
    /// плагина (TmdbProxy/Controller.cs). В LAN это http://192.168.87.24:9118 → прежняя проверка
    /// «https И не приватный хост» резала постер МОЛЧА; снаружи ({localhost} = https://tv.d1versy.com:9443)
    /// проверку проходило, но упиралось в отсутствие hairpin-NAT из контейнера (а был бы hairpin —
    /// D1VPerimeter отдал бы пустой 404: у серверного HttpClient нет ключа/куки d1v). Итог: форме URL
    /// от клиента больше не доверяем — берём из неё только путь картинки и качаем сами.
    ///
    /// Ветка «мета с диска» ОБЯЗАТЕЛЬНА для healPoster: он шлёт только hash + poster_url, без card.
    /// Query отбрасываем: апстриму account_email/uid/token не нужны (они и так в CoreInit.SkipQueryKeys).
    /// </summary>
    static string TmdbPosterPath(string posterUrl, string hash)
    {
        string path = null;

        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            var m = _tmdbImgRx.Match(posterUrl);
            if (m.Success)
                path = m.Groups[1].Value;
        }

        if (path == null && ValidHash(hash))
        {
            try
            {
                string mp = MetaPath(hash);
                if (System.IO.File.Exists(mp))
                {
                    string pp = JObject.Parse(System.IO.File.ReadAllText(mp))["poster_path"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(pp) && pp[0] == '/')
                        path = "t/p/w500" + pp;   // w500 — тот же размер, что просит saveMeta в qdl.js
                }
            }
            catch { }
        }

        return (path != null && _tmdbPathOkRx.IsMatch(path)) ? path : null;
    }

    /// <summary>
    /// Скачать картинку. Общий низ для всех веток: успех + image/* + кап 6 МБ + непустое тело.
    /// Любая ошибка — null (вызывающий решает, ретраить ли другим адресом).
    /// </summary>
    static async Task<byte[]> PosterBytes(string url, string hostHeader = null, bool forwardHttps = false)
    {
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(hostHeader))
                rq.Headers.TryAddWithoutValidation("Host", hostHeader);
            if (forwardHttps)
                rq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

            using var rs = await _posterHttp.SendAsync(rq, HttpCompletionOption.ResponseHeadersRead);
            if (!rs.IsSuccessStatusCode)
                return null;
            if (!(rs.Content.Headers.ContentType?.MediaType ?? "").StartsWith("image/"))
                return null;

            await rs.Content.LoadIntoBufferAsync(6_000_000);
            byte[] img = await rs.Content.ReadAsByteArrayAsync();
            return (img != null && img.Length > 200) ? img : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Постер TMDB качаем ЧЕРЕЗ СВОЙ /tmdb/img на loopback: там кеш картинок на SSD (immutable, TTL год)
    /// и, если настроен, апстрим-прокси. Прецедент ровно такого self-запроса — CatalogWarmup.Fetch;
    /// локальный запрос D1VPerimeter пропускает.
    ///
    /// ⚠️ Host и X-Forwarded-Proto подставляем КЛИЕНТСКИЕ: ключ Staticache = Scheme+Host+Path
    /// (Staticache.getQueryKeys), и запрос «от себя» с Host: 127.0.0.1 попал бы в ЧУЖОЙ бакет вместо
    /// того, что уже прогрет гридом и CatalogWarmup.
    /// </summary>
    static async Task<byte[]> FetchTmdbPoster(string tmdbPath, string clientAuthority, bool clientHttps)
    {
        int port = 9118;
        try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }

        byte[] img = await PosterBytes($"http://127.0.0.1:{port}/tmdb/img/{tmdbPath}", clientAuthority, clientHttps);

        // сам Kestrel не ответил — до 302-фолбэка ВНУТРИ /tmdb/img дело не дошло, идём на TMDB напрямую
        if (img == null)
            img = await PosterBytes("https://image.tmdb.org/" + tmdbPath);

        return img;
    }

    [HttpPost, AllowAnonymous]
    [Route("qdl/save")]
    async public Task<ActionResult> Save(string hash, string card = null, string poster_url = null)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "meta"));
            Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "img"));

            // Клиент шлёт уже подготовленную карточку (slimCard) со всеми нужными полями —
            // храним как есть (валидируем JSON + кап размера), чтобы метаданные были богатыми.
            if (!string.IsNullOrWhiteSpace(card) && card.Length < 65536)
            {
                try
                {
                    var j = JObject.Parse(card);
                    System.IO.File.WriteAllText(MetaPath(hash), j.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { }
            }

            // Постер качаем сами. Картинку TMDB — в ЛЮБОЙ форме URL (наш прокси, прямой image.tmdb.org)
            // или вообще без URL, по мете с диска — тянем через свой /tmdb/img (TmdbPosterPath/FetchTmdbPoster).
            // Всё остальное («внешний» не-TMDB постер) — по прежним правилам: только https + image/* +
            // кап 6 МБ, loopback/приват запрещены (анти-SSRF). Порядок веток важен: если клиент прислал
            // НАШ адрес, он уже разобран первой веткой, и проверка «хост наш» во второй не нужна.
            string tmdbPath = TmdbPosterPath(poster_url, hash);
            byte[] img = null;
            string why = null;

            if (tmdbPath != null)
            {
                // параллельный heal по этому же хэшу уже качает — второй раз не ходим
                if (_posterInFlight.TryAdd(hash, 0))
                {
                    try
                    {
                        string auth = null; bool https = false;
                        try { var hu = new Uri(host); auth = hu.Authority; https = hu.Scheme == "https"; } catch { }

                        img = await FetchTmdbPoster(tmdbPath, auth, https);
                        if (img == null)
                            why = "tmdb fetch failed: " + tmdbPath;
                    }
                    finally { _posterInFlight.TryRemove(hash, out _); }
                }
            }
            else if (!string.IsNullOrWhiteSpace(poster_url)
                && Uri.TryCreate(poster_url, UriKind.Absolute, out var pu)
                && pu.Scheme == "https" && !IsPrivateHost(pu))
            {
                img = await PosterBytes(pu.ToString());
                if (img == null)
                    why = "external fetch failed";
            }
            else if (!string.IsNullOrWhiteSpace(poster_url))
                why = "rejected poster_url (не картинка TMDB и не публичный https)";

            if (img != null)
                System.IO.File.WriteAllBytes(PosterPath(hash), img);
            else if (why != null)
                LogPosterOnce(hash, why);

            // reason — для следующего разбора: почему постера нет, видно прямо в ответе (qdl.js
            // неизвестные поля игнорирует)
            return Json(new { success = true, has_poster = System.IO.File.Exists(PosterPath(hash)), reason = why });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] save: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
    #endregion

    #region /qdl/poster — отдать локально закэшированный постер
    [HttpGet, AllowAnonymous]
    [Route("qdl/poster")]
    public ActionResult Poster(string hash)
    {
        if (!ValidHash(hash)) return BadRequest();
        string p = PosterPath(hash);
        if (!System.IO.File.Exists(p)) return NotFound();
        // сутки клиентского кэша: грид на N карточек не делает N ревалидаций при каждом входе;
        // смена постера видна инициатору сразу (qdl.js добавляет &t= после heal/save)
        HttpContext.Response.Headers["Cache-Control"] = "public,max-age=86400";
        return PhysicalFile(p, "image/jpeg");
    }
    #endregion

    #region /qdl/hls — HLS-транскод для браузера (звук EAC3/AC3/DTS → AAC; видео copy, старые кодеки → live-x264; ключ _m → мобильный 720p-профиль)
    const int HlsSegSec = 6;      // длительность сегмента (-hls_time)
    const int HlsAheadSegs = 3;   // запрос «чуть впереди» прогресса → ждём готовности, а не рестартим ffmpeg

    // один запуск ffmpeg; при hlsSeek перезапускается с -ss в точку перемотки
    sealed class HlsSession
    {
        public IFfJob job;            // локальный процесс или джоб хостового GPU-воркера
        public int startSeg;          // с какого сегмента начат запуск (-start_number); -1 = легаси с начала
        public string ffPlaylist;     // плейлист этого запуска (маркер прогресса); в VOD-режиме клиенту не отдаётся
        public volatile bool killed;  // прибит рестартом → не считать фейлом
    }
    static readonly ConcurrentDictionary<string, HlsSession> _hlsRunning = new();
    static readonly ConcurrentDictionary<string, object> _hlsLock = new();       // пер-ключ лок kill+start
    static readonly ConcurrentDictionary<string, double> _hlsDur = new();        // длительность источника по key; 0 = ключ в легаси-режиме
    static readonly ConcurrentDictionary<string, bool> _hlsCopyByPath = new();   // кэш решения copy/x264 по пути (ffprobe при каждом seek-рестарте недёшев)
    static readonly ConcurrentDictionary<string, DateTime> _hlsFailed = new();   // негатив-кэш упавших ffmpeg (key, для seek-запусков key:startSeg)
    static readonly ConcurrentDictionary<string, DateTime> _hlsTouch = new();    // последняя активность (защита от удаления при просмотре)
    static readonly TimeSpan _hlsFailTtl = TimeSpan.FromMinutes(3);
    static readonly TimeSpan _hlsTouchTtl = TimeSpan.FromMinutes(30);
    static readonly System.Threading.Timer _hlsIdleTimer = new(_ => { KillIdleHls(); CleanupHlsThrottled(300); }, null, 60_000, 30_000);   // держим ссылку от GC

    // Зритель закрыл приложение/поставил долгую паузу — запросы сегментов прекратились, а ffmpeg
    // молотил бы до конца файла (для _m-профиля это весь фильм на GPU впустую). Глушим VOD-сессии
    // без активности дольше hlsIdleKillSec: любой следующий запрос сегмента перезапустит транскод
    // с -ss в нужную точку (штатный путь §AD, ~3-5 с). Легаси-сессии (startSeg<0, линейный
    // event-плейлист) не трогаем — их рестарт умеет только с нуля.
    static void KillIdleHls()
    {
        try
        {
            int ttl = ModInit.conf?.hlsIdleKillSec ?? 0;
            if (ttl <= 0) return;
            var now = DateTime.UtcNow;
            foreach (var kv in _hlsRunning)
            {
                if (kv.Value.startSeg < 0) continue;
                if (_hlsTouch.TryGetValue(kv.Key, out var t) && (now - t).TotalSeconds < ttl) continue;
                // TryEnter, а не lock: если ключ занят рестартом (тот под локом мог зависнуть в
                // пробе/Kill) — не блокируем весь пасс таймера, уедем на следующий тик.
                var lk = _hlsLock.GetOrAdd(kv.Key, _ => new object());
                if (!System.Threading.Monitor.TryEnter(lk)) continue;
                try
                {
                    if (!_hlsRunning.TryGetValue(kv.Key, out var sess) || !ReferenceEquals(sess, kv.Value)) continue;   // сессию уже сменил рестарт
                    if (_hlsTouch.TryGetValue(kv.Key, out var t2) && (now - t2).TotalSeconds < ttl) continue;           // успели вернуться
                    sess.killed = true;   // прибит нами, не фейл — негатив-кэш не трогаем
                    try { sess.job?.Kill(); } catch { }
                    _hlsRunning.TryRemove(kv.Key, out _);
                    Console.WriteLine("[QbitDownload] hls idle-kill key=" + kv.Key + " (нет запросов " + ttl + "с)");
                }
                finally { System.Threading.Monitor.Exit(lk); }
            }
        }
        catch { }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/hls/{key}/{file}")]
    async public Task<ActionResult> Hls(string key, string file)
    {
        var mk = Regex.Match(key ?? "", "^([0-9a-fA-F]{40}|[0-9A-Za-z]{32})_(-?\\d+)(?:_(o|e\\d+|d[0-9a-f]{8}|f\\d+))?(?:_(m))?$");
        if (!mk.Success) return BadRequest();
        if (!Regex.IsMatch(file ?? "", "^(playlist\\.m3u8|seg\\d{1,6}\\.ts)$")) return BadRequest();

        string hash = mk.Groups[1].Value;
        int index = int.Parse(mk.Groups[2].Value);
        string audio = mk.Groups[3].Success ? mk.Groups[3].Value : "o";   // o=ориг, eN=встроенная дорожка, fN=внешний файл-озвучка
        bool mobile = mk.Groups[4].Success;   // «мобильный» профиль (_m): live-даунскейл + кап битрейта (телефон на сотовой)

        // Ключ периметра, предъявленный запросом (query или cookie). Нужен дважды: для подписи
        // сегментных строк плейлиста (VLC резолвит относительные URI БЕЗ query базового URL,
        // RFC 3986 — снаружи периметр зарезал бы сегменты 404-ом) и для редиректа ниже.
        // В LAN ключа нет → ответы байт-в-байт прежние.
        string d1vKey = Request.Query.TryGetValue("d1v", out var d1vq) && d1vq.Count > 0 ? d1vq[0] : null;
        if (string.IsNullOrEmpty(d1vKey))
        {
            string cn = CoreInit.conf?.d1v?.cookieName;
            if (!string.IsNullOrEmpty(cn)) Request.Cookies.TryGetValue(cn, out d1vKey);
        }

        // Профиль выключен в конфиге → 302 на обычный ключ (деградация к оригинальному
        // качеству), а НЕ 404: клиент с уже построенным _m-URL не должен терять воспроизведение.
        if (mobile && !ModInit.conf.hlsMobile)
            return Redirect("/qdl/hls/" + key.Substring(0, key.Length - 2) + "/" + file
                + (string.IsNullOrEmpty(d1vKey) ? "" : "?d1v=" + Uri.EscapeDataString(d1vKey)));

        string dir = Path.Combine(ModInit.conf.hlsPath, key);      // ключ содержит _m → своя кэш-папка, с обычным HLS не смешивается
        string target = Path.Combine(dir, file);

        try
        {
            _hlsTouch[key] = DateTime.UtcNow;   // отметка активности (и .ts, и .m3u8) → CleanupHls не удалит используемую папку

            // сегмент: отдаём с FileShare.ReadWrite (ffmpeg может ещё держать соседние файлы)
            if (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                int n = int.Parse(file.Substring(3, file.Length - 6));
                if (ModInit.conf.hlsSeek && !SegReady(dir, n))
                {
                    double sdur = _hlsDur.TryGetValue(key, out var dv) ? dv : LoadVodInfo(key, dir);
                    if (sdur <= 0 && !_hlsDur.ContainsKey(key))
                    {
                        // «холодный» ключ: сегмент запросили раньше плейлиста (напр. клиент
                        // прилетел сюда 302-редиректом с _m при выключенном профиле) — VOD-режим
                        // ещё не инициализирован. Определяем длительность здесь, иначе рестарт с
                        // -ss недоступен и любой сегмент отдал бы 404.
                        var (s0, _, _) = await ResolveHlsInputs(hash, index, audio);
                        if (s0 != null) sdur = HlsVodDuration(key, dir, s0);
                    }
                    if (sdur > 0)   // VOD-режим ключа (легаси-ключи: как раньше — мгновенный 404 ниже)
                    {
                        if (n * (double)HlsSegSec >= sdur) return NotFound();   // за концом файла
                        if (_hlsFailed.TryGetValue(key + ":" + n, out var fa) && DateTime.UtcNow - fa < _hlsFailTtl) return StatusCode(503);

                        _hlsRunning.TryGetValue(key, out var sess);
                        bool covered = sess != null && sess.startSeg >= 0 && sess.startSeg <= n && n <= SegLastCompleted(sess) + HlsAheadSegs;
                        if (!covered)   // дальний seek вперёд, назад на вычищенный сегмент или ffmpeg не запущен → рестарт с -ss
                        {
                            var (src, extAudio, audioMap) = await ResolveHlsInputs(hash, index, audio);
                            if (src == null) return NotFound();
                            CleanupHlsThrottled(60);
                            StartHls(key, dir, src, extAudio, audioMap, n, mobile);
                        }
                        for (int i = 0; i < 40 && !SegReady(dir, n); i++)   // short-poll до 10с вместо слепых ретраев hls.js
                        {
                            if (!_hlsRunning.ContainsKey(key)) break;   // ffmpeg вышел — дальше ждать нечего
                            await Task.Delay(250);
                            _hlsTouch[key] = DateTime.UtcNow;
                        }
                        if (!SegReady(dir, n)) return NotFound();
                    }
                }
                if (!System.IO.File.Exists(target)) return NotFound();
                var ts = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Внутри ключа сегмент иммутабелен (ключ = hash+index+дорожка+профиль, нарезка сетки
                // фиксирована HlsSegSec+copyts) → клиент/edge не ревалидируют его повторно. Ставим
                // только на успешном ответе: заголовок на 404 «не готового» сегмента залипнет навсегда.
                HttpContext.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
                return File(ts, "video/mp2t", enableRangeProcessing: true);
            }

            // playlist.m3u8 — VOD-режим: сервер сам генерит полный плейлист по длительности,
            // ffmpeg стартует только по запросам сегментов (перемотка не ждёт линейного транскода)
            if (ModInit.conf.hlsSeek)
            {
                double dur = _hlsDur.TryGetValue(key, out var dv) ? dv : LoadVodInfo(key, dir);
                if (dur <= 0 && !_hlsDur.ContainsKey(key))
                {
                    // режим ключа ещё не определён: резолвим источник и пробуем VOD
                    var (src, _, _) = await ResolveHlsInputs(hash, index, audio);
                    if (src == null) return NotFound();
                    dur = HlsVodDuration(key, dir, src);
                }
                if (dur > 0)
                {
                    // чистку отсюда убрали: обход /qdl-hls через 9p стоил ~2.7 с на КАЖДУЮ отдачу
                    // плейлиста (цена не зависит от размера ключа) — её тянет фоновый _hlsIdleTimer
                    HttpContext.Response.Headers["Cache-Control"] = "no-cache";   // VOD-плейлист генерится (подпись d1v, длительность)
                    return Content(SignHlsPlaylist(BuildVodPlaylist(dur), d1vKey), "application/vnd.apple.mpegurl");
                }
                // dur == 0 → легаси-фолбэк (короткий источник, start_time сдвинут или ffprobe не смог)
            }

            // легаси: линейный event-плейлист, который пишет сам ffmpeg (hlsSeek=false или странный источник)
            if (!System.IO.File.Exists(target))
            {
                // негатив-кэш: ffmpeg недавно упал на этом ключе → не спамим перезапуском
                if (_hlsFailed.TryGetValue(key, out var failedAt))
                {
                    if (DateTime.UtcNow - failedAt < _hlsFailTtl) return StatusCode(503);
                    _hlsFailed.TryRemove(key, out _);
                }

                var (src, extAudio, audioMap) = await ResolveHlsInputs(hash, index, audio);
                if (src == null) return NotFound();

                CleanupHlsThrottled(60);
                StartHls(key, dir, src, extAudio, audioMap, mobile: mobile);

                // ждём появления плейлиста + первого сегмента (event-playlist растёт по мере транскода)
                for (int i = 0; i < 60; i++)
                {
                    if (System.IO.File.Exists(target) && Directory.Exists(dir) && Directory.GetFiles(dir, "seg*.ts").Length >= 1) break;
                    if (!_hlsRunning.ContainsKey(key) && !System.IO.File.Exists(target)) break;   // ffmpeg вышел без результата → не ждём 30с
                    await Task.Delay(500);
                }
                if (!System.IO.File.Exists(target)) { _hlsFailed[key] = DateTime.UtcNow; return StatusCode(503); }
            }

            // ffmpeg продолжает ДОПИСЫВАТЬ playlist.m3u8 → читаем с FileShare.ReadWrite (иначе sharing violation → 500)
            string m3u8 = ReadShared(target);

            // event-плейлист растёт по мере нарезки → hls.js без подсказки стартует с «живого края»,
            // и фильм начинается НЕ с начала. EXT-X-START прибивает старт к нулю (явный seek не ломает).
            if (!m3u8.Contains("#EXT-X-START"))
                m3u8 = m3u8.Replace("#EXTM3U", "#EXTM3U\n#EXT-X-START:TIME-OFFSET=0,PRECISE=YES");

            HttpContext.Response.Headers["Cache-Control"] = "no-cache";   // легаси-плейлист ffmpeg ДОПИСЫВАЕТ по мере нарезки
            return Content(SignHlsPlaylist(m3u8, d1vKey), "application/vnd.apple.mpegurl");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] hls: " + ex);
            return StatusCode(503);
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/audio")]
    async public Task<ActionResult> Audio(string hash, int index = -1)
    {
        if (!ValidHash(hash)) return BadRequest();
        try
        {
            // локальный транскод: только встроенные дорожки (внешних озвучек у mp4-копий нет).
            // Оверлей идёт qBit-путём: ResolveFile сам подменит видео на mp4, а внешние
            // озвучки (d*) из живого торрента продолжают работать.
            var locA = LoadLocal(hash);
            if (locA != null && !LocalIsOverlay(locA))
            {
                var lopts = new JArray();
                var lfA = PickLocal(LocalFiles(locA), index);
                if (lfA != null)
                    foreach (var a in ProbeAudioCached(lfA.path)) lopts.Add(a);
                return ContentTo(lopts.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
            }

            using var c = await Qbit();
            string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
            var files = JArray.Parse(filesRaw);

            // найти видеофайл (по index или самый большой)
            JToken vf = null;
            if (index >= 0) foreach (var f in files) if ((f.Value<int?>("index") ?? -1) == index) { vf = f; break; }
            if (vf == null)
            {
                long max = -1;
                foreach (var f in files)
                {
                    string n = f.Value<string>("name") ?? "";
                    if (!Regex.IsMatch(n, "\\.(mkv|mp4|avi|ts|m4v|webm|mov)$", RegexOptions.IgnoreCase)) continue;
                    long s = f.Value<long?>("size") ?? 0; if (s > max) { max = s; vf = f; }
                }
            }
            if (vf == null) return ContentTo("[]", "application/json; charset=utf-8");

            string vname = (vf.Value<string>("name") ?? "").Replace('\\', '/');
            string vbase = Path.GetFileNameWithoutExtension(vname.Substring(vname.LastIndexOf('/') + 1));
            int vindex = vf.Value<int?>("index") ?? index;

            var opts = new JArray();

            // встроенные аудиодорожки (ffprobe видео; и резолв, и ffprobe — из кешей)
            string vpath = await ResolveFileCached(hash, vindex, c);
            foreach (var a in ProbeAudioCached(vpath)) opts.Add(a);

            // внешние озвучки — устойчивый матчер (студия + серия, много фолбэков; claude/06 §T).
            // Язык русский по построению: это отдельные файлы-дубляжи из русских раздач.
            bool langOn = ModInit.conf?.audioLangEnable != false;
            foreach (var d in DubsForVideo(files, vf))
            {
                var o = new JObject { ["id"] = d.id, ["label"] = d.label, ["lang"] = "rus" };
                if (langOn) { o["lang2"] = "ru"; o["langName"] = "Русский"; }
                opts.Add(o);
            }

            return ContentTo(opts.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] audio: " + ex); return ContentTo("[]", "application/json; charset=utf-8"); }
    }

    // ── Кеш ffprobe-дорожек по пути файла ──
    // ProbeAudio = запуск процесса ffprobe (до 15 с) на критическом пути КАЖДОГО старта плейбека.
    // Файл по данному пути иммутабелен, пока существует → длинный TTL + File.Exists-гард.
    // Храним строкой и парсим на выдаче: JToken одно-родительский, отдавать один и тот же
    // JObject в разные JArray-ответы нельзя. Пустой результат не кешируем (ffprobe мог
    // споткнуться о докачивающийся файл — следующий запрос перепроверит).
    static readonly ConcurrentDictionary<string, (string json, DateTime at)> _probeCache = new();
    static readonly TimeSpan ProbeTtl = TimeSpan.FromHours(12);

    static List<JObject> ProbeAudioCached(string path)
    {
        if (string.IsNullOrEmpty(path)) return new List<JObject>();
        if (_probeCache.TryGetValue(path, out var e) && DateTime.UtcNow - e.at < ProbeTtl && System.IO.File.Exists(path))
            return JArray.Parse(e.json).OfType<JObject>().ToList();

        var res = ProbeAudio(path);
        if (res.Count > 0)
            _probeCache[path] = (new JArray(res).ToString(Newtonsoft.Json.Formatting.None), DateTime.UtcNow);
        return res;
    }

    static List<JObject> ProbeAudio(string path)
    {
        var res = new List<JObject>();
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return res;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ModInit.conf.ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", "a", path })
                psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            var streams = JObject.Parse(outp)["streams"] as JArray ?? new JArray();
            int ord = 0;
            foreach (var s in streams)
            {
                var tags = s["tags"] as JObject;
                string lang = tags?.Value<string>("language") ?? "";
                string title = tags?.Value<string>("title");
                // mp4 (mov muxer) хранит название дорожки в теге "name", а не "title" —
                // иначе у транскодов §Y все озвучки становились безликим «Русский»
                if (string.IsNullOrWhiteSpace(title)) title = tags?.Value<string>("name");
                string label = !string.IsNullOrWhiteSpace(title) ? title : LangName(lang);
                var o = new JObject { ["id"] = "e" + ord, ["label"] = label + " (ориг.)", ["lang"] = lang };
                // lang2 — НОРМАЛИЗОВАННЫЙ код языка для клиента. Сырой lang оставляем как был:
                // это ffprobe-тег, на него могли завязаться другие потребители.
                if (ModInit.conf?.audioLangEnable != false)
                {
                    string code = LangCode(lang, title);
                    if (code != null) { o["lang2"] = code; o["langName"] = LangName(code); }
                }
                res.Add(o);
                ord++;
            }
        }
        catch { }
        return res;
    }

    static string LangName(string l)
    {
        switch ((l ?? "").ToLowerInvariant())
        {
            case "jpn": case "ja": return "Японский";
            case "eng": case "en": return "Английский";
            case "rus": case "ru": return "Русский";
            case "ukr": case "uk": return "Украинский";
            case "deu": case "ger": case "de": return "Немецкий";
            case "fra": case "fre": case "fr": return "Французский";
            case "spa": case "es": return "Испанский";
            case "ita": case "it": return "Итальянский";
            case "kor": case "ko": return "Корейский";
            case "zho": case "chi": case "zh": return "Китайский";
            case "": return "Оригинал";
            default: return l;
        }
    }

    // Русские студии/маркеры озвучки — для дорожек, у которых ffprobe-тег языка пуст
    // (у большинства рипов он именно такой).
    static readonly Regex _dubRuRx = new Regex(
        @"(?i)(дубляж|дублирован|многоголос|двухголос|одноголос|закадров|профессиональн|любительск|\bрус\w*|lostfilm|hdrezka|kubik|кубик|jaskier|newstudio|coldfilm|baibako|amedia|tvshows|кураж|гоблин)",
        RegexOptions.Compiled);
    static readonly Regex _dubEnRx = new Regex(@"(?i)(\benglish\b|\bангл\w*)", RegexOptions.Compiled);
    static readonly Regex _dubJaRx = new Regex(@"(?i)(\bjapanese\b|\bяпон\w*)", RegexOptions.Compiled);

    /// <summary>
    /// Нормализованный код языка дорожки: сначала ffprobe-тег, и ТОЛЬКО при пустом теге —
    /// эвристика по подписи. Возвращает null, когда язык определить нельзя (клиент тогда
    /// считает дорожку «без языка» и не прячет её).
    /// </summary>
    internal static string LangCode(string raw, string label)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "rus": case "ru": return "ru";
            case "eng": case "en": return "en";
            case "jpn": case "ja": case "jp": return "ja";
            case "ukr": case "uk": return "uk";
            case "deu": case "ger": case "de": return "de";
            case "fra": case "fre": case "fr": return "fr";
            case "spa": case "es": return "es";
            case "ita": case "it": return "it";
            case "kor": case "ko": return "ko";
            case "zho": case "chi": case "zh": return "zh";
            case "":
            case "und":
                break;                 // тега нет — пробуем подпись
            default:
                return null;           // экзотика: не выдумываем
        }

        // ⚠️ Только осмысленная подпись. При пустом теге label формируется как LangName("") =
        // «Оригинал», и классифицировать эту строку нельзя — она ничего о языке не говорит.
        if (string.IsNullOrWhiteSpace(label)) return null;
        if (_dubRuRx.IsMatch(label)) return "ru";
        if (_dubEnRx.IsMatch(label)) return "en";
        if (_dubJaRx.IsMatch(label)) return "ja";
        return null;
    }

    // ───────── Устойчивый матчер озвучек (видео↔внешние аудио). См. claude/06 §T ─────────
    sealed class Ep { public string kind; public int season = -1; public int ep = -1; public int ep2 = -1; public bool any => kind != null || ep >= 0; }

    static readonly Regex[] _noiseRx =
    {
        new Regex(@"(?i)\b(?:19|20)\d{2}\b"),
        new Regex(@"(?i)\b\d{3,4}[pi]\b"),
        new Regex(@"(?i)\b(?:2160|1080|720|480|576|360)\b"),
        new Regex(@"(?i)\b(?:x?264|x?265|h\.?26[45]|hevc|avc|av1|vp9|xvid|divx)\b"),
        new Regex(@"(?i)\b(?:10|8)\s?bit\b"),
        new Regex(@"(?i)\b(?:aac|ac3|eac3|dts(?:-hd)?|flac|opus|truehd|mp3)\b"),
        new Regex(@"(?i)\b\d+(?:\.\d+)?\s?(?:fps|kbps|mbps|hz|khz)\b"),
        new Regex(@"(?i)\b(?:bdrip|bluray|webdl|web-?dl|webrip|hdtv|dvdrip|remux|uhd)\b"),
        new Regex(@"(?i)\b[257]\.[01]\b"),
        new Regex(@"(?i)\b\d{3,4}x\d{3,4}\b"),
    };
    static string StripNoise(string s) { foreach (var r in _noiseRx) s = r.Replace(s, " "); return s; }

    static Ep ParseEp(string baseName)
    {
        string s = baseName ?? "";
        Match m;
        if ((m = Regex.Match(s, @"(?i)\b(OVA|ONA|OAD)\s*0*(\d{1,2})?\b")).Success) return new Ep { kind = m.Groups[1].Value.ToUpperInvariant(), ep = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : -1 };
        if ((m = Regex.Match(s, @"(?i)\b(?:SP|Special|Спецвыпуск|Спешл)\s*0*(\d{1,2})?\b")).Success) return new Ep { kind = "SP", ep = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : -1 };
        if ((m = Regex.Match(s, @"(?i)\b(NCOP|NCED|Creditless\s*OP|Creditless\s*ED|Clean\s*Opening|Clean\s*Ending)\b")).Success) { string k = m.Value.ToUpperInvariant(); return new Ep { kind = (k.Contains("ED") || k.Contains("ENDING")) ? "NCED" : "NCOP" }; }
        if ((m = Regex.Match(s, @"(?i)(?<![A-Za-z])(OP|ED|PV|CM|Menu|Trailer|Preview|Teaser)\s*0*(\d{1,2})?(?![A-Za-z])")).Success) return new Ep { kind = m.Groups[1].Value.ToUpperInvariant() };
        string c = StripNoise(s);
        if ((m = Regex.Match(c, @"(?i)(?:S\d{1,2})?E0*(\d{1,3})\s*-\s*E?0*(\d{1,3})")).Success) return new Ep { kind = "RANGE", ep = int.Parse(m.Groups[1].Value), ep2 = int.Parse(m.Groups[2].Value) };
        if ((m = Regex.Match(c, @"(?:^|[\s._\[-])0*(\d{1,3})\s*-\s*0*(\d{1,3})(?=[\s._\]-]|$)")).Success) return new Ep { kind = "RANGE", ep = int.Parse(m.Groups[1].Value), ep2 = int.Parse(m.Groups[2].Value) };
        // Разделитель между S## и E## обязателен к поддержке: «Silo.S02.E07», «Silo S03 E05»,
        // «Show.S01_E03» — раздачи так называют файлы сплошь и рядом. Без него сезон не читался,
        // срабатывало более позднее правило «E07» (season=-1), и сезонный гейт охоты (fail-open)
        // штамповал файлу сезон основной раздачи — 4 серии 2-го сезона «Укрытия» попали в 3-й.
        if ((m = Regex.Match(c, @"(?i)(?<![A-Za-z0-9])S(\d{1,2})[\s._\-\[\]()]{0,3}E[Pp]?[\s._-]?0*(\d{1,3})(?!\d)")).Success) return new Ep { season = int.Parse(m.Groups[1].Value), ep = int.Parse(m.Groups[2].Value) };
        if ((m = Regex.Match(c, @"(?i)(?<![A-Za-z0-9])(\d{1,2})x0*(\d{1,3})(?!\d)")).Success) return new Ep { season = int.Parse(m.Groups[1].Value), ep = int.Parse(m.Groups[2].Value) };
        if ((m = Regex.Match(c, @"(?i)(?<![A-Za-z0-9])E[Pp]?\.?\s*0*(\d{1,3})(?!\d)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"(?i)(?:серия|episode|эпизод|вып(?:уск)?)\s*[№#]?\s*0*(\d{1,3})(?!\d)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"#0*(\d{1,3})(?!\d)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"(?:^|\s)-\s+0*(\d{1,3})(?=\s|$|\[|\()")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"\[\s*0*(\d{1,3})\s*\]")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"(?:^|[._ ])0*(\d{1,3})(?=[._ \[]|$)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c.Trim(), @"^0*(\d{1,3})$")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        return new Ep();
    }

    static bool EpEqual(Ep v, Ep a)
    {
        if (v == null || a == null || !v.any || !a.any) return false;
        if (v.kind != a.kind) return false;
        if (v.kind == "RANGE") return v.ep == a.ep && v.ep2 == a.ep2;
        if (v.ep != a.ep) return false;
        if (v.season >= 0 && a.season >= 0 && v.season != a.season) return false;
        return v.kind != null || v.ep >= 0;
    }

    static readonly Regex _genericFolderRx = new Regex(@"(?i)^(rus[ ._-]?sound[s]?|sound[s]?|audio|звук|озвучк\w*|voice|dub|дубляж|переводы?|дорожк\w*|tracks?|rus|русск\w*)$");
    static bool IsGenericFolder(string name) => string.IsNullOrWhiteSpace(name) || _genericFolderRx.IsMatch(name.Trim());

    static string CleanStudio(string s)
    {
        s = Regex.Replace(s ?? "", @"[._]+", " ");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '_', '.', '[', ']', '(', ')');
        return string.IsNullOrWhiteSpace(s) ? "Озвучка" : s;
    }

    static string StudioId(string studio)
    {
        string norm = Regex.Replace((studio ?? "").ToLowerInvariant(), @"[\s._\-]+", "");
        uint h = 2166136261;
        foreach (char ch in norm) { h ^= ch; h *= 16777619; }
        return "d" + h.ToString("x8");
    }

    // студия озвучки: суффикс после имени видео → НЕ-generic подпапка → имя без хвостового номера → [скобки]
    static string StudioOf(string fullPath, string videoBase)
    {
        string p = (fullPath ?? "").Replace('\\', '/');
        string fbase = Path.GetFileNameWithoutExtension(p.Substring(p.LastIndexOf('/') + 1));

        if (fbase.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase) && fbase.Length > videoBase.Length)
        {
            string suf = fbase.Substring(videoBase.Length).Trim('.', ' ', '-', '_', '[', ']', '(', ')');
            if (!string.IsNullOrWhiteSpace(suf)) return CleanStudio(suf);
        }
        var parts = p.Split('/');
        for (int i = parts.Length - 2; i >= 1; i--)
            if (!IsGenericFolder(parts[i])) return CleanStudio(parts[i]);

        // остаток после общего префикса с видео (после вырезания тех-шума) — устойчиво к разным тегам качества
        string na = Regex.Replace(Regex.Replace(StripNoise(fbase), @"\[\s*\]|\(\s*\)", " "), @"\s{2,}", " ").Trim();
        string nv = Regex.Replace(Regex.Replace(StripNoise(videoBase), @"\[\s*\]|\(\s*\)", " "), @"\s{2,}", " ").Trim();
        int kk = 0; while (kk < na.Length && kk < nv.Length && char.ToLowerInvariant(na[kk]) == char.ToLowerInvariant(nv[kk])) kk++;
        string rem = Regex.Replace(na.Substring(kk), @"(?i)(S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|EP?\.?\d{1,3}|OVA\s*\d*|SP\s*\d*|NCOP|NCED|\d{1,3})", " ");
        rem = Regex.Replace(rem, @"\s{2,}", " ").Trim(' ', '-', '_', '.', '[', ']', '(', ')');
        if (!string.IsNullOrWhiteSpace(rem) && !Regex.IsMatch(rem, @"^\d+$")) return CleanStudio(rem);

        var b = Regex.Match(fbase, @"\[([^\]]+)\]");
        if (b.Success && !IsGenericFolder(b.Groups[1].Value)) return CleanStudio(b.Groups[1].Value);
        return "Озвучка";
    }

    static bool NormStarts(string a, string b)
    {
        a = (a ?? "").Replace('_', ' ').Replace('.', ' ').Trim();
        b = (b ?? "").Replace('_', ' ').Replace('.', ' ').Trim();
        return b.Length > 0 && a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    static readonly Regex _audioExtRx = new Regex(@"(?i)\.(mka|aac|ac3|eac3|dts|flac|opus|m4a|wav|mp3|thd)$");
    static readonly Regex _videoExtRx = new Regex(@"(?i)\.(mkv|mp4|avi|ts|m2ts|webm|mov|m4v)$");

    static string BaseNoExt(JToken f) { string n = (f.Value<string>("name") ?? "").Replace('\\', '/'); return Path.GetFileNameWithoutExtension(n.Substring(n.LastIndexOf('/') + 1)); }

    static int NaturalCompare(string a, string b)
    {
        a = a ?? ""; b = b ?? ""; int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a.Substring(si, i - si).TrimStart('0'); string nb = b.Substring(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int cmp = string.CompareOrdinal(na, nb); if (cmp != 0) return cmp;
            }
            else { int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j])); if (cmp != 0) return cmp; i++; j++; }
        }
        return (a.Length - i) - (b.Length - j);
    }

    // для видеофайла → список озвучек (studioId, label, индекс аудиофайла)
    static List<(string id, string label, int idx)> DubsForVideo(JArray files, JToken video)
    {
        var res = new List<(string, string, int)>();
        if (video == null) return res;
        string vbase = BaseNoExt(video);
        var vEp = ParseEp(vbase);

        var videos = new List<JToken>(); var audios = new List<JToken>();
        foreach (var f in files)
        {
            string n = f.Value<string>("name") ?? "";
            if (_videoExtRx.IsMatch(n)) videos.Add(f);
            else if (_audioExtRx.IsMatch(n)) audios.Add(f);
        }
        bool isMovie = videos.Count == 1;

        var byStudio = new Dictionary<string, List<JToken>>();
        var labelOf = new Dictionary<string, string>();
        foreach (var a in audios)
        {
            string studio = StudioOf(a.Value<string>("name") ?? "", vbase);
            string id = StudioId(studio);
            if (!byStudio.TryGetValue(id, out var lst)) { lst = new List<JToken>(); byStudio[id] = lst; labelOf[id] = studio; }
            lst.Add(a);
        }

        videos.Sort((x, y) => NaturalCompare(x.Value<string>("name"), y.Value<string>("name")));
        int vPos = videos.FindIndex(x => (x.Value<int?>("index") ?? -2) == (video.Value<int?>("index") ?? -1));

        foreach (var kv in byStudio)
        {
            var lst = kv.Value;
            JToken best = null; int bestRank = 0;
            foreach (var a in lst)
            {
                var aEp = ParseEp(BaseNoExt(a));
                int rank = 0;
                if (vEp.any && aEp.any && EpEqual(vEp, aEp)) rank = 6;          // A: точная серия
                else if (NormStarts(BaseNoExt(a), vbase)) rank = 5;             // B: префикс имени
                else if (isMovie && !vEp.any) rank = 3;                         // D: фильм — любая дорожка
                else if (!aEp.any && lst.Count == 1) rank = 2;                  // E: season-pack (1 файл студии без серии)
                if (rank > bestRank) { best = a; bestRank = rank; }
            }
            if (best == null && lst.Count == videos.Count && vPos >= 0)         // F: позиционный (равные счётчики, без серий)
            {
                bool noeps = true; foreach (var a in lst) if (ParseEp(BaseNoExt(a)).any) { noeps = false; break; }
                if (noeps) { lst.Sort((x, y) => NaturalCompare(x.Value<string>("name"), y.Value<string>("name"))); if (vPos < lst.Count) best = lst[vPos]; }
            }
            if (best != null) res.Add((kv.Key, labelOf[kv.Key], best.Value<int?>("index") ?? -1));
        }
        return res;
    }

    static JToken FindVideo(JArray files, int index)
    {
        if (index >= 0) foreach (var f in files) if ((f.Value<int?>("index") ?? -1) == index) return f;
        JToken vf = null; long max = -1;
        foreach (var f in files) { if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue; long s = f.Value<long?>("size") ?? 0; if (s > max) { max = s; vf = f; } }
        return vf;
    }

    // найти файл-озвучку выбранной студии именно для серии videoIndex
    static async Task<string> ResolveDubFile(HttpClient c, string hash, int videoIndex, string dubId)
    {
        string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
        var files = JArray.Parse(filesRaw);
        var video = FindVideo(files, videoIndex);
        if (video == null) return null;
        foreach (var d in DubsForVideo(files, video))
            if (d.id == dubId) return await ResolveFile(c, hash, d.idx);
        return null;
    }

    // Браузер декодирует только h264/vp9/av1. hevc сознательно копируем как раньше — для него
    // путь §Y (оффлайн-транскод в MP4: живой x264 на 4K HEVC может не тянуть). Остальное
    // (mpeg4/XviD, mpeg2video, vc1, wmv3, msmpeg4 — старые SD-рипы .avi) при copy даёт чёрный
    // экран со звуком → кодируем в H.264 на лету (SD для CPU — копейки).
    static readonly HashSet<string> _hlsCopyCodecs = new(StringComparer.OrdinalIgnoreCase) { "h264", "vp9", "av1", "hevc" };
    static bool HlsCopyVideo(string codec) => string.IsNullOrWhiteSpace(codec) || _hlsCopyCodecs.Contains(codec.Trim());

    static string ProbeVideoCodec(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ModInit.conf.ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "quiet", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=noprint_wrappers=1:nokey=1", path })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return o.Trim();
        }
        catch { }
        return "";   // не смогли пробить → copy (прежнее поведение)
    }

    // Резолв входов ffmpeg: видеофайл + внешняя озвучка + маппинг аудио (общий для плейлиста и seek-рестарта)
    static async Task<(string src, string extAudio, string audioMap)> ResolveHlsInputs(string hash, int index, string audio)
    {
        string extAudio = null, audioMap = "0:a:0?";
        string src = await ResolveFileCached(hash, index);   // обычный HLS-рестарт/seek — без обращений к qBit
        if (src == null) return (null, null, null);

        if (audio.StartsWith("e")) audioMap = "0:a:" + audio.Substring(1);       // встроенная дорожка N
        else if (audio.StartsWith("d"))                                            // внешняя озвучка по СТУДИИ — файл для ЭТОЙ серии
        {
            using var c = await Qbit();
            extAudio = await ResolveDubFile(c, hash, index, audio);
            if (!string.IsNullOrEmpty(extAudio)) audioMap = "1:a:0";
        }
        else if (audio.StartsWith("f"))                                            // back-compat: внешний файл по индексу
        {
            extAudio = await ResolveFileCached(hash, int.Parse(audio.Substring(1)));
            if (!string.IsNullOrEmpty(extAudio)) audioMap = "1:a:0";
        }
        return (src, extAudio, audioMap);
    }

    // файл может дописываться ffmpeg-ом → только FileShare.ReadWrite (иначе sharing violation)
    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    // сегмент финализирован муксером: есть следующий по номеру ЛИБО он вписан в плейлист какого-нибудь запуска.
    // Обрубок от убитого процесса не проходит проверку и будет перезаписан новым запуском (-y).
    static bool SegReady(string dir, int n)
    {
        try
        {
            if (!System.IO.File.Exists(Path.Combine(dir, "seg" + n.ToString("D5") + ".ts"))) return false;
            if (System.IO.File.Exists(Path.Combine(dir, "seg" + (n + 1).ToString("D5") + ".ts"))) return true;
            foreach (var m in Directory.GetFiles(dir, "*.m3u8"))
                if (ReadShared(m).Contains("seg" + n.ToString("D5") + ".ts")) return true;
        }
        catch { }
        return false;
    }

    // последний финализированный сегмент запуска (startSeg + число EXTINF - 1); пока пусто — startSeg - 1
    static int SegLastCompleted(HlsSession s)
    {
        try
        {
            if (s.ffPlaylist != null && System.IO.File.Exists(s.ffPlaylist))
            {
                int cnt = Regex.Matches(ReadShared(s.ffPlaylist), "#EXTINF").Count;
                if (cnt > 0) return Math.Max(0, s.startSeg) + cnt - 1;
            }
        }
        catch { }
        return Math.Max(0, s.startSeg) - 1;
    }

    // виртуальный VOD-плейлист на всю длительность: hls.js сразу знает таймлайн и запрашивает segNNNNN напрямую.
    // EXTINF ровно по 6с — фактические сегменты в copy-режиме режутся по keyframe, но -copyts даёт плееру
    // истинные PTS, и он сам выравнивает таймлайн. EXT-X-START не нужен: VOD стартует с нуля по умолчанию.
    static string BuildVodPlaylist(double duration)
    {
        int n = (int)Math.Ceiling(duration / HlsSegSec);
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-TARGETDURATION:" + (HlsSegSec + 1) + "\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        for (int i = 0; i < n; i++)
        {
            double len = i == n - 1 ? duration - (double)HlsSegSec * i : HlsSegSec;
            sb.Append("#EXTINF:" + len.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + ",\n");
            sb.Append("seg" + i.ToString("D5") + ".ts\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    // Дописать ключ периметра к сегментным строкам плейлиста: VLC (и любой плеер) резолвит
    // относительные URI без query базового URL → извне сегменты получили бы 404 от периметра.
    // Ключа нет (LAN) → плейлист не меняется.
    static string SignHlsPlaylist(string m3u8, string d1v)
    {
        if (string.IsNullOrEmpty(d1v) || string.IsNullOrEmpty(m3u8)) return m3u8;
        return Regex.Replace(m3u8, "^(seg\\d{1,6}\\.ts)$", "$1?d1v=" + Uri.EscapeDataString(d1v), RegexOptions.Multiline);
    }

    static (double duration, double start) ProbeFormat(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ModInit.conf.ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "quiet", "-show_entries", "format=duration,start_time", "-of", "default=noprint_wrappers=1", path })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            double dur = 0, start = 0;
            foreach (var line in o.Split('\n'))
            {
                var kv = line.Trim().Split('=');
                if (kv.Length != 2) continue;
                if (kv[0] == "duration") double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dur);
                else if (kv[0] == "start_time") double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out start);
            }
            return (dur, start);
        }
        catch { }
        return (0, 0);
    }

    // длительность источника для VOD-режима; 0 = фолбэк в легаси (короткий файл, сдвинутый start_time — риск
    // для copyts-сетки, — или ffprobe не смог). Решение кэшируется на key; info.json переживает рестарт контейнера.
    static double HlsVodDuration(string key, string dir, string src)
    {
        return _hlsDur.GetOrAdd(key, _ =>
        {
            var (dur, start) = ProbeFormat(src);
            if (dur <= 10 || start >= 1.0) return 0;
            WipeLegacyHlsDir(key, dir);
            try
            {
                Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(Path.Combine(dir, "info.json"),
                    "{\"duration\":" + dur.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
            catch { }
            return dur;
        });
    }

    // восстановление VOD-режима из info.json (после рестарта контейнера _hlsDur пуст, а плеер уже держит плейлист)
    static double LoadVodInfo(string key, string dir)
    {
        try
        {
            string f = Path.Combine(dir, "info.json");
            if (System.IO.File.Exists(f))
            {
                double d = JObject.Parse(System.IO.File.ReadAllText(f)).Value<double?>("duration") ?? 0;
                if (d > 0) { _hlsDur[key] = d; return d; }
            }
        }
        catch { }
        return 0;
    }

    // папки старого линейного режима нарезаны БЕЗ -copyts (muxdelay ~1.4с) — их сегменты несовместимы
    // с новой сеткой, смешивать нельзя. Маркер нового формата — info.json.
    static void WipeLegacyHlsDir(string key, string dir)
    {
        try
        {
            if (Directory.Exists(dir) && System.IO.File.Exists(Path.Combine(dir, "playlist.m3u8"))
                && !System.IO.File.Exists(Path.Combine(dir, "info.json")) && !_hlsRunning.ContainsKey(key))
                Directory.Delete(dir, true);
        }
        catch { }
    }

    // Параметры «мобильного» профиля (_m). Отдельный класс, чтобы HlsArgs осталась чистой
    // функцией (тесты собирают опции сами, без ModInit.conf); боевые значения — BuildMobileOpts.
    public sealed class HlsMobileOpts
    {
        public int height = 720;       // даунскейл до высоты (SD не апскейлится)
        public int cq = 28;            // NVENC -cq (обычный HLS-реэнкод — 23)
        public int crf = 25;           // CPU-фолбэк libx264 -crf (обычный — 21)
        public int maxrateKbps = 2500; // кап для сотового канала; -bufsize = 2×maxrate
        public int audioKbps = 128;    // AAC (обычный HLS — 256k)
        public bool hdr;               // HDR10/HLG-источник → tone-mapping в bt709
    }

    static readonly ConcurrentDictionary<string, bool> _hlsHdrByPath = new();   // кэш HDR-детекта по пути (ffprobe недёшев)

    static HlsMobileOpts BuildMobileOpts(string videoPath) => new HlsMobileOpts
    {
        height = ModInit.conf.hlsMobileHeight,
        cq = ModInit.conf.hlsMobileCq,
        crf = ModInit.conf.hlsMobileCrf,
        maxrateKbps = ModInit.conf.hlsMobileMaxrateKbps,
        audioKbps = ModInit.conf.hlsMobileAudioKbps,
        hdr = ProbeHdrCached(videoPath)
    };

    // HDR-вердикт кэшируется ТОЛЬКО при непустой пробе: пустой color_transfer (недокачанный/
    // занятый файл, упавший ffprobe) иначе навсегда пометил бы HDR-фильм как SDR —
    // блёклая картинка после докачки без единого признака поломки. Пустая проба → SDR
    // на ЭТОТ запуск, перепроба при следующем StartHls.
    static bool ProbeHdrCached(string path)
    {
        if (_hlsHdrByPath.TryGetValue(path, out bool cached)) return cached;
        string t = ProbeVideoColorTransfer(path);
        bool hdr = IsHdrTransfer(t);
        if (!string.IsNullOrWhiteSpace(t)) _hlsHdrByPath[path] = hdr;
        return hdr;
    }

    // HDR-передача (PQ/HLG): даунскейл без тонмапа дал бы блёклую картинку (§AH — бэклог закрыт для _m)
    static bool IsHdrTransfer(string t) => !string.IsNullOrWhiteSpace(t) &&
        (t.Trim().Equals("smpte2084", StringComparison.OrdinalIgnoreCase) || t.Trim().Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase));

    static string ProbeVideoColorTransfer(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ModInit.conf.ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "quiet", "-select_streams", "v:0", "-show_entries", "stream=color_transfer", "-of", "default=noprint_wrappers=1:nokey=1", path })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return o.Trim();
        }
        catch { }
        return "";   // не смогли пробить → считаем SDR (простой scale, как раньше)
    }

    // Сборка аргументов ffmpeg для HLS — чистая функция (тестируется в HlsCodecTests).
    // startSeg = -1 → легаси: линейный транскод с начала (аргументы байт-в-байт как раньше).
    // startSeg >= 0 → seek-запуск: -ss перед КАЖДЫМ входом (input seeking), -start_number, вывод в ff{N}.m3u8;
    // -copyts сохраняет истинные PTS источника → сегменты разных запусков в одной папке взаимно согласованы.
    // mobile != null → профиль _m: всегда реэнкод (copyVideo=false) с даунскейлом и капом битрейта.
    static List<string> HlsArgs(string dir, string videoPath, string extAudio, string audioMap, bool copyVideo, int startSeg = -1, bool nvenc = false, HlsMobileOpts mobile = null)
    {
        string ss = startSeg > 0 ? (startSeg * (long)HlsSegSec).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        var args = new List<string> { "-y" };
        if (nvenc && !copyVideo)   // NVDEC-декод (на хостовом воркере); неподдерживаемые кодеки ffmpeg сам тихо декодит на CPU
            args.AddRange(new[] { "-hwaccel", "cuda" });
        if (ss != null) args.AddRange(new[] { "-ss", ss });
        args.AddRange(new[] { "-i", videoPath });
        if (!string.IsNullOrEmpty(extAudio))   // внешняя озвучка — вторым входом
        {
            if (ss != null) args.AddRange(new[] { "-ss", ss });
            args.AddRange(new[] { "-i", extAudio });
        }
        args.AddRange(new[] { "-map", "0:v:0?", "-map", string.IsNullOrEmpty(audioMap) ? "0:a:0?" : audioMap });
        if (copyVideo)
            args.AddRange(new[] { "-c:v", "copy" });   // AVC/VP9/AV1 браузер играет; hevc — см. §Y
        else
        {
            if (mobile != null)
            {
                // Профиль _m: даунскейл (SD не апскейлим) + кап битрейта под сотовый канал.
                // HDR10/HLG → SDR bt709 через zscale+tonemap (CPU-фильтр: после -hwaccel cuda без
                // -hwaccel_output_format кадры и так в системной памяти, hwdownload не нужен).
                // HDR: setparams-префикс форсит bt2020 primaries/matrix — у веб-рипов часто проставлен
                // только transfer (smpte2084), а без primaries/matrix zscale не строит путь колор-
                // спейсов и падает (exit -22 → 503 навсегда). Реальный HDR10/HLG всегда bt2020,
                // так что для корректно затегированных значения просто совпадают. Проверено на бинаре.
                args.AddRange(new[] { "-vf", mobile.hdr
                    ? "setparams=colorspace=bt2020nc:color_primaries=bt2020,zscale=w=-2:h=" + mobile.height + ":t=linear:npl=100,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:p=bt709:r=tv,format=yuv420p"
                    : "scale=-2:min(" + mobile.height + "\\,ih)" });
                string maxrate = mobile.maxrateKbps + "k", bufsize = (mobile.maxrateKbps * 2) + "k";
                if (nvenc)   // -maxrate при -rc vbr -cq — жёсткий VBV-потолок; -level не форсим (§AH)
                    args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", mobile.cq.ToString(), "-b:v", "0", "-maxrate", maxrate, "-bufsize", bufsize, "-forced-idr", "1", "-profile:v", "high", "-pix_fmt", "yuv420p" });
                else
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", mobile.crf.ToString(), "-maxrate", maxrate, "-bufsize", bufsize, "-pix_fmt", "yuv420p" });
            }
            // XviD/MPEG-2/VC-1 → H.264 на лету: NVENC на хостовом воркере, иначе x264 на CPU.
            // ⚠️ -forced-idr 1 ОБЯЗАТЕЛЕН: без него force_key_frames даёт не-IDR I-кадры, муксер
            // по ним НЕ режет → сегменты по 250 кадров (~10.4с) вместо 6с — сетка VOD разъезжается.
            else if (nvenc)
                args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-forced-idr", "1", "-profile:v", "high", "-pix_fmt", "yuv420p" });
            else
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p" });
            if (startSeg >= 0)   // при реэнкоде прибиваем keyframe к сетке сегментов
                // ⚠️ t в force_key_frames — ОТНОСИТЕЛЬНОЕ время энкода (от первого кадра этого
                // процесса), НЕ абсолютный PTS источника; -copyts на него НЕ влияет (он про муксер).
                // Seek-запуск — свежий ffmpeg с -ss, его t=0 = сегмент startSeg → сетка n_forced*6
                // ложится ровно на startSeg*6, startSeg*6+6, … Прежний offset (startSeg*6+…) не
                // срабатывал никогда (t не доживал до absolute-значения) → сегменты по GOP 250
                // (~10.4с) мимо VOD-сетки на ЛЮБОЙ перемотке. Проверено на бинаре: с offset 10.4с,
                // без offset 6.006с (23.976/25/29.97/59.94).
                args.AddRange(new[] { "-force_key_frames", "expr:gte(t,n_forced*" + HlsSegSec + ")" });
        }
        args.AddRange(new[] { "-c:a", "aac", "-ac", "2", "-b:a", mobile != null ? mobile.audioKbps + "k" : "256k" });   // звук → AAC stereo
        if (startSeg >= 0)
            args.AddRange(new[] { "-copyts", "-muxdelay", "0", "-avoid_negative_ts", "disabled" });
        args.AddRange(new[]
        {
            "-f", "hls", "-hls_time", HlsSegSec.ToString(), "-hls_playlist_type", "event",
            "-hls_flags", "independent_segments"
        });
        if (startSeg >= 0) args.AddRange(new[] { "-start_number", startSeg.ToString() });
        args.AddRange(new[]
        {
            "-hls_segment_filename", Path.Combine(dir, "seg%05d.ts"),
            Path.Combine(dir, startSeg >= 0 ? "ff" + startSeg + ".m3u8" : "playlist.m3u8")
        });
        return args;
    }

    static void StartHls(string key, string dir, string videoPath, string extAudio, string audioMap, int startSeg = -1, bool mobile = false)
    {
        lock (_hlsLock.GetOrAdd(key, _ => new object()))
        {
            if (_hlsRunning.TryGetValue(key, out var old))
            {
                // легаси уже генерится; либо конкурентный запрос уже перезапустил в позицию, покрывающую нужный сегмент
                if (startSeg < 0 || (old.startSeg >= 0 && old.startSeg <= startSeg && startSeg <= SegLastCompleted(old) + HlsAheadSegs))
                    return;
                old.killed = true;
                try { old.job?.Kill(); } catch { }
                _hlsRunning.TryRemove(key, out _);
            }

            var sess = new HlsSession
            {
                startSeg = startSeg,
                ffPlaylist = Path.Combine(dir, startSeg >= 0 ? "ff" + startSeg + ".m3u8" : "playlist.m3u8")
            };
            try
            {
                Directory.CreateDirectory(dir);
                // mobile: всегда реэнкод; общий кэш решения copy/x264 (_hlsCopyByPath, по пути) не трогаем — не отравить обычный профиль
                bool copyVideo = !mobile && _hlsCopyByPath.GetOrAdd(videoPath, p => HlsCopyVideo(ProbeVideoCodec(p)));
                HlsMobileOpts mopts = mobile ? BuildMobileOpts(videoPath) : null;

                // видео copy — дёшево, остаётся в контейнере; реэнкод (XviD/MPEG-2/VC-1, профиль _m) — на GPU-воркер, если жив
                var job = FfJob.Start(nv => HlsArgs(dir, videoPath, extAudio, audioMap, copyVideo, startSeg, nvenc: nv, mobile: mopts), "hls", preferRemote: !copyVideo);
                sess.job = job;
                _hlsRunning[key] = sess;
                _ = Task.Run(async () =>
                {
                    string err = "";
                    try
                    {
                        await job.WaitForExitAsync();
                        err = job.StderrTail;
                    }
                    catch { }
                    lock (_hlsLock.GetOrAdd(key, _ => new object()))
                    {
                        // убираем только СВОЮ сессию (рестарт мог уже положить новую)
                        if (_hlsRunning.TryGetValue(key, out var cur) && ReferenceEquals(cur, sess))
                            _hlsRunning.TryRemove(key, out _);
                    }
                    try
                    {
                        string fkey = sess.startSeg >= 0 ? key + ":" + sess.startSeg : key;   // фейл seek-запуска не должен блокировать весь ключ
                        bool ok = job.HasExited && job.ExitCode == 0 && System.IO.File.Exists(sess.ffPlaylist);
                        if (ok) _hlsFailed.TryRemove(fkey, out _);
                        else if (!sess.killed)   // прибитый рестартом процесс — не фейл
                        {
                            _hlsFailed[fkey] = DateTime.UtcNow;
                            Console.WriteLine("[QbitDownload] hls ffmpeg failed key=" + key + " startSeg=" + sess.startSeg + " exit=" + (job.HasExited ? job.ExitCode.ToString() : "?") + ": " + (err ?? "").Trim());
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[QbitDownload] hls start: " + ex);
                if (_hlsRunning.TryGetValue(key, out var cur) && ReferenceEquals(cur, sess))
                    _hlsRunning.TryRemove(key, out _);
            }
        }
    }

    // сбросить HLS-кэш всех ключей раздачи (hash_index[_audio]) — источник заменён/удалён,
    // иначе сервер продолжит отдавать сегменты, нарезанные из СТАРОГО файла (напр. HEVC до транскода)
    static void DropHlsCache(string hash)
    {
        try
        {
            string root = ModInit.conf.hlsPath;
            if (!Directory.Exists(root) || string.IsNullOrEmpty(hash)) return;
            foreach (var d in Directory.GetDirectories(root, hash + "_*"))
            {
                string key = Path.GetFileName(d);
                if (_hlsRunning.ContainsKey(key)) continue;   // активный ffmpeg не трогаем
                try { Directory.Delete(d, true); } catch { }
                _hlsTouch.TryRemove(key, out _);
                _hlsFailed.TryRemove(key, out _);
                _hlsDur.TryRemove(key, out _);   // источник заменён → длительность/режим переопределятся заново
            }
        }
        catch { }
    }

    // Троттлинг чистки. Обход /qdl-hls — это stat каждого файла через 9p-маунт: 4400 файлов = ~2.7 с,
    // и цена НЕ зависит от того, большой ключ или маленький. Поэтому чистка не должна висеть в горячем
    // пути (плейлист вообще не зовёт её, seek-рестарт — не чаще раза в минуту, фоновый таймер — в 5 минут).
    static long _hlsCleanupAt;    // Ticks последнего запуска (0 = не запускалась)
    static int _hlsCleanupBusy;   // 0/1 — обход уже идёт (два потока не топчутся по каталогу)

    static void CleanupHlsThrottled(int minIntervalSec)
    {
        long now = DateTime.UtcNow.Ticks;
        long prev = Interlocked.Read(ref _hlsCleanupAt);
        if (prev != 0 && now - prev < TimeSpan.TicksPerSecond * (long)minIntervalSec) return;
        if (Interlocked.CompareExchange(ref _hlsCleanupAt, now, prev) != prev) return;   // слот забрал другой поток
        CleanupHls();
    }

    // не даём HLS-кэшу (дублирует видео) разрастаться: при превышении капа чистим старые папки
    static void CleanupHls()
    {
        if (Interlocked.CompareExchange(ref _hlsCleanupBusy, 1, 0) != 0) return;
        try
        {
            string root = ModInit.conf.hlsPath;
            if (!Directory.Exists(root)) return;
            long cap = Math.Max(1, ModInit.conf.hlsCacheCapGb) * 1024L * 1024 * 1024;

            // один обход каталога (он и есть вся цена), а вот список с сортировкой строим ТОЛЬКО при
            // превышении капа — обычный случай выходит сразу после подсчёта
            var dirs = new DirectoryInfo(root).GetDirectories();
            var sizes = new long[dirs.Length];
            var atimes = new DateTime[dirs.Length];
            long total = 0;
            for (int i = 0; i < dirs.Length; i++)
            {
                long s = 0; DateTime at = dirs[i].CreationTimeUtc;
                foreach (var f in dirs[i].GetFiles()) { s += f.Length; if (f.LastWriteTimeUtc > at) at = f.LastWriteTimeUtc; }
                sizes[i] = s; atimes[i] = at; total += s;
            }
            if (total <= cap) return;

            var list = new List<(DirectoryInfo d, long size, DateTime atime)>(dirs.Length);
            for (int i = 0; i < dirs.Length; i++) list.Add((dirs[i], sizes[i], atimes[i]));

            var now = DateTime.UtcNow;
            list.Sort((a, b) => a.atime.CompareTo(b.atime));   // старые первыми
            foreach (var it in list)
            {
                if (total <= cap) break;
                if (_hlsRunning.ContainsKey(it.d.Name)) continue;   // активный транскод не трогаем
                if (_hlsTouch.TryGetValue(it.d.Name, out var t) && (now - t) < _hlsTouchTtl) continue;   // активное воспроизведение не трогаем
                try { it.d.Delete(true); total -= it.size; _hlsTouch.TryRemove(it.d.Name, out _); _hlsFailed.TryRemove(it.d.Name, out _); } catch { }
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _hlsCleanupBusy, 0); }
    }
    #endregion

    #region /qdl/transcode — перекодировать загрузку в MP4 (H.264+AAC, все дорожки) и заменить торрент файлом
    // Зачем: браузеры не декодируют HEVC/AV1 (звук есть, картинки нет — HLS копирует видео).
    // Транскод: libx264 + AAC на все аудиодорожки (метаданные языка/студии сохраняются),
    // по успеху пишется local-маркер (тот же infohash — мета/постер/карточка не мигрируют),
    // затем торрент удаляется из qBittorrent вместе с исходными файлами.
    sealed class TcJob
    {
        public volatile string state = "running"; public double progress; public volatile string error;
        public volatile string file;       // имя текущей серии (для тостов клиента)
        public volatile int fileDone;      // готово файлов
        public volatile int filesTotal;    // всего файлов (1 = фильм, статус без новых полей)
    }
    sealed class TcFile { public int index; public string src, part, final; public long size; public double duration; }
    sealed class TcQueueItem
    {
        public string hash;
        public bool finalize;              // true: по успеху удалить торрент + снять слежение; false: оверлей (торрент жив)
        public string name;                // имя раздачи (для маркера сериала)
        public string dir;                 // папка mp4-копий сериала; null = фильм (старый плоский маркер)
        public List<TcFile> files;         // серии; может РАСТИ во время работы (авто-транскод докачавшихся) — доступ под _tcEnqLock
    }
    static readonly ConcurrentDictionary<string, TcJob> _tcJobs = new();
    static readonly ConcurrentQueue<TcQueueItem> _tcQueue = new();   // очередь: транскоды идут по одному
    static readonly object _tcEnqLock = new();                       // дедуп + синхронизация списка files
    static int _tcWorker = 0;                                        // 1 = воркер-цикл жив
    static volatile TcQueueItem _tcCurrent;                          // выполняемый элемент (для дозаписи серий)

    // Совместимость: старая сигнатура одиночного файла (фильм) — используется прежним путём и тестами.
    static int EnqueueTranscode(string hash, string src, string part, string final, double duration)
        => EnqueueTranscode(hash, finalize: true, name: null, dir: null,
            new List<TcFile> { new TcFile { index = -1, src = src, part = part, final = final, duration = duration } });

    // Поставить файлы в очередь; повторные (hash, index) не дублируются, новые серии
    // ДОЗАПИСЫВАЮТСЯ в queued/running элемент того же hash (воркер дочитает список).
    static int EnqueueTranscode(string hash, bool finalize, string name, string dir, List<TcFile> files)
    {
        lock (_tcEnqLock)
        {
            TcQueueItem target = (_tcCurrent != null && _tcCurrent.hash == hash) ? _tcCurrent : null;
            if (target == null)
                foreach (var q in _tcQueue.ToArray())
                    if (q.hash == hash) { target = q; break; }

            if (target != null)
            {
                target.files ??= new List<TcFile>();
                var covered = new HashSet<int>();
                foreach (var f in target.files) covered.Add(f.index);
                foreach (var f in files)
                    if (!covered.Contains(f.index)) { target.files.Add(f); covered.Add(f.index); }
                if (finalize) target.finalize = true;   // эскалация оверлея до финализации
                if (_tcJobs.TryGetValue(hash, out var jr)) jr.filesTotal = target.files.Count;
            }
            else if (_tcJobs.TryGetValue(hash, out var ex) && (ex.state == "queued" || ex.state == "running"))
            {
                // job числится активным, но элемента нет — не дублируем (прежняя семантика дедупа)
            }
            else
            {
                _tcJobs[hash] = new TcJob { state = "queued", progress = 0, filesTotal = files.Count };
                _tcQueue.Enqueue(new TcQueueItem { hash = hash, finalize = finalize, name = name, dir = dir, files = files });
            }
        }
        KickWorker();
        return QueuePosition(hash);
    }

    static int QueuePosition(string hash)
    {
        var arr = _tcQueue.ToArray();
        for (int i = 0; i < arr.Length; i++)
            if (arr[i].hash == hash) return i + 1;
        return 0;   // 0 = не в очереди (уже выполняется/завершён)
    }

    // одиночный воркер: берёт по одному, исключение задачи не убивает цикл
    static void KickWorker()
    {
        if (Interlocked.CompareExchange(ref _tcWorker, 1, 0) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    TcQueueItem it;
                    TcJob job;
                    lock (_tcEnqLock)   // dequeue + захват _tcCurrent атомарно: дозапись серий не теряется
                    {
                        if (!_tcQueue.TryDequeue(out it)) break;
                        _tcJobs.TryGetValue(it.hash, out job);
                        if (job == null || job.state != "queued") continue;   // защита от рассинхрона
                        _tcCurrent = it;
                    }
                    job.state = "running";
                    try { await RunTranscodeSeries(it, job); }
                    catch (Exception ex)
                    {
                        job.error = "internal";
                        job.state = "error";
                        Console.WriteLine("[QbitDownload] tc worker: " + ex);
                    }
                    finally
                    {
                        lock (_tcEnqLock) { if (ReferenceEquals(_tcCurrent, it)) _tcCurrent = null; }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _tcWorker, 0);
                if (!_tcQueue.IsEmpty) KickWorker();   // гонка: элемент положили между TryDequeue-фейлом и сбросом флага
            }
        });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/transcode")]
    async public Task<ActionResult> Transcode(string hash, string mode = null)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            var loc0 = LoadLocal(hash);
            if (loc0 != null && !LocalIsOverlay(loc0)) return Json(new { success = false, error = "уже сконвертировано в MP4" });
            if (_tcJobs.TryGetValue(hash, out var j0) && (j0.state == "running" || j0.state == "queued") && mode != "finalize" && mode != "overlay")
                return Json(new { success = true, already = true, queued = QueuePosition(hash) });

            // валидация на request-time — мгновенный отклик «раздача ещё качается» и т.п.
            using var c = await Qbit();
            string he = HttpUtility.UrlEncode(hash);
            var info = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?hashes={he}"));
            if (info.Count == 0) return Json(new { success = false, error = "раздача не найдена" });
            double torrentProgress = info[0].Value<double?>("progress") ?? 0;
            string torrentName = info[0].Value<string>("name") ?? hash;

            var files = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/files?hash={he}"));
            var vids = new List<JToken>();
            foreach (var f in files)
                if (Regex.IsMatch(f.Value<string>("name") ?? "", "\\.(mkv|mp4|avi|ts|m4v|webm|mov)$", RegexOptions.IgnoreCase)) vids.Add(f);
            if (vids.Count == 0) return Json(new { success = false, error = "видеофайлы не найдены" });

            string outDir = Path.Combine(ModInit.conf.downloadsPath, "transcoded");
            Directory.CreateDirectory(outDir);

            if (vids.Count == 1)
            {
                // фильм — прежний путь (старый плоский маркер, финализация всегда)
                if (torrentProgress < 0.999) return Json(new { success = false, error = "раздача ещё качается" });
                string src = await ResolveFile(c, hash, -1);
                if (src == null) return Json(new { success = false, error = "файл не найден на диске" });

                double duration = ProbeDuration(src);
                string baseName = Path.GetFileNameWithoutExtension(src);
                foreach (var ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
                string final = Path.Combine(outDir, baseName + ".mp4");
                if (System.IO.File.Exists(final)) final = Path.Combine(outDir, baseName + "." + hash.Substring(0, 8) + ".mp4");

                int pos = EnqueueTranscode(hash, src, final + ".part", final, duration);
                return Json(new { success = true, queued = pos });
            }

            // сериал: оверлей (торрент+слежение живут, серии подменяются mp4) или финализация (как фильм)
            bool watchedNow;
            lock (_watchLock)
            {
                watchedNow = false;
                foreach (var m in LoadWatch()) if (m.Value<string>("hash") == hash) { watchedNow = true; break; }
            }
            bool finalize = mode == "finalize" || (mode != "overlay" && !watchedNow);
            if (finalize && torrentProgress < 0.999) return Json(new { success = false, error = "раздача ещё качается" });

            vids.Sort((a, b) => NaturalCompare(a.Value<string>("name") ?? "", b.Value<string>("name") ?? ""));
            string dir = Path.Combine(outDir, SafeFileBase(torrentName) + "." + hash.Substring(0, 8));
            var lfs0 = loc0 != null ? LocalFiles(loc0) : null;

            var items = new List<TcFile>();
            int skippedDownloading = 0;
            foreach (var f in vids)
            {
                string n = f.Value<string>("name") ?? "";
                if ((f.Value<double?>("progress") ?? 0) < 0.999) { skippedDownloading++; continue; }   // докачается → авто-транскод
                if (OverlayFor(lfs0, n) != null) continue;                                             // серия уже транскожена
                string src = await ResolveFile(c, hash, f.Value<int?>("index") ?? -1);
                if (src == null) continue;
                string final = Path.Combine(dir, SafeFileBase(n) + ".mp4");
                items.Add(new TcFile
                {
                    index = f.Value<int?>("index") ?? -1,
                    src = src, part = final + ".part", final = final,
                    size = f.Value<long?>("size") ?? 0
                });
            }
            if (items.Count == 0 && !finalize)
                return Json(new { success = false, error = skippedDownloading > 0 ? "нет докачанных серий" : "все серии уже сконвертированы" });
            if (items.Count == 0 && finalize && lfs0 != null)
            {
                // всё уже транскожено оверлеем — финализируем без работы: одним пустым элементом
                items = new List<TcFile>();
            }

            int qpos = EnqueueTranscode(hash, finalize, torrentName, dir, items);
            return Json(new { success = true, queued = qpos, files = items.Count, skipped = skippedDownloading });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] transcode: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/transcode/status")]
    public ActionResult TranscodeStatus(string hash)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        if (_tcJobs.TryGetValue(hash, out var j))
        {
            if (j.state == "queued") return Json(new { state = "queued", position = QueuePosition(hash) });
            if (j.filesTotal > 1)   // сериал: клиенту видно «серия i/N»
                return Json(new { state = j.state, progress = Math.Round(j.progress, 3), error = j.error, file = j.file, fileDone = j.fileDone, filesTotal = j.filesTotal });
            return Json(new { state = j.state, progress = Math.Round(j.progress, 3), error = j.error });
        }
        var locS = LoadLocal(hash);
        if (locS != null && !LocalIsOverlay(locS)) return Json(new { state = "done", progress = 1.0 });
        return Json(new { state = "none" });   // в т.ч. после рестарта: очередь в памяти, клиент покажет «прервано»
    }

    // аргументы MP4-транскода: CPU (x264, как раньше) или NVENC на хостовом GPU-воркере.
    // nvenc p6+cq19+AQ визуально сравним с x264 fast crf19, скорость ~5-10× выше; HEVC декодится NVDEC-ом
    static List<string> Mp4Args(string src, string part, bool copyVideo = false, bool nvenc = false)
    {
        var args = new List<string> { "-y" };
        if (nvenc && !copyVideo) args.AddRange(new[] { "-hwaccel", "cuda" });
        args.AddRange(new[]
        {
            "-i", src,
            "-map", "0:v:0", "-map", "0:a?",
            "-dn", "-sn", "-map_chapters", "-1"            // data/субтитры в mp4 не тащим
        });
        if (copyVideo)
            args.AddRange(new[] { "-c:v", "copy" });       // видео уже h264 → ремукс (IO-bound, минуты вместо часов)
        else if (nvenc)
            args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0", "-spatial-aq", "1", "-temporal-aq", "1", "-b_ref_mode", "middle" });
        else
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "fast", "-crf", "19" });
        if (!copyVideo)
            args.AddRange(new[] { "-pix_fmt", "yuv420p", "-profile:v", "high" });   // только при энкоде; level не форсить: 4.1 невалиден для 4K (NVENC падает «Invalid Level», x264 молча игнорирует), энкодер сам берёт минимальный (1080p→4.0, 4K→5.1)
        args.AddRange(new[]
        {
            "-c:a", "aac", "-ac", "2", "-b:a", "256k",     // как в HLS-ветке; язык/название дорожек ffmpeg переносит сам
            "-movflags", "+faststart",
            "-f", "mp4",
            "-progress", "pipe:1", "-nostats",
            part
        });
        return args;
    }

    // общий прогресс сериала: взвешивание по размерам файлов (размеры бесплатны из qBit
    // и хорошо коррелируют со временем обработки); totalBytes<=0 → прогресс текущего файла
    static double TcOverallProgress(long doneBytes, long totalBytes, long curSize, double curFileProgress)
    {
        double p = totalBytes <= 0
            ? curFileProgress
            : (doneBytes + Math.Clamp(curFileProgress, 0, 1) * curSize) / (double)totalBytes;
        return Math.Min(0.99, Math.Max(0, p));
    }

    // Один файл: h264 → ремукс (copy), иначе NVENC/CPU-энкод. Возвращает null = ок, иначе текст ошибки.
    static async Task<string> RunTranscodeItem(TcFile f, TcJob job, Func<double, double> overall)
    {
        double duration = f.duration > 0 ? f.duration : ProbeDuration(f.src);
        bool copyV = ProbeVideoCodec(f.src) == "h264";
        Directory.CreateDirectory(Path.GetDirectoryName(f.part));

        IFfJob ff = copyV
            ? FfJob.StartLocal(Mp4Args(f.src, f.part, copyVideo: true))              // ремукс IO-bound — GPU не нужен
            : FfJob.Start(nv => Mp4Args(f.src, f.part, copyVideo: false, nvenc: nv), "mp4");
        bool cpuRetried = !copyV && ff is LocalFfJob;   // локальный энкод ретраить некуда
        bool copyRetried = false;

        while (true)
        {
            while (!ff.HasExited)
            {
                await Task.Delay(1000);
                if (duration > 0 && ff.OutTimeUs > 0)
                    job.progress = overall(ff.OutTimeUs / 1_000_000.0 / duration);
            }
            if (ff.ExitCode == 0) break;

            if (copyV && !copyRetried)
            {
                // ремукс не удался (кривой контейнер/таймстампы) → полный транскод
                Console.WriteLine("[QbitDownload] transcode: remux failed (exit=" + ff.ExitCode + "), полный транскод: " + Path.GetFileName(f.src));
                copyRetried = true; copyV = false;
                try { if (System.IO.File.Exists(f.part)) System.IO.File.Delete(f.part); } catch { }
                ff = FfJob.Start(nv => Mp4Args(f.src, f.part, copyVideo: false, nvenc: nv), "mp4");
                cpuRetried = ff is LocalFfJob;
                continue;
            }
            if (!cpuRetried)
            {
                // удалённый джоб не дожил (воркер умер / nvenc-фейл) → один повтор локально на CPU
                Console.WriteLine("[QbitDownload] transcode: ffworker job failed (exit=" + ff.ExitCode + "), повтор на CPU: " + Tail(ff.StderrTail, 400));
                cpuRetried = true;
                try { if (System.IO.File.Exists(f.part)) System.IO.File.Delete(f.part); } catch { }
                ff = FfJob.StartLocal(Mp4Args(f.src, f.part, copyVideo: false, nvenc: false));
                continue;
            }
            break;
        }

        if (ff.ExitCode != 0 || !System.IO.File.Exists(f.part) || new FileInfo(f.part).Length < 1_000_000)
        {
            try { if (System.IO.File.Exists(f.part)) System.IO.File.Delete(f.part); } catch { }
            Console.WriteLine("[QbitDownload] transcode failed file=" + Path.GetFileName(f.src) + " exit=" + ff.ExitCode + ": " + Tail(ff.StderrTail, 800));
            return "ffmpeg exit=" + ff.ExitCode;
        }
        System.IO.File.Move(f.part, f.final, true);
        return null;
    }

    // Вся раздача: цикл по файлам (список может расти — авто-транскод докачавшихся серий),
    // резюм по готовым mp4, затем финализация (маркер / удаление торрента / слежение / HLS-кэш).
    static async Task RunTranscodeSeries(TcQueueItem it, TcJob job)
    {
        var done = new List<TcFile>();
        string failErr = null;
        try
        {
            for (int i = 0; ; i++)
            {
                TcFile f;
                long totalBytes = 0;
                lock (_tcEnqLock)
                {
                    if (i >= it.files.Count) { _tcCurrent = null; break; }   // под локом: дозапись либо видит нас, либо создаст новый элемент
                    f = it.files[i];
                    job.filesTotal = it.files.Count;
                    foreach (var x in it.files) totalBytes += x.size;
                }
                job.fileDone = i;
                job.file = Path.GetFileNameWithoutExtension(f.final);

                // резюм после обрыва/рестарта: готовая серия не переделывается
                if (System.IO.File.Exists(f.final) && new FileInfo(f.final).Length > 1_000_000) { done.Add(f); continue; }

                if (!System.IO.File.Exists(f.src))
                {
                    failErr = "файл не найден (удалён, пока стоял в очереди)";
                    break;
                }

                long doneBytes = 0;
                foreach (var x in done) doneBytes += x.size;
                long curSize = f.size;
                string err = await RunTranscodeItem(f, job, p => TcOverallProgress(doneBytes, totalBytes, curSize, p));
                if (err != null)
                {
                    failErr = job.filesTotal > 1
                        ? "серия " + (i + 1) + " из " + job.filesTotal + " (" + Path.GetFileNameWithoutExtension(f.final) + "): " + err
                        : err;
                    break;
                }
                done.Add(f);
                job.fileDone = i + 1;
            }

            if (failErr != null)
            {
                // торрент НЕ трогаем, готовые mp4 ОСТАВЛЯЕМ — повторный запуск дорежет только недостающее
                job.error = failErr;
                job.state = "error";
                Console.WriteLine("[QbitDownload] transcode failed hash=" + it.hash + ": " + failErr);
                return;
            }

            // маркер пишем ДО удаления торрента — ни на секунду не остаёмся без записи в «Загрузках».
            // added_on снимаем с живого торрента: сортировка по дате загрузки, транскод позицию не меняет
            long? addedOn = null;
            try
            {
                using var qc = await Qbit();
                var ti = JArray.Parse(await qc.GetStringAsync($"/api/v2/torrents/info?hashes={HttpUtility.UrlEncode(it.hash)}"));
                if (ti.Count > 0) addedOn = ti[0].Value<long?>("added_on");
            }
            catch { }

            Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "local"));
            if (it.dir == null && it.files.Count == 1 && it.finalize)
            {
                // фильм: старый плоский формат (обратная совместимость, поведение байт-в-байт)
                string final = it.files[0].final;
                var loc = new JObject
                {
                    ["name"] = Path.GetFileName(final),
                    ["path"] = final,
                    ["size"] = new FileInfo(final).Length,
                    ["added"] = addedOn ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                System.IO.File.WriteAllText(LocalPath(it.hash), loc.ToString(Newtonsoft.Json.Formatting.None));
            }
            else
            {
                // сериал: новый формат, merge с существующим оверлеем (дозаписываем новые серии)
                var prev = LoadLocal(it.hash);
                var all = prev != null ? LocalFiles(prev) : new List<LocalFile>();
                foreach (var f in done)
                {
                    string fname = Path.GetFileName(f.final);
                    if (!System.IO.File.Exists(f.final)) continue;
                    bool exists = false;
                    foreach (var e in all) if (SafeFileBase(e.name) == SafeFileBase(fname)) { exists = true; break; }
                    if (!exists) all.Add(new LocalFile { name = fname, path = f.final, size = new FileInfo(f.final).Length });
                }
                all.Sort((a, b) => NaturalCompare(a.name, b.name));
                long total = 0; var farr = new JArray();
                for (int k = 0; k < all.Count; k++)
                {
                    total += all[k].size;
                    farr.Add(new JObject { ["index"] = k, ["name"] = all[k].name, ["path"] = all[k].path, ["size"] = all[k].size });
                }
                var loc = new JObject
                {
                    ["name"] = it.name ?? prev?.Value<string>("name"),
                    ["dir"] = it.dir ?? prev?.Value<string>("dir"),
                    ["size"] = total,
                    // addedOn первым: после re-grab prev.added — старая дата, торрент в списке показывался с новой
                    ["added"] = addedOn ?? prev?.Value<long?>("added") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["overlay"] = !it.finalize,
                    ["files"] = farr
                };
                System.IO.File.WriteAllText(LocalPath(it.hash), loc.ToString(Newtonsoft.Json.Formatting.None));
            }

            if (it.finalize)
            {
                try
                {
                    using var c = await Qbit();
                    // доноров снимаем ПЕРВЫМИ, пока основная ещё в qBit: иначе они осиротеют (watch-запись
                    // ниже удаляется целиком) и уборка при следующем старте снесёт их файлы вслепую
                    string mainContentPath = (await QbitTorrentInfo(c, it.hash))?.Value<string>("content_path");
                    await DeleteDonorsOf(c, it.hash, mainContentPath);
                    var form = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("hashes", it.hash),
                        new KeyValuePair<string, string>("deleteFiles", "true")
                    });
                    await c.PostAsync("/api/v2/torrents/delete", form);
                }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] transcode: torrent delete failed: " + ex.Message); }

                // из слежения тоже убираем (перекачка новой версии раздачи затёрла бы замену)
                try
                {
                    lock (_watchLock)
                    {
                        var a = LoadWatch(); var b = new JArray();
                        foreach (var m in a) if (m.Value<string>("hash") != it.hash) b.Add(m);
                        if (b.Count != a.Count) SaveWatch(b);
                    }
                }
                catch { }
            }

            // HLS-кэш нарезан из СТАРЫХ (HEVC) файлов — сбросить, иначе браузер продолжит получать их сегменты.
            // Кеш резолва тоже: local-маркер записан, пути подменяются на mp4-копии.
            DropHlsCache(it.hash);
            DropResolveCache(it.hash);

            job.progress = 1.0;
            job.state = "done";
            Console.WriteLine("[QbitDownload] transcode done hash=" + it.hash + " files=" + done.Count + (it.finalize ? " (финализация)" : " (оверлей)"));
        }
        catch (Exception ex)
        {
            job.error = "internal";
            job.state = "error";
            Console.WriteLine("[QbitDownload] transcode run: " + ex);
        }
    }

    // Оверлей-раздача: серии, докачавшиеся ПОСЛЕ транскода, автоматически конвертируются в mp4.
    // Вызывается из ScanEpisodeNotifications (раз в notifyScanIntervalMinutes) — files уже загружены.
    static async Task AutoTranscodeOverlay(HttpClient c, string hash, JArray files)
    {
        var loc = LoadLocal(hash);
        if (loc == null || !LocalIsOverlay(loc)) return;
        string dir = loc.Value<string>("dir");
        if (string.IsNullOrEmpty(dir)) return;
        var lfs = LocalFiles(loc);

        var fresh = new List<TcFile>();
        foreach (var f in files)
        {
            string n = f.Value<string>("name") ?? "";
            if (!_videoExtRx.IsMatch(n)) continue;
            if ((f.Value<double?>("progress") ?? 0) < 0.999) continue;   // серия ещё качается
            if (OverlayFor(lfs, n) != null) continue;                    // уже транскожена
            string src = await ResolveFile(c, hash, f.Value<int?>("index") ?? -1);
            if (src == null) continue;
            string final = Path.Combine(dir, SafeFileBase(n) + ".mp4");
            fresh.Add(new TcFile
            {
                index = f.Value<int?>("index") ?? -1,
                src = src, part = final + ".part", final = final,
                size = f.Value<long?>("size") ?? 0
            });
        }
        if (fresh.Count == 0) return;
        Console.WriteLine("[QbitDownload] auto-transcode: " + (loc.Value<string>("name") ?? hash) + " — новых серий: " + fresh.Count);
        EnqueueTranscode(hash, finalize: false, loc.Value<string>("name"), dir, fresh);
    }

    // уборка обрывков транскода после рестарта контейнера: .part пишет только RunTranscode,
    // при старте процесса ffmpeg'ов ещё нет → любой .part — мусор прерванного транскода
    public static void CleanupTranscodeParts()
    {
        try
        {
            string tdir = Path.Combine(ModInit.conf.downloadsPath, "transcoded");
            if (!Directory.Exists(tdir)) return;
            foreach (var f in Directory.GetFiles(tdir, "*.part", SearchOption.AllDirectories))   // и в подпапках сериалов
                try { System.IO.File.Delete(f); Console.WriteLine("[QbitDownload] removed stale " + Path.GetFileName(f)); } catch { }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] part cleanup: " + ex.Message); }
    }

    static double ProbeDuration(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ModInit.conf.ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "quiet", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (double.TryParse(o.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                return d;
        }
        catch { }
        return 0;
    }

    static string Tail(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? (s ?? "") : s.Substring(s.Length - n);
    #endregion

    #region /qdl/watch — слежение за сериалами (авто-докачка новых серий)
    static string WatchFile => Path.Combine(ModInit.conf.cachePath, "watch.json");
    static string LinkPath(string hash) => Path.Combine(ModInit.conf.cachePath, "links", hash + ".json");
    static readonly object _watchLock = new();

    static JArray LoadWatch()
    {
        try { if (System.IO.File.Exists(WatchFile)) return JArray.Parse(System.IO.File.ReadAllText(WatchFile)); } catch { }
        return new JArray();
    }
    static void SaveWatch(JArray a)
    {
        try { Directory.CreateDirectory(ModInit.conf.cachePath); System.IO.File.WriteAllText(WatchFile, a.ToString(Newtonsoft.Json.Formatting.None)); } catch { }
    }

    // Единый async-гейт для ФОНОВЫХ операций над watch.json (CheckWatches / HuntAll / ScanEpisodeNotifications):
    // каждая делает LoadWatch → минуты сетевого I/O → SaveWatch, поэтому без общей сериализации фоновые проходы
    // перезатирают правки друг друга (потеря доноров / воскрешение старого hash). `lock` не переживает await,
    // поэтому SemaphoreSlim + skip-if-busy (заменяет прежние отдельные гарды _hunting/_scanning).
    static readonly SemaphoreSlim _watchGate = new SemaphoreSlim(1, 1);

    static HashSet<string> WatchHashes(JArray a)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in a) { var h = m.Value<string>("hash"); if (!string.IsNullOrEmpty(h)) s.Add(h); }
        return s;
    }

    // Сохранение из фонового прохода с реконсиляцией интерактивных правок. Пока шёл проход (минуты),
    // WatchAdd/WatchRemove могли добавить/убрать записи под _watchLock. Под _watchLock перечитываем
    // актуальный файл и применяем ТОЛЬКО эти интерактивные дельты (по членству hash) к рабочему списку,
    // затем пишем — иначе слепой SaveWatch затёр бы их. originalHashes — hash-и на старте прохода
    // (чтобы отличить интерактивный add от нашего же re-grab, сменившего hash записи).
    static void SaveWatchReconciled(JArray working, HashSet<string> originalHashes)
    {
        lock (_watchLock)
        {
            var fresh = LoadWatch();
            var workingHashes = WatchHashes(working);
            var freshHashes = WatchHashes(fresh);
            // интерактивный ADD: запись есть в свежем файле, не было в нашем снимке и нет в рабочем → добавить
            foreach (var f in fresh)
            {
                var h = f.Value<string>("hash");
                if (string.IsNullOrEmpty(h)) continue;
                if (!originalHashes.Contains(h) && !workingHashes.Contains(h)) { working.Add(f); workingHashes.Add(h); }
            }
            // интерактивный REMOVE: запись была в снимке, исчезла из свежего, всё ещё в рабочем как есть → убрать
            for (int i = working.Count - 1; i >= 0; i--)
            {
                var h = working[i].Value<string>("hash");
                if (!string.IsNullOrEmpty(h) && originalHashes.Contains(h) && !freshHashes.Contains(h)) working.RemoveAt(i);
            }
            SaveWatch(working);
        }
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/watch")]
    async public Task<ActionResult> WatchAdd(string hash)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            string link = null, query = null; JObject ctx = null;
            if (System.IO.File.Exists(LinkPath(hash)))
            {
                var lj = JObject.Parse(System.IO.File.ReadAllText(LinkPath(hash)));
                link = lj.Value<string>("link"); query = lj.Value<string>("query");
                ctx = lj["ctx"] as JObject;   // TMDB-контекст поиска (может отсутствовать у старых записей)
            }
            if (string.IsNullOrWhiteSpace(link))
                return Json(new { success = false, error = "no link" });   // перекачай раздачу, чтобы включить слежение

            JObject meta = System.IO.File.Exists(MetaPath(hash)) ? JObject.Parse(System.IO.File.ReadAllText(MetaPath(hash))) : new JObject();
            int seriesId = meta.Value<int?>("id") ?? 0;
            bool added = false;
            lock (_watchLock)
            {
                var a = LoadWatch();
                bool exists = false;
                foreach (var m in a) if (m.Value<string>("hash") == hash) { exists = true; break; }
                if (!exists)
                {
                    var w = new JObject { ["hash"] = hash, ["link"] = link, ["query"] = query, ["id"] = meta.Value<int?>("id"), ["title"] = meta.Value<string>("title") };
                    if (ctx != null) w["ctx"] = ctx;
                    a.Add(w);
                    SaveWatch(a);
                    added = true;
                }
            }
            // отсекаем уже присутствующие серии: уведомляем только про то, что докачается ПОСЛЕ включения слежения
            if (added)
                await SeedBaseline(SeriesKey(seriesId, link), hash);
            return Json(new { success = true });
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] watch add: " + ex); return Json(new { success = false, error = "internal error" }); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/watch/remove")]
    async public Task<ActionResult> WatchRemove(string hash)
    {
        try
        {
            string link = null; int seriesId = 0; JArray donors = null;
            lock (_watchLock)
            {
                var a = LoadWatch(); var b = new JArray();
                foreach (var m in a)
                {
                    if (m.Value<string>("hash") != hash) b.Add(m);
                    else { link = m.Value<string>("link"); seriesId = m.Value<int?>("id") ?? 0; donors = m["donors"] as JArray; }
                }
                SaveWatch(b);
            }
            // каскад: без слежения раздачи-доноры (охота) не нужны — удаляем с файлами
            if (donors != null && donors.Count > 0)
            {
                try
                {
                    using var c = await Qbit();
                    foreach (var d in donors.OfType<JObject>())
                        await QbitDeleteDonorSafe(c, d.Value<string>("hash"), hash);   // с файлами ТОЛЬКО если категория донорская и папка не общая с основной
                }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] watch remove donors: " + ex.Message); }
            }
            // сбрасываем базу отсечения, чтобы повторное включение слежения перебазировалось заново (историю noti сохраняем)
            try { string sk = SeriesKey(seriesId, link); using var db = new SqlContext(); db.seen.Where(x => x.seriesKey == sk).ExecuteDelete(); } catch { }
            return Json(new { success = true });
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] watch remove: " + ex); return Json(new { success = false }); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/watch/list")]
    public ActionResult WatchListAll() => ContentTo(LoadWatch().ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");

    [HttpGet, AllowAnonymous]
    [Route("qdl/watch/check")]
    async public Task<ActionResult> WatchCheckNow() { int n = await CheckWatches(); return Json(new { success = true, regrabbed = n }); }

    // Фоновая проверка: пере-резолвим раздачу; если infohash изменился (добавили серии) —
    // до-добавляем новую раздачу (qBit перепроверит файлы и дотянет только новые серии).
    public static async Task<int> CheckWatches()
    {
        int regrabbed = 0;
        // общий фоновый гейт: сериализуем с HuntAll/ScanEpisodeNotifications (иначе они перезатирают
        // watch.json друг друга). Гейт НЕ реентрантный, поэтому ScanEpisodeNotifications зовём ПОСЛЕ release.
        if (await _watchGate.WaitAsync(0))
        {
        try
        {
        JArray list; HashSet<string> orig;
        lock (_watchLock) { list = LoadWatch(); orig = WatchHashes(list); }
        bool changed = false;

        foreach (var m in list)
        {
            try
            {
                string link = m.Value<string>("link");
                string curHash = m.Value<string>("hash");
                if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(curHash)) continue;

                string magnet = await ResolveMagnetStatic(link);
                if (string.IsNullOrWhiteSpace(magnet)) continue;   // трекер лежит/таймаут — застой НЕ засчитываем
                string newHash = MagnetHash(magnet);
                if (string.IsNullOrWhiteSpace(newHash)) continue;

                if (newHash.Equals(curHash, StringComparison.OrdinalIgnoreCase))
                {
                    // топик не обновился → счётчик застоя; на пороге — поискать более полную раздачу
                    m["stale"] = (m.Value<int?>("stale") ?? 0) + 1;
                    changed = true;
                    // самолечение: если основная застряла в донорской категории (промоушен не довёлся
                    // из-за сбоя qBit) — доводим здесь, иначе она невидима в «Загрузках»
                    try
                    {
                        using var cc = await Qbit();
                        await PromoteIfDonor(cc, curHash, list.OfType<JObject>(), m.Value<string>("title"));
                    }
                    catch (Exception ex) { Console.WriteLine("[QbitDownload] watch promote retry: " + ex.Message); }
                    try { await ConsiderSwitch((JObject)m); }
                    catch (Exception ex) { Console.WriteLine("[QbitDownload] switch consider: " + ex); }
                    continue;
                }

                using var c = await Qbit();
                var add = await QbitAddMagnetStatus(c, magnet, ModInit.conf.category);   // qBit перепроверит и дотянет новые серии
                if (add == QbitAddStatus.Failed) continue;

                // Перевыложенную раздачу могла уже качать охота за сериями (донор с тем же infohash):
                // тогда add — дубликат, категорию существующего торрента qBit НЕ меняет, и он остаётся
                // «донором», хотя стал основной. Промоутим и стираем донорские записи, иначе контур
                // замещения снимет «донора» С ФАЙЛАМИ — весь сериал (инцидент 2026-07-25, «Укрытие»).
                if (add == QbitAddStatus.Duplicate)
                    Console.WriteLine("[QbitDownload] watch: re-grab " + m.Value<string>("title") + " — " + newHash + " уже в qBit (дубликат)");
                await PromoteIfDonor(c, newHash, list.OfType<JObject>(), m.Value<string>("title"));

                try   // убрать старую раздачу (файлы оставить)
                {
                    var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("hashes", curHash), new KeyValuePair<string, string>("deleteFiles", "false") });
                    await c.PostAsync("/api/v2/torrents/delete", form);
                }
                catch { }

                MigrateCache(curHash, newHash);
                // явный бамп: если раздачу уже качала охота, add — дубликат и added_on у торрента старый
                ActivityTouch(newHash);
                m["hash"] = newHash;
                m["stale"] = 0;
                m["pendingSwitch"] = null;   // топик ожил — предложение переключения снимается
                changed = true; regrabbed++;
                Console.WriteLine("[QbitDownload] watch: re-grab " + m.Value<string>("title") + " " + curHash + "->" + newHash);

                // «началась загрузка»: серии докачаются не сразу, зритель узнаёт о них только из
                // ScanEpisodeNotifications через час-другой — даём сигнал сразу (дедуп по newHash)
                AddStartNotification(m.Value<int?>("id") ?? 0, link, newHash, m.Value<string>("title"));
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] watch item: " + ex); }
        }

        if (changed) SaveWatchReconciled(list, orig);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] checkwatches: " + ex); }
        finally { _watchGate.Release(); }
        }
        else Console.WriteLine("[QbitDownload] watch check: тик пропущен (gate занят)");

        // после возможного re-grab — заодно собрать уведомления о докачавшихся сериях (берёт гейт сам)
        try { await ScanEpisodeNotifications(); } catch (Exception ex) { Console.WriteLine("[QbitDownload] post-checkwatches scan: " + ex); }
        return regrabbed;
    }

    static void MigrateCache(string oldH, string newH)
    {
        DropResolveCache(oldH);   // re-grab/SWITCH: старый hash больше не резолвится, новый начнёт с чистого листа
        DropResolveCache(newH);
        void mv(string a, string b) { try { if (System.IO.File.Exists(a)) { Directory.CreateDirectory(Path.GetDirectoryName(b)); System.IO.File.Copy(a, b, true); System.IO.File.Delete(a); } } catch { } }
        mv(MetaPath(oldH), MetaPath(newH));
        mv(PosterPath(oldH), PosterPath(newH));
        mv(LinkPath(oldH), LinkPath(newH));
        mv(LocalPath(oldH), LocalPath(newH));   // оверлей-маркер транскода следует за re-grab (пути внутри абсолютные)
        CollectionsMigrateHash(oldH, newH);
        ActivityMigrate(oldH, newH);
    }

    static string MagnetHash(string magnet)
    {
        var hm = Regex.Match(magnet ?? "", "btih:([0-9a-fA-F]{40}|[0-9a-zA-Z]{32})", RegexOptions.IgnoreCase);
        return hm.Success ? hm.Groups[1].Value.ToLower() : "";
    }

    // резолв нашего loopback-парселинка в magnet (фоновая проверка, без request-host)
    static async Task<string> ResolveMagnetStatic(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return link;   // прямой magnet не меняется
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u) || !IsLoopbackSelf(u)) return null;

        using var rh = new HttpClientHandler { AllowAutoRedirect = false };
        using var rc = new HttpClient(rh) { Timeout = TimeSpan.FromSeconds(20) };
        HttpResponseMessage resp = null;
        try
        {
            var current = u;
            for (int hop = 0; hop < 5; hop++)
            {
                resp?.Dispose();
                resp = await rc.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);
                int code = (int)resp.StatusCode; var loc = resp.Headers.Location;
                if (code < 300 || code >= 400 || loc == null) break;
                var next = loc.IsAbsoluteUri ? loc : new Uri(resp.RequestMessage?.RequestUri ?? current, loc);
                if (next.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return next.OriginalString;
                if (!IsLoopbackSelf(next)) break;
                current = next;
            }
            if (resp != null)
            {
                try { await resp.Content.LoadIntoBufferAsync(5_000_000); } catch { return null; }
                var mm = Regex.Match(await resp.Content.ReadAsStringAsync() ?? "", "magnet:\\?[^\"'\\s<]+");
                if (mm.Success) return mm.Value;
            }
        }
        catch { }
        finally { resp?.Dispose(); }
        return null;
    }

    static bool IsLoopbackSelf(Uri u)
    {
        if (u == null || (u.Scheme != "http" && u.Scheme != "https")) return false;
        if (u.Port != CoreInit.conf.listen.port) return false;
        string h = u.Host.ToLowerInvariant();
        if (h == "127.0.0.1" || h == "localhost" || h == "::1") return true;
        if (!string.IsNullOrEmpty(CoreInit.conf.listen.localhost) && h == CoreInit.conf.listen.localhost.ToLowerInvariant()) return true;
        return false;
    }

    // основная категория, дефолтное поведение — тонкая обёртка над QbitAddMagnetEx (EpisodeHunter.cs)
    static Task<bool> QbitAddMagnet(HttpClient c, string magnet) => QbitAddMagnetEx(c, magnet, ModInit.conf.category);
    #endregion

    #region /qdl/notifications — уведомления о докачавшихся сериях отслеживаемых сериалов
    // серия без явного маркера сезона в имени файла («… 13.mkv») получает сезон из доминирующего:
    // иначе основная (S02E13 → s2e13) и донор ([Group] Show - 13 → e13) дают разные epkey и дублируют уведомление
    static Ep NormSeason(Ep e, int dom)
    {
        if (e != null && e.any && e.kind == null && e.season < 0 && dom > 0) e.season = dom;
        return e;
    }

    // стабильный ключ сериала (переживает смену infohash при re-grab): TMDB id, иначе хэш link
    static string SeriesKey(int seriesId, string link)
    {
        if (seriesId > 0) return "t" + seriesId;
        string s = link ?? "";
        uint h = 2166136261; foreach (char ch in s) { h ^= ch; h *= 16777619; }   // FNV-1a (стабилен между процессами, в отличие от String.GetHashCode)
        return "l" + h.ToString("x8");
    }

    // стабильный ключ серии для дедупа
    static string EpKey(Ep e)
    {
        if (e == null || !e.any) return null;
        if (e.kind == "RANGE") return "r" + e.ep + "-" + e.ep2;
        if (e.kind != null) return e.kind.ToLowerInvariant() + (e.ep >= 0 ? e.ep.ToString() : "");
        return (e.season >= 0 ? "s" + e.season : "") + "e" + e.ep;
    }

    // Серия уже «виденная», даже если её ключ раньше писался БЕЗ сезона. Файл «Show.S02.E07.mkv»
    // до фикса ParseEp (разделитель между S## и E##) давал season=-1 → epkey «e7», после фикса —
    // «s2e7». Без этой эквивалентности первый же проход после деплоя счёл бы все такие серии
    // новыми и выдал залп уведомлений о том, что зритель давно посмотрел.
    static bool SeenAlready(HashSet<string> seenKeys, Ep e, string key)
    {
        if (key == null) return false;
        if (seenKeys.Contains(key)) return true;
        return e != null && e.kind == null && e.season >= 0 && e.ep >= 0 && seenKeys.Contains("e" + e.ep);
    }

    // человекочитаемая подпись серии
    static string EpLabel(Ep e)
    {
        if (e == null || !e.any) return null;
        if (e.kind == "RANGE") return "Серии " + e.ep + "–" + e.ep2;
        if (e.kind != null) return e.kind + (e.ep >= 0 ? " " + e.ep : "");
        if (e.season >= 0 && e.ep >= 0) return "Сезон " + e.season + " · серия " + e.ep;
        if (e.ep >= 0) return "Серия " + e.ep;
        return null;
    }

    // что считаем «серией» для уведомления (экстры OP/ED/PV/NCOP… учитываем в seen, но не шумим)
    static bool IsEpisodeLike(Ep e)
    {
        if (e == null || !e.any) return false;
        if (e.kind == null) return e.ep >= 0;
        switch (e.kind) { case "RANGE": case "OVA": case "ONA": case "OAD": case "SP": return true; default: return false; }
    }

    // baseline: запомнить все серии, присутствующие на момент включения слежения (без уведомлений)
    static async Task SeedBaseline(string seriesKey, string hash)
    {
        try
        {
            using var c = await Qbit();
            string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
            var files = JArray.Parse(filesRaw);
            using var db = new SqlContext();
            var existing = new HashSet<string>(db.seen.Where(x => x.seriesKey == seriesKey).Select(x => x.epkey));
            int dom = DominantSeason(files);
            foreach (var f in files)
            {
                if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
                string key = EpKey(NormSeason(ParseEp(BaseNoExt(f)), dom));
                if (key == null || !existing.Add(key)) continue;
                db.seen.Add(new SeenModel { seriesKey = seriesKey, epkey = key });
            }
            db.SaveChanges();
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] seed baseline: " + ex); }
    }

    // Уведомление «началась загрузка» (kind=START). Зовут контуры, которые ТОЛЬКО ЧТО поставили
    // раздачу на закачку (re-grab в CheckWatches, донор из охоты) — номера серий там ещё не
    // известны, поэтому ep обычно null и текст общий; докачавшиеся серии придут отдельно из
    // ScanEpisodeNotifications.
    // Дедуп — строкой в той же таблице seen: epkey = "start:<btih>:<ep|all>" (с EpKey-ключами не
    // пересекается). Возвращает true, если запись создана.
    internal static bool AddStartNotification(int seriesId, string link, string hash, string title, string ep = null)
    {
        try
        {
            if (!ValidHash(hash)) return false;
            string sk = SeriesKey(seriesId, link);
            if (string.IsNullOrEmpty(sk)) return false;
            string dedup = StartKey(hash, ep);

            using var db = new SqlContext();
            // анти-флуд: у сериала ещё нет базы отсечения (слежение только что включили, seen пуст) —
            // молчим, иначе стартовое добавление раздачи само себе пришлёт уведомление
            if (!db.seen.Any(x => x.seriesKey == sk)) return false;
            if (db.seen.Any(x => x.seriesKey == sk && x.epkey == dedup)) return false;

            db.seen.Add(new SeenModel { seriesKey = sk, epkey = dedup });
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = seriesId, hash = hash, title = title ?? "",
                season = -1, episode = -1, kind = "START", epkey = dedup,
                label = "раздача обновилась, качаются новые серии", created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            Console.WriteLine("[QbitDownload] notify (start): " + (title ?? "") + " — " + hash);
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] notify start: " + ex.Message); return false; }
    }

    // ключ дедупа START-уведомлений (общий для всех контуров, которые их создают)
    internal static string StartKey(string hash, string ep)
        => "start:" + (hash ?? "").ToLowerInvariant() + ":" + (string.IsNullOrWhiteSpace(ep) ? "all" : ep);

    // основной сканер: для каждой отслеживаемой раздачи — новые докачавшиеся серии → записи в noti
    public static async Task<int> ScanEpisodeNotifications()
    {
        if (!await _watchGate.WaitAsync(0))   // общий фоновый гейт (был _scanning)
        {
            Console.WriteLine("[QbitDownload] noti scan: тик пропущен (gate занят)");
            return 0;
        }
        int created = 0;
        try
        {
            JArray list; HashSet<string> orig;
            lock (_watchLock) { list = LoadWatch(); orig = WatchHashes(list); }
            if (list.Count == 0) return 0;

            using var c = await Qbit();
            using var db = new SqlContext();

            // run-scoped дедуп: два watch-элемента одного сериала (два рипа, оба watched) делят seriesKey;
            // без этого оба стейджат одну (sk,epkey) → SaveChanges падает на UNIQUE-индексе и откатывает ВСЮ
            // пачку уведомлений. seenKeys грузятся ДО персиста, поэтому нужен общий на прогон набор.
            var staged = new HashSet<string>();
            // основные хэши сериалов с реально созданными уведомлениями: бамп активности (карточка всплывает
            // в «Загрузках») — строго ПОСЛЕ успешного SaveChanges, откат пачки не должен двигать карточки
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool StageSeen(string sk2, string key2)
            {
                if (!staged.Add("S|" + sk2 + "|" + key2)) return false;
                db.seen.Add(new SeenModel { seriesKey = sk2, epkey = key2 });
                return true;
            }
            bool StageNoti(string sk2, string key2) => staged.Add("N|" + sk2 + "|" + key2);

            foreach (var m in list)
            {
                try
                {
                    string hash = m.Value<string>("hash");
                    if (!ValidHash(hash)) continue;
                    int seriesId = m.Value<int?>("id") ?? 0;
                    string title = m.Value<string>("title") ?? "";
                    string sk = SeriesKey(seriesId, m.Value<string>("link"));

                    string filesRaw;
                    try { filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}"); }
                    catch { continue; }
                    JArray files;
                    try { files = JArray.Parse(filesRaw); } catch { continue; }
                    if (files.Count == 0) continue;

                    var seenKeys = new HashSet<string>(db.seen.Where(x => x.seriesKey == sk).Select(x => x.epkey));
                    bool baseline = seenKeys.Count == 0;   // первый проход (или старая запись до фичи) → только база, без уведомлений
                    int dom = DominantSeason(files);

                    foreach (var f in files)
                    {
                        if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
                        var ep = NormSeason(ParseEp(BaseNoExt(f)), dom);
                        string key = EpKey(ep);
                        if (key == null || SeenAlready(seenKeys, ep, key)) continue;

                        if (baseline) { StageSeen(sk, key); seenKeys.Add(key); continue; }

                        double progress = f.Value<double?>("progress") ?? 0;
                        if (progress < 0.999) continue;   // серия ещё качается

                        if (IsEpisodeLike(ep) && StageNoti(sk, key))
                        {
                            db.noti.Add(new NotiModel
                            {
                                seriesKey = sk, seriesId = seriesId, hash = hash, title = title,
                                season = ep.season, episode = ep.ep, kind = ep.kind, epkey = key,
                                label = EpLabel(ep), created = DateTime.UtcNow, read = false
                            });
                            created++;
                            touched.Add(hash);
                            Console.WriteLine("[QbitDownload] notify: " + title + " — " + EpLabel(ep));
                        }
                        StageSeen(sk, key);
                        seenKeys.Add(key);
                    }

                    // серии, докачавшиеся у ДОНОРОВ (охота по всем раздачам): уведомление с пометкой.
                    // hash в noti — ОСНОВНОЙ (openNotification ищет карточку по нему). На baseline-проходе
                    // доноров пропускаем: их серии не должны молча осесть в базе без уведомления.
                    var donors = m["donors"] as JArray;
                    if (!baseline && donors != null)
                        foreach (var d in donors.OfType<JObject>())
                        {
                            string dh = d.Value<string>("hash");
                            if (!ValidHash(dh)) continue;
                            var dfiles = await QbitFiles(c, dh);
                            if (dfiles == null) continue;
                            int ddom = DominantSeason(dfiles);
                            foreach (var f in dfiles)
                            {
                                if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
                                if ((f.Value<double?>("progress") ?? 0) < 0.999) continue;   // серия ещё качается (или prio 0)
                                var ep = NormSeason(ParseEp(BaseNoExt(f)), ddom > 0 ? ddom : dom);
                                string key = EpKey(ep);
                                if (key == null || SeenAlready(seenKeys, ep, key)) continue;
                                if (IsEpisodeLike(ep) && StageNoti(sk, key))
                                {
                                    db.noti.Add(new NotiModel
                                    {
                                        seriesKey = sk, seriesId = seriesId, hash = hash, title = title,
                                        season = ep.season, episode = ep.ep, kind = ep.kind, epkey = key,
                                        label = EpLabel(ep) + " · временно с другой раздачи", created = DateTime.UtcNow, read = false
                                    });
                                    created++;
                                    touched.Add(hash);   // hash здесь — ОСНОВНОЙ (как и в noti): всплывает карточка сериала
                                    Console.WriteLine("[QbitDownload] notify (donor): " + title + " — " + EpLabel(ep));
                                }
                                StageSeen(sk, key);
                                seenKeys.Add(key);
                            }
                        }

                    // оверлей-транскод: докачавшиеся серии автоматически конвертируем в mp4 (см. §транскод сериалов)
                    try { await AutoTranscodeOverlay(c, hash, files); }
                    catch (Exception ex) { Console.WriteLine("[QbitDownload] auto-transcode: " + ex.Message); }
                }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] noti scan item: " + ex); }
            }

            try
            {
                db.SaveChanges();
                foreach (var th in touched) ActivityTouch(th);
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] noti save: " + ex); }

            if (created > 0) PushNotiSignal(created);   // не ждём следующего опроса колокольчика

            // замещение: основная догнала и докачала свою версию серии → файл донора убираем,
            // опустевшие/мёртвые доноры удаляем целиком (EpisodeHunter.ScanReplacements)
            try { await ScanReplacements(c, list, orig); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] replacements: " + ex); }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] noti scan: " + ex); }
        finally { _watchGate.Release(); }
        return created;
    }

    // Мгновенный сигнал по /nws: «есть новые уведомления — дёрни /qdl/notifications».
    // Источником истины остаётся HTTP-опрос, по сокету едет только счётчик, поэтому потеря сигнала
    // (Sync выключен → Startup.Nws == null, клиент без сокета) ничего не ломает.
    // Рассылаем по ВСЕМ соединениям: NwsEvents.SendAsync адресует по uid, а уведомления общие.
    internal static void PushNotiSignal(int count)
    {
        try
        {
            var nws = Startup.Nws;
            if (nws == null) return;
            var conns = nws.AllConnections();
            if (conns == null || conns.IsEmpty) return;

            string data = "{\"count\":" + count + "}";
            foreach (var kv in conns)
            {
                try { _ = nws.SendAsync(kv.Key, "event", "", "qdl_noti", data); } catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] noti push: " + ex.Message); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/notifications")]
    public ActionResult Notifications()
    {
        try
        {
            using var db = new SqlContext();
            var items = db.noti.OrderByDescending(x => x.Id).Take(200).ToList();
            int unread = db.noti.Count(x => !x.read);
            var arr = new JArray();
            foreach (var n in items)
                arr.Add(new JObject
                {
                    ["id"] = n.Id, ["seriesId"] = n.seriesId, ["hash"] = n.hash, ["title"] = n.title,
                    ["season"] = n.season, ["episode"] = n.episode, ["kind"] = n.kind, ["label"] = n.label,
                    ["created"] = DateTime.SpecifyKind(n.created, DateTimeKind.Utc).ToString("o"), ["read"] = n.read   // помечаем UTC → корректный парсинг на фронте
                });
            return ContentTo(new JObject { ["items"] = arr, ["unread"] = unread }.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] notifications: " + ex); return ContentTo("{\"items\":[],\"unread\":0}", "application/json; charset=utf-8"); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/notifications/read")]
    public ActionResult NotificationsRead(long id = 0)
    {
        try
        {
            using var db = new SqlContext();
            if (id > 0) db.noti.Where(x => x.Id == id && !x.read).ExecuteUpdate(s => s.SetProperty(x => x.read, true));
            else db.noti.Where(x => !x.read).ExecuteUpdate(s => s.SetProperty(x => x.read, true));
            int unread = db.noti.Count(x => !x.read);
            return Json(new { success = true, unread });
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] notifications read: " + ex); return Json(new { success = false }); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/notifications/clear")]
    public ActionResult NotificationsClear()
    {
        try { using var db = new SqlContext(); db.noti.ExecuteDelete(); return Json(new { success = true }); }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] notifications clear: " + ex); return Json(new { success = false }); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/notifications/scan")]
    async public Task<ActionResult> NotificationsScan() { int n = await ScanEpisodeNotifications(); return Json(new { success = true, created = n }); }
    #endregion

    #region /qdl/collections — коллекции фильмов в «Загрузках» (общие для всех клиентов)
    // Хранение — collections.json в cachePath (по образцу watch.json): SQLite не подходит,
    // EnsureCreated не добавляет таблицы в существующую БД. Инварианты: фильм максимум в ОДНОЙ
    // коллекции (add переносит), cover ∈ hashes, пустая коллекция удаляется.
    static string CollectionsFile => Path.Combine(ModInit.conf.cachePath, "collections.json");
    static readonly object _colLock = new();   // отдельный от _watchLock, вложенно не брать
    // id = "c" + Guid("N"): без префикса 32 hex-символа Guid формально матчат _hashRx
    static readonly Regex _colIdRx = new Regex("^c[0-9a-f]{32}$", RegexOptions.Compiled);
    static bool ValidColId(string id) => !string.IsNullOrEmpty(id) && _colIdRx.IsMatch(id);

    static JArray LoadCollections()
    {
        try { if (System.IO.File.Exists(CollectionsFile)) return JArray.Parse(System.IO.File.ReadAllText(CollectionsFile)); } catch { }
        return new JArray();
    }
    static void SaveCollections(JArray a)
    {
        try { Directory.CreateDirectory(ModInit.conf.cachePath); System.IO.File.WriteAllText(CollectionsFile, a.ToString(Newtonsoft.Json.Formatting.None)); } catch { }
    }

    static JObject FindCollection(JArray a, string id)
    {
        foreach (var t in a)
            if (t is JObject col && col.Value<string>("id") == id) return col;
        return null;
    }

    // Убрать хэш из всех коллекций массива: чинит cover, удаляет опустевшие. true = были изменения.
    static bool RemoveHashFrom(JArray a, string hash)
    {
        bool changed = false;
        for (int i = a.Count - 1; i >= 0; i--)
        {
            if (a[i] is not JObject col || col["hashes"] is not JArray hs) continue;
            int before = hs.Count;
            for (int j = hs.Count - 1; j >= 0; j--)
                if (hs[j].Value<string>() == hash) hs.RemoveAt(j);
            if (hs.Count == before) continue;
            changed = true;
            if (hs.Count == 0) { a.RemoveAt(i); continue; }
            if (col.Value<string>("cover") == hash) col["cover"] = hs[0].Value<string>();
        }
        return changed;
    }

    static string TitleFromMeta(string hash)
    {
        try
        {
            if (ValidHash(hash) && System.IO.File.Exists(MetaPath(hash)))
            {
                var m = JObject.Parse(System.IO.File.ReadAllText(MetaPath(hash)));
                string t = m.Value<string>("title") ?? m.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(t)) return t.Trim();
            }
        }
        catch { }
        return null;
    }

    static string NormColTitle(string title, string fallbackHash)
    {
        if (string.IsNullOrWhiteSpace(title)) title = TitleFromMeta(fallbackHash) ?? "Коллекция";
        title = title.Trim();
        return title.Length > 120 ? title.Substring(0, 120) : title;
    }

    // null = невалидные данные. Первый хэш = обложка («первый добавленный фильм»).
    static JObject ColCreate(string title, string[] hashes)
    {
        if (hashes == null) return null;
        hashes = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).Distinct().ToArray();
        if (hashes.Length < 2 || hashes.Any(h => !ValidHash(h))) return null;

        lock (_colLock)
        {
            var a = LoadCollections();
            foreach (var h in hashes) RemoveHashFrom(a, h);   // 1 фильм — 1 коллекция
            var col = new JObject
            {
                ["id"] = "c" + Guid.NewGuid().ToString("N"),
                ["title"] = NormColTitle(title, hashes[0]),
                ["cover"] = hashes[0],
                ["hashes"] = new JArray(hashes),
                ["created"] = DateTime.UtcNow.ToString("o")
            };
            a.Add(col);
            SaveCollections(a);
            return col;
        }
    }

    static bool ColAdd(string id, string hash)
    {
        lock (_colLock)
        {
            var a = LoadCollections();
            var col = FindCollection(a, id);
            if (col == null || col["hashes"] is not JArray hs) return false;
            if (hs.Any(x => x.Value<string>() == hash)) return true;   // уже внутри — no-op
            RemoveHashFrom(a, hash);                                   // перенос из другой коллекции
            hs.Add(hash);
            if (string.IsNullOrEmpty(col.Value<string>("cover"))) col["cover"] = hash;
            SaveCollections(a);
            return true;
        }
    }

    // deleted = true → убрали последний фильм, коллекция удалена
    static (bool ok, bool deleted) ColRemove(string id, string hash)
    {
        lock (_colLock)
        {
            var a = LoadCollections();
            var col = FindCollection(a, id);
            if (col == null || col["hashes"] is not JArray hs) return (false, false);
            int before = hs.Count;
            for (int j = hs.Count - 1; j >= 0; j--)
                if (hs[j].Value<string>() == hash) hs.RemoveAt(j);
            if (hs.Count == before) return (true, false);   // хэша и не было
            bool deleted = hs.Count == 0;
            if (deleted) a.Remove(col);
            else if (col.Value<string>("cover") == hash) col["cover"] = hs[0].Value<string>();
            SaveCollections(a);
            return (true, deleted);
        }
    }

    // title и/или cover; cover обязан быть из hashes коллекции
    static bool ColUpdate(string id, string title, string cover)
    {
        lock (_colLock)
        {
            var a = LoadCollections();
            var col = FindCollection(a, id);
            if (col == null || col["hashes"] is not JArray hs) return false;
            if (!string.IsNullOrWhiteSpace(cover))
            {
                if (!ValidHash(cover) || !hs.Any(x => x.Value<string>() == cover)) return false;
                col["cover"] = cover;
            }
            if (!string.IsNullOrWhiteSpace(title)) col["title"] = NormColTitle(title, col.Value<string>("cover"));
            SaveCollections(a);
            return true;
        }
    }

    static bool ColDissolve(string id)
    {
        lock (_colLock)
        {
            var a = LoadCollections();
            var col = FindCollection(a, id);
            if (col == null) return false;
            a.Remove(col);
            SaveCollections(a);
            return true;
        }
    }

    // хук из PurgeCache: удалённый фильм исчезает из коллекций
    static void CollectionsRemoveHash(string hash)
    {
        try { lock (_colLock) { var a = LoadCollections(); if (RemoveHashFrom(a, hash)) SaveCollections(a); } }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] collections purge: " + ex.Message); }
    }

    // хук из MigrateCache: re-grab сериала не выкидывает его из коллекции
    static void CollectionsMigrateHash(string oldH, string newH)
    {
        try
        {
            lock (_colLock)
            {
                var a = LoadCollections();
                bool changed = false;
                foreach (var t in a)
                {
                    if (t is not JObject col || col["hashes"] is not JArray hs) continue;
                    for (int j = 0; j < hs.Count; j++)
                        if (hs[j].Value<string>() == oldH) { hs[j] = newH; changed = true; }
                    if (col.Value<string>("cover") == oldH) { col["cover"] = newH; changed = true; }
                }
                if (changed) SaveCollections(a);
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] collections migrate: " + ex.Message); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/collections")]
    public ActionResult CollectionsList()
    {
        try
        {
            JArray a;
            lock (_colLock) { a = LoadCollections(); }
            return ContentTo(a.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] collections: " + ex);
            return Json(new { error = "internal error" });
        }
    }

    [HttpPost, AllowAnonymous]
    [Route("qdl/collections/create")]
    public ActionResult CollectionsCreate(string title = null, string hashes = null)
    {
        try
        {
            var col = ColCreate(title, (hashes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (col == null) return BadRequest(new { error = "need >=2 valid hashes" });
            // JObject нельзя класть в Json(): System.Text.Json сериализует JToken в мусор ("id":[])
            return ContentTo("{\"success\":true,\"collection\":" + col.ToString(Newtonsoft.Json.Formatting.None) + "}", "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] collections create: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }

    [HttpPost, AllowAnonymous]
    [Route("qdl/collections/add")]
    public ActionResult CollectionsAdd(string id, string hash)
    {
        if (!ValidColId(id)) return BadRequest(new { error = "invalid id" });
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try { return Json(new { success = ColAdd(id, hash) }); }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] collections add: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }

    [HttpPost, AllowAnonymous]
    [Route("qdl/collections/remove")]
    public ActionResult CollectionsRemove(string id, string hash)
    {
        if (!ValidColId(id)) return BadRequest(new { error = "invalid id" });
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            var (ok, deleted) = ColRemove(id, hash);
            return Json(new { success = ok, deleted });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] collections remove: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }

    [HttpPost, AllowAnonymous]
    [Route("qdl/collections/update")]
    public ActionResult CollectionsUpdate(string id, string title = null, string cover = null)
    {
        if (!ValidColId(id)) return BadRequest(new { error = "invalid id" });
        try { return Json(new { success = ColUpdate(id, title, cover) }); }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] collections update: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }

    [HttpPost, AllowAnonymous]
    [Route("qdl/collections/dissolve")]
    public ActionResult CollectionsDissolve(string id)
    {
        if (!ValidColId(id)) return BadRequest(new { error = "invalid id" });
        try { return Json(new { success = ColDissolve(id) }); }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] collections dissolve: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
    #endregion

    #region activity — «актуальность» карточки (сортировка «Загрузок» по последнему событию загрузки)
    // Ключ сортировки грида — не только added_on: охота добавляет серию донором (другая категория qBit,
    // added_on основной не меняется), докачка серии видна лишь сканеру уведомлений. Здесь — персистентный
    // штамп «последней загрузки» по ОСНОВНОМУ hash: {lowercase infohash: unix seconds}. Транскод и jut.su
    // сюда не пишут намеренно (транскод позицию не меняет — §AG; jut двигает added маркера сам).
    static string ActivityFile => Path.Combine(ModInit.conf.cachePath, "activity.json");
    static readonly object _activityLock = new();

    static JObject ActivityLoad()
    {
        try { if (System.IO.File.Exists(ActivityFile)) return JObject.Parse(System.IO.File.ReadAllText(ActivityFile)); } catch { }
        return new JObject();
    }
    static void ActivitySave(JObject a)
    {
        try { Directory.CreateDirectory(ModInit.conf.cachePath); System.IO.File.WriteAllText(ActivityFile, a.ToString(Newtonsoft.Json.Formatting.None)); } catch { }
    }

    // ts <= 0 → сейчас. Монотонный: запоздавший Touch не откатывает более свежий.
    // Один метод с default-параметром, НЕ перегрузки (тестовый Access.Call матчит по числу аргументов).
    internal static void ActivityTouch(string hash, long ts = 0)
    {
        if (!ValidHash(hash)) return;
        hash = hash.ToLowerInvariant();
        if (ts <= 0) ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_activityLock)
        {
            var a = ActivityLoad();
            if ((a.Value<long?>(hash) ?? 0) >= ts) return;
            a[hash] = ts;
            ActivitySave(a);
        }
    }

    static long ActivityStored(JObject snapshot, string hash)
        => snapshot?.Value<long?>((hash ?? "").ToLowerInvariant()) ?? 0;

    // Активность карточки: max(added, completion_on при валидности, сохранённый touch).
    // completion_on берём только у докачанных (progress >= 0.999): у недокачанных qBit отдаёт мусор
    // (-1 / 4294967295), а значение из будущего (потолок now+сутки) прибило бы карточку к топу навсегда.
    internal static long CardActivity(long added, long completionOn, double progress, long stored, long now)
    {
        long act = Math.Max(added, stored);
        if (progress >= 0.999 && completionOn > 0 && completionOn != 4294967295L && completionOn <= now + 86400)
            act = Math.Max(act, completionOn);
        return act;
    }

    internal static void ActivityRemove(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return;
        hash = hash.ToLowerInvariant();
        lock (_activityLock)
        {
            var a = ActivityLoad();
            if (a.Remove(hash)) ActivitySave(a);
        }
    }

    // re-grab/switch: штамп переезжает на новый hash; при коллизии побеждает более свежий
    internal static void ActivityMigrate(string oldH, string newH)
    {
        if (string.IsNullOrEmpty(oldH) || string.IsNullOrEmpty(newH)) return;
        oldH = oldH.ToLowerInvariant(); newH = newH.ToLowerInvariant();
        lock (_activityLock)
        {
            var a = ActivityLoad();
            long ov = a.Value<long?>(oldH) ?? 0;
            if (ov == 0) return;
            if ((a.Value<long?>(newH) ?? 0) < ov) a[newH] = ov;
            a.Remove(oldH);
            ActivitySave(a);
        }
    }

    // Ключи без живой карточки: грейс 7 суток, а не сразу — основная может временно числиться
    // в донорской категории до PromoteIfDonor (самолечение ≤ 6 ч) и не попасть в liveHashes.
    internal static void ActivityPrune(HashSet<string> liveHashes, long now)
    {
        lock (_activityLock)
        {
            var a = ActivityLoad();
            var dead = a.Properties()
                .Where(p => !liveHashes.Contains(p.Name) && (p.Value.Value<long?>() ?? 0) < now - 7 * 86400)
                .Select(p => p.Name).ToList();
            if (dead.Count == 0) return;
            foreach (var k in dead) a.Remove(k);
            ActivitySave(a);
        }
    }
    #endregion

    #region helpers
    static string MetaPath(string hash) => Path.Combine(ModInit.conf.cachePath, "meta", hash + ".json");
    static string PosterPath(string hash) => Path.Combine(ModInit.conf.cachePath, "img", hash + ".jpg");

    // локальный (не-торрент) файл: транскод занял место раздачи, КЛЮЧ — тот же infohash,
    // поэтому meta/постер/привязка к карточке продолжают работать без миграции
    static string LocalPath(string hash) => Path.Combine(ModInit.conf.cachePath, "local", hash + ".json");

    // фолбэк даты загрузки для маркеров без поля added (созданы руками/битые)
    static long MarkerFallbackAdded(string markerPath)
    {
        try { return new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(markerPath)).ToUnixTimeSeconds(); }
        catch { return 0; }
    }

    static JObject LoadLocal(string hash)
    {
        try
        {
            if (ValidHash(hash) && System.IO.File.Exists(LocalPath(hash)))
                return JObject.Parse(System.IO.File.ReadAllText(LocalPath(hash)));
        }
        catch { }
        return null;
    }

    // один файл локального маркера (после транскода)
    sealed class LocalFile { public int index; public string name; public string path; public long size; }

    // Нормализация обоих форматов маркера: старый {name,path,size,added} → один файл (index 0),
    // новый {files:[{index,name,path,size}],...} → как есть. Существование на диске НЕ проверяется.
    static List<LocalFile> LocalFiles(JObject loc)
    {
        var res = new List<LocalFile>();
        if (loc == null) return res;
        if (loc["files"] is JArray arr)
        {
            foreach (var f in arr)
            {
                string p = f.Value<string>("path");
                if (string.IsNullOrEmpty(p)) continue;
                res.Add(new LocalFile
                {
                    index = f.Value<int?>("index") ?? res.Count,
                    name = f.Value<string>("name") ?? Path.GetFileName(p),
                    path = p,
                    size = f.Value<long?>("size") ?? 0
                });
            }
        }
        else
        {
            string p = loc.Value<string>("path");
            if (!string.IsNullOrEmpty(p))
                res.Add(new LocalFile { index = 0, name = Path.GetFileName(p), path = p, size = loc.Value<long?>("size") ?? 0 });
        }
        return res;
    }

    // Оверлей: торрент ЖИВ (слежение продолжается), files — транскод-копии отдельных серий.
    // false/нет поля = финальный маркер (торрент удалён, карточка живёт только маркером).
    static bool LocalIsOverlay(JObject loc) => loc?.Value<bool?>("overlay") == true;

    // выбор файла маркера: по index, иначе самый большой; null если файла нет на диске
    static LocalFile PickLocal(List<LocalFile> files, int index)
    {
        LocalFile pick = null;
        if (index >= 0)
            foreach (var f in files)
                if (f.index == index) { pick = f; break; }
        if (pick == null)
            foreach (var f in files)
                if (pick == null || f.size > pick.size) pick = f;
        return (pick != null && System.IO.File.Exists(pick.path)) ? pick : null;
    }

    // база имени файла без видеорасширения + те же замены недопустимых символов, что при создании
    // mp4-выхода — стабильный ключ соответствия «торрент-файл ↔ транскод-копия» (расширение меняется)
    static string SafeFileBase(string fileName)
    {
        string n = (fileName ?? "").Replace('\\', '/');
        n = n.Substring(n.LastIndexOf('/') + 1);
        n = Regex.Replace(n, "\\.(mkv|mp4|avi|ts|m2ts|webm|mov|m4v)$", "", RegexOptions.IgnoreCase);
        foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
        return n;
    }

    // оверлей: транскод-копия для торрент-файла (по стабильной базе имени); null если серии ещё нет
    static LocalFile OverlayFor(List<LocalFile> files, string torrentFileName)
    {
        if (files == null || files.Count == 0) return null;
        string key = SafeFileBase(torrentFileName);
        foreach (var f in files)
            if (SafeFileBase(f.name) == key && System.IO.File.Exists(f.path))
                return f;
        return null;
    }

    // удалить все файлы маркера + опустевшую папку сериала
    static void DeleteLocalFiles(JObject loc)
    {
        foreach (var f in LocalFiles(loc))
            try { if (System.IO.File.Exists(f.path)) System.IO.File.Delete(f.path); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] delete local file: " + ex.Message); }
        try
        {
            string dir = loc?.Value<string>("dir");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { }
    }

    // Полная уборка следов раздачи после удаления: файлы кэша, запись watch.json, seen/noti в qdl.db.
    // ПОРЯДОК ВАЖЕН: seriesKey вычисляется из меты/линка ДО удаления файлов. seen/noti по seriesKey
    // не трогаем, если тот же сериал ещё отслеживается ДРУГОЙ живой раздачей (re-grab дубль).
    // ⚠ Вызывать ТОЛЬКО из /qdl/delete: в RunTranscode local-файл наследует hash (мета обязана выжить),
    //   а re-grab переносит кэш через MigrateCache.
    static void PurgeCache(string hash)
    {
        try
        {
            // 1) исходники для seriesKey — пока файлы живы
            int seriesId = 0; string link = null;
            try { if (System.IO.File.Exists(MetaPath(hash))) seriesId = JObject.Parse(System.IO.File.ReadAllText(MetaPath(hash))).Value<int?>("id") ?? 0; } catch { }
            try { if (System.IO.File.Exists(LinkPath(hash))) link = JObject.Parse(System.IO.File.ReadAllText(LinkPath(hash))).Value<string>("link"); } catch { }

            // 2) watch.json: убрать запись раздачи; по остатку понять, жив ли seriesKey у другой раздачи
            string sk; bool skAlive = false;
            lock (_watchLock)
            {
                var a = LoadWatch(); var b = new JArray();
                foreach (var m in a)
                {
                    if (m.Value<string>("hash") == hash)
                    {
                        if (string.IsNullOrEmpty(link)) link = m.Value<string>("link");
                        if (seriesId == 0) seriesId = m.Value<int?>("id") ?? 0;
                        continue;
                    }
                    b.Add(m);
                }
                if (b.Count != a.Count) SaveWatch(b);
                sk = SeriesKey(seriesId, link);
                foreach (var m in b)
                    if (SeriesKey(m.Value<int?>("id") ?? 0, m.Value<string>("link")) == sk) { skAlive = true; break; }
            }

            // 3) qdl.db: noti этой раздачи — всегда; seen/noti сериала — только если сериал мёртв.
            //    Гард от вырожденного ключа: без id и link SeriesKey(0,null)=FNV("") — чужие записи не трогаем
            try
            {
                using var db = new SqlContext();
                db.noti.Where(x => x.hash == hash).ExecuteDelete();
                if (!skAlive && (seriesId > 0 || !string.IsNullOrEmpty(link)))
                {
                    db.seen.Where(x => x.seriesKey == sk).ExecuteDelete();
                    db.noti.Where(x => x.seriesKey == sk).ExecuteDelete();
                }
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] purge db: " + ex.Message); }

            // 4) коллекции: убрать фильм, опустевшие удалить
            CollectionsRemoveHash(hash);
            ActivityRemove(hash);

            // 5) файловые артефакты — в последнюю очередь
            foreach (var p in new[] { MetaPath(hash), PosterPath(hash), LinkPath(hash), LocalPath(hash) })
                try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] purge: " + ex.Message); }
    }

    // постер не должен указывать на loopback/приватную сеть (анти-SSRF для внешних картинок)
    static bool IsPrivateHost(Uri u)
    {
        string h = u.Host.ToLowerInvariant();
        if (h == "localhost" || h == "127.0.0.1" || h == "::1" || h == "0.0.0.0") return true;
        if (System.Net.IPAddress.TryParse(u.Host, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || b[0] == 127;
            if (ip.IsIPv6LinkLocal || System.Net.IPAddress.IsLoopback(ip)) return true;
        }
        return false;
    }

    // Разрешаем фетчить только собственный JacRed-резолвер (loopback / наш listen-хост, наш порт)
    bool IsSelfResolver(Uri u)
    {
        if (u == null) return false;
        if (u.Scheme != "http" && u.Scheme != "https") return false;
        if (u.Port != CoreInit.conf.listen.port) return false;

        string h = u.Host.ToLowerInvariant();
        if (h == "127.0.0.1" || h == "localhost" || h == "::1") return true;
        if (!string.IsNullOrEmpty(CoreInit.conf.listen.localhost) && h == CoreInit.conf.listen.localhost.ToLowerInvariant()) return true;
        try { if (h == new Uri(host).Host.ToLowerInvariant()) return true; } catch { }
        return false;
    }

    // .torrent — это bencode-словарь: первый значимый байт = 'd' (0x64).
    static bool LooksLikeTorrent(byte[] data)
    {
        if (data == null || data.Length < 50) return false;
        int i = 0;
        while (i < data.Length && (data[i] == (byte)' ' || data[i] == (byte)'\t' || data[i] == (byte)'\r' || data[i] == (byte)'\n')) i++;
        return i < data.Length && data[i] == 0x64;
    }
    #endregion
}
