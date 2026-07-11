using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Коллекции фильмов в «Загрузках» (/qdl/collections): CRUD в collections.json,
/// инварианты (1 фильм — 1 коллекция, cover ∈ hashes, пустая удаляется),
/// хуки PurgeCache/MigrateCache.
/// </summary>
public class CollectionsTests
{
    static readonly string H1 = new string('a', 40);
    static readonly string H2 = new string('b', 40);
    static readonly string H3 = new string('c', 40);

    static string[] Hs(params string[] h) => h;

    static void WriteMeta(string dir, string hash, string title)
    {
        Directory.CreateDirectory(Path.Combine(dir, "meta"));
        File.WriteAllText(Path.Combine(dir, "meta", hash + ".json"), new JObject { ["title"] = title }.ToString());
    }

    static string[] Hashes(JObject col) => ((JArray)col["hashes"]).Select(x => (string)x).ToArray();

    // ── создание ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_requires_at_least_two_valid_hashes()
    {
        TestEnv.FreshCache();
        Assert.Null(Access.ColCreate("Дюна", null));
        Assert.Null(Access.ColCreate("Дюна", Hs(H1)));
        Assert.Null(Access.ColCreate("Дюна", Hs(H1, H1)));            // дубль схлопывается → 1
        Assert.Null(Access.ColCreate("Дюна", Hs(H1, "not-a-hash")));
    }

    [Fact]
    public void Create_sets_id_cover_and_persists()
    {
        TestEnv.FreshCache();
        var col = Access.ColCreate("Дюна", Hs(H1, H2));

        Assert.NotNull(col);
        Assert.True(Access.ValidColId((string)col["id"]));
        Assert.False(Access.ValidHash((string)col["id"]));            // id не спутать с infohash
        Assert.Equal("Дюна", (string)col["title"]);
        Assert.Equal(H1, (string)col["cover"]);                       // обложка = первый добавленный
        Assert.Equal(new[] { H1, H2 }, Hashes(col));

        var stored = Access.LoadCollections();                        // round-trip через файл
        Assert.Single(stored);
        Assert.Equal((string)col["id"], (string)stored[0]["id"]);
    }

    [Fact]
    public void Create_empty_title_falls_back_to_meta_then_default()
    {
        string dir = TestEnv.FreshCache();
        Assert.Equal("Коллекция", (string)Access.ColCreate(null, Hs(H1, H2))["title"]);

        TestEnv.FreshCache();
        dir = QbitDownload.ModInit.conf.cachePath;
        WriteMeta(dir, H1, "Риддик");
        Assert.Equal("Риддик", (string)Access.ColCreate("  ", Hs(H1, H2))["title"]);
    }

    [Fact]
    public void Create_caps_title_length()
    {
        TestEnv.FreshCache();
        var col = Access.ColCreate(new string('x', 500), Hs(H1, H2));
        Assert.Equal(120, ((string)col["title"]).Length);
    }

    // ── add: инвариант «1 фильм — 1 коллекция» ────────────────────────────

    [Fact]
    public void Add_appends_and_moves_between_collections()
    {
        TestEnv.FreshCache();
        var a = Access.ColCreate("A", Hs(H1, H2));
        var b = Access.ColCreate("B", Hs(H3, H1));   // create тоже переносит: H1 уходит из A

        var stored = Access.LoadCollections();
        var sa = (JObject)stored.First(x => (string)x["title"] == "A");
        var sb = (JObject)stored.First(x => (string)x["title"] == "B");
        Assert.Equal(new[] { H2 }, Hashes(sa));
        Assert.Equal(H2, (string)sa["cover"]);       // cover переехал на выжившего
        Assert.Equal(new[] { H3, H1 }, Hashes(sb));

        Assert.True(Access.ColAdd((string)sa["id"], H3));   // перенос H3 из B в A
        stored = Access.LoadCollections();
        sa = (JObject)stored.First(x => (string)x["title"] == "A");
        sb = (JObject)stored.First(x => (string)x["title"] == "B");
        Assert.Equal(new[] { H2, H3 }, Hashes(sa));
        Assert.Equal(new[] { H1 }, Hashes(sb));
    }

