using Shared.Models.Module;
using System.Collections.Generic;

namespace QbitDownload;

public class ModuleConf : ModuleBaseConf
{
    public bool enable { get; set; } = true;

    // ── D1Vision: бренд и OTA-список хостов для клиентских оболочек (mac/ios/android/tizen) ──
    // Отдаются клиентам через GET /d1vision/hosts.json. Клиенты кэшируют список нативно и
    // ДОБАВЛЯЮТ его к зашитому bootstrap-списку (никогда не заменяют — защита от «окирпичивания»).
    // Боевые значения — в init.conf (секция QbitDownload), перечитывается на лету.
    // ⚠️ clientHosts БЕЗ дефолта-инициализатора: ModuleInvoke.Init популейтит JSON поверх готового
    // инстанса, и Json.NET ДОПОЛНЯЕТ уже заполненную коллекцию (получались дубли хостов).
    // Дефолтный список живёт в Controller.cs (defaultClientHosts).
    // Канонический документ: E:\Media-server\claude\08-clients.md.
    public string brand { get; set; } = "D1Vision";
    public List<string> clientHosts { get; set; }

    // Витрина расширений CUB локально (qdl 2.17, CubExtensions.cs): смонтированный том с копией
    // тем (CSS), скринсейверов (MP4), превью и JS плагинов + локальный list.json с premium=0.
    // Наполняется scripts/vendor-cub-extensions.ps1; пусто/нет файла → редирект на upstream-cub.
    public string cubExtPath { get; set; } = "/lampac/wwwroot/cubext";

    // Каталог с бинарными билдами клиентов для самообновления (OTA app updates): смонтированный
    // том, лежат manifest.json/appcast.xml + сами APK/DMG. Отдаётся через GET /d1vision/apps/<platform>/<file>.
    // Билды кладёт publish-скрипт (Android) / make-dmg.sh (Mac). См. E:\Media-server\claude\08-clients.md.
    public string clientBuildsPath { get; set; } = "/client-builds";

    // qBittorrent WebUI
    public string qbitHost { get; set; } = "http://qbittorrent:8080";
    public string qbitUser { get; set; } = "admin";
    public string qbitPass { get; set; } = "admin";

    // Папка загрузок, которую видят И qBittorrent, И этот контейнер (общий mount).
    public string downloadsPath { get; set; } = "/downloads";

    // Категория qBittorrent для фильтрации загрузок из Lampa.
    public string category { get; set; } = "lampa";

    public int timeoutSeconds { get; set; } = 20;

    // Локальный кэш метаданных/постеров загрузок (том на SSD, rw). Только картинки+JSON, не видео.
    public string cachePath { get; set; } = "/qdl-data";

    // Зеркало горячего JSON-слоя на диск E: (bind ./.qdl-cache/hot → /qdl-hot).
    // Решение владельца: «на диск E пишется как место, откуда тянуть кеш».
    // 🔴 Это ЗЕРКАЛО, не хранилище: meta/local — единственная запись о том, что скачано,
    // и держать её authoritative на drvfs означало бы поставить состав «Загрузок»
    // в зависимость от файловых локов bind-маунта. Источник истины — том на ext4.
    // null/пусто = зеркало выключено (киллсвитч на лету).
    public string hotMirrorPath { get; set; } = "/qdl-hot";

    // Кеш собранного ответа /qdl/list. Ручка зовётся на КАЖДОЕ открытие любой карточки,
    // и до кеша стоила 0.58-1.03 с (вчетвером — 1.2 с). 0 = выключить (киллсвитч на лету).
    // Отставание прогресса на TTL согласовано с владельцем: «несколько минут — не страшно».
    public int listCacheSeconds { get; set; } = 30;

