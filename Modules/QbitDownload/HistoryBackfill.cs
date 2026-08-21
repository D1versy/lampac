using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// РАЗОВЫЙ БЭКФИЛЛ «Истории просмотров» (qdl 2.61).
//
// Зачем: до 2.61 в favorite.history писали только торрент-плеер Lampa и плагин «Онлайн» — то есть
// наши сценарии («Загрузки», jut.su) не писали НИЧЕГО, и раздел был пуст у всех устройств (замер
// на боевом: таблица bookmarks — 0 строк при 47 живых таймкодах). Клиентский фикс наполняет историю
// только тем, что посмотрят ПОСЛЕ выкатки; накопленное восстанавливаем отсюда.
//
// Источник — то, что уже лежит на диске, без единого нового хранилища:
//   database/TimeCode.sql     timecodes(user, card, item, data, updated) — что смотрели и когда;
//   ведро card                <tmdbId>_movie|tv · qdl_t<tmdbId> · qdl_jut:<slug> · qdl_<infohash>;
//   <cachePath>/meta/*.json   готовый slim-card загрузки (обычно резолв вообще без сети);
//   <jutDataDir>/history/*    per-device история jut.su — она полнее таймкодов.
//
// 🔴 ТРИ ИНВАРИАНТА:
//  1. ИДЕМПОТЕНТНОСТЬ. Повтор не плодит дублей и не переставляет порядок: слияние идёт по id.
//  2. СЛИЯНИЕ, А НЕ ЗАМЕНА. Трогаем только history и card; like/wath/book/… не касаемся вовсе —
//     иначе разовая операция стала бы способом потерять закладки.
//  3. ТОЛЬКО ДОМ. На роли replica ручка отдаёт 403 (гейт в Admin.cs). Причина не косметическая:
//     мы поставили бы строке updated=now, а ReplicaHistory.ApplyBookmarks уважает HistoryNewer —
//     домашняя строка навсегда стала бы «старее» и больше никогда бы не применилась.
//
// Пишем прямым SQLite той же схемой и БЕЗ EnsureCreated: модули собираются Roslyn'ом в отдельные
// сборки и не видят друг друга (тот же приём и та же причина, что в ReplicaHistory.cs).
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    // Тот же кап, что у клиента: Lampa.Favorite.add('history', card, 100).
    internal const int HistoryBackfillCap = 100;

    static readonly Regex _histTmdbBucket = new(@"^(\d+)_(movie|tv)$", RegexOptions.Compiled);
    static readonly Regex _histSeriesKey = new(@"^t(\d+)$", RegexOptions.Compiled);
    static readonly Regex _histInfohash = new(@"^[0-9a-fA-F]{40}$", RegexOptions.Compiled);

    #region разбор ведра

    /// <summary>
    /// Ведро серверных таймкодов в «что это за тайтл».
    /// kind: "tv"/"movie" — TMDB id с известным типом; "tmdb" — id без типа (ведро qdl_t + id,
    /// SeriesKey отдаёт "t{tmdbId}" и для сериала, и для фильма); "jut" — слаг jut.su;
    /// "hash" — infohash раздачи; (null, null) — ведро бесполезно.
    /// ⚠️ "0_movie" — вырожденное ведро: карточки в activity не было, id нулевой, резолвить нечем.
    /// ⚠️ "qdl_l{fnv}" — ключ по ссылке раздачи, обратно в TMDB не разворачивается.
    /// </summary>
    internal static (string kind, string value) ParseHistoryBucket(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket)) return (null, null);
        bucket = bucket.Trim();

        var m = _histTmdbBucket.Match(bucket);
        if (m.Success)
            return m.Groups[1].Value.TrimStart('0').Length == 0 ? (null, null) : (m.Groups[2].Value, m.Groups[1].Value);

        if (bucket.StartsWith("qdl_jut:", StringComparison.Ordinal))
        {
            string slug = bucket.Substring("qdl_jut:".Length).Trim();
            return JutSuParse.IsValidSlug(slug) ? ("jut", slug) : (null, null);
        }

        if (bucket.StartsWith("qdl_", StringComparison.Ordinal))
        {
            string rest = bucket.Substring("qdl_".Length);

            var sk = _histSeriesKey.Match(rest);
            if (sk.Success)
                return sk.Groups[1].Value.TrimStart('0').Length == 0 ? (null, null) : ("tmdb", sk.Groups[1].Value);

            if (_histInfohash.IsMatch(rest)) return ("hash", rest.ToLowerInvariant());
        }

        return (null, null);
    }

    #endregion

    #region карточки

    /// <summary>
    /// Карточка тайтла jut.su для истории — тот же слепок, что строит клиентский jutHistoryCard.
    /// Слаг едет ВНУТРИ id: Utils.clearCard в бандле сохраняет только поля из белого списка
    /// card_fields, произвольного jut_slug там нет, а id/title/img/source — есть.
    /// 🔴 source="jutsu" обязателен — сканер рекомендаций Lampa берёт из истории только карточки
    /// с source из cub/tmdb, иначе на каждый тайтл ушёл бы запрос к TMDB по мёртвому id.
    /// img — КОРНЕВОЙ относительный путь, а не абсолютный: одна и та же строка обязана работать
    /// и на LAN, и на tv.d1versy.com, и на реплике, куда она уедет репликацией.
    /// </summary>
    internal static JObject HistoryJutCard(string slug, string title)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;

        return new JObject
        {
            ["id"] = "jut:" + slug,
            ["source"] = "jutsu",
            ["title"] = string.IsNullOrWhiteSpace(title) ? slug : title,
            ["img"] = "/qdl/jut/poster?slug=" + Uri.EscapeDataString(slug)
        };
    }

    /// <summary>
    /// Приводит карточку к тому, чего ждёт Lampa от элемента истории.
    /// 🔴 Вход из истории роутер строит как method = original_name ? tv : movie, а наш slim-card
    /// полей сериала не несёт вовсе (только title/original_title) — сериал открылся бы как ФИЛЬМ,
    /// то есть ЧУЖИМ объектом TMDB: у movie и tv идентификаторы в разных пространствах.
    /// first_air_date нужен ещё и Favorite.continues — по нему ряд «Продолжить» отличает сериал.
    /// </summary>
    internal static JObject HistoryNormalizeCard(JObject card)
    {
        if (card == null) return null;
        var c = (JObject)card.DeepClone();

        bool isTv = c.Value<string>("media_type") == "tv"
                 || (c.Value<int?>("number_of_seasons") ?? 0) > 0
                 || (c.Value<int?>("number_of_episodes") ?? 0) > 0
                 || !string.IsNullOrEmpty(c.Value<string>("first_air_date"))
                 || !string.IsNullOrEmpty(c.Value<string>("name"));

        if (isTv)
        {
            if (string.IsNullOrEmpty(c.Value<string>("name"))) c["name"] = c["title"];
            if (string.IsNullOrEmpty(c.Value<string>("original_name"))) c["original_name"] = c["original_title"];
            if (string.IsNullOrEmpty(c.Value<string>("first_air_date"))) c["first_air_date"] = c["release_date"];
        }

        return c;
    }

    /// <summary>Айди так же, как его кладёт BookmarkController.AddToCategory: число числом, прочее строкой.</summary>
    static JToken HistoryIdToken(string id)
        => long.TryParse(id, out long n) && n > 0 ? new JValue(n) : new JValue(id);

    #endregion

    #region слияние

    /// <summary>
    /// Вливает карточки (СВЕЖИЕ ПЕРВЫМИ) в объект закладок устройства: только history и card.
    /// Возвращает количество НОВЫХ айди. Идемпотентно: повторный вызов с тем же входом ничего не меняет.
    /// </summary>
    internal static int MergeHistoryInto(JObject data, IList<JObject> cards, int cap)
    {
        if (data == null) return 0;
        cards ??= new List<JObject>();

        if (data["history"] is not JArray hist) { hist = new JArray(); data["history"] = hist; }
        if (data["card"] is not JArray cardArr) { cardArr = new JArray(); data["card"] = cardArr; }

        var existing = hist.Select(t => t?.ToString())
                           .Where(x => !string.IsNullOrEmpty(x))
                           .ToList();
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        int added = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<JToken>();

        foreach (var c in cards)
        {
            string id = c?["id"]?.ToString();
            if (string.IsNullOrEmpty(id) || id == "0" || !seen.Add(id)) continue;
            ordered.Add(HistoryIdToken(id));
            if (!existingSet.Contains(id)) added++;
        }

        foreach (string id in existing)
            if (seen.Add(id)) ordered.Add(HistoryIdToken(id));

        if (cap > 0 && ordered.Count > cap) ordered = ordered.Take(cap).ToList();
        data["history"] = new JArray(ordered);

        // Карточки: наши свежее (название/постер могли обновиться), местные дописываются следом.
        // Осиротевшие после капа не вычищаем — лишний объект в card безвреден, а чистка чужих
        // категорий здесь означала бы решать за пользователя, что ему больше не нужно.
        var have = new HashSet<string>(StringComparer.Ordinal);
        var outCards = new JArray();

        foreach (var c in cards)
        {
            string id = c?["id"]?.ToString();
            if (string.IsNullOrEmpty(id) || !have.Add(id)) continue;
            outCards.Add(c.DeepClone());
        }

        foreach (var c in cardArr.OfType<JObject>())
        {
            string id = c["id"]?.ToString();
            if (string.IsNullOrEmpty(id) || !have.Add(id)) continue;
            outCards.Add(c);
        }

        data["card"] = outCards;
        return added;
    }

    #endregion

    #region сборка

    /// <summary>Время записи EF (2026-08-16 16:36:16.1795326). Не разобралось — самое старое.</summary>
    internal static DateTime HistoryParseTime(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue;

    /// <summary>id в slim-card и hash в slim-card по файлам meta/*.json. Сеть не нужна.</summary>
    static (Dictionary<string, JObject> byId, Dictionary<string, JObject> byHash) HistoryMetaIndex()
    {
        var byId = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var byHash = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string dir = Path.Combine(ModInit.conf.cachePath, "meta");
            if (!Directory.Exists(dir)) return (byId, byHash);

            foreach (string f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var o = JObject.Parse(System.IO.File.ReadAllText(f));
                    byHash[Path.GetFileNameWithoutExtension(f)] = o;

                    int id = o.Value<int?>("id") ?? 0;
                    if (id > 0 && !byId.ContainsKey(id.ToString())) byId[id.ToString()] = o;
                }
                catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] history backfill meta index: " + ex.Message); }

        return (byId, byHash);
    }

    /// <summary>Ведро в карточку. null — не разрезолвилось (пойдёт в отчёт «пропущено»).</summary>
    static async Task<JObject> HistoryResolveCard(
        string kind, string value,
        Dictionary<string, JObject> byId, Dictionary<string, JObject> byHash,
        Dictionary<string, JObject> memo)
    {
        string memoKey = kind + ":" + value;
        if (memo.TryGetValue(memoKey, out var cached)) return cached;

        JObject card = null;

        if (kind == "jut")
        {
            var jc = JutHistoryCard(value, "watch");
            card = HistoryJutCard(value, jc?.Value<string>("title"));
        }
        else if (kind == "hash")
        {
            byHash.TryGetValue(value, out card);
        }
        else if (byId.TryGetValue(value, out var local))
        {
            card = local;
        }
        else if (int.TryParse(value, out int tmdbId) && tmdbId > 0)
        {
            // Меты нет (загрузку удалили, смотрели онлайн) — добираем своим же прокси /tmdb/api.
            // Для ведра без типа сначала tv: SeriesKey рождается на пути «слежение за сериалом»,
            // фильм туда попадает исключением.
            if (kind == "tv" || kind == "tmdb") card = await TmdbCard(tmdbId, true);
            if (card == null && (kind == "movie" || kind == "tmdb")) card = await TmdbCard(tmdbId, false);
        }

        card = HistoryNormalizeCard(card);
        memo[memoKey] = card;
        return card;
    }

    /// <summary>Просмотренные слаги jut.su этого устройства: слаг в время. Файл на устройство.</summary>
    static Dictionary<string, DateTime> HistoryJutWatched(string uid)
    {
        var res = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var jo = JsonStore.ReadObject(JutHistoryPath(JutHistoryBucket(uid)));
            if (jo?["watched"] is not JObject watched) return res;

            foreach (var p in watched.Properties())
            {
                if (!JutSuParse.IsValidSlug(p.Name)) continue;

                // ⚠️ Разбор — на КАЖДОЙ записи в своём try: одна кривая строка не должна стоить
                // всей истории устройства. Формат метки — ISO-8601 (JutHistoryTouchWatch кладёт
                // DateTime), но запас на unix-секунды оставлен: дешевле, чем поймать это в бою.
                try { res[p.Name] = HistoryJutAt(p.Value?["at"]); } catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] history backfill jut: " + ex.Message); }

        return res;
    }

    /// <summary>Метка времени записи jut: ISO-8601 (как её пишет сервер) либо unix-секунды.</summary>
    internal static DateTime HistoryJutAt(JToken at)
    {
        if (at == null) return DateTime.MinValue;

        if (at.Type == JTokenType.Date) return at.Value<DateTime>().ToUniversalTime();
        if (at.Type == JTokenType.Integer)
        {
            long n = at.Value<long>();
            return n > 0 ? DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime : DateTime.MinValue;
        }

        return HistoryParseTime(at.ToString());
    }

    /// <summary>
    /// Собрать (и по желанию применить) историю. apply=false — сухой прогон, в БД не пишем ничего.
    /// </summary>
    internal static async Task<JObject> HistoryBackfillRun(bool apply)
    {
        var perUser = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);

        void Touch(string user, string bucket, DateTime at)
        {
            if (!perUser.TryGetValue(user, out var buckets))
                perUser[user] = buckets = new Dictionary<string, DateTime>(StringComparer.Ordinal);

            if (!buckets.TryGetValue(bucket, out var prev) || at > prev) buckets[bucket] = at;
        }

        void Skip(string bucket)
            => skipped[bucket] = skipped.TryGetValue(bucket, out int n) ? n + 1 : 1;

        // ── 1. таймкоды: самое свежее время на пару «устройство + ведро» ──
        try
        {
            if (System.IO.File.Exists(TimeCodeDbPath))
            {
                using var db = OpenDb(TimeCodeDbPath);
                if (TableExists(db, "timecodes"))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "select user, card, updated from timecodes limit 50000";
                    using var r = cmd.ExecuteReader();

                    while (r.Read())
                    {
                        string user = r.IsDBNull(0) ? null : r.GetString(0);
                        string bucket = r.IsDBNull(1) ? null : r.GetString(1);
                        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(bucket)) continue;

                        Touch(user, bucket, HistoryParseTime(r.IsDBNull(2) ? null : r.GetValue(2)?.ToString()));
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] history backfill timecodes: " + ex.Message); }

        // ── 2. история jut.su: она полнее таймкодов (пишется по факту байтов, а не по позиции) ──
        // Устройства берём и оттуда: аниме смотрят онлайн, и у такого клиента таймкодов может
        // не быть вовсе — по одним только таймкодам он остался бы с пустой историей.
        // ⚠️ _shared — это БАКЕТ для запросов без uid, а не устройство: писать ему закладки некому.
        var uids = new HashSet<string>(perUser.Keys, StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(JutHistoryDir()))
                foreach (string f in Directory.EnumerateFiles(JutHistoryDir(), "*.json"))
                {
                    string uid = Path.GetFileNameWithoutExtension(f);
                    if (!string.IsNullOrEmpty(uid) && uid != JutSharedBucket) uids.Add(uid);
                }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] history backfill jut dir: " + ex.Message); }

        foreach (string uid in uids)
            foreach (var kv in HistoryJutWatched(uid))
                Touch(uid, "qdl_jut:" + kv.Key, kv.Value);

        // ── 3. резолв ведёр в карточки ──
        var (byId, byHash) = HistoryMetaIndex();
        var memo = new Dictionary<string, JObject>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);

        foreach (var user in perUser.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var cards = new List<JObject>();
            // Разные вёдра одного тайтла — норма: '270603_tv' пишет полная карточка, 'qdl_t270603' —
            // экран серий. Дедуп здесь, а не только в слиянии, чтобы отчёт не врал про число тайтлов.
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in perUser[user].OrderByDescending(x => x.Value))
            {
                var (kind, value) = ParseHistoryBucket(kv.Key);
                if (kind == null) { Skip(kv.Key); continue; }

                var card = await HistoryResolveCard(kind, value, byId, byHash, memo);
                if (card == null) { Skip(kv.Key); continue; }

                string id = card["id"]?.ToString();
                if (string.IsNullOrEmpty(id) || !seenIds.Add(id)) continue;

                cards.Add(card);
            }

            resolved[user] = cards;
        }

        // ── 4. применение ──
        var users = new JArray();
        int totalAdded = 0;

        foreach (var user in resolved.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var cards = resolved[user];
            int added = apply ? HistoryBackfillWrite(user, cards) : HistoryCountNew(user, cards);
            totalAdded += added;

            users.Add(new JObject
            {
                ["uid"] = user,
                ["titles"] = cards.Count,
                ["added"] = added,
                ["sample"] = new JArray(cards.Take(5).Select(c => (JToken)(c.Value<string>("title") ?? c.Value<string>("name") ?? c["id"]?.ToString())))
            });
        }

        if (apply)
            Console.WriteLine($"[QbitDownload] history backfill: устройств {users.Count}, новых записей {totalAdded}");

        return new JObject
        {
            ["success"] = true,
            ["applied"] = apply,
            ["users"] = users,
            ["added"] = totalAdded,
            ["skipped"] = new JObject(skipped.OrderByDescending(x => x.Value).Select(x => new JProperty(x.Key, x.Value)))
        };
    }

    /// <summary>Сколько айди легло бы новыми — без записи (сухой прогон).</summary>
    static int HistoryCountNew(string user, IList<JObject> cards)
        => MergeHistoryInto(HistoryReadBookmarks(user) ?? new JObject(), cards, HistoryBackfillCap);

    static JObject HistoryReadBookmarks(string user)
    {
        try
        {
            if (!System.IO.File.Exists(SyncDbPath)) return null;

            using var db = OpenDb(SyncDbPath);
            if (!TableExists(db, "bookmarks")) return null;

            using var cmd = db.CreateCommand();
            cmd.CommandText = "select data from bookmarks where user=$u limit 1";
            cmd.Parameters.AddWithValue("$u", user);

            string raw = cmd.ExecuteScalar()?.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : JObject.Parse(raw);
        }
        catch { return null; }
    }

    /// <summary>Слияние и запись строки устройства. Возвращает количество новых айди.</summary>
    static int HistoryBackfillWrite(string user, IList<JObject> cards)
    {
        if (string.IsNullOrEmpty(user) || cards == null || cards.Count == 0) return 0;
        if (!System.IO.File.Exists(SyncDbPath)) return 0;

        try
        {
            using var db = OpenDb(SyncDbPath);
            if (!TableExists(db, "bookmarks")) return 0;

            using var tx = db.BeginTransaction();

            string raw;
            using (var sel = db.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "select data from bookmarks where user=$u limit 1";
                sel.Parameters.AddWithValue("$u", user);
                raw = sel.ExecuteScalar()?.ToString();
            }

            JObject data;
            try { data = string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw); }
            catch { data = new JObject(); }

            int added = MergeHistoryInto(data, cards, HistoryBackfillCap);

            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = raw == null
                    ? "insert into bookmarks(user, data, updated) values($u,$d,$up)"
                    : "update bookmarks set data=$d, updated=$up where user=$u";
                cmd.Parameters.AddWithValue("$u", user);
                cmd.Parameters.AddWithValue("$d", data.ToString(Formatting.None));
                cmd.Parameters.AddWithValue("$up", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return added;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] history backfill write: " + ex.Message);
            return 0;
        }
    }

    #endregion
}
