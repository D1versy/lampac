using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Регрессия инцидента 2026-07-25 («Укрытие» / Silo): охота взяла донором ПЕРЕРЕГИСТРАЦИЮ ТОГО ЖЕ
/// топика, за которым следила основная. Дальше цепочка: re-grab пере-резолвил основную в этот же
/// infohash → add вернул «дубликат» и категорию не сменил (торрент остался донорским) → контур
/// замещения сравнил торрент сам с собой («серия замещена основной») → delete-donor снёс сериал
/// С ФАЙЛАМИ. Здесь закрыт каждый из четырёх шлюзов по отдельности.
/// </summary>
public class DonorSelfCollisionTests
{
    static readonly DateTime Now = new DateTime(2026, 7, 25, 4, 34, 0, DateTimeKind.Utc);
    const string MainHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string DonorHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    // боевая ссылка watch-записи «Укрытия» и та же самая, но пришедшая из выдачи поиска с другим ключом
    const string SelfLink = "http://127.0.0.1:9118/rutracker/parsemagnet?id=6878482&apikey=1";
    const string SelfLinkOtherKey = "http://127.0.0.1:9118/rutracker/parsemagnet?id=6878482&apikey=zzz";

    public DonorSelfCollisionTests()
    {
        TestEnv.EnsureConf();
        ModInit.conf.category = "lampa";
        ModInit.conf.donorCategory = "";          // → "lampa-donor"
        ModInit.conf.downloadsPath = "/downloads";
    }

    static JToken File(int index, string name, double progress = 0, long size = 1_500_000_000)
        => new JObject { ["index"] = index, ["name"] = name, ["progress"] = progress, ["size"] = size };

    static JObject Cand(string title, string parselink, int sid = 10, long sizeBytes = 12_000_000_000)
        => new JObject { ["title"] = title, ["sid"] = sid, ["parselink"] = parselink, ["quality"] = 1080, ["sizeBytes"] = sizeBytes, ["score"] = 50 };

    // ── 1. TopicKey: один топик трекера при разном apikey ────────────────
    [Fact]
    public void TopicKey_SameTopic_IgnoresApikey()
        => Assert.Equal(HunterAccess.TopicKey(SelfLink), HunterAccess.TopicKey(SelfLinkOtherKey));

    [Fact]
    public void TopicKey_DifferentTopic_Differs()
        => Assert.NotEqual(HunterAccess.TopicKey(SelfLink),
                           HunterAccess.TopicKey("http://127.0.0.1:9118/rutracker/parsemagnet?id=9999999&apikey=1"));

    [Fact]
    public void TopicKey_Magnet_ByBtih()
    {
        Assert.Equal("btih:" + DonorHash, HunterAccess.TopicKey("magnet:?xt=urn:btih:" + DonorHash + "&tr=http%3A%2F%2Fbt3.t-ru.org"));
        Assert.Null(HunterAccess.TopicKey(null));
        Assert.Null(HunterAccess.TopicKey("не ссылка"));
    }

    // ── 2. Шлюз охоты: перерегистрация СВОЕЙ раздачи — не донор ──────────
    [Fact]
    public void FilterDonorCandidates_RejectsOwnTopicReregistration()
    {
        // тот же топик (новые серии → НОВЫЙ infohash, knownHashes бессильны) и чужая раздача
        var scored = new JArray(
            Cand("Укрытие / Silo [3 сезон, 1-4 из 10] WEB-DL 1080p", SelfLinkOtherKey),
            Cand("Укрытие / Silo [3 сезон, 1-4 из 10] WEB-DLRip", "http://127.0.0.1:9118/rutor/parsemagnet?id=777"));

        var h = HunterAccess.MakeHuntCtx(MainHash, 3, new[] { MainHash }, null, 3, 1080, 150, 8,
                                         "укрытие", "silo", selfLink: SelfLink);
        var res = HunterAccess.FilterDonorCandidates(scored, h);

        Assert.Single(res);
        Assert.Equal("http://127.0.0.1:9118/rutor/parsemagnet?id=777", res[0].Value<string>("parselink"));
    }

