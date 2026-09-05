using Shared;
using Shared.Models.AppConf;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.Threading;

namespace CubProxy;

public class ModInit : IModuleLoaded
{
    public static string modpath;
    public static ModuleConf conf;

    static Timer _pruneTimer;

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;

        updateConf();
        EventListener.UpdateInitFile += updateConf;

        foreach (var m in conf.limit_map)
            CoreInit.conf.WAF.limit_map.Insert(0, m);

        // qdl 2.112: уборка протухших копий страниц (PageStore). Раз в сутки, первый заход через
        // час после старта — на старте у контейнера есть дела поважнее, а копий там от силы
        // полсотни файлов по 7–22 КБ.
        _pruneTimer = new Timer(_ =>
        {
            try { PageStore.Prune(conf?.pageGuardKeepMinutes ?? 1440); } catch { }
        }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;

        _pruneTimer?.Dispose();
        _pruneTimer = null;
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
