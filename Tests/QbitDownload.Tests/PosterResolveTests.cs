using System;
using System.IO;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// TmdbPosterPath — резолвер пути картинки TMDB для /qdl/save.
///
/// Регрессия, ради которой эти тесты и написаны (qdl 2.15 → 2.22): форс proxy_tmdb=true сменил то,
/// что возвращает Lampa.TMDB.image — вместо «https://image.tmdb.org/…» клиент стал слать в poster_url
/// НАШ прокси «http://192.168.87.24:9118/tmdb/img/…». Прежний гард /qdl/save («https И не приватный
/// хост») резал это МОЛЧА, постеры новых загрузок перестали скачиваться, а healPoster строил тот же
/// URL и вылечить не мог. Форма URL здесь берётся из Modules/Proxy/TmdbProxy/plugin.js (строка с
/// '{localhost}/tmdb/img/' + account(url)) — это источник правды, при её смене тесты должны падать.
/// </summary>
public class PosterResolveTests
{
    const string Hash = "7002d7c18bdc31f0dce27801ae618bdc79632820";

    static string SeedMeta(string hash, string json)
    {
        string dir = TestEnv.FreshCache();
        Directory.CreateDirectory(Path.Combine(dir, "meta"));
        File.WriteAllText(Path.Combine(dir, "meta", hash + ".json"), json);
        return dir;
    }

    // ── что реально шлёт клиент ───────────────────────────────────────────

    [Fact]
    public void LanProxyUrl_Resolved()   // ← регрессионный: ровно то, что молча резалось
    {
        TestEnv.FreshCache();
        Assert.Equal("t/p/w500/fnIJeT9GZvscKm5PbmAvhTYDsLh.jpg", Access.TmdbPosterPath(
            "http://192.168.87.24:9118/tmdb/img/t/p/w500/fnIJeT9GZvscKm5PbmAvhTYDsLh.jpg?account_email=&uid=abc123",
            Hash));
    }

    [Fact]
    public void ExternalEdgeUrl_Resolved()
    {
        TestEnv.FreshCache();
        Assert.Equal("t/p/w500/kFzoW13fpFjEF3DPBv8xV8AF1tL.jpg", Access.TmdbPosterPath(
            "https://tv.d1versy.com:9443/tmdb/img/t/p/w500/kFzoW13fpFjEF3DPBv8xV8AF1tL.jpg?uid=abc123&token=x",
            Hash));
    }

    [Fact]
    public void DirectTmdbUrl_Resolved()   // форма до 2.15 — обратная совместимость
    {
        TestEnv.FreshCache();
        Assert.Equal("t/p/w500/a.jpg",
            Access.TmdbPosterPath("https://image.tmdb.org/t/p/w500/a.jpg", Hash));
    }

    [Fact]
    public void HttpDirectTmdbUrl_Resolved()   // Lampa.Utils.protocol() на http-странице даёт http://
    {
        TestEnv.FreshCache();
        Assert.Equal("t/p/w300/a.jpg",
            Access.TmdbPosterPath("http://image.tmdb.org/t/p/w300/a.jpg", Hash));
    }

    // ── фолбэк на мету с диска: ветка healPoster (шлёт только hash+poster_url, без card) ──

    [Fact]
    public void NoUrl_FallsBackToMetaOnDisk()
    {
        SeedMeta(Hash, "{\"id\":335984,\"poster_path\":\"/fnIJeT9GZvscKm5PbmAvhTYDsLh.jpg\"}");
        Assert.Equal("t/p/w500/fnIJeT9GZvscKm5PbmAvhTYDsLh.jpg", Access.TmdbPosterPath(null, Hash));
    }

    [Fact]
    public void UnusableUrl_FallsBackToMetaOnDisk()
    {
        SeedMeta(Hash, "{\"poster_path\":\"/b.jpg\"}");
        Assert.Equal("t/p/w500/b.jpg", Access.TmdbPosterPath("не url вообще", Hash));
    }

    [Fact]
    public void NoUrl_NoMeta_Null()
    {
        TestEnv.FreshCache();
        Assert.Null(Access.TmdbPosterPath(null, Hash));
    }

    [Theory]
    [InlineData("{\"id\":1}")]                        // нет poster_path
    [InlineData("{\"poster_path\":null}")]
    [InlineData("{\"poster_path\":\"\"}")]
    [InlineData("{\"poster_path\":\"без слеша.jpg\"}")]
    [InlineData("{ битый json")]
    public void BadMeta_NullWithoutThrow(string json)
    {
        SeedMeta(Hash, json);
        Assert.Null(Access.TmdbPosterPath(null, Hash));
    }

    [Fact]
    public void InvalidHash_IgnoresMeta()
    {
        TestEnv.FreshCache();
        Assert.Null(Access.TmdbPosterPath(null, "не-хэш"));
    }

    // ── защита: суффикс уходит в /tmdb/img/{*suffix} дословно ─────────────

    [Theory]
    [InlineData("http://192.168.87.24:9118/tmdb/img/t/p/../../3/movie/550")]
    [InlineData("http://192.168.87.24:9118/tmdb/img/t/p/w500/../../../etc/passwd")]
    [InlineData("http://192.168.87.24:9118/tmdb/img/t/p/w500/a.jpg/../../b")]
    [InlineData("http://192.168.87.24:9118/tmdb/img/t/p/w500/a.svg")]    // не растровая картинка
    [InlineData("http://192.168.87.24:9118/tmdb/img/t/p/w500/a")]        // без расширения
    [InlineData("http://192.168.87.24:9118/tmdb/img/t/p/w500/")]         // пусто
    public void Traversal_And_JunkPaths_Null(string url)
    {
        TestEnv.FreshCache();   // без меты — чтобы null означал именно отказ, а не подстановку
        Assert.Null(Access.TmdbPosterPath(url, Hash));
    }

    // ── не-TMDB постер: не наше дело, уходит во внешнюю ветку Save ────────

    [Fact]
    public void ExternalNonTmdbUrl_Null()
    {
        TestEnv.FreshCache();
        Assert.Null(Access.TmdbPosterPath("https://example.com/a.jpg", Hash));
    }

    /// <summary>
    /// Дыру не открыли: приватный не-TMDB хост по-прежнему отбивается — резолвер его не берёт,
    /// а внешняя ветка Save режет его прежним анти-SSRF гардом.
    /// </summary>
    [Fact]
    public void PrivateNonTmdbUrl_StillRejected()
    {
        TestEnv.FreshCache();
        Assert.Null(Access.TmdbPosterPath("http://192.168.1.5/evil.jpg", Hash));
        Assert.True(Access.IsPrivateHost(new Uri("http://192.168.1.5/evil.jpg")));
    }
}
