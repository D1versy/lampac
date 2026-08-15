using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QbitDownload;

/// <summary>
/// qdl 2.45: кеш выдачи поиска раздач (кнопка «Скачать») со stale-семантикой.
///
/// Зачем. /qdl/search — самое долгое ожидание во всей системе: два полных прохода по всем трекерам
/// (rutracker через flaresolverr) + bitmagnet + локальный индекс, реально 3–15 с при серверном
/// таймауте прохода 40 с и клиентском 45 с. Кеша не было вообще: повторный поиск того же тайтла
/// платил полную цену заново.
///
/// Политика (выбор владельца): моложе freshHours (6 ч) — отдаём молча; от 6 ч до staleDays (7 дней) —
/// отдаём мгновенно с пометкой stale и обновляем в фоне; старше — живой поиск.
///
/// 🔥 Кто ЧИТАЕТ кеш. Только интерактивный /qdl/search. Фоновые контуры (охота за сериями
/// EpisodeHunter, обходчик индекса IndexCrawler, предложение переключения доноров) кеш только
/// ЗАПОЛНЯЮТ, но никогда из него не читают: их работа — заметить НОВУЮ раздачу, а шестичасовой
/// снимок ровно этого их и лишил бы. Побочный эффект приятный: обходчик, который и так платит за
/// трекеры 5 тайтлов в час, попутно греет пользовательский кеш — нового трафика к трекерам ноль.
///
/// Инварианты:
///  • ⚠️ Храним выдачу ДО скоринга. TorrentScoring.SortAndMark МУТИРУЕТ элементы (пишет score,
///    watchable, ep), поэтому и на запись, и на чтение идёт DeepClone — иначе первый же читатель
///    испортил бы общий кеш, а второй получил бы чужие пометки. Это инвариант №1, он под тестом.
///  • Ключ не включает ни season, ни preferredQuality: на состав выдачи они не влияют (season в
///    FetchIndexer вообще не уходит), только на сортировку. Скоринг гоняется на каждый запрос —
///    это чистый CPU и стоит доли миллисекунды.
///  • Сиды в снимке устаревают: раздача с 30 сидами могла умереть. Магнет при этом остаётся
///    валидным, а пометка stale про это честно говорит.
///  • Файл пишется .tmp → Move: обрезанный после падения по питанию JSON обязан читаться как
///    «кеша нет», а не как пустой список раздач.
/// </summary>
public static class SearchCache
{
    sealed class Snapshot
    {
        public int ver { get; set; }
        public long at { get; set; }        // unix UTC
        public string key { get; set; }
        public string about { get; set; }   // человекочитаемо, только для разбора руками
        public JArray items { get; set; }
    }

    // Дедуп фоновых обновлений: один и тот же ключ не должен обновляться двумя потоками разом.
    static readonly ConcurrentDictionary<string, byte> _refreshing = new();

    static string Dir()
    {
        string d = Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "search");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    #region чистые функции — покрыты тестами

    /// <summary>
    /// Ключ снимка. tmdb_id — самый надёжный (по нему невозможно перепутать тайтл); без него
    /// падаем на нормализованное название + год. is_serial входит всегда: узкая и широкая ветки
    /// индексатора дают разную выдачу.
    /// </summary>
    public static string Key(string tmdbId, string titleNorm, int year, int isSerial)
    {
        string core = !string.IsNullOrWhiteSpace(tmdbId)
            ? "t" + tmdbId.Trim()
            : "n" + (titleNorm ?? "").Trim().ToLowerInvariant() + "|" + (year > 0 ? year.ToString() : "");

        if (core is "t" or "n|")
            return null;   // ни id, ни названия — кешировать нечего

        return Fnv(core + "|s" + isSerial);
    }

    static string Fnv(string s)
    {
        unchecked
        {
            ulong h = 14695981039346656037;
            foreach (char c in s) { h ^= c; h *= 1099511628211; }
            return h.ToString("x16");
        }
    }

