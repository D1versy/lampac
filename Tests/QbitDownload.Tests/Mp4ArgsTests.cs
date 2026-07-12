using System.Collections.Generic;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Аргументы MP4-транскода (Mp4Args — чистая сборка): CPU-ветка обязана быть
/// байт-в-байт как до внедрения GPU-воркера (это фолбэк), nvenc-ветка — h264_nvenc
/// с NVDEC-декодом и совместимым с браузером выходом (yuv420p, high 4.1, faststart).
/// </summary>
public class Mp4ArgsTests
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

    [Fact]
    public void Cpu_args_are_byte_identical_to_legacy()
    {
        var a = Access.Mp4Args("/downloads/movie.mkv", "/downloads/transcoded/movie.mp4.part");
        var expected = new List<string>
        {
            "-y", "-i", "/downloads/movie.mkv",
            "-map", "0:v:0", "-map", "0:a?",
            "-dn", "-sn", "-map_chapters", "-1",
            "-c:v", "libx264", "-preset", "fast", "-crf", "19",
            "-pix_fmt", "yuv420p", "-profile:v", "high", "-level", "4.1",
            "-c:a", "aac", "-ac", "2", "-b:a", "256k",
            "-movflags", "+faststart",
            "-f", "mp4",
            "-progress", "pipe:1", "-nostats",
            "/downloads/transcoded/movie.mp4.part"
        };
        Assert.Equal(expected, a);
    }

    [Fact]
    public void Nvenc_args_use_gpu_pipeline()
    {
        var a = Access.Mp4Args("/downloads/movie.mkv", "/downloads/transcoded/movie.mp4.part", nvenc: true);
        Assert.True(Sub(a, "-hwaccel", "cuda") >= 0, "NVDEC-декод (HEVC 10-бит и т.п.)");
        Assert.True(Sub(a, "-hwaccel", "cuda") < a.IndexOf("-i"), "-hwaccel — входная опция, до -i");
        Assert.True(Sub(a, "-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq") >= 0);
        Assert.True(Sub(a, "-rc", "vbr", "-cq", "19", "-b:v", "0") >= 0);
        Assert.DoesNotContain("libx264", a);
        // выход обязан остаться браузеро-совместимым и стримабельным
        Assert.True(Sub(a, "-pix_fmt", "yuv420p", "-profile:v", "high", "-level", "4.1") >= 0);
        Assert.True(Sub(a, "-c:a", "aac", "-ac", "2", "-b:a", "256k") >= 0);
        Assert.True(Sub(a, "-movflags", "+faststart") >= 0);
        Assert.True(Sub(a, "-progress", "pipe:1", "-nostats") >= 0, "прогресс парсит воркер");
        Assert.Equal("/downloads/transcoded/movie.mp4.part", a[a.Count - 1]);
    }

    [Fact]
    public void Both_variants_share_mapping_and_output()
    {
        foreach (bool nv in new[] { false, true })
        {
            var a = Access.Mp4Args("/downloads/m.mkv", "/downloads/transcoded/m.mp4.part", nv);
            Assert.True(Sub(a, "-map", "0:v:0", "-map", "0:a?") >= 0, "все аудиодорожки, одно видео");
            Assert.True(Sub(a, "-dn", "-sn", "-map_chapters", "-1") >= 0, "data/субтитры не тащим");
            Assert.Equal("-y", a[0]);
        }
    }
}
