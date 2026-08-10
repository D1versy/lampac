using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;              // CoreInit
using Shared.Services;     // Http
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// Обходчик: наполняет свой индекс (LocalIndex.cs), не дожидаясь, пока владелец сам откроет
// карточку. Иначе индекс копил бы только то, что искали руками, и как страховка был бы пуст
// ровно в тот момент, когда понадобится.
//
// ⚠️ Каждый обойдённый тайтл — это реальный запрос ко ВСЕМ трекерам. Сейчас вся выдача держится
// на rutor/nnmclub/kinozal, и получить от них бан по IP — куда хуже, чем медленно наполняемый
// индекс. Поэтому бюджет на тик маленький, обход строго последовательный с паузой, и тик
// вежливо уступает дорогу охоте за сериями.
//
// Базовый тип объявлен в Controller.cs.
public partial class QbitController
{
    static readonly SemaphoreSlim _crawlGate = new SemaphoreSlim(1, 1);
    static readonly DateTime _crawlBootAt = DateTime.UtcNow;

    static string CrawlStatePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "index-crawler.json");

    sealed class CrawlTarget
    {
        public string tmdbId, title, original;
        public int year, isSerial;
        public string Key => (string.IsNullOrEmpty(tmdbId) ? "n:" + title : "t:" + tmdbId) + ":" + isSerial;
    }

    #region источники тайтлов
    // 1) «Загрузки» и сериалы на слежении — полные метаданные лежат у нас на диске,
    //    внешних запросов не требуется вообще.
    static List<CrawlTarget> TargetsFromMeta()
    {
        var res = new List<CrawlTarget>();
        try
        {
            string dir = Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "meta");
            if (!Directory.Exists(dir)) return res;

            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var m = JObject.Parse(System.IO.File.ReadAllText(f));
                    string title = m.Value<string>("title");
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    // 🔴 ПОЯС 2 изоляции jut.su: за сериями оттуда в торренты не ходим вообще
                    // (требование владельца). Обходчик иначе подхватил бы jut-мету и начал
                    // дёргать по ней ВСЕ трекеры. См. JutSuWatch.cs и claude/jut/02-architecture.md §9.
                    if (string.Equals(m.Value<string>("source"), "jutsu", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string date = m.Value<string>("release_date") ?? m.Value<string>("first_air_date") ?? "";
                    int year = date.Length >= 4 && int.TryParse(date.Substring(0, 4), out int y) ? y : 0;
                    bool tv = (m.Value<string>("media_type") == "tv") || m["original_name"] != null || m["number_of_seasons"] != null;

                    res.Add(new CrawlTarget
                    {
                        tmdbId = m.Value<long?>("id")?.ToString(),
                        title = title,
                        original = m.Value<string>("original_title") ?? m.Value<string>("original_name"),
                        year = year,
                        isSerial = tv ? 2 : 1
                    });
                }
                catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] crawler meta: " + ex.Message); }
        return res;
    }

    // 2) карточки главной, которые уже разобрал прогрев каталога. Название берём из НАШЕГО
    //    же TMDB-прокси на loopback — детали этих карточек прогрев туда уже положил,
    //    так что это попадание в кеш, а не поход наружу.
    static async Task<List<CrawlTarget>> TargetsFromCatalog(int max)
    {
        var res = new List<CrawlTarget>();
        try
        {
            string apiKey = null;
            // тот же источник ключа, что у прогрева каталога (CatalogWarmup.cs)
            try { apiKey = CoreInit.conf.cub?.api_key; } catch { }

            foreach (var (id, tv) in CatalogWarmup.KnownCards().Take(max))
            {
                string url = $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}"
                           + $"/tmdb/api/3/{(tv ? "tv" : "movie")}/{id}?language=ru"
                           + (string.IsNullOrEmpty(apiKey) ? "" : "&api_key=" + apiKey);
                string raw = await Http.Get(url, timeoutSeconds: 10);
                if (string.IsNullOrEmpty(raw)) continue;
                try
                {
                    var m = JObject.Parse(raw);
                    string title = m.Value<string>("title") ?? m.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string date = m.Value<string>("release_date") ?? m.Value<string>("first_air_date") ?? "";
                    res.Add(new CrawlTarget
                    {
                        tmdbId = id.ToString(),
                        title = title,
                        original = m.Value<string>("original_title") ?? m.Value<string>("original_name"),
                        year = date.Length >= 4 && int.TryParse(date.Substring(0, 4), out int y) ? y : 0,
                        isSerial = tv ? 2 : 1
                    });
                }
                catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] crawler catalog: " + ex.Message); }
        return res;
    }
    #endregion

    #region тик
    public static async Task<int> IndexCrawlTick(bool force = false)
    {
        if (!LocalIndexEnabled) return 0;
        int budget = ModInit.conf?.indexCrawlPerTick ?? 0;
        if (budget <= 0 && !force) return 0;
        if (budget <= 0) budget = 5;

        if (!await _crawlGate.WaitAsync(0)) return 0;
        try
        {
            // Разогрев после старта и вежливость к охоте: обходчик — самый низкоприоритетный
            // контур, он всегда уступает.
            if (!force && (DateTime.UtcNow - _crawlBootAt).TotalMinutes < 15) return 0;
            if (!force && _watchGate.CurrentCount == 0)
            {
                Console.WriteLine("[QbitDownload] crawler: тик пропущен — занят фоновый контур");
                return 0;
            }

            var done = LoadCrawlDone();
            var targets = TargetsFromMeta();
            targets.AddRange(await TargetsFromCatalog(Math.Max(budget * 3, 30)));

            // дедуп внутри прохода + отсев уже обойдённых в этом цикле
            var queue = new List<CrawlTarget>();
            var seen = new HashSet<string>();
            foreach (var t in targets)
                if (seen.Add(t.Key) && !done.Contains(t.Key)) queue.Add(t);

            // Цикл закончился — начинаем заново: индекс должен освежать сиды, а не застыть.
            if (queue.Count == 0 && targets.Count > 0)
            {
                Console.WriteLine($"[QbitDownload] crawler: круг пройден ({targets.Count} тайтлов), начинаю заново");
                done.Clear();
                foreach (var t in targets) if (seen.Contains(t.Key)) queue.Add(t);
            }

            int n = 0;
            foreach (var t in queue.Take(budget))
            {
                try
                {
                    // тот же путь, что у живого поиска → индекс наполняется его же побочным эффектом
                    await SearchScored(t.title, t.title, t.original, t.year, t.isSerial, 0,
                                       ModInit.conf?.indexerApikey, t.tmdbId);
                    done.Add(t.Key);
                    n++;
                }
                catch (Exception ex) { Console.WriteLine($"[QbitDownload] crawler «{t.title}»: {ex.Message}"); }

                if (n < budget) await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, ModInit.conf?.indexCrawlPauseSec ?? 20)));
            }

            SaveCrawlDone(done);
            if (n > 0) Console.WriteLine($"[QbitDownload] crawler: обойдено {n} тайтлов (осталось в круге {Math.Max(0, queue.Count - n)})");
            return n;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] crawler: " + ex); return 0; }
        finally { _crawlGate.Release(); }
    }

    static HashSet<string> LoadCrawlDone()
    {
        try
        {
            if (System.IO.File.Exists(CrawlStatePath))
            {
                var a = JArray.Parse(System.IO.File.ReadAllText(CrawlStatePath));
                return new HashSet<string>(a.Select(x => x.Value<string>()).Where(x => !string.IsNullOrEmpty(x)));
            }
        }
        catch { }
        return new HashSet<string>();
    }

    static void SaveCrawlDone(HashSet<string> done)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrawlStatePath));
            System.IO.File.WriteAllText(CrawlStatePath, new JArray(done).ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] crawler state: " + ex.Message); }
    }
    #endregion

    #region ручки
    [HttpGet, AllowAnonymous]
    [Route("qdl/index/crawl")]
    async public Task<ActionResult> IndexCrawlRun()
    {
        int n = await IndexCrawlTick(force: true);
        return ContentTo(new JObject { ["success"] = true, ["crawled"] = n }.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/index/stats")]
    async public Task<ActionResult> IndexStats()
        => ContentTo((await LocalIndexStats()).ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    #endregion
}
