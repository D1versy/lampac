using JacRed.Models.AppConf;
using Shared.Models.AppConf;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using System.Threading;

namespace JacRed
{
    public class ModInit : IModuleLoaded
    {
        public static bool IsDispose;
        public static JacRedConf conf;

        public void Loaded(InitspaceModel baseconf)
        {
            IsDispose = false;
            Directory.CreateDirectory("cache/jacred");

            updateConf();
            EventListener.UpdateInitFile += updateConf;

            foreach (var m in conf.limit_map)
                CoreInit.conf.WAF.limit_map.Insert(0, m);

            CoreInit.BaseModValidQueryValueWhiteList.Add("query");

            ThreadPool.QueueUserWorkItem(async _ => await SyncCron.Run());
            ThreadPool.QueueUserWorkItem(async _ => await FileDB.Cron());
            ThreadPool.QueueUserWorkItem(async _ => await FileDB.CronFast());


            ThreadPool.QueueUserWorkItem(async _ =>
            {
                while (!IsDispose)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));

                    try
                    {
                        if (conf.typesearch == "jackett" || conf.merge == "jackett")
                        {
                            async ValueTask<bool> showdown(string name, TrackerSettings settings)
                            {
                                if (!settings.monitor_showdown)
                                    return false;

                                var proxyManager = new ProxyManager(name, settings);

                                // ⚠️ Сторожевой пинг обязан ходить по тому же правилу, что и парсеры.
                                // Иначе дохлый прокси гасит трекер (showdown=true → search() выходит
                                // сразу) ДО того, как парсерный фолбэк успеет сработать, и вся фича
                                // мертворождённая. Бонус: этот цикл раз в 5 минут ПРОГРЕВАЕТ вердикт
                                // ProxyFallback — двойной запрос платит фон, а пользовательский поиск
                                // идёт сразу правильным путём.
                                string html = await JacBaseController.HttpOrDirect(name, settings, proxyManager,
                                    h => h != null,
                                    p => Http.Get($"{settings.host}", timeoutSeconds: conf.Jackett.timeoutSeconds, proxy: p, weblog: false));

                                return html == null;
                            }

                            conf.Jackett.Rutor.showdown = await showdown("rutor", conf.Jackett.Rutor);
                            conf.Jackett.Megapeer.showdown = await showdown("megapeer", conf.Jackett.Megapeer);
                            conf.Jackett.TorrentBy.showdown = await showdown("torrentby", conf.Jackett.TorrentBy);
                            conf.Jackett.Kinozal.showdown = await showdown("kinozal", conf.Jackett.Kinozal);
                            conf.Jackett.NNMClub.showdown = await showdown("nnmclub", conf.Jackett.NNMClub);
                            conf.Jackett.Bitru.showdown = await showdown("bitru", conf.Jackett.Bitru);
                            conf.Jackett.Toloka.showdown = await showdown("toloka", conf.Jackett.Toloka);
                            conf.Jackett.Rutracker.showdown = await showdown("rutracker", conf.Jackett.Rutracker);
                            conf.Jackett.BigFanGroup.showdown = await showdown("bigfangroup", conf.Jackett.BigFanGroup);
                            conf.Jackett.Selezen.showdown = await showdown("selezen", conf.Jackett.Selezen);
                            conf.Jackett.Lostfilm.showdown = await showdown("lostfilm", conf.Jackett.Lostfilm);
                            conf.Jackett.Anilibria.showdown = await showdown("anilibria", conf.Jackett.Anilibria);
                            conf.Jackett.Animelayer.showdown = await showdown("animelayer", conf.Jackett.Animelayer);
                            conf.Jackett.Anifilm.showdown = await showdown("anifilm", conf.Jackett.Anifilm);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Serilog.Log.Error(ex, "{Class} {CatchId}", "ModInit", "id_p73el7f6");
                    }
                }
            });
        }

        public void Dispose()
        {
            IsDispose = true;
            EventListener.UpdateInitFile -= updateConf;

            // Снять сессию солвера при штатной остановке: иначе в контейнере остаётся живой Chrome.
            // Ожидание ограничено 3 секундами — у docker stop всего 10 до SIGKILL, и уборка не
            // имеет права задерживать остановку. Если не успели, не страшно: имя сессии стабильное,
            // и следующий процесс снесёт её по этому же имени (см. EnsureSession).
            try { FlareSolverr.DropSession(conf?.Jackett?.flaresolverr).Wait(TimeSpan.FromSeconds(3)); } catch { }
        }

        void updateConf()
        {
            // Вердикты фолбэка живут в статике и переживают перечит конфига намеренно — но смена
            // выключателей должна применяться сразу, а не через кулдаун. Значит перечит init.conf
            // это ещё и рычаг сброса «прокси мёртв / трекер лежит».
            ProxyFallback.Reset();

            conf = ModuleInvoke.Init("JacRed", new JacRedConf()
            {
                typesearch = "webapi",
                webApiHost = "http://ns3bg91xvuqfvq9h.cfhttp.top",
                merge = null,
                disableJackett = true,
                limit_map = new List<WafLimitRootMap>()
                {
                    new("^/api/(v1.0|v2.0)/", new WafLimitMap { limit = 10, second = 1 })
                }
            });
        }
    }
}
