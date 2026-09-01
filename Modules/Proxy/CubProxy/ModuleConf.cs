using Newtonsoft.Json;
using Shared.Models.AppConf;
using Shared.Models.Base;
using System.Collections.Generic;

namespace CubProxy;

public class ModuleConf : CubConf, Iproxy
{
    public bool viewru { get; set; }

    public int cache_api { get; set; }

    /// <summary>
    /// qdl 2.45: отдельный TTL (минуты) для /api/reactions/get/* — их просят на каждом открытии
    /// карточки, а меняются они редко. 0 — использовать общий cache_api.
    /// </summary>
    public int cache_reactions { get; set; } = 1440;

    /// <summary>
    /// qdl 2.45: отдавать пустой {} на /api/ai/metadata/* вместо похода к CUB (там стабильный 500
    /// «Метаданные не найдены» — премиум-фича чужого аккаунта). Выключить, если у CUB появится
    /// рабочая отдача метаданных и она понадобится.
    /// </summary>
    public bool stubAiMetadata { get; set; } = true;

    /// <summary>
    /// qdl 2.89: киллсвитч фильтра рядов каталога по году. Сам порог и тумблер владельца лежат
    /// в catalogFilterFile (их правят из настроек Lampa по праву «действия»); это — аварийный
    /// рубильник в init.conf, которым фича гасится целиком, не трогая значения владельца.
    /// ⚠️ Секции "cub" в боевом init.conf нет вовсе, так что дефолт из кода работает сразу.
    /// </summary>
    public bool catalogFilter { get; set; } = true;

    /// <summary>
    /// qdl 2.89: файл глобальной настройки фильтра. Пишет его модуль QbitDownload
    /// (JsonStore.WriteNow), читаем мы — напрямую связать модули нельзя, они компилируются
    /// в разные сборки. Путь обязан совпадать с cachePath модуля QbitDownload.
    /// </summary>
    public string catalogFilterFile { get; set; } = "/qdl-data/catalog-filter.json";


    [JsonProperty("limit_map", ObjectCreationHandling = ObjectCreationHandling.Replace, NullValueHandling = NullValueHandling.Ignore)]
    public List<WafLimitRootMap> limit_map { get; set; }
}
