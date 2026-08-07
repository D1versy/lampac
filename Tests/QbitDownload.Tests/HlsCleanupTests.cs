using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Фаза 1 ускорения отдачи HLS + фоновые контуры:
/// троттлинг CleanupHls (обход /qdl-hls через 9p стоит секунды и не должен висеть в горячем пути),
/// ключ дедупа START-уведомлений и фильтр OTA-списка хостов (/d1vision/hosts.json).
/// </summary>
public class HlsCleanupTests
{
    // hlsPath в несуществующую папку: CleanupHls выходит сразу, тест меряет ТОЛЬКО решение
    // «звать/не звать», а не саму чистку.
    static void NoopHlsRoot()
    {
        TestEnv.EnsureConf();
        ModInit.conf.hlsPath = Path.Combine(Path.GetTempPath(), "qdl-tests", "hls-" + Guid.NewGuid().ToString("N"));
    }

    // ── троттлинг чистки ──────────────────────────────────────────────────

    [Fact]
    public void First_call_takes_the_slot()
    {
        NoopHlsRoot();
        Access.HlsCleanupAt = 0;

        Access.CleanupHlsThrottled(60);

        Assert.NotEqual(0, Access.HlsCleanupAt);   // «ни разу не чистили» → идём всегда
    }

    [Fact]
    public void Second_call_inside_interval_is_skipped()
    {
        NoopHlsRoot();
        Access.HlsCleanupAt = 0;

        Access.CleanupHlsThrottled(60);
        long first = Access.HlsCleanupAt;

        for (int i = 0; i < 5; i++) Access.CleanupHlsThrottled(60);

        Assert.Equal(first, Access.HlsCleanupAt);   // метка не сдвинулась → обход каталога не делался
    }

    [Fact]
    public void Call_after_interval_runs_again()
    {
        NoopHlsRoot();
        Access.HlsCleanupAt = DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond * 120;
        long stale = Access.HlsCleanupAt;

        Access.CleanupHlsThrottled(60);

        Assert.True(Access.HlsCleanupAt > stale);
    }

    [Fact]
    public void Longer_interval_of_background_timer_keeps_the_slot_closed()
    {
        NoopHlsRoot();
        // 120 с назад: seek-ветка (60 с) прошла бы, фоновый таймер (300 с) — нет
        Access.HlsCleanupAt = DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond * 120;
        long stale = Access.HlsCleanupAt;

        Access.CleanupHlsThrottled(300);
        Assert.Equal(stale, Access.HlsCleanupAt);

        Access.CleanupHlsThrottled(60);
        Assert.True(Access.HlsCleanupAt > stale);
    }

    [Fact]
    public void Missing_root_does_not_throw()
    {
        NoopHlsRoot();
        Access.HlsCleanupAt = 0;
        Access.CleanupHlsThrottled(0);   // 0 — без троттлинга, чистка реально заходит внутрь
    }

    // ── ключ дедупа START-уведомлений ─────────────────────────────────────

    const string H1 = "AABBCCDDEEFF00112233445566778899AABBCCDD";
    const string H2 = "1122334455667788990011223344556677889900";

    [Fact]
    public void StartKey_without_episode_is_all()
    {
        Assert.Equal("start:" + H1.ToLowerInvariant() + ":all", Access.StartKey(H1, null));
        Assert.Equal("start:" + H1.ToLowerInvariant() + ":all", Access.StartKey(H1, ""));
        Assert.Equal("start:" + H1.ToLowerInvariant() + ":all", Access.StartKey(H1, "   "));
    }

    [Fact]
    public void StartKey_is_case_insensitive_by_hash_and_carries_episode()
    {
        Assert.Equal(Access.StartKey(H1, null), Access.StartKey(H1.ToLowerInvariant(), null));
        Assert.Equal("start:" + H1.ToLowerInvariant() + ":s1e7", Access.StartKey(H1, "s1e7"));
        Assert.NotEqual(Access.StartKey(H1, null), Access.StartKey(H2, null));
        Assert.NotEqual(Access.StartKey(H1, null), Access.StartKey(H1, "s1e7"));
    }

    [Fact]
    public void StartKey_never_collides_with_episode_keys()
    {
        // seen делится с EpKey-ключами (s1e7 / ova1 / r1-8) — префикс «start:» их не пересекает
        Assert.StartsWith("start:", Access.StartKey(H1, null));
        Assert.DoesNotContain(Access.StartKey(H1, null), new[] { "s1e7", "ova1", "r1-8", "e7" });
    }

