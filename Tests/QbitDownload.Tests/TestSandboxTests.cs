using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Models.Base;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Песочница e2e (qdl 2.64): прогон гейта не заводит пользователей и убирает свои следы сам.
///
/// 🔴 Здесь под тестом КРАСНАЯ ЛИНИЯ — код уборки удаляет данные, и цена ошибки не «красный
/// прогон», а потерянная история просмотров живого человека. Поэтому проверяется не только
/// «убирает что надо», но и — прежде всего — «не трогает то, что нельзя»:
/// именованное устройство, устройство с правами, обычного пользователя и соседа по хранилищу.
/// </summary>
public class TestSandboxTests
{
    const string HeadlessUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                              "(KHTML, like Gecko) HeadlessChrome/139.0.0.0 Safari/537.36";
    const string RealUa = "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) " +
                          "Chrome/139.0.0.0 Safari/537.36 lampa_client d1vision_ios/1.0.13-524";
    const string TestUid = Perms.TestUidPrefix + "ab12cd34";

    static RequestModel Req(string uid, string ua = null, string ip = "192.168.87.31")
        => new RequestModel { user_uid = uid, UserAgent = ua, IP = ip };

    /// <summary>Свежий cachePath + свой каталог database (шов DbDirOverride).</summary>
    static string FreshEnv()
    {
        string cache = TestEnv.FreshCache();
        string db = Path.Combine(cache, "database");
        Directory.CreateDirectory(Path.Combine(db, "storage", "syncview"));
        QbitController.DbDirOverride = db;
        return cache;
    }

    static string JutPath(string cache, string uid) => Path.Combine(cache, "jut", "history", uid + ".json");

    static string BlobPath(string cache, string uid)
    {
        string md5 = Shared.Services.Utilities.CrypTo.md5(uid);
        return Path.Combine(cache, "database", "storage", "syncview", md5.Substring(0, 2), md5.Substring(2));
    }

    static void SeedJut(string cache, string uid)
    {
        JsonStore.WriteNow(JutPath(cache, uid), new JObject { ["watch"] = new JArray("тайтл") });
    }

