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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Прогрев полок раздела «Музыка» (карта — E:\Media-server\claude\11) ───────────────────────
// Раздел живёт в АПСТРИМНОМ модуле Modules/Music, который при синке берётся папкой целиком
// (медиасервер claude/06 §CR), поэтому весь наш код — здесь, а внутри Music не меняется ни строки.
//
// Зачем. /music/home собирается из четырёх discovery-провайдеров, у каждого свой кеш на диске
// (Apple 6 ч, Spotify/SoundCloud/VK 1 ч). Протухло — за поход наружу платит первый живой клиент.
// Мы дёргаем home сами по таймеру: протухшее обновляет наш тик, а не человек. Принцип тот же,
// что у CatalogWarmup и OnlineWarm.
//
// 🔥 Греем ОДНУ ручку — /music/home. У всех четырёх провайдеров GetHomeSectionsAsync и
// GetSectionAsync ходят в ОДИН И ТОТ ЖЕ ключ MusicMetadataCacheService: ни limit, ни sectionId в
// ключ не входят (секции нарезаются из одного фида уже в памяти). Значит прогрев home попутно
// греет все /music/section, и отдельный обход секций — работа впустую.
//
// 🔴 Хост НЕ выдумываем. Обложки в ответе переписываются в /proxyimg/… с хостом ТЕКУЩЕГО запроса
// (Music/Services/Images/MusicImageProxyService.cs), а инстансы секций шарятся между запросами
// через кеш — отсюда был живой баг «полка с хостом 127.0.0.1 после curl-тестов». Поэтому греем
// только тем хостом, который реально наблюдали у живого клиента; не видели ни одного — тик
// пустой, и хелс честно говорит «греть некуда».
//
// 🔴 uid не передаём. Профильные ветки home только ЧИТАЮТ (recently_played, user_playlists,
// daily_mixes), но без uid прогрев физически не может попасть ни в чей профиль.
//
// Соседний контур: подмена user_uid на /music/* для групп устройств живёт в Groups.cs.
public static class MusicWarm
{
    sealed class HostEntry
    {
        public string scheme { get; set; }
        public string host { get; set; }
        public DateTime lastSeen { get; set; }
    }

    sealed class Shelf
    {
        public string id { get; set; }
        public int n { get; set; }
    }

    // ключ — scheme|host
    static readonly ConcurrentDictionary<string, HostEntry> _hosts = new();
    static readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    static Timer _timer;
    static int _ticking = 0;
    static bool _dirty;