    [Fact]
    public void FilterDonorCandidates_NoSelfLink_KeepsOldBehaviour()
    {
        var scored = new JArray(Cand("Укрытие / Silo [3 сезон, 1-4 из 10] WEB-DL 1080p", SelfLinkOtherKey));
        var h = HunterAccess.MakeHuntCtx(MainHash, 3, new[] { MainHash }, null, 3, 1080, 150, 8, "укрытие", "silo");
        Assert.Single(HunterAccess.FilterDonorCandidates(scored, h));
    }

    // ── 3. Шлюз замещения: донор == основная → ничего не удаляем ─────────
    [Fact]
    public void PlanReplacements_DonorIsMain_ForgetsWithoutDeleting()
    {
        // ровно состояние «Укрытия» в 07:34 MSK: re-grab перевёл основную на infohash донора,
        // донорская запись осталась; серия E04 в торренте докачана
        var item = new JObject
        {
            ["hash"] = MainHash,
            ["donors"] = new JArray(new JObject
            {
                ["hash"] = MainHash,
                ["addedAt"] = Now.AddHours(-4).ToString("o"),
                ["eps"] = new JArray(new JObject { ["epkey"] = "s3e4", ["season"] = 3, ["ep"] = 4, ["fileIndex"] = 3, ["status"] = "hunted" })
            })
        };
        var mainFiles = new JArray(
            File(0, "Silo.S03E01.1080p.mkv", progress: 1),
            File(3, "Silo.S03E04.1080p.mkv", progress: 1));
        var donorFiles = new Dictionary<string, JArray> { [MainHash] = mainFiles };

        var actions = HunterAccess.PlanReplacements(mainFiles, item, donorFiles, Now, 7);

        Assert.Single(actions);
        Assert.Equal("forget-donor", actions[0].kind);                 // не drop-file и не delete-donor!
        Assert.DoesNotContain(actions, a => a.kind == "delete-donor");
        Assert.DoesNotContain(actions, a => a.kind == "drop-file");
    }

    [Fact]
    public void PlanReplacements_RealDonor_StillReplaced()
    {
        // контроль: обычный донор (другой infohash) замещается как раньше
        var item = new JObject
        {
            ["hash"] = MainHash,
            ["donors"] = new JArray(new JObject
            {
                ["hash"] = DonorHash,
                ["addedAt"] = Now.AddHours(-4).ToString("o"),
                ["eps"] = new JArray(new JObject { ["epkey"] = "s3e4", ["season"] = 3, ["ep"] = 4, ["fileIndex"] = 0, ["status"] = "hunted" })
            })
        };
        var mainFiles = new JArray(File(3, "Silo.S03E04.1080p.mkv", progress: 1));
        var donorFiles = new Dictionary<string, JArray> { [DonorHash] = new JArray(File(0, "Silo.S03E04.WEB.mkv", progress: 1)) };

        var actions = HunterAccess.PlanReplacements(mainFiles, item, donorFiles, Now, 7);

        Assert.Contains(actions, a => a.kind == "drop-file" && a.donorHash == DonorHash);
        Assert.Contains(actions, a => a.kind == "delete-donor" && a.donorHash == DonorHash);
    }

    // ── 4. Шлюз файлов: общая папка донора и основной ────────────────────
    [Theory]
    [InlineData("/downloads/Silo (Season 3)", "/downloads/Silo (Season 3)", true)]      // та же папка
    [InlineData("/downloads/Silo (Season 3)", "/downloads/Silo (Season 3)/e4.mkv", true)] // файл внутри
    [InlineData("/downloads/Silo (Season 3)", "/downloads/Lucky S01", false)]           // разные раздачи
    [InlineData("/downloads", "/downloads/Silo (Season 3)", false)]                     // общий корень — не пересечение
    [InlineData(null, "/downloads/Silo", false)]
    public void PathsOverlap_Verdicts(string a, string b, bool expected)
        => Assert.Equal(expected, HunterAccess.PathsOverlap(a, b));

