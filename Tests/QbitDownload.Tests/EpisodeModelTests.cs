using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for the episode model helpers on <c>QbitController+Ep</c>:
/// <c>EpKey</c> (Controller.cs 1277-1284), <c>EpLabel</c> (1286-1295),
/// <c>IsEpisodeLike</c> (1297-1303) and <c>EpEqual</c> (801-809).
///
/// Real <c>Ep</c> instances are produced via <see cref="Access.ParseEp"/> so the tests exercise the
/// exact shapes the production parser emits. Input strings were chosen by reading <c>ParseEp</c>
/// (Controller.cs 778-799).
/// </summary>
public class EpisodeModelTests
{
    // ── sanity: verify the Ep shapes our inputs parse to ──────────────────
    // These pin the parser so the downstream EpKey/EpLabel/etc. rows stay meaningful.

    [Fact]
    public void ParseEp_SeasonEpisode_shape()
    {
        var e = Access.ParseEp("Show S02E07");
        Assert.Null(e.kind);
        Assert.Equal(2, e.season);
        Assert.Equal(7, e.ep);
        Assert.Equal(-1, e.ep2);
        Assert.True(e.any);
    }

    [Fact]
    public void ParseEp_EpisodeOnly_shape()
    {
        var e = Access.ParseEp("Show E07");
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(7, e.ep);
        Assert.True(e.any);
    }

    [Fact]
    public void ParseEp_OvaWithNum_shape()
    {
        var e = Access.ParseEp("Show OVA 1");
        Assert.Equal("OVA", e.kind);
        Assert.Equal(1, e.ep);
        Assert.True(e.any);
    }

    [Fact]
    public void ParseEp_OvaNoNum_shape()
    {
        var e = Access.ParseEp("Show OVA");
        Assert.Equal("OVA", e.kind);
        Assert.Equal(-1, e.ep);
        Assert.True(e.any);
    }

    [Fact]
    public void ParseEp_SpNoNum_shape()
    {
        var e = Access.ParseEp("Show SP");
        Assert.Equal("SP", e.kind);
        Assert.Equal(-1, e.ep);
        Assert.True(e.any);
    }

    [Fact]
    public void ParseEp_Range_shape()
    {
        var e = Access.ParseEp("Show E01-E08");
        Assert.Equal("RANGE", e.kind);
        Assert.Equal(1, e.ep);
        Assert.Equal(8, e.ep2);
        Assert.True(e.any);
    }

    [Fact]
    public void ParseEp_Empty_notAny()
    {
        var e = Access.ParseEp("");
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(-1, e.ep);
        Assert.False(e.any);
    }

    // ────────────────────────────── EpKey ────────────────────────────────

    [Theory]
    // season + ep  → "s{season}e{ep}"
    [InlineData("Show S02E07", "s2e7")]
    [InlineData("Show 1x07", "s1e7")]
    // ep only (no season) → "e{ep}"
    [InlineData("Show E07", "e7")]
    [InlineData("Episode 8", "e8")]
    // kind + num → lowercase(kind)+num
    [InlineData("Show OVA 1", "ova1")]
    [InlineData("Show ONA 3", "ona3")]
    [InlineData("Show OAD 2", "oad2")]
    [InlineData("Special 2", "sp2")]
    // kind, no num → lowercase(kind) with empty suffix
    [InlineData("Show OVA", "ova")]
    [InlineData("Show SP", "sp")]
    [InlineData("Show OP", "op")]
    [InlineData("Show ED", "ed")]
    // RANGE → "r{ep}-{ep2}"
    [InlineData("Show E01-E08", "r1-8")]
    [InlineData("Show 03-05", "r3-5")]
    public void EpKey_producesExpectedKey(string input, string expected)
    {
        Assert.Equal(expected, Access.EpKey(Access.ParseEp(input)));
    }

    [Fact]
    public void EpKey_null_whenNotAny()
    {
        Assert.Null(Access.EpKey(Access.ParseEp("")));
    }

