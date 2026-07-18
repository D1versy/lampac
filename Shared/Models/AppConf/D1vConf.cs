using Newtonsoft.Json;

namespace Shared.Models.AppConf;

/// <summary>
/// D1Vision: периметр внешнего доступа (см. Core/Middlewares/D1VPerimeter.cs).
/// Запросы, пришедшие через edge-прокси (Caddy ставит заголовок edgeHeader),
/// обязаны предъявить ключ платформы (query d1v / заголовок X-D1V-Key / cookie d1v).
/// Значения ключей живут в init.conf (вне git), секция перечитывается на лету.
/// </summary>
public class D1vConf
{
    public bool enable { get; set; }

    /// <summary>Заголовок-маркер «запрос пришёл извне» (Caddy header_up перезаписывает клиентское значение).</summary>
    public string edgeHeader { get; set; } = "X-D1V-Edge";

    /// <summary>Имя cookie, в которую сервер пересаживает валидный ключ (дальше WebView шлёт её сам).</summary>
    public string cookieName { get; set; } = "d1v";

    /// <summary>Срок жизни cookie, дней.</summary>
    public int cookieDays { get; set; } = 365;

    /// <summary>platform → hex-ключ (mac/ios/android/...). Компрометация одной платформы = ротация одного ключа.</summary>
    [JsonProperty("keys", ObjectCreationHandling = ObjectCreationHandling.Replace, NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string> keys { get; set; }

    /// <summary>GET-префиксы, открытые извне без ключа (OTA-канал /d1vision/apps/ — бинари подписаны).</summary>
    [JsonProperty("publicPrefixes", ObjectCreationHandling = ObjectCreationHandling.Replace, NullValueHandling = NullValueHandling.Ignore)]
    public List<string> publicPrefixes { get; set; }
}
