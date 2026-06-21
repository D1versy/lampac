using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;

namespace QbitDownload;

public class ModInit : IModuleLoaded
{
    public static string modpath;
    public static ModuleConf conf;
    static System.Threading.Timer _watchTimer;

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;

        updateConf();
        EventListener.UpdateInitFile += updateConf;

        // фоновое слежение за сериалами: первая проверка через 10 мин, далее каждые N часов
        int hours = (conf != null && conf.watchIntervalHours > 0) ? conf.watchIntervalHours : 6;
        _watchTimer?.Dispose();
        _watchTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.CheckWatches(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] watch timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(10), System.TimeSpan.FromHours(hours));
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
        _watchTimer?.Dispose();
        _watchTimer = null;
    }

    void updateConf()
    {
        conf = ModuleInvoke.Init("QbitDownload", new ModuleConf());
    }
}
