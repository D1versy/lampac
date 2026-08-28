using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// ───────────────────────────────────────────────────────────────────────────────
// «Жду следующий сезон» — подписка на СЕРИАЛ, а не на раздачу (qdl 2.79).
//
// Жалоба владельца по карточке 229564 («Телохранители»): оба сезона скачаны и завершены,
// и следить дальше не за чем — слежение /qdl/watch привязано к infohash КОНКРЕТНОЙ раздачи
// (watch.json: {hash, link, query, id, title, ctx}), сезон вычисляется из её же видеофайлов
// (EpisodeHunter.cs, DominantSeason) и жёстко гейтит всё остальное. Сезон, которого ещё нет
// в природе, выбрать нельзя в принципе: нет раздачи → нет infohash → нет links/<hash>.json →
// WatchAdd отвечает «no link». Здесь — контур, который ждёт САМ ФАКТ выхода сезона N+1.
//
// 🔴 ПОЧЕМУ ОТДЕЛЬНЫЙ КОНТУР, А НЕ ПОЛЕ В watch.json. Та запись per-infohash и нагружена
// доверху (доноры, blacklist, pendingSwitch, общий _watchGate, SaveWatchReconciled), а история
// у неё такая, что ошибка стоила сериала, снесённого С ФАЙЛАМИ (claude/06 §AK). Плюс склеенная
// карточка (SeriesMerge.cs) — это НЕСКОЛЬКО хешей, и подписке уровня сериала там просто негде
// жить. Ключ здесь — TMDB id, файл свой: /qdl-data/season-watch.json.
//
// 🔴 КРАСНАЯ ЛИНИЯ КОНТУРА: ОН ТОЛЬКО ДОБАВЛЯЕТ. Ни одной строки, удаляющей торрент или файл.
// Максимум его возможностей — поставить новую раздачу в категорию lampa и включить на ней
// штатное слежение, дальше сериал ведёт обычная охота. Это снимает целый класс рисков, которыми
// оплачены QbitDeleteDonorSafe и PromoteIfDonor: тут просто нечего удалять.
//
// 🔴 TMDB — FAIL-CLOSED. У AiredEpisodes (EpisodeHunter.cs) недоступный TMDB безопасен: потолок
// серий опускается, охота работает как раньше. Здесь недоступный TMDB означал бы «ищем и качаем
// сезон вслепую», поэтому тик молча выходит. Красная линия §AK №4: гейт, падающий открытым, —
// не защита.
//
// ⚠️ У «Телохранителей» TMDB отдаёт status=Ended и number_of_seasons=2 — третьего сезона там нет
// вовсе. Поэтому подписка НЕ гаснет от «Ended» и не требует, чтобы сезон уже был в мете: она
// стоит и ждёт, пока сезон появится, сколько бы это ни заняло.
//
// Единица подписки — (сериал, «жду сезон >= from»). Дождались → from = target + 1, ждём дальше.
// Тик раз в сутки (как у jut.su и XSMART), догон пропущенных тиков обязателен.
// ───────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region хранилище

    static string SeasonWatchFile => Path.Combine(ModInit.conf.cachePath, "season-watch.json");
    static readonly object _seasonLock = new();
    // Свой гейт, а НЕ общий _watchGate: тот не реентрантный, а нам нужно из тика звать
    // интерактивный путь включения слежения (он берёт _watchLock и пишет watch.json).
    static readonly SemaphoreSlim _seasonGate = new SemaphoreSlim(1, 1);

    static bool SeasonWatchOn => ModInit.conf?.seasonWatch ?? true;

    internal static JArray SeasonLoad()
    {
        try { if (System.IO.File.Exists(SeasonWatchFile)) return JArray.Parse(System.IO.File.ReadAllText(SeasonWatchFile)); }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: чтение " + ex.Message); }
        return new JArray();
    }

    static void SeasonSave(JArray a)
    {
        try
        {
            Directory.CreateDirectory(ModInit.conf.cachePath);
            System.IO.File.WriteAllText(SeasonWatchFile, a.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: запись " + ex.Message); }
    }

    static JObject SeasonFind(JArray a, int id)
        => id <= 0 ? null : a.OfType<JObject>().FirstOrDefault(x => (x.Value<int?>("id") ?? 0) == id);

    static HashSet<int> SeasonIds(JArray a)
        => new HashSet<int>(a.OfType<JObject>().Select(x => x.Value<int?>("id") ?? 0).Where(x => x > 0));

    /// <summary>
    /// Сохранение из фонового тика с реконсиляцией интерактивных правок — тот же приём и та же
    /// причина, что у SaveWatchReconciled (Controller.cs): пока тик ходил в TMDB и по трекерам
    /// (минуты), владелец мог включить или снять маркер, и слепая запись затёрла бы это.
    /// </summary>
    static void SeasonSaveReconciled(JArray working, HashSet<int> originalIds)
    {
        lock (_seasonLock)
        {
            var fresh = SeasonLoad();
            var workingIds = SeasonIds(working);
            var freshIds = SeasonIds(fresh);
            foreach (var f in fresh.OfType<JObject>())          // интерактивный ADD
            {
                int fid = f.Value<int?>("id") ?? 0;
                if (fid > 0 && !originalIds.Contains(fid) && !workingIds.Contains(fid)) { working.Add(f); workingIds.Add(fid); }
            }
            for (int i = working.Count - 1; i >= 0; i--)        // интерактивный REMOVE
            {
                int wid = working[i].Value<int?>("id") ?? 0;
                if (wid > 0 && originalIds.Contains(wid) && !freshIds.Contains(wid)) working.RemoveAt(i);
            }
            SeasonSave(working);
        }
    }

    /// <summary>Снять маркер при удалении последней карточки сериала. Зовётся из PurgeCache.</summary>
    internal static void SeasonWatchForgetIfOrphan(int seriesId)
    {
        if (seriesId <= 0) return;
        try
        {
            // мета удаляемой раздачи к этому моменту уже стёрта, поэтому индекс честно скажет,
            // осталась ли у сериала хоть одна карточка
            if (SeriesIndex().Values.Any(v => v == seriesId)) return;
            lock (_seasonLock)
            {
                var a = SeasonLoad();
                var rec = SeasonFind(a, seriesId);
                if (rec == null) return;
                rec.Remove();
                SeasonSave(a);
            }
            Console.WriteLine("[QbitDownload] season watch: сериал " + seriesId + " удалён из «Загрузок» — маркер снят");
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch purge: " + ex.Message); }
    }

    /// <summary>Карта «TMDB id → from» для декорации /qdl/list. Пустая карта = маркеров нет.</summary>
    internal static Dictionary<int, int> SeasonWaitMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            foreach (var rec in SeasonLoad().OfType<JObject>())
            {
                int id = rec.Value<int?>("id") ?? 0;
                if (id > 0) map[id] = Math.Max(2, rec.Value<int?>("from") ?? 2);
            }
        }
        catch { }
        return map;
    }

    #endregion

    #region TMDB: список сезонов сериала

    /// <summary>Сезон из корневого ответа TMDB: номер, дата старта, число серий.</summary>
    internal sealed class TmdbSeasonRow
    {
        public int number;
        public DateTime? air;
        public int episodes;
    }

    internal sealed class TmdbSeriesInfo
    {
        public string status;
        public int totalSeasons;
        public List<TmdbSeasonRow> seasons = new List<TmdbSeasonRow>();
    }

    // Кэш на процесс, 6 ч — как у _airedCache. Ключ — tmdbId.
    static readonly ConcurrentDictionary<int, (TmdbSeriesInfo info, DateTime at)> _seriesInfoCache = new();

    internal static void SeasonTmdbCacheDrop() => _seriesInfoCache.Clear();

    /// <summary>
    /// Корень сериала через СВОЙ tmdb-прокси на loopback (тот же приём, что AiredEpisodes и
    /// CatalogWarmup: ответ уже кешируется Staticache, ключа TMDB и внешнего доступа не нужно).
    /// null — TMDB не ответил; вызывающий обязан НИЧЕГО не делать (fail-closed).
    /// </summary>
    static async Task<TmdbSeriesInfo> TmdbSeriesSeasons(int tmdbId)
    {
        if (tmdbId <= 0) return null;
        if (_seriesInfoCache.TryGetValue(tmdbId, out var e) && (DateTime.UtcNow - e.at).TotalHours < 6) return e.info;

        int port = 9118;
        try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }

        try
        {
            using var rc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string body = await rc.GetStringAsync($"http://127.0.0.1:{port}/tmdb/api/3/tv/{tmdbId}");
            var root = JObject.Parse(body);
            if ((root.Value<int?>("id") ?? 0) <= 0) return null;

            var info = new TmdbSeriesInfo
            {
                status = root.Value<string>("status"),
                totalSeasons = root.Value<int?>("number_of_seasons") ?? 0
            };
            foreach (var s in (root["seasons"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                int n = s.Value<int?>("season_number") ?? -1;
                if (n < 0) continue;
                DateTime? air = null;
                string ad = s.Value<string>("air_date");
                if (!string.IsNullOrWhiteSpace(ad)
                    && DateTime.TryParse(ad, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) air = d.Date;
                info.seasons.Add(new TmdbSeasonRow { number = n, air = air, episodes = s.Value<int?>("episode_count") ?? 0 });
            }
            _seriesInfoCache[tmdbId] = (info, DateTime.UtcNow);
            return info;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] season watch: TMDB " + tmdbId + " недоступен (" + ex.Message + ") — тик пропущен");
            return null;
        }
    }

    #endregion

    #region чистая логика (без IO — тестируется через Access)

    /// <summary>
    /// Кандидаты в «вышедший сезон»: номер >= from, спецсезон 0 исключён, дата старта известна
    /// и уже наступила. По возрастанию.
    ///
    /// 🔴 Сезон БЕЗ даты трактуется как НЕ вышедший — обратно правилу AiredEpisodes, где пустой
    /// air_date у отдельной СЕРИИ считается «вышла». Там fail-open стоит одного лишнего кандидата,
    /// здесь — скачанного вслепую сезона, которого нет: TMDB заводит будущий сезон пустышкой без
    /// даты сразу по анонсу.
    /// </summary>
    internal static List<int> SeasonTargets(List<TmdbSeasonRow> seasons, int from, DateTime today)
    {
        var res = new List<int>();
        foreach (var s in seasons ?? new List<TmdbSeasonRow>())
        {
            if (s == null || s.number <= 0 || s.number < from) continue;
            if (s.air == null || s.air.Value.Date > today.Date) continue;
            res.Add(s.number);
        }
        res.Sort();
        return res;
    }

    /// <summary>С какого сезона начинать ждать: следующий за самым старшим из уже лежащих.</summary>
    internal static int SeasonWaitFrom(IEnumerable<int> onDisk, int numberOfSeasons)
    {
        int max = 0;
        foreach (int s in onDisk ?? Enumerable.Empty<int>()) if (s > max) max = s;
        if (max <= 0) max = Math.Max(0, numberOfSeasons);
        return Math.Max(2, max + 1);
    }

    /// <summary>Контекст отбора раздачи под новый сезон.</summary>
    internal sealed class SeasonPickCtx
    {
        public int target;
        public int minSeeds;
        public string titleNorm, originalNorm;
        public HashSet<string> knownHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // всё, что уже сидит в qBit
        public HashSet<string> selfTopics = new HashSet<string>(StringComparer.Ordinal);             // топики наших же раздач сериала
        public List<string> drops = new List<string>();                                              // причины отсева — в лог
    }

    /// <summary>
    /// Топ-1 раздача под сезон target. Выдача уже отсортирована по скору, берём первую прошедшую —
    /// требование владельца «не перебирать всё» (то же, что у охоты: OrderByCover(...).Take(1)).
    ///
    /// Гейты, каждый оплачен разбором:
    ///  • NameMatchesSeries — строгий гейт имени (коллизия «Лаки» ↔ «Счастливчик Люк / Лаки Люк»).
    ///    Для сезона, которого вчера не существовало, риск взять ЧУЖОЙ сериал максимален.
    ///  • сезоны раздачи строго == [target]. Мультисезонная («1-3 сезоны») перекачала бы уже
    ///    лежащие сезоны в ту же папку — а контур обещал только добавлять.
    ///  • топик не наш собственный (§AK шлюз 1): перевыложенный топик своей же раздачи — это
    ///    работа re-grab в CheckWatches, а не «новый сезон».
    ///  • infohash ещё не в qBit: он там либо как наша раздача (значит сезон уже есть), либо как
    ///    донор охоты — оба случая разбираются вызывающим, а не повторным add.
    /// </summary>
    internal static JObject PickSeasonCandidate(JArray scored, SeasonPickCtx h)
    {
        int badName = 0, badSeason = 0, badSeeds = 0, selfTopic = 0, known = 0, noLink = 0;
        foreach (var t in (scored ?? new JArray()).OfType<JObject>())
        {
            string title = t.Value<string>("title") ?? "";
            if (!NameMatchesSeries(title, h.titleNorm, h.originalNorm)) { badName++; continue; }

            var ss = TorrentScoring.ParseSeasons(title);
            if (ss.Count != 1 || ss[0] != h.target) { badSeason++; continue; }

            if ((t.Value<int?>("sid") ?? 0) < h.minSeeds) { badSeeds++; continue; }

            string parselink = t.Value<string>("parselink");
            string btih = MagnetHash(t.Value<string>("magnet"));
            if (string.IsNullOrEmpty(btih) && string.IsNullOrWhiteSpace(parselink)) { noLink++; continue; }

            string tk = TopicKey(parselink);
            if (tk != null && h.selfTopics.Contains(tk)) { selfTopic++; continue; }

            if (!string.IsNullOrEmpty(btih) && h.knownHashes.Contains(btih)) { known++; continue; }

            return t;
        }

        if (badName > 0) h.drops.Add("чужое имя " + badName);
        if (badSeason > 0) h.drops.Add("не сезон " + h.target + ": " + badSeason);
        if (badSeeds > 0) h.drops.Add("мало сидов " + badSeeds);
        if (selfTopic > 0) h.drops.Add("свой топик " + selfTopic);
        if (known > 0) h.drops.Add("уже в qBit " + known);
        if (noLink > 0) h.drops.Add("нечего качать " + noLink);
        return null;
    }

    #endregion

    #region сезоны сериала на диске

    /// <summary>Все хеши карточек сериала (включая одиночную). Пусто — сериала в «Загрузках» нет.</summary>
    static List<string> SeriesHashesById(int tmdbId)
    {
        if (tmdbId <= 0) return new List<string>();
        try { return SeriesIndex().Where(kv => kv.Value == tmdbId).Select(kv => kv.Key).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Какие сезоны сериала уже лежат. Номер берём из НАЗВАНИЯ раздачи (тот же CardSeason, что у
    /// склейки карточек), фолбэк — ctx.season из links/&lt;hash&gt;.json. qBit недоступен → работаем
    /// на одних links: неполный ответ здесь безопасен (маркер просто начнёт с number_of_seasons+1).
    /// </summary>
    static async Task<HashSet<int>> SeasonsOnDisk(HttpClient c, int tmdbId)
    {
        var res = new HashSet<int>();
        var hashes = SeriesHashesById(tmdbId);
        if (hashes.Count == 0) return res;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (c != null)
        {
            try
            {
                var arr = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}"));
                foreach (var t in arr.OfType<JObject>())
                {
                    string hh = t.Value<string>("hash");
                    if (!string.IsNullOrEmpty(hh)) names[hh] = t.Value<string>("name") ?? "";
                }
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: qBit недоступен (" + ex.Message + ") — сезоны по links"); }
        }

        foreach (string h in hashes)
        {
            int s = names.TryGetValue(h, out var nm) ? CardSeason(nm) : 0;
            if (s <= 0)
            {
                try
                {
                    if (System.IO.File.Exists(LinkPath(h)))
                        s = (JObject.Parse(System.IO.File.ReadAllText(LinkPath(h)))["ctx"] as JObject)?.Value<int?>("season") ?? 0;
                }
                catch { }
            }
            if (s > 0) res.Add(s);
        }
        return res;
    }

    /// <summary>Топики наших раздач этого сериала — их перевыкладка не «новый сезон» (§AK шлюз 1).</summary>
    static HashSet<string> SeriesSelfTopics(int tmdbId)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (string h in SeriesHashesById(tmdbId))
        {
            try
            {
                if (!System.IO.File.Exists(LinkPath(h))) continue;
                string tk = TopicKey(JObject.Parse(System.IO.File.ReadAllText(LinkPath(h))).Value<string>("link"));
                if (tk != null) res.Add(tk);
            }
            catch { }
        }
        // плюс топики watch-записей: раздачу могли re-grab-нуть, а links пере-записать
        try
        {
            JArray wl; lock (_watchLock) wl = LoadWatch();
            foreach (var m in wl.OfType<JObject>())
            {
                if ((m.Value<int?>("id") ?? 0) != tmdbId) continue;
                string tk = TopicKey(m.Value<string>("link"));
                if (tk != null) res.Add(tk);
            }
        }
        catch { }
        return res;
    }

    #endregion

    #region тик

    static string SeasonNotifyHash(int tmdbId, string preferred)
    {
        if (ValidHash(preferred)) return preferred;
        return SeriesHashesById(tmdbId).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
    }

    /// <summary>Уведомление контура. Дедуп — по (seriesKey, epkey), как у JutNotifySeason.</summary>
    static void SeasonNotify(int tmdbId, string title, int season, string hash, string kind, string epkey, string label)
    {
        try
        {
            string sk = SeriesKey(tmdbId, null);
            using var db = new SqlContext();
            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == epkey)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = tmdbId, hash = hash ?? "", title = title ?? "",
                season = season, episode = -1, kind = kind, epkey = epkey,
                label = label, created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
            Console.WriteLine("[QbitDownload] season watch: уведомление «" + (title ?? "") + "» — " + label);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch noti: " + ex.Message); }
    }

    /// <summary>
    /// Поставить раздачу нового сезона и включить на ней штатное слежение.
    /// Возвращает infohash или null.
    ///
    /// 🔴 Дубликат от qBit разбирается ОБЯЗАТЕЛЬНО (§AK красная линия №1): на дубликате qBit не
    /// применяет переданную категорию, и торрент, который уже качала охота донором, остался бы
    /// «донором», став основной — а контур замещения снимает донора С ФАЙЛАМИ. Точек add в модуле
    /// было три (/qdl/add, CheckWatches, ExecuteSwitch); эта — четвёртая, и правило то же.
    /// </summary>
    static async Task<string> SeasonGrab(HttpClient c, JObject rec, int target, JObject cand)
    {
        int tmdbId = rec.Value<int?>("id") ?? 0;
        string parselink = cand.Value<string>("parselink");
        string magnet = cand.Value<string>("magnet");
        if (string.IsNullOrWhiteSpace(magnet)) magnet = await ResolveMagnetStatic(parselink);
        string hash = MagnetHash(magnet);
        if (string.IsNullOrWhiteSpace(hash))
        {
            Console.WriteLine("[QbitDownload] season watch: резолв не дал магнет — " + cand.Value<string>("title"));
            return null;
        }

        var add = await QbitAddMagnetStatus(c, magnet, ModInit.conf.category);
        if (add == QbitAddStatus.Failed) { Console.WriteLine("[QbitDownload] season watch: qBit не принял " + hash); return null; }

        if (add == QbitAddStatus.Duplicate)
            Console.WriteLine("[QbitDownload] season watch: " + hash + " уже в qBit (дубликат) — довожу категорию");
        try
        {
            JArray wl; lock (_watchLock) wl = LoadWatch();
            if (await PromoteIfDonor(c, hash, wl.OfType<JObject>(), rec.Value<string>("title")))
                lock (_watchLock) { SaveWatch(wl); }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: промоушен донора " + ex.Message); }

        // указатель на раздачу — фундамент слежения и охоты (формат тот же, что пишет /qdl/add)
        try
        {
            var ctx = rec["ctx"] as JObject;
            Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "links"));
            var lj = new JObject
            {
                ["link"] = !string.IsNullOrWhiteSpace(parselink) ? parselink : magnet,
                ["query"] = ctx?.Value<string>("title") ?? rec.Value<string>("title"),
                ["ctx"] = new JObject
                {
                    ["title"] = ctx?.Value<string>("title") ?? rec.Value<string>("title"),
                    ["title_original"] = ctx?.Value<string>("title_original"),
                    ["year"] = ctx?.Value<int?>("year") ?? 0,
                    ["is_serial"] = 2,
                    ["season"] = target
                }
            };
            System.IO.File.WriteAllText(LinkPath(hash), lj.ToString(Newtonsoft.Json.Formatting.None));
            JsonStore.Forget(LinkPath(hash));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: links " + ex.Message); }

        // 🔴 Мета обязательна: без неё карточка не склеится с уже лежащими сезонами —
        // SeriesMergeId требует и id, и media_type=tv. Карточку из TMDB пишет тот же код,
        // что чинит безымянные загрузки (MetaHeal).
        try { await WriteCardFromTmdb(hash, tmdbId, true); }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: мета " + ex.Message); }

        ActivityTouch(hash);
        SeriesIndexDrop();
        DropListCache();

        // штатное слежение на новой раздаче: дальше серии добирает обычная охота, нового кода нет.
        // ⚠️ Baseline у только что добавленного торрента пуст (метаданные ещё не пришли) — и это
        // правильно: первый ScanEpisodeNotifications сделает baseline молча, и зритель получит одно
        // уведомление «вышел сезон», а не залп из шестнадцати серий (грабля §BC.5 у jut.su).
        try { await SeasonEnableWatch(hash); }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: слежение " + ex.Message); }

        Console.WriteLine("[QbitDownload] season watch: «" + rec.Value<string>("title") + "» сезон " + target
            + " → " + hash + " («" + cand.Value<string>("title") + "», " + (cand.Value<int?>("sid") ?? 0) + " сид)");
        return hash;
    }

    /// <summary>Тот же путь, что WatchAdd, но изнутри процесса (без HTTP и без ответа клиенту).</summary>
    static async Task SeasonEnableWatch(string hash)
    {
        if (!ValidHash(hash) || !System.IO.File.Exists(LinkPath(hash))) return;
        var lj = JObject.Parse(System.IO.File.ReadAllText(LinkPath(hash)));
        string link = lj.Value<string>("link");
        if (string.IsNullOrWhiteSpace(link)) return;

        var meta = System.IO.File.Exists(MetaPath(hash))
            ? JObject.Parse(System.IO.File.ReadAllText(MetaPath(hash))) : new JObject();
        bool added = false;
        lock (_watchLock)
        {
            var a = LoadWatch();
            if (!a.OfType<JObject>().Any(m => hash.Equals(m.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)))
            {
                var w = new JObject
                {
                    ["hash"] = hash, ["link"] = link, ["query"] = lj.Value<string>("query"),
                    ["id"] = meta.Value<int?>("id"), ["title"] = meta.Value<string>("title")
                };
                if (lj["ctx"] is JObject ctx) w["ctx"] = ctx;
                a.Add(w);
                SaveWatch(a);
                added = true;
            }
        }
        if (added) await SeedBaseline(SeriesKey(meta.Value<int?>("id") ?? 0, link), hash);
    }

    /// <summary>
    /// Суточный тик. dry=true — полный проход с TMDB, поиском и гейтами, но БЕЗ единой записи:
    /// единственный способ проверить контур боем, ничего не сломав (история §AK).
    /// Возвращает отчёт по каждой записи.
    /// </summary>
    public static async Task<JArray> SeasonWatchTick(bool dry = false, int onlyId = 0)
    {
        var report = new JArray();
        if (!SeasonWatchOn) { report.Add(new JObject { ["decision"] = "disabled" }); return report; }
        if (ReplicaMode) { report.Add(new JObject { ["decision"] = "replica" }); return report; }

        if (!await _seasonGate.WaitAsync(0))
        {
            Console.WriteLine("[QbitDownload] season watch: тик пропущен (gate занят)");
            report.Add(new JObject { ["decision"] = "busy" });
            return report;
        }

        HttpClient c = null;
        try
        {
            JArray list; HashSet<int> orig;
            lock (_seasonLock) { list = SeasonLoad(); orig = SeasonIds(list); }
            if (list.Count == 0) return report;

            try { c = await Qbit(); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch: qBittorrent недоступен (" + ex.Message + ")"); }

            bool changed = false;
            var now = DateTime.UtcNow;

            foreach (var rec in list.OfType<JObject>().ToList())
            {
                int id = rec.Value<int?>("id") ?? 0;
                if (id <= 0) continue;
                if (onlyId > 0 && id != onlyId) continue;

                var line = new JObject { ["id"] = id, ["title"] = rec.Value<string>("title") };
                report.Add(line);

                try
                {
                    long nextAt = rec.Value<long?>("nextAt") ?? 0;
                    if (!dry && nextAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < nextAt)
                    { line["decision"] = "backoff"; continue; }

                    var info = await TmdbSeriesSeasons(id);
                    if (info == null) { line["decision"] = "tmdb-down"; continue; }   // 🔴 fail-closed
                    line["status"] = info.status;
                    line["tmdbSeasons"] = info.totalSeasons;

                    if (!dry)
                    {
                        rec["seen"] = new JObject { ["seasons"] = info.totalSeasons, ["status"] = info.status };
                        rec["lastRun"] = now.ToString("o");
                        changed = true;
                    }

                    int from = Math.Max(2, rec.Value<int?>("from") ?? 2);
                    var targets = SeasonTargets(info.seasons, from, DateTime.UtcNow);
                    line["from"] = from;

                    // сезоны, которые уже лежат: владелец мог скачать их руками, пока маркер ждал
                    var have = await SeasonsOnDisk(c, id);
                    int skipped = 0;
                    while (targets.Count > 0 && have.Contains(targets[0]))
                    {
                        from = targets[0] + 1;
                        if (!dry) { rec["from"] = from; changed = true; }
                        targets.RemoveAt(0);
                        skipped++;
                    }
                    if (skipped > 0) line["alreadyHave"] = skipped;

                    if (targets.Count == 0) { line["decision"] = "waiting"; continue; }

                    int target = targets[0];
                    line["target"] = target;

                    // подтверждение эфира: у сезона есть дата, но реально вышедшая серия обязана быть
                    int aired = await AiredEpisodes(id, target);
                    line["aired"] = aired;
                    if (aired < 1) { line["decision"] = "waiting"; continue; }

                    string title = rec.Value<string>("title");
                    string nhash = SeasonNotifyHash(id, null);

                    if (!dry)
                        SeasonNotify(id, title, target, nhash, "SEASON", "season-" + target,
                                     "вышел " + target + " сезон — ищу раздачу");

                    string mode = (rec.Value<string>("mode") ?? "grab").ToLowerInvariant();
                    if (mode != "grab" || !(ModInit.conf?.seasonWatchAutoGrab ?? true))
                    {
                        line["decision"] = "notify-only";
                        if (!dry) { rec["from"] = target + 1; changed = true; }
                        continue;
                    }

                    if (c == null) { line["decision"] = "qbit-down"; continue; }

                    var ctx = rec["ctx"] as JObject;
                    string ctitle = ctx?.Value<string>("title");
                    if (string.IsNullOrWhiteSpace(ctitle)) ctitle = title;
                    if (string.IsNullOrWhiteSpace(ctitle)) { line["decision"] = "no-title"; continue; }

                    var scored = await SearchScored(ctitle, ctitle, ctx?.Value<string>("title_original"),
                                                    ctx?.Value<int?>("year") ?? 0, 2, target, null, id.ToString());
                    line["candidates"] = scored?.Count ?? 0;

                    int minSeeds = ModInit.conf?.seasonWatchMinSeeds ?? 0;
                    if (minSeeds <= 0) minSeeds = Math.Max(1, ModInit.conf?.recommendMinSeeds ?? 3);
                    var h = new SeasonPickCtx
                    {
                        target = target,
                        minSeeds = minSeeds,
                        titleNorm = Shared.Services.Utilities.SearchNameTo.Convert(ctitle),
                        originalNorm = Shared.Services.Utilities.SearchNameTo.Convert(ctx?.Value<string>("title_original")),
                        selfTopics = SeriesSelfTopics(id)
                    };
                    try
                    {
                        var all = JArray.Parse(await c.GetStringAsync("/api/v2/torrents/info"));
                        foreach (var t in all.OfType<JObject>())
                        { string hh = t.Value<string>("hash"); if (!string.IsNullOrEmpty(hh)) h.knownHashes.Add(hh); }
                    }
                    catch (Exception ex)
                    {
                        // 🔴 Не знаем, что уже сидит в qBit → не добавляем ничего. Иначе повторный add
                        // на донора охоты пришёл бы дубликатом мимо проверок (§AK красная линия №4).
                        Console.WriteLine("[QbitDownload] season watch: список торрентов недоступен (" + ex.Message + ") — пропуск");
                        line["decision"] = "qbit-down";
                        continue;
                    }

                    var cand = PickSeasonCandidate(scored, h);
                    if (h.drops.Count > 0) line["drops"] = string.Join(", ", h.drops);

                    if (cand == null)
                    {
                        line["decision"] = "no-candidate";
                        if (!dry)
                        {
                            int tries = (rec.Value<int?>("tries") ?? 0) + 1;
                            rec["tries"] = tries;
                            rec["err"] = "раздача сезона " + target + " не найдена";
                            rec["nextAt"] = DateTimeOffset.UtcNow.AddHours(20).ToUnixTimeSeconds();
                            changed = true;
                            int max = Math.Max(1, ModInit.conf?.seasonWatchMaxTries ?? 8);
                            if (tries >= max)
                            {
                                SeasonNotify(id, title, target, nhash, "INFO", "season-nofind-" + target,
                                    target + " сезон вышел, подходящей раздачи нет — можно скачать вручную");
                                rec["tries"] = 0;   // считаем заново, ждать НЕ перестаём
                            }
                        }
                        continue;
                    }

                    line["candidate"] = new JObject
                    {
                        ["title"] = cand.Value<string>("title"),
                        ["tracker"] = cand.Value<string>("tracker"),
                        ["sid"] = cand.Value<int?>("sid") ?? 0,
                        ["score"] = cand.Value<double?>("score") ?? 0,
                        ["quality"] = cand.Value<int?>("quality") ?? 0
                    };

                    if (dry) { line["decision"] = "would-grab"; continue; }

                    string got = await SeasonGrab(c, rec, target, cand);
                    if (string.IsNullOrEmpty(got))
                    {
                        line["decision"] = "grab-failed";
                        rec["tries"] = (rec.Value<int?>("tries") ?? 0) + 1;
                        rec["nextAt"] = DateTimeOffset.UtcNow.AddHours(20).ToUnixTimeSeconds();
                        changed = true;
                        continue;
                    }

                    line["decision"] = "grabbed";
                    line["hash"] = got;
                    rec["from"] = target + 1;
                    rec["tries"] = 0;
                    rec["err"] = null;
                    rec["nextAt"] = 0;
                    changed = true;

                    SeasonNotify(id, title, target, got, "SEASON", "season-grab-" + target,
                                 target + " сезон найден и качается");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[QbitDownload] season watch item " + id + ": " + ex);
                    line["decision"] = "error";
                    line["error"] = ex.Message;
                }
            }

            if (changed && !dry) SeasonSaveReconciled(list, orig);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] season watch tick: " + ex); }
        finally { c?.Dispose(); _seasonGate.Release(); }

        return report;
    }

    /// <summary>
    /// Догон пропущенных тиков — как у jut.su. При суточном такте обязателен: рестарт контейнера
    /// (пересборка форка, правка init.conf, падения хоста по питанию) иначе сдвигает проверку на
    /// новые сутки, и при частых рестартах она не срабатывает вообще.
    /// </summary>
    internal static bool SeasonWatchOverdue(TimeSpan period, out TimeSpan since)
    {
        since = TimeSpan.Zero;
        try
        {
            DateTime? last = null;
            foreach (var rec in SeasonLoad().OfType<JObject>())
            {
                var v = rec.Value<DateTime?>("lastRun");
                if (v != null && (last == null || v > last)) last = v;
            }
            if (last == null) return false;
            since = DateTime.UtcNow - last.Value;
            return since > period * 1.5;
        }
        catch { return false; }
    }

    #endregion

    #region ручки /qdl/season/watch

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/season/watch")]
    async public Task<ActionResult> SeasonWatchAdd(string hash, int from = 0)
    {
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;   // маркер живёт только дома
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            var meta = LoadMeta(hash);
            int id = meta?.Value<int?>("id") ?? 0;
            if (id <= 0 || (meta.Value<string>("media_type") ?? "") != "tv")
                return Json(new { success = false, error = "no tmdb tv" });

            HttpClient c = null;
            try { c = await Qbit(); } catch { }
            HashSet<int> have;
            try { have = await SeasonsOnDisk(c, id); } finally { c?.Dispose(); }

            int start = from > 0 ? Math.Max(2, from) : SeasonWaitFrom(have, meta.Value<int?>("number_of_seasons") ?? 0);

            lock (_seasonLock)
            {
                var a = SeasonLoad();
                var rec = SeasonFind(a, id);
                if (rec == null)
                {
                    rec = new JObject
                    {
                        ["id"] = id,
                        ["title"] = meta.Value<string>("title"),
                        ["ctx"] = new JObject
                        {
                            ["title"] = meta.Value<string>("title"),
                            ["title_original"] = meta.Value<string>("original_title"),
                            ["year"] = int.TryParse(meta.Value<string>("year"), out int y) ? y : 0
                        },
                        ["mode"] = "grab",
                        ["created"] = DateTime.UtcNow.ToString("o")
                    };
                    a.Add(rec);
                }
                rec["from"] = start;
                rec["tries"] = 0;
                rec["nextAt"] = 0;
                rec["err"] = null;
                SeasonSave(a);
            }
            DropListCache();
            Console.WriteLine("[QbitDownload] season watch: «" + meta.Value<string>("title") + "» — жду сезон " + start);
            return Json(new { success = true, id, from = start });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] season watch add: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/season/watch/remove")]
    public ActionResult SeasonWatchRemove(string hash, int id = 0)
    {
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;
        try
        {
            int sid = id > 0 ? id : (ValidHash(hash) ? (LoadMeta(hash)?.Value<int?>("id") ?? 0) : 0);
            if (sid <= 0) return Json(new { success = false, error = "no tmdb id" });
            lock (_seasonLock)
            {
                var a = SeasonLoad();
                var rec = SeasonFind(a, sid);
                if (rec != null) { rec.Remove(); SeasonSave(a); }
            }
            DropListCache();
            return Json(new { success = true, id = sid });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] season watch remove: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/season/watch/list")]
    public ActionResult SeasonWatchList()
        => ContentTo(SeasonLoad().ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");

    [HttpGet, AllowAnonymous]
    [Route("qdl/season/watch/check")]
    async public Task<ActionResult> SeasonWatchCheck(int dry = 0, int id = 0)
    {
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;
        var rep = await SeasonWatchTick(dry == 1, id);
        // ⚠️ Именно ContentTo, а НЕ Json(new { items = rep }): ответ уходит системным
        // System.Text.Json, который про JToken не знает и превращает отчёт в матрёшку пустых
        // массивов. Тот же приём, что у /qdl/season/watch/list и /qdl/watch/list.
        var body = new JObject { ["success"] = true, ["dry"] = dry == 1, ["items"] = rep };
        return ContentTo(body.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    #endregion
}
