using Microsoft.EntityFrameworkCore;
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
    static System.Threading.Timer _notifyTimer;
    static System.Threading.Timer _huntTimer;

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;

        updateConf();
        EventListener.UpdateInitFile += updateConf;
        AppPatch.Attach();   // вырезание upstream-колокольчика/меню из app.min.js при отдаче (см. AppReplace.cs)

        // SQLite-хранилище уведомлений: создаём схему (без миграций) + WAL для параллельных read/write
        try
        {
            using var db = new SqlContext();
            db.Database.EnsureCreated();
            try { db.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;"); } catch { }
        }
        catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] db init: " + ex); }

        // обрывки прерванных транскодов (*.part) — мусор после рестарта, чистим сразу
        try { QbitController.CleanupTranscodeParts(); }
        catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] part cleanup: " + ex); }

        // GPU-воркер: добить джобы прошлого запуска контейнера (fire-and-forget, best-effort)
        try { FfWorker.ReapOrphans(); }
        catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] ffworker reap: " + ex); }

        // осиротевшие доноры охоты (add в qBit прошёл, watch.json не сохранился до рестарта) — убрать
        _ = System.Threading.Tasks.Task.Run(async () => {
            try { await QbitController.ReconcileDonors(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] donor reconcile: " + ex); }
        });

        // фоновое слежение за сериалами: первая проверка через 10 мин, далее каждые N часов
        int hours = (conf != null && conf.watchIntervalHours > 0) ? conf.watchIntervalHours : 6;
        _watchTimer?.Dispose();
        _watchTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.CheckWatches(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] watch timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(10), System.TimeSpan.FromHours(hours));

        // сканер «серия докачалась» → уведомления: первый запуск через 2 мин, далее каждые N минут
        int notifyMin = (conf != null && conf.notifyScanIntervalMinutes > 0) ? conf.notifyScanIntervalMinutes : 15;
        _notifyTimer?.Dispose();
        _notifyTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.ScanEpisodeNotifications(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] notify timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(2), System.TimeSpan.FromMinutes(notifyMin));

        // охота за сериями по всем раздачам (EpisodeHunter): дорого (опрос индексатора → всех
        // трекеров), поэтому свой редкий таймер; кламп ≥1 ч. Первый запуск через 15 мин.
        int huntHours = (conf != null && conf.episodeHuntIntervalHours > 0) ? System.Math.Max(1, conf.episodeHuntIntervalHours) : 4;
        _huntTimer?.Dispose();
        _huntTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.HuntAll(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] hunt timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(15), System.TimeSpan.FromHours(huntHours));
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
        AppPatch.Detach();
        _watchTimer?.Dispose();
        _watchTimer = null;
        _notifyTimer?.Dispose();
        _notifyTimer = null;
        _huntTimer?.Dispose();
        _huntTimer = null;
    }

    void updateConf()
    {
        conf = ModuleInvoke.Init("QbitDownload", new ModuleConf());
    }
}
