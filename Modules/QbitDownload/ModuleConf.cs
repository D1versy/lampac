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
    public int donorBlacklistTtlDays { get; set; } = 30;   // пустышки (нет серии); meta-timeout — всегда 1 день
    public string donorCategory { get; set; } = "";        // пусто → category + "-donor"

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
}
