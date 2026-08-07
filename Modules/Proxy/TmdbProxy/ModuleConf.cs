using Shared.Models.Base;
using Shared.Models.Module;

namespace TmdbProxy;

public class ModuleConf : ModuleBaseConf
{
    public int httpversion { get; set; }

    public int cache_api { get; set; }

    public int cache_img { get; set; }

    public bool responseContentLength { get; set; }

    /// <summary>
    /// Серверный ключ TMDB для /tmdb/api: подставляется апстриму ВМЕСТО клиентского (и вместо
    /// его отсутствия). Пусто = фолбэк на CoreInit.conf.cub.api_key. Задаётся в init.conf,
    /// секция "tmdb". Ключ клиента при этом исключён из ключа Staticache (ignoreQueryKeys),
    /// поэтому кеш не фрагментируется по чужим ключам (в Android-оболочке зашит свой).
    /// </summary>
    public string api_key { get; set; }


    public TmdbProxyApiConf proxyapi { get; set; }

    public TmdbProxyApiConf proxyimg { get; set; }
}


public class TmdbProxyApiConf : Iproxy
{
    public bool useproxy { get; set; }

    public bool useproxystream { get; set; }

    public string globalnameproxy { get; set; }

    public ProxySettings proxy { get; set; }
}

public class TmdbProxyImageConf : Iproxy
{
    public bool useproxy { get; set; }

    public bool useproxystream { get; set; }

    public string globalnameproxy { get; set; }

    public ProxySettings proxy { get; set; }
}