    static void SeedBlob(string cache, string uid)
    {
        string path = BlobPath(cache, uid);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"online_view\":\"\"}");
    }

    static void SeedSql(string cache, string uid, int bookmarks, int timecodes)
    {
        string db = Path.Combine(cache, "database");
        Directory.CreateDirectory(db);

        using (var c = new SqliteConnection("Data Source=" + Path.Combine(db, "Sync.sql")))
        {
            c.Open();
            Exec(c, "create table if not exists bookmarks (user text, data text, updated text)");
            for (int i = 0; i < bookmarks; i++)
                Exec(c, "insert into bookmarks(user, data, updated) values('" + uid + "', 'd', 'u')");
        }

        using (var c = new SqliteConnection("Data Source=" + Path.Combine(db, "TimeCode.sql")))
        {
            c.Open();
            Exec(c, "create table if not exists timecodes (user text, card text, item text, data text, updated text)");
            for (int i = 0; i < timecodes; i++)
                Exec(c, "insert into timecodes(user, card, item, data, updated) values('" + uid + "', 'c" + i + "', 'i', 'd', 'u')");
        }
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static int Rows(string cache, string file, string table, string uid)
    {
        string path = Path.Combine(cache, "database", file);
        if (!File.Exists(path)) return 0;

        using var c = new SqliteConnection("Data Source=" + path);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "select count(*) from " + table + " where user=$u";
        cmd.Parameters.AddWithValue("$u", uid);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ══ реестр: кого вообще заводим ═══════════════════════════════════════════

    [Fact]
    public void Headless_со_случайным_айди_в_реестр_не_попадает_вообще()
    {
        // Ровно та проблема, ради которой всё это: 14 браузеров за прогон = 14 «пользователей».
        FreshEnv();
        Perms.Touch(Req("dq4wiujq", HeadlessUa), force: true);

        Assert.False(Perms.Known("dq4wiujq"));
        Assert.Empty(Perms.List());
    }

    [Fact]
    public void Наш_стенд_заводится_с_меткой_и_понятным_именем()
    {
        // Строка ему нужна: permsgate выдаёт и отзывает на нём права. А имя — чтобы упавший
        // посреди ночи прогон не выглядел в админке как ещё один человек.
        FreshEnv();
        Perms.Touch(Req(TestUid, HeadlessUa), force: true);

        var row = Perms.List().Single(x => (string)x["uid"] == TestUid);
        Assert.True((bool)row["test"]);
        Assert.Equal("🧪 тест (headless)", (string)row["name"]);

    }

    [Fact]
    public void Право_выданное_стенду_не_делает_его_пользователем()
    {
        FreshEnv();
        Perms.Grant(TestUid, Perms.FeatureLive, true);

        var row = Perms.List().Single(x => (string)x["uid"] == TestUid);
        Assert.True((bool)row["test"]);
        Assert.True(Perms.Allowed(TestUid, Perms.FeatureLive));   // права при этом работают
    }

    [Fact]
    public void Обычный_клиент_с_живым_UA_заводится_как_раньше()
    {
        FreshEnv();
        Perms.Touch(Req("dueq3shm", RealUa), force: true);

        var row = Perms.List().Single(x => (string)x["uid"] == "dueq3shm");
        Assert.False((bool)row["test"]);
        Assert.Equal("ios", (string)row["platform"]);
    }

    // ══ разные стенды под одним префиксом: headless, мак, iOS ═════════════════

    [Theory]
    [InlineData("d1v-test-55f581ba")]        // headless-обвязка (scripts/headless)
    [InlineData("d1v-test-mac-7f427180")]    // дев-запуск D1Vision из Xcode (D1V_TEST=1)
    [InlineData("d1v-test-ios-7f427180")]
    [InlineData("d1v-test-android-1")]
    public void Тестовым_считается_любой_айди_с_нашим_префиксом(string uid)
    {
        // 🔴 Опознание ПРЕФИКСНОЕ, формат хвоста роли не играет. Ужесточи это до
        // «префикс + 8 hex» — и маковская дев-строка выпадет из песочницы: её не пометят
        // тестовой и не уберут, она осядет в админке навсегда как живой пользователь.
        Assert.True(Perms.IsTestUid(uid));
        Assert.True(Perms.IsTestDevice(uid, null));
    }

    [Theory]
    [InlineData("d1vtest-mac-1")]
    [InlineData("d1v-testing-1")]   // дефис в префиксе не даёт зацепить чужое имя
    [InlineData("test-mac-1")]
    [InlineData("dueq3shm")]
    public void Похожий_но_чужой_айди_тестовым_не_считается(string uid)
        => Assert.False(Perms.IsTestUid(uid));

    [Theory]
    [InlineData("d1v-test-55f581ba", "🧪 тест (headless)")]
    [InlineData("d1v-test-mac-7f427180", "🧪 тест (mac)")]
    [InlineData("d1v-test-ios-7f427180", "🧪 тест (ios)")]
    public void Имя_тестовой_строки_называет_платформу(string uid, string expected)
        => Assert.Equal(expected, Perms.TestNameFor(uid));

    [Fact]
    public void Дев_запуск_мака_заводится_помеченным_и_убирается_уборкой()
    {
        // UA у него НАСТОЯЩИЙ (d1vision_mac/…) — правило про headless его не ловит, ловит
        // префикс. Проверяем весь путь: завёлся → помечен → снесён вместе со следами.
        const string MacUa = "Mozilla/5.0 (Macintosh) lampa_client d1vision_mac/1.0.13-523";
        const string MacUid = Perms.TestUidPrefix + "mac-7f427180";

        string cache = FreshEnv();
        Perms.Touch(Req(MacUid, MacUa), force: true);
        SeedJut(cache, MacUid);
        SeedSql(cache, MacUid, bookmarks: 1, timecodes: 4);

        var row = Perms.List().Single(x => (string)x["uid"] == MacUid);
        Assert.True((bool)row["test"]);
        Assert.Equal("🧪 тест (mac)", (string)row["name"]);
        Assert.Equal("mac", (string)row["platform"]);

        var report = QbitController.TestPurge(MacUid, all: false, apply: true);

        Assert.Null(report["error"]);
        Assert.False(Perms.Known(MacUid));
        Assert.False(File.Exists(JutPath(cache, MacUid)));
        Assert.Equal(0, Rows(cache, "TimeCode.sql", "timecodes", MacUid));
    }

    [Fact]
    public void Забытая_тестовая_строка_протухает_сама()
    {
        // Дев-запуск мака не убирает НИКТО: run-all чистит только свой айди. Без срока
        // годности такая строка осталась бы в админке навсегда — и вернула бы ровно ту
        // жалобу, с которой всё началось.
        string cache = FreshEnv();
        string macOld = Perms.TestUidPrefix + "mac-old";

        var devices = new JObject { [macOld] = Dev(RealUa, "🧪 тест (mac)"), ["dueq3shm"] = Dev(RealUa, "Живой") };
        devices[macOld]["last"] = DateTime.UtcNow.AddHours(-30);
        devices["dueq3shm"]["last"] = DateTime.UtcNow.AddDays(-90);   // живой не протухает НИКОГДА

        JsonStore.WriteNow(Path.Combine(cache, "access.json"),
                           new JObject { ["ver"] = 1, ["devices"] = devices });

        Perms.Touch(Req("inoxvjis", RealUa), force: true);   // любая запись в реестр зовёт Prune

        Assert.False(Perms.Known(macOld));
        Assert.True(Perms.Known("dueq3shm"));
    }

    [Fact]
    public void Свежая_тестовая_строка_не_протухает()
    {
        FreshEnv();
        Perms.Touch(Req(TestUid, HeadlessUa), force: true);
        Perms.Touch(Req("dueq3shm", RealUa), force: true);

        Assert.True(Perms.Known(TestUid));
    }

    // ══ классификатор: кого уборке трогать НЕЛЬЗЯ ═════════════════════════════


    [Fact]
    public void Именованное_устройство_защищено_даже_с_headless_UA()
    {
        // Владелец мог назвать что угодно — имя есть, значит это не безымянный прогон.
        var d = new JObject { ["ua"] = HeadlessUa, ["name"] = "Ноутбук в гараже", ["grants"] = new JArray() };
        Assert.False(Perms.IsTestDevice("dq4wiujq", d));
    }

    [Fact]
    public void Устройство_с_правами_защищено_даже_с_headless_UA()
    {
        var d = new JObject { ["ua"] = HeadlessUa, ["name"] = "", ["grants"] = new JArray("live") };
        Assert.False(Perms.IsTestDevice("dq4wiujq", d));
    }

    [Fact]
    public void Обычный_пользователь_не_тестовый_ни_при_каких_условиях()
    {
        var d = new JObject { ["ua"] = RealUa, ["name"] = "", ["grants"] = new JArray() };
        Assert.False(Perms.IsTestDevice("dueq3shm", d));
    }

    [Fact]
    public void Наш_префикс_тестовый_даже_с_правами_и_именем()
    {
        // Обратная сторона правила: упавший permsgate не должен оставлять строку гвоздём.
        var d = new JObject { ["ua"] = RealUa, ["name"] = "что угодно", ["grants"] = new JArray("live", "rec") };
        Assert.True(Perms.IsTestDevice(TestUid, d));
    }

    [Fact]
    public void Фикстура_боевого_реестра_к_уборке_ровно_тестовые()
    {
        // Слепок с боевого 24.08.2026: 85 устройств, из них 74 — прогоны гейта.
        string cache = FreshEnv();
        var devices = new JObject();

        var real = new List<string> { "sajnp6ml", "diqituzn", "dueq3shm", "inoxvjis", "7kfrxzfr",
                                      "go2kmwdz", "duxdnhak", "du7f885t", "d4wg18dw", "wxsviqle", "dcgf0e8v" };

        devices["sajnp6ml"] = Dev(RealUa, "if Mac", "live", "rec");
        devices["diqituzn"] = Dev(RealUa, "Vova");
        devices["dueq3shm"] = Dev(RealUa, "", "live", "rec");
        devices["inoxvjis"] = Dev(RealUa, "", "live", "rec");
        devices["7kfrxzfr"] = Dev(RealUa, "", "live", "rec");
        devices["go2kmwdz"] = Dev(RealUa, "", "live", "rec");
        devices["duxdnhak"] = Dev(RealUa, "Dim");
        devices["du7f885t"] = Dev(RealUa, "D5");
        devices["d4wg18dw"] = Dev(RealUa, "");
        devices["wxsviqle"] = Dev("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/139", "");
        devices["dcgf0e8v"] = Dev("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/139", "");

        for (int i = 0; i < 74; i++)
            devices["d" + i.ToString("d7")] = Dev(HeadlessUa, "");

        JsonStore.WriteNow(Path.Combine(cache, "access.json"),
                           new JObject { ["ver"] = 1, ["devices"] = devices });

        var victims = Perms.TestDevices();

        Assert.Equal(74, victims.Count);
        foreach (string uid in real)
            Assert.DoesNotContain(uid, victims);
    }

    static JObject Dev(string ua, string name, params string[] grants)
        => new JObject
        {
            ["ua"] = ua,
            ["name"] = name,
            ["platform"] = "web",
            ["grants"] = new JArray(grants),
            ["first"] = DateTime.UtcNow,
            ["last"] = DateTime.UtcNow
        };

    // ══ уборка: отказы ════════════════════════════════════════════════════════

    [Fact]
    public void Уборка_по_нетестовому_айди_отказывает_и_не_трогает_ничего()
    {
        string cache = FreshEnv();
        Perms.Touch(Req("dueq3shm", RealUa), force: true);
        SeedJut(cache, "dueq3shm");
        SeedBlob(cache, "dueq3shm");
        SeedSql(cache, "dueq3shm", bookmarks: 3, timecodes: 7);

        var report = QbitController.TestPurge("dueq3shm", all: false, apply: true);

        Assert.NotNull(report["error"]);
        Assert.Empty((JArray)report["uids"]);

        // ГЛАВНОЕ: данные человека на месте, все до одного.
        Assert.True(Perms.Known("dueq3shm"));
        Assert.True(File.Exists(JutPath(cache, "dueq3shm")));
        Assert.True(File.Exists(BlobPath(cache, "dueq3shm")));
        Assert.Equal(3, Rows(cache, "Sync.sql", "bookmarks", "dueq3shm"));
        Assert.Equal(7, Rows(cache, "TimeCode.sql", "timecodes", "dueq3shm"));
    }

    [Fact]
    public void Пустой_запрос_это_ошибка_а_не_убрать_всё()
    {
        // Умолчание не имеет права быть разрушительным.
        string cache = FreshEnv();
        Perms.Touch(Req(TestUid, HeadlessUa), force: true);

        var report = QbitController.TestPurge(null, all: false, apply: true);

        Assert.NotNull(report["error"]);
        Assert.True(Perms.Known(TestUid));
    }

    [Fact]
    public void Мусорный_айди_отказ()
    {
        FreshEnv();
        var report = QbitController.TestPurge("!!!", all: false, apply: true);

        Assert.NotNull(report["error"]);
        Assert.Empty((JArray)report["uids"]);
    }

    [Fact]
    public void Сухой_прогон_считает_но_ничего_не_пишет()
    {
        string cache = FreshEnv();
        Perms.Touch(Req(TestUid, HeadlessUa), force: true);
        SeedJut(cache, TestUid);
        SeedBlob(cache, TestUid);
        SeedSql(cache, TestUid, bookmarks: 2, timecodes: 5);

        var report = QbitController.TestPurge(TestUid, all: false, apply: false);

        Assert.Equal(1, (int)report["devices"]);
        Assert.Equal(1, (int)report["jut"]);
        Assert.Equal(1, (int)report["blobs"]);
        Assert.Equal(2, (int)report["bookmarks"]);
        Assert.Equal(5, (int)report["timecodes"]);

        // но на диске всё осталось
        Assert.True(Perms.Known(TestUid));
        Assert.True(File.Exists(JutPath(cache, TestUid)));
        Assert.True(File.Exists(BlobPath(cache, TestUid)));
        Assert.Equal(2, Rows(cache, "Sync.sql", "bookmarks", TestUid));
    }

    // ══ уборка: то, ради чего всё ═════════════════════════════════════════════

    [Fact]
    public void Уборка_сносит_все_пять_хранилищ_своего_айди()
    {
        string cache = FreshEnv();
        Perms.Touch(Req(TestUid, HeadlessUa), force: true);
        Perms.Grant(TestUid, Perms.FeatureLive, true);
        SeedJut(cache, TestUid);
        SeedBlob(cache, TestUid);
        SeedSql(cache, TestUid, bookmarks: 2, timecodes: 5);

        var report = QbitController.TestPurge(TestUid, all: false, apply: true);

        Assert.Null(report["error"]);
        Assert.Empty((JArray)report["errors"]);

        Assert.False(Perms.Known(TestUid));
        Assert.False(File.Exists(JutPath(cache, TestUid)));
        Assert.False(File.Exists(BlobPath(cache, TestUid)));
        Assert.Equal(0, Rows(cache, "Sync.sql", "bookmarks", TestUid));
        Assert.Equal(0, Rows(cache, "TimeCode.sql", "timecodes", TestUid));

        // и права ушли вместе со строкой
        Assert.False(Perms.Allowed(TestUid, Perms.FeatureLive));
    }

    [Fact]
    public void Уборка_не_задевает_соседа_ни_в_одном_хранилище()
    {
        // 🔴 Самый дорогой сценарий: удаление по uid обязано быть точечным.
        string cache = FreshEnv();
        Perms.Touch(Req(TestUid, HeadlessUa), force: true);
        Perms.Touch(Req("dueq3shm", RealUa), force: true);

        SeedJut(cache, TestUid);
        SeedJut(cache, "dueq3shm");
        SeedBlob(cache, TestUid);
        SeedBlob(cache, "dueq3shm");
        SeedSql(cache, TestUid, bookmarks: 2, timecodes: 3);
        SeedSql(cache, "dueq3shm", bookmarks: 4, timecodes: 9);

        QbitController.TestPurge(TestUid, all: false, apply: true);

        Assert.True(Perms.Known("dueq3shm"));
        Assert.True(File.Exists(JutPath(cache, "dueq3shm")));
        Assert.True(File.Exists(BlobPath(cache, "dueq3shm")));
        Assert.Equal(4, Rows(cache, "Sync.sql", "bookmarks", "dueq3shm"));
        Assert.Equal(9, Rows(cache, "TimeCode.sql", "timecodes", "dueq3shm"));
    }

    [Fact]
    public void Уборка_всех_тестовых_не_трогает_живые_устройства()
    {
        string cache = FreshEnv();

        Perms.Touch(Req(TestUid, HeadlessUa), force: true);
        Perms.Touch(Req("dueq3shm", RealUa), force: true);
        Perms.Grant("dueq3shm", Perms.FeatureRec, true);
        Perms.Rename("diqituzn", "Vova");
        SeedSql(cache, "dueq3shm", bookmarks: 4, timecodes: 9);

        var report = QbitController.TestPurge(null, all: true, apply: true);

        Assert.Equal(new[] { TestUid }, ((JArray)report["uids"]).Select(x => (string)x).ToArray());
        Assert.True(Perms.Known("dueq3shm"));
        Assert.True(Perms.Known("diqituzn"));
        Assert.Equal(4, Rows(cache, "Sync.sql", "bookmarks", "dueq3shm"));
    }

    [Fact]
    public void Общий_бакет_безымянных_уборке_недоступен()
    {
        // _shared.json — не устройство: туда стекается история всех, кто пришёл без айди.
        string cache = FreshEnv();
        JsonStore.WriteNow(JutPath(cache, "_shared"), new JObject { ["watch"] = new JArray("тайтл") });

        Perms.Touch(Req(TestUid, HeadlessUa), force: true);
        QbitController.TestPurge(TestUid, all: false, apply: true);

        Assert.True(File.Exists(JutPath(cache, "_shared")));
    }

    // ══ киллсвитч и вытеснение ════════════════════════════════════════════════

    [Fact]
    public void Киллсвитч_возвращает_прежнее_поведение_и_запрещает_уборку()
    {
        FreshEnv();
        try
        {
            ModInit.conf.testSandbox = false;

            // headless снова обычное устройство — как было до 2.64
            Perms.Touch(Req("dq4wiujq", HeadlessUa), force: true);
            Assert.True(Perms.Known("dq4wiujq"));

            // а уборка не трогает никого, даже наш собственный стенд
            Perms.Touch(Req(TestUid, HeadlessUa), force: true);
            var report = QbitController.TestPurge(TestUid, all: false, apply: true);

            Assert.NotNull(report["error"]);
            Assert.True(Perms.Known(TestUid));
        }
        finally { ModInit.conf.testSandbox = true; }
    }

    [Fact]
    public void Тестовых_строк_в_реестре_не_копится()
    {
        // Даже если кто-то запустит стенд с десятком разных айди — реестр не зарастёт.
        FreshEnv();
        for (int i = 0; i < 10; i++)
            Perms.Touch(Req(Perms.TestUidPrefix + "host" + i, HeadlessUa), force: true);

        Assert.True(Perms.List().Count <= 3);
    }

    [Fact]
    public void Вытеснение_тестовых_не_трогает_живые_устройства()
    {
        FreshEnv();
        Perms.Touch(Req("dueq3shm", RealUa), force: true);
        Perms.Touch(Req("diqituzn", RealUa), force: true);

        for (int i = 0; i < 10; i++)
            Perms.Touch(Req(Perms.TestUidPrefix + "host" + i, HeadlessUa), force: true);

        Assert.True(Perms.Known("dueq3shm"));
        Assert.True(Perms.Known("diqituzn"));
    }
}
