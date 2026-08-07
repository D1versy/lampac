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

// ── Прогрев каталога главной (qdl 2.15, карта внешних источников — E:\Media-server\claude\11) ──
// Каталог CUB идёт через CubProxy (/cub/tmdb.*) и кешируется Staticache на 3 ч (CubProxy/ModInit
// cache_api=180); раньше по истечении TTL первый живой клиент ловил MISS — поход сервера в
// tmdb.cub.red на его времени. Здесь: запоминаем каталожные URL клиентов и дёргаем их сами по
// таймеру — протухшее обновляет наш тик. Наружу это НЕ добавляет трафика сверх прежнего:
// HIT-проверки отвечает кеш с SSD, MISS — ровно тот запрос, который иначе сделал бы клиент.
// Тонкости (не видны из кода ниже по отдельности):
//  • подписка EventListener.Middleware (first:true) стоит в пайплайне ПОСЛЕ UseStaticache —
//    HIT-ы до нас не доходят, наблюдаем только MISS; для сбора достаточно (URL миснёт хоть раз);
//  • ключ Staticache = Scheme+Host+Path+Query (Staticache.getQueryKeys) → реплей обязан нести
//    ОРИГИНАЛЬНЫЙ Host (и X-Forwarded-Proto для записей со scheme=https), иначе греется чужой ключ;
//  • реплей на 127.0.0.1:<port> — локальный запрос, D1VPerimeter его пропускает;
//  • тело ответа дочитываем целиком: StaticacheWriter сохраняет запись по завершении записи ответа,
//    оборванный reader мог бы оставить кеш необновлённым;
//  • ?query= (поиск) не запоминаем — одноразовые URL забили бы LRU-кап.
public static class CatalogWarmup
{
    sealed class Entry
    {
        public string scheme { get; set; }
        public string host { get; set; }
        public string pathQuery { get; set; }
        public DateTime lastSeen { get; set; }
    }

    static readonly ConcurrentDictionary<string, Entry> _urls = new();
    static readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    static Timer _timer;
    static int _ticking = 0;
    static bool _dirty;

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

    // Наблюдатель пайплайна: только учёт, всегда true (запрос не трогаем). Зовётся на каждый
    // не-HIT запрос сервера → первые проверки максимально дешёвые.
    public static bool OnRequest(bool first, EventMiddleware e)
    {
        try
        {
            if (!first || ModInit.conf?.catalogWarmupEnabled != true) return true;

            var req = e.httpContext?.Request;
            if (req == null || !HttpMethods.IsGet(req.Method)) return true;

            string path = req.Path.Value;
            string query = req.QueryString.HasValue ? req.QueryString.Value : string.Empty;
            if (!IsCatalogUrl(path, query)) return true;

            Note(req.Scheme, req.Host.Value, path + query);
        }
        catch { }
        return true;
    }

    // чистый предикат «этот URL — каталожный, запоминаем» — покрыт Tests/QbitDownload.Tests
    public static bool IsCatalogUrl(string path, string query)
    {
        if (path == null || !path.StartsWith("/cub/tmdb.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (query != null && query.Contains("query=", StringComparison.OrdinalIgnoreCase))
            return false;   // поиск — одноразовые URL, забили бы LRU-кап

        return true;
    }

    static void Note(string scheme, string host, string pathQuery)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pathQuery)) return;

        string key = scheme + "|" + host + "|" + pathQuery;
        bool added = false;
        _urls.AddOrUpdate(key,
            _ => { added = true; return new Entry { scheme = scheme, host = host, pathQuery = pathQuery, lastSeen = DateTime.UtcNow }; },
            (_, en) => { en.lastSeen = DateTime.UtcNow; return en; });

        if (!added)
            return;

        int cap = Math.Max(8, ModInit.conf != null && ModInit.conf.catalogWarmupMaxUrls > 0 ? ModInit.conf.catalogWarmupMaxUrls : 64);
        while (_urls.Count > cap)
        {
            string oldest = null;
            DateTime oldestAt = DateTime.MaxValue;
            foreach (var kv in _urls)
                if (kv.Value.lastSeen < oldestAt) { oldestAt = kv.Value.lastSeen; oldest = kv.Key; }
            if (oldest == null || !_urls.TryRemove(oldest, out _))
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
            if (ModInit.conf?.catalogWarmupEnabled != true)
                return;

            int pruneDays = Math.Max(1, ModInit.conf.catalogWarmupPruneDays);
            foreach (var kv in _urls)
                if ((DateTime.UtcNow - kv.Value.lastSeen).TotalDays > pruneDays)
                    if (_urls.TryRemove(kv.Key, out _)) _dirty = true;

            int port = 9118;
            try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }

            int miss = 0, fail = 0;
            var snapshot = _urls.Values.ToArray();
            foreach (var en in snapshot)
            {
                try
                {
                    using var rq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{en.pathQuery}");
                    rq.Headers.TryAddWithoutValidation("Host", en.host);
                    if (string.Equals(en.scheme, "https", StringComparison.OrdinalIgnoreCase))
                        rq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

                    using var rs = await _http.SendAsync(rq);
                    _ = await rs.Content.ReadAsByteArrayAsync();

                    if (rs.Headers.TryGetValues("X-StatiCache-Status", out var st) && st.FirstOrDefault() == "MISS")
                        miss++;
                }
                catch { fail++; }

                await Task.Delay(200);   // ровный темп: не устраивать себе же burst из 64 запросов
            }

            if (miss > 0 || fail > 0)
                Console.WriteLine($"[QbitDownload] catalog warmup: {snapshot.Length} url, refreshed {miss}, fail {fail}");

            if (_dirty) { _dirty = false; Save(); }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
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
                if (!string.IsNullOrEmpty(en?.host) && !string.IsNullOrEmpty(en.pathQuery))
                    _urls.TryAdd((en.scheme ?? "http") + "|" + en.host + "|" + en.pathQuery, en);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup load: " + ex.Message); }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_urls.Values.ToList()));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] catalog warmup save: " + ex.Message); }
    }
    #endregion
}
