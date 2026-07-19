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

    // Слежение за сериалами: как часто проверять обновление раздач (часы).
    public int watchIntervalHours { get; set; } = 6;

    // Уведомления о скачанных сериях: как часто сканировать прогресс файлов отслеживаемых раздач (минуты).
    // Дёшево (локальные запросы к qBit, без пере-резолва трекеров) → можно часто.
    public int notifyScanIntervalMinutes { get; set; } = 15;
}
