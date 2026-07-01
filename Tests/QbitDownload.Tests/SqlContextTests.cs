using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for the module's SQLite persistence (<see cref="SqlContext"/> +
/// <see cref="SeenModel"/> / <see cref="NotiModel"/>). Each test uses a FRESH cache dir so the db file
/// (rooted at ModInit.conf.cachePath) is isolated and EnsureCreated builds a clean schema.
/// The context is configured with QueryTrackingBehavior.NoTracking, so reads return detached entities.
/// </summary>
public class SqlContextTests
{
    // ── schema ────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureCreated_builds_schema_and_starts_empty()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        bool created = db.Database.EnsureCreated();

        Assert.True(created);                 // brand-new db file → schema was created
        Assert.Empty(db.seen);
        Assert.Empty(db.noti);
    }

    [Fact]
    public void EnsureCreated_is_idempotent_on_same_cache()
    {
        string cache = TestEnv.FreshCache();
        using (var db1 = new QbitDownload.SqlContext())
        {
            Assert.True(db1.Database.EnsureCreated());   // first time creates
        }
        using (var db2 = new QbitDownload.SqlContext())
        {
            Assert.False(db2.Database.EnsureCreated());  // already exists → no create
        }
    }

    // ── SeenModel round-trip ──────────────────────────────────────────────

    [Fact]
    public void Seen_add_and_read_back()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.seen.Add(new SeenModel { seriesKey = "t123", epkey = "s1e7" });
        db.seen.Add(new SeenModel { seriesKey = "t123", epkey = "s1e8" });
        db.SaveChanges();

        var rows = db.seen.Where(x => x.seriesKey == "t123").OrderBy(x => x.epkey).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("s1e7", rows[0].epkey);
        Assert.Equal("s1e8", rows[1].epkey);
        // identity Id is populated by the store after SaveChanges
        Assert.True(rows[0].Id > 0);
        Assert.True(rows[1].Id > 0);
    }

    [Fact]
    public void Seen_reads_are_no_tracking_return_fresh_instances()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.seen.Add(new SeenModel { seriesKey = "t7", epkey = "e1" });
        db.SaveChanges();

        // NoTracking: each query materializes a new object; the same row read twice is not reference-equal.
        var a = db.seen.Single(x => x.epkey == "e1");
        var b = db.seen.Single(x => x.epkey == "e1");
        Assert.NotSame(a, b);
        Assert.Equal(a.Id, b.Id);
    }

    // ── SeenModel UNIQUE (seriesKey, epkey) ───────────────────────────────

    [Fact]
    public void Seen_duplicate_seriesKey_epkey_throws_on_save()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "s1e1" });
        db.SaveChanges();

        db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "s1e1" });   // duplicate composite key
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Seen_duplicate_within_single_savechanges_throws()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.seen.Add(new SeenModel { seriesKey = "t2", epkey = "e5" });
        db.seen.Add(new SeenModel { seriesKey = "t2", epkey = "e5" });
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Seen_same_epkey_different_seriesKey_is_allowed()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "s1e1" });
        db.seen.Add(new SeenModel { seriesKey = "t2", epkey = "s1e1" });   // same epkey, other series
        db.SaveChanges();

        Assert.Equal(2, db.seen.Count());
    }

    [Fact]
    public void Seen_same_seriesKey_different_epkey_is_allowed()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "e1" });
        db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "e2" });
        db.SaveChanges();

        Assert.Equal(2, db.seen.Count(x => x.seriesKey == "t1"));
    }

    // ── NotiModel round-trip ──────────────────────────────────────────────

    [Fact]
    public void Noti_add_and_read_back_all_columns()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        var now = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        db.noti.Add(new NotiModel
        {
            seriesKey = "t42", seriesId = 42, hash = "0123456789abcdef0123456789abcdef01234567",
            title = "Show", season = 1, episode = 7, kind = null, epkey = "s1e7",
            label = "Сезон 1 · серия 7", created = now, read = false
        });
        db.SaveChanges();

        var row = db.noti.Single();
        Assert.True(row.Id > 0);
        Assert.Equal("t42", row.seriesKey);
        Assert.Equal(42, row.seriesId);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", row.hash);
        Assert.Equal("Show", row.title);
        Assert.Equal(1, row.season);
        Assert.Equal(7, row.episode);
        Assert.Null(row.kind);
        Assert.Equal("s1e7", row.epkey);
        Assert.Equal("Сезон 1 · серия 7", row.label);
        Assert.Equal(now, row.created);
        Assert.False(row.read);
    }

    [Fact]
    public void Noti_negative_season_episode_and_kind_persist()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.noti.Add(new NotiModel
        {
            seriesKey = "l abc", seriesId = 0, hash = "h", title = "T",
            season = -1, episode = -1, kind = "OVA", epkey = "ova1",
            label = "OVA 1", created = DateTime.UtcNow, read = false
        });
        db.SaveChanges();

        var row = db.noti.Single();
        Assert.Equal(0, row.seriesId);
        Assert.Equal(-1, row.season);
        Assert.Equal(-1, row.episode);
        Assert.Equal("OVA", row.kind);
    }

    // ── NotiModel UNIQUE (seriesKey, epkey) ───────────────────────────────

    [Fact]
    public void Noti_duplicate_seriesKey_epkey_throws_on_save()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "s1e1", title = "a", created = DateTime.UtcNow });
        db.SaveChanges();

        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "s1e1", title = "b", created = DateTime.UtcNow });
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Noti_same_epkey_different_seriesKey_is_allowed()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e1", title = "a", created = DateTime.UtcNow });
        db.noti.Add(new NotiModel { seriesKey = "t2", epkey = "e1", title = "b", created = DateTime.UtcNow });
        db.SaveChanges();

        Assert.Equal(2, db.noti.Count());
    }

    // ── ExecuteUpdate / ExecuteDelete ─────────────────────────────────────

    [Fact]
    public void Noti_ExecuteUpdate_marks_read_true()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e1", title = "a", read = false, created = DateTime.UtcNow });
        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e2", title = "b", read = false, created = DateTime.UtcNow });
        db.SaveChanges();

        int affected = db.noti.Where(x => !x.read).ExecuteUpdate(s => s.SetProperty(x => x.read, true));

        Assert.Equal(2, affected);
        Assert.Equal(2, db.noti.Count(x => x.read));
        Assert.Equal(0, db.noti.Count(x => !x.read));
    }

    [Fact]
    public void Noti_ExecuteUpdate_only_affects_matching_rows()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e1", title = "a", read = false, created = DateTime.UtcNow });
        db.noti.Add(new NotiModel { seriesKey = "t2", epkey = "e1", title = "b", read = false, created = DateTime.UtcNow });
        db.SaveChanges();

        int affected = db.noti.Where(x => x.seriesKey == "t1").ExecuteUpdate(s => s.SetProperty(x => x.read, true));

        Assert.Equal(1, affected);
        Assert.True(db.noti.Single(x => x.seriesKey == "t1").read);
        Assert.False(db.noti.Single(x => x.seriesKey == "t2").read);
    }

    [Fact]
    public void Noti_ExecuteDelete_clears_all_rows()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e1", title = "a", created = DateTime.UtcNow });
        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e2", title = "b", created = DateTime.UtcNow });
        db.SaveChanges();

        int deleted = db.noti.ExecuteDelete();

        Assert.Equal(2, deleted);
        Assert.Empty(db.noti);
    }

    [Fact]
    public void Noti_ExecuteDelete_filtered_removes_only_matching()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e1", title = "a", read = true, created = DateTime.UtcNow });
        db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e2", title = "b", read = false, created = DateTime.UtcNow });
        db.SaveChanges();

        int deleted = db.noti.Where(x => x.read).ExecuteDelete();

        Assert.Equal(1, deleted);
        Assert.Equal(1, db.noti.Count());
        Assert.False(db.noti.Single().read);
    }

    [Fact]
    public void Seen_ExecuteDelete_clears_baseline()
    {
        string cache = TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();

        db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "e1" });
        db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "e2" });
        db.SaveChanges();

        int deleted = db.seen.Where(x => x.seriesKey == "t1").ExecuteDelete();

        Assert.Equal(2, deleted);
        Assert.Empty(db.seen);
    }

    // ── persistence across contexts (same cache dir) ──────────────────────

    [Fact]
    public void Rows_persist_across_context_instances_on_same_cache()
    {
        string cache = TestEnv.FreshCache();
        using (var db = new QbitDownload.SqlContext())
        {
            db.Database.EnsureCreated();
            db.seen.Add(new SeenModel { seriesKey = "t1", epkey = "e1" });
            db.noti.Add(new NotiModel { seriesKey = "t1", epkey = "e1", title = "a", created = DateTime.UtcNow });
            db.SaveChanges();
        }
        using (var db2 = new QbitDownload.SqlContext())
        {
            Assert.Equal(1, db2.seen.Count());
            Assert.Equal(1, db2.noti.Count());
        }
    }
}
