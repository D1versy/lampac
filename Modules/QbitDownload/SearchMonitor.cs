using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// Мониторинг поиска раздач: «канарейки» по индексатору, трекерам и ⭐.
//
// Зачем: поломка «Раздачи не найдены» (claude/06 §AY) жила неизвестно сколько и всплыла только
// потому, что владелец заметил её глазами. Логи теперь честные, но их никто не читает.
//
// Главное требование к этому контуру — НЕ СПАМИТЬ. Ложное срабатывание хуже пропущенного:
// после пары ложных тревог уведомления перестают читать, и смысл теряется. Отсюда четыре
// независимые проверки, у каждой свой стрик, скользящая медиана как база, «пропуск ≠ провал»
// и уведомление только на ПЕРЕХОДЕ состояния.
//
// Базовый тип (: BaseController) объявлен в Controller.cs — в partial-классе он указывается
// один раз, иначе CS0246 и модуль молча не грузится (все /qdl/* отдают 404).
public partial class QbitController
{
    // Свой гейт, НЕ общий _watchGate: мониторинг не трогает watch.json и qBit, ему незачем
    // конкурировать с охотой за сериями. Но перед стартом он вежливо уступает — см. Tick().
    static readonly SemaphoreSlim _diagGate = new SemaphoreSlim(1, 1);
    static readonly object _diagFileLock = new object();

    // Момент старта процесса: первые минуты трекеры холодные, кеш JackettApi пуст —
    // тик в это окно засчитывать нельзя ни как успех, ни как провал.
    static readonly DateTime _diagBootAt = DateTime.UtcNow;

    const int DiagWarmupMinutes = 10;