    [Fact]
    public async Task QbitDeleteDonorSafe_SharedFolderWithMain_KeepsFiles()
    {
        string delBody = null;
        var fake = new FakeQbit()
            .Route("/torrents/delete", req => { delBody = req.Content.ReadAsStringAsync().Result; return FakeHttpMessageHandler.Text(""); })
            .Json("info?hashes=" + DonorHash, "[{\"hash\":\"" + DonorHash + "\",\"category\":\"lampa-donor\",\"content_path\":\"/downloads/Silo (Season 3) WEB-DL 1080p\"}]")
            .Json("info?hashes=" + MainHash, "[{\"hash\":\"" + MainHash + "\",\"category\":\"lampa\",\"content_path\":\"/downloads/Silo (Season 3) WEB-DL 1080p\"}]");
        var c = fake.Build();

        await HunterAccess.QbitDeleteDonorSafe(c, DonorHash, MainHash);

        Assert.NotNull(delBody);
        Assert.Contains("deleteFiles=false", delBody);   // торрент сняли, файлы сериала целы
    }

    [Fact]
    public async Task QbitDeleteDonorSafe_OwnFolder_DeletesWithFiles()
    {
        string delBody = null;
        var fake = new FakeQbit()
            .Route("/torrents/delete", req => { delBody = req.Content.ReadAsStringAsync().Result; return FakeHttpMessageHandler.Text(""); })
            .Json("info?hashes=" + DonorHash, "[{\"hash\":\"" + DonorHash + "\",\"category\":\"lampa-donor\",\"content_path\":\"/downloads/Silo.S03E04.WEB\"}]")
            .Json("info?hashes=" + MainHash, "[{\"hash\":\"" + MainHash + "\",\"category\":\"lampa\",\"content_path\":\"/downloads/Silo (Season 3) WEB-DL 1080p\"}]");
        var c = fake.Build();

        await HunterAccess.QbitDeleteDonorSafe(c, DonorHash, MainHash);

        Assert.Contains("deleteFiles=true", delBody);    // отдельная раздача — штатная уборка донора
    }