    // HLS-транскод для браузеров (звук EAC3/AC3/DTS → AAC, видео copy). Кэш на HDD (дублирует видео).
    public string hlsPath { get; set; } = "/qdl-hls";
    public string ffmpeg { get; set; } = "/lampac/data/ffmpeg";
    public string ffprobe { get; set; } = "/lampac/data/ffprobe";
    public long hlsCacheCapGb { get; set; } = 30;

    // Быстрая перемотка HLS: виртуальный VOD-плейлист + перезапуск ffmpeg с -ss в точку seek.
    // false = старое линейное поведение (event-плейлист, транскод строго с начала).
    public bool hlsSeek { get; set; } = true;

    // Нативный GPU ffmpeg-воркер на Windows-хосте (NVENC на RTX 3090 Ti) — служба ffmpeg-worker.
    // NVENC в Docker/WSL2 не работает, поэтому тяжёлый транскод уходит на хост; данные через общие
    // тома, по HTTP только управление. Пусто = выключено, всё работает на CPU как раньше.
    public string ffworkerUrl { get; set; } = "";        // "http://host.docker.internal:9119"
    public string ffworkerToken { get; set; } = "";
    public int ffworkerTimeoutMs { get; set; } = 3000;

    // «Мобильный» HLS-профиль (ключ с суффиксом _m): live-даунскейл + кап битрейта для телефона
    // на сотовой сети (iOS-клиент строит _m-ключ при cellular, см. claude/08 репо-оркестрации).
    // Всегда реэнкод (включая h264/hevc — NVDEC декодит), уходит на GPU-воркер, CPU-фолбэк libx264.
    // Этот блок закрывает и прежний задел ffworkerHlsHevc (live-GPU-HLS для HEVC).
    public bool hlsMobile { get; set; } = true;              // false → 302 с _m-ключей на обычные (деградация к оригиналу)
    public int hlsMobileHeight { get; set; } = 720;          // -vf scale до высоты (SD не апскейлится)
    public int hlsMobileCq { get; set; } = 28;               // NVENC -cq (обычный HLS-реэнкод — 23)
    public int hlsMobileCrf { get; set; } = 25;              // CPU-фолбэк libx264 -crf (обычный — 21)
    public int hlsMobileMaxrateKbps { get; set; } = 2500;    // -maxrate; -bufsize = 2×maxrate
    public int hlsMobileAudioKbps { get; set; } = 128;       // звук AAC (обычный HLS — 256k)

    // Глушить live-HLS-транскод, если зритель перестал запрашивать сегменты (закрыл приложение,
    // длинная пауза): иначе ffmpeg молотит до конца файла впустую (для _m — весь фильм на GPU).
    // Возврат зрителя = штатный VOD-рестарт с -ss (~3-5 с). 0 = не глушить (старое поведение).
    public int hlsIdleKillSec { get; set; } = 180;

    // ── Прогрев кеша (/qdl/warmup): при открытии карточки скачанного клиент просит сервер заранее
    //    прочитать голову+хвост файла — байты оседают в page cache WSL-VM (9p cache=loose), и старт
    //    плейбека идёт из RAM, а не с холодного HDD через 9p. Заодно греется ffprobe-кеш дорожек. ──
    public bool warmupEnabled { get; set; } = true;
    public int warmupHeadMb { get; set; } = 64;    // голова файла (первые секунды видео + заголовки контейнера)
    public int warmupTailMb { get; set; } = 8;     // хвост (moov у mp4, Cues у mkv — плеер читает их до старта)
    public int warmupTtlMin { get; set; } = 15;    // дедуп «уже прогрет» по пути файла
    public int warmupPaceMs { get; set; } = 0;     // пауза между 1-МБ чанками (мс); >0 — щадить HDD при конкуренции

    // Слежение за сериалами: как часто проверять обновление раздач (часы).
    public int watchIntervalHours { get; set; } = 6;

    // Уведомления о скачанных сериях: как часто сканировать прогресс файлов отслеживаемых раздач (минуты).
    // Дёшево (локальные запросы к qBit, без пере-резолва трекеров) → можно часто.
    public int notifyScanIntervalMinutes { get; set; } = 15;

