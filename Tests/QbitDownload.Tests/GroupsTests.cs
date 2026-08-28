using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Models.Base;
using Shared.Models.Events;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Группы устройств: общая история просмотров (qdl 2.81).
///
/// Покрываем то, чья ошибка не видна глазом и стоит дорого:
///   • ГРАНИЦУ ПОДМЕНЫ — /qdl/* обязан видеть НАСТОЯЩИЙ айди устройства, иначе одна галочка
///     «объединить историю» тихо раздала бы всей группе права на камеры и удаление;
///   • ЗАМОК ОБНУЛЕНИЯ — без него третье устройство, просто открывшее карточку, сбрасывало бы
///     позицию остальным (в боевой базе такие нулевые записи есть);
///   • СЛИЯНИЕ — потерять при объединении чужую историю необратимо;
///   • ЗАЩИТУ ОТ ПЕСОЧНИЦЫ — стенд e2e умеет УДАЛЯТЬ по айди, и в группе это была бы
///     общая история владельца.
/// </summary>
public class GroupsTests
{
    const string UidA = "daaa1111";
    const string UidB = "dbbb2222";
    const string UidC = "dccc3333";

    // ── стенд ─────────────────────────────────────────────────────────────

    /// <summary>Свежий cachePath + свой каталог database (шов DbDirOverride) + пустые таблицы.</summary>
    static string FreshEnv()
    {
        string cache = TestEnv.FreshCache();
        ModInit.conf.groupsEnabled = true;
        ModInit.conf.replicaRole = null;

        string db = Path.Combine(cache, "database");
        Directory.CreateDirectory(db);
        QbitController.DbDirOverride = db;

        Exec(Path.Combine(db, "Sync.sql"), "create table if not exists bookmarks (user text, data text, updated text)");
        Exec(Path.Combine(db, "TimeCode.sql"), "create table if not exists timecodes (user text, card text, item text, data text, updated text)");
        return cache;
    }

    static void Exec(string dbPath, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + dbPath);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static string Db(string name) => Path.Combine(QbitController.DbDirOverride, name);

