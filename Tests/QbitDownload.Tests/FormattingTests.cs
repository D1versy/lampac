using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for pure formatting helpers of <c>QbitController</c>:
/// HumanSize, QualityFromTitle, LangName, SeriesKey.
/// </summary>
public class FormattingTests
{
    // ─────────────────────────── HumanSize ───────────────────────────
    // Code: if (b <= 0) return ""; then divide by 1024 while s>=1024 && i<4.
    // Units {B,KB,MB,GB,TB}. i>=3 (GB/TB) -> "0.0"; else -> "0" (integer).

    [Theory]
    [InlineData(0L, "")]
    [InlineData(-1L, "")]
    [InlineData(-1024L, "")]
    [InlineData(long.MinValue, "")]
    public void HumanSize_NonPositive_ReturnsEmpty(long bytes, string expected)
        => Assert.Equal(expected, Access.HumanSize(bytes));

    [Theory]
    // Bytes (integer format, no decimal)
    [InlineData(1L, "1 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]          // just below KB boundary
    // KB boundary
    [InlineData(1024L, "1 KB")]            // exact 1 KB
    [InlineData(1536L, "2 KB")]            // 1.5 KB -> "0" format rounds to 2
    [InlineData(2048L, "2 KB")]
    [InlineData(1024L * 1024 - 1, "1024 KB")] // 1048575 -> 1023.999.. KB rounds to 1024
    // MB boundary (integer format)
    [InlineData(1024L * 1024, "1 MB")]     // exact 1 MB
    [InlineData(1024L * 1024 * 5, "5 MB")]
    [InlineData((long)(1024L * 1024 * 1.5), "2 MB")] // 1.5 MB -> "0" rounds to 2
    [InlineData(1024L * 1024 * 1024 - 1, "1024 MB")] // just below GB rounds up
    // GB boundary (one decimal)
    [InlineData(1024L * 1024 * 1024, "1.0 GB")]         // exact 1 GB
    [InlineData(1610612736L, "1.5 GB")]                 // 1.5 GB
    [InlineData(1024L * 1024 * 1024 * 2, "2.0 GB")]
    [InlineData(1024L * 1024 * 1024 * 10, "10.0 GB")]
    // TB boundary (one decimal, top unit — no further division)
    [InlineData(1024L * 1024 * 1024 * 1024, "1.0 TB")]  // exact 1 TB
    [InlineData(1024L * 1024L * 1024 * 1024 * 1024, "1024.0 TB")] // 1 PB stays in TB (i capped)
    [InlineData(1024L * 1024L * 1024 * 1024 * 2048, "2048.0 TB")]
    public void HumanSize_Boundaries(long bytes, string expected)
        => Assert.Equal(expected, Access.HumanSize(bytes));

    // ───────────────────────── QualityFromTitle ─────────────────────────
    // Regex: (2160|1080|720|480)p? IgnoreCase. Returns int.Parse of group, else 0.

    [Theory]
    [InlineData("Movie.2160p.mkv", 2160)]
    [InlineData("Movie.1080p.mkv", 1080)]
    [InlineData("Movie.720p.mkv", 720)]
    [InlineData("Movie.480p.mkv", 480)]
    // trailing 'p' is optional
    [InlineData("Movie.2160.mkv", 2160)]
    [InlineData("Movie.1080.mkv", 1080)]
    [InlineData("Movie.720.mkv", 720)]
    [InlineData("Movie.480.mkv", 480)]
    // case-insensitive on the optional 'p'
    [InlineData("Movie.1080P.mkv", 1080)]
    [InlineData("Movie.720P", 720)]
    // absent -> 0
    [InlineData("Movie.mkv", 0)]
    [InlineData("SomeTitle", 0)]
    [InlineData("", 0)]
    // null coerced to "" via (t ?? "")
    [InlineData(null, 0)]
    // 4K written as literal is not matched (only listed tokens)
    [InlineData("Movie.4K.mkv", 0)]
    // first match wins (leftmost)
    [InlineData("1080 and 720", 1080)]
    [InlineData("720 and 1080", 720)]
    // digit substrings still match even embedded in larger numbers (regex is unanchored)
    [InlineData("x2160y", 2160)]
    [InlineData("11080p", 1080)]  // "1080p" matches inside "11080p"
    public void QualityFromTitle_Cases(string title, int expected)
        => Assert.Equal(expected, Access.QualityFromTitle(title));

    // ─────────────────────────── LangName ───────────────────────────
    // switch on (l ?? "").ToLowerInvariant().

    [Theory]
    [InlineData("jpn", "Японский")]
    [InlineData("ja", "Японский")]
    [InlineData("JPN", "Японский")]
    [InlineData("Ja", "Японский")]
    [InlineData("eng", "Английский")]
    [InlineData("en", "Английский")]
    [InlineData("ENG", "Английский")]
    [InlineData("En", "Английский")]
    [InlineData("rus", "Русский")]
    [InlineData("ru", "Русский")]
    [InlineData("RUS", "Русский")]
    [InlineData("Ru", "Русский")]
    [InlineData("", "Оригинал")]
    public void LangName_Known(string lang, string expected)
        => Assert.Equal(expected, Access.LangName(lang));

    [Fact]
    public void LangName_Null_ReturnsOriginal()
        => Assert.Equal("Оригинал", Access.LangName(null));  // (null ?? "") -> "" -> "Оригинал"

    [Theory]
    // unknown values pass through UNCHANGED (returns original `l`, not lowercased).
    // ⚠️ «fre» больше не пример неизвестного: в qdl 2.24 словарь расширен (fre → Французский).
    [InlineData("swe", "swe")]
    [InlineData("Deutsch", "Deutsch")]
    [InlineData("XYZ", "XYZ")]          // case preserved on passthrough
    [InlineData("English", "English")] // not exactly "eng"/"en"
    [InlineData("русский", "русский")] // cyrillic literal is not a known key
    public void LangName_Unknown_PassesThrough(string lang, string expected)
        => Assert.Equal(expected, Access.LangName(lang));

    // ─────────────────────────── SeriesKey ───────────────────────────
    // id>0 -> "t"+id ; else "l"+FNV1a(link) as 8 hex chars.

    [Theory]
    [InlineData(1, "t1")]
    [InlineData(42, "t42")]
    [InlineData(999999, "t999999")]
    public void SeriesKey_PositiveId_UsesTmdbKey(int id, string expected)
        => Assert.Equal(expected, Access.SeriesKey(id, "ignored-link"));

    [Fact]
    public void SeriesKey_PositiveId_IgnoresLink()
    {
        // With a positive id the link is irrelevant.
        Assert.Equal("t5", Access.SeriesKey(5, "http://a"));
        Assert.Equal("t5", Access.SeriesKey(5, "http://b"));
        Assert.Equal("t5", Access.SeriesKey(5, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void SeriesKey_NonPositiveId_UsesLinkHash(int id)
    {
        string k = Access.SeriesKey(id, "magnet:?xt=urn:btih:abc");
        Assert.StartsWith("l", k);
        Assert.Equal(9, k.Length);            // 'l' + 8 hex chars
        // 8 lowercase hex digits after the 'l'
        Assert.Matches("^l[0-9a-f]{8}$", k);
    }

    [Fact]
    public void SeriesKey_Link_IsDeterministic_And_SameForSameLink()
    {
        string a = Access.SeriesKey(0, "http://example/link");
        string b = Access.SeriesKey(0, "http://example/link");
        Assert.Equal(a, b);
        // negative id path produces the same hash as id==0 for the same link
        Assert.Equal(a, Access.SeriesKey(-7, "http://example/link"));
    }

    [Fact]
    public void SeriesKey_Link_DiffersPerLink()
    {
        string a = Access.SeriesKey(0, "http://example/one");
        string b = Access.SeriesKey(0, "http://example/two");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SeriesKey_NullOrEmptyLink_HashesEmptyString()
    {
        // link ?? "" -> both null and "" hash the empty string == FNV-1a offset basis 0x811c9dc5.
        string expected = "l811c9dc5";
        Assert.Equal(expected, Access.SeriesKey(0, null));
        Assert.Equal(expected, Access.SeriesKey(0, ""));
    }

    [Fact]
    public void SeriesKey_KnownFnvVector()
    {
        // FNV-1a 32-bit of "a" = 0xe40c292c (well-known test vector).
        Assert.Equal("le40c292c", Access.SeriesKey(0, "a"));
    }
}
