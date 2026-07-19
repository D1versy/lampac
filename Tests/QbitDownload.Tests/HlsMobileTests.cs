using Xunit;
using QbitDownload;

namespace QbitDownload.Tests;

/// <summary>
/// «Мобильный» HLS-профиль (ключ _m): live-даунскейл до 720p + кап битрейта для телефона
/// на сотовой сети. Проверяем сборку аргументов ffmpeg (HlsArgs с HlsMobileOpts), HDR-детект
/// и подпись сегментных строк плейлиста ключом периметра (SignHlsPlaylist).
/// </summary>
public class HlsMobileTests
{
    static int Sub(System.Collections.Generic.List<string> args, params string[] seq)
    {
        for (int i = 0; i + seq.Length <= args.Count; i++)
        {
            bool ok = true;
            for (int j = 0; j < seq.Length; j++) if (args[i + j] != seq[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    static QbitController.HlsMobileOpts Opts(bool hdr = false) => new QbitController.HlsMobileOpts
    {
        height = 720, cq = 28, crf = 25, maxrateKbps = 2500, audioKbps = 128, hdr = hdr
    };

    // ── NVENC-ветка (GPU-воркер) ──

    [Fact]
    public void Mobile_nvenc_downscales_and_caps_bitrate()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: false, startSeg: 100, nvenc: true, mobile: Opts());
        Assert.True(Sub(a, "-hwaccel", "cuda") >= 0, "NVDEC-декод сохранён");
        Assert.True(Sub(a, "-vf", "scale=-2:min(720\\,ih)") >= 0, "даунскейл без апскейла SD");
        Assert.True(Sub(a, "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "28", "-b:v", "0", "-maxrate", "2500k", "-bufsize", "5000k", "-forced-idr", "1", "-profile:v", "high", "-pix_fmt", "yuv420p") >= 0);
        Assert.DoesNotContain("-level", a);   // §AH: level не форсим никогда
        // keyframe-сетка и copyts-блок seek-запуска обязаны сохраниться — иначе сегменты несовместимы с VOD-нарезкой.
        // t относительное время энкода → без offset точки старта (проверено на бинаре: с offset сегменты 10.4с)
        Assert.True(Sub(a, "-force_key_frames", "expr:gte(t,n_forced*6)") >= 0);
        Assert.True(Sub(a, "-copyts", "-muxdelay", "0", "-avoid_negative_ts", "disabled") >= 0);
        // звук пожат под сотовый канал
        Assert.True(Sub(a, "-c:a", "aac", "-ac", "2", "-b:a", "128k") >= 0);
    }

    [Fact]
    public void Mobile_hdr_uses_tonemap_chain()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/hdr.mkv", null, null, copyVideo: false, startSeg: 0, nvenc: true, mobile: Opts(hdr: true));
        // setparams-префикс форсит bt2020 primaries/matrix: у веб-рипов часто проставлен только
        // transfer, без него zscale падает (exit -22 → 503 навсегда). Проверено на бинаре.
        Assert.True(Sub(a, "-vf", "setparams=colorspace=bt2020nc:color_primaries=bt2020,zscale=w=-2:h=720:t=linear:npl=100,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:p=bt709:r=tv,format=yuv420p") >= 0,
            "HDR10/HLG → SDR bt709 с форсом bt2020 на входе");
        Assert.True(Sub(a, "-c:v", "h264_nvenc") >= 0);
    }

    // ── CPU-фолбэк (воркер мёртв) ──

    [Fact]
    public void Mobile_cpu_fallback_uses_x264_with_cap()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: false, startSeg: -1, nvenc: false, mobile: Opts());
        Assert.DoesNotContain("-hwaccel", a);
        Assert.DoesNotContain("h264_nvenc", a);
        Assert.True(Sub(a, "-vf", "scale=-2:min(720\\,ih)") >= 0);
        Assert.True(Sub(a, "-c:v", "libx264", "-preset", "veryfast", "-crf", "25", "-maxrate", "2500k", "-bufsize", "5000k", "-pix_fmt", "yuv420p") >= 0);
        Assert.True(Sub(a, "-c:a", "aac", "-ac", "2", "-b:a", "128k") >= 0);
    }

    // ── внешняя озвучка сохраняется (второй -i с собственным -ss) ──

    [Fact]
    public void Mobile_keeps_external_audio_second_input()
    {
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", "/downloads/dub.mka", "1:a:0", copyVideo: false, startSeg: 10, nvenc: true, mobile: Opts());
        Assert.True(Sub(a, "-ss", "60", "-i", "/downloads/x.mkv", "-ss", "60", "-i", "/downloads/dub.mka") >= 0, "-ss перед КАЖДЫМ входом");
        Assert.True(Sub(a, "-map", "0:v:0?", "-map", "1:a:0") >= 0);
    }

    // ── обычные профили не затронуты (регрессия) ──

    [Fact]
    public void Non_mobile_args_stay_byte_identical()
    {
        // mobile=null → аргументы байт-в-байт как раньше (без -vf/-maxrate, звук 256k)
        foreach (bool nv in new[] { true, false })
        {
            var a = Access.HlsArgs("/hls/k", "/downloads/old.avi", null, null, copyVideo: false, startSeg: 100, nvenc: nv);
            Assert.DoesNotContain("-vf", a);
            Assert.DoesNotContain("-maxrate", a);
            Assert.True(Sub(a, "-c:a", "aac", "-ac", "2", "-b:a", "256k") >= 0);
        }
    }

    [Fact]
    public void Mobile_with_copy_is_ignored_by_contract()
    {
        // StartHls при mobile всегда даёт copyVideo=false; но даже при ошибочном copy=true
        // видео просто копируется (mobile-ветка недостижима) — кап уходит только в звук
        var a = Access.HlsArgs("/hls/k", "/downloads/x.mkv", null, null, copyVideo: true, startSeg: -1, nvenc: false, mobile: Opts());
        Assert.True(Sub(a, "-c:v", "copy") >= 0);
        Assert.DoesNotContain("-vf", a);
    }

    // ── HDR-детект ──

    [Theory]
    [InlineData("smpte2084", true)]    // HDR10 (PQ)
    [InlineData("SMPTE2084", true)]
    [InlineData("arib-std-b67", true)] // HLG
    [InlineData("bt709", false)]
    [InlineData("bt470bg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    public void Hdr_transfer_detection(string transfer, bool expected) =>
        Assert.Equal(expected, Access.IsHdrTransfer(transfer));

    // ── подпись сегментов ключом периметра ──

    [Fact]
    public void Sign_appends_key_to_segment_lines_only()
    {
        string m3u8 = "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:6.000000,\nseg00000.ts\n#EXTINF:6.000000,\nseg00001.ts\n#EXT-X-ENDLIST\n";
        string signed = Access.SignHlsPlaylist(m3u8, "abc123");
        Assert.Contains("seg00000.ts?d1v=abc123\n", signed);
        Assert.Contains("seg00001.ts?d1v=abc123\n", signed);
        Assert.Contains("#EXTM3U\n", signed);                      // заголовки не тронуты
        Assert.DoesNotContain("#EXT-X-ENDLIST?d1v", signed);
        Assert.DoesNotContain("#EXTINF:6.000000,?d1v", signed);
    }

    [Fact]
    public void Sign_without_key_returns_playlist_unchanged()
    {
        string m3u8 = "#EXTM3U\nseg00000.ts\n";
        Assert.Same(m3u8, Access.SignHlsPlaylist(m3u8, null));
        Assert.Same(m3u8, Access.SignHlsPlaylist(m3u8, ""));
    }

    [Fact]
    public void Sign_url_escapes_key()
    {
        string signed = Access.SignHlsPlaylist("seg00000.ts\n", "a b&c");
        Assert.Contains("seg00000.ts?d1v=a%20b%26c", signed);
    }

    // ── HDR-кэш: пустая проба (недокачанный файл) НЕ отравляет вердикт навсегда ──

    [Fact]
    public void Hdr_probe_failure_is_not_cached()
    {
        // несуществующий путь → ffprobe падает → пустая проба → SDR на этот раз, но БЕЗ записи в кэш
        string path = "/no/such/file-" + System.Guid.NewGuid().ToString("N") + ".mkv";
        Assert.False(Access.ProbeHdrCached(path));
        Assert.False(Access.HlsHdrCacheHas(path), "пустая проба не должна кэшироваться — иначе HDR-фильм навсегда останется «SDR» после докачки");
    }

    // ── idle-kill: зритель ушёл (нет запросов сегментов) → ffmpeg глушится ──

    static int SetIdleTtl(int v)
    {
        if (QbitDownload.ModInit.conf == null) QbitDownload.ModInit.conf = new QbitDownload.ModuleConf();
        int prev = QbitDownload.ModInit.conf.hlsIdleKillSec;
        QbitDownload.ModInit.conf.hlsIdleKillSec = v;
        return prev;
    }

    [Fact]
    public void IdleKill_reaps_stale_vod_session()
    {
        int prev = SetIdleTtl(180);
        try
        {
            var sess = Access.HlsSessionSeed("idletest_stale", startSeg: 5, touchAgeSec: 600);
            Access.KillIdleHls();
            Assert.False(Access.HlsRunningHas("idletest_stale"), "простоявшая VOD-сессия должна быть убита");
            Assert.True(Access.HlsSessionKilled(sess), "killed=true — прибита нами, не фейл");
            Assert.False(Access.HlsFailedHas("idletest_stale"), "негатив-кэш не должен пополняться");
        }
        finally { SetIdleTtl(prev); Access.HlsRunningRemove("idletest_stale"); }
    }

    [Fact]
    public void IdleKill_keeps_active_and_legacy_sessions()
    {
        int prev = SetIdleTtl(180);
        try
        {
            Access.HlsSessionSeed("idletest_fresh", startSeg: 5, touchAgeSec: 10);    // зритель активен
            Access.HlsSessionSeed("idletest_legacy", startSeg: -1, touchAgeSec: 600); // легаси: рестарт только с нуля — не трогаем
            Access.KillIdleHls();
            Assert.True(Access.HlsRunningHas("idletest_fresh"));
            Assert.True(Access.HlsRunningHas("idletest_legacy"));
        }
        finally { SetIdleTtl(prev); Access.HlsRunningRemove("idletest_fresh"); Access.HlsRunningRemove("idletest_legacy"); }
    }

    [Fact]
    public void IdleKill_disabled_by_zero_ttl()
    {
        int prev = SetIdleTtl(0);
        try
        {
            Access.HlsSessionSeed("idletest_off", startSeg: 5, touchAgeSec: 600);
            Access.KillIdleHls();
            Assert.True(Access.HlsRunningHas("idletest_off"), "hlsIdleKillSec=0 → старое поведение, ничего не глушим");
        }
        finally { SetIdleTtl(prev); Access.HlsRunningRemove("idletest_off"); }
    }
}
