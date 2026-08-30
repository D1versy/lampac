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
/// Музыка внутри группы устройств: подмена айди на /music/* и слияние Music.sql.
///
/// Самое дорогое здесь — ГРАНИЦА ПОДМЕНЫ. Правило для музыки префиксное (модуль молодой, роутов
/// 41, точный список молча отстал бы при первом же новом эндпоинте), поэтому цену ошибки платит
/// deny-список, и он обязан быть под тестом целиком:
///   • «/music/play» сравнивается ТОЧНО — StartsWith съел бы /music/playlists и все ручки
///     плейлистов, и групповые плейлисты отвалились бы молча;
///   • «/music/auth/*» личный — это OAuth-токены, группа не раздаёт права;
///   • «/qdl/*» не подменяется никогда.
/// Плюс инвариант идемпотентности слияния: повторный Join обязан давать ноль.
/// </summary>
public class MusicGroupsTests
{
    const string UidA = "dmus1111";
    const string UidB = "dmus2222";

    // ── стенд ─────────────────────────────────────────────────────────────

    static string FreshEnv()
    {
        string cache = TestEnv.FreshCache();
        ModInit.conf.groupsEnabled = true;
        ModInit.conf.musicGroupsEnabled = true;
        ModInit.conf.replicaRole = null;

        string db = Path.Combine(cache, "database");
        Directory.CreateDirectory(db);
        QbitController.DbDirOverride = db;

        Exec(Path.Combine(db, "Sync.sql"), "create table if not exists bookmarks (user text, data text, updated text)");
        Exec(Path.Combine(db, "TimeCode.sql"), "create table if not exists timecodes (user text, card text, item text, data text, updated text)");

        string musicDir = Path.Combine(db, "music");
        Directory.CreateDirectory(musicDir);
        QbitController.MusicDbPathOverride = Path.Combine(musicDir, "Music.sql");

        // схема — копия EnsureSchema модуля Music (Modules/Music/SQL/MusicContext.cs)
        Exec(QbitController.MusicDbPathOverride, "create table if not exists playback_history (Id integer primary key autoincrement, profile_id text not null default '', track_id text not null, payload text not null, updated text not null)");
        Exec(QbitController.MusicDbPathOverride, "create table if not exists track_stats_daily (Id integer primary key autoincrement, profile_id text not null default '', track_id text not null, day text not null, play_count integer not null default 0, total_ms integer not null default 0, last_played text not null)");
        Exec(QbitController.MusicDbPathOverride, "create table if not exists user_playlists (Id integer primary key autoincrement, profile_id text not null default '', playlist_id text not null, title text not null, payload text not null, source text not null default '', updated text not null)");
        Exec(QbitController.MusicDbPathOverride, "create table if not exists auth_credentials (Id integer primary key autoincrement, profile_id text not null default '', provider_id text not null, payload text not null, updated text not null)");
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

    static void MusicExec(string sql, params (string name, object val)[] ps)
    {
        using var c = new SqliteConnection("Data Source=" + QbitController.MusicDbPathOverride);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in ps) cmd.Parameters.AddWithValue(p.name, p.val);
        cmd.ExecuteNonQuery();
    }

