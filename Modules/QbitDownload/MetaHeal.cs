using Newtonsoft.Json.Linq;
using Shared;              // CoreInit
using Shared.Services;     // Http
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace QbitDownload;

// ───────────────────────────────────────────────────────────────────────────────
// Восстановление карточки загрузки по infohash.
//
// Карточку «Загрузок» пишет КЛИЕНТ сразу после «Скачать» (/qdl/save слепком с карточки Lampa), и это
// правильный путь: там богатые метаданные и правильный TMDB id. Но всё, что приехало мимо этого пути,
// оставалось безымянным — имя файла вместо названия, без описания и постера, клик открывал легаси
// qdl_card вместо полной карточки. Так случилось с «Холодом» (14.08.2026): Кинозал отдал .torrent
// файлом, /qdl/add не знал хеша и клиенту нечего было сохранять (корень починен в TorrentHash.cs).
//
// Здесь — страховка на все остальные случаи и ретро-фикс уже скачанного: infohash → TMDB id берём
// из ТОЧНОЙ привязки, накопленной поиском (torrent_title.tmdb_id нашего индекса), а если её нет —
// из bitmagnet (там привязка тоже по info_hash). Угадывание по имени сюда не заезжает вообще:
// «Holod…» ищется в TMDB как «Голод-33», и такая карточка хуже, чем никакой.
//
// Триггер — /qdl/list: карточка без меты чинится в фоне, горячий путь не ждёт.
// Базовый тип объявлен в Controller.cs — в partial-классе он указывается один раз.
// ───────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    static readonly ConcurrentDictionary<string, byte> _healInFlight = new();
    static readonly ConcurrentDictionary<string, DateTime> _healTried = new();

    // Сколько не возвращаться к хешу, по которому починить не вышло. Дёшево, но /qdl/list зовётся на
    // КАЖДОЕ открытие карточки: без этого каждая безымянная загрузка долбила бы Postgres и TMDB.
    static TimeSpan HealRetryAfter => TimeSpan.FromHours(6);

    static bool MetaHealEnabled => ModInit.conf?.metaHealEnabled ?? true;

    /// <summary>Фоновый запуск: вызывающий (сборка /qdl/list) ничего не ждёт.</summary>
    internal static void MetaHealKick(string hash)
    {
        if (!MetaHealEnabled || !ValidHash(hash)) return;
        if (_healTried.TryGetValue(hash, out var t) && DateTime.UtcNow - t < HealRetryAfter) return;
        if (LoadMeta(hash) != null) return;

        _ = Task.Run(async () =>
        {
            try { await MetaHealAsync(hash); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] meta heal(bg): " + ex.Message); }
        });
    }

    /// <summary>true — карточка восстановлена и записана.</summary>
    internal static async Task<bool> MetaHealAsync(string hash)
    {
        if (!MetaHealEnabled || !ValidHash(hash)) return false;
        if (LoadMeta(hash) != null) return false;
        if (_healTried.TryGetValue(hash, out var last) && DateTime.UtcNow - last < HealRetryAfter) return false;
        if (!_healInFlight.TryAdd(hash, 0)) return false;

        try
        {
            _healTried[hash] = DateTime.UtcNow;   // ставим ДО похода: неудачная попытка тоже не должна повторяться

            // 1) точная привязка: сначала наш индекс (он же даёт ссылку на раздачу для links), затем bitmagnet
            var li = await TmdbByBtih(hash);
            int tmdbId = li.tmdbId;
            bool tv = li.isSerial >= 2;

            if (tmdbId <= 0)
            {
                var bm = await BitmagnetTmdbByBtih(hash);
                tmdbId = bm.tmdbId;
                tv = bm.tv;
            }

            if (tmdbId <= 0) return false;

            // 2-3) карточка + постер по TMDB id
            var card = await WriteCardFromTmdb(hash, tmdbId, tv);
            if (card == null)
            {
                Console.WriteLine($"[QbitDownload] meta heal: {hash} → tmdb {tmdbId}, но детали не пришли");
                return false;
            }

            // 4) указатель на раздачу — фундамент слежения/охоты (сам по себе слежение НЕ включает:
            //    подписку ставит пользователь через /qdl/watch)
            HealLink(hash, !string.IsNullOrWhiteSpace(li.parselink) ? li.parselink : li.magnet,
                     li.queryNorm, li.year, li.isSerial, card);

            // негативная метка — только про НЕудачу: карточку могли удалить и скачать заново,
            // и тогда чинить её надо сразу, а не через HealRetryAfter
            _healTried.TryRemove(hash, out _);

            Console.WriteLine($"[QbitDownload] meta heal: {hash} → tmdb {tmdbId} «{card.Value<string>("title")}»");
            return true;
        }
        finally { _healInFlight.TryRemove(hash, out _); }
    }

    /// <summary>
    /// Записать карточку загрузки по TMDB id: мета + постер. Возвращает записанную карточку
    /// или null, если TMDB не отдал детали.
    ///
    /// Вынесено из MetaHealAsync, потому что тем же путём карточку заводит контур ожидания
    /// сезона (SeasonWatch.cs): раздачу там ставит сервер, клиента с его /qdl/save нет, а без
    /// меты новая раздача не склеится с уже лежащими сезонами (SeriesMergeId требует id + media_type).
    /// </summary>
    internal static async Task<JObject> WriteCardFromTmdb(string hash, int tmdbId, bool tv)
    {
        if (!ValidHash(hash) || tmdbId <= 0) return null;

        // детали карточки — через СВОЙ tmdb-прокси на loopback (как IndexCrawler/CatalogWarmup):
        // там кеш и, если настроен, апстрим-прокси; наружу напрямую не ходим
        var card = await TmdbCard(tmdbId, tv);
        if (card == null) return null;

        SaveMeta(hash, card);   // внутри — сброс горячего слоя и кеша /qdl/list

        // постер: путь берётся из только что записанной меты (как в /qdl/save)
        try
        {
            string tp = TmdbPosterPath(null, hash);
            if (tp != null && !HasPoster(hash) && _posterInFlight.TryAdd(hash, 0))
            {
                try
                {
                    // Host клиента здесь неоткуда взять (фон, не запрос) — картинка ляжет в свой
                    // бакет Staticache; на отдачу это не влияет, постер мы кешируем у себя в img/.
                    byte[] img = await FetchTmdbPoster(tp, null, false);
                    if (img != null)
                    {
                        string dir = Path.Combine(ModInit.conf.cachePath, "img");
                        Directory.CreateDirectory(dir);
                        System.IO.File.WriteAllBytes(PosterPath(hash), img);
                        // ⚠️ has_poster в /qdl/list считается по КЕШИРОВАННОМУ листингу img/, а снимок
                        // бессрочный: без сброса карточка ехала бы с битой картинкой до рестарта.
                        PosterWritten();
                    }
                }
                finally { _posterInFlight.TryRemove(hash, out _); }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] карточка из TMDB, постер: " + ex.Message); }

        return card;
    }

    /// <summary>Детали карточки TMDB нашим же прокси. null — не ответил/не карточка.</summary>
    static async Task<JObject> TmdbCard(int tmdbId, bool tv)
    {
        string apiKey = null;
        try { apiKey = CoreInit.conf.cub?.api_key; } catch { }

        string url = $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}"
                   + $"/tmdb/api/3/{(tv ? "tv" : "movie")}/{tmdbId}?language=ru"
                   + (string.IsNullOrEmpty(apiKey) ? "" : "&api_key=" + apiKey);

        string raw = await Http.Get(url, timeoutSeconds: 10);
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            var m = JObject.Parse(raw);
            return m.Value<int?>("id") > 0 ? BuildSlimCard(m, tv) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Тот же слепок, что клиентский slimCard (plugins/qdl.js): поля читают и грид, и полная карточка,
    /// поэтому набор и имена обязаны совпадать. source="index" — метка происхождения (у jut.su "jutsu").
    /// </summary>
    internal static JObject BuildSlimCard(JObject m, bool tv)
    {
        if (m == null) return null;

        string date = m.Value<string>("release_date") ?? m.Value<string>("first_air_date") ?? "";
        int runtime = m.Value<int?>("runtime")
                   ?? (m["episode_run_time"] as JArray)?.FirstOrDefault()?.Value<int>()
                   ?? 0;

        var countries = Names(m["production_countries"] as JArray);
        foreach (var oc in (m["origin_country"] as JArray) ?? new JArray())
        {
            string s = oc?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) countries.Add(s);
        }

        return new JObject
        {
            ["id"] = m.Value<int?>("id") ?? 0,
            ["media_type"] = tv ? "tv" : "movie",
            ["title"] = m.Value<string>("title") ?? m.Value<string>("name"),
            ["original_title"] = m.Value<string>("original_title") ?? m.Value<string>("original_name"),
            ["overview"] = m.Value<string>("overview"),
            ["tagline"] = m.Value<string>("tagline"),
            ["release_date"] = date,
            ["year"] = date.Length >= 4 ? date.Substring(0, 4) : "",
            ["vote_average"] = m.Value<double?>("vote_average") ?? 0,
            ["poster_path"] = m.Value<string>("poster_path"),
            ["backdrop_path"] = m.Value<string>("backdrop_path"),
            ["genres"] = new JArray(Names(m["genres"] as JArray)),
            ["runtime"] = runtime,
            ["countries"] = new JArray(countries),
            ["status"] = m.Value<string>("status"),
            ["number_of_seasons"] = m.Value<int?>("number_of_seasons"),
            ["number_of_episodes"] = m.Value<int?>("number_of_episodes"),
            ["age"] = "",
            ["source"] = "index"
        };
    }

    static System.Collections.Generic.List<string> Names(JArray arr)
    {
        var res = new System.Collections.Generic.List<string>();
        foreach (var x in arr ?? new JArray())
        {
            string s = (x as JObject)?.Value<string>("name") ?? x?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) res.Add(s);
        }
        return res;
    }

    /// <summary>links/&lt;hash&gt;.json из индексной записи — тот же формат, что пишет /qdl/add.</summary>
    static void HealLink(string hash, string link, string queryNorm, int year, int isSerial, JObject card)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(link)) return;                 // нечего пере-резолвить
            if (System.IO.File.Exists(LinkPath(hash))) return;           // свой указатель не трогаем

            string query = card?.Value<string>("title");
            if (string.IsNullOrWhiteSpace(query)) query = queryNorm;

            var lj = new JObject
            {
                ["link"] = link,
                ["query"] = query,
                ["ctx"] = new JObject
                {
                    ["title"] = query,
                    ["title_original"] = card?.Value<string>("original_title"),
                    ["year"] = year > 0 ? year : (int.TryParse(card?.Value<string>("year"), out int y) ? y : 0),
                    ["is_serial"] = isSerial > 0 ? isSerial : (card?.Value<string>("media_type") == "tv" ? 2 : 1),
                    ["season"] = 0
                }
            };

            Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "links"));
            System.IO.File.WriteAllText(LinkPath(hash), lj.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] meta heal link: " + ex.Message); }
    }
}
