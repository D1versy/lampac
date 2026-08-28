using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// ПЕРЕНОС И СЛИЯНИЕ ИСТОРИИ ПРИ СВЯЗЫВАНИИ/РАЗВЯЗЫВАНИИ УСТРОЙСТВ (qdl 2.81).
//
// Отдельный файл от Groups.cs по той же причине, по какой TestSandbox отделён от Perms:
// здесь код ПИШЕТ в чужие хранилища, и правила у него свои.
//
// ⚠️ Почему лезем в чужие базы напрямую: модули компилируются Roslyn'ом в ОТДЕЛЬНЫЕ сборки и
// API друг друга не видят. Схема таблиц и пути — те же, что уже используют ReplicaHistory.cs
// (там же подробное обоснование) и TestSandbox.cs.
//
// 🔴 ИНВАРИАНТЫ:
//
//  1. ТОЛЬКО СЛИЯНИЕ, НИКОГДА НЕ ЗАМЕНА И НИКОГДА НЕ УДАЛЕНИЕ. Ни одна операция группы не
//     стирает ни строки: связали — данные устройства ДОБАВИЛИСЬ в группу, а его собственная
//     строка осталась лежать нетронутой (она просто перестала читаться). Развязали — общая
//     история ДОБАВИЛАСЬ в личную. Поэтому любую связку можно откатить без потерь.
//  2. ИДЕМПОТЕНТНОСТЬ. Повторный перенос не плодит дублей и не двигает порядок: закладки
//     сливаются по id (HistoryMergeBookmarks), таймкоды — по паре (card, item).
//  3. ТОЛЬКО ДОМ. На роли replica операция запрещена: она поставила бы строке updated=now, и
//     домашняя копия навсегда стала бы «старее» (ReplicaHistory.ApplyBookmarks сравнивает
//     именно время) — то есть история дома перестала бы доезжать сюда вообще. Та же причина,
//     по которой закрыт HistoryBackfill.
//  4. СУХОЙ ПРОГОН. apply=false считает и не пишет ничего — админка показывает счётчики до
//     того, как владелец нажмёт «Связать».
//
// При слиянии ТАЙМКОДОВ побеждает бо́льший percent (тай-брейк — более свежая запись). Это не
// то же правило, что на живой записи позиции («последний победил»), и так задумано: при
// объединении двух историй никто не должен отнимать у другого досмотренное.
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region замок обнуления (зовётся из хука Groups)

    /// <summary>
    /// Есть ли у строки таймкода настоящий прогресс (time &gt; 0). Один точечный SELECT по
    /// уникальному индексу (user, card, item) — см. TimeCode/SqlContext.cs.
    /// Нет базы, нет таблицы, не разобрали — false, то есть «пусть пишет контроллер».
    /// </summary>
    internal static bool TimecodeHasProgress(string user, string card, string item)
    {
        try
        {
            if (!System.IO.File.Exists(TimeCodeDbPath)) return false;

            using var db = OpenDb(TimeCodeDbPath);
            if (!TableExists(db, "timecodes")) return false;

            using var cmd = db.CreateCommand();
            cmd.CommandText = "select data from timecodes where user=$u and card=$c and item=$i limit 1";
            cmd.Parameters.AddWithValue("$u", user);
            cmd.Parameters.AddWithValue("$c", card);
            cmd.Parameters.AddWithValue("$i", item);

            string data = cmd.ExecuteScalar()?.ToString();
            if (string.IsNullOrEmpty(data)) return false;

            return RoadTime(data) > 0;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups zeroguard sql: " + ex.Message); return false; }
    }

    static double RoadTime(string data)
    {
        try { return JObject.Parse(data).Value<double?>("time") ?? 0; }
        catch { return 0; }
    }

    static double RoadPercent(string data)
    {
        try { return JObject.Parse(data).Value<double?>("percent") ?? 0; }
        catch { return 0; }
    }

    #endregion

    #region перенос истории

    /// <summary>
    /// Слить историю пользователя <paramref name="from"/> в пользователя <paramref name="to"/>.
    /// Направление задаёт вызывающий: устройство → группа (связывание) или группа → устройство
    /// (разрыв с копией). Счётчики уходят в report, короткая сводка — в возврат (для лога).
    /// </summary>
    internal static string GroupsMergeHistory(string from, string to, bool apply, JObject report)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            report["error"] = "нечего переносить: одинаковые айди";
            return "";
        }

        // 🔴 Инвариант 3
        if (ReplicaMode)
        {
            report["error"] = "роль replica: группы настраиваются только на домашнем сервере";
            return "";
        }

        int bookmarks = 0, timecodes = 0, jut = 0;
        var errors = report["errors"] as JArray;
        if (errors == null) { errors = new JArray(); report["errors"] = errors; }

        try { bookmarks = GroupsMergeBookmarks(from, to, apply); }
        catch (Exception ex) { errors.Add("закладки: " + ex.Message); }

        try { timecodes = GroupsMergeTimecodes(from, to, apply); }
        catch (Exception ex) { errors.Add("таймкоды: " + ex.Message); }

        try { jut = GroupsMergeJut(from, to, apply); }
        catch (Exception ex) { errors.Add("jut: " + ex.Message); }

        report["bookmarks"] = bookmarks;
        report["timecodes"] = timecodes;
        report["jut"] = jut;

        if (apply) JsonStore.Flush();   // groups.json и бакеты jut — синхронно, админка читает сразу

        return $"закладок {bookmarks}, позиций {timecodes}, jut {jut}";
    }

    #endregion

    #region закладки (Sync.sql)

    /// <summary>
    /// Строка закладок целиком: «История просмотров» + карточки + Нравится/Позже/Закладки/Брошено.
    /// Решение владельца — группа это «один зритель», делить строку по категориям не стали.
    /// Слияние — готовым HistoryMergeBookmarks (ReplicaHistory.cs): дедуп по id и по карточкам,
    /// порядок ведёт источник, хвосты приёмника дописываются следом.
    /// Возврат: 1 — строка приёмника изменилась, 0 — переносить было нечего.
    /// </summary>
    static int GroupsMergeBookmarks(string from, string to, bool apply)
    {
        if (!System.IO.File.Exists(SyncDbPath)) return 0;

        using var db = OpenDb(SyncDbPath);
        if (!TableExists(db, "bookmarks")) return 0;

        string src = GroupsReadBookmarks(db, from);
        if (string.IsNullOrWhiteSpace(src)) return 0;

        string dst = GroupsReadBookmarks(db, to);
        string merged = HistoryMergeBookmarks(dst, src);

        if (string.IsNullOrWhiteSpace(merged)) return 0;
        if (dst != null && string.Equals(dst, merged, StringComparison.Ordinal)) return 0;   // идемпотентность
        if (!apply) return 1;

        using var cmd = db.CreateCommand();
        cmd.CommandText = dst == null
            ? "insert into bookmarks(user, data, updated) values($u,$d,$up)"
            : "update bookmarks set data=$d, updated=$up where user=$u";
        cmd.Parameters.AddWithValue("$u", to);
        cmd.Parameters.AddWithValue("$d", merged);
        cmd.Parameters.AddWithValue("$up", DateTime.UtcNow);
        cmd.ExecuteNonQuery();

        Console.WriteLine("[QbitDownload] groups bookmarks: " + from + " → " + to);
        return 1;
    }

    static string GroupsReadBookmarks(SqliteConnection db, string user)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "select data from bookmarks where user=$u limit 1";
        cmd.Parameters.AddWithValue("$u", user);
        return cmd.ExecuteScalar()?.ToString();
    }

    #endregion

    #region таймкоды (TimeCode.sql)

    /// <summary>
    /// Позиции просмотра по паре (card, item). Побеждает бо́льший percent, при равенстве —
    /// более свежая запись. Возврат — сколько строк приёмника добавилось/обновилось.
    /// </summary>
    static int GroupsMergeTimecodes(string from, string to, bool apply)
    {
        if (!System.IO.File.Exists(TimeCodeDbPath)) return 0;

        using var db = OpenDb(TimeCodeDbPath);
        if (!TableExists(db, "timecodes")) return 0;

        var rows = new List<(string card, string item, string data, string updated)>();
        using (var sel = db.CreateCommand())
        {
            sel.CommandText = "select card, item, data, updated from timecodes where user=$u";
            sel.Parameters.AddWithValue("$u", from);
            using var r = sel.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetValue(3)?.ToString()));
        }

        if (rows.Count == 0) return 0;

        int n = 0;
        using var tx = apply ? db.BeginTransaction() : null;

        foreach (var row in rows)
        {
            string dstData = null, dstUpdated = null;
            using (var sel = db.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "select data, updated from timecodes where user=$u and card=$c and item=$i limit 1";
                sel.Parameters.AddWithValue("$u", to);
                sel.Parameters.AddWithValue("$c", row.card);
                sel.Parameters.AddWithValue("$i", row.item);
                using var r = sel.ExecuteReader();
                if (r.Read())
                {
                    dstData = r.IsDBNull(0) ? null : r.GetString(0);
                    dstUpdated = r.IsDBNull(1) ? null : r.GetValue(1)?.ToString();
                }
            }

            bool exists = dstData != null || dstUpdated != null;
            if (exists && !GroupsTimecodeWins(row.data, row.updated, dstData, dstUpdated)) continue;

            n++;
            if (!apply) continue;

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = exists
                ? "update timecodes set data=$d, updated=$up where user=$u and card=$c and item=$i"
                : "insert into timecodes(user, card, item, data, updated) values($u,$c,$i,$d,$up)";
            cmd.Parameters.AddWithValue("$u", to);
            cmd.Parameters.AddWithValue("$c", row.card);
            cmd.Parameters.AddWithValue("$i", row.item);
            cmd.Parameters.AddWithValue("$d", (object)row.data ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$up", (object)row.updated ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        tx?.Commit();

        if (apply && n > 0) Console.WriteLine("[QbitDownload] groups timecodes: " + from + " → " + to + " = " + n);
        return n;
    }

    /// <summary>Побеждает бо́льший percent; равный percent — более свежая запись.</summary>
    internal static bool GroupsTimecodeWins(string srcData, string srcUpdated, string dstData, string dstUpdated)
    {
        double sp = RoadPercent(srcData), dp = RoadPercent(dstData);
        if (sp > dp) return true;
        if (sp < dp) return false;

        return HistoryNewer(srcUpdated, dstUpdated);
    }

    #endregion

    #region лента jut.su (файл-бакет на пользователя)

    /// <summary>
    /// Секции watched/searched бакета: при совпадении слага остаётся более свежий «at» и
    /// бо́льший счётчик просмотров. Кап и вытеснение — штатные (JutHistoryPrune).
    /// Возврат — сколько слагов добавилось/обновилось.
    /// </summary>
    static int GroupsMergeJut(string from, string to, bool apply)
    {
        string src = JutHistoryBucket(from), dst = JutHistoryBucket(to);
        if (src == dst || src == JutSharedBucket || dst == JutSharedBucket) return 0;

        lock (_jutHistLock)
        {
            if (!JsonStore.Exists(JutHistoryPath(src))) return 0;

            var a = JutHistoryRead(src);
            var b = JutHistoryRead(dst);

            int n = GroupsMergeJutSection(a["watched"] as JObject, b["watched"] as JObject)
                  + GroupsMergeJutSection(a["searched"] as JObject, b["searched"] as JObject);

            if (n == 0 || !apply) return n;

            JutHistoryPrune((JObject)b["watched"]);
            JutHistoryPrune((JObject)b["searched"]);
            JutHistoryWrite(dst, b);

            Console.WriteLine("[QbitDownload] groups jut: " + src + " → " + dst + " = " + n);
            return n;
        }
    }

    /// <summary>⚠️ Мутирует dst — в сухом прогоне результат просто не записывается на диск.</summary>
    static int GroupsMergeJutSection(JObject src, JObject dst)
    {
        if (src == null || dst == null) return 0;

        int n = 0;
        foreach (var p in src.Properties())
        {
            var sv = p.Value as JObject;
            if (sv == null) continue;

            var dv = dst[p.Name] as JObject;
            if (dv == null) { dst[p.Name] = sv.DeepClone(); n++; continue; }

            DateTime sa = sv["at"]?.Value<DateTime?>() ?? DateTime.MinValue;
            DateTime da = dv["at"]?.Value<DateTime?>() ?? DateTime.MinValue;
            int sc = sv["count"]?.Value<int?>() ?? 0;
            int dc = dv["count"]?.Value<int?>() ?? 0;

            if (sa <= da && sc <= dc) continue;   // ничего нового

            var merged = new JObject { ["at"] = sa > da ? sa : da };
            if (sc > 0 || dc > 0) merged["count"] = Math.Max(sc, dc);
            dst[p.Name] = merged;
            n++;
        }

        return n;
    }

    #endregion

    #region счётчики для админки

    /// <summary>Сколько всего накоплено под этим айди: тайтлов в истории, позиций, аниме jut.su.</summary>
    internal static JObject GroupsStats(string user)
    {
        var o = new JObject { ["history"] = 0, ["timecodes"] = 0, ["jut"] = 0 };
        if (string.IsNullOrEmpty(user)) return o;

        try
        {
            if (System.IO.File.Exists(SyncDbPath))
            {
                using var db = OpenDb(SyncDbPath);
                if (TableExists(db, "bookmarks"))
                {
                    string data = GroupsReadBookmarks(db, user);
                    if (!string.IsNullOrWhiteSpace(data))
                        o["history"] = (JObject.Parse(data)["history"] as JArray)?.Count ?? 0;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups stats bookmarks: " + ex.Message); }

        try
        {
            if (System.IO.File.Exists(TimeCodeDbPath))
            {
                using var db = OpenDb(TimeCodeDbPath);
                if (TableExists(db, "timecodes"))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "select count(*) from timecodes where user=$u";
                    cmd.Parameters.AddWithValue("$u", user);
                    o["timecodes"] = Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups stats timecodes: " + ex.Message); }

        try
        {
            string bucket = JutHistoryBucket(user);
            if (bucket != JutSharedBucket && JsonStore.Exists(JutHistoryPath(bucket)))
            {
                lock (_jutHistLock)
                    o["jut"] = (JutHistoryRead(bucket)["watched"] as JObject)?.Count ?? 0;
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups stats jut: " + ex.Message); }

        return o;
    }

    #endregion

    #region уборка после расформирования — КРАСНАЯ ЛИНИЯ

    // 🔴 Единственное место во всей фиче, где что-то УДАЛЯЕТСЯ. Без него каждое
    // «создал группу — расформировал» оставляло бы на диске полную копию слитой истории
    // навсегда, и она же уезжала бы на реплику. Правила жёсткие:
    //
    //   1. Только ключ с префиксом g- (айди группы). Айди устройства сюда не проходит физически.
    //   2. Только при расформировании и только если КАЖДЫЙ участник успел получить копию
    //      (keepCopy + ни одной ошибки переноса). Иначе это была бы единственная копия данных.
    //   3. Группы в реестре уже нет — значит на этот ключ никто больше не резолвится.
    //
    // Расформирование БЕЗ копии не убирает ничего: админка обещает владельцу, что накопленное
    // в группе с диска не пропадёт, и обещание должно быть правдой.

    /// <summary>Убрать данные расформированной группы. Возврат — сводка для лога.</summary>
    internal static string GroupsPurge(string gid)
    {
        if (string.IsNullOrEmpty(gid) || !gid.StartsWith(Groups.GidPrefix, StringComparison.Ordinal))
            return null;                                   // 🔴 замок 1: только айди группы
        if (ReplicaMode) return null;
        if (Groups.Exists(gid)) return null;               // 🔴 замок 3: группа ещё жива

        int bm = 0, tc = 0, jt = 0;

        try
        {
            if (System.IO.File.Exists(SyncDbPath))
            {
                using var db = OpenDb(SyncDbPath);
                if (TableExists(db, "bookmarks"))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "delete from bookmarks where user=$u";
                    cmd.Parameters.AddWithValue("$u", gid);
                    bm = cmd.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups purge bookmarks: " + ex.Message); }

        try
        {
            if (System.IO.File.Exists(TimeCodeDbPath))
            {
                using var db = OpenDb(TimeCodeDbPath);
                if (TableExists(db, "timecodes"))
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "delete from timecodes where user=$u";
                    cmd.Parameters.AddWithValue("$u", gid);
                    tc = cmd.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups purge timecodes: " + ex.Message); }

        try
        {
            string bucket = JutHistoryBucket(gid);
            if (bucket != JutSharedBucket && JsonStore.Exists(JutHistoryPath(bucket)))
            {
                JsonStore.Remove(JutHistoryPath(bucket));
                JsonStore.ForgetDir(JutHistoryDir());
                jt = 1;
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups purge jut: " + ex.Message); }

        if (bm + tc + jt == 0) return null;

        JsonStore.Flush();
        string s = $"закладок {bm}, позиций {tc}, jut {jt}";
        Console.WriteLine("[QbitDownload] groups purge " + gid + ": " + s);
        return s;
    }

    #endregion
}
