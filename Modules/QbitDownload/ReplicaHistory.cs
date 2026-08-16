using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace QbitDownload;

// ── История просмотров дом → реплика (односторонне) ─────────────────────────────
//
// Клиент, переехавший на реплику, обязан увидеть свои «Продолжить», а не чистый интерфейс.
// Переносим три хранилища модулей Sync (они лежат в томе database):
//
//   database/TimeCode.sql        timecodes(user, card, item, data, updated)  — позиции просмотра
//   database/Sync.sql            bookmarks(user, data, updated)              — закладки
//   database/storage/<b>/<md5>   блобы localStorage клиента (online_view, torrents_view, фильтры)
//
// ⚠️ Почему не через API этих модулей: модули компилируются Roslyn'ом в ОТДЕЛЬНЫЕ сборки и не
// видят друг друга. Поэтому читаем и пишем сами — но строго теми же путями и той же схемой,
// без EnsureCreated: таблицу создаёт владелец схемы, мы в неё только пишем. Нет таблицы —
// значит модуль ещё не поднялся, пропускаем тик.
//
// 🔴 ТРИ ИНВАРИАНТА:
//
//  1. Поток ОДНОСТОРОННИЙ. У дома нет ни одной ручки на запись — физически некуда отправить.
//     Что посмотрели на реплике, домой не уедет; это решение владельца, а не недоделка.
//  2. Побеждает БОЛЕЕ СВЕЖАЯ запись, а не «последний синк». Иначе домашняя копия затирала бы
//     позицию, отмотанную только что здесь: человек досмотрел серию на реплике, через пять
//     минут пришёл тик — и кино откатилось назад.
//  3. Путь блоба ПРИХОДИТ ПО СЕТИ, значит недоверенный. Всё пишется только внутрь
//     database/storage через ConfinedCombine: «../../init.conf» в rel не должен никуда доехать.
//
// Объёмы игрушечные (десятки строк, десятки файлов по ~100 байт), поэтому курсора нет:
// отдаём всё, применяем идемпотентно. Так не бывает рассинхрона после пропущенного тика.

public partial class QbitController
{
    internal const int ReplicaHistoryVersion = 1;

    static string DbDir => "database";
    static string TimeCodeDbPath => Path.Combine(DbDir, "TimeCode.sql");
    static string SyncDbPath => Path.Combine(DbDir, "Sync.sql");
    static string StorageDir => Path.Combine(DbDir, "storage");

    // Файл блоба больше этого не переносим: syncview весит ~100 байт, а /storage/set принимает
    // до 10 МиБ — прилетевший «большой» файл означает что-то другое, и в историю он не входит.
    const long HistoryMaxBlobBytes = 256 * 1024;
    const long HistoryMaxTotalBytes = 4 * 1024 * 1024;

