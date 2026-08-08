using Newtonsoft.Json.Linq;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// Источник раздач из локального индекса DHT-краулера bitmagnet (Postgres на хосте).
//
// Зачем: живых русских трекеров осталось мало, rutracker закрыт Cloudflare по TLS-отпечатку
// (не чинится ни кукой, ни прокси, ни headless-браузером — проверено). А рядом уже лежит
// ~14.8 млн раздач, из них ~875 тыс. привязаны к TMDB.
//
// Почему без мусора: ищем СТРОГО по TMDB id карточки, а не по названию. Совпадение точное,
// поэтому «Миньон 1998» вместо «Миньоны 2015» или саундтрек вместо фильма приехать не могут
// в принципе. Свободный текстовый поиск по базе сознательно НЕ делаем: там 11.7 млн раздач
// вообще без метаданных и 625 тыс. xxx — вот это и был бы мусор.
//
// Ранжирование не трогаем: раздачи просто доливаются в общий котёл, а порядок и ⭐ по-прежнему
// определяет TorrentScoring.
// Базовый тип объявлен в Controller.cs — в partial-классе он указывается один раз.
public partial class QbitController
{
    // Раздачи одного тайтла по TMDB id. Пустой список = нечего добавить (это не ошибка).
    static async Task<JArray> FetchBitmagnet(string tmdbId, int is_serial)
    {
        var result = new JArray();

        string conn = ModInit.conf.bitmagnetConnection;
        if (string.IsNullOrWhiteSpace(conn) || string.IsNullOrWhiteSpace(tmdbId))
            return result;

        // content_type сверяем с типом карточки: у TMDB id фильма и сериала свои пространства,
        // и без этого id 1399 мог бы притащить и фильм, и сериал разом.
        string wantType = is_serial >= 2 ? "tv_show" : "movie";

        const string sql = @"
select t.name,
       encode(t.info_hash, 'hex')      as btih,
       coalesce(tc.size, t.size)       as size,
       tc.seeders,
       tc.leechers,
       tc.video_resolution,
       tc.video_codec,
       tc.published_at,
       tc.updated_at,
       -- @> вместо оператора ? : знак вопроса Npgsql может принять за плейсхолдер параметра
       (tc.languages @> '[""ru""]'::jsonb) as lang_ru
from torrent_contents tc
join torrents t on t.info_hash = tc.info_hash
where tc.content_source = 'tmdb'
  and tc.content_id = @id
  and tc.content_type = @ctype
  and coalesce(t.private, false) = false
order by tc.seeders desc nulls last
limit @lim";

        try
        {
            await using var db = new NpgsqlConnection(conn);
            await db.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, db);
            cmd.CommandTimeout = Math.Max(2, ModInit.conf.bitmagnetTimeoutSec);
            cmd.Parameters.AddWithValue("id", tmdbId);
            cmd.Parameters.AddWithValue("ctype", wantType);
            cmd.Parameters.AddWithValue("lim", Math.Max(1, ModInit.conf.bitmagnetLimit));

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string name = r.IsDBNull(0) ? null : r.GetString(0);
                string btih = r.IsDBNull(1) ? null : r.GetString(1);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(btih))
                    continue;

                long size = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                int sid = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                int pir = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                string res = r.IsDBNull(5) ? null : r.GetString(5);
                string vcodec = r.IsDBNull(6) ? null : r.GetString(6);
                DateTime? published = r.IsDBNull(7) ? (DateTime?)null : r.GetDateTime(7);
                DateTime? updated = r.IsDBNull(8) ? (DateTime?)null : r.GetDateTime(8);
                bool langRu = !r.IsDBNull(9) && r.GetBoolean(9);

                // Сиды обновляются только пока краулер работает. Обнулять их нельзя — тогда
                // источник не участвует в ранжировании вовсе; но и верить архивным цифрам
                // наравне с живыми трекерными тоже нельзя. Компромисс — половина веса.
                int staleDays = ModInit.conf.bitmagnetStaleSeedsDays;
                if (staleDays > 0 && (updated == null || updated.Value < DateTime.Now.AddDays(-staleDays)))
                {
                    sid /= 2;
                    pir /= 2;
                }

                result.Add(new JObject
                {
                    ["title"] = name,
                    // Трекера нет — качаем по DHT, поэтому ни логина, ни parselink не нужно.
                    ["magnet"] = $"magnet:?xt=urn:btih:{btih}&dn={HttpUtility.UrlEncode(name)}",
                    ["parselink"] = null,
                    ["tracker"] = "bitmagnet",
                    ["sid"] = sid,
                    ["pir"] = pir,
                    ["size"] = HumanSize(size),
                    ["sizeBytes"] = size,
                    // video_resolution у bitmagnet вида V2160p/V1080p; если пусто — падаем на разбор имени
                    ["quality"] = QualityFromResolution(res) ?? QualityFromTitle(name),
                    ["codec"] = NormalizeCodec(vcodec) ?? CodecFromTitle(name),
                    ["date"] = published?.ToString("MM/dd/yyyy HH:mm:ss"),
                    // Подсказка языка из БД. Она НЕПОЛНАЯ: русские дубляжи вроде
                    // «Minions.2015.BDRip-AVC.Dub…new-team» краулер помечает как ["en"],
                    // поэтому скоринг дополнительно смотрит маркеры в имени.
                    ["lang_ru"] = langRu,
                    // Совпадение по TMDB id — сильнее любого сравнения имён: скоринг не должен
                    // проверять имя (у bitmagnet они латиницей, «Dune.2021…» против карточки
                    // «Дюна» дал бы nameMiss и раздача вылетела бы фильтром «чужой тайтл»).
                    ["id_match"] = true
                });
            }
        }
        catch (Exception ex)
        {
            // Источник дополнительный: его недоступность не должна ломать поиск по трекерам.
            Console.WriteLine($"[QbitDownload] bitmagnet недоступен ({ex.GetType().Name}): {ex.Message}");
            return new JArray();
        }

        return result;
    }

    static int? QualityFromResolution(string res)
    {
        if (string.IsNullOrWhiteSpace(res))
            return null;

        switch (res.Trim().ToUpperInvariant())
        {
            case "V2160P": return 2160;
            case "V1440P": return 1440;
            case "V1080P": return 1080;
            case "V720P": return 720;
            case "V576P": return 576;
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
}
