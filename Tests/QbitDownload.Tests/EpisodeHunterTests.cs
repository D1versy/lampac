using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>Чистая логика охоты за сериями (EpisodeHunter): гейты, инвентаризация, замещение.</summary>
public class EpisodeHunterTests
{
    static readonly DateTime Now = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    const string MainHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string DonorHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public EpisodeHunterTests() => TestEnv.EnsureConf();

    static JToken File(int index, string name, double progress = 0, long size = 1_500_000_000)
        => new JObject { ["index"] = index, ["name"] = name, ["progress"] = progress, ["size"] = size };

    // ── TitleCoversEp (tri-state по названию раздачи) ────────────────────
    [Theory]
    [InlineData("Сериал [1-8 из 12] WEB-DL", 1, 9, DonorCover.No)]
    [InlineData("Сериал [1-8 из 12] WEB-DL", 1, 8, DonorCover.Yes)]
    [InlineData("Тайтл [09 из 24]", 1, 9, DonorCover.Yes)]
    [InlineData("Сериал (Серия 9) 1080p", 1, 9, DonorCover.Yes)]
    [InlineData("Сериал (Серия 9) 1080p", 1, 10, DonorCover.No)]
    [InlineData("Сериал 2 сезон WEB-DL", 2, 9, DonorCover.Maybe)]
    [InlineData("Сериал 1 сезон WEB-DL", 2, 9, DonorCover.No)]
    [InlineData("Сериал [2 сезон, 1-8 из 12]", 2, 9, DonorCover.No)]
    public void TitleCoversEp_Verdicts(string title, int season, int ep, DonorCover expected)
        => Assert.Equal(expected, HunterAccess.TitleCoversEp(title, season, ep));

    // ── вес серии ────────────────────────────────────────────────────────
    [Fact]
    public void EpSize_RemuxRejected_AnimeAccepted()
    {
        long gb = 1024L * 1024 * 1024;
        Assert.False(HunterAccess.EpSizeOk(40 * gb, 150, 8));                          // ремукс-одиночка 40 ГБ
        Assert.True(HunterAccess.EpSizeOk(HunterAccess.EstimateEpBytes(40 * gb, 12), 150, 8));   // пак 40 ГБ / 12 серий ≈ 3.3 ГБ
        Assert.True(HunterAccess.EpSizeOk(300L * 1024 * 1024, 150, 8));                // аниме 720p ~300 МБ
        Assert.False(HunterAccess.EpSizeOk(100L * 1024 * 1024, 150, 8));               // обрезок
    }

