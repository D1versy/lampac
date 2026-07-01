using System;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for the studio-naming helpers in <c>QbitController</c>:
/// StripNoise, IsGenericFolder, CleanStudio, StudioId, StudioOf, NormStarts.
/// Assertions reflect the ACTUAL current behavior of the production code.
/// </summary>
public class StudioHelpersTests
{
    // ───────────────────────── StripNoise ─────────────────────────
    // StripNoise replaces each matched noise token with a single space; it does NOT collapse
    // or trim the result. We therefore assert that noise tokens are gone (and, where useful,
    // that surrounding real words survive).

    [Theory]
    // years 19xx / 20xx
    [InlineData("Show 1999 E01", "1999")]
    [InlineData("Show 2021 E01", "2021")]
    // resolution NNNp / NNNi
    [InlineData("Movie 1080p x264", "1080p")]
    [InlineData("Movie 720i", "720i")]
    // bare resolution numbers
    [InlineData("Movie 2160 rip", "2160")]
    [InlineData("Movie 480 rip", "480")]
    // codecs
    [InlineData("A x264 B", "x264")]
    [InlineData("A h265 B", "h265")]
    [InlineData("A HEVC B", "HEVC")]
    [InlineData("A av1 B", "av1")]
    [InlineData("A xvid B", "xvid")]
    // bit-depth
    [InlineData("A 10bit B", "10bit")]
    [InlineData("A 8 bit B", "8 bit")]
    // audio codecs
    [InlineData("A AAC B", "AAC")]
    [InlineData("A ac3 B", "ac3")]
    [InlineData("A dts-hd B", "dts-hd")]
    [InlineData("A flac B", "flac")]
    [InlineData("A opus B", "opus")]
    [InlineData("A mp3 B", "mp3")]
    // rate units
    [InlineData("A 23.976 fps B", "fps")]
    [InlineData("A 320 kbps B", "kbps")]
    [InlineData("A 48khz B", "48khz")]
    // source tags
    [InlineData("A BDRip B", "BDRip")]
    [InlineData("A WEB-DL B", "WEB-DL")]
    [InlineData("A webrip B", "webrip")]
    [InlineData("A remux B", "remux")]
    // channel layouts
    [InlineData("A 5.1 B", "5.1")]
    [InlineData("A 2.0 B", "2.0")]
    [InlineData("A 7.1 B", "7.1")]
    // dimensions WxH
    [InlineData("A 1920x1080 B", "1920x1080")]
    public void StripNoise_RemovesNoiseTokens(string input, string removedToken)
    {
        string result = Access.StripNoise(input);
        Assert.DoesNotContain(removedToken, result);
    }

    [Fact]
    public void StripNoise_KeepsSurroundingRealWords()
    {
        // "AniLibria" is not a noise token; the year is removed and replaced with a space.
        string result = Access.StripNoise("AniLibria 2021 1080p");
        Assert.Contains("AniLibria", result);
        Assert.DoesNotContain("2021", result);
        Assert.DoesNotContain("1080p", result);
    }

    [Fact]
    public void StripNoise_LeavesNonNoiseStringUnchanged()
    {
        Assert.Equal("Just A Studio Name", Access.StripNoise("Just A Studio Name"));
    }

    [Fact]
    public void StripNoise_ReplacesWithSpaceNotEmpty()
    {
        // A pure noise token becomes a single space, not an empty string.
        Assert.Equal(" ", Access.StripNoise("2021"));
    }

    [Fact]
    public void StripNoise_EmptyString()
    {
        Assert.Equal("", Access.StripNoise(""));
    }

    // ───────────────────────── IsGenericFolder ─────────────────────────

