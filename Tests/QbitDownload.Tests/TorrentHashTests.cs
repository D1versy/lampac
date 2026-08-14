using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Infohash из тела .torrent (TorrentHash.cs).
///
/// Зачем это вообще есть: login-трекеры (Кинозал, RuTracker при priority="torrent") отдают раздачу
/// файлом, а не магнитом. Без хеша /qdl/add не мог вернуть его клиенту → у загрузки не было ни
/// карточки, ни описания, ни links/ctx для слежения (разбор «Холод», 14.08.2026).
///
/// Эталон считаем независимо — SHA1 байтов info-словаря, как того требует BEP 3.
/// </summary>
public class TorrentHashTests
{
    static byte[] A(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Минимальный валидный однофайловый .torrent + его настоящий infohash.</summary>
    static (byte[] torrent, string hash) MakeTorrent(string name = "test.mkv", int length = 1234)
    {
        var pieces = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();   // ровно одна piece-хеш-запись

        // ключи словаря по алфавиту — как требует bencode
        var info = new List<byte>();
        info.AddRange(A($"d6:lengthi{length}e4:name{name.Length}:{name}12:piece lengthi16384e6:pieces20:"));
        info.AddRange(pieces);
        info.AddRange(A("e"));

        var t = new List<byte>();
        t.AddRange(A("d8:announce10:http://t/a4:info"));
        t.AddRange(info);
        t.AddRange(A("e"));

        string hash = Convert.ToHexString(SHA1.HashData(info.ToArray())).ToLowerInvariant();
        return (t.ToArray(), hash);
    }

    [Fact]
    public void Infohash_matches_sha1_of_info_dictionary()
    {
        var (torrent, expected) = MakeTorrent();

        string got = Access.TorrentInfoHash(torrent);

        Assert.Equal(expected, got);
        Assert.Equal(40, got.Length);
        Assert.Equal(got.ToLowerInvariant(), got);      // ключ qBit — строчными
        Assert.True(Access.ValidHash(got));             // и он же обязан проходить наш гейт хешей
    }

    [Fact]
    public void Different_content_gives_different_hash()
    {
        var a = MakeTorrent("a.mkv", 1234);
        var b = MakeTorrent("b.mkv", 1234);

        Assert.NotEqual(Access.TorrentInfoHash(a.torrent), Access.TorrentInfoHash(b.torrent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a torrent at all, just text")]
    [InlineData("d8:announce10:http://t/ae")]           // bencode-словарь без info
    public void Garbage_returns_null(string body)
    {
        byte[] data = body == null ? null : Encoding.ASCII.GetBytes(body.PadRight(60, ' '));

        Assert.Null(Access.TorrentInfoHash(data));
    }

    [Fact]
    public void Truncated_torrent_returns_null()
    {
        var (torrent, _) = MakeTorrent();

        Assert.Null(Access.TorrentInfoHash(torrent.Take(torrent.Length / 2).ToArray()));
    }

    [Fact]
    public void Too_short_input_returns_null()
    {
        Assert.Null(Access.TorrentInfoHash(new byte[] { 0x64, 0x65 }));   // "de"
    }
}
