using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Решение «copy vs live-x264» для видеодорожки HLS (HlsCopyVideo):
/// браузеро-совместимые (h264/vp9/av1) и hevc (путь §Y) — copy,
/// старые SD-кодеки (XviD/MPEG-2/VC-1 и т.п.) — живой транскод в H.264.
/// </summary>
public class HlsCodecTests
{
    [Theory]
    [InlineData("h264")]
    [InlineData("H264")]
    [InlineData("vp9")]
    [InlineData("av1")]
    [InlineData("hevc")]   // сознательно copy: для HEVC свой путь — оффлайн-транскод в MP4 (§Y)
    public void Browser_safe_and_hevc_are_copied(string codec) => Assert.True(Access.HlsCopyVideo(codec));

    [Theory]
    [InlineData("mpeg4")]       // XviD/DivX в .avi — «звук есть, экран чёрный»
    [InlineData("msmpeg4v3")]
    [InlineData("mpeg2video")]
    [InlineData("mpeg1video")]
    [InlineData("vc1")]
    [InlineData("wmv3")]
    [InlineData("theora")]
    public void Legacy_codecs_get_live_transcode(string codec) => Assert.False(Access.HlsCopyVideo(codec));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Probe_failure_falls_back_to_copy(string codec) => Assert.True(Access.HlsCopyVideo(codec));

    // ── аргументы ffmpeg (HlsArgs — чистая сборка, регрессия воспроизведения) ──

    static int Sub(System.Collections.Generic.List<string> args, params string[] seq)
    {
        // индекс первого вхождения подпоследовательности seq в args, -1 если нет
        for (int i = 0; i + seq.Length <= args.Count; i++)
        {
            bool ok = true;
            for (int j = 0; j < seq.Length; j++) if (args[i + j] != seq[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    [Fact]
    public void Args_copy_mode_keeps_video_untouched()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: true);
        Assert.True(Sub(a, "-c:v", "copy") >= 0);
        Assert.DoesNotContain("libx264", a);
        Assert.True(Sub(a, "-map", "0:v:0?", "-map", "0:a:0?") >= 0, "дефолтный маппинг без внешней озвучки");
    }

    [Fact]
    public void Args_transcode_mode_uses_live_x264()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/old.avi", null, null, copyVideo: false);
        Assert.True(Sub(a, "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p") >= 0);
        Assert.True(Sub(a, "-c:v", "copy") < 0);
    }

    [Fact]
    public void Args_external_audio_adds_second_input_and_custom_map()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", "/downloads/dub.mka", "1:a:0?", copyVideo: true);
        Assert.True(Sub(a, "-i", "/downloads/x.mkv", "-i", "/downloads/dub.mka") >= 0, "внешняя озвучка — вторым входом");
        Assert.True(Sub(a, "-map", "0:v:0?", "-map", "1:a:0?") >= 0, "звук берётся из второго входа");
    }

    [Fact]
    public void Args_audio_and_hls_muxing_are_stable()
    {
        // Общая часть конвейера — звук всегда AAC stereo, event-плейлист, независимые сегменты.
        // Регрессия: любые изменения тут ломают воспроизведение у ВСЕХ клиентов.
        foreach (bool copy in new[] { true, false })
        {
            var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copy);
            Assert.True(Sub(a, "-c:a", "aac", "-ac", "2", "-b:a", "256k") >= 0);
            Assert.True(Sub(a, "-f", "hls", "-hls_time", "6", "-hls_playlist_type", "event") >= 0);
            Assert.True(Sub(a, "-hls_flags", "independent_segments") >= 0);
            Assert.Contains(System.IO.Path.Combine("/hls/k", "seg%05d.ts"), a);
            Assert.Equal(System.IO.Path.Combine("/hls/k", "playlist.m3u8"), a[a.Count - 1]);
            Assert.Equal("-y", a[0]);   // перезапись без вопросов — ffmpeg не должен зависнуть на prompt
        }
    }
}
