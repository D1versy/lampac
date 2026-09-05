using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Models.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

        // ── карантин мёртвых адресов (qdl 2.65) ─────────────────────────────────
        // Поля АДДИТИВНЫЕ: в старом файле их нет, System.Text.Json оставит дефолты
        // (fails=0, deadAt=null) — то есть «все ряды живые». Старый образ, наоборот,
        // новые свойства молча проигнорирует. Значит откат образа НЕ требует отката
        // данных, и ver файла остаётся 3 (Load ветвится по форме JSON, не по ver).
        public int fails { get; set; }            // подряд «адреса не существует» (4xx кроме 408/429)
        public int lastCode { get; set; }         // последний код — только для диагностики
        public DateTime? deadAt { get; set; }     // когда похоронен И когда последний раз пробовали; null — живой
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
    static int _deadLogged = -1;   // сколько рядов было в карантине на прошлом логе (-1 — ещё не логировали)

    // курсоры ротации по стабильному ключу (последний обработанный), переживают рестарт
    static string _posterCur, _backdropCur, _cardCur;

    // ── «Лента» (qdl 2.84) ────────────────────────────────────────────────────────
    // Дыра, описанная в медиасервере claude/11: ряды греются по префиксу /cub/tmdb., а лента
    // живёт по /cub/<домен>/api/feed/all — в LRU рядов она не попадала В ПРИНЦИПЕ. Замер оттуда:
    // сама лента 3.29 с холодная / 1–5 мс тёплая, плюс на КАЖДЫЙ элемент бандл делает отдельный
    // find/<imdb>?external_source=imdb_id (160–314 мс холодный). Раз в 3 часа за это платил
    // первый открывший.
    //
    // 🔥 Форму find/ НЕ конструируем, а СНИМАЕМ с живого клиента — ровно по тем же граблям, что
    // и шаблоны карточки (§AV.4): бандл строит адрес динамически, и любая рукописная
    // реконструкция разъедется с ключом Staticache при первом же обновлении фронта. Нет снятой
    // формы — греем только саму ленту (это и есть те самые 3.29 с), find/ подтянутся, когда
    // клиент один раз откроет ленту сам.
    static Entry _feed;          // единственный URL: лент у клиента не бывает много
    static string _findForm;     // "/tmdb/api/3/find/{imdb}?external_source=imdb_id&…"

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

    /// <summary>Учёт клиентских рядов (хук пайплайна) + состояние с диска. Таймер — отдельно, StartTimer() у ведущего.</summary>
    public static void Attach()
    {
        Load();
        EventListener.Middleware += OnRequest;
    }

    /// <summary>Периодический прогрев. Зовётся из ModInit.Activate — только у ведущего (Deploy): дежурный не пишет состояние.</summary>
    public static void StartTimer()
    {
        int period = EffectivePeriodMin();
        _timer?.Dispose();
        _timer = new Timer(async _ =>
        {
            try { await Tick(); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup: " + ex.Message); }
        }, null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(period));
    }

    public static void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static void Detach()
    {
        EventListener.Middleware -= OnRequest;
        StopTimer();
    }

    /// <summary>Перечитать состояние с диска (promote в Deploy: файл дописал предыдущий экземпляр).</summary>
    internal static void Reload()
    {
        ResetForTests();
        _feed = null;
        _findForm = null;
        Load();
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

            if (cub && IsFeedUrl(path, query))
            {
                NoteFeed(req.Scheme, req.Host.Value, path + query);
                return true;
            }

            string findForm = ToFindForm(path, query);
            if (findForm != null)
            {
                if (!string.Equals(_findForm, findForm, StringComparison.Ordinal)) _dirty = true;
                _findForm = findForm;
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

        if (IsJunkUrl(path, query))
            return false;

        if (path.Contains("/3/", StringComparison.Ordinal))
            return false;

        if (query != null && query.Contains("query=", StringComparison.OrdinalIgnoreCase))
            return false;   // поиск — одноразовые URL

        return true;
    }

    /// <summary>
    /// URL, который КОРРЕКТНЫЙ клиент построить не может. Не «подозрительный», а невозможный.
    ///
    /// 🔥 Зачем (qdl 2.65). 23.08.2026 ручной прогон замеров с LAN налил в LRU 61 запись из 128:
    /// адреса с shell-экранированием (`?sort=top\&amp;genre=27`) и кэш-бастером. Заголовка
    /// X-QDL-Warmup он не слал, поэтому наблюдатель принял его за живого клиента. Половина
    /// бюджета прогрева ушла в мусор, настоящие ряды вытеснялись по LRU, а два адреса из этой
    /// пачки апстрим отдавал 404 вечно — и держали строку «CUB каталог» красной.
    ///
    /// Правила ТОЛЬКО структурные. Белый список ключей query сознательно отвергнут: бандл
    /// добавляет параметры динамически (params.filter), и первый же новый фильтр во фронте
    /// молча выключил бы прогрев целого класса рядов. Денилист по «zzr» — тоже: магическая
    /// строка под один инцидент.
    /// </summary>
    public static bool IsJunkUrl(string path, string query)
    {
        // 1. литеральный '\' — след shell-экранирования. Легальный клиент прислал бы %5C.
        // 3. управляющие символы — в URL их быть не может ни в пути, ни в query.
        if (HasJunkChars(path) || HasJunkChars(query))
            return true;

        // 2. '&' или '=' В ПУТИ: разделитель query, попавший в путь. Ловит "/blocked&zzr=1".
        //    Бандл строит query через add$7 — первый параметр всегда через '?', остальные
        //    через '&', так что в путь разделитель попасть не может.
        //    ⚠️ Легальный "/cub/tmdb.cub.rip/blocked" (qdl.js, апстрим 200) правило не трогает.
        if (path != null && (path.Contains('&') || path.Contains('=')))
            return true;

        return false;
    }

    static bool HasJunkChars(string s)
    {
        if (s == null) return false;
        foreach (char c in s)
            if (c == '\\' || c < ' ') return true;
        return false;
    }

    /// <summary>
    /// IsRowUrl для склеенной строки «путь+query» — в LRU и в файле состояния лежит именно она.
    /// Отдельный вход нужен, потому что Load() исторически звал IsRowUrl(SplitPath(pq), null),
    /// то есть query в фильтр не попадал вовсе и правила по нему к сохранённым рядам не применялись.
    /// </summary>
    internal static bool IsRowPathQuery(string pathQuery)
    {
        if (string.IsNullOrEmpty(pathQuery)) return false;
        int q = pathQuery.IndexOf('?');
        return q < 0 ? IsRowUrl(pathQuery, null)
                     : IsRowUrl(pathQuery.Substring(0, q), pathQuery.Substring(q));
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

    #region «Лента»: распознавание, форма find/ и разбор ответа (qdl 2.84)

    /// <summary>
    /// URL ленты: /cub/&lt;домен&gt;/api/feed/&lt;что угодно&gt;. Домен не пиним — CubProxy отдаёт ленту
    /// по тому хосту, который подставлен в бандл, и он меняется вместе с cub.mirror.
    /// </summary>
    public static bool IsFeedUrl(string path, string query)
    {
        if (path == null || !path.StartsWith("/cub/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsJunkUrl(path, query))
            return false;

        return path.Contains("/api/feed/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Клиентский find-по-imdb → форма с плейсхолдером {imdb}. null, если это не он.
    /// Понимает обе формы адреса: /tmdb/api/3/find/&lt;tt…&gt; и /cub/tmdb.&lt;домен&gt;/3/find/&lt;tt…&gt;.
    /// Принимаем ТОЛЬКО imdb-идентификатор (tt + цифры): find умеет и другие внешние источники,
    /// а подставлять мы будем именно imdb_id из ленты.
    /// </summary>
    public static string ToFindForm(string path, string query)
    {
        if (string.IsNullOrEmpty(path)) return null;
        query ??= string.Empty;

        if (IsJunkUrl(path, query)) return null;
        if (!query.Contains("external_source=imdb_id", StringComparison.OrdinalIgnoreCase)) return null;

        int i = path.IndexOf("/3/find/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        string head = path.Substring(0, i + "/3/find/".Length);
        string id = path.Substring(i + "/3/find/".Length);

        if (id.Length < 3 || !id.StartsWith("tt", StringComparison.Ordinal)) return null;
        for (int k = 2; k < id.Length; k++)
            if (id[k] < '0' || id[k] > '9') return null;

        return head + "{imdb}" + query;
    }

    /// <summary>
    /// imdb_id из ответа ленты. Форму ответа не пиним: обходим документ и собираем ЛЮБОЕ
    /// свойство imdb_id со значением вида tt&lt;цифры&gt; — так разбор переживёт смену обёртки
    /// (results/result/data), которую нам никто не обещал.
    /// </summary>
    public static List<string> ExtractImdbIds(byte[] body, int max)
    {
        var list = new List<string>();
        if (body == null || max <= 0) return list;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            Walk(doc.RootElement, 0);
        }
        catch { }
        return list;

        void Walk(JsonElement el, int depth)
        {
            if (list.Count >= max || depth > 8) return;

            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (list.Count >= max) return;

                    if (prop.NameEquals("imdb_id") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string v = prop.Value.GetString();
                        if (IsImdbId(v) && seen.Add(v)) list.Add(v);
                        continue;
                    }

                    Walk(prop.Value, depth + 1);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    if (list.Count >= max) return;
                    Walk(item, depth + 1);
                }
            }
        }
    }

    static bool IsImdbId(string v)
    {
        if (v == null || v.Length < 3 || !v.StartsWith("tt", StringComparison.Ordinal)) return false;
        for (int i = 2; i < v.Length; i++)
            if (v[i] < '0' || v[i] > '9') return false;
        return true;
    }

    /// <summary>IsFeedUrl для склеенной строки «путь+query» (в файле состояния лежит она).</summary>
    internal static bool IsFeedPathQuery(string pathQuery)
    {
        if (string.IsNullOrEmpty(pathQuery)) return false;
        int q = pathQuery.IndexOf('?');
        return q < 0 ? IsFeedUrl(pathQuery, null)
                     : IsFeedUrl(pathQuery.Substring(0, q), pathQuery.Substring(q));
    }

    internal static void NoteFeed(string scheme, string host, string pathQuery)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pathQuery)) return;

        var cur = _feed;
        if (cur != null && cur.host == host && cur.pathQuery == pathQuery)
        {
            cur.lastSeen = DateTime.UtcNow;
            // клиент попросил снова — снимаем карантин, как это делает Quarantined для рядов
            if (cur.deadAt != null) { cur.deadAt = null; cur.fails = 0; }
            _dirty = true;
            return;
        }

        _feed = new Entry { scheme = scheme ?? "http", host = host, pathQuery = pathQuery, lastSeen = DateTime.UtcNow };
        _dirty = true;
    }

    #endregion

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

        // Мусорный URL в ШАБЛОНЕ дороже мусорного ряда: ряд реплеится раз в тик, а шаблон —
        // до catalogWarmupCardBudget (16) раз, по разу на каждую греемую карточку.
        if (IsJunkUrl(path, query)) return null;

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

    internal static void Note(string scheme, string host, string pathQuery)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pathQuery)) return;

        string key = scheme + "|" + host + "|" + pathQuery;
        bool added = false;
        _rows.AddOrUpdate(key,
            _ => { added = true; return new Entry { scheme = scheme, host = host, pathQuery = pathQuery, lastSeen = DateTime.UtcNow }; },
            (_, en) => { en.lastSeen = DateTime.UtcNow; return en; });

        // 🔥 _dirty ставится и на ОБНОВЛЕНИИ (qdl 2.65). Раньше ранний return уходил выше него,
        // и подвинутый lastSeen не доезжал до диска: после падения по питанию (~23 раза в месяц)
        // прунинг мог выкинуть ряд, которым клиенты активно пользуются. Плюс без этого не
        // переживало рестарт снятие карантина «клиент попросил снова» (см. Quarantined).
        // Стоимость нулевая: Save() зовётся раз в конце тика, а не на запрос.
        _dirty = true;

        if (!added)
            return;

        int cap = Math.Max(8, ModInit.conf != null && ModInit.conf.catalogWarmupMaxUrls > 0 ? ModInit.conf.catalogWarmupMaxUrls : 128);
        while (_rows.Count > cap)
        {
            // карантинные уходят ПЕРВЫМИ: мёртвый адрес не должен вытеснять живой ряд по возрасту
            string victim = OldestKey(dead: true) ?? OldestKey(dead: false);
            if (victim == null || !_rows.TryRemove(victim, out _))
                break;
        }
    }

    /// <summary>Ключ самого давнего ряда; dead:true — только среди карантинных (null, если таких нет).</summary>
    static string OldestKey(bool dead)
    {
        string oldest = null;
        DateTime oldestAt = DateTime.MaxValue;
        foreach (var kv in _rows)
        {
            if (dead && kv.Value.deadAt == null) continue;
            if (kv.Value.lastSeen < oldestAt) { oldestAt = kv.Value.lastSeen; oldest = kv.Key; }
        }
        return oldest;
    }

    // ── карантин мёртвых рядов (qdl 2.65) ───────────────────────────────────────

    /// <summary>
    /// «Адреса не существует» против «сервису плохо». 4xx — приговор АДРЕСУ: повторять
    /// бессмысленно (404 — нет эндпоинта, 400 — кривой запрос, 401/403 — закрыто навсегда).
    /// Исключения: 408 (таймаут) и 429 (лимит) — временные, ряд в них не виноват.
    /// 5xx и code == 0 (не дошли / таймаут клиента) — тоже не про адрес.
    /// 3xx сюда не попадает как «мёртвый»: AllowAutoRedirect=false, редирект приходит как не-успех.
    /// </summary>
    internal static bool IsPermanentUrlError(int code)
        => code >= 400 && code < 500 && code != 408 && code != 429;

    /// <summary>
    /// Переход состояния карантина. Чистая функция над примитивами — Entry наружу не светим.
    /// deadAfter ≤ 0 — карантин выключен (киллсвитч из init.conf, без пересборки и рестарта):
    /// счётчик всё равно ведём, чтобы после включения не начинать с нуля.
    /// </summary>
    internal static (int fails, bool dead) RowQuarantine(int fails, bool dead, bool ok, int code, int deadAfter)
    {
        if (ok) return (0, false);                              // ответил — живой, карантин снят
        if (!IsPermanentUrlError(code)) return (fails, dead);   // 5xx/таймаут/429/408 — ряд ни при чём

        int n = fails + 1;
        if (deadAfter <= 0) return (n, dead);
        return (n, n >= deadAfter);
    }

    /// <summary>
    /// Пропускать ли ряд в этом тике. «Полуоткрытый» выключатель без лишних полей: deadAt значит
    /// сразу и «когда похоронен», и «когда последний раз пробовали».
    ///   • клиент попросил снова → Note() двигает lastSeen за deadAt → ровно ОДНА проба
    ///     в ближайшем тике; провалилась → deadAt = now, и условие снова закрыто.
    ///     Клиент, долбящий битый адрес, шума не создаёт: одна проба на его обращение, не на тик;
    ///   • плановая перепроверка раз в retryHours — вдруг апстрим вернул эндпоинт к жизни.
    /// </summary>
    static bool Quarantined(Entry en, DateTime now, int retryHours)
        => en.deadAt is DateTime d
           && en.lastSeen <= d
           && (now - d) < TimeSpan.FromHours(Math.Max(1, retryHours));

    // ── Засев рядов на сервер-реплику ───────────────────────────────────────────
    // Файлы Staticache между серверами непереносимы: ключ считается как Scheme+Host+Path+Query
    // (Core/Middlewares/Staticache.cs), а исходного URL в имени файла нет — оно и есть
    // односторонний хеш. Значит переносить надо не байты, а СПИСОК: реплика подставит свой
    // scheme/host и наполнит собственный кеш правильными ключами, сходив за телами напрямую
    // (её канал, домашний аплинк не участвует).

    /// <summary>
    /// Пути рядов (без scheme/host) для передачи на реплику, свежие вперёд.
    /// Карантинные не отдаём: незачем засевать реплике адреса, которые у нас уже мертвы.
    /// </summary>
    internal static List<string> ExportRowPaths()
        => _rows.Values.Where(e => e.deadAt == null)
                       .OrderByDescending(e => e.lastSeen)
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

            // 🔥 Тот же фильтр, что и в Load() (qdl 2.65). Раньше импорт принимал ЛЮБОЙ путь
            // на '/', и не-ряды (детали с /3/, мусор) оседали в LRU до первого рестарта —
            // Load их выбрасывал, а реплика тратила на них тик за тиком всё это время.
            if (!IsRowPathQuery(pq)) continue;

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

    internal static async Task Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) == 1)
            return;

        try
        {
            var conf = ModInit.conf;
            if (conf?.catalogWarmupEnabled != true)
                return;

            _tickHealth.Clear();   // агрегат «упало ВСЁ» живёт ровно один тик

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

            int deadAfter = Math.Max(0, conf.catalogWarmupDeadAfter);
            int deadRetryHours = Math.Max(1, conf.catalogWarmupDeadRetryHours);

            // свежие ряды первыми: их карточки первыми попадут под бюджеты постеров/фонов
            var rows = _rows.Values.OrderByDescending(e => e.lastSeen).ToArray();

            // 🔴 Предохранитель от «карантин съел всё»: если мёртвых больше половины — это уже
            // не кривые адреса, а сервис (CUB забанил IP и отдаёт 403 на всё). Без этого мы бы
            // перестали ходить наружу, наблюдений не стало бы, и Verdict завис бы на последнем
            // «ok» — зелёный экран при мёртвом апстриме, ровно то враньё, что чиним.
            int deadCount = rows.Count(e => e.deadAt != null);
            bool ignoreQuarantine = deadCount * 2 > rows.Length;

            int miss = 0, fail = 0, deadSkipped = 0;
            var posters = new List<(string key, string host, string scheme, string path)>();
            var backdrops = new List<(string key, string host, string scheme, string path)>();
            var cards = new List<WarmCard>();
            var posterSeen = new HashSet<string>(StringComparer.Ordinal);
            var backdropSeen = new HashSet<string>(StringComparer.Ordinal);
            var cardSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var en in rows)
            {
                if (Deploy.Draining) break;   // заморозка экземпляра (Deploy): дальше греть некому
                var now = DateTime.UtcNow;
                if (!ignoreQuarantine && Quarantined(en, now, deadRetryHours)) { deadSkipped++; continue; }

                var (ok, wasMiss, code, body, contentType) = await Fetch(port, en.scheme, en.host, en.pathQuery);

                var (f, d) = RowQuarantine(en.fails, en.deadAt != null, ok, code, deadAfter);
                if (f != en.fails || d != (en.deadAt != null) || d)
                {
                    en.fails = f;
                    en.lastCode = code;
                    // d && уже мёртв → освежаем метку пробы, иначе полуоткрытая проба
                    // повторялась бы каждый тик до конца retryHours
                    en.deadAt = d ? now : (DateTime?)null;
                    _dirty = true;
                }

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

            // обход рядов завершён — публикуем снимок аудита страниц (бюджеты постеров/карточек ниже — минуты)
            PublishPageAudit(rows.Length, Deploy.Draining);

            int posterMiss = await WarmList(port, posters, posterBudget, _posterCur, v => _posterCur = v);
            int backdropMiss = await WarmList(port, backdrops, backdropBudget, _backdropCur, v => _backdropCur = v);
            var (cardsDone, cardUrls, cardMiss) = await WarmCards(port, cards, cardBudget);

            var (feedOk, feedUrls, feedMiss) = conf.catalogWarmupFeed
                ? await WarmFeed(port, Math.Max(0, conf.catalogWarmupFeedBudget), deadAfter, deadRetryHours)
                : (false, 0, 0);

            // карантин в тригере по ИЗМЕНЕНИЮ, а не по факту: иначе строка про dead N печаталась бы
            // каждые 15 минут вечно. Пересчитываем после цикла — в нём состояние могло поменяться.
            int deadNow = _rows.Values.Count(e => e.deadAt != null);
            bool deadChanged = deadNow != _deadLogged;
            _deadLogged = deadNow;

            if (miss > 0 || fail > 0 || _pubBad > 0 || posterMiss > 0 || backdropMiss > 0 || cardMiss > 0 || feedMiss > 0 || deadChanged)
                Console.WriteLine($"[QbitDownload] catalog warmup: rows {rows.Length} (miss {miss}, fail {fail}, dead {deadNow}, skip {deadSkipped}{(ignoreQuarantine ? ", карантин отключён — мёртвых больше половины" : "")}), posters {Math.Min(posters.Count, posterBudget)}/{posters.Count} (miss {posterMiss}), backdrops {Math.Min(backdrops.Count, backdropBudget)}/{backdrops.Count} (miss {backdropMiss}), cards {cardsDone}/{cards.Count} ({cardUrls} url, miss {cardMiss}), tmpl {_tmpl.Count}, pages {_pubChecked} (bad {_pubBad}){(feedOk ? $", feed {feedUrls} url (miss {feedMiss})" : "")}");

            FlushTickHealth(Math.Max(1, conf.healthAllFailMinSamples));

            if (_dirty) { _dirty = false; Save(); }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>
    /// Прогрев «Ленты» (qdl 2.84). Два шага: сама лента, затем find/ по imdb_id из её ответа.
    ///
    /// Трафика наружу почти не добавляет: find/ живут сутки, лента — 3 часа, так что MISS дают
    /// только новые элементы. Бюджет считается в find-запросах; сама лента вне бюджета — ради
    /// неё всё и затевалось.
    /// </summary>
    static async Task<(bool ok, int urls, int miss)> WarmFeed(int port, int budget, int deadAfter, int deadRetryHours)
    {
        var feed = _feed;
        if (feed == null)
            return (false, 0, 0);

        var now = DateTime.UtcNow;
        if (Quarantined(feed, now, deadRetryHours))
            return (false, 0, 0);

        var (ok, wasMiss, code, body, contentType) = await Fetch(port, feed.scheme, feed.host, feed.pathQuery);

        var (f, d) = RowQuarantine(feed.fails, feed.deadAt != null, ok, code, deadAfter);
        if (f != feed.fails || d != (feed.deadAt != null) || d)
        {
            feed.fails = f;
            feed.lastCode = code;
            feed.deadAt = d ? now : (DateTime?)null;
            _dirty = true;
        }

        int urls = 1, miss = wasMiss ? 1 : 0;
        if (!ok)
            return (true, urls, miss);

        // формы ещё не видели (чистый том, лента не открывалась с этого образа) — греем только её
        string form = _findForm;
        if (form == null || budget <= 0 || body == null || body.Length > 3_000_000
            || contentType?.StartsWith("application/json") != true)
            return (true, urls, miss);

        foreach (string imdb in ExtractImdbIds(body, budget))
        {
            var r = await Fetch(port, feed.scheme, feed.host, form.Replace("{imdb}", imdb), readBody: false);
            urls++;
            if (r.miss) miss++;
            await Task.Delay(100);
        }

        return (true, urls, miss);
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
                var (ok, wasMiss, _, body, _) = await Fetch(port, card.scheme, card.host, p);
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
                    var (ok, wasMiss, _, _, _) = await Fetch(port, card.scheme, card.host, p);
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
            if (Deploy.Draining) break;   // заморозка экземпляра (Deploy)
            var it = list[(start + i) % list.Count];
            var (ok, wasMiss, _, _, _) = await Fetch(port, it.scheme, it.host, it.path, readBody: true);
            if (ok && wasMiss) missCount++;
            saveCursor(it.key);
            await Task.Delay(100);
        }
        return missCount;
    }

    static async Task<(bool ok, bool miss, int code, byte[] body, string contentType)> Fetch(int port, string scheme, string host, string pathQuery, bool readBody = true)
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
            NotePage(host, pathQuery, body, contentType, miss, rs);
            return (rs.IsSuccessStatusCode, miss, (int)rs.StatusCode, body, contentType);
        }
        catch
        {
            NoteHealth(pathQuery, false, 0, true);   // до апстрима не дошли — наблюдение честное
            return (false, false, 0, null, null);
        }
    }

    /// <summary>
    /// Пассивный хелс-чек внешних метаданных (HealthState.cs): прогрев регулярно тянет ряды,
    /// детали и постеры, поэтому отдельные пробы ради экрана не нужны.
    ///
    /// Два правила, симметричных друг другу:
    ///  • 🔥 считаем ТОЛЬКО MISS. Запрос идёт на НАШ /tmdb/*|/cub/* на loopback, и HIT в Staticache
    ///    отвечает 200, вообще не ходя наружу — засчитывать его как «TMDB работает» значило бы
    ///    повторить ровно ту ложную зелень, из-за которой пробы и переделывались;
    ///  • 🔥 4xx НЕ красит сервис (qdl 2.65) — это свойство адреса, а не апстрима; см. ClassifyHealth.
    /// </summary>
    internal static void NoteHealth(string pathQuery, bool ok, int code, bool miss)
    {
        if (string.IsNullOrEmpty(pathQuery)) return;
        try
        {
            string id = HealthIdFor(pathQuery);
            if (id == null) return;

            switch (ClassifyHealth(ok, code, miss))
            {
                case HealthOutcome.Ok:
                    // OkDirect, а не Ok: Degraded липкий и снимается только ClearDegraded —
                    // на эти грабли уже наступали в 2.58 (ReplicaSync).
                    HealthState.OkDirect(id);
                    Tally(id, ok: 1);
                    break;

                case HealthOutcome.Fail:
                    HealthState.Fail(id, code > 0 ? "http " + code : "не ответил");
                    Tally(id, hard: 1);
                    break;

                case HealthOutcome.Degraded:
                    HealthState.Degraded(id, "http " + code + " — лимит апстрима");
                    Tally(id, soft: 1);
                    break;

                default:
                    // 4xx: здоровье сервиса молчит, но исход идёт в агрегат тика —
                    // «упало ВСЁ» это уже не адреса
                    if (miss && !ok) Tally(id, soft: 1);
                    break;
            }
        }
        catch { }
    }

    // public, как и соседние чистые предикаты: тип фигурирует в сигнатуре тестов ([Theory] с ним
    // в параметрах), а internal-enum в public-методе теста даёт CS0051.
    public enum HealthOutcome { Skip, Ok, Fail, Degraded }

    /// <summary>
    /// Как ОДИН исход прогрева отражается на здоровье СЕРВИСА. Чистая функция.
    ///
    /// 🔥 Симметрия к правилу «считаем только MISS» (qdl 2.65). HIT не доказывает, что сервис
    /// работает, — и ровно так же 4xx на конкретном адресе не доказывает, что сервис сломан.
    /// «Не найден ОДИН URL» — это дефект нашего списка рядов (его лечит карантин), а не авария
    /// CUB/TMDB. До 2.65 один вечный 404 держал строку «CUB каталог» красной месяцами: живые
    /// ряды лежат в Staticache 3 часа и дают HIT (в хелс не попадают вовсе), а 404 кешируется
    /// на минуту и потому каждый тик приходит как свежий MISS+fail.
    ///
    /// Fail-open здесь не возникает: массовый 4xx (отозванный api_key, бан по IP) ловит
    /// агрегат тика FlushTickHealth.
    /// </summary>
    internal static HealthOutcome ClassifyHealth(bool ok, int code, bool miss)
    {
        if (!miss) return HealthOutcome.Skip;                   // HIT — апстрима не касались
        if (ok) return HealthOutcome.Ok;
        if (code == 0) return HealthOutcome.Fail;               // не дошли вовсе / таймаут
        if (code == 408 || code == 429) return HealthOutcome.Degraded;
        if (code >= 500) return HealthOutcome.Fail;
        return HealthOutcome.Skip;                              // 3xx и 4xx — свойство АДРЕСА
    }

    internal static string HealthIdFor(string pathQuery)
        => pathQuery == null ? null
         : pathQuery.StartsWith("/tmdb/img/", StringComparison.OrdinalIgnoreCase) ? HealthState.Ids.TmdbImg
         : pathQuery.StartsWith("/tmdb/api/", StringComparison.OrdinalIgnoreCase) ? HealthState.Ids.TmdbApi
         : pathQuery.StartsWith("/cub/tmdb.", StringComparison.OrdinalIgnoreCase)
               ? (pathQuery.Contains("/3/", StringComparison.Ordinal) ? HealthState.Ids.TmdbApi : HealthState.Ids.Cub)
         : null;

    #region аудит номера страницы (qdl 2.112)
    // ── Второй, независимый сторож дефекта §DI/§DO ────────────────────────────────────────────
    // Дефект, замеры и правила — шапка Modules/Proxy/CubProxy/PageGuard.cs. Здесь — почему копия:
    // предотвращение в CubProxy видит ТОЛЬКО промахи (на HIT контроллер не исполняется), а
    // отравленная запись раздаётся клиентам именно с HIT-ов все три часа. Прогрев ходит по
    // РЕАЛЬНЫМ клиентским ключам (86 адресов в боевом реестре) и видит то, что получает зритель.
    //
    // 🔴 Логика намеренно продублирована с CubProxy.PageGuard — модули в разных сборках. Обе
    // копии на ОДНОМ парсере (Newtonsoft, целые числа только как integer/string): на дробных
    // числах и нестрогом JSON они иначе расходились. Расхождение ловит тест
    // «Сторож_страницы_в_двух_модулях_судит_одинаково» на корпусе сырых тел. Правишь там —
    // правь и здесь.
    public enum PageVerdict { Skip, Match, Mismatch }

    /// <summary>Сколько примеров расхождения показываем владельцу за обход.</summary>
    const int PageSampleCap = 8;

    /// <summary>Тот же заголовок, что PageGuard.HeaderName в CubProxy — типами не связаться, поэтому строка.</summary>
    const string CubPageHeader = "X-QDL-Page";

    // Текущий обход КОПИТСЯ, последний завершённый ПУБЛИКУЕТСЯ атомарно сразу после цикла рядов
    // (образец — _tickHealth → FlushTickHealth). Иначе всё время обхода строка хелса читала бы
    // полупустые счётчики и показывала «off — рядов не было» каждые 15 минут.
    static readonly object _pageLock = new();
    static int _curChecked, _curBad, _curBadMain, _curHealed, _curRestored, _curMismatch, _curFuse;
    static readonly List<string> _curSample = new();
    static int _pubChecked, _pubBad, _pubBadMain, _pubRows, _pubHealed, _pubRestored, _pubMismatch, _pubFuse;
    static bool _pubPartial;
    static List<string> _pubSample = new();
    static DateTime _pageAt;

    /// <summary>Эффективный период тика — одна формула на таймер и на снимок хелса.</summary>
    internal static int EffectivePeriodMin()
        => Math.Max(5, ModInit.conf != null && ModInit.conf.catalogWarmupPeriodMin > 0 ? ModInit.conf.catalogWarmupPeriodMin : 15);

    /// <summary>Какую страницу просили — копия PageGuard.RequestedPage (нет параметра → 1; дубль с разными значениями → null).</summary>
    internal static int? RequestedPage(string pathQuery)
    {
        if (pathQuery == null) return null;

        int q = pathQuery.IndexOf('?');
        if (q < 0) return 1;

        int? found = null;

        foreach (var pair in pathQuery.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0 || !pair.Substring(0, eq).Equals("page", StringComparison.OrdinalIgnoreCase))
                continue;

            int? p = int.TryParse(pair.Substring(eq + 1), out int v) && v >= 1 ? v : (int?)null;
            if (found.HasValue && found != p) return null;
            if (!p.HasValue) return null;
            found = p;
        }

        return found ?? 1;
    }

    /// <summary>Форма тела — копия PageGuard.Shape. results = -1, если это не наша форма (у /blocked — массив).</summary>
    internal static (int? page, int? totalPages, int results) Shape(byte[] body)
    {
        if (body == null || body.Length == 0) return (null, null, -1);

        try
        {
            if (Newtonsoft.Json.JsonConvert.DeserializeObject<JToken>(Encoding.UTF8.GetString(body)) is not JObject o)
                return (null, null, -1);

            int results = o["results"] is JArray arr ? arr.Count : -1;
            return (PageInt(o["page"]), PageInt(o["total_pages"]), results);
        }
        catch { return (null, null, -1); }
    }

    static int? PageInt(JToken t)
        => t is JValue v && (v.Type is JTokenType.Integer or JTokenType.String)
           && int.TryParse(v.ToString(), out int n) ? n : (int?)null;

    /// <summary>Вердикт с цифрами — копия PageGuard.Judge (кламп признаём, только если тело на него похоже).</summary>
    internal static (PageVerdict verdict, int? wanted, int? got, int results) Judge(string pathQuery, byte[] body)
    {
        int? wanted = RequestedPage(pathQuery);
        var (page, totalPages, results) = Shape(body);

        if (!wanted.HasValue || results < 0 || !page.HasValue)
            return (PageVerdict.Skip, wanted, page, results);

        // Кламп признаём, только если тело на него похоже (пришла первая или последняя страница).
        // total_pages ≤ 0 — мусор, судим по page; ноль при пустых results — пустая лента, судить нечего.
        if (totalPages is >= 1 && wanted.Value > totalPages.Value && (page.Value == 1 || page.Value == totalPages.Value))
            return (PageVerdict.Skip, wanted, page, results);

        if (totalPages == 0 && results == 0)
            return (PageVerdict.Skip, wanted, page, results);

        return (page.Value == wanted.Value ? PageVerdict.Match : PageVerdict.Mismatch, wanted, page, results);
    }

    internal static PageVerdict CheckPage(string pathQuery, byte[] body) => Judge(pathQuery, body).verdict;

    /// <summary>
    /// Ряд, который зритель видит на главной: первая страница рядов свежести (те же sort, что у
    /// фильтра по году). Чужая страница там — ровно жалоба владельца, и это fail, а не warn.
    /// </summary>
    internal static bool IsMainRow(string pathQuery, int wanted)
    {
        if (wanted != 1 || pathQuery == null) return false;
        var m = System.Text.RegularExpressions.Regex.Match(pathQuery, @"[?&]sort=([^&]+)");
        if (!m.Success) return false;
        string sort = m.Groups[1].Value;
        return sort is "now_playing" or "latest" or "now" or "airing";
    }

    /// <summary>Короткое имя ряда для строки хелса: sort или хвост пути.</summary>
    static string ShortRow(string pathQuery)
    {
        pathQuery ??= "";
        var m = System.Text.RegularExpressions.Regex.Match(pathQuery, @"[?&]sort=([^&]+)");
        if (m.Success) return m.Groups[1].Value;
        int q = pathQuery.IndexOf('?');
        string path = q < 0 ? pathQuery : pathQuery.Substring(0, q);
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path.Substring(slash + 1) : path;
    }

    /// <summary>Учесть один ответ ряда. internal — под тестами (HttpResponseMessage собирается руками).</summary>
    internal static void NotePage(string host, string pathQuery, byte[] body, string contentType, bool miss, HttpResponseMessage rs)
    {
        try
        {
            // только ряды каталога: постеры, детали карточки и лента отсекаются селектором хелса
            if (HealthIdFor(pathQuery) != HealthState.Ids.Cub) return;
            if (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return;

            // На своих ПРОМАХАХ обход видит и след сторожа контроллера (X-QDL-Page) — единственный
            // способ показать владельцу «сторож вмешивался N раз» без канала между сборками.
            string guard = rs != null && rs.Headers.TryGetValues(CubPageHeader, out var g) ? g.FirstOrDefault() : null;

            var (v, wanted, got, _) = Judge(pathQuery, body);
            if (v == PageVerdict.Skip && guard == null) return;

            bool main = IsMainRow(pathQuery, wanted ?? 0);
            int badNow;

            lock (_pageLock)
            {
                switch (guard)
                {
                    case "healed": _curHealed++; break;
                    case "restored": _curRestored++; break;
                    case "mismatch": _curMismatch++; break;
                    case "fuse": _curFuse++; break;
                }

                if (v == PageVerdict.Skip) return;

                _curChecked++;
                if (v == PageVerdict.Match) return;

                _curBad++;
                if (main) _curBadMain++;

                if (_curSample.Count < PageSampleCap)
                    _curSample.Add(host + " " + ShortRow(pathQuery) + " p" + wanted + "→" + got + " " + (miss ? "MISS" : "HIT") + (main ? " ГЛАВНАЯ" : ""));

                badNow = _curBad;
            }

            // §DI: ответ сам называет свой файл в кеше — это избавляет от покоса всех рядов
            string bucket = rs != null && rs.Headers.TryGetValues("X-StatiCache-Bucket", out var b) ? b.FirstOrDefault() : null;
            string id = rs != null && rs.Headers.TryGetValues("X-StatiCache-Id", out var i) ? i.FirstOrDefault() : null;

            // Журнал ВЛАДЕЛЬЦА: чисто диагностическое событие, строку в noti не создаём —
            // зритель кухню не видит (образец SearchMonitor). Ключ — с хостом: отравление
            // ПО-ХОСТОВОЕ (три ключа Staticache на один ряд), и команда сноса у каждого своя.
            // Окно дедупа = cache_api: после протухания записи новое отравление — новое событие.
            if (badNow > PageSampleCap)
            {
                // устойчивая поломка: не 86 одинаковых строк, а одна сводная на тик
                if (!QdlEvents.Recent(QdlEvents.CatDiag, "cubpage:summary", TimeSpan.FromHours(1)))
                    QdlEvents.Log(QdlEvents.CatDiag, "Каталог CUB",
                        "Чужая страница ещё в " + badNow + " записях обхода — см. строку «CUB: номер страницы» в хелс-чеках",
                        key: "cubpage:summary");
                return;
            }

            string key = "cubpage:" + host + pathQuery;
            if (QdlEvents.Recent(QdlEvents.CatDiag, key, TimeSpan.FromHours(3)))
                return;

            string where = host + pathQuery + " · просили " + wanted + ", в теле " + got;

            if (miss)
            {
                // На MISS в кеш ничего не попало (сторож поставил saveCache:false) — снос не нужен,
                // нужно понять, почему не подменил: копии нет, предохранитель или pageGuard выключен.
                QdlEvents.Log(QdlEvents.CatDiag, "Каталог CUB",
                    "CUB отдал чужую страницу живьём, сторож не подменил (" + (guard ?? "заголовка нет — pageGuard выключен?") + "): " + where + " · в кеш не попало",
                    key: key);
            }
            else
            {
                bool known = bucket != null && id != null;
                string act = known
                    ? "docker run --rm -v media-server_lampac-cache:/c alpine sh -c 'rm -f /c/static/" + bucket + "/" + id + "*' — затем scripts\\deploy-lampac.ps1 (реестр Staticache строится при старте; под живым цветом rm даёт 500), либо переждать TTL до 3 ч"
                    : null;

                QdlEvents.Log(QdlEvents.CatDiag, "Каталог CUB",
                    "Запись кеша отдаёт чужую страницу: " + where + (known ? " · static/" + bucket + "/" + id + "*" : ""),
                    act: act, key: key);
            }
        }
        catch { }   // аудит не имеет права уронить прогрев
    }

    /// <summary>Опубликовать завершённый обход рядов и обнулить накопитель. Зовётся сразу после цикла рядов в Tick.</summary>
    static void PublishPageAudit(int rows, bool partial)
    {
        lock (_pageLock)
        {
            _pubChecked = _curChecked; _pubBad = _curBad; _pubBadMain = _curBadMain;
            _pubHealed = _curHealed; _pubRestored = _curRestored; _pubMismatch = _curMismatch; _pubFuse = _curFuse;
            _pubRows = rows; _pubPartial = partial;
            _pubSample = new List<string>(_curSample);
            _pageAt = DateTime.UtcNow;

            _curChecked = _curBad = _curBadMain = _curHealed = _curRestored = _curMismatch = _curFuse = 0;
            _curSample.Clear();
        }
    }

    /// <summary>Снимок для строки хелса — образец MusicWarm.HealthSnapshot. Описывает ПОСЛЕДНИЙ завершённый обход.</summary>
    internal static JObject PageHealthSnapshot()
    {
        lock (_pageLock)
            return new JObject
            {
                ["enabled"] = ModInit.conf?.catalogWarmupEnabled == true,
                ["timer"] = _timer != null,
                ["checked"] = _pubChecked,
                ["bad"] = _pubBad,
                ["badMain"] = _pubBadMain,
                ["rows"] = _pubRows,
                ["partial"] = _pubPartial,
                ["at"] = _pageAt == default ? null : _pageAt.ToString("o"),
                ["periodMin"] = EffectivePeriodMin(),
                ["samples"] = new JArray(_pubSample),
                ["guard"] = new JObject
                {
                    ["healed"] = _pubHealed, ["restored"] = _pubRestored,
                    ["mismatch"] = _pubMismatch, ["fuse"] = _pubFuse
                }
            };
    }

    /// <summary>Тестовый хук: опубликовать накопленное как завершённый обход.</summary>
    internal static void PublishPageAuditForTests(int rows, bool partial = false) => PublishPageAudit(rows, partial);

    /// <summary>Сброс аудита — только для тестов. Promote/Reload снимок НЕ трогают: он описывает обход этого процесса.</summary>
    internal static void ResetPageAuditForTests()
    {
        lock (_pageLock)
        {
            _curChecked = _curBad = _curBadMain = _curHealed = _curRestored = _curMismatch = _curFuse = 0;
            _pubChecked = _pubBad = _pubBadMain = _pubRows = _pubHealed = _pubRestored = _pubMismatch = _pubFuse = 0;
            _pubPartial = false;
            _curSample.Clear();
            _pubSample = new List<string>();
            _pageAt = default;
        }
    }
    #endregion

    // Исходы MISS текущего тика по id хелса; живёт ровно один тик (Clear в начале Tick).
    static readonly ConcurrentDictionary<string, (int ok, int soft, int hard)> _tickHealth = new();

    static void Tally(string id, int ok = 0, int soft = 0, int hard = 0)
        => _tickHealth.AddOrUpdate(id, (ok, soft, hard),
            (_, v) => (v.ok + ok, v.soft + soft, v.hard + hard));

    /// <summary>
    /// 🔴 Антидот fail-open. Если за тик по сервису НЕ БЫЛО ни одного успеха и ни одного жёсткого
    /// сбоя, а мягких (4xx) набралось minSamples и больше — «не найден один адрес» превращается
    /// в «сервис отвечает отказом на всё». Порог нужен, чтобы пара кривых URL в тихом тике не
    /// выдавалась за аварию. Карантинные ряды сюда не попадают: их не запрашивают.
    /// </summary>
    internal static void FlushTickHealth(int minSamples)
    {
        int need = Math.Max(1, minSamples);
        foreach (var kv in _tickHealth)
        {
            var (ok, soft, hard) = kv.Value;
            if (ok == 0 && hard == 0 && soft >= need)
                HealthState.Fail(kv.Key, "http 4xx на всех " + soft + " "
                    + HealthState.Plural(soft, "запросе", "запросах", "запросах") + " прогрева");
        }
        _tickHealth.Clear();
    }

    #region тестовый доступ
    // Статика класса течёт между кейсами (в тест-проекте параллелизм отключён, но не изоляция),
    // а Entry наружу не светим — поэтому узкие хелперы вместо публикации внутренностей.
    // Тот же приём, что у HealthState.ResetForTests.

    internal static void ResetForTests()
    {
        _rows.Clear();
        _tmpl.Clear();
        _cardIds.Clear();
        _cardFirstSeen.Clear();
        _tickHealth.Clear();
        _posterCur = _backdropCur = _cardCur = null;
        _dirty = false;
        _deadLogged = -1;
        // ⚠️ аудит страниц здесь НЕ сбрасываем: ResetForTests зовётся из Reload() при promote, а снимок
        // описывает обход ЭТОГО процесса и роли не касается. Тестам — ResetPageAuditForTests().
    }

    internal static bool DirtyForTests { get => _dirty; set => _dirty = value; }

    /// <summary>Пути рядов в LRU, включая карантинные (ExportRowPaths их отфильтровывает).</summary>
    internal static List<string> RowPathsForTests()
        => _rows.Values.Select(e => e.pathQuery).OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>Отправить ряд в карантин вручную — для тестов вытеснения и round-trip персиста.</summary>
    internal static bool MarkDeadForTests(string scheme, string host, string pathQuery, DateTime deadAt, int fails = 3)
    {
        if (!_rows.TryGetValue(scheme + "|" + host + "|" + pathQuery, out var en)) return false;
        en.deadAt = deadAt;
        en.fails = fails;
        return true;
    }

    /// <summary>Состояние карантина ряда: (есть ли запись, в карантине ли, сколько провалов).</summary>
    internal static (bool found, bool dead, int fails) RowStateForTests(string scheme, string host, string pathQuery)
        => _rows.TryGetValue(scheme + "|" + host + "|" + pathQuery, out var en)
            ? (true, en.deadAt != null, en.fails)
            : (false, false, 0);
    #endregion

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

        // qdl 2.84, оба АДДИТИВНЫЕ: старый файл их не содержит (останутся null — «ленту ещё
        // не видели»), старый образ молча проигнорирует. ver не бампаем по той же причине,
        // что и в 2.65 — откат образа не должен требовать отката данных.
        public Entry feed { get; set; }
        public string findForm { get; set; }
    }

    internal static void Load()
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

                    // тот же фильтр, что на приёме: мусор не должен переживать рестарт
                    if (st.feed != null && !string.IsNullOrEmpty(st.feed.host) && IsFeedPathQuery(st.feed.pathQuery))
                        _feed = st.feed;

                    if (!string.IsNullOrEmpty(st.findForm) && st.findForm.Contains("{imdb}", StringComparison.Ordinal))
                        _findForm = st.findForm;
                }
            }

            // v1-файлы могли содержать детали, а прогон замеров 23.08.2026 — мусорные адреса.
            // IsRowPathQuery, а не IsRowUrl(SplitPath(...), null): второй терял query, поэтому
            // правила по query к УЖЕ НАКОПЛЕННЫМ рядам не применялись. Теперь накопленный мусор
            // отсеивается на первом же старте нового образа, без ручной хирургии по тому.
            foreach (var en in rows ?? new List<Entry>())
                if (!string.IsNullOrEmpty(en?.host) && IsRowPathQuery(en.pathQuery))
                    _rows.TryAdd((en.scheme ?? "http") + "|" + en.host + "|" + en.pathQuery, en);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup load: " + ex.Message); }
    }

    internal static void Save()
    {
        if (!Deploy.WarmSavesAllowed) return;   // дежурный/замороженный экземпляр: файл принадлежит ведущему
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
                firstSeen = _cardFirstSeen.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                feed = _feed,
                findForm = _findForm
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
