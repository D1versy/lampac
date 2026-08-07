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
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Прогрев каталога главной (qdl 2.15, v2 в 2.16; карта — E:\Media-server\claude\11) ──
// Каталог CUB идёт через CubProxy (/cub/tmdb.*) и кешируется Staticache на 3 ч; по истечении TTL
// первый живой клиент ловил MISS (поход в tmdb.cub.red на его времени). Здесь: запоминаем URL
// РЯДОВ главной и дёргаем их сами по таймеру; v2 — из ответов рядов достаём карточки и греем
// ещё ПОСТЕРЫ (/tmdb/img, TTL год) и ДЕТАЛИ (/cub/tmdb./3/..., TTL 3 ч) — открытие карточки
// у клиента всегда попадает в тёплый кеш (2-3 мс вместо 150-300 мс MISS). Наружу это НЕ добавляет
// трафика сверх прежнего: HIT отвечает кеш с SSD, MISS — ровно тот запрос, что сделал бы клиент.
// Тонкости (не видны из кода по отдельности):
//  • подписка EventListener.Middleware (first:true) стоит в пайплайне ПОСЛЕ UseStaticache —
//    HIT-ы до нас не доходят, наблюдаем только MISS; для сбора достаточно (URL миснёт хоть раз);
//  • ключ Staticache = Scheme+Host+Path+Query (Staticache.getQueryKeys) → реплей обязан нести
//    ОРИГИНАЛЬНЫЙ Host (и X-Forwarded-Proto для записей со scheme=https), иначе греется чужой ключ;
//  • ключ деталей обязан посимвольно совпасть с клиентским по api_key/append_to_response/language
//    (email/uid — в SkipQueryKeys, не важны) → шаблон query СНИМАЕМ с реального клиентского
//    запроса деталей; до первого наблюдения — дефолт из CoreInit.conf.cub.api_key (тот же ключ
//    зашит в бандле; ⚠️ TMDB_API_KEY из .env — ДРУГОЙ ключ, с ним прогрелся бы чужой кеш-ключ);
//  • реплей на 127.0.0.1:<port> — локальный запрос, D1VPerimeter пропускает;
//  • тело ответа дочитываем целиком: StaticacheWriter сохраняет запись по завершении ответа;
//  • ?query= (поиск) не запоминаем — одноразовые URL забили бы LRU-кап;
//  • детали/постеры НЕ персистим и не трекаем «когда грел»: их прогрев — это GET через свой кеш,
//    живой кеш = HIT почти бесплатно, протухший = тот самый нужный MISS; бюджеты на тик +
//    ротация курсора покрывают хвост за несколько тиков.
public static class CatalogWarmup
{
    sealed class Entry
    {
        public string scheme { get; set; }
        public string host { get; set; }
        public string pathQuery { get; set; }
        public DateTime lastSeen { get; set; }
    }

    public readonly record struct Card(long id, bool tv, string poster);

