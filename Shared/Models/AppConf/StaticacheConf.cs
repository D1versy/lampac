using System.Text.RegularExpressions;

namespace Shared.Models.AppConf;

public class StaticacheConf
{
    public bool enable { get; set; }

    /// <summary>
    /// только то что явно указано в routes
    /// </summary>
    public bool manually { get; set; }

    public int minimalCacheMinutes { get; set; }

    public List<StaticacheRoute> routes { get; set; } = new();

    public string[] disabledPaths { get; set; }
}

public struct StaticacheRoute
{
    public string path { get; set; }

    public string pathRex { get; set; }

    public int cacheMinutes { get; set; }

    public bool skipUids { get; set; }

    public string[] queryKeys { get; set; }

    public string[] ignoreQueryKeys { get; set; }
}

public class StaticachePreparedRoute
{
    public StaticacheRoute Route { get; init; }

    public Regex PathRegex { get; init; }
}

/// brLength > 0 — рядом с raw-файлом лежит готовый "<file>.br" такого размера (сжат один раз при
/// записи, см. Staticache.CompressBr): HIT отдаёт его мимо ResponseCompression без пережима.
///
/// etag (qdl 2.53) — слабый ETag ТЕЛА для роутов с revalidate:true. Считается лениво, при первой
/// отдаче записи (хеш raw-файла), и живёт в модели до вытеснения: пересчёт TTL его не трогает,
/// поэтому клиент получает 304 и через сутки. null = ещё не посчитан (в т.ч. сразу после
/// рестарта, когда модели поднимаются сканом каталога) — тогда ревалидации просто нет.
public readonly record struct StaticacheCacheModel(long ex, string ext, short statusCode = 200, int contentLength = 0, int brLength = 0, string etag = null);

public record StaticacheFeature(int cacheMinutes, string cachekey);
