using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Надёжность и покрытие охоты за сериями: топ-N кандидатов за проход, «пустая выдача — не проход»,
/// догон пропущенных тиков после рестарта и сводка отсева для лога.
/// </summary>
public class HunterCoverageTests
{
    const string MainHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static readonly DateTime Now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    public HunterCoverageTests() => TestEnv.EnsureConf();

    static JObject Cand(string title, int sid, string magnet = null, string parselink = "http://t/parsemagnet?id=1",
                        int quality = 1080, long sizeBytes = 12_000_000_000, double score = 50)
        => new JObject { ["title"] = title, ["sid"] = sid, ["magnet"] = magnet, ["parselink"] = parselink, ["quality"] = quality, ["sizeBytes"] = sizeBytes, ["score"] = score };

    // ── топ-N проб за проход (раньше жёстко .Take(1), ручка donorProbesPerRun была мёртвой) ──
    [Fact]
    public void ProbeCandidates_TakesTopN_InCoverOrder()
    {
        var yesHigh = Cand("Сериал [2 сезон, 1-10 из 12] 1080p", 10, score: 80);
        var yesLow = Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, score: 40);
        var maybe = Cand("Сериал 2 сезон WEB-DL 1080p", 10, score: 99);
        var eligible = new List<JObject> { maybe, yesLow, yesHigh };

        var three = HunterAccess.ProbeCandidates(eligible, 2, new List<int> { 9 }, 3);
        Assert.Equal(3, three.Count);
        Assert.Equal(80, three[0].Value<double>("score"));   // порядок остаётся от OrderByCover
        Assert.Equal(40, three[1].Value<double>("score"));
        Assert.Equal(99, three[2].Value<double>("score"));   // Maybe — в хвосте