    // kind present but ep < 0 → key is just the kind with no trailing digits
    [Fact]
    public void EpKey_kindWithoutNum_hasNoTrailingDigit()
    {
        Assert.Equal("ova", Access.EpKey(Access.ParseEp("Show OVA")));
    }

    // ────────────────────────────── EpLabel ──────────────────────────────

    [Fact]
    public void EpLabel_seasonAndEpisode()
    {
        // "Сезон {season} · серия {ep}"  (middle dot U+00B7)
        Assert.Equal("Сезон 2 · серия 7", Access.EpLabel(Access.ParseEp("Show S02E07")));
    }

    [Fact]
    public void EpLabel_episodeOnly()
    {
        Assert.Equal("Серия 7", Access.EpLabel(Access.ParseEp("Show E07")));
    }

    [Fact]
    public void EpLabel_range()
    {
        // "Серии {ep}–{ep2}"  (en-dash U+2013)
        Assert.Equal("Серии 1–8", Access.EpLabel(Access.ParseEp("Show E01-E08")));
    }

    [Fact]
    public void EpLabel_kindWithNum()
    {
        Assert.Equal("OVA 2", Access.EpLabel(Access.ParseEp("Show OVA 2")));
    }

    [Fact]
    public void EpLabel_kindWithoutNum()
    {
        Assert.Equal("OVA", Access.EpLabel(Access.ParseEp("Show OVA")));
    }

    [Theory]
    [InlineData("Show S02E07", "Сезон 2 · серия 7")]
    [InlineData("Show 1x07", "Сезон 1 · серия 7")]
    [InlineData("Show E07", "Серия 7")]
    [InlineData("Episode 8", "Серия 8")]
    [InlineData("Show E01-E08", "Серии 1–8")]
    [InlineData("Show 03-05", "Серии 3–5")]
    [InlineData("Show OVA 2", "OVA 2")]
    [InlineData("Show OVA", "OVA")]
    [InlineData("Special 2", "SP 2")]
    [InlineData("Show SP", "SP")]
    // extras: kind present, ep<0 → kind label only (no number)
    [InlineData("Show OP", "OP")]
    [InlineData("Show ED", "ED")]
    [InlineData("Show PV", "PV")]
    public void EpLabel_producesExpectedLabel(string input, string expected)
    {
        Assert.Equal(expected, Access.EpLabel(Access.ParseEp(input)));
    }

    [Fact]
    public void EpLabel_null_whenNotAny()
    {
        Assert.Null(Access.EpLabel(Access.ParseEp("")));
    }

    // ─────────────────────────── IsEpisodeLike ───────────────────────────

    [Theory]
    // plain episode (kind==null, ep>=0) → true
    [InlineData("Show S02E07", true)]
    [InlineData("Show E07", true)]
    [InlineData("Episode 8", true)]
    // RANGE / OVA / ONA / OAD / SP → true
    [InlineData("Show E01-E08", true)]
    [InlineData("Show OVA", true)]
    [InlineData("Show OVA 2", true)]
    [InlineData("Show ONA 3", true)]
    [InlineData("Show OAD 2", true)]
    [InlineData("Show SP", true)]
    [InlineData("Special 2", true)]
    // extras OP/ED/PV/NCOP/NCED → false (still "any", just not episode-like)
    [InlineData("Show OP", false)]
    [InlineData("Show ED", false)]
    [InlineData("Show PV", false)]
    [InlineData("Show NCOP", false)]
    [InlineData("Show NCED", false)]
    // empty → false (not any)
    [InlineData("", false)]
    public void IsEpisodeLike_actualBehavior(string input, bool expected)
    {
        Assert.Equal(expected, Access.IsEpisodeLike(Access.ParseEp(input)));
    }

    // ────────────────────────────── EpEqual ──────────────────────────────

