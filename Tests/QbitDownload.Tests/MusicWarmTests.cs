using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;
using QbitDownload;
using Shared.Models.Events;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Прогрев полок раздела «Музыка» (MusicWarm.cs).
///
/// Покрываем то, чья поломка не видна глазом:
///   • ЧТО СЧИТАЕТСЯ ГЛАВНОЙ — /music.js и /music/section под прогрев попадать не должны;
///   • МАРКЕР СОБСТВЕННОГО РЕПЛЕЯ — без него наш же прогрев вечно обновляет lastSeen хоста,
///     и прунинг не срабатывает НИКОГДА (грабля 1-в-1 из CatalogWarmup);
///   • ВЫБОР ХОСТА — греть можно только наблюдённым хостом: выдуманный 127.0.0.1 запёкся бы
///     в общий кеш обложек (живой баг MusicImageProxyService);
///   • ПЕРСИСТ — файл состояния обязан читаться и без новых полей, и битым.
/// </summary>
public class MusicWarmTests
{
    static void Fresh()
    {
        TestEnv.FreshCache();
        MusicWarm.ResetForTests();
        ModInit.conf.musicWarmEnabled = true;
        ModInit.conf.musicWarmHostCap = 4;
        ModInit.conf.musicWarmPruneDays = 14;
    }