    static string DiagStatePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "search-monitor.json");

    #region канарейки
    // Дефолт зашит в коде, а не в ModuleConf: коллекции там нельзя инициализировать —
    // ModuleInvoke.Init популейтит JSON поверх готового инстанса, и Json.NET ДОПОЛНЯЕТ
    // заполненную коллекцию, давая дубли (грабля clientHosts, ModuleConf.cs).
    static readonly List<JObject> _defaultCanaries = new List<JObject>
    {
        // обычный фильм, широкая выдача, есть срез bitmagnet
        new JObject { ["id"] = "dune", ["title"] = "Дюна", ["title_original"] = "Dune", ["year"] = 2021, ["is_serial"] = 1, ["tmdb_id"] = "438631" },
        // сериал: сезоны, полнота серий, второй проход
        new JObject { ["id"] = "got", ["title"] = "Игра престолов", ["title_original"] = "Game of Thrones", ["year"] = 2011, ["is_serial"] = 2, ["tmdb_id"] = "1399" },
        // аниме: TMDB отдаёт tv, находится только ШИРОКИМ проходом (is_serial=0) — исторически
        // самая хрупкая ветка, ломалась молча
        new JObject { ["id"] = "onepiece", ["title"] = "Ван-Пис", ["title_original"] = "One Piece", ["year"] = 1999, ["is_serial"] = 2, ["tmdb_id"] = "37854" }
    };

    static List<JObject> Canaries()
    {
        var fromConf = ModInit.conf?.searchMonitorTitles;
        if (fromConf != null && fromConf.Count > 0)
        {
            var res = new List<JObject>();
            foreach (var s in fromConf)
            {
                try { if (JObject.Parse(s) is JObject o && o["title"] != null) res.Add(o); }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] searchmon: битая канарейка в конфиге — " + ex.Message); }
            }
            if (res.Count > 0) return res;
        }
        return _defaultCanaries;
    }
    #endregion

    #region состояние на диске
    static JObject LoadDiagState()
    {
        lock (_diagFileLock)
        {
            try { // System.IO.File явно: в контроллере File — это ControllerBase.File(byte[], string)
                if (System.IO.File.Exists(DiagStatePath)) return JObject.Parse(System.IO.File.ReadAllText(DiagStatePath)); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] searchmon state read: " + ex.Message); }
            return new JObject { ["runs"] = new JArray(), ["checks"] = new JObject() };
        }
    }

    static void SaveDiagState(JObject st)
    {
        lock (_diagFileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagStatePath));
                System.IO.File.WriteAllText(DiagStatePath, st.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] searchmon state write: " + ex.Message); }
        }
    }
    #endregion

    #region оценка одной проверки
    // Машина состояний одной проверки. Уведомление отдаётся ТОЛЬКО на переходе ok→fail
    // (после набора стрика) и один раз на fail→ok. Внутри состояния — тишина.
    // Возвращает текст уведомления или null.
    internal static string EvalCheck(JObject checks, string id, bool failed, int needStreak, string failText, string okText, int cooldownHours, DateTime now)
    {
        var c = checks[id] as JObject;
        if (c == null) { c = new JObject { ["state"] = "ok", ["streak"] = 0, ["incident"] = 0 }; checks[id] = c; }

        string state = c.Value<string>("state") ?? "ok";
        int streak = c.Value<int?>("streak") ?? 0;

        if (failed)
        {
            streak++;
            c["streak"] = streak;
            if (state == "fail" || streak < needStreak) return null;   // уже сообщили / стрик не набран

            // Кулдаун поверх перехода — защита от «мигания» на границе критерия.
            // ⚠️ Считается от последней тревоги О ПОЛОМКЕ, а не от любого уведомления: иначе
            // сообщение «восстановилось» продлевало бы окно тишины и система на cooldownHours
            // слепла бы к НОВОЙ поломке.
            var lastFail = c.Value<DateTime?>("lastFail");
            if (lastFail != null && cooldownHours > 0 && (now - lastFail.Value).TotalHours < cooldownHours) return null;

            c["state"] = "fail";
            c["incident"] = (c.Value<int?>("incident") ?? 0) + 1;
            c["lastFail"] = now;
            c["lastNotify"] = now;
            return failText;
        }

        c["streak"] = 0;
        if (state != "fail") return null;
        c["state"] = "ok";
        c["lastNotify"] = now;   // намеренно НЕ трогаем lastFail — см. выше
        return okText;
    }

    // Медиана по истории удачных прогонов. Меньше трёх точек — базы нет, относительное
    // правило обязано молчать (иначе на холодном старте оно сработает на пустом месте).
    internal static int MedianOrZero(List<int> values)
    {
        if (values == null || values.Count < 3) return 0;
        var s = values.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }
    #endregion

    #region прогон
    public class DiagResult
    {
        public bool skipped; public string skipReason;
        public JObject report = new JObject();
        public int created;
    }

    // dry: не писать состояние и не создавать уведомлений (ручной прогон для сверки).
    public static async Task<DiagResult> SearchMonitorTick(bool dry = false)
    {
        var res = new DiagResult();

        if (!await _diagGate.WaitAsync(0))
        {
            res.skipped = true; res.skipReason = "занят другой прогон";
            return res;
        }

        try
        {
            // Пропуск ≠ провал. Тик в разогревочном окне или поверх занятой охоты просто пропадает
            // и НЕ двигает стрики — иначе рестарт контейнера сам себе присылал бы тревогу.
            if (!dry && (DateTime.UtcNow - _diagBootAt).TotalMinutes < DiagWarmupMinutes)
            {
                res.skipped = true; res.skipReason = "разогрев после старта";
                return res;
            }
            if (!dry && _watchGate.CurrentCount == 0)
            {
                res.skipped = true; res.skipReason = "занят фоновый контур (watch/notify/hunt)";
                return res;
            }

            var canaries = Canaries();
            var report = new JObject();
            var trackersSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int indexerOkCount = 0, starCount = 0, totalRows = 0;

            for (int i = 0; i < canaries.Count; i++)
            {
                var c = canaries[i];
                string cid = c.Value<string>("id") ?? ("c" + i);
                string title = c.Value<string>("title");
                var sw = System.Diagnostics.Stopwatch.StartNew();

                JArray list = null;
                try
                {
                    list = await SearchScored(title, title, c.Value<string>("title_original"),
                                              c.Value<int?>("year") ?? 0, c.Value<int?>("is_serial") ?? 1,
                                              0, ModInit.conf?.indexerApikey, c.Value<string>("tmdb_id"));
                }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] searchmon «" + title + "»: " + ex.Message); }

                sw.Stop();
                int total = list?.Count ?? 0;
                bool star = list != null && list.OfType<JObject>().Any(x => x.Value<bool?>("rec") == true);
                var tr = new JObject();
                if (list != null)
                {
                    foreach (var g in list.OfType<JObject>().GroupBy(x => x.Value<string>("tracker") ?? "?"))
                    {
                        tr[g.Key] = g.Count();
                        // склейки вида "kinozal, rutracker" разбираем — иначе трекер «пропадёт»
                        foreach (var part in g.Key.Split(','))
                            if (!string.IsNullOrWhiteSpace(part)) trackersSeen.Add(part.Trim());
                    }
                }

                if (total > 0) indexerOkCount++;
                if (star) starCount++;
                totalRows += total;

                report[cid] = new JObject { ["total"] = total, ["starred"] = star, ["ms"] = sw.ElapsedMilliseconds, ["trackers"] = tr };

                // канарейки последовательно и с паузой — не наваливаем на трекеры веером
                if (i < canaries.Count - 1) await Task.Delay(TimeSpan.FromSeconds(8));
            }

            res.report = report;

            var st = LoadDiagState();
            var runs = st["runs"] as JArray ?? new JArray();
            var checks = st["checks"] as JObject ?? new JObject();
            var now = DateTime.UtcNow;
            var conf = ModInit.conf;
            int baselineRuns = Math.Max(3, conf?.searchMonitorBaselineRuns ?? 10);
            int needStreak = Math.Max(1, conf?.searchMonitorFailStreak ?? 3);
            int cooldown = Math.Max(0, conf?.searchMonitorCooldownHours ?? 12);
            var alerts = new List<string>();

            // ── 1. индексатор целиком ──
            bool indexerDown = indexerOkCount == 0;
            string a = EvalCheck(checks, "indexer", indexerDown, 1,
                "Индексатор не отвечает — поиск раздач возвращает пусто",
                "Индексатор снова отвечает", cooldown, now);
            if (a != null) alerts.Add(a);

            // ── 2. объём по каждой канарейке ──
            foreach (var c in canaries)
            {
                string cid = c.Value<string>("id") ?? "?";
                int total = report[cid]?.Value<int?>("total") ?? 0;
                var hist = runs.OfType<JObject>()
                               .Select(r => (r["canary"]?[cid] as JObject)?.Value<int?>("total") ?? -1)
                               .Where(v => v > 0).ToList();
                int med = MedianOrZero(hist);
                int minRes = Math.Max(1, conf?.searchMonitorMinResults ?? 10);
                int dropPct = Math.Max(1, conf?.searchMonitorDropPercent ?? 60);

                // Двойной критерий: абсолютный пол И падение к медиане. Одного абсолютного мало
                // (для редкого тайтла 8 раздач — норма), одного относительного тоже (на холодном
                // старте медианы нет). Относительное правило спит, пока med == 0.
                bool fail = total == 0 || (total < minRes && med > 0 && total < med * dropPct / 100);
                a = EvalCheck(checks, "canary:" + cid, fail, needStreak,
                    "Поиск «" + c.Value<string>("title") + "»: " + total + " раздач" + (med > 0 ? " (обычно ~" + med + ")" : ""),
                    "Поиск «" + c.Value<string>("title") + "» восстановился", cooldown, now);
                if (a != null) alerts.Add(a);
            }

            // ── 3. пропавшие трекеры ──
            if (conf?.searchMonitorTrackers != false && !indexerDown)
            {
                var okRuns = runs.OfType<JObject>().Where(r => r.Value<bool?>("ok") == true).ToList();
                if (okRuns.Count >= 3)
                {
                    var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in okRuns)
                        foreach (var t in (r["trackers"] as JArray)?.Select(x => x.Value<string>()) ?? Enumerable.Empty<string>())
                            if (!string.IsNullOrWhiteSpace(t)) freq[t] = freq.TryGetValue(t, out int n) ? n + 1 : 1;

                    // только трекеры с историей: временно выключенный в init.conf иначе дал бы тревогу
                    foreach (var kv in freq.Where(k => k.Value * 100 / okRuns.Count >= 80))
                    {
                        bool gone = !trackersSeen.Contains(kv.Key);
                        a = EvalCheck(checks, "tracker:" + kv.Key, gone, needStreak,
                            "Трекер " + kv.Key + " пропал из выдачи",
                            "Трекер " + kv.Key + " снова в выдаче", cooldown, now);
                        if (a != null) alerts.Add(a);
                    }
                }
            }

            // ── 4. скоринг: раздачи есть, а ⭐ нет ни одной ──
            a = EvalCheck(checks, "stars", starCount == 0 && totalRows > 0, needStreak,
                "Раздачи находятся, но ⭐ не назначается — проверь скоринг",
                "⭐ снова назначается", cooldown, now);
            if (a != null) alerts.Add(a);

            if (!dry)
            {
                runs.Add(new JObject
                {
                    ["at"] = now,
                    ["ok"] = !indexerDown,
                    ["canary"] = report,
                    ["trackers"] = new JArray(trackersSeen.OrderBy(x => x))
                });
                while (runs.Count > baselineRuns) runs.RemoveAt(0);
                st["runs"] = runs; st["checks"] = checks;
                SaveDiagState(st);

                foreach (var text in alerts)
                {
                    Console.WriteLine("[QbitDownload] searchmon: " + text);
                    // qdl 2.111: в журнал владельца тревоги идут ВСЕГДА, независимо от
                    // searchMonitorNotify — тот гейт был про показ зрителю, а зритель
                    // диагностику поиска больше не видит вовсе. Дедуп на 12 ч: у самих
                    // проверок кулдаун свой, но перезапуск модуля его обнуляет.
                    if (NotiRoute.Enabled && !QdlEvents.Recent(QdlEvents.CatDiag, text, TimeSpan.FromHours(12)))
                        QdlEvents.Log(QdlEvents.CatDiag, "Поиск раздач", text, key: text);
                }

                if (conf?.searchMonitorNotify == true && alerts.Count > 0 && !NotiRoute.Enabled)
                {
                    res.created = AddDiagNotifications(checks, alerts);
                    if (res.created > 0) PushNotiSignal(res.created);
                }
            }

            res.report["_alerts"] = new JArray(alerts);
            res.report["_trackers"] = new JArray(trackersSeen.OrderBy(x => x));
            return res;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] searchmon: " + ex);
            return res;
        }
        finally { _diagGate.Release(); }
    }

    // Уведомления кладём в СУЩЕСТВУЮЩУЮ таблицу noti без новых полей (новая таблица в SQLite
    // не создалась бы: EnsureCreated() не добавляет таблицы в существующую БД).
    // Дедуп бесплатный: UNIQUE (seriesKey, epkey), где epkey = "<check>:<номер инцидента>".
    // ⚠️ В таблицу seen НЕ пишем — это база отсечения серий, ей тут не место.
    static int AddDiagNotifications(JObject checks, List<string> alerts)
    {
        int created = 0;
        try
        {
            using var db = new SqlContext();
            for (int i = 0; i < alerts.Count; i++)
            {
                string epkey = "diag:" + DateTime.UtcNow.ToString("yyyyMMddHHmm") + ":" + i;
                if (db.noti.Any(x => x.seriesKey == "diag" && x.epkey == epkey)) continue;
                db.noti.Add(new NotiModel
                {
                    seriesKey = "diag", seriesId = 0, hash = "", title = "Поиск раздач",
                    season = -1, episode = -1, kind = "DIAG", epkey = epkey,
                    label = alerts[i], created = DateTime.UtcNow, read = false
                });
                created++;
            }
            if (created > 0) db.SaveChanges();
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] searchmon noti: " + ex.Message); }
        return created;
    }
    #endregion

    #region ручки
    [HttpGet, AllowAnonymous]
    [Route("qdl/diag/search")]
    async public Task<ActionResult> DiagSearch(int dry = 0)
    {
        var r = await SearchMonitorTick(dry == 1);
        return ContentTo(new JObject
        {
            ["success"] = !r.skipped,
            ["skipped"] = r.skipped,
            ["reason"] = r.skipReason,
            ["created"] = r.created,
            ["report"] = r.report
        }.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/diag/state")]
    public ActionResult DiagState()
    {
        var st = LoadDiagState();

        // Какие анонс-хосты реально встречаются в магнетах. Это данные для решения «включать ли
        // sanitizeMagnetTrackers»: список из знакомых публичных трекеров — резать не спешим,
        // посторонние адреса — повод включить. Счётчик в памяти, обнуляется рестартом.
        st["magnetAnnounce"] = AnnounceSnapshot();

        return ContentTo(st.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }
    #endregion
}
