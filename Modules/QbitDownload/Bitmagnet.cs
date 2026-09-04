using Newtonsoft.Json.Linq;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// Источник раздач из локального индекса DHT-краулера bitmagnet (Postgres на хосте).
//
// Зачем: живых русских трекеров осталось мало, rutracker закрыт Cloudflare по TLS-отпечатку
// (не чинится ни кукой, ни прокси, ни headless-браузером — проверено). А рядом уже лежит
// ~16 млн раздач, из них ~1 млн привязаны к TMDB, и краулер жив (+60 тыс. записей в сутки).
//
// Почему без мусора: ищем СТРОГО по TMDB id карточки, а не по названию. Совпадение точное,
// поэтому «Миньон 1998» вместо «Миньоны 2015» или саундтрек вместо фильма приехать не могут
// в принципе. Свободный текстовый поиск по базе сознательно НЕ делаем: там 11.7 млн раздач
// вообще без метаданных и 696 тыс. xxx — вот это и был бы мусор. content_type в WHERE
// ОБЯЗАТЕЛЕН: пространства id у фильмов и сериалов разные — под TMDB 94997 («Дом Дракона»)
// в базе висят и 4 строки xxx «A Thousand and One Erotic Nights (1982)».
//
// qdl 2.107 — две выборки вместо одной:
//   • интерактив (/qdl/search): top-N по сидам ∪ top-M по дате появления в DHT. Без второй
//     ветки свежая серия у сериала с 1149 привязанными раздачами не попадала в сотню — русские
//     S03E10 «Укрытия» стояли на 208-м и 362-м местах по сидам;
//   • охота (сезонный скоуп): ВСЕ раздачи сезона по episodes jsonb ({"3":{"10":{}}} — серия,
//     {"3":{}} — пак), плюс списки файлов multi-торрентов — для файловой подписи «своя раздача»
//     и добора качества у паков без токена разрешения в имени.
// Сиды: greatest(tc.seeders, dht.seeders). Копия в torrent_contents обнуляется при
// переклассификации, честный замер лежит в torrents_torrent_sources. Оба — подсказка
// (sid_hint), не гейт: у ColdFilm 1080p в базе стояло «2», реально 35.
//
// Ранжирование не трогаем: раздачи просто доливаются в общий котёл, а порядок и ⭐ по-прежнему
// определяет TorrentScoring (⭐ bitmagnet-строкам не выдаётся — см. SortAndMark).
// Базовый тип объявлен в Controller.cs — в partial-классе он указывается один раз.
public partial class QbitController
{
    // Строка выборки как есть (без IO) — чтобы маппинг в JObject тестировался без Postgres.
    internal sealed class BitmagnetRow
    {
        public string name, btih, res, codec, source, modifier, episodesJson, filesStatus, contentTitle, contentOriginal;
        public long size;
        public int seeders, leechers, filesCount;
        public DateTime? published, created, updated;
        public bool langRu;
        public List<(string name, long size)> files;   // null = не запрашивали / запрос не удался
    }

    // Общая часть трёх запросов. Позиции колонок фиксированы — их читает BitmagnetReadRow.
    // ⚠️ Текст под сторожем в тестах (BitmagnetTests): content_type, torrents_torrent_sources,
    // episodes @>, отсутствие текстового поиска по имени.
    internal const string BmSqlSelect = @"
select t.name,
       encode(t.info_hash, 'hex')                                          as btih,
       coalesce(tc.size, t.size)                                           as size,
       greatest(coalesce(tc.seeders, 0), coalesce(s.seeders, 0))           as seeders,
       greatest(coalesce(tc.leechers, 0), coalesce(s.leechers, 0))         as leechers,
       tc.video_resolution,
       tc.video_codec,
       tc.published_at,
       tc.updated_at,
       -- @> вместо оператора ? : знак вопроса Npgsql может принять за плейсхолдер параметра
       (tc.languages @> '[""ru""]'::jsonb)                                 as lang_ru,
       tc.video_source,
       tc.video_modifier,
       tc.episodes::text                                                   as episodes,
       coalesce(tc.files_count, t.files_count, 0)                          as files_count,
       t.files_status::text                                                as files_status,
       t.created_at,
       c.title,
       c.original_title
from torrent_contents tc
join torrents t on t.info_hash = tc.info_hash
left join content c on c.type = tc.content_type and c.source = tc.content_source and c.id = tc.content_id
left join torrents_torrent_sources s on s.info_hash = tc.info_hash and s.source = 'dht'
where tc.content_source = 'tmdb'
  and tc.content_id = @id
  and tc.content_type = @ctype
  and coalesce(t.private, false) = false";

