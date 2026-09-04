using Newtonsoft.Json.Linq;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// Свой индекс раздач в Postgres.
//
// Зачем: чужой удалённый индекс (webApiHost) — единственный путь к rutracker и заметная часть
// выдачи, и он сторонний, по plain HTTP. Режим `red` независимости не даёт: синкается с ТОГО ЖЕ
// хоста и кладёт сотни тысяч мелких файлов в WSL-vhdx, который обратно не сжимается. Свой индекс
// копит то, что видели ВСЕ наши источники (rutor, nnmclub, kinozal, bitmagnet, webapi), в своей
// схеме на F: — и переживает смерть любого из них.
//
// Инварианты, которые нельзя нарушать:
//  1. Недоступность Postgres НИКОГДА не роняет поиск — все методы глушат исключения.
//  2. Запись идёт fire-and-forget: пользователь не ждёт индекс.
//  3. В индекс попадает только то, что ПРОШЛО отсев (мусор и чужие тайтлы отсекаются раньше).
//  4. Живые источники побеждают индекс при дедупе — он добавляет только то, чего никто не отдал.
//
// Базовый тип объявлен в Controller.cs — в partial-классе он указывается один раз.
public partial class QbitController
{
    static int _liReady;              // 0 = схема ещё не создавалась
    static DateTime _liDownUntil;     // кулдаун после сбоя: не долбим мёртвую БД на каждом поиске
    static readonly SemaphoreSlim _liSchemaGate = new SemaphoreSlim(1, 1);

    internal static bool LocalIndexEnabled => !string.IsNullOrWhiteSpace(ModInit.conf?.localIndexConnection);

    static bool LiDown => DateTime.UtcNow < _liDownUntil;

    static void LiFail(string where, Exception ex)
    {
        _liDownUntil = DateTime.UtcNow.AddMinutes(2);
        Console.WriteLine($"[QbitDownload] индекс ({where}): {ex.GetType().Name}: {ex.Message}");
    }