    [Fact]
    public void Add_is_noop_for_existing_member_and_false_for_unknown_id()
    {
        TestEnv.FreshCache();
        var col = Access.ColCreate("A", Hs(H1, H2));
        string id = (string)col["id"];

        Assert.True(Access.ColAdd(id, H1));                            // уже внутри
        Assert.Equal(new[] { H1, H2 }, Hashes((JObject)Access.LoadCollections()[0]));   // порядок цел

        Assert.False(Access.ColAdd("c" + new string('0', 32), H3));    // нет такой коллекции
    }

    // ── remove: фикс cover, автоудаление пустой ───────────────────────────

    [Fact]
    public void Remove_cover_hash_moves_cover_to_first_survivor()
    {
        TestEnv.FreshCache();
        string id = (string)Access.ColCreate("A", Hs(H1, H2, H3))["id"];

        var (ok, deleted) = Access.ColRemove(id, H1);
        Assert.True(ok); Assert.False(deleted);

        var col = (JObject)Access.LoadCollections()[0];
        Assert.Equal(new[] { H2, H3 }, Hashes(col));
        Assert.Equal(H2, (string)col["cover"]);
    }

    [Fact]
    public void Remove_last_member_deletes_collection()
    {
        TestEnv.FreshCache();
        string id = (string)Access.ColCreate("A", Hs(H1, H2))["id"];

        Assert.Equal((true, false), Access.ColRemove(id, H1));
        Assert.Equal((true, true), Access.ColRemove(id, H2));
        Assert.Empty(Access.LoadCollections());
        Assert.Equal((false, false), Access.ColRemove(id, H1));   // коллекции больше нет
    }

    [Fact]
    public void Remove_missing_hash_is_ok_and_changes_nothing()
    {
        TestEnv.FreshCache();
        string id = (string)Access.ColCreate("A", Hs(H1, H2))["id"];
        Assert.Equal((true, false), Access.ColRemove(id, H3));
        Assert.Equal(new[] { H1, H2 }, Hashes((JObject)Access.LoadCollections()[0]));
    }

    // ── update: rename + cover ────────────────────────────────────────────

    [Fact]
    public void Update_renames_and_changes_cover_only_to_member()
    {
        TestEnv.FreshCache();
        string id = (string)Access.ColCreate("A", Hs(H1, H2))["id"];

        Assert.True(Access.ColUpdate(id, "Дюна: сага", H2));
        var col = (JObject)Access.LoadCollections()[0];
        Assert.Equal("Дюна: сага", (string)col["title"]);
        Assert.Equal(H2, (string)col["cover"]);

        Assert.False(Access.ColUpdate(id, null, H3));                  // H3 не член → отказ
        Assert.Equal(H2, (string)((JObject)Access.LoadCollections()[0])["cover"]);

        Assert.True(Access.ColUpdate(id, null, null));                 // no-op разрешён
        Assert.False(Access.ColUpdate("c" + new string('0', 32), "X", null));
    }

    // ── dissolve ──────────────────────────────────────────────────────────

    [Fact]
    public void Dissolve_removes_collection()
    {
        TestEnv.FreshCache();
        string id = (string)Access.ColCreate("A", Hs(H1, H2))["id"];
        Assert.True(Access.ColDissolve(id));
        Assert.Empty(Access.LoadCollections());
        Assert.False(Access.ColDissolve(id));
    }

    // ── хуки PurgeCache / MigrateCache ────────────────────────────────────

    [Fact]
    public void CollectionsRemoveHash_cleans_all_and_drops_empty()
    {
        TestEnv.FreshCache();
        Access.ColCreate("A", Hs(H1, H2));
        Access.ColCreate("B", Hs(H3, new string('d', 40)));

        Access.CollectionsRemoveHash(H1);
        var stored = Access.LoadCollections();
        Assert.Equal(2, stored.Count);
        Assert.Equal(new[] { H2 }, Hashes((JObject)stored.First(x => (string)x["title"] == "A")));

        Access.CollectionsRemoveHash(H2);                              // последний в A → A удалена
        stored = Access.LoadCollections();
        Assert.Single(stored);
        Assert.Equal("B", (string)stored[0]["title"]);
    }

