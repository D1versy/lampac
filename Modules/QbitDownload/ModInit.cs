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
    static System.Threading.Timer _crawlTimer;
    static System.Threading.Timer _pruneTimer;
    static System.Threading.Timer _jutTimer;      // jut.su: слежение за сезоном (раз в сутки)
    static System.Threading.Timer _jutWarmTimer;  // jut.su: прогрев кеша тайтлов (2 раза в сутки)
    static System.Threading.Timer _jutCatTimer;   // jut.su: снапшот-индекс каталога (сид/голова/ресид)
    static System.Threading.Timer _xsTimer;       // XSMART: слежение за сезоном (раз в сутки)
    static System.Threading.Timer _healthTimer;   // пассивные хелс-чеки: сброс реестра на диск
    static System.Threading.Timer _onlineWarmTimer; // прогрев кнопок «Онлайн» (три полосы, постепенно)
    static System.Threading.Timer _replicaTimer;    // цикл репликации (только при replicaRole=replica)
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

        // Наблюдения хелс-чеков (HealthState.cs) — ДО таймеров: иначе первые же исходы легли бы
        // в пустой реестр, а прочитанный следом файл затёр бы их старыми значениями.
        try { HealthState.Load(); }
        catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] health state load: " + ex); }

        EventListener.MyLocalIp += MyIp;   // внешний IP без api.ipify.org (qdl 2.15, см. MyIp ниже)
        CatalogWarmup.Attach();            // почасовой прогрев каталога главной (CatalogWarmup.cs)
        Perms.Attach();                    // реестр устройств для прав на D1versy Live/Rec (Perms.cs)

        // SQLite-хранилище уведомлений: создаём схему (без миграций) + WAL для параллельных read/write
        try
        {
            using var db = new SqlContext();
            db.Database.EnsureCreated();
            try { db.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;"); } catch { }
            // Ретенция ленты уведомлений: таблица noti росла вечно (единственным удалением был
            // ручной /clear), а каждая скачанная серия добавляла строку.
            try { QbitController.NotiPrune(conf?.notiKeepRows ?? 500); } catch { }
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

        // Обходчик индекса — самый низкоприоритетный контур: первый тик через 25 мин, после всех
        // остальных. Внутри он ещё раз уступает дорогу охоте, если та занята.
        _crawlTimer?.Dispose();
        _crawlTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if ((conf?.indexCrawlPerTick ?? 0) <= 0) return;
                await QbitController.IndexCrawlTick();
            }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] crawler timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(25), CrawlPeriod());

        // Наблюдения хелс-чеков на диск: липкий сбой обязан пережить рестарт контейнера (хост
        // падает от скачков напряжения, ИБП нет) — иначе модель врёт ровно тогда, когда нужна.
        // Тик на спокойном сервере бесплатен: без изменений FlushIfDirty выходит сразу.
        _healthTimer?.Dispose();
        _healthTimer = new System.Threading.Timer(_ =>
        {
            try { HealthState.FlushIfDirty(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] health flush: " + ex.Message); }
        }, null, System.TimeSpan.FromSeconds(30), System.TimeSpan.FromSeconds(30));

        // Ретенция индекса — раз в сутки, первый прогон через час после старта.
        _pruneTimer?.Dispose();
        _pruneTimer = new System.Threading.Timer(async _ =>
        {
            try { await QbitController.IndexPrune(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] index prune: " + ex); }
            try { QbitController.NotiPrune(conf?.notiKeepRows ?? 500); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] noti prune: " + ex); }
            // qdl 2.45: снимки поиска раздач (~80 КБ на тайтл) — без ретенции том рос бы вечно
            try
            {
                int n = SearchCache.Prune();
                if (n > 0) System.Console.WriteLine($"[QbitDownload] search cache prune: удалено {n}");
            }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] search cache prune: " + ex); }
        }, null, System.TimeSpan.FromHours(1), System.TimeSpan.FromHours(24));

        // ── jut.su: слежение за сезоном, раз в сутки (решение владельца) ──
        // Первый тик на 35-й минуте — позже всех существующих контуров
        // (notify@2 / watch@10 / hunt@15 / diag@20 / crawl@25), чтобы не толкаться на старте.
        // ⚠️ Догон обязателен: при СУТОЧНОМ такте без него каждый рестарт контейнера (дорогой —
        // Roslyn-компиляция модулей) сдвигал бы проверку на новые сутки, и при частых рестартах
        // слежение не срабатывало бы вообще.
        int jutHours = System.Math.Max(1, conf?.jutWatchIntervalHours ?? 24);
        bool jutOverdue = QbitController.JutWatchOverdue(System.TimeSpan.FromHours(jutHours), out var jutSince);
        if (jutOverdue)
            System.Console.WriteLine($"[QbitDownload] jut/watch: пропущено {jutSince.TotalHours:F1} ч — первый тик через 6 мин");

        _jutTimer?.Dispose();
        _jutTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (conf?.jutEnable != true) return;   // выключатель проверяется В НАЧАЛЕ тика
                await QbitController.JutWatchTick();
            }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jut watch timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(jutOverdue ? 6 : 35), System.TimeSpan.FromHours(jutHours));

        // ── XSMART: слежение за новыми сериями (раз в сутки, решение владельца) ──
        // Первый тик на 50-й минуте — ПОСЛЕ jut-тика (@35) и его прогрева (@45): контуры
        // независимы (разные источники), но делят диск и очередь скачивания, и толкаться
        // на старте контейнера им незачем.
        _xsTimer?.Dispose();
        _xsTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (conf?.xsmartEnable != true) return;   // выключатель проверяется В НАЧАЛЕ тика
                await QbitController.XsmartWatchTick();
            }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] xsmart watch timer: " + ex); }
        }, null, System.TimeSpan.FromMinutes(50), System.TimeSpan.FromHours(24));

        // ── jut.su: прогрев кеша тайтлов (решение владельца — 2 раза в сутки) ──
        // Промах TTL заставляет ПЕРВОГО открывшего карточку ждать полный обход: для хаб-тайтла
        // это 1 + до 24 последовательных запроса к сайту по ~1.1 с (живой замер naruuto — 3.58 с).
        // Первый прогон на 45-й минуте — ПОСЛЕ тика слежения (@35), чтобы не толкаться:
        // оба контура ходят на jut.su и делят фоновый гейт.
        int warmHours = conf?.jutWarmIntervalHours ?? 12;
        _jutWarmTimer?.Dispose();
        _jutWarmTimer = null;
        if (warmHours > 0)
        {
            _jutWarmTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    if (conf?.jutEnable != true) return;   // выключатель — В НАЧАЛЕ прогона
                    await QbitController.JutWarmTitles();
                }
                catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jut warm timer: " + ex); }
            }, null, System.TimeSpan.FromMinutes(45), System.TimeSpan.FromHours(System.Math.Max(1, warmHours)));
        }

        // ── jut.su: снапшот-индекс каталога (qdl 2.38) ──
        // Первый прогон на 50-й минуте — последним из jut-контуров (watch@35 / warm@45):
        // все трое делят ФОНОВЫЙ гейт в один слот, и разведённые старты не дают им
        // толкаться. Первый прогон после чистого тома — это сид всех ~46 страниц с паузой,
        // дальше тик стоит один запрос (страница 1).
        int catHours = conf?.jutCatalogHeadHours ?? 6;
        _jutCatTimer?.Dispose();
        _jutCatTimer = null;
        if (catHours > 0)
        {
            _jutCatTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    if (conf?.jutEnable != true) return;   // выключатель — В НАЧАЛЕ прогона
                    await QbitController.JutCatalogTick();
                }
                catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jut catalog timer: " + ex); }
            }, null, System.TimeSpan.FromMinutes(50), System.TimeSpan.FromHours(System.Math.Max(1, catHours)));
        }

        // ── Прогрев кнопок «Онлайн» (qdl 2.45) ──
        // Первый прогон на 55-й минуте — ПОСЛЕ всех jut-контуров (watch@35 / warm@45 / catalog@50),
        // чтобы старты не толкались. Внутри прогона три полосы с маленькими капами и паузой
        // onlineWarmPaceMs между карточками: греем постепенно, новинки в приоритете, хвост каталога
        // ползёт по курсору. Устройство и арифметика нагрузки — в шапке OnlineWarm.cs.
        int owHours = conf?.onlineWarmIntervalHours ?? 6;
        _onlineWarmTimer?.Dispose();
        _onlineWarmTimer = null;
        if (owHours > 0)
        {
            _onlineWarmTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    if (conf?.onlineWarmEnabled != true) return;   // выключатель — В НАЧАЛЕ прогона
                    await OnlineWarm.Tick();
                }
                catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] online warm timer: " + ex); }
            }, null, System.TimeSpan.FromMinutes(55), System.TimeSpan.FromHours(System.Math.Max(1, owHours)));
        }

        // Цикл репликации (роль "replica", Replica*.cs). На доме таймер не создаётся вовсе —
        // роль там "main"/пусто, и весь вклад реплики в домашний сервер это три GET-ручки.
        // Первый тик через 3 мин: дать подняться своему qBittorrent и прогреться JsonStore,
        // иначе первый же манифест уткнётся в «свой qBit недоступен» и потратит тик впустую.
        _replicaTimer?.Dispose();
        _replicaTimer = null;
        if (QbitController.ReplicaMode)
        {
            int rmin = System.Math.Max(1, conf?.replicaIntervalMin ?? 5);
            System.Console.WriteLine($"[QbitDownload] РОЛЬ: РЕПЛИКА. Дом: {conf?.replicaMainUrl}; бюджет {conf?.replicaBudgetGb} ГБ; "
                + $"мост {conf?.replicaBridgeMBps} МБ/с; ротация {(conf?.replicaRotateDryRun == false ? "БОЕВАЯ" : "dry-run")}; тик {rmin} мин");
            _replicaTimer = new System.Threading.Timer(async _ =>
            {
                try { await QbitController.ReplicaTick(); }
                catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] replica timer: " + ex); }
            }, null, System.TimeSpan.FromMinutes(3), System.TimeSpan.FromMinutes(rmin));
        }

        // Недокачанные .part после рестарта — добрать в очередь (очередь живёт в памяти).
        System.Threading.Tasks.Task.Run(async () =>
        {
            // Горячий слой JSON — сразу: иначе первое открытие карточки после рестарта платит
            // полный обход meta/ и local/ (замер до слоя: 0.58-1.03 с). Прогрев фоновый,
            // запросы до его конца просто идут прежним путём и сами наполняют РАМ.
            try
            {
                string cache = conf?.cachePath;
                if (!string.IsNullOrEmpty(cache))
                {
                    int n = JsonStore.Warm(System.IO.Path.Combine(cache, "meta"))
                          + JsonStore.Warm(System.IO.Path.Combine(cache, "local"));
                    System.Console.WriteLine("[QbitDownload] jsonstore: прогрето файлов — " + n);
                }
            }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jsonstore warm: " + ex); }

            try { await QbitController.JutReconcile(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jut reconcile: " + ex); }

            // XSMART: маркер по факту диска + недокачанное обратно в очередь. Контейнер
            // перезапускают часто (Roslyn-сборка модуля), и без этого прохода начатый фильм
            // лежал бы мёртвым набором сегментов до следующего ручного «Скачать».
            try { await QbitController.XsmartReconcile(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] xsmart reconcile: " + ex); }

            // Апгрейд постеров у уже скачанного: в каталоге этих тайтлов может не быть,
            // а карточка в «Загрузках» иначе навсегда останется с квадратом 186×186.
            try { QbitController.JutPosterSeedDownloads(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jut poster seed: " + ex); }

            // ⚠️ Пережатие ПЕРЕД доводом каталога, хотя работа и низкоприоритетная: это быстрый
            // разовый выигрыш (462 файла ≈ минута), а бэкфилл — длинный хвост на десятки минут,
            // потому что ждёт живую очередь к Shikimori/AniList. Обратный порядок откладывал бы
            // главный эффект до конца хвоста. Идемпотентно — зовётся на каждом старте.
            try { await QbitController.JutPosterReencodeAll(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jut poster reencode: " + ex); }

            // Довод каталога до полноты: постер высокого качества должен быть у ВСЕХ тайтлов,
            // а не только у тех страниц, которые кто-то открыл. Идемпотентно — готовые
            // отсеиваются по файлу и решению, без единого запроса.
            try { await QbitController.JutPosterBackfillAll(); }
            catch (System.Exception ex) { System.Console.WriteLine("[QbitDownload] jut poster backfill: " + ex); }
        });
    }

    static System.TimeSpan CrawlPeriod()
    {
        int m = conf?.indexCrawlIntervalMinutes ?? 0;
        return System.TimeSpan.FromMinutes(m > 0 ? System.Math.Max(15, m) : 60);
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
        Perms.Detach();
        _watchTimer?.Dispose();
        _watchTimer = null;
        _notifyTimer?.Dispose();
        _notifyTimer = null;
        _huntTimer?.Dispose();
        _huntTimer = null;
        _diagTimer?.Dispose();
        _diagTimer = null;
        _crawlTimer?.Dispose();
        _crawlTimer = null;
        _pruneTimer?.Dispose();
        _pruneTimer = null;
        _replicaTimer?.Dispose();
        _replicaTimer = null;
        _jutTimer?.Dispose();
        _jutTimer = null;
        _xsTimer?.Dispose();
        _xsTimer = null;
        _jutWarmTimer?.Dispose();
        _jutWarmTimer = null;
        _jutCatTimer?.Dispose();
        _jutCatTimer = null;
        _healthTimer?.Dispose();
        _healthTimer = null;
        _onlineWarmTimer?.Dispose();
        _onlineWarmTimer = null;
        try { HealthState.FlushIfDirty(); } catch { }
        // Грязное из горячего слоя обязано доехать до диска: иначе выгрузка модуля
        // теряет ещё не записанные маркер/activity (окно дебаунса — 200 мс).
        try { JsonStore.Flush(); } catch { }
    }

    void updateConf()
    {
        string prevUa = JutNet.Ua;
        string prevCache = conf?.cachePath;

        conf = ModuleInvoke.Init("QbitDownload", new ModuleConf());
        // период мониторинга правится в init.conf на лету — иначе включение требовало бы рестарта
        try { _diagTimer?.Change(DiagPeriod(), DiagPeriod()); } catch { }
        try { _crawlTimer?.Change(CrawlPeriod(), CrawlPeriod()); } catch { }

        // ── горячий слой JSON ──
        // Смена cachePath обесценивает весь РАМ-кеш (ключ = путь к файлу). Сбрасываем и кеш
        // собранного ответа /qdl/list. ⚠️ ResetForConfigReload сначала доводит грязное до диска.
        try
        {
            if (!string.Equals(prevCache, conf?.cachePath, System.StringComparison.Ordinal))
            {
                JsonStore.ResetForConfigReload();
                // Снапшот каталога живёт файлом внутри cachePath — РАМ-копия относится
                // к прежнему пути и обязана быть забыта (перечитается лениво с нового).
                QbitController.JutIdxReset();
                Perms.ResetForConfigReload();
            }
            QbitController.DropListCache();
        }
        catch { }

        // ── jut.su ──
        // Вердикты прокси-фолбэка живут в статике (conf пересоздаётся целиком), иначе
        // «прокси мёртв» залипал бы до конца кулдауна даже после правки init.conf.
        try { JutProxyFallback.Reset(); } catch { }
        // 🔥 Смена UA обесценивает ВСЕ выданные ссылки: hash в CDN-URL криптографически
        // связан с UA, которым была запрошена страница. Не сбросить = массовые 403.
        try { if (!string.Equals(prevUa, JutNet.Ua, System.StringComparison.Ordinal)) JutNet.ResetLinks(); } catch { }
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