    [Theory]
    [InlineData("rus")]
    [InlineData("Rus")]
    [InlineData("RUS")]
    [InlineData("sound")]
    [InlineData("sounds")]
    [InlineData("Sound")]
    [InlineData("rus sound")]      // rus[ ._-]?sound
    [InlineData("rus.sound")]
    [InlineData("rus_sound")]
    [InlineData("rus-sound")]
    [InlineData("russound")]
    [InlineData("rus sounds")]
    [InlineData("audio")]
    [InlineData("Audio")]
    [InlineData("звук")]
    [InlineData("озвучка")]        // озвучк\w*
    [InlineData("озвучки")]
    [InlineData("Озвучка")]
    [InlineData("voice")]
    [InlineData("dub")]
    [InlineData("дубляж")]
    [InlineData("перевод")]        // переводы?
    [InlineData("переводы")]
    [InlineData("дорожка")]        // дорожк\w*
    [InlineData("дорожки")]
    [InlineData("track")]          // tracks?
    [InlineData("tracks")]
    [InlineData("русский")]        // русск\w*
    [InlineData("русская")]
    [InlineData("  audio  ")]      // trimmed before match
    public void IsGenericFolder_TrueForGenericNames(string name)
    {
        Assert.True(Access.IsGenericFolder(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void IsGenericFolder_TrueForBlankOrNull(string name)
    {
        Assert.True(Access.IsGenericFolder(name));
    }

    [Theory]
    [InlineData("AniLibria")]
    [InlineData("AniDUB")]          // contains "dub" but not exactly "dub"
    [InlineData("SHIZA Project")]
    [InlineData("Kansai")]
    [InlineData("rus sound extra")] // extra token breaks the whole-string anchor
    [InlineData("audiobook")]       // not exactly "audio"
    [InlineData("voices")]          // regex is "voice" not "voice\w*"
    [InlineData("soundtrack")]      // not "sound" nor "sounds" alone
    public void IsGenericFolder_FalseForRealStudioNames(string name)
    {
        Assert.False(Access.IsGenericFolder(name));
    }

    // ───────────────────────── CleanStudio ─────────────────────────

    [Theory]
    [InlineData("AniLibria", "AniLibria")]
    [InlineData("Ani.Libria", "Ani Libria")]        // [._]+ -> space
    [InlineData("Ani_Libria", "Ani Libria")]
    [InlineData("Ani...Libria", "Ani Libria")]       // collapsed
    [InlineData("Ani  Libria", "Ani Libria")]        // \s{2,} collapsed
    [InlineData("[AniLibria]", "AniLibria")]          // brackets trimmed
    [InlineData("(AniLibria)", "AniLibria")]
    [InlineData("-AniLibria-", "AniLibria")]
    [InlineData("  AniLibria  ", "AniLibria")]
    [InlineData("_._AniLibria_._", "AniLibria")]
    public void CleanStudio_CollapsesAndTrims(string input, string expected)
    {
        Assert.Equal(expected, Access.CleanStudio(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("...")]
    [InlineData("[]")]
    [InlineData("-_-")]
    public void CleanStudio_BlankReturnsОзвучка(string input)
    {
        Assert.Equal("Озвучка", Access.CleanStudio(input));
    }

    [Fact]
    public void CleanStudio_KeepsInteriorHyphen()
    {
        // Only leading/trailing '-' are trimmed; interior hyphens survive.
        Assert.Equal("Ani-Libria", Access.CleanStudio("Ani-Libria"));
    }

    // ───────────────────────── StudioId (FNV-1a) ─────────────────────────

    [Fact]
    public void StudioId_StartsWithD_AndIsNineChars()
    {
        string id = Access.StudioId("AniLibria");
        Assert.StartsWith("d", id);
        Assert.Equal(9, id.Length); // 'd' + 8 hex digits
    }

    [Fact]
    public void StudioId_Deterministic()
    {
        Assert.Equal(Access.StudioId("AniLibria"), Access.StudioId("AniLibria"));
    }

    [Theory]
    [InlineData("AniLibria", "anilibria")]
    [InlineData("AniLibria", "Ani.Libria")]
    [InlineData("AniLibria", "Ani Libria")]
    [InlineData("AniLibria", "Ani_Libria")]
    [InlineData("AniLibria", "Ani-Libria")]
    [InlineData("AniLibria", "  ANI LIBRIA  ")]
    public void StudioId_InsensitiveToCaseSpaceSeparators(string a, string b)
    {
        Assert.Equal(Access.StudioId(a), Access.StudioId(b));
    }

    [Fact]
    public void StudioId_DiffersForDifferentStudios()
    {
        Assert.NotEqual(Access.StudioId("AniLibria"), Access.StudioId("SHIZA"));
    }

    [Fact]
    public void StudioId_NullAndEmptyEqual()
    {
        // (studio ?? "") makes null equivalent to empty.
        Assert.Equal(Access.StudioId(null), Access.StudioId(""));
    }

    [Fact]
    public void StudioId_KnownFnvValueForEmpty()
    {
        // FNV-1a offset basis over an empty string is the offset basis itself: 0x811c9dc5.
        Assert.Equal("d811c9dc5", Access.StudioId(""));
    }

    // ───────────────────────── StudioOf ─────────────────────────

    [Fact]
    public void StudioOf_SuffixAfterVideoBase()
    {
        // fbase "Show E05 AniLibria" starts with videoBase "Show E05" and is longer:
        // suffix "AniLibria" (after trimming) is used.
        string s = Access.StudioOf("Show/Show E05 AniLibria.mka", "Show E05");
        Assert.Equal("AniLibria", s);
    }

    [Fact]
    public void StudioOf_SuffixTrimsSeparators()
    {
        string s = Access.StudioOf("Show/Show E05 - [AniLibria].mka", "Show E05");
        Assert.Equal("AniLibria", s);
    }

    [Fact]
    public void StudioOf_FirstNonGenericParentFolder()
    {
        // fbase == videoBase (no suffix), so fall through to parent-folder scan.
        // parts: [Show, Rus Sound, AniLibria, Show E05.mka]; scan i=2..1:
        // parts[2]="AniLibria" is non-generic -> returned.
        string s = Access.StudioOf("Show/Rus Sound/AniLibria/Show E05.mka", "Show E05");
        Assert.Equal("AniLibria", s);
    }

    [Fact]
    public void StudioOf_SkipsGenericParentFolders()
    {
        // parts: [Show, AniLibria, Rus Sound, Show E05.mka]; scan i=2..1:
        // parts[2]="Rus Sound" generic -> skip; parts[1]="AniLibria" non-generic -> returned.
        string s = Access.StudioOf("Show/AniLibria/Rus Sound/Show E05.mka", "Show E05");
        Assert.Equal("AniLibria", s);
    }

    [Fact]
    public void StudioOf_BackslashesNormalized()
    {
        string s = Access.StudioOf(@"Show\AniLibria\Show E05.mka", "Show E05");
        Assert.Equal("AniLibria", s);
    }

    [Fact]
    public void StudioOf_SuffixWinsOverParentAndPrefix()
    {
        // fbase "Show 05 Kansai" starts with videoBase "Show 05" and is longer, so the
        // suffix branch fires first and returns "Kansai" (before parent/prefix branches).
        string s = Access.StudioOf("Rus Sound/Show 05 Kansai.mka", "Show 05");
        Assert.Equal("Kansai", s);
    }

    [Fact]
    public void StudioOf_CommonPrefixRemainder()
    {
        // No suffix (fbase "Ep05 Kansai" does NOT start with videoBase "Show 05"),
        // only a generic parent folder, so we reach the common-prefix remainder branch.
        // StripNoise+prefix: na "Ep05 Kansai" vs nv "Show 05" share prefix length 0 (E vs S),
        // remainder = whole na with episode/number tokens (Ep05, 05) stripped -> "Kansai".
        string s = Access.StudioOf("Rus Sound/Ep05 Kansai.mka", "Show 05");
        Assert.Equal("Kansai", s);
    }

    [Fact]
    public void StudioOf_BracketContentSurvivesViaRemainder()
    {
        // fbase "05 [Kansai]" vs videoBase "Episode": no suffix, generic parent only.
        // Common-prefix branch: remainder strips the bare number "05" and trims the brackets,
        // leaving "Kansai" -> returned before the dedicated [bracket] fallback is reached.
        string s = Access.StudioOf("Rus Sound/05 [Kansai].mka", "Episode");
        Assert.Equal("Kansai", s);
    }

    [Fact]
    public void StudioOf_GenericBracketFallsThroughToDefault()
    {
        // fbase "05 [dub]": common-prefix remainder strips "05", trims brackets -> "dub",
        // but "dub" is generic (^dub$) ... note the remainder branch does NOT check
        // IsGenericFolder, only rejects ^\d+$, so "dub" IS returned as-is.
        // BUG?: a generic bracket tag like [dub]/[rus] leaks through the common-prefix
        // remainder branch (which only rejects all-digit remainders), so the dedicated
        // "generic bracket -> Озвучка" guard on the final branch is bypassed here.
        string s = Access.StudioOf("Rus Sound/05 [dub].mka", "Episode");
        Assert.Equal("dub", s);
    }

    [Fact]
    public void StudioOf_DefaultОзвучка()
    {
        // fbase "05" doesn't start with videoBase, only generic parent, remainder is a bare
        // number, no bracket tag -> default.
        string s = Access.StudioOf("Rus Sound/05.mka", "Episode");
        Assert.Equal("Озвучка", s);
    }

    [Fact]
    public void StudioOf_NullPath()
    {
        // (fullPath ?? "") -> "" ; fbase "" ; no branch matches -> default.
        string s = Access.StudioOf(null, "Show E05");
        Assert.Equal("Озвучка", s);
    }

    [Fact]
    public void StudioOf_NoParentFoldersFallsThrough()
    {
        // Single-segment path: parts=["05.mka"], parent loop (i from -1) never runs.
        // fbase "05" doesn't start with videoBase "Episode"; remainder is bare number;
        // no bracket -> default.
        string s = Access.StudioOf("05.mka", "Episode");
        Assert.Equal("Озвучка", s);
    }

    [Fact]
    public void StudioOf_SuffixBranchDoesNotStripNoise()
    {
        // fbase "Show 05 1080p Kansai" starts with videoBase "Show 05" -> suffix branch.
        // The suffix branch uses CleanStudio (which does NOT call StripNoise), so quality
        // noise such as "1080p" survives here.
        // BUG?: only the common-prefix branch strips technical noise; the (earlier, higher
        // priority) suffix branch does not, so studios detected via suffix keep quality tags.
        string s = Access.StudioOf("Rus Sound/Show 05 1080p Kansai.mka", "Show 05");
        Assert.Equal("1080p Kansai", s);
    }

    [Fact]
    public void StudioOf_CommonPrefixBranchStripsNoise()
    {
        // Reaches the common-prefix branch (fbase "Ep05 1080p Kansai" does not start with
        // videoBase "Show 05"): StripNoise removes "1080p" and the episode token is stripped,
        // leaving "Kansai".
        string s = Access.StudioOf("Rus Sound/Ep05 1080p Kansai.mka", "Show 05");
        Assert.Equal("Kansai", s);
    }

    // ───────────────────────── NormStarts ─────────────────────────

    [Theory]
    [InlineData("Show E05 AniLibria", "Show E05", true)]
    [InlineData("Show_E05_AniLibria", "Show E05", true)]   // '_' -> ' '
    [InlineData("Show.E05.AniLibria", "Show E05", true)]   // '.' -> ' '
    [InlineData("show e05 anilibria", "SHOW E05", true)]   // case-insensitive
    [InlineData("Show_E05", "Show.E05", true)]             // both normalized
    [InlineData("  Show E05 X", "Show E05", true)]         // leading ws trimmed on a
    [InlineData("Other Show", "Show", false)]              // no prefix match
    [InlineData("Sho", "Show", false)]                     // b longer than a
    public void NormStarts_PrefixInsensitive(string a, string b, bool expected)
    {
        Assert.Equal(expected, Access.NormStarts(a, b));
    }

    [Theory]
    [InlineData("Anything", "")]     // b empty -> false
    [InlineData("Anything", null)]   // b null -> "" -> false
    [InlineData("Anything", "   ")]  // b whitespace -> trims to "" -> false
    [InlineData("Anything", "._")]   // b "._" -> "  " -> trim "" -> false
    public void NormStarts_FalseWhenPrefixBlank(string a, string b)
    {
        Assert.False(Access.NormStarts(a, b));
    }

    [Fact]
    public void NormStarts_NullSubjectWithNonEmptyPrefix()
    {
        // a null -> "" ; b "Show" length>0 ; "".StartsWith("Show") -> false.
        Assert.False(Access.NormStarts(null, "Show"));
    }

    [Fact]
    public void NormStarts_BothNull()
    {
        // a "" , b "" -> b.Length==0 -> false.
        Assert.False(Access.NormStarts(null, null));
    }
}