    // Сколько последних уведомлений отдавать клиенту (/qdl/notifications). Раньше было зашито 200,
    // а лента рисуется целиком — при скачивании аниме центр уведомлений превращался в простыню.
    // Клампится 1..500.
    public int notiFeedLimit { get; set; } = 50;

    // Ретенция таблицы noti: держим последние N строк, остальное удаляет суточный _pruneTimer.
    // ⚠️ Таблицу seen прун НЕ трогает — на ней держится дедуп «уже уведомляли», её чистка
    // вызвала бы залп повторных уведомлений. 0 = не прунить (киллсвитч).
    public int notiKeepRows { get; set; } = 500;

    // ── Умная выдача /qdl/search (TorrentScoring) ──
    public bool searchScoring { get; set; } = true;        // false = старая сортировка только по сидам
    public int preferredQuality { get; set; } = 2160;      // согласовано с video_quality_default клиента
    public int recommendMinSeeds { get; set; } = 5;        // гейт «⭐ рекомендуемая»
    public string indexerApikey { get; set; } = "";        // apikey JacRed для ФОНОВЫХ поисков (охота/переключение); пусто = без ключа

    // ── Свой индекс раздач (LocalIndex.cs, Postgres) ──
    // Копит всё, что видели ВСЕ источники, в своей БД: страховка от смерти чужого удалённого
    // индекса и мгновенный локальный поиск. Пусто = источник выключен целиком.
    public string localIndexConnection { get; set; } = "";
    public int localIndexLimit { get; set; } = 200;        // сколько раздач тянуть на тайтл
    public int localIndexTimeoutSec { get; set; } = 5;
    public int localIndexPruneDays { get; set; } = 180;    // не виденные N дней удаляются (0 = не чистить)
    public bool localIndexVerbose { get; set; } = false;   // лог каждой записи (для обкатки)

    // Обходчик индекса (IndexCrawler.cs): наполняет индекс, не дожидаясь ручных поисков.
    // ⚠️ Каждый тайтл = запрос ко ВСЕМ трекерам. Бан по IP на rutor/nnmclub/kinozal хуже,
    // чем медленное наполнение, поэтому бюджет маленький и с паузой. 0 = обходчик выключен.
    public int indexCrawlPerTick { get; set; } = 5;
    public int indexCrawlIntervalMinutes { get; set; } = 60;   // кламп ≥15
    public int indexCrawlPauseSec { get; set; } = 20;          // пауза между тайтлами, кламп ≥5

    // ── Язык аудиодорожки на экране серий (qdl 2.24) ──
    // false → сервер не отдаёт lang2/langName, и клиент ведёт себя ровно как до фичи.
    public bool audioLangEnable { get; set; } = true;

    // ── Мониторинг поиска (SearchMonitor.cs): канарейки по индексатору, трекерам и ⭐ ──
    // Поломка «Раздачи не найдены» жила незамеченной, пока владелец не увидел её глазами.
    public int searchMonitorIntervalMinutes { get; set; } = 180;   // 0 = выключено; кламп ≥15
    public bool searchMonitorNotify { get; set; } = false;         // false = только лог (обкатка без спама)
    public bool searchMonitorTrackers { get; set; } = true;        // следить за пропажей отдельных трекеров
    public int searchMonitorMinResults { get; set; } = 10;         // абсолютный пол по числу раздач
    public int searchMonitorDropPercent { get; set; } = 60;        // и падение ниже % от медианы
    public int searchMonitorFailStreak { get; set; } = 3;          // сколько провалов подряд = тревога
    public int searchMonitorBaselineRuns { get; set; } = 10;       // глубина истории для медианы
    public int searchMonitorCooldownHours { get; set; } = 12;      // антидребезг поверх переходов
    // ⚠️ Без инициализатора: ModuleInvoke.Init популейтит JSON поверх готового инстанса, и Json.NET
    // ДОПОЛНЯЕТ заполненную коллекцию → дубли. Дефолт живёт в SearchMonitor._defaultCanaries.
    public List<string> searchMonitorTitles { get; set; }

