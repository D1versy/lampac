using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Middlewares;
using Microsoft.AspNetCore.Http;
using Shared;
using Shared.Models.AppConf;
using Shared.Models.Base;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Периметр внешнего доступа D1Vision (Core/Middlewares/D1VPerimeter.cs).
///
/// Через эти 145 строк проходит ВЕСЬ внешний трафик — websocket, статика, /proxy*, /ts/*,
/// все контроллеры. До этого файла у него не было ни одного теста.
///
/// Гоняем Invoke() на DefaultHttpContext: middleware — обычный метод, а поднимать TestServer
/// значило бы тянуть весь Startup форка (Roslyn-компиляция модулей, SQLite, Playwright, Kestrel)
/// ради 145 строк, да ещё и драться с общей мутацией CoreInit.conf в этой же сьюте.
/// </summary>
public class D1VPerimeterTests
{
    const string Key = "aaaabbbbccccdddd1111222233334444aaaabbbbccccdddd1111222233334444";
    const string OtherKey = "ffffeeeeddddcccc9999888877776666ffffeeeeddddcccc9999888877776666";
    const string Edge = "X-D1V-Edge";

    /// <summary>Оракул «пропустило ли»: считает вызовы _next.</summary>
    sealed class Spy
    {
        public int Calls;
        public RequestDelegate Next => _ => { Calls++; return Task.CompletedTask; };
    }

    /// <summary>Ставит CoreInit.conf.d1v на время теста и возвращает прежнее значение обратно —
    /// сьюта серийная, но соседние тесты (Live) читают ту же секцию.</summary>
    sealed class Conf : IDisposable
    {
        readonly D1vConf _prev;
        public Conf(D1vConf c) { TestEnv.EnsureConf(); _prev = CoreInit.conf.d1v; CoreInit.conf.d1v = c; }
        public void Dispose() => CoreInit.conf.d1v = _prev;
    }

    static D1vConf Enabled(Action<D1vConf> tweak = null)
    {
        var c = new D1vConf
        {
            enable = true,
            edgeHeader = Edge,
            cookieName = "d1v",
            cookieDays = 365,
            keys = new Dictionary<string, string> { ["mac"] = Key, ["android"] = OtherKey }
        };
        tweak?.Invoke(c);
        return c;
    }

    static DefaultHttpContext Ctx(string path = "/lampainit.js", bool edge = true, string method = "GET",
                                  string scheme = "http", bool local = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Request.Scheme = scheme;
        ctx.Features.Set(new RequestModel { IsLocalRequest = local, IP = "203.0.113.7" });
        if (edge) ctx.Request.Headers[Edge] = "1";
        return ctx;
    }

    static async Task<(int calls, DefaultHttpContext ctx)> Run(D1vConf conf, DefaultHttpContext ctx)
    {
        using (new Conf(conf))
        {
            var spy = new Spy();
            await new D1VPerimeter(spy.Next).Invoke(ctx);
            return (spy.Calls, ctx);
        }
    }

    static string SetCookie(HttpContext ctx) =>
        ctx.Response.Headers.TryGetValue("Set-Cookie", out var v) ? string.Join("|", v.ToArray()) : null;

    static void AssertStealth404(DefaultHttpContext ctx)
    {
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("application/octet-stream", ctx.Response.ContentType);
    }

    // ── выключенный периметр ──────────────────────────────────────────────

