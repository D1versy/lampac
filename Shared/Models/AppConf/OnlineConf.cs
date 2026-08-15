using Shared.Models.Module;

namespace Shared.Models.AppConf;

public class OnlineConf : ModuleBaseConf
{
    public string name { get; set; }

    public bool version { get; set; }

    public bool checkOnlineSearch { get; set; }

    /// <summary>
    /// qdl 2.45: сколько минут живёт результат checkOnlineSearch (набор рабочих балансеров для
    /// карточки). Раньше было жёстко 5 минут в коде — при 23 балансерах полный набор собирается
    /// 8.2 с (замер), то есть кнопки «Онлайн» дособирались заново почти на каждое открытие.
    /// Дефолт 5 оставлен для совместимости; боевое значение задаётся в init.conf (у нас 1440),
    /// и в паре с фоновым прогревом (QbitDownload/OnlineWarm.cs) набор всегда готов заранее.
    /// </summary>
    public int checkOnlineSearchMinutes { get; set; } = 5;

    public bool btn_priority_forced { get; set; }

    public HashSet<string> with_search { get; set; }
}
