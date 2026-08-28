using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Reflection-шлюз к приватной половине SeasonWatch.cs — тот же приём, что Access/HunterAccess.
/// Внутренние (internal) члены контура видны напрямую: тестовая сборка ЛИНКУЕТ исходники модуля.
/// </summary>
static class SeasonAccess
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Static;

    public static Task<string> SeasonGrab(HttpClient c, JObject rec, int target, JObject cand)
        => (Task<string>)Access.Call("SeasonGrab", c, rec, target, cand);

    public static Task<HashSet<int>> SeasonsOnDisk(HttpClient c, int id)
        => (Task<HashSet<int>>)Access.Call("SeasonsOnDisk", c, id);

    /// <summary>Тот же production-стек Qbit(), что берёт контур (под ним сидит FakeQbit).</summary>
    public static Task<HttpClient> Qbit() => (Task<HttpClient>)Access.Call("Qbit");

    /// <summary>Подсадить ответ TMDB в процессный кэш — контур в сеть тогда не пойдёт.</summary>
    public static void SeedTmdb(int id, string status, params (int num, string air, int eps)[] seasons)
    {
        var info = new QbitController.TmdbSeriesInfo { status = status, totalSeasons = seasons.Count(s => s.num > 0) };
        foreach (var (num, air, eps) in seasons)
            info.seasons.Add(new QbitController.TmdbSeasonRow
            {
                number = num,
                air = string.IsNullOrEmpty(air) ? (DateTime?)null : DateTime.ParseExact(air, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                episodes = eps
            });
        InfoCache()[id] = (info, DateTime.UtcNow);
    }

    /// <summary>Сколько серий сезона «вышло» — тот же кэш, что читает AiredEpisodes.</summary>
    public static void SeedAired(int id, int season, int aired)
        => AiredCache()[id + ":" + season] = (aired, DateTime.UtcNow);

    public static void DropCaches()
    {
        QbitController.SeasonTmdbCacheDrop();
        AiredCache().Clear();
    }

    static ConcurrentDictionary<int, (QbitController.TmdbSeriesInfo, DateTime)> InfoCache()
        => (ConcurrentDictionary<int, (QbitController.TmdbSeriesInfo, DateTime)>)
           C.GetField("_seriesInfoCache", SF).GetValue(null);

    static ConcurrentDictionary<string, (int, DateTime)> AiredCache()
        => (ConcurrentDictionary<string, (int, DateTime)>)C.GetField("_airedCache", SF).GetValue(null);
}

/// <summary>
/// «Жду следующий сезон» (qdl 2.79, SeasonWatch.cs) — подписка на СЕРИАЛ, а не на раздачу.
///
/// Жалоба владельца по карточке 229564 («Телохранители»): оба сезона скачаны и завершены,
/// и следить дальше не за чем — /qdl/watch привязано к infohash конкретной раздачи, а сезон,
/// которого ещё нет, выбрать нельзя в принципе.
///
/// 🔒 Инварианты, которые здесь заперты:
///   • TMDB не ответил → контур не делает НИЧЕГО (fail-closed; у AiredEpisodes наоборот);
///   • спецсезон 0 и сезон без даты выхода целью не бывают никогда;
///   • сезон, уже лежащий на диске, проматывается молча — без уведомления и без скачивания;
///   • кандидат: строгий гейт имени, СТРОГО односезонная раздача, не наш топик, не сидящий btih;
///   • контур ТОЛЬКО ДОБАВЛЯЕТ — ни одного вызова /torrents/delete;
///   • dry-прогон не пишет ни строки;
///   • killswitch seasonWatch=false выключает контур целиком.
/// </summary>
public class SeasonWatchTests
{
    const int ID = 229564;
    const string H1 = "1111111111111111111111111111111111111111";   // сезон 1
    const string H2 = "2222222222222222222222222222222222222222";   // сезон 2
    const string H3 = "3333333333333333333333333333333333333333";   // новый сезон
    const string BTIH3 = "3333333333333333333333333333333333333333";

