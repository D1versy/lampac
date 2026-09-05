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
    // qdl 2.107: мультисезонный пак — счётчик серий сквозной, серию внутри сезона не доказывает → Maybe
    // (раньше 27 ≥ 10 давало ложный Yes, пак сгорал в no-episode; 20 записей blacklist у «Укрытия»)
    [InlineData("Укрытие (Бункер) (1-3 сезон: 1-27 серии из 30) / Silo / 2022-2026 / ДБ, 6 x ПМ, АП, ЛМ, СТ / WEB-DL (1080p)", 3, 10, DonorCover.Maybe)]
    [InlineData("Дом дракона (1-3 сезоны: 1-20 серии из 26) / House of the Dragon / WEB-DL (1080p)", 3, 8, DonorCover.Maybe)]
    [InlineData("Укрытие (Бункер) (3 сезон: 1-10 серии из 10) / Silo / 2026 / ПМ (RuDub) / WEBRip", 3, 10, DonorCover.Yes)]
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
        Assert.Equal(2, res.Count);   // «ок» + «качество неизвестно» (старая политика: rejectUnknownQuality выключен в MakeHuntCtx)
        Assert.All(res, t => Assert.Equal(10, t.Value<int>("sid")));

        // 🔴 Политика 2026-09-04 (донором «Укрытия» стал 720×400 XviD с quality:0): неизвестное = ниже порога
        var strict = HunterAccess.MakeHuntCtx(MainHash, 2, new[] { MainHash }, null, 3, 1080, 150, 8, rejectUnknownQuality: true);
        var res2 = HunterAccess.FilterDonorCandidates(scored, strict);
        Assert.Single(res2);
        Assert.Equal("качество не распознано", HunterAccess.DropReason(scored[3] as JObject, strict));
    }

    // ── сезон донора (инцидент 2026-08-09, «Укрытие») ────────────────────
    const string SiloS02Title = "Укрытие (Бункер) (2 сезон: 1-10 серии из 10) / Silo / 2024 / ПМ (NewComers), СТ / WEB-DLRip | NewComers";

    [Fact]
    public void FilterDonorCandidates_KinozalOtherSeason_Rejected()
    {
        var scored = new JArray(Cand(SiloS02Title, 35));
        var h = HunterAccess.MakeHuntCtx(MainHash, 3, new[] { MainHash }, null, 3, 1080, 150, 8, "укрытие", "silo");
        Assert.Empty(HunterAccess.FilterDonorCandidates(scored, h));
        Assert.Equal("сезон", HunterAccess.DropReason(scored[0] as JObject, h));
    }

    [Theory]
    [InlineData("Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E07.avi", 2)]
    [InlineData("Silo (Season 3) WEB-DL 1080p/Silo.S03E01.mkv", 3)]
    [InlineData("Dogulwang.S01/Dogulwang.EP01.mkv", 1)]
    [InlineData("Silo.S01-S03.Complete/ep.mkv", 0)]   // неоднозначно — пусть решает сам файл
    [InlineData("Show.2024.1080p/ep.mkv", 0)]
    [InlineData("ep.mkv", 0)]                          // папки нет
    public void SeasonFromPath_Cases(string name, int expected)
        => Assert.Equal(expected, HunterAccess.SeasonFromPath(name));

    [Fact]
    public void FindEpFiles_ForeignSeason_FailClosed()
    {
        // ровно те файлы, что уехали в 3-й сезон «Укрытия»
        var files = new JArray(
            File(14, "Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E07.Rus.Eng.by.Сибиряк.avi", 1, 764_588_032),
            File(16, "Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E08.Rus.Eng.by.Сибиряк.avi", 1, 733_130_752));

        Assert.Equal(2, HunterAccess.DonorSeasons(files).Count == 1 ? 2 : 0);   // сезон донора однозначен
        Assert.Equal(2, HunterAccess.DonorSeason(files, SiloS02Title));

        var wanted = new List<int> { 7, 8, 9, 10 };
        Assert.Empty(HunterAccess.FindEpFiles(files, 3, wanted, null, 2));   // охотим 3-й — не берём ничего
        Assert.Equal(2, HunterAccess.FindEpFiles(files, 2, wanted, null, 2).Count);   // 2-й — берём обе
    }

    [Fact]
    public void FindEpFiles_UnknownSeason_S1_Works_S3_FailClosed()
    {
        var files = new JArray(File(0, "[Group] Show - 07.mkv", 1, 400_000_000));
        var wanted = new List<int> { 7 };
        // односезонники/аниме без маркеров — как раньше
        Assert.Single(HunterAccess.FindEpFiles(files, 1, wanted, null, 0));
        // не первый сезон и сезон ничем не подтверждён — не берём
        Assert.Empty(HunterAccess.FindEpFiles(files, 3, wanted, null, 0));
        // но если сезон донора подтверждён — берём и проставляем его
        var got = HunterAccess.FindEpFiles(files, 3, wanted, null, 3);
        Assert.Single(got);
        Assert.Equal(3, got[0].season);
        Assert.Equal("s3e7", got[0].epkey);
    }

    [Fact]
    public void FindEpFiles_MultiSeasonPack_TakesOnlyHuntedSeason()
    {
        var files = new JArray(
            File(0, "Silo.S01-S03/Silo.S02.E07.mkv", 1, 1_500_000_000),
            File(1, "Silo.S01-S03/Silo.S03.E07.mkv", 1, 1_500_000_000));
        var got = HunterAccess.FindEpFiles(files, 3, new List<int> { 7 }, null, 0);
        Assert.Single(got);
        Assert.Equal(1, got[0].index);
        Assert.Equal(3, got[0].season);
    }

    // ── MaxClaim: «серия существует» ≠ «отсюда можно качать» ─────────────
    [Fact]
    public void MaxClaim_CountsIdentityOnly_NotDonorEligibility()
    {
        // фикстуры «Великого расхитителя гробниц»: три кандидата с 5 сериями отсеяны как доноры
        var scored = new JArray(
            Cand("Великий расхититель гробниц [1 сезон, 5 из 12] 1080p", 17, parselink: "http://t/kinozal?id=2146746"),  // blacklist
            Cand("Великий расхититель гробниц [1 сезон, 5 из 12] 1080p", 29, parselink: "http://t/self?id=6882889"),     // свой топик
            Cand("Великий расхититель гробниц [1 сезон, 5 из 12] 720p", 8, quality: 720),                                 // качество
            Cand("Великий расхититель гробниц [1 сезон, 1 из 12] 1080p", 5, parselink: "http://t/other?id=9", sizeBytes: 2_000_000_000));   // единственный годный

        var h = HunterAccess.MakeHuntCtx(MainHash, 1, new[] { MainHash }, new[] { "http://t/kinozal?id=2146746" },
                                         3, 1080, 150, 8,
                                         Shared.Services.Utilities.SearchNameTo.Convert("Великий расхититель гробниц"), null,
                                         "http://t/self?id=6882889");

        Assert.Single(HunterAccess.FilterDonorCandidates(scored, h));
        Assert.Equal(1, HunterAccess.MaxClaim(HunterAccess.FilterDonorCandidates(scored, h)));   // как было — врало
        Assert.Equal(4, HunterAccess.ClaimCandidates(scored, h).Count);
        Assert.Equal(5, HunterAccess.MaxClaim(HunterAccess.ClaimCandidates(scored, h)));         // правда

        Assert.Equal(new[] { 3, 4, 5 }, HunterAccess.ComputeWanted(new HashSet<int> { 1, 2 }, 5));
        Assert.Equal(5, HunterAccess.SelfTopicClaim(scored, h));   // свой топик перевыложен → пора re-grab
    }

    // ── апгрейд донорской серии на раздачу получше ───────────────────────
    static JObject Donor(string hash, double score, int quality, params int[] eps)
    {
        var arr = new JArray();
        foreach (int e in eps)
            arr.Add(new JObject { ["epkey"] = "s1e" + e, ["season"] = 1, ["ep"] = e, ["fileIndex"] = e, ["status"] = "hunted" });
        return new JObject { ["hash"] = hash, ["link"] = "http://t/d?id=" + hash, ["score"] = score, ["quality"] = quality, ["eps"] = arr };
    }

    [Fact]
    public void ComputeUpgrades_PicksBetterRelease_SkipsEpisodesMainAlreadyHas()
    {
        var donors = new JArray(Donor(DonorHash, 60, 720, 5, 6));
        var better = Cand("Сериал [1 сезон, 1-6 из 12] 1080p", 40, parselink: "http://t/better?id=2", score: 90);
        var scored = new JArray(better);

        // E5 апгрейдим, E6 уже есть в ОСНОВНОЙ — её донором не трогаем (основная приоритетнее)
        var up = HunterAccess.ComputeUpgrades(donors, scored, new List<JObject> { better }, new HashSet<int> { 6 }, 1, 15);
        Assert.Equal(new[] { 5 }, up);

        // кандидат не лучше — апгрейда нет
        var same = Cand("Сериал [1 сезон, 1-6 из 12] 720p", 40, parselink: "http://t/same?id=3", quality: 720, score: 62);
        Assert.Empty(HunterAccess.ComputeUpgrades(donors, new JArray(same), new List<JObject> { same }, new HashSet<int>(), 1, 15));
    }

    // qdl 2.107: ранг качества относительно цели — единый компаратор (охота/апгрейд/уборка/показ)
    [Fact]
    public void ComputeUpgrades_ByQualityRank_NotScore()
    {
        // донор с quality:0 (XviD «WEBRip» без цифры) раньше был неуязвим (guard bquality>0) — теперь ранг 1000, худший
        var xvid = new JArray(Donor(DonorHash, 122, 0, 10));
        var cold = Cand("Silo.S03E10.1080p.ColdFilm.mkv", 2, parselink: null, magnet: "magnet:?xt=urn:btih:cccccccccccccccccccccccccccccccccccccccc", score: 107);
        Assert.Equal(new[] { 10 }, HunterAccess.ComputeUpgrades(xvid, new JArray(cold), new List<JObject> { cold }, new HashSet<int>(), 3, 15, 1080));

        // цель 1080: донор 1080 не апгрейдится ни на 2160 (выше цели), ни на 720 (ниже) — даже при +40 скора
        var d1080 = new JArray(Donor(DonorHash, 60, 1080, 5));
        var c2160 = Cand("Сериал [1 сезон, 1-6 из 12] 2160p", 40, parselink: "http://t/a?id=1", quality: 2160, score: 100);
        var c720 = Cand("Сериал [1 сезон, 1-6 из 12] 720p", 40, parselink: "http://t/b?id=2", quality: 720, score: 100);
        Assert.Empty(HunterAccess.ComputeUpgrades(d1080, new JArray(c2160, c720), new List<JObject> { c2160, c720 }, new HashSet<int>(), 1, 15, 1080));

        // равный ранг — только по +15 скора
        var c1080 = Cand("Сериал [1 сезон, 1-6 из 12] 1080p", 40, parselink: "http://t/c?id=3", quality: 1080, score: 76);
        Assert.Equal(new[] { 5 }, HunterAccess.ComputeUpgrades(d1080, new JArray(c1080), new List<JObject> { c1080 }, new HashSet<int>(), 1, 15, 1080));
        var c1080low = Cand("Сериал [1 сезон, 1-6 из 12] 1080p", 40, parselink: "http://t/d?id=4", quality: 1080, score: 70);
        Assert.Empty(HunterAccess.ComputeUpgrades(d1080, new JArray(c1080low), new List<JObject> { c1080low }, new HashSet<int>(), 1, 15, 1080));

        // цель 1080, донор 2160 → кандидат 1080 апгрейдит (ровно цель лучше «выше цели»)
        var d2160 = new JArray(Donor(DonorHash, 90, 2160, 5));
        Assert.Equal(new[] { 5 }, HunterAccess.ComputeUpgrades(d2160, new JArray(c1080low), new List<JObject> { c1080low }, new HashSet<int>(), 1, 15, 1080));

        // Maybe-пак — только при строго лучшем ранге; тот же файл, что у донора (подпись), — не апгрейд
        var pack = Cand("Сериал 1 сезон WEB-DL 1080p", 40, parselink: "http://t/p?id=5", quality: 1080, score: 100);
        Assert.Empty(HunterAccess.ComputeUpgrades(d1080, new JArray(pack), new List<JObject> { pack }, new HashSet<int>(), 1, 15, 1080));
        var d720 = new JArray(Donor(DonorHash, 60, 720, 5));
        Assert.Equal(new[] { 5 }, HunterAccess.ComputeUpgrades(d720, new JArray(pack), new List<JObject> { pack }, new HashSet<int>(), 1, 15, 1080));
        var sameFile = Cand("Silo.S01E05.1080p.ColdFilm.mkv", 5, parselink: null, magnet: "magnet:?xt=urn:btih:dddddddddddddddddddddddddddddddddddddddd", score: 100);
        sameFile["bm_files"] = new JArray(new JObject { ["name"] = "Silo.S01E05.1080p.ColdFilm.mkv", ["size"] = 2_352_351_365L });
        var donorSig = new HashSet<string> { HunterAccess.SigKey("Silo.S01E05.1080p.ColdFilm.mkv", 2_352_351_365L) };
        Assert.Empty(HunterAccess.ComputeUpgrades(d720, new JArray(sameFile), new List<JObject> { sameFile }, new HashSet<int>(), 1, 15, 1080, donorSig));
    }

    [Theory]
    [InlineData(1080, 1080, 0)]
    [InlineData(1440, 1080, 4)]
    [InlineData(2160, 1080, 11)]
    [InlineData(720, 1080, 280)]
    [InlineData(480, 1080, 400)]
    [InlineData(0, 1080, 1000)]
    [InlineData(2160, 2160, 0)]
    [InlineData(1080, 2160, 640)]   // ниже цели: 100 + (target − q)/2 — при цели 2160 1080 и 720 больше не один ранг
    [InlineData(720, 2160, 820)]
    [InlineData(240, 2160, 999)]    // кап 899 — всё равно лучше «не распознано» (1000)
    public void QualityRank_Order(int q, int target, int expected) => Assert.Equal(expected, HunterAccess.QualityRank(q, target));

    [Fact]
    public void DonorTargetQuality_MainOrFloor1080()
    {
        var conf = new ModuleConf();
        Assert.Equal(1080, HunterAccess.DonorTargetQuality(new JArray(File(0, "Silo (Season 3) WEB-DL 1080p/Silo.S03E01.1080p.WEB-DL.RGzsRutracker.mkv")), conf));
        Assert.Equal(2160, HunterAccess.DonorTargetQuality(new JArray(File(0, "Show.S01E01.2160p.mkv"), File(1, "Show.S01E02.2160p.mkv")), conf));
        Assert.Equal(1080, HunterAccess.DonorTargetQuality(new JArray(File(0, "Silo.s03e01.WEBRip.XviD.Rus.RuDub.tv.avi")), conf));   // основная 720×400/XviD → цель 1080, не потолок
        Assert.Equal(1080, HunterAccess.DonorTargetQuality(new JArray(File(0, "Show.S01E01.720p.mkv")), conf));                     // основная 720p → цель 1080
        conf.donorQualityTarget = 2160;
        Assert.Equal(2160, HunterAccess.DonorTargetQuality(new JArray(File(0, "Show.S01E01.720p.mkv")), conf));                     // явное перекрывает
    }

    [Fact]
    public void PlanReplacements_UpgradedDonor_DroppedOnlyWhenWinnerComplete()
    {
        var oldD = Donor(DonorHash, 60, 720, 5);
        var newD = Donor("cccccccccccccccccccccccccccccccccccccccc", 95, 1080, 5);
        var item = new JObject { ["hash"] = MainHash, ["donors"] = new JArray(oldD, newD) };

        var oldFiles = new JArray(File(5, "old/Show.S01E05.avi", 1));
        var newFilesPartial = new JArray(File(5, "new/Show.S01E05.mkv", 0.4));
        var newFilesDone = new JArray(File(5, "new/Show.S01E05.mkv", 1));
        var main = new JArray(File(0, "Show.S01E01.mkv", 1));

        var files = new Dictionary<string, JArray> { [DonorHash] = oldFiles, ["cccccccccccccccccccccccccccccccccccccccc"] = newFilesPartial };
        Assert.DoesNotContain(HunterAccess.PlanReplacements(main, item, files, Now, 7), a => a.kind == "upgraded");   // новый ещё качается — старый не трогаем

        files["cccccccccccccccccccccccccccccccccccccccc"] = newFilesDone;
        var acts = HunterAccess.PlanReplacements(main, item, files, Now, 7);
        Assert.Contains(acts, a => a.kind == "upgraded" && a.donorHash == DonorHash);   // снимаем ХУДШЕГО
        Assert.DoesNotContain(acts, a => a.kind == "upgraded" && a.donorHash == "cccccccccccccccccccccccccccccccccccccccc");
    }

    [Fact]
    public void PlanReplacements_WrongSeasonRecord_SelfHeals()
    {
        // запись, сделанная до fail-closed гейта: season=3, а файл — S02
        var d = new JObject
        {
            ["hash"] = DonorHash, ["link"] = "http://t/d",
            ["eps"] = new JArray(new JObject { ["epkey"] = "e7", ["season"] = 3, ["ep"] = 7, ["fileIndex"] = 14, ["status"] = "hunted" })
        };
        var item = new JObject { ["hash"] = MainHash, ["donors"] = new JArray(d) };
        var main = new JArray(File(0, "Silo (Season 3) WEB-DL 1080p/Silo.S03E01.mkv", 1));
        var files = new Dictionary<string, JArray>
        {
            [DonorHash] = new JArray(File(14, "Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E07.Rus.Eng.by.Сибиряк.avi", 1))
        };
        var acts = HunterAccess.PlanReplacements(main, item, files, Now, 7);
        Assert.Contains(acts, a => a.kind == "wrong-season" && a.fileIndex == 14);
        Assert.Contains(acts, a => a.kind == "delete-donor");   // серий не осталось → донор снимается
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
        // qdl 2.107: одиночка без сезона ни в имени, ни в папке, ни у донора — при season > 1 НЕ берём
        // (раньше «s <= 0 → s = season» подставлял сезон охоты вслепую и делал проверку тождеством)
        Assert.Empty(HunterAccess.FindEpFiles(files, 2, new List<int> { 9 }, "Сериал (Серия 9) 1080p"));
        // сезон подтверждён донором (имя/папка/episodes классификатора) — берём и проставляем его
        var found = HunterAccess.FindEpFiles(files, 2, new List<int> { 9 }, "Сериал (Серия 9) 1080p", 2);
        Assert.Single(found);
        Assert.Equal(9, found[0].ep);
        Assert.Equal(2, found[0].season);
        // первый сезон / аниме без маркеров — как раньше
        Assert.Single(HunterAccess.FindEpFiles(files, 1, new List<int> { 9 }, "Сериал (Серия 9) 1080p"));
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
        HunterAccess.BlacklistAdd(item, DonorHash, "http://t/parsemagnet?id=3", "wrong-season", 30);   // no-episode с 05.09 parselink ключом не делает (см. BlacklistAddNoEpisode)
        HunterAccess.BlacklistAdd(item, "cccccccccccccccccccccccccccccccccccccccc", null, "meta-timeout", 1);

        var keys = HunterAccess.BlacklistKeys(item);
        Assert.Contains(DonorHash, keys);
        Assert.Contains("http://t/parsemagnet?id=3", keys);

        // BlacklistKeys теперь time-aware: отдаёт только ДЕЙСТВУЮЩИЕ блокировки (until > now).
        // Сама запись после истечения TTL живёт ещё сутки (grace) — но только ради счётчика попыток
        // бэкоффа, блокировкой она уже не является.
        HunterAccess.PruneBlacklist(item, now.AddDays(2));   // meta-timeout (1 день) протух
        keys = HunterAccess.BlacklistKeys(item, now.AddDays(2));
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
