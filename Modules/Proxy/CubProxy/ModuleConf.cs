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


    [JsonProperty("limit_map", ObjectCreationHandling = ObjectCreationHandling.Replace, NullValueHandling = NullValueHandling.Ignore)]
    public List<WafLimitRootMap> limit_map { get; set; }
}