    static SqliteConnection OpenDb(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10,
            Pooling = true
        }.ToString());
        c.Open();
        return c;
    }

    static bool TableExists(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "select count(*) from sqlite_master where type='table' and name=$t";
        cmd.Parameters.AddWithValue("$t", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// «Удалённая запись свежее локальной?» Строки EF-времени ('2026-08-16 16:36:16.1795326')
    /// сравнимы и лексикографически, но полагаться на это нельзя: у записи, пришедшей из другой
    /// культуры или формата, порядок байт соврёт. Поэтому сначала разбор, и только потом фолбэк.
    /// Локальной записи нет → свежее по определению.
    /// </summary>
    internal static bool HistoryNewer(string remote, string local)
    {
        if (string.IsNullOrEmpty(remote)) return false;
        if (string.IsNullOrEmpty(local)) return true;

        if (DateTime.TryParse(remote, CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) &&
            DateTime.TryParse(local, CultureInfo.InvariantCulture, DateTimeStyles.None, out var l))
            return r > l;

        return string.CompareOrdinal(remote, local) > 0;
    }

    #region роль main: отдать историю

    /// <summary>
    /// Снимок истории просмотров для реплики. Только чтение, только через мост.
    /// </summary>
    [Microsoft.AspNetCore.Mvc.HttpGet, Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [Microsoft.AspNetCore.Mvc.Route("qdl/replica/history")]
    public Microsoft.AspNetCore.Mvc.ActionResult ReplicaHistory()
    {
        var deny = ReplicaBridgeDeny(); if (deny != null) return deny;

        var timecodes = new JArray();
        var bookmarks = new JArray();
        var storage = new JArray();
        int skippedBig = 0;
        bool capped = false;

        try
        {
            if (System.IO.File.Exists(TimeCodeDbPath))
            {
                using var db = OpenDb(TimeCodeDbPath);
                if (TableExists(db, "timecodes"))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "select user, card, item, data, updated from timecodes limit 20000";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        timecodes.Add(new JObject
                        {
                            ["user"] = r.GetString(0),
                            ["card"] = r.GetString(1),
                            ["item"] = r.GetString(2),
                            ["data"] = r.IsDBNull(3) ? null : r.GetString(3),
                            ["updated"] = r.IsDBNull(4) ? null : r.GetValue(4)?.ToString()
                        });
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica history timecodes: " + ex.Message); }

        try
        {
            if (System.IO.File.Exists(SyncDbPath))
            {
                using var db = OpenDb(SyncDbPath);
                if (TableExists(db, "bookmarks"))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "select user, data, updated from bookmarks limit 20000";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        bookmarks.Add(new JObject
                        {
                            ["user"] = r.GetString(0),
                            ["data"] = r.IsDBNull(1) ? null : r.GetString(1),
                            ["updated"] = r.IsDBNull(2) ? null : r.GetValue(2)?.ToString()
                        });
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica history bookmarks: " + ex.Message); }

        try
        {
            if (Directory.Exists(StorageDir))
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(StorageDir, "*", SearchOption.AllDirectories))
                {
                    // temp — обменный буфер /storage/temp, живёт минутами и переносу не подлежит
                    string rel = Path.GetRelativePath(StorageDir, f).Replace('\\', '/');
                    if (rel.StartsWith("temp/", StringComparison.OrdinalIgnoreCase)) continue;

                    var fi = new FileInfo(f);
                    if (fi.Length > HistoryMaxBlobBytes) { skippedBig++; continue; }
                    if (total + fi.Length > HistoryMaxTotalBytes) { capped = true; break; }
                    total += fi.Length;

                    storage.Add(new JObject
                    {
                        ["rel"] = rel,
                        ["mtime"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                        ["data"] = System.IO.File.ReadAllText(f)
                    });
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica history storage: " + ex.Message); }

        // Обрезали набор — говорим об этом вслух: молчаливая неполнота выглядела бы как
        // «часть устройств не синхронизируется», и искали бы её в сети, а не здесь.
        if (skippedBig > 0 || capped)
            Console.WriteLine($"[QbitDownload] replica history: пропущено крупных блобов {skippedBig}{(capped ? ", набор обрезан по общему капу" : "")}");

        var j = new JObject
        {
            ["historyVersion"] = ReplicaHistoryVersion,
            ["timecodes"] = timecodes,
            ["bookmarks"] = bookmarks,
            ["storage"] = storage,
            ["skippedBig"] = skippedBig,
            ["capped"] = capped
        };

        return ContentTo(j.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    #endregion

    #region роль replica: применить историю

    /// <summary>
    /// Тянет историю с дома и применяет у себя. Возвращает «что изменилось» для лога.
    /// </summary>
    static async Task<string> ReplicaPullHistory(string main)
    {
        if (ModInit.conf?.replicaHistory == false) return null;

        JObject j;
        try
        {
            string raw = await _replicaHttp.GetStringAsync(main + "/qdl/replica/history");
            j = JObject.Parse(raw);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica history: не получена: " + ex.Message);
            return null;
        }

        int ver = j.Value<int?>("historyVersion") ?? 0;
        if (ver != ReplicaHistoryVersion)
        {
            Console.WriteLine($"[QbitDownload] replica history: версия {ver} != {ReplicaHistoryVersion}, пропуск");
            return null;
        }

        int tc = ApplyTimecodes(j["timecodes"] as JArray);
        int bm = ApplyBookmarks(j["bookmarks"] as JArray);
        int st = ApplyStorage(j["storage"] as JArray);

        if (tc == 0 && bm == 0 && st == 0) return null;
        return $"история: таймкодов {tc}, закладок {bm}, блобов {st}";
    }

    static int ApplyTimecodes(JArray rows)
    {
        if (rows == null || rows.Count == 0) return 0;
        if (!System.IO.File.Exists(TimeCodeDbPath)) return 0;

        int n = 0;
        try
        {
            using var db = OpenDb(TimeCodeDbPath);
            if (!TableExists(db, "timecodes")) return 0;

            using var tx = db.BeginTransaction();

            foreach (var row in rows)
            {
                string user = row.Value<string>("user"), card = row.Value<string>("card"), item = row.Value<string>("item");
                if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(card) || string.IsNullOrEmpty(item)) continue;

                string upd = row.Value<string>("updated");

                string local = null;
                using (var sel = db.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "select updated from timecodes where user=$u and card=$c and item=$i limit 1";
                    sel.Parameters.AddWithValue("$u", user);
                    sel.Parameters.AddWithValue("$c", card);
                    sel.Parameters.AddWithValue("$i", item);
                    local = sel.ExecuteScalar()?.ToString();
                }

                // 🔴 Инвариант 2: не затираем более свежую местную позицию домашней старой
                if (local != null && !HistoryNewer(upd, local)) continue;

                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = local == null
                    ? "insert into timecodes(user, card, item, data, updated) values($u,$c,$i,$d,$up)"
                    : "update timecodes set data=$d, updated=$up where user=$u and card=$c and item=$i";
                cmd.Parameters.AddWithValue("$u", user);
                cmd.Parameters.AddWithValue("$c", card);
                cmd.Parameters.AddWithValue("$i", item);
                cmd.Parameters.AddWithValue("$d", (object)row.Value<string>("data") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$up", (object)upd ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                n++;
            }

            tx.Commit();
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica history timecodes apply: " + ex.Message); }

        return n;
    }

    static int ApplyBookmarks(JArray rows)
    {
        if (rows == null || rows.Count == 0) return 0;
        if (!System.IO.File.Exists(SyncDbPath)) return 0;

        int n = 0;
        try
        {
            using var db = OpenDb(SyncDbPath);
            if (!TableExists(db, "bookmarks")) return 0;

            using var tx = db.BeginTransaction();

            foreach (var row in rows)
            {
                string user = row.Value<string>("user");
                if (string.IsNullOrEmpty(user)) continue;
                string upd = row.Value<string>("updated");

                string local = null;
                using (var sel = db.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "select updated from bookmarks where user=$u limit 1";
                    sel.Parameters.AddWithValue("$u", user);
                    local = sel.ExecuteScalar()?.ToString();
                }

                if (local != null && !HistoryNewer(upd, local)) continue;

                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = local == null
                    ? "insert into bookmarks(user, data, updated) values($u,$d,$up)"
                    : "update bookmarks set data=$d, updated=$up where user=$u";
                cmd.Parameters.AddWithValue("$u", user);
                cmd.Parameters.AddWithValue("$d", (object)row.Value<string>("data") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$up", (object)upd ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                n++;
            }

            tx.Commit();
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica history bookmarks apply: " + ex.Message); }

        return n;
    }

    /// <summary>
    /// Куда лечь блобу. Отдельной функцией — потому что это единственная защита от того, что
    /// «дом» (или тот, кто им притворился в туннеле) пришлёт rel вида ../../init.conf.
    /// null = путь отвергнут.
    /// </summary>
    internal static string HistoryBlobPath(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return null;

        string norm = rel.Replace('\\', '/');

        // ⚠️ ConfinedCombine сегменты «..» ВЫЧИЩАЕТ, то есть наружу не выпустит, но и промолчит:
        // «../../init.conf» тихо стал бы storage/init.conf. Легальный rel таких сегментов не
        // содержит вовсе, поэтому здесь именно ОТКАЗ — он попадёт в лог и будет виден.
        foreach (var seg in norm.Split('/'))
            if (seg == "..") return null;

        return ConfinedCombine(StorageDir, norm);   // второй пояс: за пределы каталога не выйдем
    }

    static int ApplyStorage(JArray rows)
    {
        if (rows == null || rows.Count == 0) return 0;

        int n = 0;
        foreach (var row in rows)
        {
            try
            {
                string rel = row.Value<string>("rel");
                string data = row.Value<string>("data");
                if (string.IsNullOrEmpty(rel) || data == null) continue;

                // 🔴 Инвариант 3: rel пришёл по сети. Только внутрь database/storage.
                string dst = HistoryBlobPath(rel);
                if (dst == null)
                {
                    Console.WriteLine("[QbitDownload] replica history: отвергнут путь блоба «" + rel + "»");
                    continue;
                }

                long mtime = row.Value<long?>("mtime") ?? 0;
                if (System.IO.File.Exists(dst))
                {
                    long localMs = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(dst)).ToUnixTimeMilliseconds();
                    if (mtime <= localMs) continue;   // местная копия свежее — не трогаем
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                string tmp = dst + ".part";
                System.IO.File.WriteAllText(tmp, data);
                if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst);
                System.IO.File.Move(tmp, dst);

                // время правки переносим вместе с содержимым: иначе следующий тик увидит
                // «местная копия свежее» и перестанет обновлять её вообще
                if (mtime > 0)
                    System.IO.File.SetLastWriteTimeUtc(dst, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime);

                n++;
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] replica history blob: " + ex.Message); }
        }

        return n;
    }

    #endregion
}