    static readonly ConcurrentDictionary<string, Entry> _rows = new();
    static readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    static Timer _timer;
    static int _ticking = 0;
    static bool _dirty;
    static int _posterOffset, _detailOffset;          // ротация хвоста между тиками
    static volatile string _detailQuery;              // шаблон query деталей, снятый с живого клиента

    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "catalog-warmup.json");

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
            if (path == null || !path.StartsWith("/cub/tmdb.", StringComparison.OrdinalIgnoreCase)) return true;

            string query = req.QueryString.HasValue ? req.QueryString.Value : string.Empty;

            if (IsRowUrl(path, query))
                Note(req.Scheme, req.Host.Value, path + query);
            else if (IsDetailUrl(path) && query.Contains("api_key=", StringComparison.OrdinalIgnoreCase))
                _detailQuery = query;   // живой шаблон точнее дефолта (language/append_to_response клиента)
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

    // Деталь карточки: /cub/tmdb.<mirror>/3/movie/<id> | /3/tv/<id> (сезоны/прочее не считаем)
    public static bool IsDetailUrl(string path)
    {
        if (path == null || !path.StartsWith("/cub/tmdb.", StringComparison.OrdinalIgnoreCase))
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

                cards.Add(new Card(id, tv, poster));
            }
        }
        catch { }
        return cards;
    }

    public static string DefaultDetailQuery(string apiKey)
        => "?api_key=" + apiKey + "&append_to_response=content_ratings,release_dates,keywords,alternative_titles&language=ru";

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
            int detailBudget = Math.Max(0, conf.catalogWarmupDetailBudget);

            // свежие ряды первыми: их карточки первыми попадут под бюджеты постеров/деталей
            var rows = _rows.Values.OrderByDescending(e => e.lastSeen).ToArray();

            int miss = 0, fail = 0;
            var posters = new List<(string host, string scheme, string path)>();
            var details = new List<(string host, string scheme, string path)>();
            var posterSeen = new HashSet<string>(StringComparer.Ordinal);
            var detailSeen = new HashSet<string>(StringComparer.Ordinal);

            string detailQuery = _detailQuery;
            if (string.IsNullOrEmpty(detailQuery))
            {
                string apiKey = null;
                try { apiKey = CoreInit.conf.cub?.api_key; } catch { }
                detailQuery = string.IsNullOrEmpty(apiKey) ? null : DefaultDetailQuery(apiKey);
            }

            foreach (var en in rows)
            {
                var (ok, wasMiss, body, contentType) = await Fetch(port, en.scheme, en.host, en.pathQuery);
                if (!ok) { fail++; continue; }
                if (wasMiss) miss++;

                // карточки ряда → очереди прогрева (дедуп по host+идентичности между рядами)
                if (body != null && body.Length < 3_000_000 && contentType?.StartsWith("application/json") == true)
                {
                    foreach (var card in ExtractCards(body, cardsPerRow))
                    {
                        if (!string.IsNullOrEmpty(card.poster) && card.poster.StartsWith('/')
                            && posterSeen.Add(en.host + "|" + card.poster))
                            posters.Add((en.host, en.scheme, "/tmdb/img/t/p/w300" + card.poster));

                        if (detailQuery != null && detailSeen.Add(en.host + "|" + card.tv + "|" + card.id))
                            details.Add((en.host, en.scheme, "/cub/tmdb./3/" + (card.tv ? "tv/" : "movie/") + card.id + detailQuery));
                    }
                }

                await Task.Delay(100);
            }

            int posterMiss = await WarmList(port, posters, posterBudget, _posterOffset, v => _posterOffset = v);
            int detailMiss = await WarmList(port, details, detailBudget, _detailOffset, v => _detailOffset = v);

            if (miss > 0 || fail > 0 || posterMiss > 0 || detailMiss > 0)
                Console.WriteLine($"[QbitDownload] catalog warmup: rows {rows.Length} (miss {miss}, fail {fail}), posters {Math.Min(posters.Count, posterBudget)}/{posters.Count} (miss {posterMiss}), details {Math.Min(details.Count, detailBudget)}/{details.Count} (miss {detailMiss})");

            if (_dirty) { _dirty = false; Save(); }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    // Прогрев списка с ротацией курсора: за несколько тиков покрывается весь хвост, даже если он больше бюджета
    static async Task<int> WarmList(int port, List<(string host, string scheme, string path)> list, int budget, int offset, Action<int> saveOffset)
    {
        int missCount = 0, todo = Math.Min(budget, list.Count);
        for (int i = 0; i < todo; i++)
        {
            var it = list[(offset + i) % list.Count];
            var (ok, wasMiss, _, _) = await Fetch(port, it.scheme, it.host, it.path, readBody: true);
            if (ok && wasMiss) missCount++;
            await Task.Delay(100);
        }
        if (list.Count > 0)
            saveOffset((offset + todo) % list.Count);
        return missCount;
    }

    static async Task<(bool ok, bool miss, byte[] body, string contentType)> Fetch(int port, string scheme, string host, string pathQuery, bool readBody = true)
    {
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{pathQuery}");
            rq.Headers.TryAddWithoutValidation("Host", host);
            if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                rq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

            using var rs = await _http.SendAsync(rq);
            byte[] body = readBody ? await rs.Content.ReadAsByteArrayAsync() : null;

            bool miss = rs.Headers.TryGetValues("X-StatiCache-Status", out var st) && st.FirstOrDefault() == "MISS";
            string contentType = rs.Content.Headers.ContentType?.MediaType != null
                ? rs.Content.Headers.ContentType.MediaType : null;

            return (rs.IsSuccessStatusCode, miss, body, contentType);
        }
        catch
        {
            return (false, false, null, null);
        }
    }

    #region persist
    static void Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return;

            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StorePath));
            if (list == null)
                return;

            foreach (var en in list)
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
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_rows.Values.ToList()));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup save: " + ex.Message); }
    }
    #endregion
}
