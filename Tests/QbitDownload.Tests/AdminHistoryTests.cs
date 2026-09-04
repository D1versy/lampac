using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Models.Base;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Страница истории просмотров в админке (/admin/d1v/history, qdl 2.105).
///
/// Покрываем ровно то, чья ошибка не видна глазом на живом сервере:
///   • СХЛОПЫВАНИЕ ПО ТАЙТЛУ — у сериала вёдер штатно два («270603_tv» от карточки и
///     «qdl_t270603» от экрана серий). Ключ по ведру давал в журнале два «Повелителя духов»,
///     и на боевых данных это увидели глазами уже ПОСЛЕ первой выкатки;
///   • ССЫЛКУ ПОСТЕРА — треть боевых карточек несёт адрес «https://tv.d1versy.com:9443/…»,
///     который из локалки не открывается вовсе. Без переписывания владелец получил бы стену
///     битых картинок;
///   • «НИ ОДНОЙ ЗАПИСИ» — главное требование владельца к этой странице, и, кроме теста,
///     его ничто не стережёт;
///   • ЧЕСТНОСТЬ ПРО ГРУППЫ — при выключенном groupsEnabled сервер уже пишет в личный ключ,
///     и показать «историю группы» значило бы соврать.
/// </summary>
public class AdminHistoryTests
{
    const string UidA = "dh111111";
    const string UidB = "dh222222";

    // ── стенд ─────────────────────────────────────────────────────────────

