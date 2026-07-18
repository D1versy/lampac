using Shared.Models.Module;

namespace TorrServer;

public class ModuleConf : ModuleBaseConf
{
    /// <summary>
    /// D1Vision: URL внешнего TorrServer (например http://torrserver:8090 — контейнер из docker-compose).
    /// Если задан — модуль НЕ качает и НЕ запускает свой бинарь, а только проксирует /ts/* на этот адрес
    /// (без Basic-auth — внешний TS работает без --httpauth). Смена значения требует рестарта.
    /// </summary>
    public string external_url { get; set; }

    public string releases { get; set; }

    public bool rdb { get; set; }

    public string defaultPasswd { get; set; }

    public int tsport { get; set; }

    public int group { get; set; }

    public bool checkfile { get; set; }
}
