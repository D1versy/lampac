using System;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>Proves the reflection harness + source linking + net10 toolchain all work end-to-end.</summary>
public class SmokeTests
{
    [Fact]
    public void HumanSize_formats_kb()
        => Assert.Equal("1 KB", Access.HumanSize(1024));

    [Fact]
    public void MagnetHash_extracts_lowercase_btih()
        => Assert.Equal("0123456789abcdef0123456789abcdef01234567",
                        Access.MagnetHash("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=x"));

    [Fact]
    public void ParseEp_reads_season_episode()
    {
        var e = Access.ParseEp("Show.S02E07.1080p.WEB-DL");
        Assert.True(e.any);
        Assert.Equal(2, e.season);
        Assert.Equal(7, e.ep);
        Assert.Equal("s2e7", Access.EpKey(e));
    }

    [Fact]
    public void ValidHash_accepts_40hex_rejects_junk()
    {
        Assert.True(Access.ValidHash("0123456789abcdef0123456789abcdef01234567"));
        Assert.False(Access.ValidHash("nope"));
    }

    [Fact]
    public void SeriesKey_prefers_tmdb_then_link_hash()
    {
        Assert.Equal("t123", Access.SeriesKey(123, "ignored"));
        Assert.StartsWith("l", Access.SeriesKey(0, "http://x/y"));
    }

    [Fact]
    public void IsLoopbackSelf_honours_configured_listen()
    {
        TestEnv.SetListen(9118, "127.0.0.1");
        Assert.True(Access.IsLoopbackSelf(new Uri("http://127.0.0.1:9118/x")));
        Assert.False(Access.IsLoopbackSelf(new Uri("http://8.8.8.8:9118/x")));
    }
}
