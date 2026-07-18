using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Shared;
using Shared.Models.Base;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Core.Middlewares;

/// <summary>
/// D1Vision: периметр внешнего доступа (claude/08 репо-оркестрации).
///
/// Модель: наружу проброшен ТОЛЬКО один порт (9443) → Caddy, который принудительно (header_up)
/// ставит заголовок-маркер conf.d1v.edgeHeader. Запрос с маркером = пришёл из интернета
/// и обязан предъявить ключ платформы (query d1v / заголовок X-D1V-Key / cookie d1v).
/// Запрос без маркера мог прийти только из LAN (9118 не форвардится) — пропускается.
/// Валидный ключ из query/заголовка пересаживается в cookie — дальше WebView-движки
/// клиентов шлют её сами на каждый запрос (XHR/media/ws), натив не участвует.
/// Отказ — пустой 404 (стелс), как у Accsdb для статики.
///
/// Ставится в Startup сразу после UseRequestInfo и ДО Map("/nws")/Staticache —
/// покрывает websocket, кэш, статику, /proxy*, /ts/* и все контроллеры.
/// </summary>
public class D1VPerimeter
{
    private readonly RequestDelegate _next;

    // Админ/служебные пути lampac — извне НЕДОСТУПНЫ ни при каких условиях (даже с ключом).
    // Только из LAN (запрос без edge-маркера сюда не доходит). Захардкожено = secure by default.
    private static readonly string[] ExternalDenyPrefixes = { "/adminpanel", "/admin", "/stats", "/weblog" };

    public D1VPerimeter(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext httpContext)
    {
        var conf = CoreInit.conf.d1v;

        if (conf == null || !conf.enable)
            return _next(httpContext);

        // Внутренние межмодульные вызовы (lcrqpasswd) — всегда свои.
        var requestInfo = httpContext.Features.Get<RequestModel>();
        if (requestInfo != null && requestInfo.IsLocalRequest)
            return _next(httpContext);

        // Нет маркера Caddy → запрос не проходил через edge → LAN → открыто.
        // (Подделка маркера из LAN лишь ВКЛЮЧАЕТ проверку ключа — fail-closed.)
        if (string.IsNullOrEmpty(conf.edgeHeader) || !httpContext.Request.Headers.ContainsKey(conf.edgeHeader))
            return _next(httpContext);

        // ── Дальше запрос считается внешним ──

        // Админ-пути закрыты СНАРУЖИ всегда — даже с валидным ключом (админка только из LAN).
        string reqPath = httpContext.Request.Path.Value ?? string.Empty;
        foreach (string deny in ExternalDenyPrefixes)
        {
            if (reqPath.StartsWith(deny, StringComparison.OrdinalIgnoreCase))
            {
                Serilog.Log.Information("D1VPerimeter admin-deny: {IP} {Path}", requestInfo?.IP, reqPath);
                httpContext.Response.StatusCode = 404;
                httpContext.Response.ContentType = "application/octet-stream";
                return Task.CompletedTask;
            }
        }

        // Ключ: query → заголовок → cookie.
        string presented = null;
        bool fromCookie = false;

        if (httpContext.Request.Query.TryGetValue("d1v", out StringValues qv) && qv.Count > 0 && !string.IsNullOrEmpty(qv[0]))
            presented = qv[0];
        else if (httpContext.Request.Headers.TryGetValue("X-D1V-Key", out StringValues hv) && hv.Count > 0 && !string.IsNullOrEmpty(hv[0]))
            presented = hv[0];
        else if (!string.IsNullOrEmpty(conf.cookieName) && httpContext.Request.Cookies.TryGetValue(conf.cookieName, out string cv) && !string.IsNullOrEmpty(cv))
        {
            presented = cv;
            fromCookie = true;
        }

        if (presented != null && IsValidKey(conf.keys, presented))
        {
            // Пересадить ключ в cookie (посев при первой навигации с ?d1v=),
            // чтобы все последующие запросы WebView несли его автоматически.
            if (!fromCookie && !string.IsNullOrEmpty(conf.cookieName))
            {
                bool needSet = !httpContext.Request.Cookies.TryGetValue(conf.cookieName, out string existing) || existing != presented;
                if (needSet)
                {
                    httpContext.Response.Cookies.Append(conf.cookieName, presented, new CookieOptions()
                    {
                        Path = "/",
                        MaxAge = TimeSpan.FromDays(conf.cookieDays > 0 ? conf.cookieDays : 365),
                        HttpOnly = true,
                        Secure = httpContext.Request.Scheme == "https",
                        SameSite = SameSiteMode.Lax
                    });
                }
            }

            return _next(httpContext);
        }

        // Публичные GET-префиксы (OTA-канал /d1vision/apps/ — бинари подписаны).
        if (conf.publicPrefixes != null && HttpMethods.IsGet(httpContext.Request.Method))
        {
            string path = httpContext.Request.Path.Value ?? string.Empty;
            foreach (string prefix in conf.publicPrefixes)
            {
                if (!string.IsNullOrEmpty(prefix) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return _next(httpContext);
            }
        }

        // Отказ: пустой 404 (стелс — не отличим от несуществующего пути).
        Serilog.Log.Information("D1VPerimeter deny: {IP} {Method} {Path}", requestInfo?.IP, httpContext.Request.Method, httpContext.Request.Path.Value);
        httpContext.Response.StatusCode = 404;
        httpContext.Response.ContentType = "application/octet-stream";
        return Task.CompletedTask;
    }

    static bool IsValidKey(System.Collections.Generic.Dictionary<string, string> keys, string presented)
    {
        if (keys == null || keys.Count == 0)
            return false;

        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);

        foreach (string key in keys.Values)
        {
            if (string.IsNullOrEmpty(key))
                continue;

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (keyBytes.Length == presentedBytes.Length && CryptographicOperations.FixedTimeEquals(keyBytes, presentedBytes))
                return true;
        }

        return false;
    }
}