    // ── Хелс-чеки внешних сервисов (Health.cs, экран «Хелс-чеки» в настройках) ──
    // Пустая строка = сервис показывается как «выключен» (⏸), а не как сбой.
    // Адреса FlareSolverr и JacRed webapi ЗАДУБЛИРОВАНЫ здесь намеренно: их конфиг живёт
    // в модуле JacRed, а модули компилируются Roslyn'ом в отдельные сборки и не видят друг
    // друга. Дефолты совпадают с боевыми (docker-compose + JacRed/ModInit) — если поменяете
    // адрес там, поправьте и здесь, иначе хелс-чек будет врать про живой сервис.
    public int healthCacheSeconds { get; set; } = 30;      // общий кеш ответа /qdl/health
    public string healthFlaresolverrUrl { get; set; } = "http://flaresolverr:8191";
    public string healthJacredWebApi { get; set; } = "http://ns3bg91xvuqfvq9h.cfhttp.top";

    // ── bitmagnet: локальный индекс DHT-краулера (Postgres на хосте) ──
    // Ищем ТОЛЬКО по TMDB id карточки, а не по названию: совпадение точное, поэтому чужой фильм
    // притащить нельзя в принципе. Свободный текстовый поиск сознательно не делаем — в базе
    // ~11.7 млн раздач без метаданных и ~625 тыс. xxx, вот это и был бы мусор.
    public string bitmagnetConnection { get; set; } = "";  // пусто = источник выключен
    public int bitmagnetLimit { get; set; } = 100;         // сколько раздач тянуть на тайтл
    public int bitmagnetTimeoutSec { get; set; } = 8;
    // Краулер обновляет сиды только когда работает. Обнулять их у подтухшего среза нельзя —
    // источник тогда вообще не участвует в ранжировании; но и верить архивным цифрам наравне
    // с живыми трекерными тоже нельзя. Старше N дней → половина веса (0 = не резать).
    public int bitmagnetStaleSeedsDays { get; set; } = 3;

    // ── Охота за сериями по всем раздачам (EpisodeHunter): новая серия на ЛЮБОЙ раздаче →
    //    докачиваем её файл с лучшего «донора», при догоне основной — замещаем её версией ──
    public bool episodeHunt { get; set; } = true;          // фича в целом (слежение и так opt-in per сериал)
    public int episodeHuntIntervalHours { get; set; } = 4; // интервал HuntAll (кламп ≥1: не долбим трекеры)
    public int donorMinSeeds { get; set; } = 3;
    public int donorMinQuality { get; set; } = 1080;       // ниже — ждём основную (0 = любое)
    public int epSizeMinMb { get; set; } = 150;            // оценка веса ОДНОЙ серии: не обрезок…
    public int epSizeMaxGb { get; set; } = 8;              // …и не ремукс по 40 ГБ
    public int donorMaxPerSeries { get; set; } = 3;
    public int donorProbesPerRun { get; set; } = 3;        // проб add-paused за проход на сериал
    public int donorMetadataTimeoutSec { get; set; } = 90;
    public int donorStaleDays { get; set; } = 7;           // донор не докачал за N дней → снять и в blacklist
    public int donorBlacklistTtlDays { get; set; } = 30;   // пустышки (нет серии); транзиенты (сеть/qBit) — бэкофф от 30 мин
    public string donorCategory { get; set; } = "";        // пусто → category + "-donor"
    // Апгрейд уже добытой донорской серии на раздачу получше (⭐/выше качество/выше скор).
    // Серия из основной раздачи НИКОГДА не апгрейдится донором — основная всегда приоритетнее.
    public bool donorUpgrade { get; set; } = true;
    public int donorUpgradeMinScore { get; set; } = 15;    // насколько кандидат должен обойти текущего донора
    public bool tmdbAiredCap { get; set; } = true;         // не охотиться за сериями, которые ещё не вышли