    [Fact]
    public async Task QbitDeleteDonorSafe_DonorHashIsMain_NoRequestsAtAll()
    {
        var fake = new FakeQbit()
            .Json("/torrents/info", "[{\"hash\":\"" + MainHash + "\",\"category\":\"lampa-donor\"}]")
            .Text("/torrents/delete", "");
        var c = fake.Build();

        await HunterAccess.QbitDeleteDonorSafe(c, MainHash, MainHash);

        Assert.DoesNotContain(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/delete"));
    }

    // ── 5. Промоушен донора, ставшего основной ───────────────────────────
    [Fact]
    public async Task PromoteDonorToMain_SetsCategory_RemovesTag_EnablesAllFiles()
    {
        string catBody = null, tagBody = null, prioBody = null;
        var fake = new FakeQbit()
            .Route("/torrents/setCategory", req => { catBody = req.Content.ReadAsStringAsync().Result; return FakeHttpMessageHandler.Text(""); })
            .Route("/torrents/removeTags", req => { tagBody = req.Content.ReadAsStringAsync().Result; return FakeHttpMessageHandler.Text(""); })
            .Route("/torrents/filePrio", req => { prioBody = req.Content.ReadAsStringAsync().Result; return FakeHttpMessageHandler.Text(""); })
            .Json("/torrents/files", "[{\"index\":0,\"name\":\"e1.mkv\"},{\"index\":1,\"name\":\"e2.mkv\"},{\"index\":2,\"name\":\"e3.mkv\"}]")
            .Text("/torrents/start", "");
        var c = fake.Build();

        Assert.True(await HunterAccess.PromoteDonorToMain(c, DonorHash));

        Assert.Contains("category=lampa", catBody);
        Assert.Contains("qdl-donor", tagBody);
        Assert.Contains("priority=1", prioBody);              // весь сезон обратно в загрузку…
        Assert.Contains("id=0%7C1%7C2", prioBody);            // …а не одна серия-цель донора
        Assert.Contains(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/start"));
    }

    [Fact]
    public async Task PromoteIfDonor_ByDonorRecord_PromotesAndDropsRefs()
    {
        var items = new List<JObject>
        {
            new JObject { ["hash"] = MainHash, ["title"] = "Укрытие", ["donors"] = new JArray(new JObject { ["hash"] = DonorHash }) },
            new JObject { ["hash"] = "cccccccccccccccccccccccccccccccccccccccc", ["donors"] = new JArray(new JObject { ["hash"] = DonorHash }) }
        };
        var fake = new FakeQbit()
            .Text("/torrents/setCategory", "").Text("/torrents/removeTags", "")
            .Json("/torrents/files", "[{\"index\":0,\"name\":\"e1.mkv\"}]")
            .Text("/torrents/filePrio", "").Text("/torrents/start", "");
        var c = fake.Build();

        Assert.True(await HunterAccess.PromoteIfDonor(c, DonorHash, items, "Укрытие"));

        Assert.Contains(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/setCategory"));
        Assert.All(items, m => Assert.Empty((JArray)m["donors"]));   // донорские записи сняты во ВСЕХ сериалах
    }

    [Fact]
    public async Task PromoteIfDonor_PlainNewTorrent_NoOp()
    {
        var items = new List<JObject> { new JObject { ["hash"] = MainHash, ["title"] = "Укрытие" } };
        var fake = new FakeQbit()
            .Json("/torrents/info", "[{\"hash\":\"" + DonorHash + "\",\"category\":\"lampa\"}]")
            .Text("/torrents/setCategory", "");
        var c = fake.Build();

        Assert.False(await HunterAccess.PromoteIfDonor(c, DonorHash, items, "Укрытие"));
        Assert.DoesNotContain(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/setCategory"));
    }

    [Fact]
    public void DropDonorRefs_RemovesOnlyMatching()
    {
        var items = new List<JObject>
        {
            new JObject { ["hash"] = MainHash, ["donors"] = new JArray(
                new JObject { ["hash"] = DonorHash },
                new JObject { ["hash"] = "cccccccccccccccccccccccccccccccccccccccc" }) }
        };
        Assert.True(HunterAccess.IsDonorRef(items, DonorHash.ToUpperInvariant()));
        Assert.Equal(1, HunterAccess.DropDonorRefs(items, DonorHash));
        Assert.Single((JArray)items[0]["donors"]);
        Assert.False(HunterAccess.IsDonorRef(items, DonorHash));
    }

    // ── 6. Статус add: дубликат отличим от нового торрента ───────────────
    [Fact]
    public async Task AddMagnetStatus_DistinguishesDuplicate()
    {
        var dup = FakeHttpMessageHandler.Client(_ => FakeHttpMessageHandler.Text("Conflict", HttpStatusCode.Conflict));
        Assert.Equal(QbitAddStatus.Duplicate, await HunterAccess.QbitAddMagnetStatus(dup, "magnet:?xt=urn:btih:" + DonorHash, "lampa"));

        var dupJson = FakeHttpMessageHandler.Client(_ => FakeHttpMessageHandler.Json("{\"success_count\":0,\"duplicate_count\":1}"));
        Assert.Equal(QbitAddStatus.Duplicate, await HunterAccess.QbitAddMagnetStatus(dupJson, "magnet:?xt=urn:btih:" + DonorHash, "lampa"));

        var ok = FakeHttpMessageHandler.Client(_ => FakeHttpMessageHandler.Text("Ok."));
        Assert.Equal(QbitAddStatus.Added, await HunterAccess.QbitAddMagnetStatus(ok, "magnet:?xt=urn:btih:" + DonorHash, "lampa"));

        var fail = FakeHttpMessageHandler.Client(_ => FakeHttpMessageHandler.Text("Fails.", HttpStatusCode.UnsupportedMediaType));
        Assert.Equal(QbitAddStatus.Failed, await HunterAccess.QbitAddMagnetStatus(fail, "magnet:?xt=urn:btih:" + DonorHash, "lampa"));
    }
}