    const string N1 = "Телохранители / Сезон: 1 / Серии: 1-16 из 16 [2023, Комедия, HDTV 1080p]";
    const string N2 = "Телохранители / Сезон: 2 / Серии: 1-16 из 16 [2025, Комедия, HDTV 1080p]";

    static readonly DateTime Today = new DateTime(2026, 8, 28);

    static void Fresh()
    {
        TestEnv.FreshCache();
        SeasonAccess.DropCaches();
        Access.SaveWatch(new JArray());
        using (var db = new SqlContext()) db.Database.EnsureCreated();   // свой cachePath — своя qdl.db
        // 🔴 Дохлый порт вместо 9118. Контур ходит в TMDB и в индексатор через СВОЙ loopback, а на
        // машине разработки по 127.0.0.1:9118 отвечает боевой контейнер — без этого кейс «TMDB не
        // ответил» зеленел бы на настоящем ответе TMDB и не проверял ровно то, ради чего написан.
        TestEnv.SetListen(1, "127.0.0.1");
    }

    static void Meta(string hash, int id, string mediaType = "tv", string title = "Телохранители", int seasons = 2)
        => Access.SaveMeta(hash, new JObject
        {
            ["id"] = id, ["media_type"] = mediaType, ["title"] = title,
            ["original_title"] = "Telohraniteli", ["year"] = "2023", ["number_of_seasons"] = seasons
        });