    [Fact]
    public async Task Conf_null_passes_through()
    {
        var (calls, _) = await Run(null, Ctx());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Disabled_passes_through_even_from_edge_without_key()
    {
        var (calls, _) = await Run(Enabled(c => c.enable = false), Ctx());
        Assert.Equal(1, calls);
    }

    // ── свои: локальные межмодульные вызовы и LAN ─────────────────────────

    [Fact]
    public async Task LocalRequest_passes_even_with_edge_marker_and_no_key()
    {
        var (calls, _) = await Run(Enabled(), Ctx(local: true));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LocalRequest_passes_even_on_admin_path()
    {
        // lcrqpasswd-вызовы — всегда свои, они не доходят даже до admin-deny.
        var (calls, _) = await Run(Enabled(), Ctx("/admin/d1v", local: true));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task No_edge_marker_in_request_means_LAN_and_passes()
    {
        var (calls, _) = await Run(Enabled(), Ctx(edge: false));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Empty_edgeHeader_in_conf_disables_the_check(string header)
    {
        // Маркер не настроен → отличить внешний запрос от LAN нечем → пропускаем.
        var (calls, _) = await Run(Enabled(c => c.edgeHeader = header), Ctx(edge: false));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Forged_edge_marker_from_LAN_only_ENABLES_the_check()
    {
        // fail-closed: подделка маркера не открывает дверь, а закрывает — ключа-то нет.
        var (calls, ctx) = await Run(Enabled(), Ctx());
        Assert.Equal(0, calls);
        AssertStealth404(ctx);
    }

    // ── admin-deny: снаружи 404 ВСЕГДА, даже с валидным ключом ────────────

    [Theory]
    [InlineData("/adminpanel")]
    [InlineData("/admin")]
    [InlineData("/admin/d1v")]
    // История просмотров — самые чувствительные данные во всей админке: кто что и когда смотрел.
    [InlineData("/admin/d1v/history")]
    [InlineData("/admin/d1v/api/history")]
    [InlineData("/stats")]
    [InlineData("/weblog")]
    public async Task Admin_paths_are_404_from_outside_even_WITH_a_valid_key(string path)
    {
        var ctx = Ctx(path);
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(0, calls);
        AssertStealth404(res);
        Assert.Null(SetCookie(res));   // отказ не сеет cookie
    }

    [Theory]
    [InlineData("/ADMIN/d1v")]
    [InlineData("/AdminPanel")]
    [InlineData("/Stats")]
    public async Task Admin_deny_is_case_insensitive(string path)
    {
        var ctx = Ctx(path);
        ctx.Request.Headers["X-D1V-Key"] = Key;
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Fact]
    public async Task Admin_deny_matches_by_PREFIX_not_by_segment()
    {
        // Задокументированное поведение: «/administrivia» тоже отсекается, потому что сравнение
        // префиксное. Это намеренно (secure by default) — расширять список безопаснее, чем сужать.
        var ctx = Ctx("/administrivia");
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Fact]
    public void Admin_deny_list_is_hardcoded_and_covers_four_prefixes()
    {
        // Канарейка: список зашит в код намеренно (не в конфиг, чтобы его нельзя было ослабить
        // правкой init.conf). Изменение состава обязано быть осознанным.
        var field = typeof(D1VPerimeter).GetField("ExternalDenyPrefixes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var prefixes = (string[])field.GetValue(null);

        Assert.Equal(new[] { "/adminpanel", "/admin", "/stats", "/weblog" }, prefixes);
    }

    // ── ключ: источники и приоритет ───────────────────────────────────────

    [Fact]
    public async Task Valid_key_in_query_passes_and_is_planted_into_cookie()
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(1, calls);
        string cookie = SetCookie(res);
        Assert.Contains("d1v=" + Key, cookie);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_key_in_header_passes_and_is_planted_into_cookie()
    {
        var ctx = Ctx();
        ctx.Request.Headers["X-D1V-Key"] = Key;
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(1, calls);
        Assert.Contains("d1v=" + Key, SetCookie(res));
    }

    [Fact]
    public async Task Valid_key_in_cookie_passes_and_is_NOT_re_planted()
    {
        var ctx = Ctx();
        ctx.Request.Headers["Cookie"] = "d1v=" + Key;
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(1, calls);
        Assert.Null(SetCookie(res));   // лишний Set-Cookie на каждом запросе — трафик впустую
    }

    [Fact]
    public async Task Query_beats_header_and_cookie()
    {
        // Порядок источников — часть контракта: свежий ключ из query обязан перебивать
        // протухшую cookie, иначе после ротации клиент не починится сам.
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        ctx.Request.Headers["X-D1V-Key"] = "garbage";
        ctx.Request.Headers["Cookie"] = "d1v=garbage";
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(1, calls);
        Assert.Contains("d1v=" + Key, SetCookie(res));
    }

    [Fact]
    public async Task Header_beats_cookie()
    {
        var ctx = Ctx();
        ctx.Request.Headers["X-D1V-Key"] = Key;
        ctx.Request.Headers["Cookie"] = "d1v=garbage";
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(1, calls);
        Assert.Contains("d1v=" + Key, SetCookie(res));
    }

    [Fact]
    public async Task Stale_cookie_is_overwritten_when_query_presents_a_different_valid_key()
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + OtherKey);
        ctx.Request.Headers["Cookie"] = "d1v=" + Key;
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(1, calls);
        Assert.Contains("d1v=" + OtherKey, SetCookie(res));
    }

    [Fact]
    public async Task Any_platform_key_is_accepted()
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + OtherKey);
        var (calls, _) = await Run(Enabled(), ctx);
        Assert.Equal(1, calls);
    }

    // ── ключ: отказы ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("wrong")]
    [InlineData("aaaabbbbccccdddd1111222233334444aaaabbbbccccdddd111122223333444")]   // на символ короче
    [InlineData("aaaabbbbccccdddd1111222233334444aaaabbbbccccdddd11112222333344445")]  // на символ длиннее
    public async Task Invalid_key_is_a_stealth_404(string presented)
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + presented);
        var (calls, res) = await Run(Enabled(), ctx);

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Fact]
    public async Task Null_keys_dictionary_denies_everyone()
    {
        // fail-closed: не настроили ключи — периметр закрыт, а не открыт.
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (calls, res) = await Run(Enabled(c => c.keys = null), ctx);

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Fact]
    public async Task Empty_keys_dictionary_denies_everyone()
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (calls, res) = await Run(Enabled(c => c.keys = new Dictionary<string, string>()), ctx);

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Empty_key_VALUE_never_matches_an_empty_presentation(string configured)
    {
        // 🔴 Самый опасный класс: пустая строка в init.conf не должна превращаться
        // в «ключ, который подходит всем». Именно поэтому entrypoint.sh реплики
        // требует все четыре D1V_KEY_*.
        var ctx = Ctx();
        ctx.Request.Headers["X-D1V-Key"] = "";
        var (calls, res) = await Run(
            Enabled(c => c.keys = new Dictionary<string, string> { ["mac"] = configured }), ctx);

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    // ── публичные префиксы (OTA) ──────────────────────────────────────────

    [Fact]
    public async Task Public_prefix_is_open_for_GET_without_a_key()
    {
        // Клиент без ключа (или после ротации) обязан суметь скачать обновление,
        // которое этот ключ и чинит.
        var conf = Enabled(c => c.publicPrefixes = new List<string> { "/d1vision/apps/" });
        var (calls, _) = await Run(conf, Ctx("/d1vision/apps/windows/Setup.exe"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Public_prefix_is_case_insensitive()
    {
        var conf = Enabled(c => c.publicPrefixes = new List<string> { "/d1vision/apps/" });
        var (calls, _) = await Run(conf, Ctx("/D1Vision/Apps/mac/appcast.xml"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Public_prefix_is_GET_only(string method)
    {
        var conf = Enabled(c => c.publicPrefixes = new List<string> { "/d1vision/apps/" });
        var (calls, res) = await Run(conf, Ctx("/d1vision/apps/windows/Setup.exe", method: method));

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Fact]
    public async Task Null_publicPrefixes_denies()
    {
        var (calls, res) = await Run(Enabled(c => c.publicPrefixes = null), Ctx("/d1vision/apps/x"));
        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Fact]
    public async Task Empty_string_in_publicPrefixes_does_not_open_everything()
    {
        // "" — префикс любого пути; гард на IsNullOrEmpty обязан его отбросить.
        var conf = Enabled(c => c.publicPrefixes = new List<string> { "" });
        var (calls, res) = await Run(conf, Ctx("/qdl/delete"));

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    [Fact]
    public async Task Public_prefix_never_overrides_admin_deny()
    {
        // Даже если кто-то впишет /admin в publicPrefixes — admin-deny стоит РАНЬШЕ и выигрывает.
        var conf = Enabled(c => c.publicPrefixes = new List<string> { "/admin" });
        var (calls, res) = await Run(conf, Ctx("/admin/d1v"));

        Assert.Equal(0, calls);
        AssertStealth404(res);
    }

    // ── атрибуты cookie ───────────────────────────────────────────────────

    [Fact]
    public async Task Cookie_is_Secure_only_over_https()
    {
        var http = Ctx(scheme: "http");
        http.Request.QueryString = new QueryString("?d1v=" + Key);
        var (_, r1) = await Run(Enabled(), http);
        Assert.DoesNotContain("secure", SetCookie(r1), StringComparison.OrdinalIgnoreCase);

        var https = Ctx(scheme: "https");
        https.Request.QueryString = new QueryString("?d1v=" + Key);
        var (_, r2) = await Run(Enabled(), https);
        Assert.Contains("secure", SetCookie(r2), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieDays_zero_falls_back_to_a_year()
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (_, res) = await Run(Enabled(c => c.cookieDays = 0), ctx);

        Assert.Contains("max-age=" + (int)TimeSpan.FromDays(365).TotalSeconds,
            SetCookie(res), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieDays_is_honoured_when_set()
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (_, res) = await Run(Enabled(c => c.cookieDays = 30), ctx);

        Assert.Contains("max-age=" + (int)TimeSpan.FromDays(30).TotalSeconds,
            SetCookie(res), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_cookieName_passes_the_key_through_without_planting()
    {
        var ctx = Ctx();
        ctx.Request.QueryString = new QueryString("?d1v=" + Key);
        var (calls, res) = await Run(Enabled(c => c.cookieName = ""), ctx);

        Assert.Equal(1, calls);
        Assert.Null(SetCookie(res));
    }

    // ── стелс ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Denial_looks_identical_for_wrong_key_and_missing_key()
    {
        // Разница в ответах помогала бы перебирать ключи.
        var noKey = Ctx("/qdl/list");
        var (_, a) = await Run(Enabled(), noKey);

        var badKey = Ctx("/qdl/list");
        badKey.Request.QueryString = new QueryString("?d1v=nope");
        var (_, b) = await Run(Enabled(), badKey);

        Assert.Equal(a.Response.StatusCode, b.Response.StatusCode);
        Assert.Equal(a.Response.ContentType, b.Response.ContentType);
        Assert.Equal(SetCookie(a), SetCookie(b));
    }

    [Fact]
    public async Task Null_requestInfo_does_not_throw()
    {
        // RequestModel может отсутствовать (запрос до UseRequestInfo) — логирование
        // обязано пережить это, а не уронить весь конвейер.
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/qdl/list";
        ctx.Request.Method = "GET";
        ctx.Request.Headers[Edge] = "1";

        var (calls, res) = await Run(Enabled(), ctx);
        Assert.Equal(0, calls);
        AssertStealth404(res);
    }
}
