using System;
using System.Threading.Tasks;
using Core.Middlewares;
using Microsoft.AspNetCore.Http;
using Shared;
using Shared.Models.AppConf;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// CSP на отдаваемые документы (Core/Middlewares/D1VContentPolicy.cs, qdl 2.88).
///
/// Что именно тут стережётся — не «заголовок присутствует», а три решения, которые легко
/// откатить по невнимательности:
///
///  1. 'unsafe-inline' и 'unsafe-eval' в script-src ОБЯЗАТЕЛЬНЫ. В index.html три инлайн-блока
///     (два document.write и весь загрузчик putScript), а RCH исполняет eval(data) из сокета.
///     Убрать их «для безопасности» = белый экран и мёртвые RCH-балансеры. Это единственное
///     место, где такое решение записано исполняемо, а не комментарием.
///  2. Страница-мост трейлеров получает СВОЙ, более широкий CSP. Иначе главная либо ломает
///     трейлеры, либо расширяется до youtube.com целиком.
///  3. Кил-свитч через пустую строку в конфиге работает. Секция d1v перечитывается на лету,
///     и это единственный способ погасить CSP без пересборки образа.
///
/// Как и D1VPerimeterTests, гоняем Invoke() на DefaultHttpContext: middleware — обычный метод,
/// а TestServer притащил бы весь Startup форка ради двадцати строк.
/// </summary>
public class D1VContentPolicyTests
{
    sealed class Spy
    {
        public int Calls;
        public RequestDelegate Next => _ => { Calls++; return Task.CompletedTask; };
    }

    sealed class Conf : IDisposable
    {
        readonly D1vConf _prev;
        public Conf(D1vConf c) { TestEnv.EnsureConf(); _prev = CoreInit.conf.d1v; CoreInit.conf.d1v = c; }
        public void Dispose() => CoreInit.conf.d1v = _prev;
    }

    static (HttpContext ctx, Spy spy) Run(string path, D1vConf conf, string host = "192.168.87.24")
    {
        using var _ = new Conf(conf);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString(host);

        var spy = new Spy();
        new D1VContentPolicy(spy.Next).Invoke(ctx).GetAwaiter().GetResult();

        return (ctx, spy);
    }

    static D1vConf On(Action<D1vConf> tweak = null)
    {
        var c = new D1vConf { enable = true };
        tweak?.Invoke(c);
        return c;
    }

    static string Csp(HttpContext ctx) => ctx.Response.Headers["Content-Security-Policy"].ToString();
    static string CspRo(HttpContext ctx) => ctx.Response.Headers["Content-Security-Policy-Report-Only"].ToString();

    [Fact]
    public void Root_GetsPolicy()
    {
        var (ctx, spy) = Run("/", On());

        Assert.False(string.IsNullOrEmpty(Csp(ctx)));
        Assert.Equal(1, spy.Calls);   // middleware не глотает запрос
    }

    /// <summary>🔴 Без этих двух источников страница не грузится вообще. См. шапку класса.</summary>
    [Fact]
    public void Root_KeepsUnsafeInlineAndEval()
    {
        var (ctx, _) = Run("/", On());
        string csp = Csp(ctx);

        Assert.Contains("script-src", csp);
        Assert.Contains("'unsafe-inline'", csp);
        Assert.Contains("'unsafe-eval'", csp);
    }

    /// <summary>Главная НЕ должна разрешать чужие фреймы — youtube живёт на своей странице.</summary>
    [Fact]
    public void Root_DoesNotAllowForeignFrames()
    {
        var (ctx, _) = Run("/", On());
        string csp = Csp(ctx);

        Assert.Contains("frame-src 'self'", csp);
        Assert.DoesNotContain("frame-src https://www.youtube.com", csp);
    }

    /// <summary>Страница-мост трейлеров — отдельная, более широкая политика.</summary>
    [Fact]
    public void YoutubeBridge_GetsWiderPolicy()
    {
        var (ctx, _) = Run("/lampa-main/youtube.html", On());
        string csp = Csp(ctx);

        Assert.Contains("https://www.youtube.com", csp);
        Assert.Contains("frame-src", csp);
    }

    [Theory]
    [InlineData("/tmdb/img/t/p/w300/abc.jpg")]
    [InlineData("/proxyimg/xxx")]
    [InlineData("/qdl/health")]
    [InlineData("/app.min.js")]
    public void NonDocuments_NoPolicy(string path)
    {
        var (ctx, _) = Run(path, On());
        Assert.True(string.IsNullOrEmpty(Csp(ctx)));
    }

    [Fact]
    public void Disabled_NoPolicy()
    {
        var (ctx, spy) = Run("/", new D1vConf { enable = false });

        Assert.True(string.IsNullOrEmpty(Csp(ctx)));
        Assert.Equal(1, spy.Calls);
    }

    /// <summary>Кил-свитч: пустая строка в конфиге гасит заголовок, не трогая образ.</summary>
    [Fact]
    public void EmptyCsp_IsKillSwitch()
    {
        var (ctx, _) = Run("/", On(c => c.csp = ""));

        Assert.True(string.IsNullOrEmpty(Csp(ctx)));
        Assert.True(string.IsNullOrEmpty(CspRo(ctx)));
    }

    /// <summary>Режим обкатки: браузер только жалуется, ничего не режет.</summary>
    [Fact]
    public void ReportOnly_UsesReportHeader()
    {
        var (ctx, _) = Run("/", On(c => c.cspReportOnly = true));

        Assert.True(string.IsNullOrEmpty(Csp(ctx)));
        Assert.False(string.IsNullOrEmpty(CspRo(ctx)));
    }

    [Fact]
    public void CustomCsp_Overrides()
    {
        var (ctx, _) = Run("/", On(c => c.csp = "default-src 'none'"));
        Assert.Equal("default-src 'none'", Csp(ctx));
    }

    /// <summary>
    /// 🔴 Раздел XSMART живёт в отдельном контейнере на порту 9140 — для CSP это другой origin,
    /// и 'self' его режет. Поймано xsmartcheck при первом же включении CSP: плагин не грузился,
    /// пункт меню исчезал, JS-ошибок при этом ноль. Тест держит развилку «дома добавляем :9140».
    /// </summary>
    [Theory]
    [InlineData("192.168.87.24")]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.0.3")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Lan_AllowsXsmartOrigin(string host)
    {
        var (ctx, _) = Run("/", On(), host);
        Assert.Contains($"http://{host}:9140", Csp(ctx));
    }

    /// <summary>Снаружи XSMART идёт через Caddy на нашем же адресе — добавлять нечего.</summary>
    [Theory]
    [InlineData("tv.d1versy.com")]
    [InlineData("172.15.0.1")]   // вне 172.16/12 — не наша сеть
    [InlineData("172.32.0.1")]
    public void Wan_NoExtraOrigin(string host)
    {
        var (ctx, _) = Run("/", On(), host);
        string csp = Csp(ctx);

        Assert.DoesNotContain(":9140", csp);
        Assert.DoesNotContain("{xsmart}", csp);   // плейсхолдер обязан быть раскрыт всегда
    }

    [Fact]
    public void Placeholder_NeverLeaksIntoHeader()
    {
        foreach (var path in new[] { "/", "/lampa-main/youtube.html" })
        {
            var (ctx, _) = Run(path, On());
            Assert.DoesNotContain("{xsmart}", Csp(ctx));
        }
    }
}
