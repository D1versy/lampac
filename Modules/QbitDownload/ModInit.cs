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
    static System.Threading.Timer _diagTimer;
    static System.TimeSpan _huntPeriod = System.TimeSpan.FromHours(4);

    // Ранний повтор охоты (EpisodeHunter): индексатор не дал кандидатов → следующий тик раньше срока.
    // Change() перезадаёт и периодику, поэтому период передаём явно (иначе он стал бы «раз в due»).
    public static void RescheduleHunt(System.TimeSpan due)
    {
        try { _huntTimer?.Change(due, _huntPeriod); } catch { }
    }

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;

        updateConf();
        EventListener.UpdateInitFile += updateConf;
        AppPatch.Attach();   // вырезание upstream-колокольчика/меню из app.min.js при отдаче (см. AppReplace.cs)
        EventListener.MyLocalIp += MyIp;   // внешний IP без api.ipify.org (qdl 2.15, см. MyIp ниже)
        CatalogWarmup.Attach();            // почасовой прогрев каталога главной (CatalogWarmup.cs)

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

        // Охота за сериями (EpisodeHunter) дорогая — опрос индексатора → всех трекеров, поэтому свой
        // редкий таймер; кламп ≥1 ч. Догон пропущенных тиков: каждый рестарт контейнера отодвигал
        // первый прогон на +15 мин и обнулял отсчёт периода, так что при частых рестартах охота не
        // запускалась вовсе. Если с прошлого прогона (hunt.lastRun в watch.json) прошло больше
        // 1.5 периода — стартуем раньше.
        int huntHours = (conf != null && conf.episodeHuntIntervalHours > 0) ? System.Math.Max(1, conf.episodeHuntIntervalHours) : 4;
        _huntPeriod = System.TimeSpan.FromHours(huntHours);
        bool huntOverdue = false;
        var huntSince = System.TimeSpan.Zero;
        try { huntOverdue = QbitController.HuntOverdue(_huntPeriod, out huntSince); }
        catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] hunt overdue: " + ex.Message); }

        // Все три контура берут общий _watchGate (skip-if-busy) — опоздавший тик просто пропадает до
        // следующего периода, поэтому старты разведены. Догонная охота идёт минутами, так что
        // слежение при догоне сдвигаем за неё (у него период 6 ч — 20 минут роли не играют).
        var notifyFirst = System.TimeSpan.FromMinutes(2);
        var huntFirst = System.TimeSpan.FromMinutes(huntOverdue ? 4 : 15);
        var watchFirst = System.TimeSpan.FromMinutes(huntOverdue ? 30 : 10);
        if (huntOverdue)
            System.Console.WriteLine("[QbitDownload] hunt: догон после рестарта — прошлый прогон "
                + (huntSince == System.TimeSpan.Zero ? "не зафиксирован" : System.Math.Round(huntSince.TotalHours, 1) + " ч назад")
                + ", период " + huntHours + " ч → первый тик через " + huntFirst.TotalMinutes + " мин");

        // фоновое слежение за сериалами: первая проверка через watchFirst, далее каждые N часов
        int hours = (conf != null && conf.watchIntervalHours > 0) ? conf.watchIntervalHours : 6;
        _watchTimer?.Dispose();
        _watchTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.CheckWatches(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] watch timer: " + ex); }
        }, null, watchFirst, System.TimeSpan.FromHours(hours));

        // сканер «серия докачалась» → уведомления: первый запуск через 2 мин, далее каждые N минут
        int notifyMin = (conf != null && conf.notifyScanIntervalMinutes > 0) ? conf.notifyScanIntervalMinutes : 15;
        _notifyTimer?.Dispose();
        _notifyTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.ScanEpisodeNotifications(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] notify timer: " + ex); }
        }, null, notifyFirst, System.TimeSpan.FromMinutes(notifyMin));

        _huntTimer?.Dispose();
        _huntTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.HuntAll(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] hunt timer: " + ex); }
        }, null, huntFirst, _huntPeriod);

        // Мониторинг поиска (SearchMonitor.cs). Первый тик через 20 мин — после notify@2 / watch@10 /
        // hunt@15, чтобы старты не толкались. Таймер создаётся ВСЕГДА: при интервале 0 тик выходит
        // сразу, зато включение мониторинга не требует рестарта — updateConf() перезаводит период.
        _diagTimer?.Dispose();
        _diagTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if ((conf?.searchMonitorIntervalMinutes ?? 0) <= 0) return;
                await QbitController.SearchMonitorTick();
            }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] searchmon timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(20), DiagPeriod());
    }

    static System.TimeSpan DiagPeriod()
    {
        int m = conf?.searchMonitorIntervalMinutes ?? 0;
        return System.TimeSpan.FromMinutes(m > 0 ? System.Math.Max(15, m) : 60);
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
        AppPatch.Detach();
        EventListener.MyLocalIp -= MyIp;
        CatalogWarmup.Detach();
        _watchTimer?.Dispose();
        _watchTimer = null;
        _notifyTimer?.Dispose();
        _notifyTimer = null;
        _huntTimer?.Dispose();
        _huntTimer = null;
        _diagTimer?.Dispose();
        _diagTimer = null;
    }

    void updateConf()
    {
        conf = ModuleInvoke.Init("QbitDownload", new ModuleConf());
        // период мониторинга правится в init.conf на лету — иначе включение требовало бы рестарта
        try { _diagTimer?.Change(DiagPeriod(), DiagPeriod()); } catch { }
    }

    // ── mylocalip без api.ipify.org (qdl 2.15) ──
    // Семантика «внешний IP сервера» обязана остаться настоящей: Kodik/Alloha подписывают ссылки
    // на реальный IP (BaseController фолбэком ходил в ipify). Берём A-запись СОБСТВЕННОГО домена
    // (myIpHost) — тот же IP, DNS самолечится при его смене. Ошибка резолва → отдаём последний
    // удачный, а на самом первом сбое null → upstream-фолбэк (ipify) остаётся страховкой.
    static string _myIp;
    static System.DateTime _myIpAt;
    static async System.Threading.Tasks.Task<string> MyIp(Shared.Models.Events.EventMyLocalIp e)
    {
        string host = conf?.myIpHost;
        if (string.IsNullOrEmpty(host))
            return null;

        if (_myIp != null && (System.DateTime.UtcNow - _myIpAt).TotalHours < 12)
            return _myIp;

        try
        {
            foreach (var ip in await System.Net.Dns.GetHostAddressesAsync(host))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    _myIp = ip.ToString();
                    _myIpAt = System.DateTime.UtcNow;
                    return _myIp;
                }
            }
        }
        catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] myip dns: " + ex.Message); }

        return _myIp;
    }
}