    // ── D1VERSY LIVE: записи домашнего видеорегистратора (проект IPCamLive) ──
    // Регистратор доступен только из LAN, клиенты ходят к нему через наш прокси /qdl/live/*
    // (см. Live.cs). Адрес наружу не отдаётся.
    public string liveUrl { get; set; } = "http://192.168.87.24";
    public string liveTimezone { get; set; } = "";   // IANA-зона для показа времени; пусто = TZ контейнера
    public int liveDaysBack { get; set; } = 14;      // как далеко назад предлагать дни в выборе даты

    // ── Заброшенная основная раздача: предложение переключиться на более полную ──
    public string watchAutoSwitch { get; set; } = "notify"; // off | notify (уведомление+подтверждение) | auto
    public int watchStaleChecks { get; set; } = 8;          // проверок без смены infohash (~2 суток при 6ч)
    public int watchSwitchCooldownDays { get; set; } = 7;   // не чаще одного переключения в неделю
    public bool switchDeleteOldFiles { get; set; } = false; // другой рип = другие файлы; старые по умолчанию оставляем

    // ── Локализация внешних источников (qdl 2.15, карта — E:\Media-server\claude\11) ──
    // mylocalip без api.ipify.org: внешний IP = A-запись СОБСТВЕННОГО домена (DNS самолечится при
    // смене IP; Kodik/Alloha подписывают ссылки на реальный внешний IP — LAN-адрес сюда нельзя).
    // Пусто = хук выключен, upstream-фолбэк на ipify остаётся.
    public string myIpHost { get; set; } = "tv.d1versy.com";

    // Прогрев каталога главной (CatalogWarmup.cs): запоминаем URL РЯДОВ /cub/tmdb.* и периодически
    // дёргаем их локально — протухшую запись Staticache (TTL CubProxy 3 ч) обновляет наш тик, а не
    // живой клиент. v2 (qdl 2.16): из ответов рядов достаём карточки и греем ещё постеры (/tmdb/img)
    // и детали (/cub/tmdb./3/...) — открытие карточки всегда в тёплый кеш. HIT-проверки бесплатны.
    public bool catalogWarmupEnabled { get; set; } = true;
    public int catalogWarmupPeriodMin { get; set; } = 15;    // кламп ≥5; наружу всё равно ≈раз в TTL на URL
    public int catalogWarmupMaxUrls { get; set; } = 128;     // LRU-кап РЯДОВ (ряды по годам/жанрам легко >64)
    public int catalogWarmupPruneDays { get; set; } = 14;    // ряд не запрашивался клиентами N дней → забыть
    public int catalogWarmupCardsPerRow { get; set; } = 12;  // сколько первых карточек ряда греть (видимая часть на ТВ)
    public int catalogWarmupPosterBudget { get; set; } = 120; // постеров за тик (ротация курсора добирает хвост)
    public int catalogWarmupDetailBudget { get; set; } = 32;  // деталей за тик (каждый MISS = поход в api.themoviedb.org)
    // Фон карточки (backdrop_path, w1280) весит 130-280 КБ против 20-40 КБ у постера w300, поэтому
    // отдельный, заведомо меньший бюджет вместо общего счётчика штук: 24 фона ≈ 120 постеров по байтам.
    public int catalogWarmupBackdropsPerRow { get; set; } = 3; // сколько первых карточек ряда греть фоном
    public int catalogWarmupBackdropBudget { get; set; } = 24; // фонов за тик (ротация курсора добирает хвост)