    [Fact]
    public void EpEqual_equal_sameSeasonAndEp()
    {
        var a = Access.ParseEp("Show S02E07");
        var b = Access.ParseEp("Other S02E07");
        Assert.True(Access.EpEqual(a, b));
    }

    [Fact]
    public void EpEqual_unequal_differentKind()
    {
        // kind==null (plain ep) vs kind=="OVA"
        var plain = Access.ParseEp("Show E07");
        var ova = Access.ParseEp("Show OVA 7");
        Assert.False(Access.EpEqual(plain, ova));
    }

    [Fact]
    public void EpEqual_range_comparesEpAndEp2()
    {
        var r1 = Access.ParseEp("Show E01-E08");
        var r2 = Access.ParseEp("Other 01-08");
        Assert.True(Access.EpEqual(r1, r2));

        var r3 = Access.ParseEp("Show E01-E09");
        Assert.False(Access.EpEqual(r1, r3));
    }

    [Fact]
    public void EpEqual_range_differentStart_unequal()
    {
        var r1 = Access.ParseEp("Show E02-E08");
        var r2 = Access.ParseEp("Show E01-E08");
        Assert.False(Access.EpEqual(r1, r2));
    }

    [Fact]
    public void EpEqual_seasonIgnored_whenOneSideMinusOne()
    {
        // one side has a season (S02E07), the other only ep (E07) → season==-1 side is ignored.
        var withSeason = Access.ParseEp("Show S02E07");
        var epOnly = Access.ParseEp("Show E07");
        Assert.Equal(-1, epOnly.season);
        Assert.True(Access.EpEqual(withSeason, epOnly));
        Assert.True(Access.EpEqual(epOnly, withSeason));
    }

    [Fact]
    public void EpEqual_bothSeasons_differ_unequal()
    {
        var s1 = Access.ParseEp("Show S01E07");
        var s2 = Access.ParseEp("Show S02E07");
        Assert.False(Access.EpEqual(s1, s2));
    }

    [Fact]
    public void EpEqual_sameEp_differentSameSeason_equal()
    {
        var a = Access.ParseEp("Show 2x07");
        var b = Access.ParseEp("Show S02E07");
        Assert.Equal(2, a.season);
        Assert.Equal(2, b.season);
        Assert.True(Access.EpEqual(a, b));
    }

    [Fact]
    public void EpEqual_differentEp_unequal()
    {
        var a = Access.ParseEp("Show E07");
        var b = Access.ParseEp("Show E08");
        Assert.False(Access.EpEqual(a, b));
    }

    [Fact]
    public void EpEqual_false_whenEitherNotAny()
    {
        var real = Access.ParseEp("Show E07");
        var empty = Access.ParseEp("");
        Assert.False(Access.EpEqual(real, empty));
        Assert.False(Access.EpEqual(empty, real));
        Assert.False(Access.EpEqual(empty, empty));
    }

    [Fact]
    public void EpEqual_sameKind_sameEp_equal()
    {
        var a = Access.ParseEp("Show OVA 2");
        var b = Access.ParseEp("Other OVA 2");
        Assert.True(Access.EpEqual(a, b));
    }

    [Fact]
    public void EpEqual_sameKind_differentEp_unequal()
    {
        var a = Access.ParseEp("Show OVA 1");
        var b = Access.ParseEp("Show OVA 2");
        Assert.False(Access.EpEqual(a, b));
    }

    // Both kind-only extras with same kind and ep==-1 on both sides:
    // kind=="OP" != "RANGE", ep==-1 on both so v.ep!=a.ep is false, season both -1,
    // final line: v.kind != null → true. So two "OP" extras compare equal.
    [Fact]
    public void EpEqual_twoSameKindExtras_equal()
    {
        var a = Access.ParseEp("Show OP");
        var b = Access.ParseEp("Other OP");
        Assert.Equal("OP", a.kind);
        Assert.Equal(-1, a.ep);
        Assert.True(Access.EpEqual(a, b));
    }
}
