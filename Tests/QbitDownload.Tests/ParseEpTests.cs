using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for <c>QbitController.ParseEp</c> (reached via <see cref="Access.ParseEp"/>).
///
/// ParseEp classifies a torrent-file "base name" into an episode descriptor (Ep) with fields
/// kind / season / ep / ep2 / any. Its body is a priority-ordered cascade of regex alternatives
/// (Controller.cs ~778-799). These tests exercise, in that same priority order:
///   OVA/ONA/OAD, SP/Special/Спецвыпуск/Спешл, NCOP/NCED/Creditless/Clean, OP/ED/PV/CM/Menu/Trailer/Preview/Teaser,
///   RANGE (E-form &amp; bare), SxxEyy, NxNN, EPn/E.n, серия/episode/эпизод/вып, #n, "- n", [nn],
///   leading/underscore number, bare number — plus the StripNoise pre-pass (1080p, x265, years, 5.1 audio, WxH…).
///
/// STYLE = CHARACTERIZATION: every assertion below encodes the CURRENT observed behavior, including
/// a couple of clearly-surprising results flagged with // BUG?: . Nothing here is expected to fail.
/// </summary>
public class ParseEpTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 1) OVA / ONA / OAD   — kind = uppercase token; ep from optional 1-2 digit number (0-stripped).
    //    Regex: \b(OVA|ONA|OAD)\s*0*(\d{1,2})?\b   (max TWO digits, requires a word boundary)
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("OVA", "OVA", -1)]
    [InlineData("ONA", "ONA", -1)]
    [InlineData("OAD", "OAD", -1)]
    [InlineData("OVA2", "OVA", 2)]
    [InlineData("OVA 03", "OVA", 3)]   // leading zero stripped
    [InlineData("OAD5", "OAD", 5)]
    [InlineData("ova12", "OVA", 12)]   // case-insensitive; two digits ok
    [InlineData("Bonus OVA 07", "OVA", 7)]
    public void Ova_family(string input, string kind, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Equal(kind, e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(ep, e.ep);
        Assert.Equal(-1, e.ep2);
        Assert.True(e.any);
    }

    [Fact]
    public void Ova_withThreeDigits_doesNotMatchOvaBranch_andFallsThrough()
    {
        // "OVA123": \b(OVA|ONA|OAD)\s*0*(\d{1,2})?\b cannot reach a \b after only 2 of the 3 digits,
        // so the OVA branch fails. No later branch matches either (StripNoise leaves "OVA123",
        // and leading/bare number rules need the digits isolated). Result: empty Ep.
        var e = Access.ParseEp("OVA123");
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(-1, e.ep);
        Assert.False(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2) SP / Special / Спецвыпуск / Спешл  — kind normalized to "SP"; optional number captured.
    //    Regex: \b(?:SP|Special|Спецвыпуск|Спешл)\s*0*(\d{1,2})?\b
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("SP", -1)]
    [InlineData("Special", -1)]
    [InlineData("Спецвыпуск", -1)]
    [InlineData("Спешл", -1)]
    [InlineData("SP07", 7)]
    [InlineData("Special 03", 3)]
    [InlineData("Спешл 2", 2)]
    public void Sp_family(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Equal("SP", e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3) NCOP / NCED / Creditless OP|ED / Clean Opening|Ending
    //    kind = NCED if uppercased match contains "ED" OR "ENDING", else NCOP. No ep.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("NCOP", "NCOP")]
    [InlineData("NCED", "NCED")]
    [InlineData("Clean Opening", "NCOP")]
    [InlineData("Clean Ending", "NCED")]     // "ENDING" branch
    [InlineData("Creditless ED", "NCED")]    // literal "ED"
    public void NonCredit_family(string input, string kind)
    {
        var e = Access.ParseEp(input);
        Assert.Equal(kind, e.kind);
        Assert.Equal(-1, e.ep);
        Assert.Equal(-1, e.ep2);
        Assert.True(e.any);
    }

    [Fact]
    public void Creditless_OP_isMisclassified_as_NCED()
    {
        // "Creditless OP" is an OPENING, so one would expect NCOP.
        // But the classifier does: k = "CREDITLESS OP"; kind = k.Contains("ED") ? "NCED" : "NCOP".
        // "CREDITLESS" contains "ED" (…cr-ED-itless…), so it is (wrongly) tagged NCED.
        // BUG?: substring test on "ED" collides with the word "creditless"; should anchor on OP/ED tokens.
        var e = Access.ParseEp("Creditless OP");
        Assert.Equal("NCED", e.kind);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4) OP / ED / PV / CM / Menu / Trailer / Preview / Teaser
    //    kind = uppercase token; ep is NEVER captured here (group 2 is discarded), so ep stays -1.
    //    Guarded by (?<![A-Za-z]) … (?![A-Za-z]) so it won't fire mid-word.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("OP", "OP")]
    [InlineData("ED", "ED")]
    [InlineData("PV", "PV")]
    [InlineData("CM", "CM")]
    [InlineData("Menu", "MENU")]
    [InlineData("Trailer", "TRAILER")]
    [InlineData("Preview", "PREVIEW")]
    [InlineData("Teaser", "TEASER")]
    public void Extras_family_kindOnly_noEp(string input, string kind)
    {
        var e = Access.ParseEp(input);
        Assert.Equal(kind, e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(-1, e.ep);      // number is intentionally ignored for these kinds
        Assert.Equal(-1, e.ep2);
        Assert.True(e.any);
    }

    [Theory]
    [InlineData("OP2", "OP")]        // trailing digit allowed by (?![A-Za-z]) but NOT stored
    [InlineData("ED03", "ED")]
    [InlineData("Preview 3", "PREVIEW")]
    public void Extras_family_ignoresTrailingNumber(string input, string kind)
    {
        var e = Access.ParseEp(input);
        Assert.Equal(kind, e.kind);
        Assert.Equal(-1, e.ep);      // BUG?: "OP2"/"ED03" number is dropped; only kind survives.
        Assert.True(e.any);
    }

    [Theory]
    [InlineData("OPening")]          // (?![A-Za-z]) blocks OP inside a word
    [InlineData("STOP")]             // (?<![A-Za-z]) blocks OP inside a word
    public void Extras_family_notMatchedMidWord(string input)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.False(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5) RANGE (E-form): (?:S\d{1,2})?E0*(\d{1,3})\s*-\s*E?0*(\d{1,3})  → kind=RANGE, ep, ep2.
    //    Evaluated AFTER StripNoise.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("E01-E12", 1, 12)]
    [InlineData("E1-12", 1, 12)]          // trailing E is optional
    [InlineData("S01E01-E12", 1, 12)]     // optional S\d prefix
    [InlineData("S1E5-E10", 5, 10)]
    public void Range_eForm(string input, int ep, int ep2)
    {
        var e = Access.ParseEp(input);
        Assert.Equal("RANGE", e.kind);
        Assert.Equal(-1, e.season);       // RANGE never records a season
        Assert.Equal(ep, e.ep);
        Assert.Equal(ep2, e.ep2);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6) RANGE (bare): (?:^|[\s._\[-])0*(\d{1,3})\s*-\s*0*(\d{1,3})(?=[\s._\]-]|$) → kind=RANGE.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("01-12", 1, 12)]
    [InlineData("12-24", 12, 24)]
    public void Range_bare(string input, int ep, int ep2)
    {
        var e = Access.ParseEp(input);
        Assert.Equal("RANGE", e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(ep, e.ep);
        Assert.Equal(ep2, e.ep2);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7) SxxEyy: (?<![A-Za-z0-9])S(\d{1,2})E0*(\d{1,3})(?!\d) → season + ep, kind=null.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("S01E05", 1, 5)]
    [InlineData("S1E5", 1, 5)]
    [InlineData("S12E123", 12, 123)]
    [InlineData("S3E7 720p x264", 3, 7)]              // noise stripped first
    [InlineData("Naruto S02E13 [BDRip]", 2, 13)]      // BDRip noise stripped
    [InlineData("S01E05 extra 1", 1, 5)]              // trailing bare number ignored
    // 7b) разделитель между S## и E##. Без него сезон не читался, срабатывало правило «E07»
    // (season=-1), и fail-open гейт охоты штамповал файлу сезон основной — 4 серии 2-го сезона
    // «Укрытия» попали в 3-й (инцидент 2026-08-09).
    [InlineData("Silo.S02.E07.Rus.Eng.by.Сибиряк", 2, 7)]
    [InlineData("Укрытие - Silo S03 E05 1080p", 3, 5)]
    [InlineData("Show_S02_E07", 2, 7)]
    [InlineData("Show S02-E07", 2, 7)]
    [InlineData("Show.S02.EP07", 2, 7)]
    [InlineData("Show S2 [E05]", 2, 5)]
    public void SeasonEpisode(string input, int season, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(season, e.season);
        Assert.Equal(ep, e.ep);
        Assert.Equal(-1, e.ep2);
        Assert.True(e.any);
    }

    // Разделитель не должен породить ложных сезонов: между S## и E## только пунктуация,
    // цифру или букву проглотить нельзя по построению шаблона.
    [Theory]
    [InlineData("S02 Extras 05")]
    [InlineData("S1 Episode 5")]
    [InlineData("Show S02 2024 07")]
    [InlineData("Dogulwang.EP01.2026.1080p.WEB-DL")]
    public void SeasonEpisode_Separator_NoFalsePositives(string input)
        => Assert.Equal(-1, Access.ParseEp(input).season);

    // RANGE-правило стоит ВЫШЕ — склейка серий не должна стать одиночкой из-за нового разделителя.
    [Theory]
    [InlineData("S01E01-E12", 1, 12)]
    [InlineData("Silo.S02.E07-E08", 7, 8)]
    public void SeasonEpisode_Separator_DoesNotStealRange(string input, int ep, int ep2)
    {
        var e = Access.ParseEp(input);
        Assert.Equal("RANGE", e.kind);
        Assert.Equal(ep, e.ep);
        Assert.Equal(ep2, e.ep2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8) NxNN: (?<![A-Za-z0-9])(\d{1,2})x0*(\d{1,3})(?!\d) → season + ep, kind=null.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("2x05", 2, 5)]
    [InlineData("10x03", 10, 3)]
    [InlineData("1x1", 1, 1)]
    [InlineData("13x99", 13, 99)]
    [InlineData("Anime_2x08_end", 2, 8)]
    public void SeasonX_Episode(string input, int season, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(season, e.season);
        Assert.Equal(ep, e.ep);
        Assert.Equal(-1, e.ep2);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9) EPn / E.n / En: (?<![A-Za-z0-9])E[Pp]?\.?\s*0*(\d{1,3})(?!\d) → ep only.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("EP7", 7)]
    [InlineData("E7", 7)]
    [InlineData("E.7", 7)]
    [InlineData("EP 07", 7)]
    [InlineData("Ep.05", 5)]
    public void EpisodeToken(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 10) Word form: (?:серия|episode|эпизод|вып(?:уск)?)\s*[№#]?\s*0*(\d{1,3})(?!\d) → ep only.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("серия 5", 5)]
    [InlineData("episode 12", 12)]
    [InlineData("эпизод 3", 3)]
    [InlineData("выпуск 8", 8)]
    [InlineData("вып 4", 4)]
    [InlineData("серия №7", 7)]
    [InlineData("Episode 007", 7)]     // leading zeros stripped
    public void WordEpisode(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 11) "#n": #0*(\d{1,3})(?!\d) → ep only.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("#7", 7)]
    [InlineData("#07", 7)]
    public void HashNumber(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 12) "- n": (?:^|\s)-\s+0*(\d{1,3})(?=\s|$|\[|\() → ep only. Note the dash needs surrounding space.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("- 5", 5)]
    [InlineData("Movie - 7", 7)]
    [InlineData("Bleach - 07", 7)]
    [InlineData("Show - 07 [1080p]", 7)]     // 1080p stripped, "- 07" then "[" lookahead
    [InlineData("Naruto 2020 - 05", 5)]      // year 2020 stripped, then dash form
    public void DashNumber(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 13) "[nn]": \[\s*0*(\d{1,3})\s*\] → ep only.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("[07]", 7)]
    [InlineData("[ 12 ]", 12)]
    public void BracketNumber(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 14) leading/underscore number: (?:^|[._ ])0*(\d{1,3})(?=[._ \[]|$) → ep only.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Naruto_07", 7)]
    [InlineData("07_extra", 7)]
    [InlineData("name.05.mkv", 5)]
    [InlineData("Show 01", 1)]
    [InlineData("vol 3", 3)]
    [InlineData("part 2", 2)]
    [InlineData("BDRip 12", 12)]       // "BDRip" is stripped as noise, leaving the number
    public void LeadingOrDelimitedNumber(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 15) bare number (whole trimmed string): ^0*(\d{1,3})$ → ep only.
    //     (Note: 1-3 digit numbers already satisfy the leading rule above; the bare rule is the
    //      final fallback. Either way the outcome for a pure number is ep = that number.)
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("5", 5)]
    [InlineData("07", 7)]
    [InlineData("007", 7)]
    [InlineData("10", 10)]
    [InlineData("100", 100)]
    public void BareNumber(string input, int ep)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(ep, e.ep);
        Assert.True(e.any);
    }

    [Fact]
    public void FourDigitNumber_isNotAnEpisode()
    {
        // "1000": \d{1,3} can only grab 3 of the 4 digits, but then the lookahead ([._ \[]|$ / end-anchor)
        // fails because a 4th digit follows. No branch matches → empty Ep.
        var e = Access.ParseEp("1000");
        Assert.Null(e.kind);
        Assert.Equal(-1, e.ep);
        Assert.False(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 16) StripNoise pre-pass: resolution / codecs / years / audio-layout / WxH must NOT be read as episodes.
    //     These strings contain digits, yet after noise removal there is nothing episode-like left.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Naruto 2019")]        // year
    [InlineData("Title 720p")]         // resolution
    [InlineData("Show 1080p x265")]    // resolution + codec
    [InlineData("Movie 5.1")]          // audio channel layout \b[257]\.[01]\b
    [InlineData("name 8bit aac")]      // bit-depth + audio codec
    [InlineData("1920x1080")]          // WxH resolution
    [InlineData("Rip 30fps")]          // frame rate
    public void Noise_isNotReadAsEpisode(string input)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(-1, e.ep);
        Assert.Equal(-1, e.ep2);
        Assert.False(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 17) No episode signal at all → empty Ep (any == false).
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Something")]
    [InlineData("")]
    [InlineData(null)]                 // ParseEp coalesces null → "" internally
    public void NoSignal_producesEmptyEp(string input)
    {
        var e = Access.ParseEp(input);
        Assert.Null(e.kind);
        Assert.Equal(-1, e.season);
        Assert.Equal(-1, e.ep);
        Assert.Equal(-1, e.ep2);
        Assert.False(e.any);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 18) Precedence: earlier branches win over later ones.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Precedence_SxxEyy_beatsBareNumber()
    {
        // "S01E05" must be parsed as season 1 / episode 5 — NOT as bare "1" or "5".
        var e = Access.ParseEp("S01E05");
        Assert.Null(e.kind);
        Assert.Equal(1, e.season);
        Assert.Equal(5, e.ep);
        Assert.Equal(-1, e.ep2);
    }

    [Fact]
    public void Precedence_RangeBeatsSingleEpisode()
    {
        // The RANGE (E-form) rule is checked before SxxEyy, so "S01E01-E12" is a range, not S1E1.
        var e = Access.ParseEp("S01E01-E12");
        Assert.Equal("RANGE", e.kind);
        Assert.Equal(1, e.ep);
        Assert.Equal(12, e.ep2);
        Assert.Equal(-1, e.season);
    }

    [Fact]
    public void Precedence_OvaKindBeatsTrailingEpisodeNumberRules()
    {
        // "OVA 03" is classified by the very first (OVA) rule as kind=OVA, ep=3 —
        // it never reaches the generic leading/bare-number fallbacks.
        var e = Access.ParseEp("OVA 03");
        Assert.Equal("OVA", e.kind);
        Assert.Equal(3, e.ep);
    }
}
