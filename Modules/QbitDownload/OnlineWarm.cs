using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

/// <summary>
/// qdl 2.45: фоновый прогрев кнопок «Онлайн» — ПОСТЕПЕННЫЙ, тремя полосами.
///
/// Зачем. Набор рабочих балансеров для карточки (checkOnlineSearch в Online/OnlineApi.cs) собирается
/// 8.2 с при 23 балансерах — замер по поллингу /lifeevents: 45 мс → 7 из 23, 1.1 с → 18, 3.1 с → 21,
/// 8.2 с → все. Клиент видит это как «кнопки доезжают по одной». Мы держим набор тёплым заранее:
/// TTL поднят до суток (OnlineConf.checkOnlineSearchMinutes) и лежит на диске
/// (Online/OnlineEventsCache.cs), а эта джоба его продлевает.
///
/// 🔥 Главное требование владельца: НЕ греть всё разом. «Постепенно, но обязательно следить за
/// новинками, а что дальше — подтягивать совсем не спеша, чтобы точно не заспамить и не попасть
/// в лимиты». Отсюда три полосы с раздельными маленькими капами вместо одного большого прогона:
///
///   A. keep-warm  — то, что уже открывали, плюс скачанное и подписки слежения. Нового веера
///                   не создаёт вообще: продлевает ровно те наборы, что и так существуют.
///   B. новинки    — карточки, впервые появившиеся в каталоге и ещё ни разу не гретые.
///                   Попадают в прогрев в пределах одного цикла, то есть ≤ 6 часов.
///   C. хвост      — всё остальное, по персистентному курсору, по чуть-чуть за прогон.
///
/// Арифметика при дефолтах (20/10/5 за прогон, 4 прогона в сутки, 23 балансера):
///   35 карточек × 23 пробы × 4 = ~3220 исходящих проб в сутки ≈ 0.037 rps.
///   Для сравнения: прогреть все ~1128 карточек каталога залпом = 26 000 проб за цикл.
///   Полоса C проходит хвост каталога примерно за два месяца — и это ровно то, что просили.
///
/// ⚠️ Адаптивный тормоз: если за прогон доля пустых ответов выросла, капы следующего прогона
/// режутся вдвое (до минимума 2) и это логируется; восстанавливаются они по +1 за удачный прогон.
/// У части балансеров (Filmix/Kodik/Alloha) квоты по ключу — тормоз важнее скорости прогрева.
///
/// ⚠️ Ключ кеша (memkey в OnlineApi) считается по id + serial + source + online.Count, причём
/// source "tmdb" и "cub" сворачиваются в пустую строку. Поэтому греть достаточно ОДИН раз на
/// карточку: набор, прогретый нами с source=tmdb, обслужит и карточку с главной (source=cub).
/// </summary>
public static class OnlineWarm
{
    sealed class State
    {
        public int ver { get; set; }
        public string cursor { get; set; }              // полоса C: последний обработанный ключ
        public long lastRun { get; set; }               // unix UTC
        public int capA { get; set; }
        public int capB { get; set; }
        public int capC { get; set; }
        public Dictionary<string, long> warmed { get; set; }   // "movie|550" → unix, когда грели
    }

    static readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    static readonly object _lock = new();
    static State _st;
    static int _running = 0;