    static EventMiddleware Get(string path, string host = "192.168.87.24:9118", bool warmupHeader = false, string method = "GET")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString(host);
        if (warmupHeader) ctx.Request.Headers[CatalogWarmup.WarmupHeader] = "1";
        return new EventMiddleware(true, ctx);
    }

    // ── что считается главной ─────────────────────────────────────────────

    [Theory]
    [InlineData("/music")]
    [InlineData("/music/home")]
    [InlineData("/MUSIC/HOME")]
    public void Home_urls_are_recognized(string path) => Assert.True(MusicWarm.IsHomeUrl(path));

    [Theory]
    [InlineData("/music.js")]
    [InlineData("/music/section")]
    [InlineData("/music/search")]
    [InlineData("/music/js/token")]
    [InlineData("/musical")]
    [InlineData("/qdl/list")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_urls_are_not_home(string path) => Assert.False(MusicWarm.IsHomeUrl(path));

    // ── наблюдатель ───────────────────────────────────────────────────────

    [Fact]
    public void Observer_remembers_client_host()
    {
        Fresh();
        MusicWarm.OnRequest(first: true, Get("/music/home"));
        Assert.Equal(new[] { "http|192.168.87.24:9118" }, MusicWarm.HostsForTests());
    }

    [Fact]
    public void Observer_ignores_own_replay()
    {
        Fresh();
        MusicWarm.OnRequest(first: true, Get("/music/home", warmupHeader: true));
        Assert.Empty(MusicWarm.HostsForTests());
    }

    [Fact]
    public void Observer_ignores_post_and_foreign_paths()
    {
        Fresh();
        MusicWarm.OnRequest(first: true, Get("/music/home", method: "POST"));
        MusicWarm.OnRequest(first: true, Get("/music/section"));
        MusicWarm.OnRequest(first: true, Get("/music.js"));
        Assert.Empty(MusicWarm.HostsForTests());
    }

    [Fact]
    public void Observer_is_off_when_disabled()
    {
        Fresh();
        ModInit.conf.musicWarmEnabled = false;
        MusicWarm.OnRequest(first: true, Get("/music/home"));
        Assert.Empty(MusicWarm.HostsForTests());
    }

    [Fact]
    public void Observer_runs_only_on_first_stage()
    {
        Fresh();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/music/home";
        ctx.Request.Host = new HostString("tv.d1versy.com");
        MusicWarm.OnRequest(first: false, new EventMiddleware(false, ctx));
        Assert.Empty(MusicWarm.HostsForTests());
    }

    [Theory]
    [InlineData("127.0.0.1:9118")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost:9118")]
    [InlineData("LOCALHOST")]
    [InlineData("[::1]:9118")]
    [InlineData("")]
    [InlineData(null)]
    public void Loopback_hosts_are_never_warmed(string host) => Assert.True(MusicWarm.IsLoopbackHost(host));

    [Theory]
    [InlineData("192.168.87.24:9118")]
    [InlineData("tv.d1versy.com:9443")]
    [InlineData("127001.example.com")]
    public void Real_client_hosts_are_warmable(string host) => Assert.False(MusicWarm.IsLoopbackHost(host));

    [Fact]
    public void Observer_ignores_own_diagnostics_from_loopback()
    {
        // curl из контейнера и headless-проверки гейта не должны подсовывать прогреву
        // хост 127.0.0.1: он запёкся бы в общий кеш обложек
        Fresh();
        MusicWarm.OnRequest(first: true, Get("/music/home", host: "127.0.0.1:9118"));
        Assert.Empty(MusicWarm.HostsForTests());
    }

    // ── выбор хоста и капы ────────────────────────────────────────────────

    [Fact]
    public void Pick_returns_nothing_when_nobody_opened_music()
    {
        Fresh();
        var (scheme, host) = MusicWarm.PickHost();
        Assert.Null(scheme);
        Assert.Null(host);
    }

    [Fact]
    public void Pick_returns_freshest_host()
    {
        Fresh();
        MusicWarm.NoteHost("http", "192.168.87.24:9118");
        MusicWarm.NoteHost("https", "tv.d1versy.com:9443");

        var (scheme, host) = MusicWarm.PickHost();
        Assert.Equal("https", scheme);
        Assert.Equal("tv.d1versy.com:9443", host);
    }

    [Fact]
    public void Host_cap_evicts_oldest()
    {
        Fresh();
        ModInit.conf.musicWarmHostCap = 2;
        MusicWarm.NoteHost("http", "a:1");
        MusicWarm.NoteHost("http", "b:2");
        MusicWarm.NoteHost("http", "c:3");

        var hosts = MusicWarm.HostsForTests();
        Assert.Equal(2, hosts.Count);
        Assert.DoesNotContain("http|a:1", hosts);
    }

    [Fact]
    public void Prune_forgets_stale_hosts_only()
    {
        Fresh();
        MusicWarm.NoteHost("http", "old:1");
        MusicWarm.NoteHost("http", "new:2");

        // «сейчас» отодвигаем в будущее — так же дешевле, чем подделывать lastSeen
        int gone = MusicWarm.PruneHosts(DateTime.UtcNow.AddDays(30), 14);
        Assert.Equal(2, gone);
        Assert.Empty(MusicWarm.HostsForTests());

        MusicWarm.NoteHost("http", "fresh:3");
        Assert.Equal(0, MusicWarm.PruneHosts(DateTime.UtcNow, 14));
        Assert.Single(MusicWarm.HostsForTests());
    }

    [Fact]
    public void Prune_is_off_when_days_not_positive()
    {
        Fresh();
        MusicWarm.NoteHost("http", "a:1");
        Assert.Equal(0, MusicWarm.PruneHosts(DateTime.UtcNow.AddDays(999), 0));
        Assert.Single(MusicWarm.HostsForTests());
    }

    // ── разбор ответа /music/home ─────────────────────────────────────────

    const string HomeBody = """
    {
      "status": "ok",
      "browse_sections_warming": true,
      "browse_sections": [
        { "id": "browse:top_albums", "type": "album", "albums": [{"id":"1"},{"id":"2"}], "artists": [], "tracks": [] },
        { "id": "browse:vk_top_tracks", "type": "track", "albums": [], "artists": [], "tracks": [] },
        { "type": "album", "albums": [{"id":"x"}] }
      ]
    }
    """;

    [Fact]
    public void ParseHome_reads_shelves_and_warming()
    {
        Fresh();
        var shelves = MusicWarm.ParseHomeForTests(Encoding.UTF8.GetBytes(HomeBody), out bool warming);

        Assert.True(warming);
        // полка без id пропускается: греть её всё равно нечем
        Assert.Equal(2, shelves.Count);
        Assert.Equal(("browse:top_albums", 2), shelves[0]);
        Assert.Equal(("browse:vk_top_tracks", 0), shelves[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("[1,2,3]")]
    public void ParseHome_survives_garbage(string body)
    {
        Fresh();
        var shelves = MusicWarm.ParseHomeForTests(Encoding.UTF8.GetBytes(body), out bool warming);
        Assert.Null(shelves);
        Assert.False(warming);
    }

    [Fact]
    public void ParseHome_survives_empty_body()
    {
        Fresh();
        Assert.Null(MusicWarm.ParseHomeForTests(null, out _));
        Assert.Null(MusicWarm.ParseHomeForTests(Array.Empty<byte>(), out _));
    }

    // ── персист ───────────────────────────────────────────────────────────

    static string StorePath() => Path.Combine(ModInit.conf.cachePath, "music-warm.json");

    [Fact]
    public void State_round_trips()
    {
        Fresh();
        MusicWarm.NoteHost("https", "tv.d1versy.com:9443");
        MusicWarm.SeedShelvesForTests(("browse:top_albums", 20), ("browse:vk_top_tracks", 0));
        MusicWarm.Save();

        MusicWarm.ResetForTests();
        MusicWarm.Load();

        Assert.Equal(new[] { "https|tv.d1versy.com:9443" }, MusicWarm.HostsForTests());
        Assert.Equal(2, MusicWarm.ShelvesForTests().Count);
        Assert.Equal(("browse:vk_top_tracks", 0), MusicWarm.ShelvesForTests()[1]);
    }

    [Fact]
    public void State_reads_file_without_new_fields()
    {
        Fresh();
        // аддитивность: файл прошлой версии (только хосты) обязан читаться без потерь
        File.WriteAllText(StorePath(), """{"ver":1,"hosts":[{"scheme":"http","host":"192.168.87.24:9118","lastSeen":"2026-08-30T10:00:00Z"}]}""");

        MusicWarm.Load();
        Assert.Equal(new[] { "http|192.168.87.24:9118" }, MusicWarm.HostsForTests());
        Assert.Empty(MusicWarm.ShelvesForTests());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{обрез")]
    public void State_survives_broken_file(string raw)
    {
        Fresh();
        File.WriteAllText(StorePath(), raw);

        MusicWarm.Load();   // обрезанный JSON после падения по питанию = «состояния нет»
        Assert.Empty(MusicWarm.HostsForTests());
    }

    [Fact]
    public void Save_writes_atomically_and_leaves_no_tmp()
    {
        Fresh();
        MusicWarm.NoteHost("http", "192.168.87.24:9118");
        MusicWarm.Save();

        Assert.True(File.Exists(StorePath()));
        Assert.False(File.Exists(StorePath() + ".tmp"));
    }
}
