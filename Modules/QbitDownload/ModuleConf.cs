using Shared.Models.Module;

namespace QbitDownload;

public class ModuleConf : ModuleBaseConf
{
    public bool enable { get; set; } = true;

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

    // Слежение за сериалами: как часто проверять обновление раздач (часы).
    public int watchIntervalHours { get; set; } = 6;

    // Уведомления о скачанных сериях: как часто сканировать прогресс файлов отслеживаемых раздач (минуты).
    // Дёшево (локальные запросы к qBit, без пере-резолва трекеров) → можно часто.
    public int notifyScanIntervalMinutes { get; set; } = 15;
}