    const int WarmedCap = 4000;

    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "online-warm.json");

    #region чистые функции — покрыты тестами

    /// <summary>Ключ карточки в состоянии джобы.</summary>
    public static string CardKey(long id, bool tv) => (tv ? "tv|" : "movie|") + id;

    /// <summary>
    /// Пора ли обновлять: набор считается «скоро протухнет», когда прошло больше 2/3 TTL.
    /// Раньше — трогать незачем, позже — клиент успеет поймать холодный набор.
    /// </summary>
    public static bool NeedsRefresh(long warmedAtUnix, long nowUnix, int ttlMinutes)
    {
        if (warmedAtUnix <= 0) return true;
        long age = nowUnix - warmedAtUnix;
        return age >= ttlMinutes * 60L * 2 / 3;
    }

    /// <summary>
    /// Ответ /lite/events (не-life) — это JSON-массив кодов кнопок. Пустой массив значит, что не
    /// ответил НИ ОДИН балансер: считаем прогон карточки неудачным (сигнал для тормоза).
    /// </summary>
    public static bool LooksEmpty(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return true;
        string s = body.Trim();
        return s.Length <= 2 || s == "[]";
    }

    /// <summary>
    /// Новые капы после прогона. Много пустых ответов → режем вдвое (пол — 2), иначе потихоньку
    /// возвращаем к настроенным максимумам. Возврат по +1, чтобы после аварии не рвануть обратно.
    /// </summary>
    public static (int a, int b, int c) NextCaps((int a, int b, int c) cur, (int a, int b, int c) max, int done, int empty)
    {
        if (done <= 0)
            return cur;

        double failShare = (double)empty / done;

        if (failShare > 0.5)
            return (Math.Max(2, cur.a / 2), Math.Max(2, cur.b / 2), Math.Max(2, cur.c / 2));

        if (failShare < 0.2)
            return (Math.Min(max.a, cur.a + 1), Math.Min(max.b, cur.b + 1), Math.Min(max.c, cur.c + 1));

        return cur;
    }

    #endregion

    #region state

    static State St()
    {
        lock (_lock)
        {
            if (_st != null) return _st;

            var conf = ModInit.conf;
            _st = new State
            {
                ver = 1,
                capA = Math.Max(1, conf?.onlineWarmPerRunA ?? 20),
                capB = Math.Max(1, conf?.onlineWarmPerRunB ?? 10),
                capC = Math.Max(1, conf?.onlineWarmPerRunC ?? 5),
                warmed = new Dictionary<string, long>()
            };

            try
            {
                if (File.Exists(StorePath))
                {
                    string raw = File.ReadAllText(StorePath);
                    var loaded = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<State>(raw);
                    if (loaded != null && loaded.ver == 1)
                    {
                        loaded.warmed ??= new Dictionary<string, long>();
                        if (loaded.capA <= 0) loaded.capA = _st.capA;
                        if (loaded.capB <= 0) loaded.capB = _st.capB;
                        if (loaded.capC <= 0) loaded.capC = _st.capC;
                        _st = loaded;
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] online warm load: " + ex.Message); }

            return _st;
        }
    }

    static void Save()
    {
        try
        {
            var st = St();
            lock (_lock)
            {
                // ретенция: состояние не должно расти вечно
                if (st.warmed.Count > WarmedCap)
                    foreach (var k in st.warmed.OrderBy(kv => kv.Value).Take(st.warmed.Count - WarmedCap).Select(kv => kv.Key).ToList())
                        st.warmed.Remove(k);

                string dir = Path.GetDirectoryName(StorePath);
                Directory.CreateDirectory(dir);

                // .tmp → Move: обрезанный после падения по питанию JSON читается как «состояния нет»,
                // и джоба просто начинает круг заново — а не теряет курсор молча посередине.
                string tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(st));
                File.Move(tmp, StorePath, overwrite: true);
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] online warm save: " + ex.Message); }
    }

    #endregion

    /// <summary>Кандидат на прогрев: карточка + всё, что нужно балансерам для поиска.</summary>
    sealed record Cand(long id, bool tv, string title, string originalTitle, int year)
    {
        public string Key => CardKey(id, tv);
    }

    public static async Task Tick()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
            return;

        try
        {
            var conf = ModInit.conf;
            if (conf?.onlineWarmEnabled != true)
                return;

            var st = St();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int ttl = OnlineTtlMinutes();

            int port = 9118;
            try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }

            string host = LoopbackHost(port);

            // ── полоса A: keep-warm ──
            // Скачанное и подписки слежения — всегда в приоритете: это то, что владелец реально
            // смотрит. Плюс всё, что мы уже грели и чему пора обновиться.
            var laneA = new List<Cand>();
            foreach (var c in FromMeta())
                if (NeedsRefresh(st.warmed.TryGetValue(c.Key, out long t) ? t : 0, now, ttl))
                    laneA.Add(c);

            foreach (var kv in st.warmed.ToList())
            {
                if (!NeedsRefresh(kv.Value, now, ttl)) continue;
                if (laneA.Any(x => x.Key == kv.Key)) continue;
                var c = FromCatalog(kv.Key);
                if (c != null) laneA.Add(c);
            }

            // ── полосы B и C: каталог ──
            // B — карточки, которых мы ещё не грели, самые новые впереди (по firstSeen).
            // C — всё остальное, по стабильному курсору.
            var laneB = new List<Cand>();
            var tail = new List<Cand>();

            if (conf.onlineWarmCatalog)
            {
                foreach (var (id, tv) in CatalogWarmup.KnownCards())
                {
                    string key = CardKey(id, tv);
                    if (st.warmed.ContainsKey(key)) continue;   // уже грели — это полоса A
                    if (laneA.Any(x => x.Key == key)) continue;

                    var c = new Cand(id, tv, null, null, 0);
                    if (CatalogWarmup.CardFirstSeen(id) > 0) laneB.Add(c);
                    else tail.Add(c);
                }

                // новинки — самые свежие первыми
                laneB.Sort((x, y) => CatalogWarmup.CardFirstSeen(y.id).CompareTo(CatalogWarmup.CardFirstSeen(x.id)));

                // хвост — по стабильному ключу, продолжаем с сохранённого курсора
                tail.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            }

            var laneC = TailSlice(tail, st.cursor, st.capC);

            var plan = new List<(string lane, Cand c)>();
            foreach (var c in laneA.Take(st.capA)) plan.Add(("A", c));
            foreach (var c in laneB.Take(st.capB)) plan.Add(("B", c));
            foreach (var c in laneC) plan.Add(("C", c));

            if (plan.Count == 0)
                return;

            int pace = Math.Max(200, conf.onlineWarmPaceMs);
            int done = 0, empty = 0;
            int nA = 0, nB = 0, nC = 0;

            foreach (var (lane, cand) in plan)
            {
                var c = cand;
                if (string.IsNullOrEmpty(c.title))
                    c = await Enrich(port, host, c) ?? c;

                bool ok = await WarmOne(port, host, c);
                done++;
                if (!ok) empty++;
                else
                {
                    lock (_lock) st.warmed[c.Key] = now;
                    if (lane == "A") nA++; else if (lane == "B") nB++; else nC++;
                }

                if (lane == "C")
                    st.cursor = c.Key;

                await Task.Delay(pace);
            }

            var max = (Math.Max(1, conf.onlineWarmPerRunA), Math.Max(1, conf.onlineWarmPerRunB), Math.Max(1, conf.onlineWarmPerRunC));
            var caps = NextCaps((st.capA, st.capB, st.capC), max, done, empty);
            bool braked = caps.a < st.capA || caps.b < st.capB || caps.c < st.capC;
            (st.capA, st.capB, st.capC) = caps;
            st.lastRun = now;

            Save();

            Console.WriteLine($"[QbitDownload] online warm: A {nA}, B {nB}, C {nC} (всего {done}, пустых {empty}), капы {st.capA}/{st.capB}/{st.capC}{(braked ? " — ТОРМОЗ" : "")}");
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] online warm: " + ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    static int OnlineTtlMinutes()
    {
        try
        {
            int m = CoreInit.conf.online.checkOnlineSearchMinutes;
            return m > 0 ? m : 5;
        }
        catch { return 5; }
    }

    static string LoopbackHost(int port) => "127.0.0.1:" + port;

    /// <summary>Срез хвоста по стабильному курсору (тот же приём, что в CatalogWarmup).</summary>
    static List<Cand> TailSlice(List<Cand> tail, string cursor, int cap)
    {
        var res = new List<Cand>();
        if (tail.Count == 0 || cap <= 0) return res;

        int start = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            start = tail.FindIndex(x => string.CompareOrdinal(x.Key, cursor) > 0);
            if (start < 0) start = 0;   // прошли хвост — новый круг
        }

        for (int i = 0; i < cap && i < tail.Count; i++)
            res.Add(tail[(start + i) % tail.Count]);

        return res;
    }

    /// <summary>Скачанное («Загрузки») — самый приоритетный источник кандидатов.</summary>
    static List<Cand> FromMeta()
    {
        var list = new List<Cand>();
        try
        {
            string dir = Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "meta");
            if (!Directory.Exists(dir)) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var m = JsonStore.ReadObject(f);
                    if (m == null) continue;

                    long id = m.Value<long?>("id") ?? 0;
                    if (id <= 0) continue;

                    bool tv = string.Equals(m.Value<string>("media_type"), "tv", StringComparison.OrdinalIgnoreCase);
                    var c = new Cand(id, tv, m.Value<string>("title"), m.Value<string>("original_title"), m.Value<int?>("year") ?? 0);
                    if (seen.Add(c.Key)) list.Add(c);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    static Cand FromCatalog(string key)
    {
        int p = key.IndexOf('|');
        if (p <= 0 || !long.TryParse(key.Substring(p + 1), out long id)) return null;
        return new Cand(id, key.StartsWith("tv|", StringComparison.Ordinal), null, null, 0);
    }

    /// <summary>
    /// Достаёт название/год из НАШЕГО же TMDB-прокси. Прогрев каталога эти детали уже положил в
    /// Staticache, так что это почти всегда локальный HIT за пару миллисекунд, а не поход наружу.
    /// </summary>
    static async Task<Cand> Enrich(int port, string host, Cand c)
    {
        try
        {
            string apiKey = null;
            try { apiKey = CoreInit.conf.cub?.api_key; } catch { }
            if (string.IsNullOrEmpty(apiKey)) return null;

            string url = $"http://127.0.0.1:{port}{CatalogWarmup.DetailPath(c.id, c.tv)}?api_key={apiKey}&language=ru";
            using var rq = new HttpRequestMessage(HttpMethod.Get, url);
            rq.Headers.TryAddWithoutValidation("Host", host);
            rq.Headers.TryAddWithoutValidation(CatalogWarmup.WarmupHeader, "1");

            using var rs = await _http.SendAsync(rq);
            if (!rs.IsSuccessStatusCode) return null;

            var j = JObject.Parse(await rs.Content.ReadAsStringAsync());
            string title = j.Value<string>("title") ?? j.Value<string>("name");
            string orig = j.Value<string>("original_title") ?? j.Value<string>("original_name");
            string date = j.Value<string>("release_date") ?? j.Value<string>("first_air_date");

            int year = 0;
            if (!string.IsNullOrEmpty(date) && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out int y))
                year = y;

            return string.IsNullOrEmpty(title) ? null : c with { title = title, originalTitle = orig, year = year };
        }
        catch { return null; }
    }

    /// <summary>
    /// Один прогрев: НЕ-life запрос /lite/events, то есть с ожиданием полного набора. Именно он
    /// заполняет memoryCache и (через континуейшн в OnlineApi) кладёт снимок на диск.
    /// </summary>
    static async Task<bool> WarmOne(int port, string host, Cand c)
    {
        try
        {
            if (string.IsNullOrEmpty(c.title))
                return false;   // без названия балансерам искать нечего — не тратим пробу

            string q = $"id={c.id}&serial={(c.tv ? 1 : 0)}&source=tmdb"
                     + $"&title={Uri.EscapeDataString(c.title)}"
                     + (string.IsNullOrEmpty(c.originalTitle) ? "" : $"&original_title={Uri.EscapeDataString(c.originalTitle)}")
                     + (c.year > 0 ? $"&year={c.year}" : "")
                     + "&external_ids=true";

            using var rq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/lite/events?{q}");
            rq.Headers.TryAddWithoutValidation("Host", host);

            using var rs = await _http.SendAsync(rq);
            if (!rs.IsSuccessStatusCode) return false;

            return !LooksEmpty(await rs.Content.ReadAsStringAsync());
        }
        catch { return false; }
    }
}