    // ── ComputeWanted: только вперёд от максимума ────────────────────────
    [Fact]
    public void ComputeWanted_OnlyForward()
    {
        Assert.Equal(new List<int> { 9, 10, 11, 12 }, HunterAccess.ComputeWanted(new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8 }, 12));
        Assert.Equal(new List<int> { 1, 2, 3 }, HunterAccess.ComputeWanted(new HashSet<int>(), 3));
        Assert.Empty(HunterAccess.ComputeWanted(new HashSet<int> { 12 }, 12));
        Assert.Empty(HunterAccess.ComputeWanted(new HashSet<int> { 3, 8 }, 5));   // «дырки» в середине не охотим
    }

    // ── инвентаризация ───────────────────────────────────────────────────
    [Fact]
    public void InventoryEps_MainAndDonors()
    {
        var mainFiles = new JArray(
            File(0, "Show S02E01.mkv"), File(1, "Show S02E02.mkv"),
            File(2, "Show S01E05.mkv"),          // чужой сезон — не в инвентарь
            File(3, "Show NCOP.mkv"),            // экстра
            File(4, "readme.txt"));
        var donors = new JArray(new JObject
        {
            ["hash"] = DonorHash,
            ["eps"] = new JArray(new JObject { ["epkey"] = "s2e9", ["season"] = 2, ["ep"] = 9, ["fileIndex"] = 4, ["status"] = "hunted" })
        });
        var inv = HunterAccess.InventoryEps(mainFiles, donors, 2);
        Assert.Equal(new HashSet<int> { 1, 2, 9 }, inv);
    }

    [Fact]
    public void DominantSeason_ByMajority()
    {
        var files = new JArray(File(0, "Show S02E01.mkv"), File(1, "Show S02E02.mkv"), File(2, "Show S01E01.mkv"));
        Assert.Equal(2, HunterAccess.DominantSeason(files));
        Assert.Equal(0, HunterAccess.DominantSeason(new JArray(File(0, "Show 01.mkv"))));   // сезона нет
    }

    // ── строгий гейт имени (коллизия «Лаки» ↔ «Счастливчик Люк / Лаки Люк / Lucky Luke») ──
    [Theory]
    [InlineData("Лаки / Lucky [2024, WEB-DL 1080p]", true)]              // тот же сериал
    [InlineData("Lucky | Лаки [2024]", true)]
    [InlineData("Лаки. Сезон 1 / Lucky. Season 1 [2024]", true)]         // сезон в сегменте не мешает
    [InlineData("Счастливчик Люк / Лаки Люк / Lucky Luke [1984]", false)] // ЧУЖОЙ мультсериал — отсев
    [InlineData("Лаки Люк (Lucky Luke) 1x03", false)]                    // «лакилюк» ≠ «лаки»
    public void NameMatchesSeries_KillsCollision(string title, bool expected)
        => Assert.Equal(expected, HunterAccess.NameMatchesSeries(title, "лаки", "lucky"));

    [Fact]
    public void NameMatchesSeries_NoContext_Passes()
        => Assert.True(HunterAccess.NameMatchesSeries("Что угодно", null, null));

    [Fact]
    public void FilterDonorCandidates_RejectsWrongShow_ByName()
    {
        // мультсериал проходит по сезону/сидам/весу, но имя не совпадает → НЕ донор
        var scored = new JArray(
            Cand("Счастливчик Люк / Лаки Люк / Lucky Luke [1984, Сезон 1, 1-8 из 8] 1080p", 10),
            Cand("Лаки / Lucky [2024, 1-8 из 8] 1080p", 10));
        var h = HunterAccess.MakeHuntCtx(MainHash, 1, new[] { MainHash }, null, 3, 1080, 150, 8, "лаки", "lucky");
        var res = HunterAccess.FilterDonorCandidates(scored, h);
        Assert.Single(res);
        Assert.StartsWith("Лаки / Lucky", res[0].Value<string>("title"));
    }

    // ── гейты кандидатов ─────────────────────────────────────────────────
    static JObject Cand(string title, int sid, string magnet = null, string parselink = "http://t/parsemagnet?id=1",
                        int quality = 1080, long sizeBytes = 12_000_000_000, double score = 50)
        => new JObject { ["title"] = title, ["sid"] = sid, ["magnet"] = magnet, ["parselink"] = parselink, ["quality"] = quality, ["sizeBytes"] = sizeBytes, ["score"] = score };

    [Fact]
    public void FilterDonorCandidates_Gates()
    {
        string knownMagnet = "magnet:?xt=urn:btih:" + MainHash;
        var scored = new JArray(
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10),                                        // ок
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 1),                                         // мало сидов
            Cand("Сериал [2 сезон, 1-9 из 12] 720p", 10, quality: 720),                           // ниже donorMinQuality
            Cand("Сериал [2 сезон, 1-9 из 12]", 10, quality: 0),                                  // качество неизвестно — гейт пропускаем
            Cand("Сериал [1 сезон, 1-12 из 12] 1080p", 10),                                       // чужой сезон
            Cand("Сериал 1080p WEB-DL", 10),                                                      // сезон не заявлен, охотим 2-й — риск
            Cand("Сериал [2 сезон, Серия 9] Remux 1080p", 10, sizeBytes: 40L * 1024 * 1024 * 1024), // 40 ГБ на серию
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, magnet: knownMagnet, parselink: null)   // это сама основная
        );
        var h = HunterAccess.MakeHuntCtx(MainHash, 2, new[] { MainHash }, null, 3, 1080, 150, 8);
        var res = HunterAccess.FilterDonorCandidates(scored, h);
        Assert.Equal(2, res.Count);   // «ок» + «качество неизвестно»
        Assert.All(res, t => Assert.Equal(10, t.Value<int>("sid")));
    }

    [Fact]
    public void FilterDonorCandidates_Blacklist()
    {
        var scored = new JArray(Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, parselink: "http://t/parsemagnet?id=666"));
        var h = HunterAccess.MakeHuntCtx(MainHash, 2, new[] { MainHash }, new[] { "http://t/parsemagnet?id=666" }, 3, 1080, 150, 8);
        Assert.Empty(HunterAccess.FilterDonorCandidates(scored, h));
    }

    [Fact]
    public void OrderByCover_YesBeforeMaybe_ByScore()
    {
        var yesLow = Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, score: 40);
        var yesHigh = Cand("Сериал [2 сезон, 1-10 из 12] 1080p", 10, score: 80);
        var maybe = Cand("Сериал 2 сезон WEB-DL 1080p", 10, score: 99);
        var res = HunterAccess.OrderByCover(new List<JObject> { maybe, yesLow, yesHigh }, 2, new List<int> { 9 });
        Assert.Equal(80, res[0].Value<double>("score"));
        Assert.Equal(40, res[1].Value<double>("score"));
        Assert.Equal(99, res[2].Value<double>("score"));   // Maybe — в хвосте, несмотря на скор
    }

    // ── подтверждение по файлам ──────────────────────────────────────────
    [Fact]
    public void FindEpFiles_MatchesWanted_RejectsRange()
    {
        var files = new JArray(
            File(0, "Show S02E09.mkv"), File(1, "Show S02E10.mkv"), File(2, "Show S02E08.mkv"),
            File(3, "Show 01-08.mkv"),           // RANGE — отвергаем
            File(4, "Show S01E09.mkv"));         // чужой сезон
        var found = HunterAccess.FindEpFiles(files, 2, new List<int> { 9, 10 }, null);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.ep == 9 && f.index == 0 && f.epkey == "s2e9");
        Assert.Contains(found, f => f.ep == 10 && f.index == 1);
    }

    [Fact]
    public void FindEpFiles_SingleFileTakesEpFromTitle()
    {
        var files = new JArray(File(0, "Show.mkv"));
        var found = HunterAccess.FindEpFiles(files, 2, new List<int> { 9 }, "Сериал (Серия 9) 1080p");
        Assert.Single(found);
        Assert.Equal(9, found[0].ep);
        Assert.Equal(2, found[0].season);
    }

    // ── объединённый плейлист ────────────────────────────────────────────
    [Fact]
    public void MergeEpisodeFiles_DonorFillsGap_MainWinsWhenDone()
    {
        var mainFiles = new JArray(
            File(0, "Show S02E01.mkv", 1.0), File(1, "Show S02E02.mkv", 1.0),
            File(2, "Show S02E03.mkv", 0.4),     // качается
            File(3, "Show NCOP.mkv", 1.0));      // экстра
        var donor = new JObject
        {
            ["hash"] = DonorHash,
            ["eps"] = new JArray(
                new JObject { ["epkey"] = "s2e3", ["season"] = 2, ["ep"] = 3, ["fileIndex"] = 5, ["status"] = "hunted" },
                new JObject { ["epkey"] = "s2e4", ["season"] = 2, ["ep"] = 4, ["fileIndex"] = 6, ["status"] = "hunted" },
                new JObject { ["epkey"] = "s2e1", ["season"] = 2, ["ep"] = 1, ["fileIndex"] = 7, ["status"] = "hunted" })
        };
        var donorFiles = new JArray(
            File(5, "Other S02E03.mkv", 1.0), File(6, "Other S02E04.mkv", 0.7), File(7, "Other S02E01.mkv", 1.0));

        var merged = HunterAccess.MergeEpisodeFiles(MainHash, mainFiles,
            new List<(JObject, JArray)> { (donor, donorFiles) }, "t125988", 2);

        // e1: основная докачана → main; e3: основная качается → донор; e4: только донор; экстра в хвосте
        var e1 = merged.First(x => x.Value<int?>("episode") == 1);
        Assert.Equal("main", e1.Value<string>("source"));
        var e3 = merged.First(x => x.Value<int?>("episode") == 3);
        Assert.Equal("donor", e3.Value<string>("source"));
        Assert.Equal(DonorHash, e3.Value<string>("hash"));
        var e4 = merged.First(x => x.Value<int?>("episode") == 4);
        Assert.Equal("donor", e4.Value<string>("source"));
        Assert.Equal("t125988:s2e4", e4.Value<string>("tl"));
        Assert.Equal("NCOP", System.IO.Path.GetFileNameWithoutExtension(merged.Last().Value<string>("name")).Split(' ').Last());
        // порядок: e1..e4 затем экстры
        var eps = merged.Where(x => x.Value<int?>("episode") != null).Select(x => x.Value<int>("episode")).ToList();
        Assert.Equal(eps.OrderBy(x => x).ToList(), eps);
    }

    // ── замещение основной ───────────────────────────────────────────────
    static JObject WatchItem(params JObject[] donors)
        => new JObject { ["hash"] = MainHash, ["title"] = "Сериал", ["donors"] = new JArray(donors.Cast<object>().ToArray()) };

    static JObject Donor(string addedAt, string status = "hunted")
        => new JObject
        {
            ["hash"] = DonorHash, ["link"] = "http://t/parsemagnet?id=2", ["addedAt"] = addedAt,
            ["eps"] = new JArray(new JObject { ["epkey"] = "s2e9", ["season"] = 2, ["ep"] = 9, ["fileIndex"] = 4, ["status"] = status })
        };

    [Fact]
    public void PlanReplacements_MainDone_DropsAndDeletesDonor()
    {
        var item = WatchItem(Donor(Now.AddDays(-1).ToString("o")));
        var mainFiles = new JArray(File(0, "Show S02E09.mkv", 1.0));
        var donorFiles = new Dictionary<string, JArray> { [DonorHash] = new JArray(File(4, "Other S02E09.mkv", 1.0)) };
        var actions = HunterAccess.PlanReplacements(mainFiles, item, donorFiles, Now, 7);
        Assert.Contains(actions, a => a.kind == "drop-file" && a.donorHash == DonorHash && a.fileIndex == 4);
        Assert.Contains(actions, a => a.kind == "delete-donor" && a.donorHash == DonorHash);
    }

    [Fact]
    public void PlanReplacements_MainIncomplete_NoActions()
    {
        var item = WatchItem(Donor(Now.AddDays(-1).ToString("o")));
        var mainFiles = new JArray(File(0, "Show S02E09.mkv", 0.5));
        var donorFiles = new Dictionary<string, JArray> { [DonorHash] = new JArray(File(4, "Other S02E09.mkv", 0.9)) };
        Assert.Empty(HunterAccess.PlanReplacements(mainFiles, item, donorFiles, Now, 7));
    }

    [Fact]
    public void PlanReplacements_StaleStuckDonor_Dead()
    {
        var item = WatchItem(Donor(Now.AddDays(-9).ToString("o")));
        var mainFiles = new JArray();
        var donorFiles = new Dictionary<string, JArray> { [DonorHash] = new JArray(File(4, "Other S02E09.mkv", 0.2)) };
        var actions = HunterAccess.PlanReplacements(mainFiles, item, donorFiles, Now, 7);
        Assert.Contains(actions, a => a.kind == "dead-donor" && a.donorHash == DonorHash);
    }

    [Fact]
    public void PlanReplacements_DonorGone_Forget()
    {
        var item = WatchItem(Donor(Now.AddDays(-1).ToString("o")));
        var donorFiles = new Dictionary<string, JArray> { [DonorHash] = null };   // удалили извне
        var actions = HunterAccess.PlanReplacements(new JArray(), item, donorFiles, Now, 7);
        Assert.Contains(actions, a => a.kind == "forget-donor" && a.donorHash == DonorHash);
    }

    [Fact]
    public void PlanReplacements_AllReplaced_DeleteDonor()
    {
        var item = WatchItem(Donor(Now.AddDays(-1).ToString("o"), status: "replaced"));
        var donorFiles = new Dictionary<string, JArray> { [DonorHash] = new JArray() };
        var actions = HunterAccess.PlanReplacements(new JArray(), item, donorFiles, Now, 7);
        Assert.Contains(actions, a => a.kind == "delete-donor");
    }

    // ── blacklist ────────────────────────────────────────────────────────
    [Fact]
    public void Blacklist_TtlPrune_AndKeys()
    {
        // BlacklistAdd штампует until от РЕАЛЬНОГО UtcNow, поэтому и «сейчас» здесь реальное
        // (фиксированная дата делала тест бомбой замедленного действия — краснел сам по себе).
        var now = DateTime.UtcNow;
        var item = new JObject();
        HunterAccess.BlacklistAdd(item, DonorHash, "http://t/parsemagnet?id=3", "no-episode", 30);
        HunterAccess.BlacklistAdd(item, "cccccccccccccccccccccccccccccccccccccccc", null, "meta-timeout", 1);

        var keys = HunterAccess.BlacklistKeys(item);
        Assert.Contains(DonorHash, keys);
        Assert.Contains("http://t/parsemagnet?id=3", keys);

        HunterAccess.PruneBlacklist(item, now.AddDays(2));   // meta-timeout (1 день) протух
        keys = HunterAccess.BlacklistKeys(item);
        Assert.Contains(DonorHash, keys);
        Assert.DoesNotContain("cccccccccccccccccccccccccccccccccccccccc", keys);

        HunterAccess.PruneBlacklist(item, now.AddDays(40));   // всё протухло
        Assert.Empty(HunterAccess.BlacklistKeys(item));
    }

    // ── старые watch-записи (обратная совместимость) ─────────────────────
    [Fact]
    public void OldWatchRecord_NoNewFields_Valid()
    {
        var old = JObject.Parse("{\"hash\":\"" + MainHash + "\",\"link\":\"magnet:?xt=urn:btih:" + MainHash + "\",\"query\":\"x\",\"id\":1,\"title\":\"t\"}");
        Assert.Equal(0, old.Value<int?>("stale") ?? 0);
        Assert.Null(old["donors"]);
        Assert.Empty(HunterAccess.PlanReplacements(new JArray(), old, new Dictionary<string, JArray>(), Now, 7));
        HunterAccess.PruneBlacklist(old, Now);   // не бросает
    }
}
