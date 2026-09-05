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

    /// <summary>
    /// qdl 2.112: киллсвитч сторожа номера страницы (PageGuard). Выключает всё разом — и
    /// проверку, и повтор, и подстановку копии; поведение становится ровно таким, как до 2.112.
    /// ⚠️ Секции "cub" в боевом init.conf нет вовсе, так что дефолт из кода работает сразу.
    /// </summary>
    public bool pageGuard { get; set; } = true;

    /// <summary>
    /// qdl 2.112: отдельный рубильник на ПОВТОР — единственную часть сторожа, которая ходит
    /// наружу. Выключен — расхождение по-прежнему ловим, тело не кешируем и подставляем копию,
    /// но лишнего запроса к CUB не делаем. ⚠️ Без повторов предохранитель (pageGuardOpenAfter)
    /// не взводится: подтвердить, что врёт сам ответ, а не кеш, можно только повтором.
    /// </summary>
    public bool pageGuardRetry { get; set; } = true;

    /// <summary>
    /// qdl 2.112: сколько минут годится сохранённая копия страницы для подстановки. 0 —
    /// подстановка выключена (копии всё равно ведём, чтобы после включения не начинать с нуля).
    /// </summary>
    public int pageGuardKeepMinutes { get; set; } = 1440;

    /// <summary>
    /// qdl 2.112: TTL для ВЫЛЕЧЕННОГО повтором тела. Оно верное, но кеш CUB по этому ключу
    /// сейчас нестабилен — держать его общие 3 часа неразумно.
    /// </summary>
    public int pageGuardSuspectMinutes { get; set; } = 15;

    /// <summary>
    /// qdl 2.112: потолок повторов за окно PageGuard.SlotMinutes (10 мин). 0 — повторов нет
    /// (ноль во всей секции значит «выключено», как у keepMinutes и suspectMinutes).
    /// </summary>
    public int pageGuardRetryCap { get; set; } = 60;

    /// <summary>
    /// qdl 2.112: сколько ПОДТВЕРЖДЁННЫХ расхождений за окно открывают предохранитель.
    /// Подтверждённое — повтор мимо кеша CUB вернул тело, и номер страницы всё равно чужой: врёт
    /// не кеш, а сам ответ, и вероятнее, что ошиблись мы (CUB сменил семантику page). Пока
    /// предохранитель открыт, сторож только наблюдает: отдаёт то, что дал CUB, с коротким TTL
    /// (pageGuardSuspectMinutes), копию не подставляет и кеш не выключает — иначе весь каталог
    /// стал бы некешируемым. Окно 10 мин, со сменой окна закрывается сам. 0 — никогда.
    /// </summary>
    public int pageGuardOpenAfter { get; set; } = 20;


    [JsonProperty("limit_map", ObjectCreationHandling = ObjectCreationHandling.Replace, NullValueHandling = NullValueHandling.Ignore)]
    public List<WafLimitRootMap> limit_map { get; set; }
}