    // ─────────────────────────────────────────────────────────────────────────
    // jut.su — вкладка аниме (JutSu*.cs).
    // Разведка сайта и рунбук: E:\Media-server\claude\jut\
    // ─────────────────────────────────────────────────────────────────────────
    public bool jutEnable { get; set; } = true;
    public string jutHost { get; set; } = "https://jut.su";

    // 🔥 Куки решают ВСЁ: без них jut.su подменяет ссылки на gen.jut.su/.../pixel.png —
    // заглушку БЕЗ текста ошибки, при целой разметке (label/res на месте). Нужна ровно пара
    // dle_user_id + dle_password (PHPSESSID и LB_member_sc не влияют ни на что).
    // ⚠️ Значения — ТОЛЬКО в init.conf (D:\docker\config\lampac, вне git). Сюда не коммитить.
    public string jutUserId { get; set; } = "";
    public string jutPassword { get; set; } = "";

    // 🔥 ОДНА константа UA на весь пайплайн (HTML-страница И байты у CDN): hash в CDN-ссылке
    // криптографически связан с UA, которым запрошена страница. Плеер с чужим UA получит 403 —
    // поэтому ссылку нельзя отдавать клиенту, только проксировать. Смена значения немедленно
    // инвалидирует все выданные ссылки. Пусто → JutNet.DefaultUa.
    public string jutUserAgent { get; set; } = "";

    // Прокси: по умолчанию ходим со СВОЕГО IP, а фолбэк вооружён и подхватывает сам, если
    // прямой путь отвалился (reached=false). Полная матрица режимов — claude/jut/02-architecture.md.
    // Обе ручки трёхзначные (bool?): надо отличать «не задано» от «явно выключено» (§BB.4).
    public JutProxyConf jutProxy { get; set; } = new JutProxyConf();
    public bool? jutProxyFallbackEnable { get; set; }            // null = true (вооружён)
    public bool? jutProxyFallbackDirect { get; set; }            // при useproxy:true; false = киллсвич «только прокси»
    public int jutProxyFallbackCooldownSeconds { get; set; } = 300;  // кламп ≥30

    // Кеши (свои, не Staticache: его ключ = path+query, без queryKeys разные ?page= слиплись бы — §AN)
    public int jutCatalogTtlMin { get; set; } = 30;
    public int jutTitleTtlHours { get; set; } = 12;
    public int jutOngoingTtlHours { get; set; } = 2;   // онгоингу нужен более свежий список серий
    public int jutSearchTtlMin { get; set; } = 10;
    public int jutLinkTtlSec { get; set; } = 240;      // реальный TTL токена ≥9 мин — берём запас ×2
    public int jutTimeoutSec { get; set; } = 20;       // только для API-запросов; медиа — без общего таймаута
    public int jutMaxConcurrent { get; set; } = 3;     // гейт запросов к jut.su
    public int jutStreamConcurrent { get; set; } = 6;  // гейт на СТАРТ стрима (отпускается после заголовков)

    // 0 = всегда максимум из фактически доступных (требование владельца).
    // Число (1080/720/...) — ПОТОЛОК: берём наибольшее не выше него.
    public int jutPreferredQuality { get; set; } = 0;
    public bool jutForceHls { get; set; } = false;     // аварийный: гнать через /qdl/hls (если аудио не AAC)