    // Сезонный скоуп охоты: {"<season>":{}} содержится и в одиночках {"3":{"10":{}}}, и в паках {"3":{}}.
    // Сортировка по алиасу seeders (greatest), не по сырому tc.seeders; limit — страховочный потолок.
    internal const string BmSqlScoped = BmSqlSelect + @"
  and tc.episodes @> @seasonJson::jsonb
order by seeders desc nulls last
limit @huntLim";

    // Интерактив: top-N по сидам ∪ top-M по появлению в DHT (одна и та же колонка t.created_at и в
    // select-списке, и в order by). union без all — одинаковые строки схлопываются.
    internal const string BmSqlTop = "(" + BmSqlSelect + @"
order by seeders desc nulls last
limit @lim)
union
(" + BmSqlSelect + @"
order by t.created_at desc
limit @fresh)";

    internal const string BmSqlTopSeedsOnly = BmSqlSelect + @"
order by seeders desc nulls last
limit @lim";

    // Списки файлов multi-торрентов (у single строк в torrent_files нет — берём (t.name, t.size)).
    // info_hash — bytea, поэтому параметр обязан быть byte[][] (Array | Bytea), а не hex-строки.
    internal const string BmSqlFiles = @"
select encode(info_hash, 'hex'), path, size
from torrent_files
where info_hash = any(@hashes)";

    static BitmagnetRow BitmagnetReadRow(NpgsqlDataReader r)
    {
        return new BitmagnetRow
        {
            name = r.IsDBNull(0) ? null : r.GetString(0),
            btih = r.IsDBNull(1) ? null : r.GetString(1),
            size = r.IsDBNull(2) ? 0 : r.GetInt64(2),
            seeders = r.IsDBNull(3) ? 0 : r.GetInt32(3),
            leechers = r.IsDBNull(4) ? 0 : r.GetInt32(4),
            res = r.IsDBNull(5) ? null : r.GetString(5),
            codec = r.IsDBNull(6) ? null : r.GetString(6),
            published = r.IsDBNull(7) ? (DateTime?)null : r.GetDateTime(7),
            updated = r.IsDBNull(8) ? (DateTime?)null : r.GetDateTime(8),
            langRu = !r.IsDBNull(9) && r.GetBoolean(9),
            source = r.IsDBNull(10) ? null : r.GetString(10),
            modifier = r.IsDBNull(11) ? null : r.GetString(11),
            episodesJson = r.IsDBNull(12) ? null : r.GetString(12),
            filesCount = r.IsDBNull(13) ? 0 : Convert.ToInt32(r.GetValue(13)),
            filesStatus = r.IsDBNull(14) ? null : r.GetString(14),
            created = r.IsDBNull(15) ? (DateTime?)null : r.GetDateTime(15),
            contentTitle = r.IsDBNull(16) ? null : r.GetString(16),
            contentOriginal = r.IsDBNull(17) ? null : r.GetString(17),
        };
    }

    /// <summary>
    /// Раздачи одного тайтла по TMDB id. scopeSeason > 0 — сезонная выборка для охоты (все раздачи
    /// сезона + файлы multi-торрентов); 0 — интерактивная (по сидам ∪ по свежести).
    /// Пустой список = нечего добавить (это не ошибка).
    /// </summary>
    static async Task<JArray> FetchBitmagnet(string tmdbId, int is_serial, int scopeSeason = 0)
    {
        var result = new JArray();

        string conn = ModInit.conf.bitmagnetConnection;
        if (string.IsNullOrWhiteSpace(conn) || string.IsNullOrWhiteSpace(tmdbId))
            return result;

        // content_type сверяем с типом карточки: у TMDB id фильма и сериала свои пространства,
        // и без этого id 1399 мог бы притащить и фильм, и сериал разом.
        string wantType = is_serial >= 2 ? "tv_show" : "movie";
        var rows = new List<BitmagnetRow>();

        try
        {
            await using var db = new NpgsqlConnection(conn);
            await db.OpenAsync();

            string sql;
            if (scopeSeason > 0) sql = BmSqlScoped;
            else sql = ModInit.conf.bitmagnetFreshLimit > 0 ? BmSqlTop : BmSqlTopSeedsOnly;

            await using var cmd = new NpgsqlCommand(sql, db);
            cmd.CommandTimeout = Math.Max(2, ModInit.conf.bitmagnetTimeoutSec);
            cmd.Parameters.AddWithValue("id", tmdbId);
            cmd.Parameters.AddWithValue("ctype", wantType);
            if (scopeSeason > 0)
            {
                cmd.Parameters.AddWithValue("seasonJson", NpgsqlDbType.Text, "{\"" + scopeSeason + "\":{}}");
                cmd.Parameters.AddWithValue("huntLim", Math.Max(1, ModInit.conf.bitmagnetHuntLimit));
            }
            else
            {
                cmd.Parameters.AddWithValue("lim", Math.Max(1, ModInit.conf.bitmagnetLimit));
                if (ModInit.conf.bitmagnetFreshLimit > 0)
                    cmd.Parameters.AddWithValue("fresh", Math.Max(1, ModInit.conf.bitmagnetFreshLimit));
            }

            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    var row = BitmagnetReadRow(r);
                    if (string.IsNullOrWhiteSpace(row.name) || string.IsNullOrWhiteSpace(row.btih)) continue;
                    rows.Add(row);
                }

            // Файлы — только для охоты и только multi (в отдельном try: провал файлового запроса даёт
            // bm_files = null, «гейт файлов молчит», а не пустую выдачу источника).
            if (scopeSeason > 0)
                await BitmagnetLoadFiles(db, rows);
        }
        catch (Exception ex)
        {
            // Источник дополнительный: его недоступность не должна ломать поиск по трекерам.
            Console.WriteLine($"[QbitDownload] bitmagnet недоступен ({ex.GetType().Name}): {ex.Message}");
            return new JArray();
        }

        foreach (var row in rows)
            result.Add(BitmagnetItem(row, scopeSeason));
        return result;
    }

    static async Task BitmagnetLoadFiles(NpgsqlConnection db, List<BitmagnetRow> rows)
    {
        try
        {
            var byHash = new Dictionary<string, BitmagnetRow>(StringComparer.OrdinalIgnoreCase);
            var hashes = new List<byte[]>();
            foreach (var row in rows)
            {
                if (string.Equals(row.filesStatus, "single", StringComparison.OrdinalIgnoreCase))
                {
                    // у single-торрента в torrent_files строк нет — файл это сам торрент
                    row.files = new List<(string, long)> { (row.name, row.size) };
                    continue;
                }
                if (!string.Equals(row.filesStatus, "multi", StringComparison.OrdinalIgnoreCase)) continue;   // over_threshold/no_info — списка нет
                try { hashes.Add(Convert.FromHexString(row.btih)); byHash[row.btih] = row; row.files = new List<(string, long)>(); }
                catch { }
            }
            if (hashes.Count == 0) return;

            await using var cmd = new NpgsqlCommand(BmSqlFiles, db);
            cmd.CommandTimeout = Math.Max(2, ModInit.conf.bitmagnetTimeoutSec);
            cmd.Parameters.Add(new NpgsqlParameter("hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = hashes.ToArray() });
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string h = r.IsDBNull(0) ? null : r.GetString(0);
                string path = r.IsDBNull(1) ? null : r.GetString(1);
                long size = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                if (h == null || path == null || !byHash.TryGetValue(h, out var row)) continue;
                if (!_videoExtRx.IsMatch(path)) continue;   // nfo/srt/jpg — не файлы серий
                row.files.Add((path, size));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QbitDownload] bitmagnet: список файлов недоступен ({ex.GetType().Name}: {ex.Message}) — файловая подпись доноров отключена в этом проходе");
            foreach (var row in rows) if (!string.Equals(row.filesStatus, "single", StringComparison.OrdinalIgnoreCase)) row.files = null;
        }
    }

    static readonly Regex _bmLegacyCodecRx = new(@"(?i)^(xvid|divx|mpeg-?2|mpeg-?4)$", RegexOptions.Compiled);
    static readonly Regex _legacyCodecInTitleRx = new(@"(?i)(?<![a-z0-9])(xvid|divx)(?![a-z0-9])", RegexOptions.Compiled);
    static readonly Regex _screenerInTitleRx = new(@"(?i)(?<![a-z0-9])(camrip|hdcam|telesync|telecine|workprint|screener|dvdscr)(?![a-z0-9])", RegexOptions.Compiled);
    static readonly HashSet<string> _screenerSources = new(StringComparer.OrdinalIgnoreCase) { "CAM", "TELESYNC", "TELECINE", "WORKPRINT" };

    /// <summary>
    /// Чистый маппинг строки bitmagnet в элемент выдачи. Поверх полей, которые есть у всех источников:
    /// sid_hint (сиды — подсказка), id_match, id_title/id_title_original (эталон имени для аниме с
    /// японским оригиналом на карточке), bm_legacy/bm_screener, bm_season/bm_eps/bm_pack (из episodes),
    /// bm_files (имя+размер видеофайлов), files_count/files_status.
    /// </summary>
    internal static JObject BitmagnetItem(BitmagnetRow r, int scopeSeason)
    {
        string name = r.name ?? "";
        bool legacy = (r.codec != null && _bmLegacyCodecRx.IsMatch(r.codec.Trim())) || _legacyCodecInTitleRx.IsMatch(name);
        bool screener = (r.source != null && _screenerSources.Contains(r.source.Trim()))
                     || string.Equals(r.modifier?.Trim(), "SCREENER", StringComparison.OrdinalIgnoreCase)
                     || _screenerInTitleRx.IsMatch(name);

        // качество: разрешение из БД → имя → (у legacy-кодека без разрешения) 480 → имена файлов пака
        int quality = QualityFromResolution(r.res) ?? QualityFromTitle(name);
        if (quality <= 0 && legacy) quality = 480;
        if (quality <= 0 && r.files != null && r.files.Count > 0)
            quality = DominantQuality(r.files.Select(f => f.name));

        var it = new JObject
        {
            ["title"] = name,
            // Трекера нет — качаем по DHT, поэтому ни логина, ни parselink не нужно.
            ["magnet"] = $"magnet:?xt=urn:btih:{r.btih}&dn={HttpUtility.UrlEncode(name)}",
            ["parselink"] = null,
            ["tracker"] = "bitmagnet",
            ["sid"] = Math.Max(0, r.seeders),
            ["pir"] = Math.Max(0, r.leechers),
            // Сиды краулера — подсказка, не измерение: скоринг даёт им нейтраль, охота не гейтит,
            // ⭐ не выдаёт. Живость доказывает проба метаданных в qBit.
            ["sid_hint"] = true,
            ["size"] = HumanSize(r.size),
            ["sizeBytes"] = r.size,
            ["quality"] = quality,
            ["codec"] = NormalizeCodec(r.codec) ?? CodecFromTitle(name),
            ["date"] = r.published?.ToString("MM/dd/yyyy HH:mm:ss"),
            // Подсказка языка из БД. Она НЕПОЛНАЯ: русские дубляжи вроде
            // «Minions.2015.BDRip-AVC.Dub…new-team» краулер помечает как ["en"],
            // поэтому скоринг дополнительно смотрит маркеры в имени.
            ["lang_ru"] = r.langRu,
            // Совпадение по TMDB id — сильнее любого сравнения имён в СКОРИНГЕ (имена латиницей против
            // русской карточки давали бы nameMiss). Для ДОНОРА этого мало: там голова имени сверяется
            // с карточкой ИЛИ с эталоном из bitmagnet (id_title) — см. NameMatchesSeriesOrId.
            ["id_match"] = true,
            ["id_title"] = r.contentTitle,
            ["id_title_original"] = r.contentOriginal,
            ["files_count"] = r.filesCount,
            ["files_status"] = r.filesStatus,
            ["bm_src"] = r.source,
            ["bm_mod"] = r.modifier
        };
        if (legacy) it["bm_legacy"] = true;
        if (screener) it["bm_screener"] = true;

        // episodes jsonb: {"3":{"10":{}}} — одиночная серия; {"3":{}} — сезон без разбивки (пак);
        // {"1":{"1":{},…,"10":{}}} — мультисерийный. Ошибки парсера бывают («Silo.S02S10…» → {"2":{}}
        // с одним файлом) — паком считаем только сезон без разбивки С files_count >= 2.
        try
        {
            if (!string.IsNullOrWhiteSpace(r.episodesJson) && JToken.Parse(r.episodesJson) is JObject eps && eps.Count > 0)
            {
                string key = null;
                if (scopeSeason > 0 && eps[scopeSeason.ToString()] is JObject) key = scopeSeason.ToString();
                else if (eps.Count == 1) key = eps.Properties().First().Name;

                if (key != null && eps[key] is JObject inner)
                {
                    var list = new List<int>();
                    foreach (var p in inner.Properties())
                        if (int.TryParse(p.Name, out int e) && e > 0) list.Add(e);
                    list.Sort();
                    it["bm_season"] = int.TryParse(key, out int sk) ? sk : 0;
                    it["bm_eps"] = new JArray(list);
                    it["bm_pack"] = list.Count == 0 && r.filesCount >= 2;
                }
                else if (eps.Count > 1)
                {
                    // многосезонный пак: серии внутри сезона считает FindEpFiles после метаданных
                    it["bm_multi"] = true;
                    it["bm_pack"] = r.filesCount >= 2;
                }
            }
        }
        catch { }

        if (r.files != null)
        {
            var fa = new JArray();
            foreach (var f in r.files) fa.Add(new JObject { ["name"] = f.name, ["size"] = f.size });
            it["bm_files"] = fa;
        }
        return it;
    }

    // Обратный ход: раздача (btih) → TMDB id. Вторая попытка после нашего индекса, когда карточку
    // загрузки надо восстановить, а искали её не мы (старые загрузки, ручное добавление в qBit).
    // Привязка тут такая же точная — по info_hash, а не по имени.
    static async Task<(int tmdbId, bool tv)> BitmagnetTmdbByBtih(string btih)
    {
        string conn = ModInit.conf.bitmagnetConnection;
        if (string.IsNullOrWhiteSpace(conn) || string.IsNullOrWhiteSpace(btih))
            return (0, false);

        try
        {
            await using var db = new NpgsqlConnection(conn);
            await db.OpenAsync();

            await using var cmd = new NpgsqlCommand(@"
select tc.content_id, tc.content_type
from torrent_contents tc
where tc.content_source = 'tmdb'
  and tc.info_hash = decode(@h, 'hex')
limit 1", db);
            cmd.CommandTimeout = Math.Max(2, ModInit.conf.bitmagnetTimeoutSec);
            cmd.Parameters.AddWithValue("h", btih.ToLowerInvariant());

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync() || r.IsDBNull(0))
                return (0, false);

            string id = r.GetString(0);
            string ctype = r.IsDBNull(1) ? "" : r.GetString(1);
            return (int.TryParse(id, out int tmdb) ? tmdb : 0, ctype == "tv_show");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QbitDownload] bitmagnet по btih ({ex.GetType().Name}): {ex.Message}");
            return (0, false);
        }
    }

    static int? QualityFromResolution(string res)
    {
        if (string.IsNullOrWhiteSpace(res))
            return null;

        switch (res.Trim().ToUpperInvariant())
        {
            case "V4320P": return 4320;
            case "V2160P": return 2160;
            case "V1440P": return 1440;
            case "V1080P": return 1080;
            case "V720P": return 720;
            case "V576P": return 576;
            case "V540P": return 540;
            case "V480P": return 480;
            case "V360P": return 360;
            default: return null;
        }
    }

    static string NormalizeCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return null;

        string c = codec.Trim().ToLowerInvariant();
        if (c.Contains("265") || c.Contains("hevc")) return "hevc";
        if (c.Contains("av1")) return "av1";
        if (c.Contains("264") || c.Contains("avc")) return "h264";
        return null;
    }

    #region /qdl/search — свёртка иностранных поштучных серий из DHT (qdl 2.107)
    // Решение владельца (2026-09-04): иностранные ПОШТУЧНЫЕ серии из bitmagnet показывать только когда
    // русских раздач в выдаче нет (§CE: ~80 китайских одиночек B.King в списке «Скачать»). Паки не
    // трогаем. Это пост-фильтр на выходе ручки, а НЕ в SortAndMark: тот — общий путь охоты, обходчика
    // и записи в индекс, скрытие там урезало бы ClaimCandidates (английская E10 — доказательство
    // «серия вышла») и torrent_index. Одиночка определяется по episodes (bm_eps), не по files_status:
    // иностранная серия часто упакована multi (видео + nfo/sample). Эхо нашего индекса без bm_* — по имени.
    internal static (JArray list, int hidden) HideForeignSingles(JArray sorted)
    {
        if (sorted == null || sorted.Count == 0) return (sorted, 0);
        bool anyRussian = sorted.OfType<JObject>().Any(t => TorrentScoring.IsRussian(t.Value<string>("title"), t.Value<bool?>("lang_ru")));
        if (!anyRussian) return (sorted, 0);

        var keep = new JArray();
        int hidden = 0;
        foreach (var tok in sorted)
        {
            if (tok is JObject t && IsForeignSingle(t)) { hidden++; continue; }
            keep.Add(tok);
        }
        if (keep.Count == 0) return (sorted, 0);   // предохранитель: выдачу не обнуляем никогда
        return (keep, hidden);
    }

    internal static bool IsForeignSingle(JObject t)
    {
        if (t == null || t.Value<string>("tracker") != "bitmagnet") return false;
        if (TorrentScoring.IsRussian(t.Value<string>("title"), t.Value<bool?>("lang_ru"))) return false;
        if (t.Value<bool?>("bm_pack") == true || t.Value<bool?>("bm_multi") == true) return false;
        if (t["bm_eps"] is JArray eps) return eps.Count == 1;
        var pe = ParseEp(StripSeasonMarks(t.Value<string>("title") ?? ""));
        return pe != null && pe.any && pe.kind == null && pe.ep >= 0;
    }
    #endregion
}