    static string FreshEnv()
    {
        string cache = TestEnv.FreshCache();
        ModInit.conf.groupsEnabled = true;
        ModInit.conf.replicaRole = null;
        ModInit.conf.xsmartApi = "";      // по умолчанию портал не спрашиваем: тесты не ходят в сеть

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

    static void SeedRoad(string user, string card, string item, JObject road, string updated)
    {
        using var c = new SqliteConnection("Data Source=" + Db("TimeCode.sql"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "insert into timecodes(user, card, item, data, updated) values($u,$c,$i,$d,$up)";
        cmd.Parameters.AddWithValue("$u", user);
        cmd.Parameters.AddWithValue("$c", card);
        cmd.Parameters.AddWithValue("$i", item);
        cmd.Parameters.AddWithValue("$d", road.ToString(Newtonsoft.Json.Formatting.None));
        cmd.Parameters.AddWithValue("$up", updated);
        cmd.ExecuteNonQuery();
    }

    static void Road(string user, string card, string item, double percent, double time, string updated)
        => SeedRoad(user, card, item, new JObject { ["duration"] = 100, ["time"] = time, ["percent"] = percent }, updated);

    /// <summary>Карточка сериала в том виде, в каком её кладёт Lampa.</summary>
    static JObject Tv(object id, string title) => new JObject
    {
        ["id"] = JToken.FromObject(id),
        ["title"] = title,
        ["name"] = title,
        ["first_air_date"] = "2026-01-01",
        ["source"] = "cub",
        ["img"] = "http://192.168.87.24:9118/tmdb/img/t/p/w300/poster.jpg?uid=" + UidA
    };

    static void Register(string uid) => Perms.Grant(uid, "live", false);   // заводит строку в реестре

    static JArray Plays(JObject r) => (JArray)r["plays"];
    static JObject Play(JObject r, string title) => Plays(r).OfType<JObject>().First(x => (string)x["title"] == title);

    // ══ схлопывание журнала по тайтлу ═════════════════════════════════════

    [Fact]
    public async Task Two_buckets_of_one_series_collapse_into_a_single_row()
    {
        // 🔴 Регресс, найденный глазами на боевых данных: «270603_tv» пишет полная карточка,
        // «qdl_t270603» — экран серий. Ключ по ведру рисовал тайтл в журнале ДВАЖДЫ.
        FreshEnv();
        Register(UidA);

        SeedBookmarks(UidA, new JObject { ["history"] = new JArray(270603), ["card"] = new JArray(Tv(270603, "Рыцарь")) });
        Road(UidA, "270603_tv", "i1", 20, 10, "2026-08-30 10:00:00");
        Road(UidA, "qdl_t270603", "i2", 95, 90, "2026-08-31 10:00:00");

        var r = await QbitController.AdminHistory(UidA);

        Assert.Single(Plays(r));
        var row = Play(r, "Рыцарь");
        Assert.Equal(2, (int)row["rows"]);
        Assert.Equal(2, ((JArray)row["buckets"]).Count);
        Assert.Equal(1, (int)r["counts"]["titles"]);
    }

    [Fact]
    public async Task Journal_reports_rows_watched_max_and_the_time_span()
    {
        FreshEnv();
        Register(UidA);

        SeedBookmarks(UidA, new JObject { ["history"] = new JArray(270603), ["card"] = new JArray(Tv(270603, "Рыцарь")) });
        Road(UidA, "qdl_t270603", "i1", 10, 5, "2026-08-30 10:00:00");
        Road(UidA, "qdl_t270603", "i2", 93, 90, "2026-08-31 12:00:00");
        Road(UidA, "qdl_t270603", "i3", 0, 0, "2026-08-29 08:00:00");

        var row = Play(await QbitController.AdminHistory(UidA), "Рыцарь");

        Assert.Equal(3, (int)row["rows"]);
        Assert.Equal(1, (int)row["done"]);          // порог 90 %, как у самого плеера
        Assert.Equal(93, (int)row["percentMax"]);
        Assert.Equal("2026-08-29 08:00:00", (string)row["first"]);
        Assert.Equal("2026-08-31 12:00:00", (string)row["last"]);
    }

    [Fact]
    public async Task A_native_player_row_without_time_still_counts_as_progress()
    {
        // 🔴 Нативные плееры Android/iOS пишут ПРОЦЕНТ без времени. Проверка «есть time» считала
        // бы такую запись пустой — на этом уже спотыкались в Groups (замок обнуления).
        FreshEnv();
        Register(UidA);

        SeedBookmarks(UidA, new JObject { ["history"] = new JArray(270603), ["card"] = new JArray(Tv(270603, "Рыцарь")) });
        SeedRoad(UidA, "qdl_t270603", "i1",
                 new JObject { ["duration"] = 0, ["time"] = 0, ["percent"] = 95 }, "2026-08-31 12:00:00");

        var row = Play(await QbitController.AdminHistory(UidA), "Рыцарь");

        Assert.Equal(95, (int)row["percentMax"]);
        Assert.Equal(1, (int)row["done"]);
    }

    [Fact]
    public async Task Unresolvable_buckets_are_counted_and_not_dropped()
    {
        // «0_movie» — вырожденное ведро (карточки не было), «qdl_tprobe» — наш же зонд.
        // Показать по ним нечего, но и молчать нельзя: иначе счётчик соврёт о том, сколько смотрели.
        FreshEnv();
        Register(UidA);

        Road(UidA, "0_movie", "i1", 10, 5, "2026-08-30 10:00:00");
        Road(UidA, "qdl_tprobe", "i2", 10, 5, "2026-08-30 11:00:00");

        var r = await QbitController.AdminHistory(UidA);

        Assert.Empty(Plays(r));
        Assert.Equal(2, (int)r["counts"]["unresolved"]);
        Assert.Equal(2, (int)r["counts"]["timecodes"]);
    }

    // ══ XSMART ════════════════════════════════════════════════════════════

    [Fact]
    public async Task An_xsmart_row_survives_a_silent_portal_with_a_readable_label()
    {
        // xsmartApi пуст — это киллсвитч (так он и выглядит на реплике). Ни одного запроса,
        // но строка остаётся: просмотр был.
        FreshEnv();
        Register(UidA);
        Road(UidA, "qdl_xsmart:6:9147477", "i1", 72, 60, "2026-08-30 10:00:00");

        var r = await QbitController.AdminHistory(UidA);
        var row = Assert.Single(Plays(r)) as JObject;

        Assert.Equal("XSMART · 6-9147477", (string)row["title"]);
        Assert.False((bool)row["resolved"]);
        Assert.Equal("xsmart", (string)row["source"]);
        Assert.Equal(72, (int)row["percentMax"]);
        Assert.Equal(0, (int)r["counts"]["unresolved"]);   // строка НЕ потеряна
    }

    [Theory]
    [InlineData("qdl_xsmart:6:9147477", true)]
    [InlineData("qdl_xsmart:99:1", true)]         // категории такой нет, но просмотр был — строку не теряем
    [InlineData("qdl_xsmart:6:abc", false)]       // айди только цифрами
    [InlineData("qdl_xsmart:6", false)]
    public async Task A_malformed_xsmart_bucket_never_pretends_to_be_a_title(string bucket, bool row)
    {
        FreshEnv();
        Register(UidA);
        Road(UidA, bucket, "i1", 50, 30, "2026-08-30 10:00:00");

        var r = await QbitController.AdminHistory(UidA);

        Assert.Equal(row ? 1 : 0, Plays(r).Count);
        Assert.Equal(row ? 0 : 1, (int)r["counts"]["unresolved"]);
    }

    [Fact]
    public async Task A_bogus_category_never_reaches_the_portal()
    {
        // 🔴 cat и id уходят прямо в URL. Гейт XsmartNet.Valid обязан отсечь их ДО запроса —
        // проверяем это по времени: настроен заведомо чёрный адрес, и если бы запрос ушёл,
        // ответ пришёл бы не быстрее таймаута в 2.5 с.
        FreshEnv();
        ModInit.conf.xsmartApi = "http://192.0.2.1:9140";   // TEST-NET-1, пакеты туда не доходят

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = (Task<(string, string)>)Access.Call("AdminHistoryXsmartTitle", 99, "1");
        var (title, _) = await task;
        sw.Stop();

        Assert.Null(title);
        Assert.True(sw.ElapsedMilliseconds < 500, $"гейт не сработал: ушёл запрос, {sw.ElapsedMilliseconds} мс");
    }

    // ══ постеры ═══════════════════════════════════════════════════════════

    [Theory]
    // снаружи: адрес недостижим из локалки — обязан стать своим относительным
    [InlineData("https://tv.d1versy.com:9443/tmdb/img/t/p/w300/a.jpg?uid=x", "/tmdb/img/t/p/w300/a.jpg")]
    // изнутри: тот же путь, но с прибитым хостом и подписью
    [InlineData("http://192.168.87.24:9118/tmdb/img/t/p/w300/a.jpg?uid=x", "/tmdb/img/t/p/w300/a.jpg")]
    // прямой TMDB: браузер админки не должен ходить в интернет
    [InlineData("https://image.tmdb.org/t/p/w300/a.jpg", "/tmdb/img/t/p/w300/a.jpg")]
    // постер jut.su с хостом — режем до корневого пути
    [InlineData("http://192.168.87.24:9118/qdl/jut/poster?slug=abc", "/qdl/jut/poster?slug=abc")]
    // уже относительный — не трогаем
    [InlineData("/qdl/jut/poster?slug=abc", "/qdl/jut/poster?slug=abc")]
    public void A_poster_url_is_rewritten_to_this_server(string img, string expect)
    {
        var card = new JObject { ["img"] = img };
        Assert.Equal(expect, (string)Access.Call("AdminHistoryPoster", card));
    }

    [Fact]
    public void An_empty_img_falls_back_to_poster_path()
    {
        // 20 боевых карточек лежат именно так: img пустой, а poster_path живой.
        var card = new JObject { ["img"] = "", ["poster_path"] = "/abc.jpg" };
        Assert.Equal("/tmdb/img/t/p/w300/abc.jpg", (string)Access.Call("AdminHistoryPoster", card));

        var nothing = new JObject { ["img"] = "" };
        Assert.Equal("", (string)Access.Call("AdminHistoryPoster", nothing));
    }

    [Fact]
    public void The_writer_uid_is_lifted_out_and_left_out_of_the_url()
    {
        string img = "http://192.168.87.24:9118/tmdb/img/t/p/w300/a.jpg?uid=sajnp6ml";

        Assert.Equal("sajnp6ml", (string)Access.Call("AdminHistoryWriterUid", img));
        Assert.DoesNotContain("uid=", (string)Access.Call("AdminHistoryPoster", new JObject { ["img"] = img }));
        Assert.Equal("", (string)Access.Call("AdminHistoryWriterUid", "/qdl/jut/poster?slug=abc"));
    }

    [Fact]
    public void A_jut_card_is_not_labelled_a_movie()
    {
        // Слепок jut.su несёт только id/title/img — признаков типа там нет. Эвристика
        // «не сериал ⇒ фильм» подписывала бы каждое аниме фильмом.
        var jut = new JObject { ["id"] = "jut:abc", ["title"] = "Аниме", ["source"] = "jutsu" };
        Assert.Equal("", (string)Access.Call("AdminHistoryType", jut));

        Assert.Equal("movie", (string)Access.Call("AdminHistoryType", new JObject { ["release_date"] = "2020-01-01" }));
        Assert.Equal("tv", (string)Access.Call("AdminHistoryType", new JObject { ["first_air_date"] = "2020-01-01" }));
    }

    // ══ карточки ══════════════════════════════════════════════════════════

    [Fact]
    public async Task Cards_follow_the_history_order_and_bookmarks_stay_out()
    {
        FreshEnv();
        Register(UidA);

        SeedBookmarks(UidA, new JObject
        {
            ["history"] = new JArray(2, 1),                        // порядок истории — свежие первыми
            ["book"] = new JArray(3),
            ["card"] = new JArray(Tv(1, "Первый"), Tv(2, "Второй"), Tv(3, "Закладка"))
        });

        var r = await QbitController.AdminHistory(UidA);
        var cards = (JArray)r["cards"];

        Assert.Equal(new[] { "Второй", "Первый" }, cards.Select(c => (string)c["title"]).ToArray());
        Assert.DoesNotContain("Закладка", cards.Select(c => (string)c["title"]));
        Assert.Equal(2, (int)r["counts"]["history"]);
    }

    [Fact]
    public async Task A_history_id_without_a_card_still_gets_a_row()
    {
        // Счётчик обязан сойтись с тем, что видит сам пользователь в Lampa.
        FreshEnv();
        Register(UidA);
        SeedBookmarks(UidA, new JObject { ["history"] = new JArray(1, 999), ["card"] = new JArray(Tv(1, "Первый")) });

        var cards = (JArray)(await QbitController.AdminHistory(UidA))["cards"];

        Assert.Equal(2, cards.Count);
        Assert.Equal("999", (string)cards[1]["id"]);
        Assert.Equal("", (string)cards[1]["title"]);
    }

    [Fact]
    public async Task Numeric_and_string_ids_join_the_same_way()
    {
        // В боевом card[] айди лежат и числом, и строкой в одном массиве.
        FreshEnv();
        Register(UidA);
        SeedBookmarks(UidA, new JObject
        {
            ["history"] = new JArray(125988, "jut:abc"),
            ["card"] = new JArray(Tv("125988", "Укрытие"),
                                  new JObject { ["id"] = "jut:abc", ["title"] = "Аниме", ["source"] = "jutsu" })
        });

        var cards = (JArray)(await QbitController.AdminHistory(UidA))["cards"];

        Assert.Equal(new[] { "Укрытие", "Аниме" }, cards.Select(c => (string)c["title"]).ToArray());
    }

    [Fact]
    public async Task The_writer_of_each_card_is_reported_when_it_is_known()
    {
        FreshEnv();
        Register(UidA);
        Perms.Rename(UidA, "Мак Оли");

        SeedBookmarks(UidA, new JObject
        {
            ["history"] = new JArray(1, 2),
            ["card"] = new JArray(Tv(1, "Со штампом"),
                                  new JObject { ["id"] = 2, ["title"] = "Без штампа", ["img"] = "/qdl/jut/poster?slug=a" })
        });

        var cards = (JArray)(await QbitController.AdminHistory(UidA))["cards"];

        Assert.Equal(UidA, (string)cards[0]["byUid"]);
        Assert.Equal("Мак Оли", (string)cards[0]["byName"]);
        Assert.Equal("", (string)cards[1]["byUid"]);
    }

    // ══ чья это история ═══════════════════════════════════════════════════

    [Fact]
    public async Task A_grouped_device_reads_the_group_key()
    {
        FreshEnv();
        Register(UidA);
        Register(UidB);

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, true);
        Groups.Join(gid, UidB, true);

        SeedBookmarks(gid, new JObject { ["history"] = new JArray(1), ["card"] = new JArray(Tv(1, "Общее")) });

        var r = await QbitController.AdminHistory(UidA);

        Assert.Equal(gid, (string)r["scope"]["key"]);
        Assert.Equal("group", (string)r["scope"]["kind"]);
        Assert.Equal("Семья", (string)r["scope"]["groupName"]);
        Assert.Equal(2, ((JArray)r["scope"]["members"]).Count);
        Assert.Equal("Общее", (string)((JArray)r["cards"])[0]["title"]);
    }

    [Fact]
    public async Task The_groups_killswitch_shows_the_personal_history_and_says_so()
    {
        // 🔴 Groups.Resolve уважает киллсвитч, а Groups.GroupOf — нет. Когда группы выключили,
        // сервер уже пишет в личный ключ; показать «историю группы» значило бы соврать.
        FreshEnv();
        Register(UidA);

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, true);

        SeedBookmarks(gid, new JObject { ["history"] = new JArray(1), ["card"] = new JArray(Tv(1, "Общее")) });
        SeedBookmarks(UidA, new JObject { ["history"] = new JArray(2), ["card"] = new JArray(Tv(2, "Личное")) });

        ModInit.conf.groupsEnabled = false;
        Groups.ResetForConfigReload();

        var r = await QbitController.AdminHistory(UidA);

        Assert.Equal(UidA, (string)r["scope"]["key"]);
        Assert.Equal("device", (string)r["scope"]["kind"]);
        Assert.False((bool)r["scope"]["groupsEnabled"]);
        Assert.Equal(gid, (string)r["scope"]["gid"]);           // общая история никуда не делась
        Assert.Equal("Личное", (string)((JArray)r["cards"])[0]["title"]);
    }

    [Fact]
    public async Task A_group_id_typed_by_hand_is_treated_as_a_group()
    {
        // В списке устройств такой ссылки нет, но адрес руками набрать можно, и ответ
        // «не в группе» про саму группу выглядел бы поломкой.
        FreshEnv();
        Register(UidA);

        string gid = Groups.Create("Семья");
        Groups.Join(gid, UidA, true);
        SeedBookmarks(gid, new JObject { ["history"] = new JArray(1), ["card"] = new JArray(Tv(1, "Общее")) });

        var r = await QbitController.AdminHistory(gid);

        Assert.Equal(gid, (string)r["scope"]["key"]);
        Assert.Equal("group", (string)r["scope"]["kind"]);
    }

    [Fact]
    public async Task An_ungrouped_device_reads_its_own_key()
    {
        FreshEnv();
        Register(UidA);
        SeedBookmarks(UidA, new JObject { ["history"] = new JArray(1), ["card"] = new JArray(Tv(1, "Личное")) });

        var r = await QbitController.AdminHistory(UidA);

        Assert.Equal(UidA, (string)r["scope"]["key"]);
        Assert.Equal("device", (string)r["scope"]["kind"]);
        Assert.Empty((JArray)r["scope"]["members"]);
        Assert.Null(r["scope"]["personal"].Type == JTokenType.Null ? null : r["scope"]["personal"]);
    }

    // ══ отказы и пустота ══════════════════════════════════════════════════

    [Fact]
    public async Task An_unknown_uid_has_no_history_to_show()
    {
        FreshEnv();
        Assert.Null(await QbitController.AdminHistory("zzzzzzzz"));
        Assert.Null(await QbitController.AdminHistory(""));
        Assert.Null(await QbitController.AdminHistory(null));
    }

    [Fact]
    public async Task An_empty_history_is_an_answer_not_an_error()
    {
        FreshEnv();
        Register(UidA);

        var r = await QbitController.AdminHistory(UidA);

        Assert.Empty((JArray)r["cards"]);
        Assert.Empty(Plays(r));
        Assert.Equal(0, (int)r["counts"]["history"]);
    }

    [Fact]
    public async Task A_missing_database_is_an_empty_page_not_a_crash()
    {
        // Модуль Sync на этом сервере ещё ни разу не поднимался — баз просто нет.
        // ⚠️ Тест стережёт ПАРУ страховок, а не каждую по отдельности: Mode=ReadOnly на
        // несуществующем файле бросает, и это ловят и File.Exists перед открытием, и catch внутри.
        // Негативный прогон это подтвердил: снятие любой одной строки тест не краснит, снятие
        // обеих — краснит. Так и задумано, но знать об этом полезно.
        string cache = TestEnv.FreshCache();
        ModInit.conf.groupsEnabled = true;
        QbitController.DbDirOverride = Path.Combine(cache, "no-database-here");
        Register(UidA);

        var r = await QbitController.AdminHistory(UidA);

        Assert.Empty((JArray)r["cards"]);
        Assert.False((bool)r["db"]["sync"]);
        Assert.False((bool)r["db"]["timecode"]);
    }

    // ══ 🔴 главный инвариант: ручка ничего не пишет ════════════════════════

    [Fact]
    public async Task The_history_api_never_writes_to_the_databases()
    {
        // Владелец просил страницу, которая только показывает. Кроме этого теста, ничто не мешает
        // будущей правке взять OpenDb вместо OpenDbRo и тихо начать писать в чужие базы.
        FreshEnv();
        Register(UidA);

        SeedBookmarks(UidA, new JObject { ["history"] = new JArray(1), ["card"] = new JArray(Tv(1, "Кино")) });
        Road(UidA, "qdl_t1", "i1", 50, 30, "2026-08-30 10:00:00");

        var files = new[] { "Sync.sql", "TimeCode.sql" }.Select(n => new FileInfo(Db(n))).ToArray();
        var before = files.Select(f => (f.Length, f.LastWriteTimeUtc)).ToArray();

        await Task.Delay(1100);   // мельче секунды mtime на NTFS не различает
        await QbitController.AdminHistory(UidA);
        await QbitController.AdminHistory(UidA);

        for (int i = 0; i < files.Length; i++)
        {
            var f = new FileInfo(files[i].FullName);
            Assert.Equal(before[i].Length, f.Length);
            Assert.Equal(before[i].LastWriteTimeUtc, f.LastWriteTimeUtc);
        }

        // И никаких хвостов журнала: read-only соединение не создаёт ни -wal, ни -journal.
        Assert.False(File.Exists(Db("Sync.sql-wal")));
        Assert.False(File.Exists(Db("TimeCode.sql-wal")));
    }

    [Fact]
    public void A_read_only_connection_refuses_to_write()
    {
        FreshEnv();

        using var db = (SqliteConnection)Access.Call("OpenDbRo", Db("Sync.sql"));
        using var cmd = db.CreateCommand();
        cmd.CommandText = "insert into bookmarks(user, data, updated) values('x','{}','now')";

        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("readonly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ══ контроллер ════════════════════════════════════════════════════════

    static D1VAdminController Controller(bool marker = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("192.168.87.24:9118");
        ctx.Request.Method = "GET";
        ctx.Features.Set(new RequestModel { IP = "192.168.87.5", IsLocalRequest = true });
        if (marker) ctx.Request.Headers["X-D1V-Admin"] = "1";

        return new D1VAdminController { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    static int StatusOf(ActionResult r) => r switch
    {
        StatusCodeResult s => s.StatusCode,
        ObjectResult o => o.StatusCode ?? 200,
        ContentResult => 200,
        _ => 200,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public async Task A_degenerate_uid_is_refused_rather_than_defaulted(string uid)
    {
        FreshEnv();
        Assert.Equal(400, StatusOf(await Controller().DeviceHistory(uid)));
    }

    [Fact]
    public async Task An_unknown_device_is_a_404()
    {
        FreshEnv();
        Assert.Equal(404, StatusOf(await Controller().DeviceHistory("zzzzzzzz")));
    }

    [Fact]
    public async Task A_read_only_request_needs_no_csrf_marker()
    {
        // SameOrigin стережёт мутации. Требовать заголовок на GET — значит сломать переход
        // по обычной ссылке из списка устройств.
        FreshEnv();
        Register(UidA);

        var res = await Controller(marker: false).DeviceHistory(UidA);

        Assert.Equal(200, StatusOf(res));
        Assert.Contains("\"scope\"", Assert.IsType<ContentResult>(res).Content);
    }

    [Fact]
    public async Task The_history_api_is_never_cached()
    {
        FreshEnv();
        Register(UidA);

        var c = Controller();
        await c.DeviceHistory(UidA);

        Assert.Contains("no-cache", (string)c.Response.Headers["Cache-Control"], StringComparison.OrdinalIgnoreCase);
    }
}