    static void Link(string hash, string link, int season)
    {
        string dir = Path.Combine(ModInit.conf.cachePath, "links");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, hash + ".json"), new JObject
        {
            ["link"] = link,
            ["query"] = "Телохранители",
            ["ctx"] = new JObject { ["title"] = "Телохранители", ["title_original"] = "Telohraniteli", ["year"] = 2023, ["is_serial"] = 2, ["season"] = season }
        }.ToString(Newtonsoft.Json.Formatting.None));
    }

    static string Torrent(string hash, string name, long addedOn)
        => $"{{\"hash\":\"{hash}\",\"name\":{new JValue(name).ToString(Newtonsoft.Json.Formatting.None)},\"progress\":1.0,"
         + $"\"state\":\"queuedUP\",\"size\":100,\"save_path\":\"/downloads\",\"content_path\":\"/downloads/x\","
         + $"\"added_on\":{addedOn},\"completion_on\":0}}";

    static QbitController Ctrl()
        => new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    static JObject JsonOf(IActionResult r) => JObject.FromObject(Assert.IsType<JsonResult>(r).Value);

    static JObject Cand(string title, int sid = 20, string parselink = null, string magnet = null)
        => new JObject
        {
            ["title"] = title,
            ["sid"] = sid,
            ["magnet"] = magnet,
            ["parselink"] = parselink,
            ["tracker"] = "rutracker",
            ["quality"] = 1080,
            ["score"] = 120.0
        };

    static QbitController.SeasonPickCtx Ctx(int target, int minSeeds = 3, params string[] selfTopics)
    {
        var h = new QbitController.SeasonPickCtx
        {
            target = target,
            minSeeds = minSeeds,
            titleNorm = Shared.Services.Utilities.SearchNameTo.Convert("Телохранители"),
            originalNorm = Shared.Services.Utilities.SearchNameTo.Convert("Telohraniteli")
        };
        foreach (string t in selfTopics) h.selfTopics.Add(t);
        return h;
    }

    // ── чистая логика: какой сезон считаем вышедшим ───────────────────────────

    [Fact]
    public void Спецсезон_и_сезон_без_даты_целью_не_бывают()
    {
        var seasons = new List<QbitController.TmdbSeasonRow>
        {
            new QbitController.TmdbSeasonRow { number = 0, air = new DateTime(2023, 1, 1) },   // «Спецматериалы»
            new QbitController.TmdbSeasonRow { number = 3, air = null },                       // анонсирован, даты нет
            new QbitController.TmdbSeasonRow { number = 4, air = new DateTime(2027, 1, 1) }    // ещё не вышел
        };

        Assert.Empty(QbitController.SeasonTargets(seasons, 3, Today));
        // спецсезон отсекается СВОИМ гейтом, а не тем, что он ниже from: «Спецматериалы» —
        // это не «сезон 0», а свалка трейлеров и бэкстейджа, качать её никто не просил
        Assert.Empty(QbitController.SeasonTargets(seasons, 0, Today));
    }

    [Fact]
    public void Целями_становятся_вышедшие_сезоны_начиная_с_from_по_возрастанию()
    {
        var seasons = new List<QbitController.TmdbSeasonRow>
        {
            new QbitController.TmdbSeasonRow { number = 4, air = new DateTime(2026, 6, 1) },
            new QbitController.TmdbSeasonRow { number = 2, air = new DateTime(2025, 2, 24) },
            new QbitController.TmdbSeasonRow { number = 3, air = new DateTime(2026, 1, 1) }
        };

        Assert.Equal(new[] { 3, 4 }, QbitController.SeasonTargets(seasons, 3, Today));
    }

    [Fact]
    public void From_считается_от_самого_старшего_скачанного_сезона()
    {
        Assert.Equal(3, QbitController.SeasonWaitFrom(new[] { 1, 2 }, 2));
        Assert.Equal(4, QbitController.SeasonWaitFrom(new[] { 3, 1 }, 2));   // порядок не важен
        // сезоны раздач не разобрались («Сезоны 1-3», кривое имя) — опираемся на мету
        Assert.Equal(3, QbitController.SeasonWaitFrom(new int[0], 2));
        // и никогда не ждём «сезон 1»: он либо есть, либо его качают кнопкой «Скачать»
        Assert.Equal(2, QbitController.SeasonWaitFrom(new int[0], 0));
    }

    // ── чистая логика: отбор раздачи ──────────────────────────────────────────

    [Fact]
    public void Чужой_сериал_с_похожим_именем_кандидатом_не_становится()
    {
        // тот самый случай, ради которого писан NameMatchesSeries: «Лаки» ↔ «Лаки Люк»
        var scored = new JArray { Cand("Телохранители поневоле / Сезон: 3 [2026, HDTV 1080p]", parselink: "http://h/t?id=1") };
        var h = Ctx(3);

        Assert.Null(QbitController.PickSeasonCandidate(scored, h));
        Assert.Contains("чужое имя", string.Join(",", h.drops));
    }

    [Fact]
    public void Мультисезонная_раздача_отвергается_даже_если_содержит_нужный_сезон()
    {
        // «1-3 сезоны» перекачали бы уже лежащие сезоны в ту же папку — контур обещал только добавлять
        var scored = new JArray { Cand("Телохранители (1-3 сезоны: 1-48 серии из 48) [2026, WEB-DL 1080p]", parselink: "http://h/t?id=1") };
        var h = Ctx(3);

        Assert.Null(QbitController.PickSeasonCandidate(scored, h));
        Assert.Contains("не сезон 3", string.Join(",", h.drops));
    }

    [Fact]
    public void Не_тот_сезон_мало_сидов_и_свой_топик_отсеиваются()
    {
        var scored = new JArray
        {
            Cand("Телохранители / Сезон: 2 / Серии: 1-16 из 16", parselink: "http://h/rutracker/parsemagnet?id=1"),
            Cand("Телохранители / Сезон: 3 / Серии: 1-8 из 16", sid: 1, parselink: "http://h/rutracker/parsemagnet?id=2"),
            Cand("Телохранители / Сезон: 3 / Серии: 1-16 из 16", parselink: "http://h/rutracker/parsemagnet?id=777")
        };
        // третий кандидат — перевыкладка НАШЕГО же топика: это работа re-grab, а не «новый сезон» (§AK шлюз 1)
        string selfTopic = (string)Access.Call("TopicKey", "http://other-host/rutracker/parsemagnet?id=777&apikey=zzz");
        var h = Ctx(3, 3, selfTopic);

        Assert.Null(QbitController.PickSeasonCandidate(scored, h));
        string drops = string.Join(", ", h.drops);
        Assert.Contains("не сезон 3", drops);
        Assert.Contains("мало сидов", drops);
        Assert.Contains("свой топик", drops);
    }

    [Fact]
    public void Раздача_которая_уже_сидит_в_qBit_повторно_не_добавляется()
    {
        string magnet = "magnet:?xt=urn:btih:" + BTIH3;
        var scored = new JArray { Cand("Телохранители / Сезон: 3 / Серии: 1-16 из 16", magnet: magnet) };
        var h = Ctx(3);
        h.knownHashes.Add(BTIH3);

        Assert.Null(QbitController.PickSeasonCandidate(scored, h));
        Assert.Contains("уже в qBit", string.Join(",", h.drops));
    }

    [Fact]
    public void Берётся_первая_прошедшая_гейты_раздача_а_не_перебор()
    {
        var scored = new JArray
        {
            Cand("Телохранители (1-3 сезоны) [2026]", parselink: "http://h/t?id=1"),                       // мультисезонная
            Cand("Телохранители / Сезон: 3 / Серии: 1-16 из 16 [2026, WEB-DL 1080p]", sid: 40, parselink: "http://h/t?id=2"),
            Cand("Телохранители / Сезон: 3 / Серии: 1-16 из 16 [2026, HDTV 720p]", sid: 90, parselink: "http://h/t?id=3")
        };

        var got = QbitController.PickSeasonCandidate(scored, Ctx(3));
        Assert.NotNull(got);
        Assert.Contains("WEB-DL 1080p", got.Value<string>("title"));   // порядок выдачи (скор), а не сиды
    }

    // ── ручки ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Маркер_включается_на_сезон_следующий_за_скачанными()
    {
        Fresh();
        Meta(H1, ID); Meta(H2, ID);

        Access.SeedQbitFake(new FakeQbit()
            .Json("/api/v2/torrents/info", "[" + Torrent(H1, N1, 100) + "," + Torrent(H2, N2, 200) + "]")
            .BuildHandler());
        try
        {
            var r = JsonOf(await Ctrl().SeasonWatchAdd(H1));
            Assert.True(r.Value<bool>("success"));
            Assert.Equal(3, r.Value<int>("from"));
        }
        finally { Access.ResetQbitFake(); }

        var rec = Assert.Single(QbitController.SeasonLoad()).ToObject<JObject>();
        Assert.Equal(ID, rec.Value<int>("id"));
        Assert.Equal(3, rec.Value<int>("from"));
        Assert.Equal("grab", rec.Value<string>("mode"));
        Assert.Equal(3, QbitController.SeasonWaitMap()[ID]);
    }

    [Fact]
    public async Task Фильм_и_карточка_без_TMDB_маркер_не_получают()
    {
        Fresh();
        Meta(H1, ID, "movie");
        Meta(H2, 0);

        Assert.False(JsonOf(await Ctrl().SeasonWatchAdd(H1)).Value<bool>("success"));
        Assert.False(JsonOf(await Ctrl().SeasonWatchAdd(H2)).Value<bool>("success"));
        Assert.Empty(QbitController.SeasonLoad());
    }

    [Fact]
    public async Task Маркер_снимается_и_по_хешу_и_по_id()
    {
        Fresh();
        Meta(H1, ID);
        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", "[]").BuildHandler());
        try { await Ctrl().SeasonWatchAdd(H1); } finally { Access.ResetQbitFake(); }
        Assert.Single(QbitController.SeasonLoad());

        Assert.True(JsonOf(Ctrl().SeasonWatchRemove(H1)).Value<bool>("success"));
        Assert.Empty(QbitController.SeasonLoad());
    }

    [Fact]
    public async Task Карточка_Загрузок_несёт_маркер_клиенту()
    {
        Fresh();
        Meta(H1, ID); Meta(H2, ID);
        Access.SeedQbitFake(new FakeQbit()
            .Json("/api/v2/torrents/info", "[" + Torrent(H1, N1, 100) + "," + Torrent(H2, N2, 200) + "]")
            .BuildHandler());
        try
        {
            await Ctrl().SeasonWatchAdd(H1);
            var list = JArray.Parse(Assert.IsType<ContentResult>(await Ctrl().List()).Content);

            // склеенная карточка одна, и маркер висит именно на ней (декорация ПОСЛЕ склейки)
            var card = Assert.Single(list).ToObject<JObject>();
            Assert.Equal(3, (card["seasonWait"] as JObject)?.Value<int>("from"));
        }
        finally { Access.ResetQbitFake(); }
    }

    [Fact]
    public async Task Удаление_последней_карточки_снимает_маркер_а_одного_сезона_нет()
    {
        Fresh();
        Meta(H1, ID); Meta(H2, ID);
        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", "[]").BuildHandler());
        try { await Ctrl().SeasonWatchAdd(H1, from: 3); } finally { Access.ResetQbitFake(); }

        Access.PurgeCache(H1);
        Assert.Single(QbitController.SeasonLoad());   // второй сезон ещё лежит — ждём дальше

        Access.PurgeCache(H2);
        Assert.Empty(QbitController.SeasonLoad());    // сериала не осталось — ожидание бессмысленно
    }

    // ── тик ───────────────────────────────────────────────────────────────────

    static JObject Rec(int from) => new JObject
    {
        ["id"] = ID, ["title"] = "Телохранители", ["from"] = from, ["mode"] = "grab",
        ["ctx"] = new JObject { ["title"] = "Телохранители", ["title_original"] = "Telohraniteli", ["year"] = 2023 }
    };

    static void SeedRecord(int from)
    {
        Directory.CreateDirectory(ModInit.conf.cachePath);
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "season-watch.json"),
            new JArray { Rec(from) }.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public async Task TMDB_не_ответил_контур_не_делает_ничего()
    {
        Fresh();
        Meta(H1, ID);
        SeedRecord(3);
        // кэш пуст, а сети в тестах нет → TmdbSeriesSeasons вернёт null

        var rep = await QbitController.SeasonWatchTick();

        Assert.Equal("tmdb-down", Assert.Single(rep).Value<string>("decision"));
        // 🔴 fail-closed: ни lastRun, ни продвижения from — иначе следующий тик соврал бы
        var rec = Assert.Single(QbitController.SeasonLoad()).ToObject<JObject>();
        Assert.Equal(3, rec.Value<int>("from"));
        Assert.Null(rec.Value<string>("lastRun"));
    }

    [Fact]
    public async Task Сезон_ещё_не_вышел_тик_молчит()
    {
        Fresh();
        Meta(H1, ID);
        SeedRecord(3);
        // ровно случай «Телохранителей»: TMDB знает 2 сезона и говорит Ended
        SeasonAccess.SeedTmdb(ID, "Ended", (1, "2023-08-21", 16), (2, "2025-02-24", 16));

        var rep = await QbitController.SeasonWatchTick();

        var line = Assert.Single(rep);
        Assert.Equal("waiting", line.Value<string>("decision"));
        Assert.Equal("Ended", line.Value<string>("status"));
        // 🔴 «Ended» подписку НЕ гасит: сезон может появиться и у закрытого по мнению TMDB сериала
        var rec = Assert.Single(QbitController.SeasonLoad()).ToObject<JObject>();
        Assert.Equal(3, rec.Value<int>("from"));
        Assert.NotNull(rec.Value<string>("lastRun"));
    }

    [Fact]
    public async Task Сезон_с_датой_но_без_вышедших_серий_не_цель()
    {
        Fresh();
        Meta(H1, ID);
        SeedRecord(3);
        SeasonAccess.SeedTmdb(ID, "Returning Series", (1, "2023-08-21", 16), (3, "2026-01-01", 16));
        SeasonAccess.SeedAired(ID, 3, 0);   // дата у сезона есть, эфира ещё нет

        var line = Assert.Single(await QbitController.SeasonWatchTick());
        Assert.Equal("waiting", line.Value<string>("decision"));
        Assert.Equal(3, line.Value<int>("target"));
        Assert.Equal(0, line.Value<int>("aired"));
    }

    [Fact]
    public async Task Скачанный_руками_сезон_проматывается_молча()
    {
        Fresh();
        Meta(H1, ID); Meta(H3, ID);
        SeedRecord(3);
        SeasonAccess.SeedTmdb(ID, "Returning Series", (1, "2023-08-21", 16), (3, "2026-01-01", 16));
        SeasonAccess.SeedAired(ID, 3, 16);

        Access.SeedQbitFake(new FakeQbit()
            .Json("/api/v2/torrents/info", "[" + Torrent(H1, N1, 100)
                + "," + Torrent(H3, "Телохранители / Сезон: 3 / Серии: 1-16 из 16", 300) + "]")
            .BuildHandler());
        JToken line;
        try { line = Assert.Single(await QbitController.SeasonWatchTick()); }
        finally { Access.ResetQbitFake(); }

        Assert.Equal("waiting", line.Value<string>("decision"));
        Assert.Equal(1, line.Value<int>("alreadyHave"));
        Assert.Equal(4, Assert.Single(QbitController.SeasonLoad()).Value<int>("from"));   // ждём уже четвёртый
        Assert.Empty(NotiLabels());   // 🔴 про «вышел 3 сезон» никто не уведомлял: он у нас и так есть
    }

    [Fact]
    public async Task Киллсвитч_выключает_контур_целиком()
    {
        Fresh();
        Meta(H1, ID);
        SeedRecord(3);
        SeasonAccess.SeedTmdb(ID, "Returning Series", (3, "2026-01-01", 16));
        SeasonAccess.SeedAired(ID, 3, 16);

        ModInit.conf.seasonWatch = false;
        try
        {
            Assert.Equal("disabled", Assert.Single(await QbitController.SeasonWatchTick()).Value<string>("decision"));
            Assert.Equal(3, Assert.Single(QbitController.SeasonLoad()).Value<int>("from"));
        }
        finally { ModInit.conf.seasonWatch = true; }
    }

    [Fact]
    public async Task Сухой_прогон_не_пишет_ни_строки()
    {
        Fresh();
        Meta(H1, ID);
        SeedRecord(3);
        SeasonAccess.SeedTmdb(ID, "Returning Series", (3, "2026-01-01", 16));
        SeasonAccess.SeedAired(ID, 3, 16);

        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", "[]").BuildHandler());
        try { await QbitController.SeasonWatchTick(dry: true); }
        finally { Access.ResetQbitFake(); }

        var rec = Assert.Single(QbitController.SeasonLoad()).ToObject<JObject>();
        Assert.Equal(3, rec.Value<int>("from"));
        Assert.Null(rec.Value<string>("lastRun"));
        Assert.Null(rec.Value<JObject>("seen"));
        Assert.Empty(NotiLabels());
    }

    [Fact]
    public async Task Ручка_check_отдаёт_читаемый_отчёт()
    {
        // 🔴 Боевой баг выкатки 2.79: отчёт уходил через Json(new { items = rep }), то есть системным
        // System.Text.Json, который про JToken не знает — вместо решений приезжала матрёшка пустых
        // массивов, и «сухой прогон» становился бесполезен ровно там, где он и нужен.
        Fresh();
        Meta(H1, ID);
        SeedRecord(3);
        SeasonAccess.SeedTmdb(ID, "Ended", (1, "2023-08-21", 16), (2, "2025-02-24", 16));

        var res = await Ctrl().SeasonWatchCheck(dry: 1);
        var body = JObject.Parse(Assert.IsType<ContentResult>(res).Content);

        Assert.True(body.Value<bool>("dry"));
        var line = Assert.Single((JArray)body["items"]);
        Assert.Equal("waiting", line.Value<string>("decision"));
        Assert.Equal("Телохранители", line.Value<string>("title"));
    }

    // ── постановка раздачи ────────────────────────────────────────────────────

    [Fact]
    public async Task Постановка_пишет_указатель_включает_слежение_и_ничего_не_удаляет()
    {
        Fresh();
        Meta(H1, ID);
        var fake = new FakeQbit()
            .Text("/torrents/add", "Ok.")
            .Json("/torrents/files", "[]")
            .Json("/api/v2/torrents/info", "[]");
        var handler = fake.BuildHandler();
        Access.SeedQbitFake(handler);
        string got;
        try
        {
            using var c = await SeasonAccess.Qbit();
            got = await SeasonAccess.SeasonGrab(c, Rec(3), 3,
                Cand("Телохранители / Сезон: 3 / Серии: 1-16 из 16", magnet: "magnet:?xt=urn:btih:" + BTIH3));
        }
        finally { Access.ResetQbitFake(); }

        Assert.Equal(BTIH3, got);

        // указатель на раздачу — фундамент слежения и охоты
        var lj = JObject.Parse(File.ReadAllText(Path.Combine(ModInit.conf.cachePath, "links", BTIH3 + ".json")));
        Assert.Equal(3, (lj["ctx"] as JObject).Value<int>("season"));
        Assert.Equal(2, (lj["ctx"] as JObject).Value<int>("is_serial"));

        // штатное слежение заведено — дальше серии добирает обычная охота
        var w = Assert.Single(Access.LoadWatch()).ToObject<JObject>();
        Assert.Equal(BTIH3, w.Value<string>("hash"));

        // 🔴 КРАСНАЯ ЛИНИЯ: контур только добавляет
        Assert.DoesNotContain(fake.Requests, r => (r.RequestUri?.ToString() ?? "").Contains("/torrents/delete"));
    }

    [Fact]
    public async Task Дубликат_от_qBit_доводит_донора_до_основной_категории()
    {
        // §AK красная линия №1: на дубликате qBit НЕ применяет переданную категорию. Раздачу мог
        // уже качать донором охотник — оставить её донорской значит обречь на удаление С ФАЙЛАМИ.
        Fresh();
        Meta(H1, ID);
        Access.SaveWatch(new JArray
        {
            new JObject
            {
                ["hash"] = H1, ["link"] = "http://h/t?id=1", ["id"] = ID, ["title"] = "Телохранители",
                ["donors"] = new JArray { new JObject { ["hash"] = BTIH3, ["title"] = "донор" } }
            }
        });

        var fake = new FakeQbit()
            .Text("/torrents/add", "Conflict", HttpStatusCode.Conflict)
            .Text("/torrents/setCategory", "Ok.")
            .Text("/torrents/removeTags", "Ok.")
            .Json("/torrents/files", "[{\"index\":0,\"name\":\"a.mkv\",\"size\":10,\"progress\":1,\"priority\":0}]")
            .Text("/torrents/filePrio", "Ok.")
            .Text("/torrents/start", "Ok.")
            .Text("/torrents/resume", "Ok.")
            .Json("/api/v2/torrents/info", "[]");
        Access.SeedQbitFake(fake.BuildHandler());
        try
        {
            using var c = await SeasonAccess.Qbit();
            Assert.Equal(BTIH3, await SeasonAccess.SeasonGrab(c, Rec(3), 3,
                Cand("Телохранители / Сезон: 3 / Серии: 1-16 из 16", magnet: "magnet:?xt=urn:btih:" + BTIH3)));
        }
        finally { Access.ResetQbitFake(); }

        Assert.Contains(fake.Requests, r => (r.RequestUri?.ToString() ?? "").Contains("/torrents/setCategory"));
        // донорская запись снята — иначе контур замещения снял бы «донора» с файлами
        var w = Access.LoadWatch().OfType<JObject>().First(x => x.Value<string>("hash") == H1);
        Assert.Empty((JArray)w["donors"]);
        Assert.DoesNotContain(fake.Requests, r => (r.RequestUri?.ToString() ?? "").Contains("/torrents/delete"));
    }

    // ── вспомогательное ───────────────────────────────────────────────────────

    static List<string> NotiLabels()
    {
        using var db = new SqlContext();
        db.Database.EnsureCreated();   // cachePath у каждого кейса свой — база создаётся заново
        return db.noti.Select(x => x.label).ToList();
    }
}