    // ── AddStartNotification: baseline-гейт и дедуп ───────────────────────

    static void FreshDb()
    {
        TestEnv.FreshCache();
        using var db = new QbitDownload.SqlContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Start_notification_is_silent_until_baseline_exists()
    {
        FreshDb();

        Assert.False(Access.AddStartNotification(777, "https://tracker/t1", H1, "Сериал", null));

        using var db = new QbitDownload.SqlContext();
        Assert.Empty(db.noti);
        Assert.Empty(db.seen);   // и следов в базе отсечения не оставили
    }

    [Fact]
    public void Start_notification_created_once_per_hash()
    {
        FreshDb();
        string sk = Access.SeriesKey(777, "https://tracker/t1");
        using (var db = new QbitDownload.SqlContext())
        {
            db.seen.Add(new SeenModel { seriesKey = sk, epkey = "s1e1" });   // база отсечения построена
            db.SaveChanges();
        }

        Assert.True(Access.AddStartNotification(777, "https://tracker/t1", H1, "Сериал", null));
        Assert.False(Access.AddStartNotification(777, "https://tracker/t1", H1, "Сериал", null));   // повторный проход
        Assert.False(Access.AddStartNotification(777, "https://tracker/t1", H1, "Сериал", null));

        using (var db = new QbitDownload.SqlContext())
        {
            var n = Assert.Single(db.noti);
            Assert.Equal("START", n.kind);
            Assert.Equal(Access.StartKey(H1, null), n.epkey);
            Assert.Equal(H1, n.hash);
            Assert.Equal(sk, n.seriesKey);
            Assert.Equal(-1, n.season);
            Assert.Equal(-1, n.episode);
            Assert.False(n.read);
            Assert.False(string.IsNullOrWhiteSpace(n.label));
        }
    }

    [Fact]
    public void New_hash_gives_a_new_start_notification()
    {
        FreshDb();
        string sk = Access.SeriesKey(777, "https://tracker/t1");
        using (var db = new QbitDownload.SqlContext())
        {
            db.seen.Add(new SeenModel { seriesKey = sk, epkey = "s1e1" });
            db.SaveChanges();
        }

        Assert.True(Access.AddStartNotification(777, "https://tracker/t1", H1, "Сериал", null));
        Assert.True(Access.AddStartNotification(777, "https://tracker/t1", H2, "Сериал", null));   // re-grab на новый infohash

        using var db2 = new QbitDownload.SqlContext();
        Assert.Equal(2, db2.noti.Count());
    }

    [Fact]
    public void Start_notification_rejects_garbage_hash()
    {
        FreshDb();
        Assert.False(Access.AddStartNotification(777, "https://tracker/t1", "zzz", "Сериал", null));
        Assert.False(Access.AddStartNotification(777, "https://tracker/t1", null, "Сериал", null));
    }

    [Fact]
    public void Push_signal_without_nws_is_a_noop()
    {
        Shared.Startup.Nws = null;   // Sync выключен — модуль обязан работать
        Access.PushNotiSignal(3);
    }

    // ── фильтр OTA-списка хостов ─────────────────────────────────────────

    [Theory]
    [InlineData("https://tv.d1versy.com:9443")]
    [InlineData("https://tv2.d1versy.com:9443")]
    [InlineData("https://d1versy.com")]
    [InlineData("http://192.168.87.24:9118")]
    [InlineData("http://10.0.0.5:9118")]
    [InlineData("http://172.16.0.1:9118")]
    [InlineData("http://172.31.255.255:9118")]
    [InlineData("http://127.0.0.1:9118")]
    [InlineData("http://localhost:9118")]
    public void Our_hosts_pass(string url) => Assert.True(Access.IsOurClientHost(url), url);

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("https://d1versy.com.evil.com")]   // суффикс-подделка
    [InlineData("https://xd1versy.com")]           // нужен именно поддомен, а не «оканчивается на»
    [InlineData("http://8.8.8.8")]
    [InlineData("http://172.15.0.1:9118")]         // ниже приватного диапазона
    [InlineData("http://172.32.0.1:9118")]         // выше приватного диапазона
    [InlineData("http://11.0.0.1:9118")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void Foreign_hosts_are_dropped(string url) => Assert.False(Access.IsOurClientHost(url), url ?? "<null>");
}
