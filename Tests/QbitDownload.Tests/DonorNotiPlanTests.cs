using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// §BS: донорская ветка уведомлений считает серии по donors[].eps[] — по тому, что охота реально
/// заказывала, — а не по всем файлам донора. Регрессия на инцидент 2026-08-09: пак ВТОРОГО сезона
/// «Укрытия» отдал уведомления про s3e7…s3e10 (таких серий нет в эфире), ключи осели в seen, и
/// настоящая S03E07 через шесть дней приехала молча.
/// </summary>
public class DonorNotiPlanTests
{
    const string DONOR = "dddddddddddddddddddddddddddddddddddddddd";

    static JToken F(int index, string name, double progress)
        => new JObject { ["index"] = index, ["name"] = name, ["progress"] = progress, ["size"] = 1_500_000_000L };

    static JObject E(string epkey, int season, int ep, int fileIndex, string status = "hunted")
        => new JObject
        {
            ["epkey"] = epkey, ["season"] = season, ["ep"] = ep, ["fileIndex"] = fileIndex,
            ["status"] = status, ["grabbedAt"] = "2026-08-15T01:50:38Z", ["replacedAt"] = null
        };

    static JObject Donor(params JObject[] eps) => new JObject { ["hash"] = DONOR, ["eps"] = new JArray(eps) };

    static HashSet<string> Seen(params string[] keys) => new HashSet<string>(keys);

    // Реальный пак-донор «Укрытия» от 15.08: заказана только E07, остальное выключено filePrio=0
    static JArray SiloDonor(double e7progress = 1.0) => new JArray(
        F(0, "Укрытие (Silo) Сезон 3/Укрытие - Silo S03 E07 (Радио) WEB-DL 1080p (2026).mkv", e7progress),
        F(1, "Укрытие (Silo) Сезон 3/Укрытие - Silo S03 E04 (Что бы ни случилось) WEB-DL 1080p (2026).mkv", 0.0002),
        F(2, "Укрытие (Silo) Сезон 3/Укрытие - Silo S03 E01 (Кто ты) WEB-DL 1080p (2026).mkv", 0.0));

    [Fact]
    public void GrabbedEpisodeComplete_YieldsRecordedEpkey()
    {
        var plan = Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 0)), SiloDonor(), Seen("s3e1", "s3e6"), 3);

        var one = Assert.Single(plan.OfType<JObject>());
        Assert.Equal("s3e7", one.Value<string>("epkey"));
        Assert.Equal(3, one.Value<int>("season"));
        Assert.Equal(7, one.Value<int>("episode"));
        Assert.Equal("Сезон 3 · серия 7", one.Value<string>("label"));
    }

    [Fact]
    public void MultiSeasonPack_OtherSeasonsComplete_YieldsNothing()   // форма инцидента 2026-08-09
    {
        var dfiles = new JArray(
            F(0, "Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E07.Rus.Eng.by.Сибиряк.avi", 1.0),
            F(1, "Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E08.Rus.Eng.by.Сибиряк.avi", 1.0),
            F(2, "Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E09.Rus.Eng.by.Сибиряк.avi", 1.0),
            F(3, "Silo (Season 3)/Silo.S03E07.1080p.WEB-DL.mkv", 0.4));   // единственная заказанная — ещё качается

        var plan = Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 3)), dfiles, Seen("s3e1"), 3);

        Assert.Empty(plan);   // до фикса здесь рождались s3e7…s3e9 из файлов ВТОРОГО сезона
    }

    [Fact]
    public void RecordedSeasonWrong_HealedFromFile_YieldsNothing()
    {
        // запись сделана до fail-closed сезонного гейта: season=3, а файл — второго сезона
        var dfiles = new JArray(F(0, "Укрытие.S02.WEB-DLRip/Silo.S02.E07.Rus.Eng.avi", 1.0));

        Assert.Empty(Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 0)), dfiles, Seen("s3e1"), 3));
    }

    [Fact]
    public void DonorWithoutEps_YieldsNothing()
    {
        var noEps = new JObject { ["hash"] = DONOR };
        var emptyEps = new JObject { ["hash"] = DONOR, ["eps"] = new JArray() };

        Assert.Empty(Access.DonorNotiPlan(noEps, SiloDonor(), Seen("s3e1"), 3));
        Assert.Empty(Access.DonorNotiPlan(emptyEps, SiloDonor(), Seen("s3e1"), 3));
    }

    [Fact]
    public void AlreadySeenEpisode_YieldsNothing()
    {
        Assert.Empty(Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 0)), SiloDonor(), Seen("s3e7"), 3));
        // legacy-ключ без сезона — эквивалентность SeenAlready
        Assert.Empty(Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 0)), SiloDonor(), Seen("e7"), 3));
    }

    [Fact]
    public void StaleFileIndex_NoNameFallback()
    {
        // индекс из записи протух: в паке такого файла нет, но s3e7 лежит под другим индексом
        Assert.Empty(Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 9)), SiloDonor(), Seen("s3e1"), 3));
    }

    [Fact]
    public void ReplacedEps_YieldsNothing()
    {
        Assert.Empty(Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 0, "replaced")), SiloDonor(), Seen("s3e1"), 3));
    }

    [Fact]
    public void UnfinishedGrabbedEpisode_YieldsNothing()
    {
        Assert.Empty(Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 0)), SiloDonor(e7progress: 0.9), Seen("s3e1"), 3));
    }

    [Fact]
    public void EpNumberMismatch_YieldsNothing()
    {
        var dfiles = new JArray(F(0, "Silo (Season 3)/Silo.S03E09.1080p.WEB-DL.mkv", 1.0));

        Assert.Empty(Access.DonorNotiPlan(Donor(E("s3e7", 3, 7, 0)), dfiles, Seen("s3e1"), 3));
    }

    [Fact]
    public void SeasonlessDonor_KeepsBareKey()   // аниме со сквозной нумерацией: ключ e7, а не s0e7
    {
        var dfiles = new JArray(F(0, "[Group] Show - 07 [WEB-DL 1080p].mkv", 1.0));

        var one = Assert.Single(Access.DonorNotiPlan(Donor(E("e7", -1, 7, 0)), dfiles, Seen("e1"), 0).OfType<JObject>());
        Assert.Equal("e7", one.Value<string>("epkey"));
        Assert.Equal(-1, one.Value<int>("season"));
        Assert.Equal("Серия 7", one.Value<string>("label"));
    }

    [Fact]
    public void AboveAired_FailsOpenOnUnknown()
    {
        Assert.False(Access.AboveAired(8, 0));    // TMDB неизвестен → не глушим
        Assert.True(Access.AboveAired(8, 7));     // серии 8 ещё не было в эфире
        Assert.False(Access.AboveAired(7, 7));    // ровно вышедшая — законна
    }

    [Fact]
    public void EpKeyForms_CoversBothKeyShapes()
    {
        Assert.Equal(new[] { "e7", "s3e7" }, Access.EpKeyForms("e7", 3, 7));       // запись «Укрытия» от 09.08
        Assert.Equal(new[] { "s3e7", "e7" }, Access.EpKeyForms("s3e7", 3, 7));
        Assert.Equal(new[] { "e7" }, Access.EpKeyForms("e7", -1, 7));
    }
}