    static void SeedBookmarks(string user, JObject data)
    {
        using var c = new SqliteConnection("Data Source=" + Db("Sync.sql"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "insert into bookmarks(user, data, updated) values($u,$d,$up)";
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$d", data.ToString(Newtonsoft.Json.Formatting.None));
        cmd.Parameters.AddWithValue("$up", "2026-08-01 00:00:00");
        cmd.ExecuteNonQuery();
    }

    static JObject ReadBookmarks(string user)
    {
        using var c = new SqliteConnection("Data Source=" + Db("Sync.sql"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "select data from bookmarks where user=$u limit 1";
        cmd.Parameters.AddWithValue("$u", user);
        string s = cmd.ExecuteScalar()?.ToString();
        return string.IsNullOrEmpty(s) ? null : JObject.Parse(s);
    }

    static void SeedTimecode(string user, string card, string item, double percent, double time, string updated)
    {
        using var c = new SqliteConnection("Data Source=" + Db("TimeCode.sql"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "insert into timecodes(user, card, item, data, updated) values($u,$c,$i,$d,$up)";
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$c", card);
        cmd.Parameters.AddWithValue("$i", item);
        cmd.Parameters.AddWithValue("$d", new JObject { ["duration"] = 100, ["time"] = time, ["percent"] = percent }.ToString(Newtonsoft.Json.Formatting.None));
        cmd.Parameters.AddWithValue("$up", updated);
        cmd.ExecuteNonQuery();
    }

    static double ReadPercent(string user, string card, string item)
    {
        using var c = new SqliteConnection("Data Source=" + Db("TimeCode.sql"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "select data from timecodes where user=$u and card=$c and item=$i limit 1";
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$c", card);
        cmd.Parameters.AddWithValue("$i", item);
        string s = cmd.ExecuteScalar()?.ToString();
        return s == null ? -1 : JObject.Parse(s).Value<double>("percent");
    }

    static JObject Hist(params int[] ids)
    {
        var h = new JArray();
        var cards = new JArray();
        foreach (int id in ids) { h.Add(id); cards.Add(new JObject { ["id"] = id }); }
        return new JObject { ["history"] = h, ["card"] = cards };
    }

    static bool HasId(JObject data, string field, int id)
    {
        if (data?[field] is not JArray arr) return false;
        foreach (var t in arr) if (t.ToString() == id.ToString()) return true;
        return false;
    }

    // ── резолв ────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_returns_input_when_not_grouped()
    {
        FreshEnv();
        Assert.Equal(UidA, Groups.Resolve(UidA));
        Assert.Null(Groups.GroupOf(UidA));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public void Resolve_passes_garbage_through(string uid)
    {
        FreshEnv();
        Assert.Equal(uid, Groups.Resolve(uid));
    }

    [Fact]
    public void Resolve_maps_member_to_group()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Assert.StartsWith(Groups.GidPrefix, gid);

        Assert.Null(Groups.Join(gid, UidA, apply: true)["error"]);

        Assert.Equal(gid, Groups.Resolve(UidA));
        Assert.Equal(gid, Groups.GroupOf(UidA));
        Assert.Equal(UidB, Groups.Resolve(UidB));      // сосед не в группе — не тронут
        Assert.Equal(gid, Groups.Resolve(gid));        // айди группы резолвится сам в себя
    }

    [Fact]
    public void Killswitch_makes_resolve_identity_but_keeps_membership()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        ModInit.conf.groupsEnabled = false;
        try
        {
            Assert.Equal(UidA, Groups.Resolve(UidA));                  // история снова личная
            Assert.Equal(gid, Groups.GroupOf(UidA));                   // но состав цел
        }
        finally { ModInit.conf.groupsEnabled = true; }

        Assert.Equal(gid, Groups.Resolve(UidA));                       // и включается обратно
    }

    // ── красные линии состава ─────────────────────────────────────────────

    [Fact]
    public void Test_device_never_joins()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        string testUid = Perms.TestUidPrefix + "ab12cd34";

        Assert.NotNull(Groups.JoinDenied(gid, testUid));
        Assert.NotNull(Groups.Join(gid, testUid, apply: true)["error"]);
        Assert.Null(Groups.GroupOf(testUid));
    }

    [Fact]
    public void Device_lives_in_one_group_only()
    {
        FreshEnv();
        string g1 = Groups.Create("Первая");
        string g2 = Groups.Create("Вторая");

        Assert.Null(Groups.Join(g1, UidA, apply: true)["error"]);
        Assert.NotNull(Groups.Join(g2, UidA, apply: true)["error"]);
        Assert.Equal(g1, Groups.GroupOf(UidA));
    }

    [Fact]
    public void Replica_role_refuses_to_edit_groups()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");

        ModInit.conf.replicaRole = "replica";
        try
        {
            Assert.NotNull(Groups.Join(gid, UidA, apply: true)["error"]);
            Assert.Null(Groups.GroupOf(UidA));
        }
        finally { ModInit.conf.replicaRole = null; }
    }

    // ── слияние истории ───────────────────────────────────────────────────

    [Fact]
    public void Join_merges_bookmarks_of_all_members()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1, 2));
        SeedBookmarks(UidB, Hist(3));

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        Groups.Join(gid, UidB, apply: true);

        var group = ReadBookmarks(gid);
        Assert.True(HasId(group, "history", 1));
        Assert.True(HasId(group, "history", 2));
        Assert.True(HasId(group, "history", 3));
        Assert.Equal(3, ((JArray)group["card"]).Count);

        // 🔴 Личные строки участников остаются нетронутыми — иначе связку нельзя было бы откатить.
        Assert.True(HasId(ReadBookmarks(UidA), "history", 1));
        Assert.True(HasId(ReadBookmarks(UidB), "history", 3));
    }

    [Fact]
    public void Join_is_idempotent_on_repeat_merge()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1, 2));

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        string once = ReadBookmarks(gid).ToString(Newtonsoft.Json.Formatting.None);

        // повторный перенос той же строки не должен ни плодить дублей, ни менять порядок
        var report = new JObject();
        QbitController.GroupsMergeHistory(UidA, gid, apply: true, report);