    [Fact]
    public void CollectionsMigrateHash_replaces_hash_and_cover()
    {
        TestEnv.FreshCache();
        Access.ColCreate("A", Hs(H1, H2));

        Access.CollectionsMigrateHash(H1, H3);                         // re-grab: H1 → H3
        var col = (JObject)Access.LoadCollections()[0];
        Assert.Equal(new[] { H3, H2 }, Hashes(col));
        Assert.Equal(H3, (string)col["cover"]);
    }

    [Fact]
    public void MigrateCache_keeps_watched_serial_in_collection()
    {
        // Сценарий «Следить за новыми сериями»: re-grab меняет infohash раздачи (MigrateCache),
        // сериал в коллекции не должен из неё выпадать
        string dir = TestEnv.FreshCache();
        WriteMeta(dir, H1, "Дом Дракона");
        Access.ColCreate("Сериалы", Hs(H1, H2));

        Access.MigrateCache(H1, H3);                                   // re-grab: H1 → H3
        var col = (JObject)Access.LoadCollections()[0];
        Assert.Equal(new[] { H3, H2 }, Hashes(col));
        Assert.Equal(H3, (string)col["cover"]);
        Assert.True(File.Exists(Path.Combine(dir, "meta", H3 + ".json")), "мета переехала на новый hash");
    }

    [Fact]
    public void PurgeCache_removes_deleted_download_from_collections()
    {
        TestEnv.FreshCache();
        Access.ColCreate("A", Hs(H1, H2));

        Access.PurgeCache(H1);                                         // сценарий /qdl/delete
        var col = (JObject)Access.LoadCollections()[0];
        Assert.Equal(new[] { H2 }, Hashes(col));
        Assert.Equal(H2, (string)col["cover"]);
    }

    // ── изоляция: коллекции НЕ трогают уведомления (noti/seen) и слежение (watch.json) ──

    static void SeedNoti(string hash)
    {
        using var db = new SqlContext();
        db.Database.EnsureCreated();
        db.seen.Add(new SeenModel { seriesKey = "t42", epkey = "s1e1" });
        db.noti.Add(new NotiModel { seriesKey = "t42", seriesId = 42, hash = hash, title = "Дом Дракона", season = 1, episode = 1, epkey = "s1e1", label = "S1E1", created = DateTime.UtcNow, read = false });
        db.SaveChanges();
    }

    [Fact]
    public void Collection_ops_do_not_touch_notifications()
    {
        TestEnv.FreshCache();
        SeedNoti(H1);
        Access.SaveWatch(new JArray { new JObject { ["hash"] = H1, ["id"] = 42, ["title"] = "Дом Дракона", ["link"] = "magnet:x" } });

        // полный жизненный цикл коллекции вокруг отслеживаемого сериала
        string id = (string)Access.ColCreate("Сериалы", Hs(H1, H2))["id"];
        Access.ColAdd(id, H3);
        Access.ColUpdate(id, "Драконы", H2);
        Access.ColRemove(id, H3);
        Access.CollectionsRemoveHash(H2);
        Access.ColDissolve(id);

        using var db = new SqlContext();
        Assert.Equal(1, db.noti.Count());                              // уведомления целы
        Assert.Equal(1, db.seen.Count());                              // база отсечения цела
        var w = Access.LoadWatch();                                    // слежение цело
        Assert.Single(w);
        Assert.Equal(H1, (string)w[0]["hash"]);
    }

    [Fact]
    public void PurgeCache_of_collection_member_still_cleans_notifications()
    {
        // Регрессия: членство в коллекции не должно мешать штатной чистке noti при /qdl/delete
        TestEnv.FreshCache();
        SeedNoti(H1);
        Access.ColCreate("Сериалы", Hs(H1, H2));

        Access.PurgeCache(H1);

        using var db = new SqlContext();
        Assert.Equal(0, db.noti.Count(x => x.hash == H1));             // noti раздачи вычищены как раньше
        Assert.Single(Access.LoadCollections());                       // коллекция ужалась до H2
    }
}
