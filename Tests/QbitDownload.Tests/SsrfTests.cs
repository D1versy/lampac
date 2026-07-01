using System;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for the SSRF/anti-loopback helpers:
/// <see cref="Access.MagnetHash"/>, <see cref="Access.IsPrivateHost"/> and <see cref="Access.IsLoopbackSelf"/>.
/// </summary>
public class SsrfTests
{
    // ── MagnetHash ────────────────────────────────────────────────────────
    // Regex: btih:([0-9a-fA-F]{40}|[0-9a-zA-Z]{32}) IgnoreCase, result lowercased; "" when no match.

    [Theory]
    // 40-hex btih, mixed case -> lowercased
    [InlineData("magnet:?xt=urn:btih:0123456789ABCDEF0123456789abcdef01234567&dn=x",
                "0123456789abcdef0123456789abcdef01234567")]
    // all uppercase hex
    [InlineData("magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                "abcdef0123456789abcdef0123456789abcdef01")]
    // 32-char base32 alnum -> lowercased
    [InlineData("magnet:?xt=urn:btih:ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
                "abcdefghijklmnopqrstuvwxyz234567")]
    // uppercase "BTIH:" is matched due to IgnoreCase
    [InlineData("magnet:?xt=urn:BTIH:0123456789ABCDEF0123456789ABCDEF01234567",
                "0123456789abcdef0123456789abcdef01234567")]
    // btih appears mid-string; still extracted
    [InlineData("prefix btih:0123456789abcdef0123456789abcdef01234567 suffix",
                "0123456789abcdef0123456789abcdef01234567")]
    public void MagnetHash_Extracts(string magnet, string expected)
        => Assert.Equal(expected, Access.MagnetHash(magnet));

    [Theory]
    [InlineData("")]                                   // empty string
    [InlineData("magnet:?dn=nohash")]                  // no btih at all
    [InlineData("btih:012345")]                        // too short (6 hex), <32 for base32 too
    [InlineData("btih:0123-4567-89ab")]                 // hyphens break the 32-run, too short otherwise
    [InlineData("xt=urn:sha1:0123456789abcdef0123456789abcdef01234567")] // "sha1:" not "btih:"
    public void MagnetHash_NoMatch_ReturnsEmpty(string magnet)
        => Assert.Equal("", Access.MagnetHash(magnet));

    [Fact]
    public void MagnetHash_Null_ReturnsEmpty()
        => Assert.Equal("", Access.MagnetHash(null));

    [Fact]
    public void MagnetHash_41Hex_TakesFirst40()
    {
        // 41 hex chars: the 40-hex alternative matches greedily the first 40, remainder ignored.
        string magnet = "btih:0123456789abcdef0123456789abcdef012345678";
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", Access.MagnetHash(magnet));
    }

    [Fact]
    public void MagnetHash_33Base32_TakesFirst32()
    {
        // 33 alnum chars (no hex-40 possible because letters beyond f): first 32 taken by base32 alt.
        string magnet = "btih:GHIJKLMNOPQRSTUVWXYZ234567GHIJKLM";
        Assert.Equal("ghijklmnopqrstuvwxyz234567ghijklm".Substring(0, 32), Access.MagnetHash(magnet));
    }

    // ── IsPrivateHost ─────────────────────────────────────────────────────

    [Theory]
    // literal hostnames
    [InlineData("http://localhost/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://0.0.0.0/x")]
    // 10.0.0.0/8
    [InlineData("http://10.0.0.1/x")]
    [InlineData("http://10.255.255.255/x")]
    // 172.16.0.0 - 172.31.255.255
    [InlineData("http://172.16.0.1/x")]
    [InlineData("http://172.31.255.254/x")]
    [InlineData("http://172.20.10.5/x")]
    // 192.168.0.0/16
    [InlineData("http://192.168.1.1/x")]
    // 169.254.0.0/16 link-local
    [InlineData("http://169.254.1.1/x")]
    // 127.0.0.0/8
    [InlineData("http://127.5.6.7/x")]
    // IPv6 loopback / link-local
    [InlineData("http://[fe80::1]/x")]
    public void IsPrivateHost_True(string url)
        => Assert.True(Access.IsPrivateHost(new Uri(url)));

    [Theory]
    // public IPv4
    [InlineData("http://8.8.8.8/x")]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg")]
    [InlineData("http://1.1.1.1/x")]
    // boundary: 172.15 is below the private block, 172.32 is above it
    [InlineData("http://172.15.255.255/x")]
    [InlineData("http://172.32.0.0/x")]
    // 192.169 not private, 169.253 not private
    [InlineData("http://192.169.1.1/x")]
    [InlineData("http://169.253.1.1/x")]
    // public IPv6 (documentation range) is not loopback/link-local
    [InlineData("http://[2001:4860:4860::8888]/x")]
    public void IsPrivateHost_False(string url)
        => Assert.False(Access.IsPrivateHost(new Uri(url)));

    // ── IsLoopbackSelf ────────────────────────────────────────────────────
    // Requires the configured listen port; host must be 127.0.0.1/localhost/::1 or the configured localhost.

    [Theory]
    [InlineData("http://127.0.0.1:9118/x")]
    [InlineData("https://127.0.0.1:9118/x")]
    [InlineData("http://localhost:9118/x")]
    public void IsLoopbackSelf_True_ForConfiguredPortAndKnownHost(string url)
    {
        TestEnv.SetListen(9118, "127.0.0.1");
        Assert.True(Access.IsLoopbackSelf(new Uri(url)));
    }

    [Fact]
    public void IsLoopbackSelf_IPv6Loopback_NotRecognized()
    {
        // BUG?: the code compares host == "::1", but Uri.Host for an IPv6 literal returns "[::1]"
        // (brackets included), so a real "http://[::1]:port/" is NOT treated as loopback-self.
        // Same quirk affects IsSelfResolver/IsPrivateHost. Low impact — our loopback links use
        // 127.0.0.1/localhost, never [::1] — but the "::1" allow-branch is effectively dead code.
        TestEnv.SetListen(9118, "127.0.0.1");
        Assert.False(Access.IsLoopbackSelf(new Uri("http://[::1]:9118/x")));
    }

    [Theory]
    // wrong port
    [InlineData("http://127.0.0.1:9119/x")]
    [InlineData("http://localhost:80/x")]
    // wrong host (public)
    [InlineData("http://8.8.8.8:9118/x")]
    [InlineData("http://example.com:9118/x")]
    // non-http scheme on the right port
    [InlineData("ftp://127.0.0.1:9118/x")]
    public void IsLoopbackSelf_False(string url)
    {
        TestEnv.SetListen(9118, "127.0.0.1");
        Assert.False(Access.IsLoopbackSelf(new Uri(url)));
    }

    [Fact]
    public void IsLoopbackSelf_CustomLocalhost_Matches()
    {
        TestEnv.SetListen(5000, "listen.localhost");
        Assert.True(Access.IsLoopbackSelf(new Uri("http://listen.localhost:5000/x")));
        // built-in hosts still honored at the new port
        Assert.True(Access.IsLoopbackSelf(new Uri("http://127.0.0.1:5000/x")));
        // the previously-valid port is now wrong
        Assert.False(Access.IsLoopbackSelf(new Uri("http://127.0.0.1:9118/x")));
    }

    [Fact]
    public void IsLoopbackSelf_CustomLocalhost_IsCaseInsensitive()
    {
        TestEnv.SetListen(5000, "My.Custom.Host");
        Assert.True(Access.IsLoopbackSelf(new Uri("http://my.custom.host:5000/x")));
    }
}