    // Скачивание. ⚠️ /downloads смонтирован :ro — нужен отдельный rw-бинд на /downloads/jutsu.
    public string jutDownloadsPath { get; set; } = "/downloads/jutsu";
    // ⚠️ Ключа jutDownloadConcurrency здесь НЕТ намеренно: он существовал, но никогда не читался —
    // «один файл за раз» зашито флагом _jutWorker в JutKickWorker (щадим и CDN, и шпиндель).
    // Мёртвая ручка в конфиге хуже её отсутствия: её крутят и ждут эффекта.
    public int jutGrabRetries { get; set; } = 5;
    // Коалесинг WS-пуша «серия скачана». Строки в noti пишутся на каждую серию — троттлится
    // только сигнал клиентам: иначе скачивание аниме даёт по тосту и полному опросу ленты
    // на КАЖДУЮ серию, на каждом устройстве. Последняя серия пачки пушится немедленно.
    // 0 = выключить коалесинг (прежнее поведение, киллсвитч на лету).
    public int jutNotifyCoalesceSec { get; set; } = 300;
    // Одно уведомление на пачку вместо строки на каждую серию (жалоба владельца: тайтл на
    // 60 серий = 60 записей в ленте). Пачкой считается постановка >1 серии и любой добор к уже
    // качающейся. Одиночная серия (в т.ч. вышедшая новая у подписки) уведомляет как раньше.
    // false = прежнее поведение (киллсвитч; латчится на пачку, начатая доживает по своему режиму).
    public bool jutNotifyAggregate { get; set; } = true;
    // Гейт ФОНОВЫХ запросов к jut.su (воркер скачивания, тик слежения, постеры, прогрев).
    // Отдельный от jutMaxConcurrent: раньше один гейт на 3 слота обслуживал и качалку,
    // и карточки, и плеер — качалка отбирала слоты, и карточка вставала в очередь за ней
    // (один запрос к сайту ≈ 1.1 с). Интерактивные запросы теперь не ждут фоновых.
    public int jutBgConcurrent { get; set; } = 1;
    // Idle-таймаут чтения тела при скачивании. ⚠️ НЕ общий таймаут: у медиа-клиента
    // Timeout.InfiniteTimeSpan обязателен (иначе 489-МиБ файл рвётся), нужен именно
    // «данные не идут N секунд». Без него зависшее соединение к CDN держало ЕДИНСТВЕННЫЙ
    // воркер бесконечно и вся очередь вставала. 0 = выключить.
    public int jutGrabIdleSec { get; set; } = 60;

    // ── Прогрев кеша тайтлов (решение владельца: «джоба, пусть обновляется 2 раза в сутки») ──
    // Промах TTL заставляет ПЕРВОГО открывшего карточку ждать полный обход: для хаб-тайтла
    // это 1 + до 24 последовательных запроса по ~1.1 с (живой замер naruuto — 3.58 с).
    // Греем скачанное + подписки + недавно открытое. 0 часов = выключить джобу.
    public int jutWarmIntervalHours { get; set; } = 12;
    public int jutWarmTitlesPerRun { get; set; } = 40;
    public int jutWarmRecentDays { get; set; } = 7;
    public int jutWarmPaceMs { get; set; } = 1500;   // щадим сайт и конечный бюджет прокси-выходов

    // ── Снапшот-индекс каталога (JutSuCatalogIndex.cs, qdl 2.38) ──
    // Витрина order-by-add меняется только сверху (1-2 новинки в день), а листали её через
    // пер-страничный кеш с TTL 30 мин — то есть за каждую страницу снова платили ~1.1 с.
    // Держим ОДИН упорядоченный список карточек и режем страницы из него: новинки просто
    // встают в голову, шва между страницами нет (иначе сдвиг ленты теряет тайтлы).
    // false = прежний путь через пер-страничный кеш (киллсвитч на лету).
    public bool jutCatalogIndex { get; set; } = true;
    public int jutCatalogSeedPaceMs { get; set; } = 3000;   // пауза между страницами сида
    public int jutCatalogSeedMaxPages { get; set; } = 60;   // предохранитель: на сайте ~46 страниц
    public int jutCatalogHeadHours { get; set; } = 6;       // период тика (сид/голова/ресид); 0 = таймер не создавать
    public int jutCatalogHeadMaxPages { get; set; } = 5;    // насколько глубоко искать знакомый слаг
    // Полный пересбор в теневой буфер — ЕДИНСТВЕННЫЙ способ вычистить удалённые с сайта тайтлы
    // (голова про них не узнает). 0 = не пересобирать (ручной пересид всё равно работает).
    public int jutCatalogReseedDays { get; set; } = 30;
    public int jutGrabPaceMs { get; set; } = 0;            // мягкий кап скорости (>0 = пауза между чанками)
    public int jutMinFreeGb { get; set; } = 20;            // серия 1080p ≈ 489 МиБ, сезон ≈ 12 ГБ