        Assert.Equal(once, ReadBookmarks(gid).ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void Join_keeps_the_furthest_position()
    {
        FreshEnv();
        SeedTimecode(UidA, "qdl_t1", "i1", percent: 80, time: 80, updated: "2026-08-01 10:00:00");
        SeedTimecode(UidB, "qdl_t1", "i1", percent: 20, time: 20, updated: "2026-08-02 10:00:00");

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        Groups.Join(gid, UidB, apply: true);   // свежее по времени, но НЕ дальше

        Assert.Equal(80, ReadPercent(gid, "qdl_t1", "i1"));
    }

    [Theory]
    // src, dst → победил ли src
    [InlineData(80, "2026-08-01", 20, "2026-08-02", true)]    // дальше важнее свежести
    [InlineData(20, "2026-08-02", 80, "2026-08-01", false)]
    [InlineData(50, "2026-08-02", 50, "2026-08-01", true)]    // равные — по свежести
    [InlineData(50, "2026-08-01", 50, "2026-08-02", false)]
    public void Timecode_wins_by_percent_then_freshness(double sp, string su, double dp, string du, bool srcWins)
    {
        string S(double p) => new JObject { ["percent"] = p, ["time"] = p }.ToString(Newtonsoft.Json.Formatting.None);
        Assert.Equal(srcWins, QbitController.GroupsTimecodeWins(S(sp), su, S(dp), du));
    }

    [Fact]
    public void Leave_with_copy_hands_the_shared_history_back()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1));

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        // в группе посмотрели ещё один тайтл и одну серию
        SeedBookmarks(UidB, Hist(2));
        QbitController.GroupsMergeHistory(UidB, gid, apply: true, new JObject());
        SeedTimecode(gid, "qdl_t9", "i9", percent: 95, time: 95, updated: "2026-08-05 10:00:00");

        Assert.Null(Groups.Leave(UidA, keepCopy: true, apply: true)["error"]);

        Assert.Null(Groups.GroupOf(UidA));
        Assert.Equal(UidA, Groups.Resolve(UidA));

        var mine = ReadBookmarks(UidA);
        Assert.True(HasId(mine, "history", 1));
        Assert.True(HasId(mine, "history", 2));                 // накопленное в группе осталось
        Assert.Equal(95, ReadPercent(UidA, "qdl_t9", "i9"));
    }

    [Fact]
    public void Leave_without_copy_leaves_personal_history_untouched()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1));

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        SeedBookmarks(UidB, Hist(2));
        QbitController.GroupsMergeHistory(UidB, gid, apply: true, new JObject());

        Groups.Leave(UidA, keepCopy: false, apply: true);

        var mine = ReadBookmarks(UidA);
        Assert.True(HasId(mine, "history", 1));
        Assert.False(HasId(mine, "history", 2));
    }

    [Fact]
    public void Preview_writes_nothing()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1, 2));

        string gid = Groups.Create("Семья");
        var report = Groups.Join(gid, UidA, apply: false);

        Assert.Null(report["error"]);
        Assert.Equal(1, report.Value<int>("bookmarks"));
        Assert.Null(ReadBookmarks(gid));            // в БД не легло ничего
        Assert.Null(Groups.GroupOf(UidA));          // и связки тоже нет
    }

    [Fact]
    public void Dissolve_frees_every_member()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        Groups.Join(gid, UidB, apply: true);

        Assert.Null(Groups.Dissolve(gid, keepCopy: true, apply: true)["error"]);

        Assert.Null(Groups.GroupOf(UidA));
        Assert.Null(Groups.GroupOf(UidB));
        Assert.False(Groups.Exists(gid));
    }

    // ── замок обнуления ───────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"duration\":0,\"time\":0,\"percent\":0}", true)]
    [InlineData("{\"duration\":100,\"time\":0,\"percent\":0}", true)]
    [InlineData("{\"duration\":0,\"time\":10,\"percent\":10}", true)]
    [InlineData("{\"duration\":100,\"time\":10,\"percent\":10}", false)]
    [InlineData("не json", false)]
    public void Zero_road_detected(string data, bool zero) => Assert.Equal(zero, Groups.IsZeroRoad(data));

    [Fact]
    public void Zero_guard_protects_existing_progress_only()
    {
        FreshEnv();
        SeedTimecode("g-deadbeef", "qdl_t1", "i1", percent: 40, time: 40, updated: "2026-08-01 10:00:00");

        Assert.True(QbitController.TimecodeHasProgress("g-deadbeef", "qdl_t1", "i1"));
        Assert.False(QbitController.TimecodeHasProgress("g-deadbeef", "qdl_t1", "i-нет"));   // нет строки — пусть пишет
        Assert.False(QbitController.TimecodeHasProgress("другой", "qdl_t1", "i1"));

        SeedTimecode("g-deadbeef", "qdl_t2", "i2", percent: 0, time: 0, updated: "2026-08-01 10:00:00");
        Assert.False(QbitController.TimecodeHasProgress("g-deadbeef", "qdl_t2", "i2"));      // нулевую затирать можно
    }

    // ── граница подмены (самый дорогой инвариант) ─────────────────────────

    static async Task<string> UidSeenByController(string path, string uid)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        var req = new RequestModel { user_uid = uid, IP = "192.168.87.31" };
        ctx.Features.Set(req);

        await Groups.OnRequestAsync(first: false, new EventMiddleware(false, ctx));
        return req.user_uid;
    }

    [Theory]
    [InlineData("/bookmark/list")]
    [InlineData("/bookmark/add")]
    [InlineData("/bookmark/added")]
    [InlineData("/bookmark/set")]
    [InlineData("/bookmark/remove")]
    [InlineData("/timecode/all")]
    [InlineData("/reqinfo")]
    public async Task Sync_routes_see_the_group(string path)
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        Assert.Equal(gid, await UidSeenByController(path, UidA));
    }

    [Theory]
    // 🔴 Права и реестр устройств: группой не подменяется НИЧЕГО из /qdl/*.
    [InlineData("/qdl/features")]
    [InlineData("/qdl/live/cams")]
    [InlineData("/qdl/list")]
    [InlineData("/qdl/delete")]
    [InlineData("/qdl/jut/search")]
    // Блоб localStorage оставлен личным сознательно: он пишется «весь документ целиком».
    [InlineData("/storage/get")]
    [InlineData("/storage/set")]
    // Отдача самих плагинов — не синк.
    [InlineData("/timecode.js")]
    [InlineData("/bookmark.js")]
    [InlineData("/timecode/js/token")]
    public async Task Other_routes_keep_the_device_id(string path)
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        Assert.Equal(UidA, await UidSeenByController(path, UidA));
    }

    [Fact]
    public async Task Inter_module_calls_are_never_rewritten()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/bookmark/list";
        var req = new RequestModel { user_uid = UidA, IsLocalRequest = true };
        ctx.Features.Set(req);

        await Groups.OnRequestAsync(first: false, new EventMiddleware(false, ctx));
        Assert.Equal(UidA, req.user_uid);
    }

    [Fact]
    public async Task First_pass_does_not_rewrite()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/bookmark/list";
        var req = new RequestModel { user_uid = UidA };
        ctx.Features.Set(req);

        // first:true — этап до UseAuthorization/UseAccsdb, там айди обязан быть устройства
        await Groups.OnRequestAsync(first: true, new EventMiddleware(true, ctx));
        Assert.Equal(UidA, req.user_uid);
    }

    // ── связь с реестром устройств ────────────────────────────────────────

    [Fact]
    public void Perms_forget_drops_membership()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        Groups.Join(gid, UidB, apply: true);

        Perms.Forget(UidA);

        Assert.Null(Groups.GroupOf(UidA));
        Assert.Equal(gid, Groups.GroupOf(UidB));   // соседа не задело
        Assert.Equal(UidA, Groups.Resolve(UidA));
    }

    [Fact]
    public void Grouped_device_is_never_evicted_by_cap()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidC, apply: true);

        // Участник «видели» давно, всех остальных — только что: по капу вылетел бы первым.
        Perms.Touch(new RequestModel { user_uid = UidC, IP = "1.1.1.1" }, force: true);

        for (int i = 0; i < 260; i++)
            Perms.Touch(new RequestModel { user_uid = "dfill" + i.ToString("D4"), IP = "1.1.1.1" }, force: true);

        Assert.True(Perms.Known(UidC));
        Assert.Equal(gid, Groups.GroupOf(UidC));
    }

    // ── перенос состава на реплику ────────────────────────────────────────

    [Fact]
    public void Snapshot_round_trip_rebuilds_the_index()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        var snap = Groups.Snapshot();

        FreshEnv();                                   // «другая машина»: пустой реестр
        Assert.Null(Groups.GroupOf(UidA));

        Assert.True(Groups.ApplySnapshot(snap));
        Assert.Equal(gid, Groups.Resolve(UidA));

        Assert.False(Groups.ApplySnapshot(snap));     // идемпотентность тика репликации
    }

    [Fact]
    public void Snapshot_of_garbage_is_ignored()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        Assert.False(Groups.ApplySnapshot(null));
        Assert.False(Groups.ApplySnapshot(new JObject()));
        Assert.Equal(gid, Groups.GroupOf(UidA));      // прежний состав цел
    }

    // ── уборка после расформирования (красная линия: единственный DELETE фичи) ──

    [Fact]
    public void Dissolve_with_copy_purges_the_group_row()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1));

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        SeedTimecode(gid, "qdl_t9", "i9", percent: 95, time: 95, updated: "2026-08-05 10:00:00");

        var report = Groups.Dissolve(gid, keepCopy: true, apply: true);

        Assert.NotNull(report["purged"]);
        Assert.Null(ReadBookmarks(gid));                        // дубликат убран
        Assert.Equal(-1, ReadPercent(gid, "qdl_t9", "i9"));
        // 🔴 но копия у участника осталась — ради этого всё и делалось
        Assert.True(HasId(ReadBookmarks(UidA), "history", 1));
        Assert.Equal(95, ReadPercent(UidA, "qdl_t9", "i9"));
    }

    [Fact]
    public void Dissolve_without_copy_keeps_everything_on_disk()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1));

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        SeedTimecode(gid, "qdl_t9", "i9", percent: 95, time: 95, updated: "2026-08-05 10:00:00");

        var report = Groups.Dissolve(gid, keepCopy: false, apply: true);

        Assert.Null(report["purged"]);
        Assert.NotNull(ReadBookmarks(gid));                     // админка обещает — обещание держим
        Assert.Equal(95, ReadPercent(gid, "qdl_t9", "i9"));
    }

    [Fact]
    public void Purge_never_touches_a_device_id()
    {
        FreshEnv();
        SeedBookmarks(UidA, Hist(1));
        SeedTimecode(UidA, "qdl_t9", "i9", percent: 95, time: 95, updated: "2026-08-05 10:00:00");

        Assert.Null(QbitController.GroupsPurge(UidA));           // не g-… → отказ молча
        Assert.Null(QbitController.GroupsPurge(null));
        Assert.Null(QbitController.GroupsPurge(""));

        Assert.NotNull(ReadBookmarks(UidA));
        Assert.Equal(95, ReadPercent(UidA, "qdl_t9", "i9"));
    }

    [Fact]
    public void Purge_refuses_while_the_group_is_alive()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        SeedTimecode(gid, "qdl_t9", "i9", percent: 95, time: 95, updated: "2026-08-05 10:00:00");

        Assert.Null(QbitController.GroupsPurge(gid));
        Assert.Equal(95, ReadPercent(gid, "qdl_t9", "i9"));
    }

    [Fact]
    public void Dissolve_of_a_gone_group_cleans_its_leftovers()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);
        SeedTimecode(gid, "qdl_t9", "i9", percent: 95, time: 95, updated: "2026-08-05 10:00:00");

        Groups.Dissolve(gid, keepCopy: false, apply: true);       // данные намеренно оставлены
        Assert.Equal(95, ReadPercent(gid, "qdl_t9", "i9"));

        var again = Groups.Dissolve(gid, keepCopy: true, apply: true);
        Assert.Null(again["error"]);
        Assert.NotNull(again["purged"]);
        Assert.Equal(-1, ReadPercent(gid, "qdl_t9", "i9"));
    }

    [Fact]
    public void Dissolve_refuses_a_device_id_outright()
    {
        FreshEnv();
        SeedTimecode(UidA, "qdl_t9", "i9", percent: 95, time: 95, updated: "2026-08-05 10:00:00");

        var r = Groups.Dissolve(UidA, keepCopy: true, apply: true);
        Assert.NotNull(r["error"]);
        Assert.Equal(95, ReadPercent(UidA, "qdl_t9", "i9"));
    }
}