        Assert.Single(HunterAccess.ProbeCandidates(eligible, 2, new List<int> { 9 }, 1));
        Assert.Equal(2, HunterAccess.ProbeCandidates(eligible, 2, new List<int> { 9 }, 2).Count);
    }

    [Fact]
    public void ProbeCandidates_ClampsAndNeverExceedsEligible()
    {
        var eligible = new List<JObject> { Cand("Сериал [2 сезон, 1-10 из 12] 1080p", 10) };
        Assert.Single(HunterAccess.ProbeCandidates(eligible, 2, new List<int> { 9 }, 0));    // 0 → кламп до 1
        Assert.Single(HunterAccess.ProbeCandidates(eligible, 2, new List<int> { 9 }, -5));   // отрицательное — тоже
        Assert.Single(HunterAccess.ProbeCandidates(eligible, 2, new List<int> { 9 }, 10));   // больше, чем есть
        Assert.Empty(HunterAccess.ProbeCandidates(new List<JObject>(), 2, new List<int> { 9 }, 3));
    }

    // ── зеркало причин отсева (только для лога) не должно разойтись с самим гейтом ──
    [Fact]
    public void DropReason_MirrorsFilter()
    {
        string knownMagnet = "magnet:?xt=urn:btih:" + MainHash;
        var scored = new JArray(
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10),                                         // ок
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 1),                                          // мало сидов
            Cand("Сериал [2 сезон, 1-9 из 12] 720p", 10, quality: 720),                            // ниже minQuality
            Cand("Сериал [2 сезон, 1-9 из 12]", 10, quality: 0),                                   // качество неизвестно — ок
            Cand("Сериал [1 сезон, 1-12 из 12] 1080p", 10),                                        // чужой сезон
            Cand("Сериал 1080p WEB-DL", 10),                                                       // сезон не заявлен
            Cand("Сериал [2 сезон, Серия 9] Remux 1080p", 10, sizeBytes: 40L * 1024 * 1024 * 1024),// 40 ГБ на серию
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, magnet: knownMagnet, parselink: null),   // сама основная
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, parselink: "http://t/parsemagnet?id=666"),// blacklist
            Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, parselink: null),                        // без ссылки
            Cand("Чужой сериал / Other Show [2 сезон, 1-9 из 12] 1080p", 10)                       // имя
        );
        var h = HunterAccess.MakeHuntCtx(MainHash, 2, new[] { MainHash }, new[] { "http://t/parsemagnet?id=666" },
                                         3, 1080, 150, 8, "сериал", "series");

        var kept = HunterAccess.FilterDonorCandidates(scored, h);
        foreach (var cand in scored.OfType<JObject>())
        {
            bool passed = kept.Any(x => ReferenceEquals(x, cand));
            string reason = HunterAccess.DropReason(cand, h);
            Assert.Equal(passed, reason == null);   // прошёл гейт ⇔ причины отсева нет
        }
        Assert.Equal(2, kept.Count);   // «ок» + «качество неизвестно»
    }

    [Fact]
    public void DropReason_SelfTopicReregistration()
    {
        const string self = "http://127.0.0.1:9118/rutracker/parsemagnet?id=6878482&apikey=abc";
        var cand = Cand("Сериал [2 сезон, 1-9 из 12] 1080p", 10, parselink: "http://127.0.0.1:9118/rutracker/parsemagnet?id=6878482&apikey=zzz");
        var h = HunterAccess.MakeHuntCtx(MainHash, 2, new[] { MainHash }, null, 3, 1080, 150, 8, null, null, self);
        Assert.Equal("своя раздача", HunterAccess.DropReason(cand, h));
    }

    // ── пустая выдача индексатора не должна выглядеть как удачный проход ──
    [Fact]
    public void MarkHuntBarren_KeepsLastRun_AndCountsStreak()
    {
        var m = new JObject { ["hash"] = MainHash, ["title"] = "Сериал" };
        HunterAccess.SetHuntStamp(m, Now, 12);
        string lastRun = (m["hunt"] as JObject).Value<string>("lastRun");

        HunterAccess.MarkHuntBarren(m, Now.AddHours(4));
        HunterAccess.MarkHuntBarren(m, Now.AddHours(8));
        var hunt = m["hunt"] as JObject;

        Assert.Equal(lastRun, hunt.Value<string>("lastRun"));   // штамп удачного прогона не сдвинулся
        Assert.Equal(12, hunt.Value<int>("lastMaxClaim"));
        Assert.Equal(2, hunt.Value<int>("emptyStreak"));
        Assert.NotNull(hunt.Value<string>("lastEmpty"));
    }

    [Fact]
    public void SetHuntStamp_ResetsEmptyStreak()
    {
        var m = new JObject { ["hash"] = MainHash };
        HunterAccess.MarkHuntBarren(m, Now);          // штампа ещё нет — заводится с нуля
        Assert.Null((m["hunt"] as JObject).Value<string>("lastRun"));

        HunterAccess.SetHuntStamp(m, Now.AddHours(1), 9);
        var hunt = m["hunt"] as JObject;
        Assert.Null(hunt.Value<int?>("emptyStreak"));
        Assert.Equal(9, hunt.Value<int>("lastMaxClaim"));
    }

    [Theory]
    [InlineData(3, 3, 0, true)]     // пусто у всех — сбой индексатора, повторяем
    [InlineData(3, 2, 0, false)]    // часть сериалов выдачу дала — это не сбой
    [InlineData(0, 0, 0, false)]    // никого не опрашивали
    [InlineData(1, 1, 99, false)]   // лимит подряд идущих повторов исчерпан
    public void ShouldRetryHunt_OnlyOnTotalBlackout(int searched, int barren, int retries, bool expected)
        => Assert.Equal(expected, HunterAccess.ShouldRetryHunt(searched, barren, retries));

    [Fact]
    public void ShouldRetryHunt_StopsAtLimit()
    {
        int max = HunterAccess.HuntRetryMax;
        Assert.True(HunterAccess.ShouldRetryHunt(2, 2, max - 1));
        Assert.False(HunterAccess.ShouldRetryHunt(2, 2, max));
    }

    // ── догон пропущенных тиков после рестарта ──
    [Fact]
    public void HuntOverdue_ByLastRunAge()
    {
        TestEnv.FreshCache();
        var period = TimeSpan.FromHours(4);

        // свежий прогон (1 ч назад) — не просрочено
        HunterAccess.SaveWatch(new JArray(new JObject
        {
            ["hash"] = MainHash,
            ["hunt"] = new JObject { ["lastRun"] = DateTime.UtcNow.AddHours(-1).ToString("o") }
        }));
        Assert.False(QbitController.HuntOverdue(period, out _));

        // 10 ч назад — больше 1.5 периода
        HunterAccess.SaveWatch(new JArray(new JObject
        {
            ["hash"] = MainHash,
            ["hunt"] = new JObject { ["lastRun"] = DateTime.UtcNow.AddHours(-10).ToString("o") }
        }));
        Assert.True(QbitController.HuntOverdue(period, out var since));
        Assert.True(since.TotalHours > 9);
    }

    [Fact]
    public void HuntOverdue_NoStampAtAll_IsOverdue_EmptyListIsNot()
    {
        TestEnv.FreshCache();
        var period = TimeSpan.FromHours(4);

        HunterAccess.SaveWatch(new JArray());
        Assert.False(QbitController.HuntOverdue(period, out _));            // следить не за чем

        HunterAccess.SaveWatch(new JArray(new JObject { ["hash"] = MainHash, ["title"] = "Сериал" }));
        Assert.True(QbitController.HuntOverdue(period, out _));             // охота ни разу не отработала

        // самый свежий штамп по всем записям решает за всех
        HunterAccess.SaveWatch(new JArray(
            new JObject { ["hash"] = MainHash, ["hunt"] = new JObject { ["lastRun"] = DateTime.UtcNow.AddDays(-3).ToString("o") } },
            new JObject { ["hash"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ["hunt"] = new JObject { ["lastRun"] = DateTime.UtcNow.AddMinutes(-30).ToString("o") } }));
        Assert.False(QbitController.HuntOverdue(period, out _));
    }

    [Fact]
    public void HuntOverdue_FeatureOff_False()
    {
        TestEnv.FreshCache();
        HunterAccess.SaveWatch(new JArray(new JObject { ["hash"] = MainHash }));
        bool prev = ModInit.conf.episodeHunt;
        try
        {
            ModInit.conf.episodeHunt = false;
            Assert.False(QbitController.HuntOverdue(TimeSpan.FromHours(4), out _));
        }
        finally { ModInit.conf.episodeHunt = prev; }
    }
}