    // ── Апгрейд постеров (JutSuPoster.cs + JutSuMatch.cs) ──
    // jut.su отдаёт КВАДРАТ 186×186 (10–25 КБ), а сетка Lampa ждёт портрет 2:3 — отсюда мыло.
    // Большего варианта на сайте нет: карточка каталога и страница тайтла ссылаются на один файл.
    // Цепочка: романдзи с jut → Shikimori (поиск; его id == MAL id) → AniList ПО idMal (без
    // угадывания) → coverImage.extraLarge 460×690.
    // 🔥 AniList нельзя использовать матчером: у него своё написание («SPY×FAMILY», «ONE PIECE»).
    // 🔥 false работает как ПОЛНЫЙ откат: апгрейженные постеры перестают отдаваться сразу же,
    //    файлы остаются на диске, обратное включение мгновенно.
    public bool jutPosterUpgrade { get; set; } = true;
    public bool jutBackdrop { get; set; } = true;          // фон экрана тайтла (2560×1440 с самого jut.su)
    public int jutPosterPaceMs { get; set; } = 350;        // ≈3 rps при лимите Shikimori 5 rps / 90 rpm
    public int jutPosterRetryDays { get; set; } = 14;      // ⚠️ отказ обязан протухать: новинки приезжают в базы позже
    // Санити картинки: портрет и не миниатюра. ⚠️ Не поднимать выше 225 — запасной источник
    // (Shikimori, 225–240 px) тогда перестаёт проходить, и лестница фолбэка становится мёртвой.
    public int jutPosterMinWidth { get; set; } = 200;
    public string jutShikimoriHost { get; set; } = "https://shikimori.io";   // ⚠️ .one отвечает 301 → .io
    public string jutAniListUrl { get; set; } = "https://graphql.anilist.co";
    public int jutAniListPaceMs { get; set; } = 900;       // ⚠️ лимит AniList 90/мин, в деградации 30 — темп обязателен

    // Слежение — ТОЛЬКО по jut.su. В торренты за этими сериями не ходим (три пояса изоляции,
    // см. JutSuWatch.cs и claude/jut/02-architecture.md §9).
    public int jutWatchIntervalHours { get; set; } = 24;   // кламп ≥1
    public int jutWatchTitlesPerTick { get; set; } = 30;   // бюджет ПОЛНЫХ опросов страниц тайтлов
    // Дефолт ТОЛЬКО для подписок без явного режима (curl, старый закешированный клиент):
    // UI передаёт режим сам — карточка тайтла autoGrab=0, «Загрузки» autoGrab=1.
    // Уже созданные подписки этот флаг НЕ переопределяет (см. JutAutoGrabFor).
    public bool jutWatchAutoGrab { get; set; } = true;     // новая серия сезона → сразу в очередь скачивания
    public bool jutWatchSeasonSwitch { get; set; } = true; // вышел новый сезон → переключить слежение на него
}

// Прокси-настройки jut.su для ProxyManager("jutsu", ...).
// Отдельный класс, потому что ProxyManager принимает Iproxy, а не голый список.
public class JutProxyConf : Shared.Models.Base.Iproxy
{
    public bool useproxy { get; set; } = false;        // дефолт: свой IP; прокси — только фолбэк
    public bool useproxystream { get; set; } = false;
    public string globalnameproxy { get; set; }
    public Shared.Models.Base.ProxySettings proxy { get; set; } = new Shared.Models.Base.ProxySettings();
}
