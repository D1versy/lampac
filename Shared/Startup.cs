using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Shared.Models;

namespace Shared;

public class Startup
{
    public static bool IsShutdown { get; set; }

    /// <summary>
    /// D1Vision (qdl 2.110): запросы в полёте и тик последнего запроса. Пишет Core/Middlewares/D1VInflight.cs,
    /// читает Modules/QbitDownload/Deploy.cs — дренаж и «тишина» при заморозке экземпляра (blue/green-редеплой).
    /// </summary>
    public static long Inflight;
    public static long LastRequestTicks;

    public static INws Nws { get; set; }

    public static AppReload appReload { get; private set; }

    public static IServiceProvider ApplicationServices { get; private set; }

    public static IMemoryCache memoryCache { get; private set; }

    public static void Configure(AppReload reload, INws nws)
    {
        appReload = reload;
        Nws = nws;
    }

    public static void Configure(IApplicationBuilder app, IMemoryCache mem)
    {
        ApplicationServices = app.ApplicationServices;
        memoryCache = mem;
    }
}


public class AppReload
{
    public Action InkvReload { get; set; }

    public void Reload()
    {
        if (InkvReload == null)
            return;

        InkvReload();
    }
}
