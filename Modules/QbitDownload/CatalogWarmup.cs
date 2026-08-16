using Microsoft.AspNetCore.Http;
using Shared;
using Shared.Models.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Прогрев каталога главной (qdl 2.15, v2 в 2.16, v3 в 2.45; карта — E:\Media-server\claude\11) ──
// Каталог CUB идёт через CubProxy (/cub/tmdb.*) и кешируется Staticache на 3 ч; по истечении TTL
// первый живой клиент ловил MISS (поход в tmdb.cub.red на его времени). Здесь: запоминаем URL
// РЯДОВ главной и дёргаем их сами по таймеру; из ответов рядов достаём карточки и греем ПОСТЕРЫ
// (/tmdb/img/t/p/w300, TTL год), ФОНЫ (w1280 по backdrop_path) и всё, что клиент просит при
// открытии карточки. Наружу это НЕ добавляет трафика сверх прежнего: HIT отвечает кеш с SSD,
// MISS — ровно тот запрос, что сделал бы клиент.
//
// 🔥 Что изменила v3 (qdl 2.45). Замер открытия карточки: 200–2985 мс холодное против 11–12 мс
// тёплого. v2 грела ОДИН запрос из восьми — сами детали, — а карточка собирается через Status(9)
// и ждёт САМЫЙ МЕДЛЕННЫЙ из ~8 параллельных: детали, credits, recommendations, similar, videos
// (ru и en), для сериала ещё последовательный season/N, плюс cub-реакции. Грелась при этом ещё и
// не та форма деталей: у бандла ДВА загрузчика — full$1 (карточка с source:'cub', вся главная) и
// full$3 (source:'tmdb' — поиск, «Загрузки», recommendations), и у второго в append_to_response
// добавлен external_ids. В боевом кеше это видно прямо: 107 записей с external_ids против 2243.
//
// Как v3 это чинит — НЕ угадывая URL, а снимая их с живого клиента. Бандл строит адреса
// динамически (url$1: api_key, потом language, потом опциональные page/with_genres/…), поэтому
// любая рукописная реконструкция разъедется с ключом Staticache при первом же обновлении фронта.
// Вместо этого наблюдатель превращает каждый клиентский запрос вида /tmdb/api/3/{movie|tv}/<id>…
// в ШАБЛОН с плейсхолдерами ({k}, {id}, {s}) и копит их набор с частотами; прогрев инстанцирует
// шаблоны для своих карточек. Набор самоисправляется при смене фронта, а редкие формы (например
// en-фолбэк описания) сами вытесняются частыми за пределы бюджета.
//
// Тонкости (не видны из кода по отдельности):
//  • подписка EventListener.Middleware (first:true) стоит в пайплайне ПОСЛЕ UseStaticache —
//    HIT-ы до нас не доходят, наблюдаем только MISS; для сбора достаточно (URL миснёт хоть раз);
//  • ключ Staticache = Scheme+Host+Path+Query (Staticache.getQueryKeys) → реплей обязан нести
//    ОРИГИНАЛЬНЫЙ Host (и X-Forwarded-Proto для записей со scheme=https), иначе греется чужой ключ;
//  • ⚠️ клиент НЕ ходит в /cub/tmdb./3/... — XHR-патч lampainit-invc.js переписывает карточку на
//    {localhost}/tmdb/api/3/movie|tv/<id>, а нормализации путей в ключе Staticache нет;
//  • api_key с qdl 2.19 в ignoreQueryKeys эндпоинта /tmdb/api и на ключ не влияет (TmdbProxy всё
//    равно подставляет апстриму серверный), но в шаблоне он сохраняется как есть — так дешевле,
//    чем вырезать, и на совпадение ключа не влияет;
//  • реплей на 127.0.0.1:<port> — локальный запрос, D1VPerimeter пропускает;
//  • тело ответа дочитываем целиком: StaticacheWriter сохраняет запись по завершении ответа;
//  • ?query= (поиск) не запоминаем — одноразовые URL забили бы LRU-кап;
//  • бюджет считается в КАРТОЧКАХ, а не в URL: полуразогретая карточка не стоит почти ничего,
//    клиент всё равно ждёт самый медленный из её запросов;
//  • курсоры ротации персистятся и идут по СТАБИЛЬНОМУ ключу. В v2 это были индексы в списке,
//    который на каждом тике пересортировывался по lastSeen, да ещё и обнулялись рестартом —
//    хвост систематически не добирался (хост падает по питанию ~23 раза в месяц).
public static class CatalogWarmup
{
    sealed class Entry
    {
        public string scheme { get; set; }
        public string host { get; set; }
        public string pathQuery { get; set; }
        public DateTime lastSeen { get; set; }
    }

