using Shared;
using Shared.Models.AppConf;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;
using System.Collections.Generic;

namespace CubProxy;

public class ModInit : IModuleLoaded
{
    public static string modpath;
    public static ModuleConf conf;

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;

        updateConf();
        EventListener.UpdateInitFile += updateConf;

        foreach (var m in conf.limit_map)
            CoreInit.conf.WAF.limit_map.Insert(0, m);
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
    }

    void updateConf()
    {
        conf = ModuleInvoke.Init("cub", new ModuleConf()
        {
            viewru = true,
            scheme = CoreInit.conf.cub.scheme,
            domain = CoreInit.conf.cub.domain,
            mirror = CoreInit.conf.cub.mirror,
            cache_api = 180, // 3h
            cache_reactions = 1440, // qdl 2.45: сутки — реакции почти статика, а просят их на каждой карточке
            stubAiMetadata = true, // qdl 2.45: у CUB там стабильный 500, ждать его незачем
            limit_map = new List<WafLimitRootMap>()
            {
                new("^/cub/", new WafLimitMap { limit = 50, second = 1 })
            }
        });
    }
}
