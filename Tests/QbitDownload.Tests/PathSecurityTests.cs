using System;
using System.IO;
using System.Text;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for the path-security / content-sniffing helpers:
/// <c>ValidHash</c>, <c>ConfinedCombine</c>, <c>MimeType</c>, <c>LooksLikeTorrent</c>.
/// Assertions capture the ACTUAL current behavior of the production code.
/// </summary>
public class PathSecurityTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // ValidHash — regex ^([0-9a-fA-F]{40}|[0-9A-Za-z]{32})$ ; null/empty -> false
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    // 40-char hex (SHA-1 / btih)
    [InlineData("0123456789abcdef0123456789abcdef01234567")] // 40 lower hex
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")] // 40 upper hex
    [InlineData("aAbBcCdDeEfF0123456789aAbBcCdDeEfF012345")]  // 40 mixed hex
    [InlineData("ffffffffffffffffffffffffffffffffffffffff")]  // 40 all f
    public void ValidHash_True_For40Hex(string h)
    {
        Assert.True(Access.ValidHash(h));
    }

    [Theory]
    // 32-char alternative is [0-9A-Za-z]{32} — alphanumeric (NOT strictly base32).
    [InlineData("0123456789abcdef0123456789abcdef")]   // 32 hex-ish lower
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]   // 32 upper + digits
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]   // 32 lower + digits
    [InlineData("gggggggggggggggggggggggggggggggg")]   // 32 non-hex letters (still alnum)
    [InlineData("00000000000000000000000000000000")]   // 32 digits
    [InlineData("Zz9Zz9Zz9Zz9Zz9Zz9Zz9Zz9Zz9Zz9Zz")]   // 32 mixed alnum
    public void ValidHash_True_For32Alnum(string h)
    {
        Assert.True(Access.ValidHash(h));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]                                          // too short
    [InlineData("0123456789abcdef0123456789abcdef012345")]       // 38 chars
    [InlineData("0123456789abcdef0123456789abcdef0123456")]      // 39 chars
    [InlineData("0123456789abcdef0123456789abcdef012345678")]    // 41 chars
    [InlineData("0123456789abcdef0123456789abcde")]              // 31 chars
    [InlineData("0123456789abcdef0123456789abcdef0")]            // 33 chars
    [InlineData("0123456789abcdef0123456789abcdefg")]            // 33 chars, non-hex g
    [InlineData("0123456789abcdef0123456789abcde!")]             // 32 with symbol '!'
    [InlineData("0123456789abcdef 123456789abcdef")]             // 32 with space
    [InlineData("0123456789abcdef0123456789abcde-")]             // 32 with hyphen
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]     // 40 with non-hex g -> not hex, len!=32
    public void ValidHash_False_For_BadInputs(string h)
    {
        Assert.False(Access.ValidHash(h));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ConfinedCombine — drop ""/"."/".." ; confined absolute path; null on escape
    // ─────────────────────────────────────────────────────────────────────────

    static string Base => Path.GetTempPath();

    [Fact]
    public void Confined_SimpleFile_StaysInsideBase()
    {
        string res = Access.ConfinedCombine(Base, "movie.mp4");
        Assert.NotNull(res);
        Assert.StartsWith(Path.GetFullPath(Base), res, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("movie.mp4", Path.GetFileName(res));
    }

    [Fact]
    public void Confined_NestedSubdirs_Preserved()
    {
        string res = Access.ConfinedCombine(Base, "season1/ep2/video.mkv");
        Assert.NotNull(res);
        Assert.StartsWith(Path.GetFullPath(Base), res, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("video.mkv", Path.GetFileName(res));
        Assert.Contains("season1", res, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ep2", res, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confined_BackslashSeparators_Normalized()
    {
        string res = Access.ConfinedCombine(Base, @"dir\sub\file.avi");
        Assert.NotNull(res);
        Assert.StartsWith(Path.GetFullPath(Base), res, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("file.avi", Path.GetFileName(res));
    }

    [Fact]
    public void Confined_DotAndEmptySegments_Dropped()
    {
        // "./a//./b/./c.mp4" -> a/b/c.mp4
        string res = Access.ConfinedCombine(Base, "./a//./b/./c.mp4");
        Assert.NotNull(res);
        Assert.StartsWith(Path.GetFullPath(Base), res, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("c.mp4", Path.GetFileName(res));
        Assert.Contains("a", res, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("b", res, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confined_LeadingDotDotButNetInside_StaysInside()
    {
        // "../keep/x.mp4": the ".." is dropped (not resolved), so it collapses to keep/x.mp4 inside base.
        string res = Access.ConfinedCombine(Base, "../keep/x.mp4");
        Assert.NotNull(res);
        Assert.StartsWith(Path.GetFullPath(Base), res, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("x.mp4", Path.GetFileName(res));
        Assert.Contains("keep", res, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]                 // empty rel -> null (IsNullOrEmpty)
    [InlineData(".")]                // all segments dropped -> null
    [InlineData("..")]               // all segments dropped -> null
    [InlineData("./.")]              // all dropped -> null
    [InlineData("../..")]            // all dropped -> null
    [InlineData("/")]                // splits to two empties -> null
    [InlineData("\\")]               // backslash -> two empties -> null
    [InlineData("./")]               // dot + empty -> null
    public void Confined_AllSegmentsDropped_ReturnsNull(string rel)
    {
        Assert.Null(Access.ConfinedCombine(Base, rel));
    }

    [Theory]
    [InlineData(null, "file.mp4")]
    [InlineData("", "file.mp4")]
    [InlineData(@"C:\downloads", null)]
    [InlineData(@"C:\downloads", "")]
    public void Confined_NullOrEmptyArgs_ReturnsNull(string baseDir, string rel)
    {
        Assert.Null(Access.ConfinedCombine(baseDir, rel));
    }

    [Fact]
    public void Confined_DriveLetterInRel_EscapesBase_ReturnsNull()
    {
        // Segments ".." are dropped, but "C:" is kept as a segment. Path.Combine with a segment
        // containing a drive/root can rebase the candidate outside baseDir -> null.
        // Using a base that is NOT on C: guarantees the drive-letter segment escapes.
        string res = Access.ConfinedCombine(@"D:\safe", @"C:\Windows\system32\evil.mp4");
        // "C:" is a segment; join gives "C:/Windows/system32/evil.mp4"; Path.Combine treats a
        // rooted second arg as absolute -> escapes D:\safe -> null.
        Assert.Null(res);
    }

    [Fact]
    public void Confined_UncStyleSegments_DoNotEscape_WhenDropped()
    {
        // Pure ".." traversal segments are dropped, so an attempt like "..\..\..\x" collapses inside base.
        string res = Access.ConfinedCombine(Base, @"..\..\..\x.mkv");
        Assert.NotNull(res);
        Assert.StartsWith(Path.GetFullPath(Base), res, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("x.mkv", Path.GetFileName(res));
    }

    [Fact]
    public void Confined_ResultIsAbsolute()
    {
        string res = Access.ConfinedCombine(@"C:\downloads", "sub/movie.mp4");
        Assert.NotNull(res);
        Assert.True(Path.IsPathRooted(res));
        Assert.StartsWith(@"C:\downloads", res, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confined_CaseInsensitive_BaseMatch()
    {
        // Windows comparison is OrdinalIgnoreCase; a lower/upper mix of the base still confines.
        string b = @"C:\Downloads";
        string res = Access.ConfinedCombine(b, "x.mp4");
        Assert.NotNull(res);
        Assert.StartsWith(@"c:\downloads", res, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MimeType — by extension, case-insensitive; unknown -> application/octet-stream
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.mp4", "video/mp4")]
    [InlineData("a.m4v", "video/mp4")]
    [InlineData("a.mkv", "video/x-matroska")]
    [InlineData("a.avi", "video/x-msvideo")]
    [InlineData("a.ts", "video/mp2t")]
    [InlineData("a.webm", "video/webm")]
    [InlineData("a.mov", "video/quicktime")]
    // case-insensitive
    [InlineData("A.MP4", "video/mp4")]
    [InlineData("A.M4V", "video/mp4")]
    [InlineData("A.MKV", "video/x-matroska")]
    [InlineData("A.Avi", "video/x-msvideo")]
    [InlineData("A.Ts", "video/mp2t")]
    [InlineData("A.WebM", "video/webm")]
    [InlineData("A.MOV", "video/quicktime")]
    // full paths still resolve by extension
    [InlineData(@"C:\downloads\season 1\ep.mkv", "video/x-matroska")]
    [InlineData("dir/sub/clip.mp4", "video/mp4")]
    public void MimeType_KnownExtensions(string path, string expected)
    {
        Assert.Equal(expected, Access.MimeType(path));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.srt")]
    [InlineData("a.mp3")]
    [InlineData("noextension")]
    [InlineData("a.")]          // GetExtension -> ""
    [InlineData("a.mp4.part")]  // extension is ".part"
    [InlineData("")]            // GetExtension("") -> ""
    [InlineData("archive.tar.gz")]
    [InlineData(".mkvhidden")]  // no dot after -> extension ".mkvhidden"
    public void MimeType_Unknown_ReturnsOctetStream(string path)
    {
        Assert.Equal("application/octet-stream", Access.MimeType(path));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LooksLikeTorrent — true only if len>=50 AND first non-ws byte is 'd' (0x64)
    // ─────────────────────────────────────────────────────────────────────────

    static byte[] Pad(string prefix, int total)
    {
        var head = Encoding.ASCII.GetBytes(prefix);
        var buf = new byte[Math.Max(total, head.Length)];
        Array.Copy(head, buf, head.Length);
        // remaining bytes stay 0x00 — fine, they're past the significant byte.
        return buf;
    }

    [Fact]
    public void Torrent_True_ForBencodeDict_LongEnough()
    {
        // 'd' at start, length >= 50.
        byte[] data = Pad("d8:announce", 60);
        Assert.True(Access.LooksLikeTorrent(data));
    }

    [Fact]
    public void Torrent_True_WithLeadingWhitespace()
    {
        // whitespace (space/tab/cr/lf) skipped, then 'd'.
        byte[] data = Pad(" \t\r\nd8:announce", 60);
        Assert.True(Access.LooksLikeTorrent(data));
    }

    [Fact]
    public void Torrent_True_ExactlyLength50WithD()
    {
        byte[] data = Pad("d", 50);
        Assert.True(Access.LooksLikeTorrent(data));
    }

    [Theory]
    [InlineData(49)] // one short
    [InlineData(10)]
    [InlineData(0)]
    public void Torrent_False_TooShort(int len)
    {
        byte[] data = Pad("d8:announce", len);
        // truncate to exact len
        Array.Resize(ref data, len);
        if (len > 0) data[0] = (byte)'d';
        Assert.False(Access.LooksLikeTorrent(data));
    }

    [Fact]
    public void Torrent_False_Null()
    {
        Assert.False(Access.LooksLikeTorrent(null));
    }

    [Fact]
    public void Torrent_False_HtmlContent()
    {
        byte[] data = Pad("<!DOCTYPE html><html><head><title>x</title></head></html>", 80);
        Assert.False(Access.LooksLikeTorrent(data));
    }

    [Fact]
    public void Torrent_False_LeadingJunkBeforeD()
    {
        // Non-whitespace junk 'x' before 'd' — not skipped -> first significant byte is 'x'.
        byte[] data = Pad("xd8:announce", 60);
        Assert.False(Access.LooksLikeTorrent(data));
    }

    [Fact]
    public void Torrent_False_AllWhitespace()
    {
        // Only whitespace: index walks to end, i==Length -> false, even though long.
        var buf = new byte[60];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)' ';
        Assert.False(Access.LooksLikeTorrent(buf));
    }

    [Fact]
    public void Torrent_False_AllZeros_LongEnough()
    {
        // 0x00 is not whitespace and not 'd' -> first significant byte is 0x00 -> false.
        var buf = new byte[60];
        Assert.False(Access.LooksLikeTorrent(buf));
    }

    [Fact]
    public void Torrent_False_UppercaseD()
    {
        // 'D' is 0x44, not 0x64 -> false.
        byte[] data = Pad("D8:announce", 60);
        Assert.False(Access.LooksLikeTorrent(data));
    }

    [Fact]
    public void Torrent_False_Empty()
    {
        Assert.False(Access.LooksLikeTorrent(Array.Empty<byte>()));
    }
}
