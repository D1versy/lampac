using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Middlewares;

/// <summary>
/// D1Vision (qdl 2.110): счётчик запросов в полёте + отметка последнего запроса — для бесшовного
/// редеплоя (Modules/QbitDownload/Deploy.cs). Стоит первым в пайплайне, сразу после
/// UseForwardedHeaders, поэтому видит ВСЁ, включая хиты Staticache и статику. Не считает /nws
/// (сокет живёт часами и закрывается при заморозке отдельно) и /qdl/deploy/* (опрос самого
/// деплоя и хелсчеки Caddy — иначе inflight никогда не дошёл бы до нуля).
/// </summary>
public class D1VInflight
{
    private readonly RequestDelegate _next;

    public D1VInflight(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext httpContext)
    {
        var path = httpContext.Request.Path;
        // /nws — сокет живёт часами; /qdl/deploy/* — опрос деплоя и хелсчеки Caddy; X-QDL-Warmup —
        // собственные реплеи прогрева (CatalogWarmup/MusicWarm/OnlineWarm ходят на 127.0.0.1):
        // это не клиенты, и считать их значило бы никогда не увидеть inflight=0 при дотяжке.
        if (path.StartsWithSegments("/nws") || path.StartsWithSegments("/qdl/deploy")
            || httpContext.Request.Headers.ContainsKey("X-QDL-Warmup"))
        {
            await _next(httpContext);
            return;
        }

        Interlocked.Exchange(ref Shared.Startup.LastRequestTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref Shared.Startup.Inflight);
        try { await _next(httpContext); }
        finally { Interlocked.Decrement(ref Shared.Startup.Inflight); }
    }
}

public static class D1VInflightExtensions
{
    public static IApplicationBuilder UseD1VInflight(this IApplicationBuilder builder)
        => builder.UseMiddleware<D1VInflight>();
}
