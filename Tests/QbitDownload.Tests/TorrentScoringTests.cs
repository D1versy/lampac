using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Services.Utilities;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>TorrentScoring — чистый скоринг выдачи /qdl/search (класс публичный, без reflection).</summary>
public class TorrentScoringTests
{
    static readonly DateTime Now = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    static ScoreCtx SerialCtx(string title = "Тестовый сериал", string original = "Test Serial", int year = 2024, int season = 2)
        => new ScoreCtx { titleNorm = SearchNameTo.Convert(title), originalNorm = SearchNameTo.Convert(original), year = year, isSerial = true, wantSeason = season, preferredQuality = 2160, now = Now };

    static ScoreCtx MovieCtx(string title = "Фильм", int year = 2020)
        => new ScoreCtx { titleNorm = SearchNameTo.Convert(title), originalNorm = null, year = year, isSerial = false, wantSeason = 0, preferredQuality = 2160, now = Now };

    static JObject T(string title, int sid, int pir = 1, string parselink = null, string date = null, long sizeBytes = 0)
        => new JObject { ["title"] = title, ["sid"] = sid, ["pir"] = pir, ["parselink"] = parselink, ["date"] = date, ["sizeBytes"] = sizeBytes, ["quality"] = QualityFrom(title) };

    static int QualityFrom(string t)
    {
        var m = System.Text.RegularExpressions.Regex.Match(t ?? "", "(2160|1080|720|480)p?");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    // ── ParseEpCoverage ──────────────────────────────────────────────────
    [Theory]
    [InlineData("Сериал [1-8 из 12]", 8, 12)]
    [InlineData("Сериал (8 из 12)", 8, 12)]
    [InlineData("Тайтл [09 из 24] WEB-DL", 9, 24)]
    [InlineData("Серии: 1-16 (16)", 16, 16)]
    // формат kinozal: слово «серии» между диапазоном и «из», плюс «сезон:» перед ним
    [InlineData("Укрытие (Бункер) (3 сезон: 1-5 серии из 10) / Silo / 2026", 5, 10)]
    [InlineData("Аутсорс (1 сезон: 1-8 серии из 8) / 2024 / РУ, СТ", 8, 8)]
    [InlineData("Сериал (2 сезон: 3 серия из 12)", 3, 12)]
    public void ParseEpCoverage_Formats(string title, int have, int total)
    {
        var c = TorrentScoring.ParseEpCoverage(title);
        Assert.NotNull(c);
        Assert.Equal(have, c.have);
        Assert.Equal(total, c.total);
    }

    [Fact]
    public void ParseEpCoverage_UnknownTotal()
    {
        var c = TorrentScoring.ParseEpCoverage("Сериал [5 из ??]");
        Assert.NotNull(c);
        Assert.Equal(5, c.have);
        Assert.Equal(0, c.total);
    }

    [Fact]
    public void ParseEpCoverage_SeasonGuard_NotEpisodes()
        => Assert.Null(TorrentScoring.ParseEpCoverage("Сериал [Сезон 1 из 3] WEB-DL"));

    [Theory]
    [InlineData("Друзья [10 из 10 сезонов]")]   // «сезон» ПОСЛЕ чисел — это сезоны, не серии
    [InlineData("Сериал 2 из 5 сезонов")]
    [InlineData("Сериал [1-3 из 5 сезонов]")]
    public void ParseEpCoverage_SeasonGuard_After_NotEpisodes(string title)
        => Assert.Null(TorrentScoring.ParseEpCoverage(title));

    [Fact]
    public void ParseEpCoverage_Garbage_Null()
        => Assert.Null(TorrentScoring.ParseEpCoverage("Просто фильм (2024) BDRip 1080p"));

    [Fact]
    public void ParseEpCoverage_Ongoing()
    {
        Assert.True(TorrentScoring.ParseEpCoverage("Сериал [1-8 из 12]").ongoing);
        Assert.False(TorrentScoring.ParseEpCoverage("Сериал [12 из 12]").ongoing);
    }

    // ── ParseSeasons ─────────────────────────────────────────────────────
    [Theory]
    [InlineData("Сериал (2 сезон)", new[] { 2 })]
    [InlineData("Сериал [Сезон: 1-3]", new[] { 1, 2, 3 })]
    [InlineData("Show S02E05 WEB-DL", new[] { 2 })]
    [InlineData("Show S01-S03 Complete", new[] { 1, 2, 3 })]
    [InlineData("Show Season 2", new[] { 2 })]
    [InlineData("Сериал [1-2 сезоны]", new[] { 1, 2 })]
    public void ParseSeasons_Formats(string title, int[] expected)
        => Assert.Equal(expected, TorrentScoring.ParseSeasons(title));

    [Fact]
    public void ParseSeasons_NoFalsePositives_OnQuality()
        => Assert.Empty(TorrentScoring.ParseSeasons("Фильм (2024) WEB-DL 1080p x264"));

    // ── Score / SortAndMark ──────────────────────────────────────────────
    [Fact]
    public void RelevantLowSeeds_Beats_IrrelevantHighSeeds()
    {
        var relevant = T("Тестовый сериал / Test Serial [2 сезон, 1-8 из 12] (2024) WEB-DL 1080p", 30);
        var alien = T("Совсем другое кино (2018) BDRip 2160p", 700, 100);
        var sorted = TorrentScoring.SortAndMark(new JArray(alien, relevant), SerialCtx(), 5);
        Assert.Equal(30, sorted[0].Value<int>("sid"));   // релевантная — первой
        Assert.True(sorted[0].Value<bool?>("rec") == true);
        Assert.False(sorted[1].Value<bool?>("rec") == true);   // nameMiss не может быть ⭐
    }

    [Fact]
    public void MovieQuery_SerialRelease_Sinks()
    {
        var movie = T("Фильм (2020) BDRip", 10);
        var serial = T("Фильм (3 сезон) WEB-DL", 500);
        var sorted = TorrentScoring.SortAndMark(new JArray(serial, movie), MovieCtx(), 5);
        Assert.Equal(10, sorted[0].Value<int>("sid"));
        Assert.False(sorted.Any(x => x.Value<bool?>("rec") == true && x.Value<int>("sid") == 500));   // typeMiss — не ⭐
    }

    [Fact]
    public void DeadTorrent_Sinks()
    {
        var dead = T("Тестовый сериал (2024) 1080p", 0, 0);
        var alive = T("Тестовый сериал (2024) 1080p", 5);
        var sorted = TorrentScoring.SortAndMark(new JArray(dead, alive), SerialCtx(), 5);
        Assert.Equal(5, sorted[0].Value<int>("sid"));
        Assert.True(sorted[0].Value<double>("score") - sorted[1].Value<double>("score") > 20);
    }

    [Fact]
    public void LogScaleSeeds_500vs700_AlmostEqual()
    {
        var a = TorrentScoring.Score(T("Тестовый сериал (2024) 1080p", 500), SerialCtx());
        var b = TorrentScoring.Score(T("Тестовый сериал (2024) 1080p", 700), SerialCtx());
        Assert.True(Math.Abs(a.score - b.score) < 0.5);   // кап лог-шкалы: неразличимы
    }

    [Fact]
    public void Freshness_BadDate_Neutral()
    {
        // TorrentDetails.createTime дефолтится в UtcNow при неудачном парсе — даты из будущего/древние игнорим
        var bad = TorrentScoring.Score(T("Тестовый сериал (2024)", 10, date: "1997-05-01T00:00:00Z"), SerialCtx());
        var none = TorrentScoring.Score(T("Тестовый сериал (2024)", 10), SerialCtx());
        Assert.Equal(none.score, bad.score, 3);
    }

    [Fact]
    public void Freshness_RecentSerial_Boost()
    {
        var fresh = TorrentScoring.Score(T("Тестовый сериал (2024)", 10, date: Now.AddDays(-3).ToString("o")), SerialCtx());
        var old = TorrentScoring.Score(T("Тестовый сериал (2024)", 10, date: Now.AddDays(-400).ToString("o")), SerialCtx());
        Assert.True(fresh.score > old.score + 5);
        Assert.Contains(fresh.why, w => w.StartsWith("обновлена"));
    }

    [Fact]
    public void OngoingFresh_GetsBonus_AndEpField()
    {
        var t = T("Тестовый сериал [1-8 из 12] (2024)", 10, date: Now.AddDays(-2).ToString("o"));
        var r = TorrentScoring.Score(t, SerialCtx());
        Assert.NotNull(r.ep);
        Assert.True(r.ep.ongoing);
        Assert.Contains("обновляется", r.why);
    }

    [Fact]
    public void GracefulDegradation_MinimalFields_NoThrow()
    {
        var minimal = new JObject { ["title"] = "Тестовый сериал", ["sid"] = 7 };
        var sorted = TorrentScoring.SortAndMark(new JArray(minimal), SerialCtx(), 5);
        Assert.NotNull(sorted[0]["score"]);
        Assert.Equal("Тестовый сериал", sorted[0].Value<string>("title"));   // старые поля не тронуты
    }

    [Fact]
    public void Rec_RequiresMinSeeds()
    {
        var lowSeeds = T("Тестовый сериал (2024) 1080p", 3);
        var sorted = TorrentScoring.SortAndMark(new JArray(lowSeeds), SerialCtx(), 5);
        Assert.False(sorted.Any(x => x.Value<bool?>("rec") == true));
    }

    [Fact]
    public void Rec_OnlyOne_WithWhy()
    {
        var a = T("Тестовый сериал [2 сезон, 1-8 из 12] (2024) 1080p", 40, parselink: "http://127.0.0.1:9118/rutracker/parsemagnet?id=1");
        var b = T("Тестовый сериал (2024) 1080p", 30);
        var sorted = TorrentScoring.SortAndMark(new JArray(b, a), SerialCtx(), 5);
        Assert.Equal(1, sorted.Count(x => x.Value<bool?>("rec") == true));
        var rec = sorted.First(x => x.Value<bool?>("rec") == true);
        Assert.False(string.IsNullOrWhiteSpace(rec.Value<string>("why")));
        Assert.True(rec.Value<bool?>("watchable") == true);   // parselink у сериала → 🔔
    }

    [Fact]
    public void KillSwitchFields_EpAndWatchable_Serialized()
    {
        var t = T("Тестовый сериал [1-8 из 12] (2024)", 10, parselink: "http://x/parsemagnet");
        var sorted = TorrentScoring.SortAndMark(new JArray(t), SerialCtx(), 5);
        Assert.Equal(8, sorted[0]["ep"].Value<int>("have"));
        Assert.Equal(12, sorted[0]["ep"].Value<int>("total"));
        Assert.True(sorted[0].Value<bool>("watchable"));
    }
}
