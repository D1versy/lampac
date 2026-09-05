using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Журнал событий для владельца (qdl 2.111) — вкладка «Уведомления» в /admin/d1v.
//
// Сюда переезжает вся кухня, которую раньше видел зритель (постановка в очередь, смена
// раздачи, доноры охоты, перекачка ради качества, диагностика поиска), плюс события,
// которых до сих пор не было НИГДЕ, кроме stdout контейнера, — а логи контейнера
// ротируются, и вопрос «почему сериал перекачался» задаётся через неделю.
//
// 🔴 Почему не таблица в SQLite. ModInit зовёт только EnsureCreated(): она не добавляет ни
// таблицы, ни колонки в уже существующую БД — новая таблица молча не создастся (та же
// грабля описана в SearchMonitor.cs). Поэтому журнал — JSON-кольцо на томе, как
// search-monitor.json, и по той же причине у noti нет колонки «аудитория»: маршрутизация
// живёт в NotiRoute по полю kind.
//
// Запись идёт через JsonStore (РАМ — истина, диск — write-behind с коалесингом), поэтому
// залп охоты из полусотни строк стоит одной записи файла. У дежурного и замороженного
// экземпляра JsonStore.WritesEnabled == false — их события останутся только в РАМ, и это
// верно: фоновые контуры всё равно крутит ведущий, диск принадлежит ему.
// ─────────────────────────────────────────────────────────────────────────────
internal static class QdlEvents
{
    // Категории — они же фильтр-чипы в админке. Строки, а не enum: значение уезжает в JSON
    // и должно переживать перезапуск с другим порядком полей.
    internal const string CatDownload = "download";   // очередь, старт, пачка скачана
    internal const string CatRelease  = "release";    // re-grab, найдена более полная, переключено
    internal const string CatHunt     = "hunt";       // доноры, замещения, отказы
    internal const string CatWatch    = "watch";      // подписки: вышла серия/сезон, раздачи нет
    internal const string CatQuality  = "quality";    // перекачка ради 720/1080
    internal const string CatSpace    = "space";      // нет места
    internal const string CatDiag     = "diag";       // SearchMonitor
    // Ярлык для строк, которые реально ушли зрителю. В журнал они НЕ пишутся: источник правды
    // по ним — сама таблица noti, и админка подмешивает её при чтении (Admin.Events).
    // Иначе одно и то же событие лежало бы в двух местах и ело бы кольцо вдвое быстрее.
    internal const string CatUser     = "user";

    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "events.json");

    // Кольцо правится read-modify-write, поэтому под своим локом: без него два фоновых
    // контура (охота и сканер серий крутятся под общим _watchGate, а вот jut-тик — нет)
    // затёрли бы записи друг друга.
    static readonly object _lock = new();

    internal static bool Enabled => ModInit.conf?.adminEvents != false;

    static int Keep => Math.Clamp(ModInit.conf?.adminEventsKeep ?? 2000, 100, 20000);

    /// <summary>
    /// Записать событие. Никогда не бросает: журнал — диагностика, он не имеет права уронить
    /// контур, который его позвал.
    /// </summary>
    /// <param name="key">необязательный ключ дедупа для Recent() (например, "switch:&lt;btih&gt;")</param>
    internal static void Log(string cat, string title, string text,
                             string hash = null, string sk = null, string act = null, string key = null)
    {
        if (!Enabled || string.IsNullOrEmpty(text)) return;
        try
        {
            var row = new JObject
            {
                ["at"] = DateTime.UtcNow.ToString("o"),
                ["cat"] = cat ?? CatDiag,
                ["title"] = title ?? "",
                ["text"] = text
            };
            if (!string.IsNullOrEmpty(hash)) row["hash"] = hash;
            if (!string.IsNullOrEmpty(sk)) row["sk"] = sk;
            if (!string.IsNullOrEmpty(act)) row["act"] = act;
            if (!string.IsNullOrEmpty(key)) row["key"] = key;

            lock (_lock)
            {
                var arr = JsonStore.ReadArray(StorePath) ?? new JArray();
                arr.Add(row);
                int keep = Keep;
                while (arr.Count > keep) arr.RemoveAt(0);
                JsonStore.Write(StorePath, arr);
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] events log: " + ex.Message); }
    }

    /// <summary>
    /// Было ли уже такое событие за последние within. Дедуп для контуров, у которых он раньше
    /// держался на самой ленте (а её чистит NotiPrune) — см. DIAG в SearchMonitor.
    /// </summary>
    internal static bool Recent(string cat, string key, TimeSpan within)
    {
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            var arr = JsonStore.ReadArray(StorePath);
            if (arr == null) return false;
            var since = DateTime.UtcNow - within;
            foreach (var t in arr.OfType<JObject>().Reverse())
            {
                if (!DateTime.TryParse(t.Value<string>("at"), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var at)) continue;
                if (at < since) break;                       // массив упорядочен по времени
                if (t.Value<string>("cat") == cat && t.Value<string>("key") == key) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Последние limit записей, СВЕЖИЕ СВЕРХУ. Второй элемент — сколько записей всего.</summary>
    internal static (JArray items, int total) Read(int limit)
    {
        try
        {
            var arr = JsonStore.ReadArray(StorePath) ?? new JArray();
            int total = arr.Count;
            var take = arr.OfType<JObject>().Reverse().Take(Math.Max(1, limit));
            var res = new JArray();
            foreach (var o in take) res.Add(o);
            return (res, total);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] events read: " + ex.Message);
            return (new JArray(), 0);
        }
    }

    internal static void Clear()
    {
        lock (_lock) { JsonStore.Write(StorePath, new JArray()); }
    }
}
