using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Роуты вкладки jut.su: каталог, поиск, тайтл, серии, resolve, постер, диагностика.
//
// Кеш свой (не Staticache): его ключ = Scheme+Host+Path+Query, и без queryKeys разные
// ?page= слиплись бы в один бакет (§AN). Диск нужен ещё и затем, чтобы каталог работал,
// когда jut.su лежит — «вчерашнее» с флагом stale лучше пустого экрана.
//
// Документация: E:\Media-server\claude\jut\
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region инфраструктура ответов и кеша

    ActionResult JutJson(JToken payload)
    {
        SetHeadersNoCache();
        return ContentTo(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    /// <summary>
    /// То же, плюс отметка версии постера и посев очереди апгрейда (JutSuPoster.cs).
    /// 🔥 Обязан стоять на ВСЕХ путях возврата каталога/поиска/тайтла, включая кеш и stale:
    /// свежий разбор случается раз в 30 минут, а карточки показываются постоянно.
    /// </summary>
    ActionResult JutJsonArt(JToken payload)
    {
        JutPosterStamp(payload);
        return JutJson(payload);
    }

    // Ошибку отдаём 200 + {"error":...} — как в Live.cs: клиент показывает текст на экране,
    // а не молчаливый «пустой список» от провалившегося XHR.
    ActionResult JutErr(string code, string msg = null)
        => JutJson(new JObject { ["ok"] = false, ["error"] = code, ["message"] = msg ?? JutMessage(code) });

    static string JutMessage(string code) => code switch
    {
        "NOT_AUTHORIZED" => "Требуется вход на jut.su — обновите куки на сервере",
        "NOT_FOUND" => "Не найдено на jut.su",
        "SITE_DOWN" => "jut.su недоступен",
        "PARSE" => "Не удалось разобрать страницу jut.su (сменилась вёрстка)",
        "DISABLED" => "Раздел jut.su выключен в настройках",
        "BAD_SLUG" => "Некорректный идентификатор аниме",
        _ => "Ошибка jut.su"
    };

    static bool JutOn => ModInit.conf?.jutEnable == true;

    // Двухуровневый кеш: память + JSON на томе qdl-data.
    sealed class JutCacheItem { public DateTime at; public string json; }
    static readonly ConcurrentDictionary<string, JutCacheItem> _jutMem = new();

    static string JutDir(string sub)
    {
        string d = Path.Combine(JutNet.JutDataDir(), sub);
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    static string JutKeyHash(string s)
    {
        // FNV-1a: ключ кеша из фильтров каталога, коллизии тут безобидны
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }

    static JObject JutCacheRead(string sub, string key, TimeSpan ttl, out bool stale)
    {
        stale = false;
        string memKey = sub + "/" + key;
        if (_jutMem.TryGetValue(memKey, out var mi) && DateTime.UtcNow - mi.at < ttl)
        {
            try { return JObject.Parse(mi.json); } catch { }
        }

        string path = Path.Combine(JutDir(sub), key + ".json");
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var age = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path);
            string body = System.IO.File.ReadAllText(path);
            var jo = JObject.Parse(body);
            _jutMem[memKey] = new JutCacheItem { at = DateTime.UtcNow - (age < ttl ? age : ttl), json = body };
            // Протухшее НЕ выбрасываем: если сайт лежит, отдадим его с "stale":true
            stale = age >= ttl;
            return jo;
        }
        catch { return null; }
    }

    static void JutCacheWrite(string sub, string key, JObject data)
    {
        string memKey = sub + "/" + key;
        string body = data.ToString(Newtonsoft.Json.Formatting.None);
        _jutMem[memKey] = new JutCacheItem { at = DateTime.UtcNow, json = body };
        try { System.IO.File.WriteAllText(Path.Combine(JutDir(sub), key + ".json"), body); } catch { }
    }

    #endregion

    #region каталог и поиск

    // Витрина вкладки = order-by-add (требование владельца).
    static readonly HashSet<string> _jutOrders = new(StringComparer.OrdinalIgnoreCase)
        { "add", "name", "count", "date", "rate" };

    static readonly System.Text.RegularExpressions.Regex _jutFilterRx =
        new(@"^[a-z0-9-]{1,80}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Склейка URL каталога: /anime/{жанры-через-дефис}/{годы-через-and}/order-by-X/page-N/</summary>
    static string JutCatalogUrl(int page, string genres, string years, string order)
    {
        var sb = new StringBuilder(JutNet.Host).Append("/anime/");
        if (!string.IsNullOrEmpty(genres)) sb.Append(genres).Append('/');
        if (!string.IsNullOrEmpty(years)) sb.Append(years).Append('/');
        if (!string.IsNullOrEmpty(order) && !order.Equals("rate", StringComparison.OrdinalIgnoreCase))
            sb.Append("order-by-").Append(order).Append('/');
        // ⚠️ ТОЛЬКО page-N/. Ловушка: ?page=2 отдаёт 200 и контент ПЕРВОЙ страницы,
        // а page/2/ и /2/ редиректят на /anime/ (сброс сортировки).
        if (page > 1) sb.Append("page-").Append(page).Append('/');
        return sb.ToString();
    }

    static JArray JutCardsJson(List<JutCard> cards) => new JArray(cards.Select(c => new JObject
    {
        ["slug"] = c.slug,
        ["id"] = c.id,
        ["title"] = c.titleRu,
        ["original"] = c.titleOrig,
        ["poster"] = c.poster,
        ["descr"] = c.descr,
        ["episodes"] = c.episodes,
        ["seasons"] = c.seasons,
        ["films"] = c.films,
        ["ongoing"] = c.ongoing,
        ["viewed"] = c.viewed,
        ["years"] = new JArray(c.years),
        ["genres"] = new JArray(c.genres)
    }));

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/catalog")]
    async public Task<ActionResult> JutCatalog(int page = 1, string genres = null, string years = null,
                                               string order = "add", int nocache = 0)
    {
        if (!JutOn) return JutErr("DISABLED");
        if (page < 1) page = 1;
        if (string.IsNullOrEmpty(order) || !_jutOrders.Contains(order)) order = "add";
        if (!string.IsNullOrEmpty(genres) && !_jutFilterRx.IsMatch(genres)) return JutErr("BAD_SLUG");
        if (!string.IsNullOrEmpty(years) && !_jutFilterRx.IsMatch(years)) return JutErr("BAD_SLUG");

        string key = JutKeyHash(order + "|" + genres + "|" + years) + "-p" + page;
        var ttl = TimeSpan.FromMinutes(Math.Max(1, ModInit.conf?.jutCatalogTtlMin ?? 30));

        if (nocache == 0)
        {
            var cached = JutCacheRead("catalog", key, ttl, out bool stale);
            if (cached != null && !stale) return JutJsonArt(cached);
        }

        string url = JutCatalogUrl(page, genres, years, order);
        // AJAX-форма того же URL: только карточки, вдвое легче полной страницы.
        var resp = await JutNet.PostForm(url,
            "ajax_load=yes&start_from_page=" + page + "&show_search=&anime_of_user=",
            JutSuParse.ReachedCatalog);

        if (resp == null || !resp.reached)
        {
            // Сайт лежит — отдаём последнее известное с честным флагом, а не пустой экран
            var stalePage = JutCacheRead("catalog", key, TimeSpan.MaxValue, out _);
            if (stalePage != null) { stalePage["stale"] = true; return JutJsonArt(stalePage); }
            return JutErr(resp?.error ?? "SITE_DOWN");
        }

        var parsed = JutSuParse.ParseCatalog(resp.body);
        var payload = new JObject
        {
            ["ok"] = true,
            ["page"] = page,
            ["order"] = order,
            ["hasNext"] = parsed.hasNext,
            ["total"] = parsed.items.Count,
            ["via"] = resp.exitId,
            ["stale"] = false,
            ["items"] = JutCardsJson(parsed.items)
        };
        if (parsed.items.Count > 0) JutCacheWrite("catalog", key, payload);
        return JutJsonArt(payload);
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/search")]
    async public Task<ActionResult> JutSearch(string query, int page = 1, int nocache = 0)
    {
        if (!JutOn) return JutErr("DISABLED");
        if (string.IsNullOrWhiteSpace(query)) return JutErr("NOT_FOUND", "Пустой запрос");
        if (page < 1) page = 1;
        query = query.Trim();
        if (query.Length > 100) query = query.Substring(0, 100);

        string key = JutKeyHash(query) + "-p" + page;
        var ttl = TimeSpan.FromMinutes(Math.Max(1, ModInit.conf?.jutSearchTtlMin ?? 10));
        if (nocache == 0)
        {
            var cached = JutCacheRead("search", key, ttl, out bool stale);
            if (cached != null && !stale) return JutJsonArt(cached);
        }

        // ⚠️ show_search обязан быть в UTF-8. В CP1251 сайт возвращает 0 результатов.
        // (А вот POST /search/ с полем ystext — наоборот, требует CP1251. Две разные кодировки
        //  у двух разных входов, проверено обоими способами — claude/jut/01-recon.md §3.)
        string form = "ajax_load=yes&start_from_page=" + page
                    + "&show_search=" + Uri.EscapeDataString(query) + "&anime_of_user=";
        var resp = await JutNet.PostForm(JutNet.Host + "/anime/", form, JutSuParse.ReachedCatalog);

        if (resp == null || !resp.reached)
        {
            // Пустая выдача поиска — валидный ответ: сайт возвращает только <script> без карточек.
            if (resp != null && resp.body != null && resp.body.Contains("anime_page_next"))
                return JutJson(new JObject { ["ok"] = true, ["page"] = page, ["hasNext"] = false,
                                             ["total"] = 0, ["items"] = new JArray() });
            return JutErr(resp?.error ?? "SITE_DOWN");
        }

        var parsed = JutSuParse.ParseCatalog(resp.body);
        var payload = new JObject
        {
            ["ok"] = true,
            ["page"] = page,
            ["query"] = query,
            ["hasNext"] = parsed.hasNext,
            ["total"] = parsed.items.Count,
            ["items"] = JutCardsJson(parsed.items)
        };
        JutCacheWrite("search", key, payload);
        return JutJsonArt(payload);
    }

    #endregion

    #region тайтл и серии

    static JObject JutTitleJson(JutTitle t) => new JObject
    {
        ["ok"] = true,
        ["slug"] = t.slug,
        ["id"] = t.id,
        ["title"] = t.titleRu,
        ["original"] = t.titleOrig,
        ["poster"] = t.poster,
        ["backdrop"] = t.backdrop,
        ["descr"] = t.descr,
        ["rating"] = t.rating,
        ["ratingCount"] = t.ratingCount,
        ["age"] = t.ageRating,
        ["ongoing"] = t.ongoing,
        ["genres"] = new JArray(t.genres),
        ["themes"] = new JArray(t.themes),
        ["years"] = new JArray(t.years),
        ["seasons"] = new JArray(t.items.Where(e => e.kind == JutEpKind.Episode)
                                         .Select(e => e.season).Distinct().OrderBy(x => x)),
        ["count"] = t.items.Count,
        ["items"] = new JArray(t.items.Select(e => new JObject
        {
            ["url"] = e.url,
            ["kind"] = e.kind.ToString().ToLowerInvariant(),
            ["season"] = e.season,
            ["ep"] = e.num,
            ["key"] = e.epkey,
            ["name"] = e.name,
            ["arc"] = e.arcRu,
            ["watched"] = e.watched,
            ["percent"] = e.percent
        }))
    };

    /// <summary>
    /// Тайтл + плоский список серий. Хаб-вёрстку (Наруто) разворачивает рекурсивно:
    /// там страница тайтла — это каталог разделов, ни одной кнопки серии.
    /// </summary>
    async Task<(JutTitle title, string error)> JutLoadTitle(string slug, bool nocache)
    {
        var resp = await JutNet.Get(JutNet.Host + "/" + slug + "/",
                                    h => JutSuParse.ReachedTitle(h) || JutSuParse.ReachedSection(h));
        if (resp == null || !resp.reached) return (null, resp?.error ?? "SITE_DOWN");

        var t = JutSuParse.ParseTitle(resp.body, slug);

        if (t.isHub)
        {
            // Обходим разделы: /naruuto/season-1/, /naruuto/ova/, /naruuto/film/ ...
            var all = new List<JutEp>();
            foreach (string section in t.hubSections.Take(24))
            {
                string u = section.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? section : JutNet.Host + section;
                var sr = await JutNet.Get(u, JutSuParse.ReachedSection);
                if (sr == null || !sr.reached) continue;
                all.AddRange(JutSuParse.ParseEpisodeList(JutSuParse.Strip(sr.body)));
            }
            // Дедуп: разделы могут пересекаться
            t.items = all.GroupBy(e => e.url).Select(g => g.First())
                         .OrderBy(e => e.kind).ThenBy(e => e.season).ThenBy(e => e.num).ToList();
        }

        return (t, null);
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/title")]
    [Route("qdl/jut/episodes")]
    async public Task<ActionResult> JutTitleRoute(string slug, int nocache = 0)
    {
        if (!JutOn) return JutErr("DISABLED");
        // ⚠️ slug идёт в пути на диске и в URL к сайту — гейт обязателен (прецеденты §L, §AX)
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");

        // Онгоингу нужен более свежий список серий, чем завершённому тайтлу
        var cached = JutCacheRead("title", slug, TimeSpan.MaxValue, out _);
        bool ongoing = cached?["ongoing"]?.Value<bool>() ?? false;
        var ttl = TimeSpan.FromHours(ongoing
            ? Math.Max(1, ModInit.conf?.jutOngoingTtlHours ?? 2)
            : Math.Max(1, ModInit.conf?.jutTitleTtlHours ?? 12));

        if (nocache == 0)
        {
            var fresh = JutCacheRead("title", slug, ttl, out bool stale);
            if (fresh != null && !stale) return JutJsonArt(fresh);
        }

        var (t, err) = await JutLoadTitle(slug, nocache != 0);
        if (t == null)
        {
            if (cached != null) { cached["stale"] = true; return JutJsonArt(cached); }
            return JutErr(err);
        }

        var payload = JutTitleJson(t);
        JutCacheWrite("title", slug, payload);
        return JutJsonArt(payload);
    }

    #endregion

    #region resolve — выбор качества и выдача токена

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/resolve")]
    async public Task<ActionResult> JutResolve(string slug, int season = 1, int ep = 1,
                                               string kind = null, int quality = 0)
    {
        if (!JutOn) return JutErr("DISABLED");
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");
        if (season < 1) season = 1;
        if (ep < 1) ep = 1;
        if (string.IsNullOrEmpty(kind)) kind = "episode";
        if (kind is not ("episode" or "film" or "ova" or "game-ova" or "special")) return JutErr("BAD_SLUG");

        string token = JutNet.MakeToken(slug, season, ep, kind, quality);
        var link = await JutNet.EnsureLink(token, force: false, HttpContext.RequestAborted);
        if (link == null) return JutErr("NOT_FOUND");
        if (link.error != null) return JutErr(link.error);

        return JutJson(new JObject
        {
            ["ok"] = true,
            // Единственный URL, который видит плеер: ссылку на CDN отдавать нельзя —
            // hash вяжет UA, у плеера он свой → 403.
            ["url"] = "/qdl/jut/stream?t=" + token,
            ["quality"] = link.quality,
            ["available"] = new JArray(link.available),
            ["duration"] = link.duration,
            ["outro"] = link.outro,
            ["season"] = season,
            ["ep"] = ep,
            ["kind"] = kind
        });
    }

    #endregion

    #region постер

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/poster")]
    async public Task<ActionResult> JutPoster(string slug, string v = null)
    {
        if (!JutOn) return NotFound();
        if (!JutSuParse.IsValidSlug(slug)) return NotFound();

        // 1. Апгрейд (Shikimori → MAL id → AniList, см. JutSuPoster.cs). Его может не быть —
        //    это штатно: неуверенный матч оставляет постер jut.su, см. JutSuMatch.Pick.
        //    ⚠️ Выключатель jutPosterUpgrade:false гасит эту ветку СРАЗУ — полный откат,
        //    файлы при этом остаются на диске и включение обратно мгновенно.
        if (JutHasUpPoster(slug))
        {
            // immutable только когда клиент пришёл с версией: без ?v= URL постоянный и вечный кеш
            // на нём означал бы, что следующий апгрейд до этого клиента уже не доедет.
            HttpContext.Response.Headers["Cache-Control"] =
                string.IsNullOrEmpty(v) ? "public,max-age=86400" : "public,max-age=31536000,immutable";
            return PhysicalFile(JutUpPosterPath(slug), JutUpPosterCtype(slug));
        }

        string path = Path.Combine(JutDir("img"), slug + ".jpg");
        if (System.IO.File.Exists(path))
        {
            // ⚠️ Сутки, а не неделя: апгрейд обязан доехать до уже показанной карточки
            // даже к клиенту, который ?v= не увидел.
            HttpContext.Response.Headers["Cache-Control"] = "public,max-age=86400";
            return PhysicalFile(path, "image/jpeg");
        }

        // Постер берём ТОЛЬКО из HTML: имя файла на CDN не выводится из слага
        // (/oneepiece/ → anime_onepiece.jpg, /boku-hero-academia/ → anime_boku-no-hero-academia.jpg).
        // Каталог этот URL уже разобрал и положил в решение — так холодная страница из 30 карточек
        // не превращается в 30 загрузок страниц тайтлов.
        string url = JutRawPosterFromMatch(slug);
        if (string.IsNullOrEmpty(url))
        {
            var cached = JutCacheRead("title", slug, TimeSpan.MaxValue, out _);
            url = cached?["poster"]?.Value<string>();
        }
        if (string.IsNullOrEmpty(url))
        {
            var (t, _) = await JutLoadTitle(slug, false);
            url = t?.poster;
            if (t != null) JutCacheWrite("title", slug, JutTitleJson(t));
        }
        if (string.IsNullOrEmpty(url)) return NotFound();

        byte[] bytes = await JutFetchImage(url);
        if (bytes == null || bytes.Length < 128) return NotFound();
        try { await System.IO.File.WriteAllBytesAsync(path, bytes); } catch { }

        HttpContext.Response.Headers["Cache-Control"] = "public,max-age=86400";
        return File(bytes, "image/jpeg");
    }

    /// <summary>
    /// Фон экрана тайтла — 2560×1440 с самой страницы jut.su. Риск ошибки нулевой: картинка
    /// разобрана из HTML этого же тайтла, никакого сопоставления с внешними базами тут нет.
    /// </summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/backdrop")]
    async public Task<ActionResult> JutBackdrop(string slug)
    {
        if (!JutOn || ModInit.conf?.jutBackdrop == false) return NotFound();
        if (!JutSuParse.IsValidSlug(slug)) return NotFound();

        string path = Path.Combine(JutDir("bg"), slug + ".jpg");
        if (System.IO.File.Exists(path))
        {
            HttpContext.Response.Headers["Cache-Control"] = "public,max-age=604800";
            return PhysicalFile(path, "image/jpeg");
        }

        var cached = JutCacheRead("title", slug, TimeSpan.MaxValue, out _);
        string url = cached?["backdrop"]?.Value<string>();
        if (string.IsNullOrEmpty(url))
        {
            var (t, _) = await JutLoadTitle(slug, false);
            url = t?.backdrop;
            if (t != null) JutCacheWrite("title", slug, JutTitleJson(t));
        }
        if (string.IsNullOrEmpty(url)) return NotFound();

        byte[] bytes = await JutFetchImage(url);
        if (bytes == null || bytes.Length < 1024) return NotFound();
        try { await System.IO.File.WriteAllBytesAsync(path, bytes); } catch { }

        HttpContext.Response.Headers["Cache-Control"] = "public,max-age=604800";
        return File(bytes, "image/jpeg");
    }

    internal static async Task<byte[]> JutFetchImage(string url)
    {
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", JutNet.Ua);
            using var resp = await JutNet.Media("direct").SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch { return null; }
    }

    #endregion

    #region диагностика

    /// <summary>
    /// Главный инструмент починки: любой разбор «сломалось» начинается отсюда.
    /// Читает reached / authorized РАЗДЕЛЬНО — это принципиально разные состояния.
    /// probeProxies=1 — платный режим (по запросу на выход), бюджет выходов конечен (§BB.5).
    /// </summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/diag")]
    async public Task<ActionResult> JutDiag(string slug = null, int probeProxies = 0)
    {
        var conf = ModInit.conf;
        var (mode, until) = JutProxyFallback.State("jutsu");
        var (cached, oldest) = JutNet.LinksState();

        var jo = new JObject
        {
            ["enable"] = conf?.jutEnable ?? false,
            ["host"] = JutNet.Host,
            ["ua"] = JutNet.Ua,
            // ⚠️ только НАЛИЧИЕ кук, никогда значения: dle_password — секрет
            ["cookies"] = new JObject
            {
                ["dle_user_id"] = !string.IsNullOrWhiteSpace(conf?.jutUserId),
                ["dle_password"] = !string.IsNullOrWhiteSpace(conf?.jutPassword)
            },
            ["proxy"] = new JObject
            {
                ["useproxy"] = conf?.jutProxy?.useproxy ?? false,
                ["fallbackEnable"] = conf?.jutProxyFallbackEnable ?? true,
                ["fallbackDirect"] = conf?.jutProxyFallbackDirect,
                ["listSize"] = conf?.jutProxy?.proxy?.list?.Length ?? 0,
                ["fallbackMode"] = mode,
                ["until"] = until
            },
            ["links"] = new JObject { ["cached"] = cached, ["oldestAgeSec"] = oldest },
            ["posters"] = JutPosterStats()
        };

        if (!JutOn) { jo["probe"] = "skipped: jutEnable=false"; return JutJson(jo); }

        // Достижимы ли базы аниме ИЗ КОНТЕЙНЕРА — главный вопрос при гео- или UA-блокировке:
        // фича fail-open, поэтому её отказ виден только здесь.
        if (probeProxies != 0 || slug != null) jo["postersProbe"] = await JutPosterProbe();

        var probe = new JObject();
        jo["probe"] = probe;

        // 1. каталог
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cat = await JutNet.PostForm(JutNet.Host + "/anime/order-by-add/",
            "ajax_load=yes&start_from_page=1&show_search=&anime_of_user=", JutSuParse.ReachedCatalog);
        var catParsed = cat?.body != null ? JutSuParse.ParseCatalog(cat.body) : null;
        probe["catalog"] = new JObject
        {
            ["status"] = (int)(cat?.status ?? 0),
            ["reached"] = cat?.reached ?? false,
            ["ms"] = sw.ElapsedMilliseconds,
            ["via"] = cat?.exitId,
            ["cards"] = catParsed?.items.Count ?? 0,
            ["error"] = cat?.error
        };

        // 2. тайтл (по умолчанию — тот, на котором велась разведка)
        string probeSlug = JutSuParse.IsValidSlug(slug) ? slug
                         : (catParsed?.items.FirstOrDefault()?.slug ?? "spy-family");
        var (t, terr) = await JutLoadTitle(probeSlug, true);
        probe["title"] = new JObject
        {
            ["slug"] = probeSlug,
            ["reached"] = t != null,
            ["episodes"] = t?.items.Count ?? 0,
            ["isHub"] = t?.isHub ?? false,
            ["ongoing"] = t?.ongoing ?? false,
            ["error"] = terr
        };

        // 3. страница серии — здесь и только здесь видно authorized
        var firstEp = t?.items.FirstOrDefault(e => e.kind == JutEpKind.Episode) ?? t?.items.FirstOrDefault();
        if (firstEp != null)
        {
            string token = JutNet.MakeToken(probeSlug, firstEp.season, firstEp.num,
                                            firstEp.kind.ToString().ToLowerInvariant() switch
                                            {
                                                "film" => "film",
                                                "ova" => "ova",
                                                "gameova" => "game-ova",
                                                "special" => "special",
                                                _ => "episode"
                                            }, 0);
            var link = await JutNet.EnsureLink(token, force: true, HttpContext.RequestAborted);
            bool notAuth = link?.error == "NOT_AUTHORIZED";
            probe["episode"] = new JObject
            {
                ["key"] = firstEp.epkey,
                // 🔥 pixel.png = куки протухли. Разметка при этом ЦЕЛАЯ, поэтому без явного
                //    детекта это выглядело бы как «успешный парсинг с битыми ссылками».
                ["authorized"] = link != null && link.error == null,
                ["pixelPng"] = notAuth,
                ["qualities"] = new JArray(link?.available ?? Array.Empty<int>()),
                ["quality"] = link?.quality ?? 0,
                ["duration"] = link?.duration ?? 0,
                ["outro"] = link?.outro ?? 0,
                ["error"] = link?.error
            };

            // 4. CDN: доходят ли байты и тем ли UA
            if (link?.url != null)
            {
                probe["cdn"] = await JutProbeCdn(link);
            }
        }

        if (probeProxies == 1)
        {
            // ⚠️ Платно: по запросу на каждый выход. Бюджет Proton-выходов конечен, баны ~час.
            var arr = new JArray();
            foreach (string ex in conf?.jutProxy?.proxy?.list ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(ex)) continue;
                var w = System.Diagnostics.Stopwatch.StartNew();
                WebProxy wp = null;
                try { wp = new WebProxy(new Uri(ex)); } catch { }
                var r = await JutNet.Get(JutNet.Host + "/anime/", JutSuParse.ReachedCatalog);
                arr.Add(new JObject
                {
                    ["exit"] = ex,
                    ["ok"] = r?.reached ?? false,
                    ["ms"] = w.ElapsedMilliseconds,
                    ["why"] = (r?.reached ?? false) ? null : (r?.error ?? "reached=false")
                });
            }
            jo["proxies"] = arr;
        }

        // ⚠️ Ставим ПОСЛЕ проб, а не до: иначе diag показывал бы состояние авторизации
        // от прошлого запуска — то есть врал бы ровно в тот момент, когда его читают.
        jo["authOk"] = JutNet.AuthOk;
        return JutJson(jo);
    }

    async Task<JObject> JutProbeCdn(JutLink link)
    {
        var o = new JObject();
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, link.url);
            // 🔥 ТОТ ЖЕ UA, которым получена страница — иначе гарантированный 403
            req.Headers.TryAddWithoutValidation("User-Agent", JutNet.Ua);
            // moov весит ~1.2 МБ и лежит В НАЧАЛЕ файла (faststart) — на 64 КБ атом mp4a
            // не попадался, и acodec вечно был null. diag ручной и редкий, 1.5 МБ он переживёт.
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-1572863");
            using var resp = await JutNet.Media(link.exitId).SendAsync(
                req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);

            o["status"] = (int)resp.StatusCode;
            o["acceptRanges"] = resp.Headers.AcceptRanges.FirstOrDefault()
                                ?? (resp.Content.Headers.ContentRange != null ? "bytes" : null);
            o["contentLength"] = resp.Content.Headers.ContentRange?.Length
                                 ?? resp.Content.Headers.ContentLength;
            o["contentType"] = resp.Content.Headers.ContentType?.ToString();

            byte[] head = await resp.Content.ReadAsByteArrayAsync();
            // Контейнер по magic bytes: ftyp на смещении 4 = ISO BMFF (MP4)
            o["mp4"] = head.Length > 12
                       && head[4] == (byte)'f' && head[5] == (byte)'t'
                       && head[6] == (byte)'y' && head[7] == (byte)'p';
            o["probedBytes"] = head.Length;
            // Кодеки — по имени атома в stsd (avc1/hvc1/mp4a лежат в moov, он в начале файла).
            // Latin1, а не ASCII: иначе байты >0x7F схлопнутся в '?' и порвут искомую подстроку.
            string tags = Encoding.Latin1.GetString(head);
            o["vcodec"] = tags.Contains("avc1") ? "h264"
                        : (tags.Contains("hev1") || tags.Contains("hvc1")) ? "hevc"
                        : tags.Contains("av01") ? "av1" : null;
            o["acodec"] = tags.Contains("mp4a") ? "aac"
                        : tags.Contains("ec-3") ? "eac3"
                        : tags.Contains("ac-3") ? "ac3"
                        : tags.Contains("Opus") ? "opus" : null;
            o["exit"] = link.exitId;
        }
        catch (Exception ex) { o["error"] = ex.Message; }
        return o;
    }

    #endregion
}
