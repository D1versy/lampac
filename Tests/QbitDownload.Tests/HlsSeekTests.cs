using System.Collections.Generic;
using System.IO;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Быстрая перемотка HLS (hlsSeek): виртуальный VOD-плейлист (BuildVodPlaylist),
/// seek-запуски ffmpeg с -ss/-start_number/-copyts (HlsArgs со startSeg >= 0)
/// и готовность сегментов из «дырявого» кэша (SegReady).
/// </summary>
public class HlsSeekTests
{
    static int Sub(List<string> args, params string[] seq)
    {
        for (int i = 0; i + seq.Length <= args.Count; i++)
        {
            bool ok = true;
            for (int j = 0; j < seq.Length; j++) if (args[i + j] != seq[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    // ── HlsArgs: seek-режим ───────────────────────────────────────────────

    [Fact]
    public void Seek_puts_ss_before_input_and_numbers_segments()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: true, startSeg: 100);
        Assert.True(Sub(a, "-ss", "600", "-i", "/downloads/x.mkv") >= 0, "-ss (input seeking) перед -i");
        Assert.True(Sub(a, "-start_number", "100") >= 0);
        Assert.True(Sub(a, "-copyts", "-muxdelay", "0", "-avoid_negative_ts", "disabled") >= 0, "истинные PTS — сегменты разных запусков совместимы");
        Assert.Equal(Path.Combine("/hls/k", "ff100.m3u8"), a[a.Count - 1]);   // прогресс запуска, клиенту не отдаётся
        Assert.DoesNotContain(Path.Combine("/hls/k", "playlist.m3u8"), a);
    }

    [Fact]
    public void Seek_with_external_audio_seeks_both_inputs()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", "/downloads/dub.mka", "1:a:0", copyVideo: true, startSeg: 50);
        Assert.True(Sub(a, "-ss", "300", "-i", "/downloads/x.mkv") >= 0);
        Assert.True(Sub(a, "-ss", "300", "-i", "/downloads/dub.mka") >= 0, "-ss нужен перед КАЖДЫМ входом");
    }

    [Fact]
    public void Seek_from_zero_has_no_ss_but_keeps_vod_grid()
    {
        // startSeg=0 — рестарт с начала (например, сегмент вычищен CleanupHls): -ss не нужен,
        // но copyts/нумерация обязательны, чтобы сегменты остались совместимы с остальными запусками
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: true, startSeg: 0);
        Assert.DoesNotContain("-ss", a);
        Assert.True(Sub(a, "-start_number", "0") >= 0);
        Assert.True(Sub(a, "-copyts", "-muxdelay", "0", "-avoid_negative_ts", "disabled") >= 0);
        Assert.Equal(Path.Combine("/hls/k", "ff0.m3u8"), a[a.Count - 1]);
    }

    [Fact]
    public void Legacy_mode_args_stay_untouched()
    {
        // startSeg=-1 (или дефолт) — старое линейное поведение байт-в-байт: без -ss/-copyts/-start_number
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: true);
        Assert.DoesNotContain("-ss", a);
        Assert.DoesNotContain("-copyts", a);
        Assert.DoesNotContain("-start_number", a);
        Assert.DoesNotContain("-force_key_frames", a);
        Assert.Equal(Path.Combine("/hls/k", "playlist.m3u8"), a[a.Count - 1]);
    }

    [Fact]
    public void Reencode_seek_forces_keyframe_grid_with_offset()
    {
        // t под copyts — абсолютное медиа-время → сетке нужен offset точки старта, иначе каждый кадр станет ключевым
        var a = Access.HlsArgs("/hls/k", "/downloads/old.avi", null, null, copyVideo: false, startSeg: 100);
        Assert.True(Sub(a, "-force_key_frames", "expr:gte(t,600+n_forced*6)") >= 0);

        // copy-режим и легаси-реэнкод — без принудительных keyframe
        Assert.DoesNotContain("-force_key_frames", Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: true, startSeg: 100));
        Assert.DoesNotContain("-force_key_frames", Access.HlsArgs("/hls/k", "/downloads/old.avi", null, null, copyVideo: false));
    }

    // ── BuildVodPlaylist ──────────────────────────────────────────────────

    [Fact]
    public void Vod_playlist_covers_full_duration()
    {
        string m = Access.BuildVodPlaylist(100.0);   // 16 полных по 6с + хвост 4с
        Assert.StartsWith("#EXTM3U", m);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", m);
        Assert.Contains("#EXT-X-INDEPENDENT-SEGMENTS", m);
        Assert.EndsWith("#EXT-X-ENDLIST\n", m);
        Assert.Contains("seg00000.ts", m);
        Assert.Contains("seg00016.ts", m);
        Assert.DoesNotContain("seg00017.ts", m);
        Assert.Contains("#EXTINF:4.000000,", m);          // хвост
        Assert.DoesNotContain("#EXT-X-START", m);          // VOD стартует с нуля сам, хак не нужен
        Assert.Equal(17, System.Text.RegularExpressions.Regex.Matches(m, "#EXTINF").Count);
    }

    [Fact]
    public void Vod_playlist_exact_multiple_has_no_zero_tail()
    {
        string m = Access.BuildVodPlaylist(60.0);   // ровно 10 сегментов
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(m, "#EXTINF").Count);
        Assert.DoesNotContain("#EXTINF:0.000000", m);
    }

    // ── SegReady: «дырявый» кэш из нескольких запусков ────────────────────

    [Fact]
    public void Segment_ready_rules()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qdl-segready-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(Access.SegReady(dir, 5));                                   // файла нет

            File.WriteAllText(Path.Combine(dir, "seg00005.ts"), "x");
            Assert.False(Access.SegReady(dir, 5), "одиночный сегмент без плейлиста — возможно, обрубок убитого ffmpeg");

            File.WriteAllText(Path.Combine(dir, "seg00006.ts"), "x");
            Assert.True(Access.SegReady(dir, 5), "есть следующий по номеру → муксер финализировал");

            Assert.False(Access.SegReady(dir, 6), "теперь seg6 — последний и не вписан в плейлист");
            File.WriteAllText(Path.Combine(dir, "ff5.m3u8"), "#EXTM3U\n#EXTINF:6.0,\nseg00005.ts\n#EXTINF:6.0,\nseg00006.ts\n");
            Assert.True(Access.SegReady(dir, 6), "вписан в плейлист запуска → готов");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