    public readonly record struct Card(long id, bool tv, string poster, string backdrop);

    /// <summary>
    /// Шаблон запроса карточки, снятый с живого клиента: путь+query с плейсхолдерами
    /// {k} — movie|tv, {id} — id карточки, {s} — номер сезона.
    /// </summary>
    public sealed class Tmpl
    {
        public string form { get; set; }        // "/tmdb/api/3/{k}/{id}/credits?api_key=…&language=ru"
        public string kind { get; set; }        // "movie" | "tv" | "any"
        public long hits { get; set; }          // частота: по ней выбираем, что греть в первую очередь
        public DateTime lastSeen { get; set; }
    }

    // Маркер собственного реплея: наблюдатель не должен принимать наш же прогрев
    // за «живой клиентский запрос» и подменять им снятый с клиента набор шаблонов.
    public const string WarmupHeader = "X-QDL-Warmup";

    static readonly ConcurrentDictionary<string, Entry> _rows = new();
    static readonly ConcurrentDictionary<string, Tmpl> _tmpl = new();
    static readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    static Timer _timer;
    static int _ticking = 0;
    static bool _dirty;

    // курсоры ротации по стабильному ключу (последний обработанный), переживают рестарт
    static string _posterCur, _backdropCur, _cardCur;

    const int TmplCap = 24;

    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "catalog-warmup.json");

    // ── карточки главной для обходчика индекса (LocalIndex/IndexCrawler) и джобы прогрева «Онлайн» ──
    // Только id + признак сериала: этого хватает, чтобы взять название из нашего же
    // TMDB-прокси (детали этих карточек прогрев уже положил в кеш). Набор ограничен —
    // это «что показывали недавно», а не полная история.
    static readonly ConcurrentDictionary<long, bool> _cardIds = new();
    // когда карточку увидели ВПЕРВЫЕ (unix, UTC) — на этом стоит полоса «новинки» в OnlineWarm
    static readonly ConcurrentDictionary<long, long> _cardFirstSeen = new();
    const int CardIdsCap = 3000;

    internal static void NoteCard(long id, bool tv)
    {
        if (id <= 0) return;
        if (_cardIds.Count >= CardIdsCap && !_cardIds.ContainsKey(id)) return;
        _cardIds[id] = tv;
        if (_cardFirstSeen.TryAdd(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            _dirty = true;
    }

    internal static IReadOnlyList<(long id, bool tv)> KnownCards()
        => _cardIds.Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>Когда карточку увидели впервые (unix UTC); 0 — не видели.</summary>
    internal static long CardFirstSeen(long id)
        => _cardFirstSeen.TryGetValue(id, out long t) ? t : 0;

    public static void Attach()
    {
        Load();
        EventListener.Middleware += OnRequest;
        int period = Math.Max(5, ModInit.conf != null && ModInit.conf.catalogWarmupPeriodMin > 0 ? ModInit.conf.catalogWarmupPeriodMin : 15);
        _timer?.Dispose();
        _timer = new Timer(async _ =>
        {
            try { await Tick(); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup: " + ex.Message); }
        }, null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(period));
    }

    public static void Detach()
    {
        EventListener.Middleware -= OnRequest;
        _timer?.Dispose();
        _timer = null;
    }

    // Наблюдатель пайплайна: только учёт, всегда true (запрос не трогаем). Держать ДЁШЕВО.
    public static bool OnRequest(bool first, EventMiddleware e)
    {
        try
        {
            if (!first || ModInit.conf?.catalogWarmupEnabled != true) return true;

            var req = e.httpContext?.Request;
            if (req == null || !HttpMethods.IsGet(req.Method)) return true;

            string path = req.Path.Value;
            if (path == null) return true;

            bool cub = path.StartsWith("/cub/", StringComparison.OrdinalIgnoreCase);
            if (!cub && !path.StartsWith("/tmdb/api/", StringComparison.OrdinalIgnoreCase)) return true;

            // собственный реплей клиентом не считаем: иначе MISS нашего же прогрева обновлял бы
            // lastSeen ряда (catalogWarmupPruneDays никогда не срабатывал) и накручивал бы
            // частоты шаблонам, которые греем мы сами, а не клиент
            if (req.Headers.ContainsKey(WarmupHeader)) return true;

            string query = req.QueryString.HasValue ? req.QueryString.Value : string.Empty;

            if (cub && IsRowUrl(path, query))
            {
                Note(req.Scheme, req.Host.Value, path + query);
                return true;
            }

            NoteTemplate(path, query);
        }
        catch { }
        return true;
    }

    // ── чистые предикаты/парсеры — покрыты Tests/QbitDownload.Tests ──

    // Ряд каталога: /cub/tmdb.* БЕЗ tmdb-api-сегмента /3/ (детали идут /cub/tmdb./3/...).
    // v2: детали в LRU рядов НЕ пускаем — вытесняли ряды (кап), их источник теперь парс ответов.
    public static bool IsRowUrl(string path, string query)
    {
        if (path == null || !path.StartsWith("/cub/tmdb.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Contains("/3/", StringComparison.Ordinal))
            return false;

        if (query != null && query.Contains("query=", StringComparison.OrdinalIgnoreCase))
            return false;   // поиск — одноразовые URL

        return true;
    }

    // Деталь карточки: /tmdb/api/3/movie/<id> | /3/tv/<id> (форма клиента после XHR-патча)
    // либо старая /cub/tmdb.<mirror>/3/... (сезоны/персоны/прочее не считаем).
    public static bool IsDetailUrl(string path)
    {
        if (path == null)
            return false;

        if (!path.StartsWith("/cub/tmdb.", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/tmdb/api/", StringComparison.OrdinalIgnoreCase))
            return false;

        int i = path.IndexOf("/3/", StringComparison.Ordinal);
        if (i < 0) return false;

        ReadOnlySpan<char> tail = path.AsSpan(i + 3);
        if (tail.StartsWith("movie/")) tail = tail.Slice(6);
        else if (tail.StartsWith("tv/")) tail = tail.Slice(3);
        else return false;

        if (tail.IsEmpty) return false;
        foreach (char c in tail)
            if (c < '0' || c > '9') return false;
        return true;
    }

    #region шаблоны запросов карточки (снимаются с живого клиента)

    /// <summary>
    /// Превращает клиентский URL карточки в шаблон с плейсхолдерами. null — если URL не привязан
    /// к конкретной карточке (ряды, коллекции, поиск, персоны) и шаблонизировать нечего.
    ///
    /// Понимает две формы:
    ///   /tmdb/api/3/{movie|tv}/&lt;id&gt;[/суффикс][?query]   → "/tmdb/api/3/{k}/{id}[/суффикс]?query"
    ///   /cub/&lt;домен&gt;/api/reactions/get/{movie|tv}_&lt;id&gt; → "/cub/&lt;домен&gt;/api/reactions/get/{k}_{id}"
    /// Номер сезона в суффиксе /season/&lt;N&gt; заменяется на {s} — он зависит от самой карточки.
    /// </summary>
    public static Tmpl ToTemplate(string path, string query)
    {
        if (string.IsNullOrEmpty(path)) return null;
        query ??= string.Empty;

        // ?query= — поиск, к карточке не привязан
        if (query.Contains("query=", StringComparison.OrdinalIgnoreCase)) return null;

        string kind, form;

        int r = path.IndexOf("/api/reactions/get/", StringComparison.OrdinalIgnoreCase);
        if (r >= 0)
        {
            // /cub/<домен>/api/reactions/get/movie_550
            string tail = path.Substring(r + "/api/reactions/get/".Length);
            int us = tail.IndexOf('_');
            if (us <= 0) return null;

            kind = tail.Substring(0, us).ToLowerInvariant();
            if (kind != "movie" && kind != "tv") return null;
            if (!AllDigits(tail.AsSpan(us + 1))) return null;

            form = path.Substring(0, r) + "/api/reactions/get/{k}_{id}" + query;
            return new Tmpl { form = form, kind = kind, hits = 1, lastSeen = DateTime.UtcNow };
        }

        int i = path.IndexOf("/3/", StringComparison.Ordinal);
        if (i < 0) return null;

        string prefix = path.Substring(0, i + 3);
        string rest = path.Substring(i + 3);

        if (rest.StartsWith("movie/", StringComparison.Ordinal)) { kind = "movie"; rest = rest.Substring(6); }
        else if (rest.StartsWith("tv/", StringComparison.Ordinal)) { kind = "tv"; rest = rest.Substring(3); }
        else return null;

        // <id>[/суффикс]
        int slash = rest.IndexOf('/');
        string idPart = slash < 0 ? rest : rest.Substring(0, slash);
        string suffix = slash < 0 ? string.Empty : rest.Substring(slash);

        if (!AllDigits(idPart.AsSpan())) return null;

        // /season/<N> → /season/{s}: номер зависит от карточки, в шаблон его вшивать нельзя
        int se = suffix.IndexOf("/season/", StringComparison.Ordinal);
        if (se >= 0)
        {
            string after = suffix.Substring(se + "/season/".Length);
            // только «/season/<N>» целиком; /season/<N>/episode/<M> нам не нужен
            if (!AllDigits(after.AsSpan())) return null;
            suffix = suffix.Substring(0, se) + "/season/{s}";
        }
        else if (suffix.Length > 0 && !IsSafeSuffix(suffix))
            return null;

        form = prefix + "{k}/{id}" + suffix + query;
        return new Tmpl { form = form, kind = kind, hits = 1, lastSeen = DateTime.UtcNow };
    }

    // Суффиксы, которые действительно относятся к карточке. Белый список, а не «всё подряд»:
    // иначе в набор попадут разовые ручки (например /rating от действий пользователя) и съедят бюджет.
    static bool IsSafeSuffix(string suffix)
        => suffix is "/credits" or "/recommendations" or "/similar" or "/videos"
                  or "/images" or "/external_ids" or "/keywords" or "/content_ratings" or "/release_dates";

    static bool AllDigits(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }

    /// <summary>Подставляет карточку в шаблон. season нужен только формам с {s}.</summary>
    public static string Instantiate(string form, bool tv, long id, int season)
        => form == null ? null
         : form.Replace("{k}", tv ? "tv" : "movie")
               .Replace("{id}", id.ToString())
               .Replace("{s}", season.ToString());

    /// <summary>
    /// Сколько сезонов показывать — ровно как Utils.countSeasons в бандле: считаем сезоны с
    /// episode_count &gt; 0 и не превышаем number_of_seasons. Нужен, чтобы попасть в тот же URL
    /// /tv/&lt;id&gt;/season/&lt;N&gt;, который запросит клиент.
    /// </summary>
    public static int SeasonForWarm(byte[] detailsBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailsBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 0;

            int count = 0;
            if (root.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
                foreach (var s in seasons.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.Object
                        && s.TryGetProperty("episode_count", out var ec)
                        && ec.TryGetInt32(out int n) && n > 0)
                        count++;

            // отсутствующий number_of_seasons в JS даёт `count > undefined` == false, то есть кап
            // не применяется — повторяем это, иначе разъедемся с клиентом на битых ответах
            if (root.TryGetProperty("number_of_seasons", out var nos) && nos.TryGetInt32(out int cap) && count > cap)
                count = cap;

            return count;
        }
        catch { return 0; }
    }

    static void NoteTemplate(string path, string query)
    {
        var t = ToTemplate(path, query);
        if (t == null) return;

        string key = t.kind + "|" + t.form;
        _tmpl.AddOrUpdate(key, _ => { _dirty = true; return t; }, (_, old) =>
        {
            old.hits++;
            old.lastSeen = DateTime.UtcNow;
            return old;
        });

        // кап: вытесняем самый редкий, при равенстве — самый давний
        while (_tmpl.Count > TmplCap)
        {
            var worst = _tmpl.OrderBy(kv => kv.Value.hits).ThenBy(kv => kv.Value.lastSeen).FirstOrDefault();
            if (worst.Key == null || !_tmpl.TryRemove(worst.Key, out _)) break;
            _dirty = true;
        }
    }

    /// <summary>
    /// Формы деталей по умолчанию — пока живых наблюдений нет. Две, потому что у бандла два
    /// загрузчика карточки: full$1 (source:'cub' — вся главная) и full$3 (source:'tmdb' — поиск,
    /// «Загрузки», recommendations), и второй просит ещё external_ids.
    /// </summary>
    public static string DefaultDetailQuery(string apiKey)
        => "?api_key=" + apiKey + "&append_to_response=content_ratings,release_dates,keywords,alternative_titles&language=ru";

    public static string DefaultDetailQueryExternalIds(string apiKey)
        => "?api_key=" + apiKey + "&append_to_response=content_ratings,release_dates,external_ids,keywords,alternative_titles&language=ru";

    // Путь деталей — ровно тот, что просит клиент (XHR-патч lampainit-invc.js → {localhost}/tmdb/api/…).
    public static string DetailPath(long id, bool tv)
        => "/tmdb/api/3/" + (tv ? "tv/" : "movie/") + id;

    // Картинка через свой TMDB-прокси; file уже начинается с '/' (как в TMDB: "/abc.jpg").
    public static string ImgPath(string size, string file)
        => "/tmdb/img/t/p/" + size + file;

    #endregion

    // Фоны карточек (backdrop_path, w1280 — размер, который берёт полная карточка) для первых
    // perRow карточек ряда. Карточки без backdrop_path пропускаем и добираем следующими: бюджет
    // фонов маленький (фон в 5-10 раз тяжелее постера), тратить его на «дыры» незачем.
    public static List<string> BackdropPaths(IReadOnlyList<Card> cards, int perRow)
    {
        var list = new List<string>();
        if (cards == null || perRow <= 0)
            return list;

        for (int i = 0; i < cards.Count && list.Count < perRow; i++)
        {
            string b = cards[i].backdrop;
            if (!string.IsNullOrEmpty(b) && b[0] == '/')
                list.Add(ImgPath("w1280", b));
        }
        return list;
    }

    // Карточки из ответа ряда (TMDB-подобный JSON {results:[{id, poster_path, title|name, ...}]}).
    // Тип: есть name/original_name → tv, иначе movie (та же логика, что у фронта в full()).
    public static List<Card> ExtractCards(byte[] body, int max)
    {
        var cards = new List<Card>(Math.Max(0, max));
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return cards;

            foreach (var el in results.EnumerateArray())
            {
                if (cards.Count >= max) break;
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id) || id <= 0) continue;

                bool tv = (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                       || (el.TryGetProperty("original_name", out var on) && on.ValueKind == JsonValueKind.String);

                string poster = el.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null;

                string backdrop = el.TryGetProperty("backdrop_path", out var b) && b.ValueKind == JsonValueKind.String
                    ? b.GetString() : null;

                cards.Add(new Card(id, tv, poster, backdrop));
            }
        }
        catch { }
        return cards;
    }

    static void Note(string scheme, string host, string pathQuery)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pathQuery)) return;

        string key = scheme + "|" + host + "|" + pathQuery;
        bool added = false;
        _rows.AddOrUpdate(key,
            _ => { added = true; return new Entry { scheme = scheme, host = host, pathQuery = pathQuery, lastSeen = DateTime.UtcNow }; },
            (_, en) => { en.lastSeen = DateTime.UtcNow; return en; });

        if (!added)
            return;

        int cap = Math.Max(8, ModInit.conf != null && ModInit.conf.catalogWarmupMaxUrls > 0 ? ModInit.conf.catalogWarmupMaxUrls : 128);
        while (_rows.Count > cap)
        {
            string oldest = null;
            DateTime oldestAt = DateTime.MaxValue;
            foreach (var kv in _rows)
                if (kv.Value.lastSeen < oldestAt) { oldestAt = kv.Value.lastSeen; oldest = kv.Key; }
            if (oldest == null || !_rows.TryRemove(oldest, out _))
                break;
        }
        _dirty = true;
    }

    // ── Засев рядов на сервер-реплику ───────────────────────────────────────────
    // Файлы Staticache между серверами непереносимы: ключ считается как Scheme+Host+Path+Query
    // (Core/Middlewares/Staticache.cs), а исходного URL в имени файла нет — оно и есть
    // односторонний хеш. Значит переносить надо не байты, а СПИСОК: реплика подставит свой
    // scheme/host и наполнит собственный кеш правильными ключами, сходив за телами напрямую
    // (её канал, домашний аплинк не участвует).

    /// <summary>Пути рядов (без scheme/host) для передачи на реплику, свежие вперёд.</summary>
    internal static List<string> ExportRowPaths()
        => _rows.Values.OrderByDescending(e => e.lastSeen)
                       .Select(e => e.pathQuery)
                       .Where(p => !string.IsNullOrEmpty(p))
                       .Distinct()
                       .ToList();

    /// <summary>
    /// Принять ряды от дома под СВОИ scheme/host. Возвращает число принятых.
    /// ⚠️ lastSeen ставится «сейчас», поэтому принятые ряды не выпадут по catalogWarmupPruneDays
    /// раньше, чем реплика успеет их прогреть.
    /// </summary>
    internal static int ImportRowPaths(IEnumerable<string> pathQueries, string scheme, string host)
    {
        if (pathQueries == null || string.IsNullOrEmpty(host)) return 0;

        int n = 0;
        foreach (var pq in pathQueries)
        {
            if (string.IsNullOrEmpty(pq) || !pq.StartsWith("/", StringComparison.Ordinal)) continue;
            Note(scheme ?? "http", host, pq);
            n++;
        }

        if (n > 0) { try { Save(); } catch { } }
        return n;
    }

    sealed record WarmCard(string host, string scheme, long id, bool tv)
    {
        // стабильный ключ ротации: не зависит ни от порядка рядов, ни от рестарта
        public string Key => host + "|" + (tv ? "tv" : "movie") + "|" + id.ToString("D10");
    }

    static async Task Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) == 1)
            return;

        try
        {
            var conf = ModInit.conf;
            if (conf?.catalogWarmupEnabled != true)
                return;

            int pruneDays = Math.Max(1, conf.catalogWarmupPruneDays);
            foreach (var kv in _rows)
                if ((DateTime.UtcNow - kv.Value.lastSeen).TotalDays > pruneDays)
                    if (_rows.TryRemove(kv.Key, out _)) _dirty = true;

            int port = 9118;
            try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }

            int cardsPerRow = Math.Max(1, conf.catalogWarmupCardsPerRow);
            int posterBudget = Math.Max(0, conf.catalogWarmupPosterBudget);
            int backdropBudget = Math.Max(0, conf.catalogWarmupBackdropBudget);
            int backdropsPerRow = Math.Max(0, conf.catalogWarmupBackdropsPerRow);
            int cardBudget = Math.Max(0, conf.catalogWarmupCardBudget);

            // свежие ряды первыми: их карточки первыми попадут под бюджеты постеров/фонов
            var rows = _rows.Values.OrderByDescending(e => e.lastSeen).ToArray();

            int miss = 0, fail = 0;
            var posters = new List<(string key, string host, string scheme, string path)>();
            var backdrops = new List<(string key, string host, string scheme, string path)>();
            var cards = new List<WarmCard>();
            var posterSeen = new HashSet<string>(StringComparer.Ordinal);
            var backdropSeen = new HashSet<string>(StringComparer.Ordinal);
            var cardSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var en in rows)
            {
                var (ok, wasMiss, body, contentType) = await Fetch(port, en.scheme, en.host, en.pathQuery);
                if (!ok) { fail++; continue; }
                if (wasMiss) miss++;

                // карточки ряда → очереди прогрева (дедуп по host+идентичности между рядами)
                if (body != null && body.Length < 3_000_000 && contentType?.StartsWith("application/json") == true)
                {
                    var rowCards = ExtractCards(body, cardsPerRow);

                    // Обходчик индекса (IndexCrawler) переиспользует уже разобранные карточки
                    // главной: своих запросов к TMDB он из-за этого не делает.
                    foreach (var card in rowCards) NoteCard(card.id, card.tv);

                    foreach (var card in rowCards)
                    {
                        if (!string.IsNullOrEmpty(card.poster) && card.poster.StartsWith('/')
                            && posterSeen.Add(en.host + "|" + card.poster))
                            posters.Add((en.host + "|" + card.poster, en.host, en.scheme, ImgPath("w300", card.poster)));

                        var wc = new WarmCard(en.host, en.scheme, card.id, card.tv);
                        if (cardSeen.Add(wc.Key))
                            cards.Add(wc);
                    }

                    // фон берут только первые карточки ряда — они же первыми открываются с пульта
                    foreach (string bd in BackdropPaths(rowCards, backdropsPerRow))
                        if (backdropSeen.Add(en.host + "|" + bd))
                            backdrops.Add((en.host + "|" + bd, en.host, en.scheme, bd));
                }

                await Task.Delay(100);
            }

            int posterMiss = await WarmList(port, posters, posterBudget, _posterCur, v => _posterCur = v);
            int backdropMiss = await WarmList(port, backdrops, backdropBudget, _backdropCur, v => _backdropCur = v);
            var (cardsDone, cardUrls, cardMiss) = await WarmCards(port, cards, cardBudget);

            if (miss > 0 || fail > 0 || posterMiss > 0 || backdropMiss > 0 || cardMiss > 0)
                Console.WriteLine($"[QbitDownload] catalog warmup: rows {rows.Length} (miss {miss}, fail {fail}), posters {Math.Min(posters.Count, posterBudget)}/{posters.Count} (miss {posterMiss}), backdrops {Math.Min(backdrops.Count, backdropBudget)}/{backdrops.Count} (miss {backdropMiss}), cards {cardsDone}/{cards.Count} ({cardUrls} url, miss {cardMiss}), tmpl {_tmpl.Count}");

            if (_dirty) { _dirty = false; Save(); }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>
    /// Прогрев ЦЕЛЫХ карточек: за тик берём cardBudget карточек и внутри каждой проходим все
    /// известные шаблоны. Половинчатый прогрев смысла не имеет — карточка у клиента собирается
    /// через Status(9) и ждёт самый медленный из своих запросов.
    /// </summary>
    static async Task<(int cards, int urls, int miss)> WarmCards(int port, List<WarmCard> list, int budget)
    {
        if (list.Count == 0 || budget <= 0)
            return (0, 0, 0);

        var forms = TemplatesToWarm();
        if (forms.Count == 0)
            return (0, 0, 0);

        list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        int start = CursorStart(list.Select(x => x.Key).ToList(), _cardCur);

        int done = 0, urls = 0, missCount = 0;
        for (int i = 0; i < budget && i < list.Count; i++)
        {
            var card = list[(start + i) % list.Count];
            string kind = card.tv ? "tv" : "movie";

            // Детали идут ПЕРВЫМИ и с чтением тела: из него берётся номер сезона для /season/{s}
            // (Utils.countSeasons). Без этого сезонный URL не совпал бы с клиентским.
            int season = 0;
            foreach (var t in forms)
            {
                if (t.kind != "any" && t.kind != kind) continue;
                if (t.form.Contains("{s}", StringComparison.Ordinal)) continue;   // отложим до вычисления сезона

                string p = Instantiate(t.form, card.tv, card.id, 0);
                var (ok, wasMiss, body, _) = await Fetch(port, card.scheme, card.host, p);
                urls++;
                if (ok && wasMiss) missCount++;

                if (season == 0 && card.tv && ok && body != null && IsBareDetail(t.form))
                    season = SeasonForWarm(body);

                await Task.Delay(100);
            }

            if (card.tv && season > 0)
            {
                foreach (var t in forms)
                {
                    if (t.kind != "any" && t.kind != kind) continue;
                    if (!t.form.Contains("{s}", StringComparison.Ordinal)) continue;

                    string p = Instantiate(t.form, card.tv, card.id, season);
                    var (ok, wasMiss, _, _) = await Fetch(port, card.scheme, card.host, p);
                    urls++;
                    if (ok && wasMiss) missCount++;
                    await Task.Delay(100);
                }
            }

            _cardCur = card.Key;
            done++;
        }

        return (done, urls, missCount);
    }

    // «Голая» деталь карточки — форма без суффикса пути (именно её тело несёт seasons[]).
    static bool IsBareDetail(string form)
    {
        int i = form.IndexOf("{id}", StringComparison.Ordinal);
        if (i < 0) return false;
        string tail = form.Substring(i + 4);
        int q = tail.IndexOf('?');
        return (q < 0 ? tail : tail.Substring(0, q)).Length == 0;
    }

    /// <summary>
    /// Что греть: снятые с клиента шаблоны по убыванию частоты. Пока наблюдений нет — обе
    /// дефолтные формы деталей, чтобы прогрев работал сразу после чистого старта.
    /// </summary>
    static List<Tmpl> TemplatesToWarm()
    {
        if (!_tmpl.IsEmpty)
            return _tmpl.Values.OrderByDescending(t => t.hits).ThenByDescending(t => t.lastSeen).ToList();

        string apiKey = null;
        try { apiKey = CoreInit.conf.cub?.api_key; } catch { }
        if (string.IsNullOrEmpty(apiKey))
            return new List<Tmpl>();

        return new List<Tmpl>
        {
            new() { form = "/tmdb/api/3/{k}/{id}" + DefaultDetailQuery(apiKey), kind = "any", hits = 0, lastSeen = DateTime.UtcNow },
            new() { form = "/tmdb/api/3/{k}/{id}" + DefaultDetailQueryExternalIds(apiKey), kind = "any", hits = 0, lastSeen = DateTime.UtcNow }
        };
    }

    /// <summary>
    /// Позиция после последнего обработанного ключа. Список отсортирован по этому же ключу,
    /// поэтому курсор переживает и пересортировку набора, и рестарт процесса; ключ исчез —
    /// продолжаем со следующего за ним по порядку, а не с начала.
    /// </summary>
    static int CursorStart(List<string> keys, string cursor)
    {
        if (string.IsNullOrEmpty(cursor) || keys.Count == 0)
            return 0;

        for (int i = 0; i < keys.Count; i++)
            if (string.CompareOrdinal(keys[i], cursor) > 0)
                return i;

        return 0;   // прошли хвост — начинаем круг заново
    }

    // Прогрев списка URL с ротацией по стабильному ключу: за несколько тиков покрывается весь хвост
    static async Task<int> WarmList(int port, List<(string key, string host, string scheme, string path)> list, int budget, string cursor, Action<string> saveCursor)
    {
        if (list.Count == 0 || budget <= 0)
            return 0;

        list.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        int start = CursorStart(list.Select(x => x.key).ToList(), cursor);

        int missCount = 0, todo = Math.Min(budget, list.Count);
        for (int i = 0; i < todo; i++)
        {
            var it = list[(start + i) % list.Count];
            var (ok, wasMiss, _, _) = await Fetch(port, it.scheme, it.host, it.path, readBody: true);
            if (ok && wasMiss) missCount++;
            saveCursor(it.key);
            await Task.Delay(100);
        }
        return missCount;
    }

    static async Task<(bool ok, bool miss, byte[] body, string contentType)> Fetch(int port, string scheme, string host, string pathQuery, bool readBody = true)
    {
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{pathQuery}");
            rq.Headers.TryAddWithoutValidation("Host", host);
            rq.Headers.TryAddWithoutValidation(WarmupHeader, "1");   // чтобы наблюдатель не принял реплей за клиента
            if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                rq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

            using var rs = await _http.SendAsync(rq);
            byte[] body = readBody ? await rs.Content.ReadAsByteArrayAsync() : null;

            bool miss = rs.Headers.TryGetValues("X-StatiCache-Status", out var st) && st.FirstOrDefault() == "MISS";
            string contentType = rs.Content.Headers.ContentType?.MediaType != null
                ? rs.Content.Headers.ContentType.MediaType : null;

            NoteHealth(pathQuery, rs.IsSuccessStatusCode, (int)rs.StatusCode, miss);
            return (rs.IsSuccessStatusCode, miss, body, contentType);
        }
        catch
        {
            NoteHealth(pathQuery, false, 0, true);   // до апстрима не дошли — наблюдение честное
            return (false, false, null, null);
        }
    }

    /// <summary>
    /// Пассивный хелс-чек внешних метаданных (HealthState.cs): прогрев регулярно тянет ряды,
    /// детали и постеры, поэтому отдельные пробы ради экрана не нужны.
    ///
    /// 🔥 Считаем ТОЛЬКО MISS. Запрос идёт на НАШ /tmdb/*|/cub/* на loopback, и HIT в Staticache
    /// отвечает 200, вообще не ходя наружу — засчитывать его как «TMDB работает» значило бы
    /// повторить ровно ту ложную зелень, из-за которой пробы и переделывались.
    /// </summary>
    static void NoteHealth(string pathQuery, bool ok, int code, bool miss)
    {
        if (!miss || string.IsNullOrEmpty(pathQuery)) return;
        try
        {
            string id = pathQuery.StartsWith("/tmdb/img/", StringComparison.OrdinalIgnoreCase) ? HealthState.Ids.TmdbImg
                      : pathQuery.StartsWith("/tmdb/api/", StringComparison.OrdinalIgnoreCase) ? HealthState.Ids.TmdbApi
                      : pathQuery.StartsWith("/cub/tmdb.", StringComparison.OrdinalIgnoreCase)
                            ? (pathQuery.Contains("/3/", StringComparison.Ordinal) ? HealthState.Ids.TmdbApi : HealthState.Ids.Cub)
                      : null;
            if (id == null) return;

            if (ok) HealthState.Ok(id);
            else HealthState.Fail(id, code > 0 ? "http " + code : "не ответил");
        }
        catch { }
    }

    #region persist
    // v3-формат: объект со всем состоянием. v2 был голым массивом рядов — читаем и его, иначе
    // после обновления прогрев начинал бы с нуля и первые сутки грел вслепую.
    sealed class State
    {
        public int ver { get; set; }
        public List<Entry> rows { get; set; }
        public List<Tmpl> tmpl { get; set; }
        public string posterCur { get; set; }
        public string backdropCur { get; set; }
        public string cardCur { get; set; }
        [JsonPropertyName("firstSeen")]
        public Dictionary<string, long> firstSeen { get; set; }
    }

    static void Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return;

            string raw = File.ReadAllText(StorePath);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            List<Entry> rows = null;

            if (raw.TrimStart().StartsWith('['))
            {
                rows = JsonSerializer.Deserialize<List<Entry>>(raw);   // v2
            }
            else
            {
                var st = JsonSerializer.Deserialize<State>(raw);
                if (st != null)
                {
                    rows = st.rows;
                    _posterCur = st.posterCur;
                    _backdropCur = st.backdropCur;
                    _cardCur = st.cardCur;

                    foreach (var t in st.tmpl ?? new List<Tmpl>())
                        if (!string.IsNullOrEmpty(t?.form) && !string.IsNullOrEmpty(t.kind))
                            _tmpl.TryAdd(t.kind + "|" + t.form, t);

                    foreach (var kv in st.firstSeen ?? new Dictionary<string, long>())
                        if (long.TryParse(kv.Key, out long cid) && cid > 0)
                            _cardFirstSeen.TryAdd(cid, kv.Value);
                }
            }

            foreach (var en in rows ?? new List<Entry>())
                if (!string.IsNullOrEmpty(en?.host) && !string.IsNullOrEmpty(en.pathQuery)
                    && IsRowUrl(SplitPath(en.pathQuery), null))   // v1-файлы могли содержать детали — отсеять
                    _rows.TryAdd((en.scheme ?? "http") + "|" + en.host + "|" + en.pathQuery, en);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup load: " + ex.Message); }
    }

    static string SplitPath(string pathQuery)
    {
        int q = pathQuery.IndexOf('?');
        return q < 0 ? pathQuery : pathQuery.Substring(0, q);
    }

    static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(StorePath);
            Directory.CreateDirectory(dir);

            var st = new State
            {
                ver = 3,
                rows = _rows.Values.ToList(),
                tmpl = _tmpl.Values.ToList(),
                posterCur = _posterCur,
                backdropCur = _backdropCur,
                cardCur = _cardCur,
                firstSeen = _cardFirstSeen.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            };

            // ⚠️ .tmp → Move, а не WriteAllText поверх: хост падает по питанию ~23 раза в месяц,
            // и обрезанный JSON стоил бы всего накопленного состояния прогрева.
            string tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup save: " + ex.Message); }
    }
    #endregion
}
