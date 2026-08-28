using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QbitDownload;

// ───────────────────────────────────────────────────────────────────────────────
// Сезоны одного сериала — ОДНА карточка «Загрузок» (qdl 2.78).
//
// Жалоба владельца: «Телохранители» лежали в «Загрузках» двумя визуально ОДИНАКОВЫМИ
// карточками (постер, название и год у них общие — мета одна и та же), и отличались они
// только тем, что внутри разные сезоны. Хуже того, с полной карточки сериала второй сезон
// был НЕДОСТИЖИМ вовсе: findDownload на клиенте берёт ПЕРВОЕ совпадение по TMDB id, то есть
// кнопки «Смотреть»/«Продолжить» всегда вели в одну из двух раздач, и в какую именно —
// решал порядок сортировки по актуальности.
//
// Что делаем: карточки с ОДНИМ TMDB id сериала склеиваются в одну (здесь), а /qdl/episodes
// отдаёт по любому хешу группы общий плейлист всех её раздач (EpisodeHunter.cs). Клиенту
// новых понятий не нужно: файл серии и так несёт свой `hash` (механика доноров охоты), а
// сортировка по (вид, сезон, номер) в qdl.js уже была — оттого 2-й сезон и встаёт после
// 1-го по сериям, ровно как просили.
//
// 🔴 Главная часть группы — САМАЯ РАННЯЯ по added, а не «первый сезон». Выбор обязан быть
// СТАБИЛЬНЫМ: на hash главной части висят постер карточки, запомненная озвучка (localStorage
// клиента), кеш серий и activity. Сезон же берётся из НАЗВАНИЯ раздачи и бывает неизвестен
// («Сезоны 1-3», кривое имя) — сделав ключом его, мы бы переклеивали карточку на другой hash
// при каждой докачке нового сезона.
//
// 🔴 jut.su и XSMART в группы не берём. У них СВОЙ контур подписки (слаг / cat-id) и один
// маркер на весь тайтл: там сезоны и так внутри одной карточки, а склейка сломала бы пункт
// «Следить за новыми сериями» — он гейтится наличием ровно одного контура на карточке.
//
// Кто ещё это читает: /qdl/list (Controller.cs) и /qdl/episodes (EpisodeHunter.cs).
// Киллсвитч на лету — `mergeSeasons: false` в секции QbitDownload init.conf.
// ───────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    /// <summary>TMDB id сериала, по которому карточку МОЖНО объединять с другими. 0 = нельзя.</summary>
    static int SeriesMergeId(JObject meta)
    {
        if (meta == null) return 0;
        // media_type обязателен: у movie и tv id живут в РАЗНЫХ пространствах, совпадение
        // номера без типа — чужой объект (тот же инвариант, что в findDownload на клиенте).
        if ((meta.Value<string>("media_type") ?? "") != "tv") return 0;
        int id = meta.Value<int?>("id") ?? 0;
        return id > 0 ? id : 0;
    }

    /// <summary>Карточка из /qdl/list пригодна к объединению? Возвращает id сериала или 0.</summary>
    static int ListMergeId(JObject item)
    {
        if (item == null) return 0;
        if (item["jut"] != null || item["xsmart"] != null) return 0;   // свои контуры подписки
        return SeriesMergeId(item["meta"] as JObject);
    }

    /// <summary>Сезон карточки по названию раздачи. 0 = неизвестен или их несколько.</summary>
    static int CardSeason(string name)
    {
        try
        {
            var s = TorrentScoring.ParseSeasons(name ?? "");
            return s.Count == 1 ? s[0] : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Одна карточка из нескольких раздач сериала. Числа агрегируются честно:
    /// размер — сумма, прогресс — ВЗВЕШЕННЫЙ по размеру (иначе 99%-й сезон на 20 ГБ и пустой
    /// на 1 ГБ дали бы «50%»), activity — максимум (новая серия в любом сезоне поднимает
    /// карточку наверх), added — минимум (карточка появилась, когда скачали первую раздачу).
    /// </summary>
    static JObject MergeSeriesGroup(List<JObject> group)
    {
        // порядок частей — по сезону, неизвестный сезон в конец; тай-брейк — дата добавления
        var ordered = group
            .OrderBy(p => { int s = CardSeason(p.Value<string>("name")); return s > 0 ? s : int.MaxValue; })
            .ThenBy(p => p.Value<long?>("added") ?? 0)
            .ThenBy(p => p.Value<string>("hash") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primary = group
            .OrderBy(p => p.Value<long?>("added") ?? 0)
            .ThenBy(p => p.Value<string>("hash") ?? "", StringComparer.OrdinalIgnoreCase)
            .First();

        var card = (JObject)primary.DeepClone();

        long size = 0, added = long.MaxValue, activity = 0;
        double weighted = 0;
        bool watched = false, allLocal = true;
        string partialState = null, liveState = null;
        var seasons = new SortedSet<int>();
        var parts = new JArray();

        foreach (var p in ordered)
        {
            long ps = Math.Max(0, p.Value<long?>("size") ?? 0);
            double pp = Math.Clamp(p.Value<double?>("progress") ?? 0, 0, 1);
            long pa = p.Value<long?>("added") ?? 0;
            bool loc = (p.Value<bool?>("local") ?? false) || p.Value<string>("state") == "local";
            int sn = CardSeason(p.Value<string>("name"));

            size += ps;
            weighted += ps * pp;
            if (pa > 0) added = Math.Min(added, pa);
            activity = Math.Max(activity, p.Value<long?>("activity") ?? pa);
            watched |= p.Value<bool?>("watched") ?? false;
            allLocal &= loc;
            if (sn > 0) seasons.Add(sn);
            if (pp < 0.999 && partialState == null) partialState = p.Value<string>("state");
            if (!loc && liveState == null) liveState = p.Value<string>("state");

            var part = new JObject
            {
                ["hash"] = p.Value<string>("hash"),
                ["name"] = p.Value<string>("name"),
                ["size"] = ps,
                ["progress"] = pp,
                ["state"] = p.Value<string>("state"),
                ["added"] = pa,
                ["activity"] = p.Value<long?>("activity") ?? pa,
                ["watched"] = p.Value<bool?>("watched") ?? false,
                ["local"] = loc
            };
            if (sn > 0) part["season"] = sn;
            parts.Add(part);
        }

        card["size"] = size;
        card["progress"] = size > 0 ? Math.Min(1.0, weighted / size) : (primary.Value<double?>("progress") ?? 0);
        card["added"] = added == long.MaxValue ? 0 : added;
        card["activity"] = activity;
        card["watched"] = watched;
        // 🔴 local/state берём по ГРУППЕ, а не у главной части: транскодированный 1-й сезон
        // (local) рядом с торрентным 2-м иначе выдал бы карточке бейдж MP4 и убрал пункт
        // «Транскодировать», хотя половина сериала — обычный торрент.
        card["local"] = allLocal;
        card["state"] = partialState ?? (allLocal ? "local" : (liveState ?? primary.Value<string>("state")));
        card["parts"] = parts;
        if (seasons.Count > 0) card["seasons"] = new JArray(seasons.Select(x => (object)x));
        return card;
    }

    /// <summary>
    /// Склейка карточек одного сериала в списке /qdl/list. Порядок остальных не трогаем —
    /// сортировка по актуальности идёт следом и всё равно решает сама.
    /// </summary>
    static JArray MergeSeriesCards(JArray items)
    {
        if (items == null || items.Count < 2) return items;

        var groups = new Dictionary<int, List<JObject>>();
        foreach (var it in items.OfType<JObject>())
        {
            int id = ListMergeId(it);
            if (id <= 0) continue;
            if (!groups.TryGetValue(id, out var l)) groups[id] = l = new List<JObject>();
            l.Add(it);
        }

        var replace = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups.Values)
        {
            if (g.Count < 2) continue;
            var card = MergeSeriesGroup(g);
            string ph = card.Value<string>("hash") ?? "";
            if (ph.Length == 0) continue;
            replace[ph] = card;
            foreach (var p in g)
            {
                string h = p.Value<string>("hash") ?? "";
                if (h.Length > 0 && !ph.Equals(h, StringComparison.OrdinalIgnoreCase)) drop.Add(h);
            }
        }
        if (replace.Count == 0) return items;

        var res = new JArray();
        foreach (var it in items.OfType<JObject>())
        {
            string h = it.Value<string>("hash") ?? "";
            if (drop.Contains(h)) continue;
            res.Add(replace.TryGetValue(h, out var m) ? m : it);
        }
        return res;
    }

    // ── индекс «hash → id сериала» для /qdl/episodes ───────────────────────────
    // Список карточек там не строится (это отдельная дорогая сборка с походом в qBit), а
    // сиблингов знать надо — поэтому индекс собирается по каталогу meta/ из горячего слоя.
    // Мемоизация обязательна: /qdl/episodes зовётся на КАЖДОЕ открытие карточки (prewarm),
    // а обход 90 мет с клонированием каждой — не та цена, которую можно платить на этом пути.
    // Инвалидация — по снимку листинга (SaveMeta/PurgeCache зовут ForgetDir) + TTL-подстраховка.
    static readonly object _seriesIdxLock = new();
    static Dictionary<string, int> _seriesIdx;
    static string _seriesIdxDir;
    static int _seriesIdxCount = -1;
    static DateTime _seriesIdxAt;

    internal static void SeriesIndexDrop()
    {
        lock (_seriesIdxLock) { _seriesIdx = null; _seriesIdxDir = null; _seriesIdxCount = -1; }
    }

    static Dictionary<string, int> SeriesIndex()
    {
        string dir = Path.Combine(ModInit.conf.cachePath, "meta");
        var files = JsonStore.List(dir, "*.json") ?? Array.Empty<string>();
        lock (_seriesIdxLock)
        {
            // каталог в ключе памятки: cachePath меняется на лету (updateConf) и в тестах,
            // а совпасть числу файлов между двумя разными кешами ничто не мешает
            if (_seriesIdx != null && _seriesIdxCount == files.Length
                && string.Equals(_seriesIdxDir, dir, StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _seriesIdxAt).TotalMinutes < 5)
                return _seriesIdx;

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string h = Path.GetFileNameWithoutExtension(f);
                if (!ValidHash(h)) continue;
                int id = SeriesMergeId(LoadMeta(h));
                if (id <= 0) continue;
                var loc = LoadLocal(h);
                if (loc != null && (loc["jut"] != null || loc["xsmart"] != null)) continue;   // свои контуры
                map[h] = id;
            }
            _seriesIdx = map; _seriesIdxDir = dir; _seriesIdxCount = files.Length; _seriesIdxAt = DateTime.UtcNow;
            return map;
        }
    }

    /// <summary>
    /// Все хеши группы сезонов (включая свой), в порядке сезонов; null — карточка одиночная.
    /// Мёртвая мета (раздачу удалили мимо PurgeCache) не страшна: файлов у неё не найдётся,
    /// и в общий плейлист она ничего не добавит.
    /// </summary>
    static List<string> SeriesGroupHashes(string hash)
    {
        if (!ModInit.conf.mergeSeasons || !ValidHash(hash)) return null;
        try
        {
            var idx = SeriesIndex();
            if (!idx.TryGetValue(hash, out int id)) return null;
            var all = idx.Where(kv => kv.Value == id).Select(kv => kv.Key)
                         .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();   // порядок детерминирован
            return all.Count < 2 ? null : all;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] series group: " + ex.Message); return null; }
    }

    // ── общий плейлист группы (для /qdl/episodes) ──────────────────────────────

    /// <summary>Ранг вида серии: обычные → экстры. Тот же порядок, что у epKindRank в qdl.js.</summary>
    static int EpisodeKindRank(string epkey)
    {
        if (string.IsNullOrEmpty(epkey)) return 5;                       // непонятный файл — в самый конец
        if (System.Text.RegularExpressions.Regex.IsMatch(epkey, @"^s\d+e\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 0;
        if (System.Text.RegularExpressions.Regex.IsMatch(epkey, @"^film\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 1;
        if (System.Text.RegularExpressions.Regex.IsMatch(epkey, @"^ova\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 2;
        if (System.Text.RegularExpressions.Regex.IsMatch(epkey, @"^gameova\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 3;
        return 4;
    }

    /// <summary>
    /// Серии всех раздач группы в один список: на epkey — ОДНА запись, порядок (вид, сезон, номер).
    ///
    /// 🔴 Дедуп обязателен, и он не теоретический: группа склеивается по TMDB id, а под одним id
    /// лежат и «сезон 1 + сезон 2» (нужный случай), и «тот же сезон, перекачанный в другом
    /// качестве». Во втором случае без дедупа зритель получил бы каждую серию дважды.
    /// Побеждает докачанная копия, при равенстве — та, что крупнее (обычно и качественнее).
    /// Экстры и неразобранные файлы (у них epkey нет) не дедупятся — они уходят в хвост как есть.
    /// </summary>
    static JArray MergeGroupEpisodes(JArray all)
    {
        var best = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var tail = new List<JObject>();

        foreach (var e in (all ?? new JArray()).OfType<JObject>())
        {
            string key = e.Value<string>("epkey");
            if (string.IsNullOrEmpty(key)) { tail.Add(e); continue; }
            if (!best.TryGetValue(key, out var have)) { best[key] = e; order.Add(key); continue; }

            double hp = have.Value<double?>("progress") ?? 0, np = e.Value<double?>("progress") ?? 0;
            long hs = have.Value<long?>("size") ?? 0, ns = e.Value<long?>("size") ?? 0;
            bool wins = (np >= 0.999 && hp < 0.999) || ((np >= 0.999) == (hp >= 0.999) && ns > hs);
            if (wins) best[key] = e;
        }

        var res = new JArray();
        foreach (var e in order.Select(k => best[k])
                               .OrderBy(x => EpisodeKindRank(x.Value<string>("epkey")))
                               .ThenBy(x => x.Value<int?>("season") ?? 0)
                               .ThenBy(x => x.Value<int?>("episode") ?? 0)
                               .ThenBy(x => x.Value<string>("epkey"), StringComparer.OrdinalIgnoreCase))
            res.Add(e);
        foreach (var e in tail) res.Add(e);
        return res;
    }
}