    static object MusicScalar(string sql, params (string name, object val)[] ps)
    {
        using var c = new SqliteConnection("Data Source=" + QbitController.MusicDbPathOverride);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in ps) cmd.Parameters.AddWithValue(p.name, p.val);
        return cmd.ExecuteScalar();
    }

    static void SeedPlay(string profile, string track, string updated)
        => MusicExec("insert into playback_history(profile_id, track_id, payload, updated) values($u,$t,$p,$up)",
                     ("$u", profile), ("$t", track), ("$p", "{\"id\":\"" + track + "\"}"), ("$up", updated));

    static void SeedStats(string profile, string track, string day, long plays, long ms, string last)
        => MusicExec("insert into track_stats_daily(profile_id, track_id, day, play_count, total_ms, last_played) values($u,$t,$d,$p,$m,$l)",
                     ("$u", profile), ("$t", track), ("$d", day), ("$p", plays), ("$m", ms), ("$l", last));

    static void SeedPlaylist(string profile, string id, string title, string updated)
        => MusicExec("insert into user_playlists(profile_id, playlist_id, title, payload, source, updated) values($u,$p,$t,$pl,$s,$up)",
                     ("$u", profile), ("$p", id), ("$t", title), ("$pl", "[]"), ("$s", ""), ("$up", updated));

    static int PlayCount(string profile)
        => Convert.ToInt32(MusicScalar("select count(*) from playback_history where profile_id=$u", ("$u", profile)));

    static string PlayUpdated(string profile, string track)
        => MusicScalar("select updated from playback_history where profile_id=$u and track_id=$t", ("$u", profile), ("$t", track))?.ToString();

    static (long plays, long ms, string last) Stats(string profile, string track, string day)
    {
        using var c = new SqliteConnection("Data Source=" + QbitController.MusicDbPathOverride);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "select play_count, total_ms, last_played from track_stats_daily where profile_id=$u and track_id=$t and day=$d";
        cmd.Parameters.AddWithValue("$u", profile);
        cmd.Parameters.AddWithValue("$t", track);
        cmd.Parameters.AddWithValue("$d", day);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)) : (-1, -1, null);
    }

    // ── deny-список: классификация всех роутов раздела ────────────────────

    [Theory]
    // профильные: история, статистика, плейлисты, миксы, радио — их и объединяем
    [InlineData("/music")]
    [InlineData("/music/home")]
    [InlineData("/music/history/mark")]
    [InlineData("/music/history/remove")]
    [InlineData("/music/stats/top")]
    [InlineData("/music/stats/clear")]
    [InlineData("/music/daily")]
    [InlineData("/music/radio")]
    [InlineData("/music/playlists")]
    [InlineData("/music/playlists/tracks")]
    [InlineData("/music/playlists/create")]
    [InlineData("/music/playlists/delete")]
    [InlineData("/music/playlists/import")]
    [InlineData("/music/playlists/import/soundcloud")]
    [InlineData("/music/playlists/sync")]
    [InlineData("/music/playlists/track/add")]
    [InlineData("/music/playlists/track/remove")]
    [InlineData("/music/playlists/track/move")]
    // каталожные: профиль не читают вовсе, подмена для них no-op — правил под них не заводим
    [InlineData("/music/section")]
    [InlineData("/music/search")]
    [InlineData("/music/album")]
    [InlineData("/music/artist")]
    [InlineData("/music/lyrics")]
    public void Music_paths_are_group_scoped(string path) => Assert.True(Groups.IsMusicSyncPath(path));

    [Theory]
    // 🔴 главный кейс файла: точное сравнение /music/play против префикса
    [InlineData("/music/play")]
    [InlineData("/music/stream")]
    [InlineData("/music/matches")]
    [InlineData("/music/match/select")]
    [InlineData("/music/match/reset")]
    [InlineData("/music/playlist.m3u")]
    [InlineData("/music/clientlog")]
    // OAuth-креды остаются личными — группа не влияет на права
    [InlineData("/music/auth/state")]
    [InlineData("/music/auth/save")]
    [InlineData("/music/auth/logout")]
    // отдача плагина
    [InlineData("/music/js/abc")]
    [InlineData("/music.js")]
    // чужое
    [InlineData("/musical/home")]
    [InlineData("/qdl/list")]
    [InlineData("")]
    [InlineData(null)]
    public void Music_paths_stay_personal(string path) => Assert.False(Groups.IsMusicSyncPath(path));

    // ── граница подмены в живом middleware ────────────────────────────────

    static async Task<string> UidSeenByController(string path, string uid)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        var req = new RequestModel { user_uid = uid, IP = "192.168.87.31" };
        ctx.Features.Set(req);

        await Groups.OnRequestAsync(first: false, new EventMiddleware(false, ctx));
        return req.user_uid;
    }

    [Fact]
    public async Task Music_history_sees_group_id()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        Assert.Equal(gid, await UidSeenByController("/music/home", UidA));
        Assert.Equal(gid, await UidSeenByController("/music/history/mark", UidA));
        Assert.Equal(gid, await UidSeenByController("/music/stats/top", UidA));
        Assert.Equal(gid, await UidSeenByController("/music/playlists/create", UidA));
    }

    [Theory]
    [InlineData("/music/auth/state")]
    [InlineData("/music/play")]
    [InlineData("/music/stream")]
    [InlineData("/music.js")]
    [InlineData("/qdl/features")]
    [InlineData("/qdl/live/watch/1")]
    public async Task Personal_paths_see_device_id(string path)
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        Assert.Equal(UidA, await UidSeenByController(path, UidA));
    }

    [Fact]
    public async Task Killswitch_turns_off_music_only()
    {
        FreshEnv();
        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        ModInit.conf.musicGroupsEnabled = false;

        Assert.Equal(UidA, await UidSeenByController("/music/home", UidA));   // музыка снова личная
        Assert.Equal(gid, await UidSeenByController("/bookmark/list", UidA)); // кино не тронуто
    }

    [Fact]
    public async Task Ungrouped_device_keeps_its_id()
    {
        FreshEnv();
        Assert.Equal(UidA, await UidSeenByController("/music/home", UidA));
    }

    // ── слияние ───────────────────────────────────────────────────────────

    [Fact]
    public void Merge_moves_history_stats_and_playlists()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");
        SeedStats(UidA, "yt:aaa", "2026-08-01", plays: 3, ms: 600000, last: "2026-08-01 10:00:00");
        SeedPlaylist(UidA, "pl1", "Мой список", "2026-08-01 10:00:00");

        string gid = Groups.Create("Семья");
        Assert.Null(Groups.Join(gid, UidA, apply: true)["error"]);

        Assert.Equal(1, PlayCount(gid));
        Assert.Equal((3, 600000, "2026-08-01 10:00:00"), Stats(gid, "yt:aaa", "2026-08-01"));
        Assert.Equal(1, Convert.ToInt32(MusicScalar("select count(*) from user_playlists where profile_id=$u", ("$u", gid))));

        // 🔴 инвариант «никогда не удаляем»: личная строка осталась лежать
        Assert.Equal(1, PlayCount(UidA));
    }

    [Fact]
    public void Merge_keeps_the_fresher_history_row()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");
        SeedPlay("g-deadbeef", "yt:aaa", "2026-08-20 10:00:00");

        var report = new JObject();
        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, report);

        Assert.Equal("2026-08-20 10:00:00", PlayUpdated("g-deadbeef", "yt:aaa"));
        Assert.Equal(0, report.Value<int>("music"));
    }

    [Fact]
    public void Merge_takes_the_fresher_row_from_source()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-25 10:00:00");
        SeedPlay("g-deadbeef", "yt:aaa", "2026-08-01 10:00:00");

        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, new JObject());
        Assert.Equal("2026-08-25 10:00:00", PlayUpdated("g-deadbeef", "yt:aaa"));
    }

    [Fact]
    public void Stats_merge_takes_max_and_never_sums()
    {
        FreshEnv();
        SeedStats(UidA, "yt:aaa", "2026-08-01", plays: 3, ms: 100, last: "2026-08-01 12:00:00");
        SeedStats("g-deadbeef", "yt:aaa", "2026-08-01", plays: 5, ms: 50, last: "2026-08-01 09:00:00");

        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, new JObject());

        // ровно правило таймкодов: никто не отнимает у другого послушанное, но и не удваивает
        Assert.Equal((5, 100, "2026-08-01 12:00:00"), Stats("g-deadbeef", "yt:aaa", "2026-08-01"));
    }

    [Fact]
    public void Merge_is_idempotent()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");
        SeedStats(UidA, "yt:aaa", "2026-08-01", plays: 3, ms: 600000, last: "2026-08-01 10:00:00");
        SeedPlaylist(UidA, "pl1", "Мой список", "2026-08-01 10:00:00");

        var first = new JObject();
        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, first);
        Assert.Equal(3, first.Value<int>("music"));

        var second = new JObject();
        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, second);
        Assert.Equal(0, second.Value<int>("music"));

        Assert.Equal(1, PlayCount("g-deadbeef"));
        Assert.Equal((3, 600000, "2026-08-01 10:00:00"), Stats("g-deadbeef", "yt:aaa", "2026-08-01"));
    }

    [Fact]
    public void Preview_writes_nothing()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");

        var report = new JObject();
        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: false, report);

        Assert.Equal(1, report.Value<int>("music"));
        Assert.Equal(0, PlayCount("g-deadbeef"));
    }

    [Fact]
    public void Replica_refuses_to_merge()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");
        ModInit.conf.replicaRole = "replica";

        var report = new JObject();
        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, report);

        Assert.NotNull(report["error"]);
        Assert.Equal(0, PlayCount("g-deadbeef"));
        ModInit.conf.replicaRole = null;
    }

    [Fact]
    public void Auth_credentials_are_never_merged()
    {
        FreshEnv();
        MusicExec("insert into auth_credentials(profile_id, provider_id, payload, updated) values($u,$p,$pl,$up)",
                  ("$u", UidA), ("$p", "soundcloud"), ("$pl", "{\"token\":\"секрет\"}"), ("$up", "2026-08-01 10:00:00"));

        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, new JObject());

        Assert.Equal(0, Convert.ToInt32(MusicScalar("select count(*) from auth_credentials where profile_id=$u", ("$u", "g-deadbeef"))));
        Assert.Equal(1, Convert.ToInt32(MusicScalar("select count(*) from auth_credentials where profile_id=$u", ("$u", UidA))));
    }

    [Fact]
    public void Missing_music_db_is_not_an_error()
    {
        FreshEnv();
        QbitController.MusicDbPathOverride = Path.Combine(ModInit.conf.cachePath, "нет-такой", "Music.sql");

        var report = new JObject();
        QbitController.GroupsMergeHistory(UidA, "g-deadbeef", apply: true, report);

        Assert.Equal(0, report.Value<int>("music"));
        Assert.Empty((JArray)report["errors"]);
    }

    [Fact]
    public void Leave_with_copy_returns_music_to_device()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, apply: true);

        // в группе послушали ещё один трек
        SeedPlay(gid, "yt:bbb", "2026-08-20 10:00:00");

        Assert.Null(Groups.Leave(UidA, keepCopy: true, apply: true)["error"]);
        Assert.Equal(2, PlayCount(UidA));
    }

    // ── счётчик админки ───────────────────────────────────────────────────

    [Fact]
    public void Stats_counts_tracks()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");
        SeedPlay(UidA, "yt:bbb", "2026-08-02 10:00:00");

        Assert.Equal(2, QbitController.GroupsStats(UidA).Value<int>("music"));
        Assert.Equal(0, QbitController.GroupsStats(UidB).Value<int>("music"));
    }

    // ── уборка расформированной группы ────────────────────────────────────

    [Fact]
    public void Purge_removes_music_of_dissolved_group_only()
    {
        FreshEnv();
        SeedPlay("g-deadbeef", "yt:aaa", "2026-08-01 10:00:00");
        SeedStats("g-deadbeef", "yt:aaa", "2026-08-01", plays: 1, ms: 10, last: "2026-08-01 10:00:00");
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");

        Assert.NotNull(QbitController.GroupsPurge("g-deadbeef"));

        Assert.Equal(0, PlayCount("g-deadbeef"));
        Assert.Equal(1, PlayCount(UidA));   // устройство не тронуто
    }

    [Fact]
    public void Purge_refuses_device_id_and_live_group()
    {
        FreshEnv();
        SeedPlay(UidA, "yt:aaa", "2026-08-01 10:00:00");

        // 🔴 замок 1: айди устройства сюда не проходит физически
        Assert.Null(QbitController.GroupsPurge(UidA));
        Assert.Equal(1, PlayCount(UidA));

        // 🔴 замок 3: живая группа не убирается
        string gid = Groups.Create("Семья");
        SeedPlay(gid, "yt:bbb", "2026-08-01 10:00:00");
        Assert.Null(QbitController.GroupsPurge(gid));
        Assert.Equal(1, PlayCount(gid));
    }
}
