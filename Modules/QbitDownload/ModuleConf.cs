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

    // Сезоны одного сериала — одной карточкой «Загрузок» (qdl 2.78, SeriesMerge.cs).
    // Группа — раздачи с ОДНИМ TMDB id сериала (jut.su/XSMART не участвуют: у них свой контур
    // подписки и один маркер на тайтл). false = киллсвитч на лету, карточки снова разъедутся.
    public bool mergeSeasons { get; set; } = true;

    // Кеш собранного ответа /qdl/list. Ручка зовётся на КАЖДОЕ открытие любой карточки,
    // и до кеша стоила 0.58-1.03 с (вчетвером — 1.2 с). 0 = выключить (киллсвитч на лету).
    // Отставание прогресса на TTL согласовано с владельцем: «несколько минут — не страшно».
    public int listCacheSeconds { get; set; } = 30;

    // Восстановление карточки загрузки по infohash (MetaHeal.cs): безымянная карточка в «Загрузках»
    // чинится в фоне через точную привязку btih → tmdb_id (наш индекс, затем bitmagnet).
    // false = киллсвитч на лету (карточку тогда пишет только клиент при «Скачать»).
    public bool metaHealEnabled { get; set; } = true;

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

    // Не уведомлять о донорской серии, которой ещё не было в эфире по TMDB (§BS). Пропуск не пишет
    // ни noti, ни seen, поэтому лаг TMDB стоит одного тика, а не потерянной серии. false = киллсвитч
    // (и он же выключает поход в TMDB из тестов сканера).
    public bool notifyAiredCap { get; set; } = true;

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

    // ── Санитайз магнетов (SanitizeMagnet в Controller.cs) ──
    // Веб-сиды (ws/as/xs), адреса пиров (x.pe) и прочие URL-параметры режутся ВСЕГДА и
    // безусловно: они заставляют qBittorrent ходить по чужому адресу, а пользы для поиска
    // пиров не дают. Ручка ниже — только про tr (адреса анонса), у которых польза реальная.
    //
    // true → выбрасывать и tr. Тогда раздача ищется через DHT/PeX/LSD (они включены).
    // ⚠️ По умолчанию false: у ПРИВАТНЫХ раздач DHT отключён флагом внутри торрента, и без tr
    // такая ссылка не скачает даже метаданные. Сломать закачку хуже, чем оставить анонс.
    // Прежде чем включать, посмотрите на /qdl/diag/state → magnetAnnounce: там видно, какие
    // анонс-хосты реально встречаются в магнетах и сколько раз.
    public bool sanitizeMagnetTrackers { get; set; } = false;

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

    // Окно «флапа» (qdl 2.44): последняя операция удалась, но в окне были ошибки → ⚠️, а не ✅.
    public int healthFlapWindowMinutes { get; set; } = 60;

    // Сколько 4xx за ОДИН тик прогрева считать сбоем СЕРВИСА (qdl 2.65). Одиночный 4xx здоровье
    // не красит — это свойство адреса (CatalogWarmup.ClassifyHealth). Но если за тик по сервису
    // не было ни одного успеха и ни одного 5xx, а 4xx набралось столько — значит отказывают ВСЕ
    // запросы (отозванный api_key, бан по IP), и это уже авария. Антидот к fail-open карантина.
    public int healthAllFailMinSamples { get; set; } = 3;

    // Сколько % от searchMonitorIntervalMinutes можно прожить без прогона, прежде чем вердикты
    // канареек считать устаревшими. 250, а не 200: тик штатно пропускается на разогреве после
    // старта и при занятом _watchGate — один законный пропуск не должен красить экран.
    public int healthMonitorStalePercent { get; set; } = 250;

    // ⚠️ Устарело с qdl 2.44 и больше не читается: чужой индекс проверялся пробой голого корня,
    // которая ничего не доказывала. Боевой путь идёт через наш локальный индексатор и наблюдается
    // пассивно (строка «Индексатор»). Ключ оставлен, чтобы не ломать существующий init.conf.
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

    // ── «Жду следующий сезон» (SeasonWatch.cs, qdl 2.79): подписка на СЕРИАЛ, а не на раздачу.
    //    Слежение выше умеет только то, что уже лежит на диске; здесь ждём выхода сезона N+1.
    //    Контур ТОЛЬКО ДОБАВЛЯЕТ раздачи — удалять он не умеет по построению.
    public bool seasonWatch { get; set; } = true;          // киллсвитч на лету
    public int seasonWatchIntervalHours { get; set; } = 24; // тик раз в сутки, как у jut.su/XSMART
    public bool seasonWatchAutoGrab { get; set; } = true;   // false — только уведомлять, не качать
    public int seasonWatchMinSeeds { get; set; } = 0;       // 0 → recommendMinSeeds
    public int seasonWatchMaxTries { get; set; } = 8;       // после N суток без раздачи — уведомление

    // ── D1VERSY LIVE: записи домашнего видеорегистратора (проект IPCamLive) ──
    // Регистратор доступен только из LAN, клиенты ходят к нему через наш прокси /qdl/live/*
    // (см. Live.cs). Адрес наружу не отдаётся.
    public string liveUrl { get; set; } = "http://192.168.87.24";
    public string liveTimezone { get; set; } = "";   // IANA-зона для показа времени; пусто = TZ контейнера
    public int liveDaysBack { get; set; } = 14;      // как далеко назад предлагать дни в выборе даты

    // Права на скрытые разделы (Perms.cs, qdl 2.54): D1versy Live/Rec видят только те устройства,
    // которым выдано право в админке /admin/d1v. Права лежат в access.json (cachePath), не в конфиге.
    // false — КИЛЛСВИТЧ на лету: разделы снова открыты всем, как было до 2.54. Оставлен потому, что
    // отказ прав ломал бы боевые разделы, а конфиг перечитывается без рестарта контейнера.
    public bool permsEnabled { get; set; } = true;

    // Песочница e2e (Perms.cs + TestSandbox.cs, qdl 2.64): безымянный headless-браузер не
    // попадает в реестр устройств, а стенд ходит под одним стабильным айди d1v-test-… и
    // убирает свои следы сам. false — КИЛЛСВИТЧ на лету: поведение как до 2.64 (headless снова
    // обычное устройство), и уборка /admin/d1v/api/test-purge отказывает всем без исключения.
    public bool testSandbox { get; set; } = true;

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
    public int catalogWarmupDetailBudget { get; set; } = 32;  // (v2, больше не читается — бюджет теперь в карточках)
    // v3 (qdl 2.45): бюджет считается в КАРТОЧКАХ, а не в URL. Открытие карточки — это ~8-9
    // параллельных запросов (детали, credits, recommendations, similar, videos ru+en, для сериала
    // ещё season/N, плюс cub-реакции), и клиент ждёт самый медленный из них — прогреть половину
    // почти то же, что не греть вовсе. Арифметика: ~1128 карточек-кандидатов, TTL деталей 24 ч,
    // 96 тиков в сутки → нужно ~11.75 карточек за тик; 16 даёт запас ×1.36. Стоимость тика:
    // 16 × 9 ≈ 144 запроса × 100 мс ≈ 15 с. Наружу уходят только MISS (~0.12 rps при WAF-лимите 50/с).
    public int catalogWarmupCardBudget { get; set; } = 16;

    // ── Карантин мёртвых рядов (qdl 2.65) ──
    // Ряд, на который апстрим подряд отвечает 4xx (кроме 408/429), — это не авария сервиса,
    // а несуществующий адрес: клиент прислал кривой URL, либо CUB убрал эндпоинт. Дёргать его
    // каждые 15 минут вечно бессмысленно. Порог 3 = 45 минут наблюдения: одна авария апстрима,
    // отдавшая 4xx, ряд не хоронит. Выход из карантина: клиент попросил ряд снова (одна проба)
    // либо прошло DeadRetryHours. 0 в DeadAfter — киллсвитч, правится в init.conf на лету.
    public int catalogWarmupDeadAfter { get; set; } = 3;
    public int catalogWarmupDeadRetryHours { get; set; } = 24;

    // ── Прогрев кнопок «Онлайн» (OnlineWarm.cs, qdl 2.45) ──
    // Набор рабочих балансеров собирается 8.2 с при 23 балансерах; TTL набора поднят до суток
    // (online.checkOnlineSearchMinutes) и лежит на диске, а джоба его продлевает — ПОСТЕПЕННО.
    // Решение владельца: новинки подхватывать сразу, хвост каталога подтягивать «совсем не спеша,
    // чтобы точно не заспамить и не попасть в лимиты». Отсюда три полосы с раздельными капами.
    // Дефолты дают ~3220 исходящих проб в сутки (0.037 rps) против 26 000 за цикл при прогреве залпом.
    public bool onlineWarmEnabled { get; set; } = true;
    public bool onlineWarmCatalog { get; set; } = true;   // полосы B+C; false — только keep-warm
    public int onlineWarmIntervalHours { get; set; } = 6; // 4 прогона в сутки
    public int onlineWarmPerRunA { get; set; } = 20;      // keep-warm: скачанное, слежение, уже гретое
    public int onlineWarmPerRunB { get; set; } = 10;      // новинки каталога (тёплые в пределах цикла)
    public int onlineWarmPerRunC { get; set; } = 5;       // хвост каталога по персистентному курсору
    public int onlineWarmPaceMs { get; set; } = 5000;     // пауза между карточками — прогон растянут, а не залпом

    // ── Кеш выдачи поиска раздач (SearchCache.cs, qdl 2.45) ──
    // /qdl/search — самое долгое ожидание в системе: два прохода по всем трекерам + bitmagnet +
    // локальный индекс, реально 3–15 с при таймаутах 40/45 с. Политика владельца: 6 ч отдаём молча,
    // до 7 дней — мгновенно с пометкой stale и фоновым обновлением, дальше живой поиск.
    // Читает кеш только интерактивный /qdl/search; охота и обходчик его лишь заполняют.
    public bool searchCacheEnabled { get; set; } = true;
    public int searchCacheFreshHours { get; set; } = 6;
    public int searchCacheStaleDays { get; set; } = 7;
    public int searchCacheRefreshParallel { get; set; } = 1;  // не частить с трекерами: одно фоновое обновление разом
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

    // ── Прогрев СЛЕДУЮЩЕЙ серии (JutSuSegments.cs, «автопилот» jut.su) ──
    // Дорогая часть старта серии — резолв (полная загрузка HTML-страницы, ~1.1 с). Греем
    // ссылку заранее, чтобы автопереход был бесшовным на всех платформах разом.
    // ⚠️ Порог считается от ПРОГРЕССА текущей серии, а не от её старта: TTL ссылки 240 с,
    // прогретая на первой минуте протухла бы задолго до конца серии.
    public bool jutPrewarmNext { get; set; } = true;
    public int jutPrewarmAtPercent { get; set; } = 60;   // кламп 10..95
    public int jutPrewarmCdnKb { get; set; } = 2048;     // первые КБ у CDN (греет их edge); 0 = только резолв

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

    // ── Пережатие постеров (JutSuPosterOptimize.cs) ──
    // AniList отдаёт обложку как есть, и часть из них — PNG: 460×650 в PNG весит 665 КБ, она же
    // в WebP q92 — 123 КБ. На странице витрины 30 карточек, то есть 6.1 МБ против 0.72 МБ у CUB.
    // 🔥 Качество не режем (решение владельца): разрешение родное, q92 — визуально без потерь.
    // "none" — полный откат к побайтовому сохранению, на лету, без пересборки.
    public string jutPosterFormat { get; set; } = "webp";  // webp|jpeg|none
    public int jutPosterQuality { get; set; } = 92;        // кламп 40..100
    // 0 = НЕ уменьшать (родное разрешение). >0 — кап ширины; апскейла не будет в любом случае.
    public int jutPosterWidth { get; set; } = 0;
    // Разовый фоновый проход по уже лежащим .up.jpg. Идемпотентен (WebP пропускается), поэтому
    // зовётся на каждом старте и просто дорабатывает остаток.
    public bool jutPosterReencode { get; set; } = true;
    public int jutPosterReencodePaceMs { get; set; } = 150;

    // ── Довод каталога до полноты (JutSuPosterOptimize.cs) ──
    // Очередь апгрейда засевается только теми страницами, которые кто-то ОТКРЫЛ, поэтому из 1357
    // тайтлов постер высокого качества был у 462. Джоба доводит остаток, переиспользуя обычный
    // конвейер JutPosterEnqueue (дедуп, кап 500, один воркер, пейс, BackgroundScope).
    // ⚠️ Сидер и голова каталога апгрейд по-прежнему НЕ зовут — это отдельная джоба (инвариант #3).
    public bool jutPosterBackfill { get; set; } = true;
    public int jutPosterBackfillBatch { get; set; } = 100;  // сколько держать в очереди, чтобы не упереться в кап 500

    // Слежение — ТОЛЬКО по jut.su. В торренты за этими сериями не ходим (три пояса изоляции,
    // см. JutSuWatch.cs и claude/jut/02-architecture.md §9).
    public int jutWatchIntervalHours { get; set; } = 24;   // кламп ≥1
    public int jutWatchTitlesPerTick { get; set; } = 30;   // бюджет ПОЛНЫХ опросов страниц тайтлов
    // Дефолт ТОЛЬКО для подписок без явного режима (curl, старый закешированный клиент):
    // UI передаёт режим сам — карточка тайтла autoGrab=0, «Загрузки» autoGrab=1.
    // Уже созданные подписки этот флаг НЕ переопределяет (см. JutAutoGrabFor).
    public bool jutWatchAutoGrab { get; set; } = true;     // новая серия сезона → сразу в очередь скачивания
    public bool jutWatchSeasonSwitch { get; set; } = true; // вышел новый сезон → переключить слежение на него

    // ─────────────────────────────────────────────────────────────────────────
    // XSMART (Xsmart.cs, XsmartGrab.cs, XsmartWatch.cs) — скачивание раздела в «Загрузки».
    // Устройство и контракт прокси: E:\Media-server\xsmart\service\CONTRACT.md
    // ─────────────────────────────────────────────────────────────────────────

    // Выключатель ТОЛЬКО скачивания и слежения. Онлайн-просмотр раздела он не трогает —
    // тот живёт целиком в контейнере xsmart-proxy и гасится docker compose stop.
    public bool xsmartEnable { get; set; } = true;

    // Адрес контейнера ВНУТРИ сети media. Наружу этот адрес не попадает никогда.
    public string xsmartApi { get; set; } = "http://xsmart-proxy:9140";

    // ⚠️ Путь обязан быть смонтирован rw в docker-compose.yml. Корень /downloads примонтирован
    // :ro, писать можно только в явно перечисленные подпапки (как /downloads/jutsu).
    public string xsmartDownloadsPath { get; set; } = "/downloads/xsmart";

    public int xsmartGrabRetries { get; set; } = 5;
    public int xsmartGrabIdleSec { get; set; } = 60;       // обрыв «нет данных от CDN» (0 = выключить)
    public int xsmartGrabPaceMs { get; set; } = 0;         // мягкий кап скорости (>0 = пауза между чанками)
    // Фильм 1080p ≈ 4 ГБ, 4K HDR ≈ 30+ ГБ. На том же диске живут торренты и записи
    // регистратора, поэтому резерв здесь больше, чем у аниме.
    public int xsmartMinFreeGb { get; set; } = 30;
    public bool xsmartNotifyAggregate { get; set; } = true;
    public int xsmartNotifyCoalesceSec { get; set; } = 300;

    // Дефолт ТОЛЬКО для подписок без явного режима (curl, старый клиент): UI режим передаёт
    // сам — карточка тайтла autoGrab=0, «Загрузки» autoGrab=1.
    public bool xsmartWatchAutoGrab { get; set; } = true;
    public bool xsmartWatchSeasonSwitch { get; set; } = true;  // вышел новый сезон → переключить слежение
    // Такт слежения. Раньше был захардкожен 24 ч в ModInit — а вместе с ним не существовало
    // и догона пропущенных тиков, потому что писать его было не на чем (поля lastRun не было).
    public int xsmartWatchIntervalHours { get; set; } = 24;    // кламп ≥1

    // ─────────────────────────────────────────────────────────────────────────
    // Персистентная очередь скачивания (DownloadWants.cs) — общая для jut.su и XSMART.
    // Ставится по одному разу на пачку, снимается по факту готового файла.
    // ─────────────────────────────────────────────────────────────────────────

    // Аварийные выключатели: если файл namерений когда-нибудь отравится в бою, владелец
    // гасит слой правкой init.conf, без пересборки форка. Поведение при false — ровно
    // сегодняшнее (очередь только в РАМ).
    public bool xsmartQueuePersist { get; set; } = true;
    public bool jutQueuePersist { get; set; } = true;

    // Порог парковки: после стольких неудач подряд серия перестаёт ставиться автоматически,
    // но запись НЕ удаляется (видна в статусе, снимается вручную или переживает до починки).
    public int xsmartWantMaxTries { get; set; } = 12;
    public int jutWantMaxTries { get; set; } = 12;

    // Свип долгов: проход по журналу намерений БЕЗ единого сетевого запроса. Нужен потому,
    // что резолв ретраев не делает вовсе — серия, упавшая через 10 секунд после старта
    // (прокси ещё не поднялся), иначе ждала бы следующего рестарта. 0 = только на старте и в тике.
    public int wantsSweepMinutes { get; set; } = 5;
    public int wantsMaxPerTitle { get; set; } = 1000;
    public int wantsKeepDays { get; set; } = 30;              // TTL припаркованных записей

    // ─────────────────────────────────────────────────────────────────────────
    // Апгрейд качества уже скачанного. Порталы выкладывают свежие серии РАНЬШЕ, чем
    // дотранскодят высокие дорожки: «Телохранители» s2e11–s2e14 приехали 360p при 720p
    // у соседних серий, и ключ серии качества не различает — апгрейда не случилось бы никогда.
    // 0 = выключено полностью: ничего не проверяется и не качается.
    // ─────────────────────────────────────────────────────────────────────────
    public int xsmartQualityTarget { get; set; } = 0;         // 720 — включить
    public int jutQualityTarget { get; set; } = 0;            // 1080 — включить
    public int qualityIntervalHours { get; set; } = 24;       // такт прохода, кламп ≥1
    public int qualityPerTick { get; set; } = 20;             // бюджет резолвов за проход
    public int qualityMaxUpgrades { get; set; } = 3;          // кап попыток на серию
    public int qualityRecheckDays { get; set; } = 7;          // когда перепроверять «портал не дотранскодил»

    // ─────────────────────────────────────────────────────────────────────────
    // Реплика (Replica.cs) — маленький бекап-сервер на второй площадке.
    // Канон: E:\D1vision-replica\claude\, план — claude/ этого репозитория.
    // ─────────────────────────────────────────────────────────────────────────

    // "main" (или пусто) — обычный домашний сервер: поднимаются только ручки /qdl/replica/* на
    // ЧТЕНИЕ, поведение не меняется ничем. "replica" — сервер-реплика: включается цикл
    // репликации и ЗАПРЕЩАЮТСЯ все мутирующие ручки (реплика ничего не качает по своей воле).
    // ⚠️ Роль читается в одном месте (ReplicaRole), сравнение регистронезависимое: опечатка в
    // init.conf не должна тихо превратить реплику в дом, который начнёт удалять чужое.
    public string replicaRole { get; set; } = "";

    // Адрес дома ДЛЯ РЕПЛИКИ. Это порт МОСТА (сегментированный сайт Caddy, где разрешены только
    // GET /qdl/replica/*, /qdl/stream, /qdl/poster), а НЕ 9118: туннель делает реплику жителем
    // домашней LAN, и полный доступ к /qdl/delete оттуда открывать нельзя.
    public string replicaMainUrl { get; set; } = "";

    // Бюджет ВИДЕО в гигабайтах (торренты + аниме). Служебное — образ, кеши, БД, HLS — сюда не
    // входит и живёт сверх; на диске 300 ГБ разумный бюджет ≈ 240.
    public int replicaBudgetGb { get; set; } = 240;

    // Ватерлинии в процентах от бюджета: набираем до low, чистим только выше high. Одна граница
    // давала бы «пилу» — граничный элемент вечно качался бы и вычищался, сжигая аплинк дома.
    public int replicaLowWatermark { get; set; } = 85;
    public int replicaHighWatermark { get; set; } = 95;

    // Элемент крупнее N% бюджета пропускается (skip-and-continue), а не останавливает набор:
    // иначе один 4K-ремукс сверху вытеснил бы всю библиотеку или заклинил селектор.
    public int replicaMaxItemPercent { get; set; } = 40;

    // Минимальная резиденция: только что приехавшее не удаляем, даже если оно ушло вниз окна.
    public int replicaMinResidenceHours { get; set; } = 24;

    // Период тика репликации (минуты, кламп ≥1).
    public int replicaIntervalMin { get; set; } = 5;

    // Общий потолок скорости МОСТА (МБ/с) — один token bucket на процесс, а не на поток.
    public int replicaBridgeMBps { get; set; } = 5;

    // 🔴 По умолчанию ротация НИЧЕГО не удаляет, только пишет в журнал «удалил бы X, N ГБ, причина».
    // Включать боевой режим осознанно и после чтения журнала: прецедент §BY — модуль уже удалял
    // раздачу вместе с файлами, и на удалённой площадке такое неотлаживаемо.
    public bool replicaRotateDryRun { get; set; } = true;

    // Предохранитель на случай ошибки в отборе: не более N удалений за тик.
    public int replicaMaxDeletesPerTick { get; set; } = 5;

    // Резерв свободного места (ГБ) сверх размера файла: под БД, кеши, HLS и рост образа.
    // Ниже него ротация останавливается и кричит в хелс-чеки, а не «дочищает» диск.
    public int replicaFreeReserveGb { get; set; } = 25;

    // Манифест «съёжился» больше чем на N% против прошлого успешного — считаем это аварией дома,
    // а не сигналом «всё удалили»: работаем на чтение и НИЧЕГО не удаляем (fail-safe).
    public int replicaShrinkGuardPercent { get; set; } = 30;

    // Раздача, у которой сторонних сидов нет (единственный источник — дом), качается ПО МОСТУ:
    // иначе swarm-загрузка съела бы домашний аплинк мимо шейпера.
    public bool replicaBridgeWhenOnlyHomeSeeds { get; set; } = true;

    // Отдача реплики. Исторически её не было вовсе: символический 1 КБ/с плюс ratioLimit=0
    // (остановка сразу по завершении) — раздача с датацентрового адреса это abuse-письма.
    // 🔄 25.08.2026 владелец решение пересмотрел: реплика сидирует. Общий потолок (2 МБ/с)
    // живёт в самом qBittorrent реплики (Session\GlobalUPSpeedLimit) — он один на ВСЕ раздачи;
    // здесь же лимит на каждую по отдельности, и <= 0 означает «не ограничивать».
    public int replicaSeedUpLimitKBps { get; set; } = -1;

    // Порог ratio, на котором qBittorrent останавливает раздачу: -1 — без лимита (сидируем),
    // 0 — вставать сразу по завершении (прежнее поведение «реплика не сидирует»).
    public double replicaSeedRatioLimit { get; set; } = -1;

    // Перенос истории просмотров дом → реплика (ReplicaHistory.cs): таймкоды «Продолжить»,
    // закладки и блобы localStorage клиента. Односторонне: что посмотрели на реплике, домой
    // не уедет. false = киллсвитч (клиент на реплике увидит чистый профиль).
    public bool replicaHistory { get; set; } = true;

    // Зеркало ленты уведомлений и памяти экрана jut.su дом → реплика (ReplicaNoti.cs). Своих
    // событий у реплики нет вовсе (слежение и граберы там 403), поэтому без переноса колокольчик
    // на tv2 пуст всегда. false = киллсвитч (лента снова станет пустой, ничего больше не сломав).
    public bool replicaNoti { get; set; } = true;

    // Хост, под который реплика греет каталог: "https://tv2.d1versy.com:9443". Ключ Staticache
    // считается из Scheme+Host, поэтому греть надо ровно тем адресом, которым ходят клиенты.
    // Пусто = ряды от дома не засеваются, прогрев учится только на своих живых клиентах
    // (то есть холодный кеш ровно тогда, когда реплика впервые понадобилась).
    public string replicaWarmHost { get; set; } = "";

    // ── Зеркалирование удалений (ReplicaRotate.cs, qdl 2.55) ─────────────────────────────────
    // 🔴 Контур ОТДЕЛЬНЫЙ от бюджетной ротации, и выключатели у него свои. Это разные классы
    // удаления: бюджет выкидывает то, что у дома ЖИВО (и вернётся, когда освободится место),
    // зеркало — то, что дома уже УДАЛИЛИ. Общий dry-run означал бы «либо обе, либо никакая»,
    // а нужно ровно наоборот: зеркало в бою при бюджетной ротации, оставленной в журнале.
    public bool replicaMirrorDeletes { get; set; } = false;   // киллсвитч всего контура
    public bool replicaMirrorDryRun { get; set; } = true;     // журнал вместо удаления

    // Сирота подтверждается ОБОИМИ условиями сразу. Одни тики — три промаха набегают за три
    // минуты после рестарта; одно время — скачок часов контейнера даёт мгновенное удаление.
    public int replicaOrphanConfirmMinutes { get; set; } = 15;
    public int replicaOrphanConfirmTicks { get; set; } = 3;

    // «Играли здесь» для сироты — ОТСРОЧКА, а не вето: счётчик подтверждений не сбрасывается,
    // и в первый же тик после грейса она уходит. Грейс короче бюджетных суток намеренно —
    // иначе регулярный просмотр превратил бы зеркалирование в вечный откат. 0 = выключить.
    public int replicaOrphanPlayedGraceMinutes { get; set; } = 30;

    // Свой кап за тик: общий с бюджетным означал бы, что накопленные дубли голодают ротацию
    // (и наоборот).
    public int replicaMaxOrphanDeletesPerTick { get; set; } = 5;

    // Тормоз массовости: проход не выполняется ЦЕЛИКОМ, если сирот и БОЛЬШЕ N% набора,
    // и не меньше replicaOrphanBrakeMinCount штук. Аналог replicaShrinkGuardPercent, но по
    // другому основанию: shrink сравнивает СЧЁТЧИК манифеста с прошлым тиком, а этот —
    // ПЕРЕСЕЧЕНИЕ нашего набора с домашним, и ловит валидный, но не тот снимок дома.
    //
    // 🔴 Абсолютный пол обязателен, и вот почему (боевой случай 21.08.2026). Реплика держит
    // ДЕСЯТКИ позиций, а не сотни: 6 сирот из 19 — это 31%, то есть один только процент
    // блокировал бы совершенно нормальный хвост. Хуже того, ПЕРВЫЙ запуск зеркалирования по
    // определению несёт накопленное за месяцы — и порог по доле гасил бы фичу ровно в тот
    // момент, ради которого она написана. Настоящая беда, от которой этот тормоз стоит
    // (дом отдал валидный, но не тот снимок), осиротила бы почти ВСЁ, а не треть.
    public int replicaOrphanMaxSharePercent { get; set; } = 25;
    public int replicaOrphanBrakeMinCount { get; set; } = 10;
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