    #region схема
    // Отдельная БД, а не таблица в bitmagnet: переустановка или сброс краулера не должны
    // трогать наши данные. postgres — суперюзер, поэтому БД заводим сами.
    static async Task<bool> EnsureSchema()
    {
        if (Volatile.Read(ref _liReady) == 1) return true;
        if (LiDown) return false;

        await _liSchemaGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _liReady) == 1) return true;

            var csb = new NpgsqlConnectionStringBuilder(ModInit.conf.localIndexConnection);
            string dbName = csb.Database;

            // CREATE DATABASE нельзя выполнить, будучи подключённым к создаваемой БД —
            // и он не поддерживает IF NOT EXISTS, поэтому проверяем через pg_database.
            var admin = new NpgsqlConnectionStringBuilder(ModInit.conf.localIndexConnection) { Database = "postgres" };
            await using (var a = new NpgsqlConnection(admin.ConnectionString))
            {
                await a.OpenAsync();
                await using var chk = new NpgsqlCommand("select 1 from pg_database where datname=@n", a);
                chk.Parameters.AddWithValue("n", dbName);
                if (await chk.ExecuteScalarAsync() == null)
                {
                    await using var cr = new NpgsqlCommand($"create database \"{dbName.Replace("\"", "\"\"")}\"", a);
                    await cr.ExecuteNonQueryAsync();
                    Console.WriteLine($"[QbitDownload] индекс: создана база {dbName}");
                }
            }

            await using (var db = new NpgsqlConnection(ModInit.conf.localIndexConnection))
            {
                await db.OpenAsync();
                await using var cmd = new NpgsqlCommand(@"
create table if not exists torrent_index (
  id           bigserial primary key,
  dedupe_key   text not null unique,
  btih         text,
  title        text not null,
  tracker      text,
  magnet       text,
  parselink    text,
  size_bytes   bigint,
  sid          int,
  pir          int,
  quality      int,
  codec        text,
  published_at timestamptz,
  first_seen   timestamptz not null default now(),
  last_seen    timestamptz not null default now()
);

create table if not exists torrent_title (
  torrent_id bigint not null references torrent_index(id) on delete cascade,
  tmdb_id    int,
  query_norm text,
  year       int,
  is_serial  smallint,
  last_seen  timestamptz not null default now()
);

-- две привязки: точная (по TMDB id) и запасная (по нормализованному названию+году)
create unique index if not exists torrent_title_tmdb_uq on torrent_title (torrent_id, tmdb_id) where tmdb_id is not null;
create unique index if not exists torrent_title_norm_uq on torrent_title (torrent_id, query_norm, year) where query_norm is not null;
create index if not exists torrent_title_lookup_tmdb on torrent_title (tmdb_id) where tmdb_id is not null;
create index if not exists torrent_title_lookup_norm on torrent_title (query_norm, year) where query_norm is not null;
create index if not exists torrent_index_last_seen on torrent_index (last_seen);", db);
                await cmd.ExecuteNonQueryAsync();
            }

            Volatile.Write(ref _liReady, 1);
            return true;
        }
        catch (Exception ex) { LiFail("схема", ex); return false; }
        finally { _liSchemaGate.Release(); }
    }
    #endregion

    #region запись
    // Обёртка «выстрелил и забыл»: поиск не должен ждать индекс ни при каких обстоятельствах.
    internal static void IndexStoreAsync(JArray items, string tmdbId, string titleNorm, int year, int isSerial)
    {
        if (!LocalIndexEnabled || items == null || items.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try { await IndexStore(items, tmdbId, titleNorm, year, isSerial); }
            catch (Exception ex) { LiFail("запись(bg)", ex); }
        });
    }

    // Пишет ТОЛЬКО прошедшие отсев раздачи.
    internal static async Task IndexStore(JArray items, string tmdbId, string titleNorm, int year, int isSerial)
    {
        if (!LocalIndexEnabled || LiDown || items == null || items.Count == 0) return;
        if (!await EnsureSchema()) return;

        int tmdb = 0;
        int.TryParse(tmdbId, out tmdb);
        if (tmdb <= 0 && string.IsNullOrWhiteSpace(titleNorm)) return;   // привязать не к чему

        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();

            int stored = 0;
            foreach (var t in items.OfType<JObject>())
            {
                string magnet = t.Value<string>("magnet");
                string link = t.Value<string>("parselink");
                string btih = MagnetHash(magnet);
                // Ключ дедупа — ТОТ ЖЕ, что в SearchScored: иначе индекс и выдача разошлись бы.
                string key = !string.IsNullOrWhiteSpace(btih) ? btih : link;
                if (string.IsNullOrWhiteSpace(key)) continue;

                await using var cmd = new NpgsqlCommand(@"
with up as (
  insert into torrent_index (dedupe_key, btih, title, tracker, magnet, parselink, size_bytes, sid, pir, quality, codec, published_at)
  values (@k, @b, @t, @tr, @m, @p, @sz, @sid, @pir, @q, @c, @pub)
  on conflict (dedupe_key) do update set
    title = excluded.title, tracker = excluded.tracker,
    magnet = coalesce(excluded.magnet, torrent_index.magnet),
    parselink = coalesce(excluded.parselink, torrent_index.parselink),
    size_bytes = excluded.size_bytes, sid = excluded.sid, pir = excluded.pir,
    quality = excluded.quality, codec = excluded.codec, last_seen = now()
  returning id
)
insert into torrent_title (torrent_id, tmdb_id, query_norm, year, is_serial)
select id, @tmdb, @qn, @yr, @ser from up
on conflict do nothing;", db);

                cmd.Parameters.AddWithValue("k", key);
                cmd.Parameters.AddWithValue("b", (object)(string.IsNullOrWhiteSpace(btih) ? null : btih) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("t", t.Value<string>("title") ?? "");
                cmd.Parameters.AddWithValue("tr", (object)t.Value<string>("tracker") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("m", (object)magnet ?? DBNull.Value);
                cmd.Parameters.AddWithValue("p", (object)link ?? DBNull.Value);
                cmd.Parameters.AddWithValue("sz", t.Value<long?>("sizeBytes") ?? 0L);
                cmd.Parameters.AddWithValue("sid", t.Value<int?>("sid") ?? 0);
                cmd.Parameters.AddWithValue("pir", t.Value<int?>("pir") ?? 0);
                cmd.Parameters.AddWithValue("q", t.Value<int?>("quality") ?? 0);
                cmd.Parameters.AddWithValue("c", (object)t.Value<string>("codec") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pub", (object)ParsePub(t.Value<string>("date")) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("tmdb", tmdb > 0 ? (object)tmdb : DBNull.Value);
                cmd.Parameters.AddWithValue("qn", string.IsNullOrWhiteSpace(titleNorm) ? (object)DBNull.Value : titleNorm);
                cmd.Parameters.AddWithValue("yr", year > 0 ? (object)year : DBNull.Value);
                cmd.Parameters.AddWithValue("ser", (short)isSerial);

                await cmd.ExecuteNonQueryAsync();
                stored++;
            }

            if (stored > 0 && ModInit.conf.localIndexVerbose)
                Console.WriteLine($"[QbitDownload] индекс: сохранено {stored} для tmdb={tmdb} «{titleNorm}»");
        }
        catch (Exception ex) { LiFail("запись", ex); }
    }

    static DateTime? ParsePub(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
            return null;
        // 01/01/0001 приходит от трекеров без даты; timestamptz такое не любит
        return d.Year < 1990 || d.Year > 2100 ? (DateTime?)null : d;
    }
    #endregion

    #region чтение
    // Проход по индексу — рядом с bitmagnet. Никогда не бросает: источник дополнительный.
    internal static async Task<JArray> FetchLocalIndex(string tmdbId, string titleNorm, int year, int isSerial)
    {
        var res = new JArray();
        if (!LocalIndexEnabled || LiDown) return res;
        if (!await EnsureSchema()) return res;

        int tmdb = 0;
        int.TryParse(tmdbId, out tmdb);
        if (tmdb <= 0 && string.IsNullOrWhiteSpace(titleNorm)) return res;

        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();

            // Привязка по tmdb_id — точная; по названию+году — запасная, для карточек без id.
            // ⚠️ limit — снаружи подзапроса и после сортировки по сидам/свежести (qdl 2.107). Раньше
            // он стоял прямо за distinct on, а тот требует order by dedupe_key — то есть срез был
            // по ЛЕКСИКОГРАФИИ btih: у «Укрытия» из 807 ключей уходили 200 младших по hex — случайная
            // четверть DHT-строк и ни одной трекерной (их ключ — http://…).
            await using var cmd = new NpgsqlCommand(@"
select x.title, x.tracker, x.magnet, x.parselink, x.size_bytes, x.sid, x.pir, x.quality, x.codec, x.published_at
from (
  select distinct on (i.dedupe_key)
         i.title, i.tracker, i.magnet, i.parselink, i.size_bytes, i.sid, i.pir, i.quality, i.codec, i.published_at, i.last_seen
  from torrent_index i
  join torrent_title tt on tt.torrent_id = i.id
  where (@tmdb > 0 and tt.tmdb_id = @tmdb)
     or (@tmdb = 0 and @qn <> '' and tt.query_norm = @qn and (@yr = 0 or tt.year = @yr))
  order by i.dedupe_key, i.last_seen desc
) x
order by x.sid desc nulls last, x.last_seen desc
limit @lim;", db);
            cmd.CommandTimeout = Math.Max(2, ModInit.conf.localIndexTimeoutSec);
            cmd.Parameters.AddWithValue("tmdb", tmdb);
            cmd.Parameters.AddWithValue("qn", titleNorm ?? "");
            cmd.Parameters.AddWithValue("yr", year);
            cmd.Parameters.AddWithValue("lim", Math.Max(1, ModInit.conf.localIndexLimit));

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string title = r.IsDBNull(0) ? null : r.GetString(0);
                if (string.IsNullOrWhiteSpace(title)) continue;
                string magnet = r.IsDBNull(2) ? null : r.GetString(2);
                string link = r.IsDBNull(3) ? null : r.GetString(3);
                if (string.IsNullOrWhiteSpace(magnet) && string.IsNullOrWhiteSpace(link)) continue;

                long size = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                res.Add(new JObject
                {
                    ["title"] = title,
                    ["magnet"] = magnet,
                    ["parselink"] = link,
                    ["tracker"] = r.IsDBNull(1) ? "индекс" : r.GetString(1),
                    ["sid"] = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    ["pir"] = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    ["size"] = HumanSize(size),
                    ["sizeBytes"] = size,
                    ["quality"] = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    ["codec"] = r.IsDBNull(8) ? null : r.GetString(8),
                    ["date"] = r.IsDBNull(9) ? null : r.GetDateTime(9).ToString("MM/dd/yyyy HH:mm:ss"),
                    // раздача уже привязана к карточке при записи — сверять имя не нужно
                    ["id_match"] = tmdb > 0
                });
            }
        }
        catch (Exception ex) { LiFail("чтение", ex); }

        return res;
    }

    // Обратный ход индекса: раздача (btih) → карточка, которую по ней искали.
    //
    // Индекс писался «карточка → раздачи», но привязка симметрична, и это единственное место в
    // системе, где по скачанному торренту можно ТОЧНО узнать его TMDB id. Нужно, чтобы у загрузки
    // всегда были карточка и описание: карточку сохраняет клиент сразу после «Скачать», а всё, что
    // добавлено мимо этого пути (login-трекеры до фикса хеша, авто-контуры), чинится по btih отсюда.
    internal static async Task<(int tmdbId, int isSerial, string queryNorm, int year, string magnet, string parselink)> TmdbByBtih(string btih)
    {
        var none = (0, 0, (string)null, 0, (string)null, (string)null);
        if (!LocalIndexEnabled || LiDown || string.IsNullOrWhiteSpace(btih)) return none;
        if (!await EnsureSchema()) return none;

        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();

            await using var cmd = new NpgsqlCommand(@"
select tt.tmdb_id, tt.is_serial, tt.query_norm, tt.year, i.magnet, i.parselink
from torrent_index i
join torrent_title tt on tt.torrent_id = i.id
where lower(i.btih) = @b and tt.tmdb_id is not null
order by tt.last_seen desc
limit 1;", db);
            cmd.CommandTimeout = Math.Max(2, ModInit.conf.localIndexTimeoutSec);
            cmd.Parameters.AddWithValue("b", btih.ToLowerInvariant());

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return none;

            return (
                r.IsDBNull(0) ? 0 : r.GetInt32(0),
                r.IsDBNull(1) ? 0 : r.GetInt16(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? 0 : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5));
        }
        catch (Exception ex) { LiFail("чтение по btih", ex); return none; }
    }
    #endregion

    #region статистика
    internal static async Task<JObject> LocalIndexStats()
    {
        var res = new JObject { ["enabled"] = LocalIndexEnabled, ["down"] = LiDown };
        if (!LocalIndexEnabled || LiDown) return res;
        if (!await EnsureSchema()) { res["error"] = "схема недоступна"; return res; }

        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
select (select count(*) from torrent_index),
       (select count(*) from torrent_title),
       (select count(distinct tmdb_id) from torrent_title where tmdb_id is not null),
       (select max(last_seen) from torrent_index),
       pg_size_pretty(pg_database_size(current_database()))", db);
            cmd.CommandTimeout = 15;
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                res["torrents"] = r.GetInt64(0);
                res["links"] = r.GetInt64(1);
                res["titles"] = r.GetInt64(2);
                res["last_seen"] = r.IsDBNull(3) ? null : r.GetDateTime(3).ToString("o");
                res["size"] = r.GetString(4);
            }
        }
        catch (Exception ex) { res["error"] = ex.Message; }
        return res;
    }
    #endregion

    #region ретенция
    internal static async Task IndexPrune()
    {
        if (!LocalIndexEnabled || LiDown) return;
        int days = ModInit.conf.localIndexPruneDays;
        if (days <= 0) return;
        if (!await EnsureSchema()) return;

        try
        {
            await using var db = new NpgsqlConnection(ModInit.conf.localIndexConnection);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("delete from torrent_index where last_seen < now() - make_interval(days => @d)", db);
            cmd.CommandTimeout = 120;
            cmd.Parameters.AddWithValue("d", days);
            int n = await cmd.ExecuteNonQueryAsync();
            if (n > 0) Console.WriteLine($"[QbitDownload] индекс: вычищено {n} раздач старше {days} дн");
        }
        catch (Exception ex) { LiFail("ретенция", ex); }
    }
    #endregion
}