    // последний результат — вход для строки хелс-чека (Health.MusicWarmVerdict)
    static DateTime? _lastRun, _lastOkAt;
    static int _lastMs;
    static bool _lastWarming;
    static int _fails;
    static List<Shelf> _shelves = new();
    static string _loggedShape;   // подпись прошлого лога: молчим, пока картина не изменилась

    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "music-warm.json");

    internal static int PeriodMin => Math.Max(5, ModInit.conf != null && ModInit.conf.musicWarmIntervalMin > 0 ? ModInit.conf.musicWarmIntervalMin : 20);

    public static void Attach()
    {
        Load();
        EventListener.Middleware += OnRequest;
        _timer?.Dispose();
        _timer = new Timer(async _ =>
        {
            try { await Tick(); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] music warm: " + ex.Message); }
        }, null, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(PeriodMin));
    }

    public static void Detach()
    {
        EventListener.Middleware -= OnRequest;
        _timer?.Dispose();
        _timer = null;
    }

    // Наблюдатель пайплайна: только учёт, всегда true. Держать ДЁШЕВО — стоит на каждом запросе.
    public static bool OnRequest(bool first, EventMiddleware e)
    {
        try
        {
            if (!first || ModInit.conf?.musicWarmEnabled != true) return true;

            var req = e.httpContext?.Request;
            if (req == null || !HttpMethods.IsGet(req.Method)) return true;

            if (!IsHomeUrl(req.Path.Value)) return true;

            // 🔴 Loopback в набор не берём. Обложки в ответе подписываются хостом запроса, и
            // прогрев с Host: 127.0.0.1 запекал бы этот адрес в ОБЩИЙ кеш полок — ровно тот живой
            // баг MusicImageProxyService. А ходит на loopback только своя же диагностика (curl из
            // контейнера, headless-проверки гейта): реальный клиент всегда приходит по LAN-адресу
            // или домену. Настоящего клиента мы этим не теряем.
            if (IsLoopbackHost(req.Host.Value)) return true;

            // 🔴 собственный реплей клиентом не считаем: иначе наш же прогрев вечно обновлял бы
            // lastSeen хоста и musicWarmPruneDays не сработал бы никогда
            if (req.Headers.ContainsKey(CatalogWarmup.WarmupHeader)) return true;

            NoteHost(req.Scheme, req.Host.Value);
        }
        catch { }
        return true;
    }

    // ── чистые функции — покрыты Tests/QbitDownload.Tests/MusicWarmTests.cs ──

    /// <summary>Главная раздела: /music и /music/home. Плагин (/music.js), секции и POST — нет.</summary>
    public static bool IsHomeUrl(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.Equals("/music", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/music/home", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Хост своей же диагностики: греть им нельзя (см. OnRequest).</summary>
    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;

        string h = host;
        int slash = h.IndexOf("//", StringComparison.Ordinal);
        if (slash >= 0) h = h.Substring(slash + 2);

        // отрезаем порт, но не путаем его с двоеточиями IPv6 ([::1]:9118)
        if (h.StartsWith("[", StringComparison.Ordinal))
        {
            int close = h.IndexOf(']');
            if (close > 0) h = h.Substring(1, close - 1);
        }
        else
        {
            int colon = h.IndexOf(':');
            if (colon > 0) h = h.Substring(0, colon);
        }

        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.Equals("::1", StringComparison.Ordinal)
            || h.StartsWith("127.", StringComparison.Ordinal);
    }

    internal static void NoteHost(string scheme, string host)
    {
        if (string.IsNullOrEmpty(host)) return;

        scheme = string.IsNullOrEmpty(scheme) ? "http" : scheme;
        string key = scheme + "|" + host;

        _hosts.AddOrUpdate(key,
            _ => new HostEntry { scheme = scheme, host = host, lastSeen = DateTime.UtcNow },
            (_, h) => { h.lastSeen = DateTime.UtcNow; return h; });

        _dirty = true;

        int cap = Math.Max(1, ModInit.conf != null && ModInit.conf.musicWarmHostCap > 0 ? ModInit.conf.musicWarmHostCap : 4);
        while (_hosts.Count > cap)
        {
            string victim = _hosts.OrderBy(kv => kv.Value.lastSeen).Select(kv => kv.Key).FirstOrDefault();
            if (victim == null || !_hosts.TryRemove(victim, out _))
                break;
        }
    }

    /// <summary>Хост для реплея — самый свежий по lastSeen; (null, null), если наблюдать было некого.</summary>
    internal static (string scheme, string host) PickHost()
    {
        var h = _hosts.Values.OrderByDescending(x => x.lastSeen).FirstOrDefault();
        return h == null ? (null, null) : (h.scheme, h.host);
    }

    /// <summary>Забыть хосты, не заходившие дольше pruneDays. Возвращает, сколько выкинули.</summary>
    internal static int PruneHosts(DateTime now, int pruneDays)
    {
        if (pruneDays <= 0) return 0;

        int gone = 0;
        foreach (var kv in _hosts.ToArray())
        {
            if ((now - kv.Value.lastSeen).TotalDays > pruneDays && _hosts.TryRemove(kv.Key, out _))
            {
                gone++;
                _dirty = true;
            }
        }
        return gone;
    }

    /// <summary>Полки и флаг догрева из тела /music/home. Битое тело → (null, false).</summary>
    static (List<Shelf> shelves, bool warming) ParseHome(byte[] body)
    {
        try
        {
            if (body == null || body.Length == 0) return (null, false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, false);

            bool warming = root.TryGetProperty("browse_sections_warming", out var w)
                && w.ValueKind == JsonValueKind.True;

            var list = new List<Shelf>();
            if (root.TryGetProperty("browse_sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in secs.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;

                    string id = s.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    // полка может быть альбомной, артистной или трековой — считаем всё, что пришло
                    int n = 0;
                    foreach (string field in new[] { "albums", "artists", "tracks" })
                        if (s.TryGetProperty(field, out var arr) && arr.ValueKind == JsonValueKind.Array)
                            n += arr.GetArrayLength();

                    list.Add(new Shelf { id = id, n = n });
                }
            }

            return (list, warming);
        }
        catch { return (null, false); }
    }

    // ── тик ──

    internal static async Task Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;

        try
        {
            var conf = ModInit.conf;
            // выключатель читаем В НАЧАЛЕ прогона, а не при заводе таймера: включение без рестарта
            if (conf?.musicWarmEnabled != true) return;

            PruneHosts(DateTime.UtcNow, conf.musicWarmPruneDays > 0 ? conf.musicWarmPruneDays : 14);

            var (scheme, host) = PickHost();
            _lastRun = DateTime.UtcNow;
            _dirty = true;

            if (host == null)
            {
                // «Музыку» ещё никто не открывал — греть некуда и НЕЧЕМ: выдуманный хост запёк бы
                // 127.0.0.1 в общий кеш обложек
                Save();
                return;
            }

            int port = 9118;
            try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ok, body) = await Fetch(port, scheme, host, "/music/home");
            sw.Stop();

            _lastMs = (int)sw.ElapsedMilliseconds;

            if (!ok)
            {
                _fails++;
                Console.WriteLine($"[QbitDownload] music warm: {host} — не ответил ({_lastMs} мс, подряд {_fails})");
                Save();
                return;
            }

            var (shelves, warming) = ParseHome(body);
            _fails = 0;
            _lastOkAt = DateTime.UtcNow;
            _lastWarming = warming;
            if (shelves != null) _shelves = shelves;

            LogIfChanged(host);
            Save();
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    // Логируем не каждый тик, а когда картина изменилась (правило CatalogWarmup: строка в логе
    // должна что-то значить). Подпись — хост, догрев и состав полок с количествами.
    static void LogIfChanged(string host)
    {
        var empty = _shelves.Where(s => s.n == 0).Select(s => s.id).ToList();
        string shape = host + "|" + _lastWarming + "|" + string.Join(",", _shelves.Select(s => s.id + ":" + s.n));
        if (string.Equals(shape, _loggedShape, StringComparison.Ordinal)) return;

        _loggedShape = shape;
        Console.WriteLine($"[QbitDownload] music warm: host {host}, home {_lastMs} мс, полок {_shelves.Count}"
            + (empty.Count > 0 ? $" (пусто: {string.Join(", ", empty)})" : string.Empty)
            + $", warming {(_lastWarming ? "true" : "false")}");
    }

    // Копия CatalogWarmup.Fetch МИНУС NoteHealth: тот раскладывает исходы по строкам tmdb-api/cub,
    // и наши вердикты уехали бы в чужой хелс. Свой вердикт считается из music-warm.json.
    static async Task<(bool ok, byte[] body)> Fetch(int port, string scheme, string host, string pathQuery)
    {
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{pathQuery}");
            rq.Headers.TryAddWithoutValidation("Host", host);
            rq.Headers.TryAddWithoutValidation(CatalogWarmup.WarmupHeader, "1");
            if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                rq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

            using var rs = await _http.SendAsync(rq);
            byte[] body = await rs.Content.ReadAsByteArrayAsync();
            return (rs.IsSuccessStatusCode, body);
        }
        catch { return (false, null); }
    }

    // ── состояние для хелс-чека ──

    /// <summary>Снимок для Health.cs: только JObject, внутренние типы наружу не светим.</summary>
    internal static JObject HealthSnapshot()
    {
        var o = new JObject
        {
            ["enabled"] = ModInit.conf?.musicWarmEnabled == true,
            ["periodMin"] = PeriodMin,
            ["hosts"] = _hosts.Count,
            ["ms"] = _lastMs,
            ["warming"] = _lastWarming,
            ["fails"] = _fails,
            ["shelves"] = _shelves.Count,
            ["empty"] = new JArray(_shelves.Where(s => s.n == 0).Select(s => s.id))
        };

        if (_lastRun != null) o["lastRun"] = _lastRun.Value;
        if (_lastOkAt != null) o["lastOkAt"] = _lastOkAt.Value;
        return o;
    }

    #region тестовый доступ
    // Статика течёт между кейсами (параллелизм в тест-проекте выключен, но не изоляция).

    internal static void ResetForTests()
    {
        _hosts.Clear();
        _shelves = new List<Shelf>();
        _lastRun = _lastOkAt = null;
        _lastMs = 0;
        _fails = 0;
        _lastWarming = false;
        _loggedShape = null;
        _dirty = false;
    }

    internal static bool DirtyForTests { get => _dirty; set => _dirty = value; }

    internal static List<string> HostsForTests()
        => _hosts.Values.OrderBy(h => h.host, StringComparer.Ordinal).Select(h => h.scheme + "|" + h.host).ToList();

    internal static List<(string id, int n)> ShelvesForTests()
        => _shelves.Select(s => (s.id, s.n)).ToList();

    internal static List<(string id, int n)> ParseHomeForTests(byte[] body, out bool warming)
    {
        var (sh, w) = ParseHome(body);
        warming = w;
        return sh?.Select(s => (s.id, s.n)).ToList();
    }

    /// <summary>Разложить снятые полки вручную — для кейсов хелса и персиста.</summary>
    internal static void SeedShelvesForTests(params (string id, int n)[] shelves)
        => _shelves = shelves.Select(s => new Shelf { id = s.id, n = s.n }).ToList();
    #endregion

    #region persist
    sealed class State
    {
        public int ver { get; set; }
        public List<HostEntry> hosts { get; set; }
        public List<Shelf> shelves { get; set; }
        public DateTime? lastRun { get; set; }
        public DateTime? lastOkAt { get; set; }
        public int lastMs { get; set; }
        public bool warming { get; set; }
        public int fails { get; set; }
    }

    internal static void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;

            string raw = File.ReadAllText(StorePath);
            if (string.IsNullOrWhiteSpace(raw)) return;

            var st = JsonSerializer.Deserialize<State>(raw);
            if (st == null) return;

            foreach (var h in st.hosts ?? new List<HostEntry>())
                if (!string.IsNullOrEmpty(h?.host))
                    _hosts.TryAdd((h.scheme ?? "http") + "|" + h.host, h);

            _shelves = st.shelves ?? new List<Shelf>();
            _lastRun = st.lastRun;
            _lastOkAt = st.lastOkAt;
            _lastMs = st.lastMs;
            _lastWarming = st.warming;
            _fails = st.fails;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] music warm load: " + ex.Message); }
    }

    internal static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(StorePath);
            Directory.CreateDirectory(dir);

            var st = new State
            {
                ver = 1,
                hosts = _hosts.Values.ToList(),
                shelves = _shelves,
                lastRun = _lastRun,
                lastOkAt = _lastOkAt,
                lastMs = _lastMs,
                warming = _lastWarming,
                fails = _fails
            };

            // ⚠️ .tmp → Move: хост падает по питанию ~23 раза в месяц, и обрезанный JSON обязан
            // читаться как «состояния нет», а не как пустой набор хостов
            string tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st));
            File.Move(tmp, StorePath, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] music warm save: " + ex.Message); }
    }
    #endregion
}