    public enum Freshness { Miss, Fresh, Stale }

    /// <summary>Свежесть снимка по его возрасту.</summary>
    public static Freshness Judge(long atUnix, long nowUnix, int freshHours, int staleDays)
    {
        if (atUnix <= 0) return Freshness.Miss;

        long age = nowUnix - atUnix;
        if (age < 0) return Freshness.Fresh;              // часы уехали назад — не наказываем кеш
        if (age <= freshHours * 3600L) return Freshness.Fresh;
        if (age <= staleDays * 86400L) return Freshness.Stale;
        return Freshness.Miss;
    }

    #endregion

    static int FreshHours => Math.Max(1, ModInit.conf?.searchCacheFreshHours ?? 6);
    static int StaleDays => Math.Max(1, ModInit.conf?.searchCacheStaleDays ?? 7);

    static string PathFor(string key) => Path.Combine(Dir(), key + ".json");

    /// <summary>
    /// Читает снимок. items — ВСЕГДА глубокая копия: вызывающий отдаёт её в SortAndMark, который
    /// мутирует элементы.
    /// </summary>
    public static Freshness TryRead(string key, out JArray items)
    {
        items = null;
        if (string.IsNullOrEmpty(key) || ModInit.conf?.searchCacheEnabled != true)
            return Freshness.Miss;

        try
        {
            string path = PathFor(key);
            if (!File.Exists(path))
                return Freshness.Miss;

            var snap = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(path));
            if (snap?.items == null || snap.ver != 1)
                return Freshness.Miss;

            var fresh = Judge(snap.at, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), FreshHours, StaleDays);
            if (fresh == Freshness.Miss)
                return Freshness.Miss;

            items = (JArray)snap.items.DeepClone();
            return fresh;
        }
        catch { return Freshness.Miss; }
    }

    /// <summary>
    /// Сохраняет выдачу ДО скоринга. Клонируем сразу и синхронно: вызывающий тут же отдаст этот
    /// же JArray в SortAndMark, и без копии на диск уехали бы уже помеченные элементы.
    /// </summary>
    public static void Write(string key, JArray items, string about)
    {
        if (string.IsNullOrEmpty(key) || items == null || ModInit.conf?.searchCacheEnabled != true)
            return;

        // Пустую выдачу не кешируем: «ничего не нашлось» чаще означает разовый сбой обхода,
        // и закреплять его на 6 часов — ровно тот случай, когда кеш вредит.
        if (items.Count == 0)
            return;

        JArray copy;
        try { copy = (JArray)items.DeepClone(); }
        catch { return; }

        try
        {
            var snap = new Snapshot
            {
                ver = 1,
                at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                key = key,
                about = about,
                items = copy
            };

            string path = PathFor(key);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(snap));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] search cache write: " + ex.Message); }
    }

    /// <summary>Занять слот фонового обновления (дедуп по ключу). false — уже обновляется.</summary>
    public static bool TryBeginRefresh(string key)
        => !string.IsNullOrEmpty(key) && _refreshing.TryAdd(key, 1);

    public static void EndRefresh(string key)
    {
        if (!string.IsNullOrEmpty(key)) _refreshing.TryRemove(key, out _);
    }

    public static int RefreshingCount => _refreshing.Count;

    /// <summary>Уборка снимков старше staleDays — цепляется к суточному _pruneTimer.</summary>
    public static int Prune()
    {
        int removed = 0;
        try
        {
            long deadline = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - StaleDays * 86400L;
            foreach (var f in Directory.EnumerateFiles(Dir(), "*.json"))
            {
                try
                {
                    var snap = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(f));
                    if (snap == null || snap.at < deadline) { File.Delete(f); removed++; }
                }
                catch { try { File.Delete(f); removed++; } catch { } }
            }
        }
        catch { }
        return removed;
    }
}
