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

// ─────────────────────────────────────────────────────────────────────────────
// Слежение за новыми сериями XSMART.
//
// 🔴 ТРЕБОВАНИЕ ВЛАДЕЛЬЦА (то же, что у jut.su): за сериями этих тайтлов в ТОРРЕНТЫ не ходим.
// Держится теми же тремя поясами:
//   Пояс 1 (структурный). Торрентная охота EpisodeHunter.HuntAll итерирует ИСКЛЮЧИТЕЛЬНО
//     /qdl-data/watch.json. Подписки XSMART живут в ОТДЕЛЬНОМ файле /qdl-data/xsmart/watch.json.
//     Добавить такой тайтл в торрентное слежение невозможно КОДОМ: WatchAdd требует
//     links/<hash>.json, а XsmartGrab его не создаёт никогда.
//   Пояс 2. IndexCrawler.TargetsFromMeta пропускает меты с "source":"xsmart".
//   Пояс 3. Тесты XsmartIsolationTests.
//
// Единица слежения — (тайтл, СЕЗОН), тик раз в сутки.
//
// ДВА РЕЖИМА, режим живёт в поле autoGrab записи:
//   "notify" (autoGrab:false) — включается с экрана карточки тайтла: уведомляем о новых
//        сериях и не качаем ничего. Смысл: следить за сериалом, которого нет на диске.
//   "grab"   (autoGrab:true)  — включается из «Загрузок» (то есть на уже скачанном):
//        уведомляем И качаем новые серии сами.
// Режим гейтит ИСКЛЮЧИТЕЛЬНО постановку в очередь: уведомления и продвижение baseline
// одинаковы в обоих режимах.
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region состояние

    static readonly SemaphoreSlim _xsWatchGate = new SemaphoreSlim(1, 1);
    static readonly object _xsWatchLock = new();

    internal static string XsmartWatchPath() => Path.Combine(XsmartNet.DataDir(), "watch.json");

    internal static JArray XsmartLoadWatch()
    {
        try
        {
            string p = XsmartWatchPath();
            if (System.IO.File.Exists(p)) return JArray.Parse(System.IO.File.ReadAllText(p));
        }
        catch (Exception ex) { XsmartNet.Log("watch", "чтение: " + ex.Message); }
        return new JArray();
    }

    internal static void XsmartSaveWatch(JArray arr)
    {
        try
        {
            Directory.CreateDirectory(XsmartNet.DataDir());
            System.IO.File.WriteAllText(XsmartWatchPath(), arr.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception ex) { XsmartNet.Log("watch", "запись: " + ex.Message); }
    }

    static JObject XsmartFindWatch(JArray arr, string sref)
        => arr.OfType<JObject>().FirstOrDefault(x =>
               string.Equals(x.Value<string>("ref"), sref, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Режим подписки: "grab" — новые серии качаем сами, "notify" — только уведомляем.
    /// Поля нет → "grab": такие записи создавались, когда автоскачивание было единственным
    /// поведением, и трактовать их как "notify" значило бы молча выключить скачивание
    /// у живых подписок — без единой строки в логе.
    /// </summary>
    internal static string XsmartModeOf(JObject rec)
        => (rec?.Value<bool?>("autoGrab") ?? true) ? "grab" : "notify";

    /// <summary>
    /// Режим для записи: явный параметр UI > уже сохранённый режим > дефолт конфига.
    /// Чистая функция отдельно от роута: ломалась именно эта развилка — повторный вызов без
    /// параметра затирал режим дефолтом конфига, то есть молча включал автоскачивание.
    /// </summary>
    internal static bool XsmartAutoGrabFor(bool? prev, int autoGrab)
        => autoGrab >= 0 ? autoGrab == 1 : (prev ?? (ModInit.conf?.xsmartWatchAutoGrab ?? true));

    /// <summary>ref → режим подписки, для отметки карточек в /qdl/list. Читается ОДИН раз на запрос.</summary>
    internal static Dictionary<string, string> XsmartWatchModes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in XsmartLoadWatch().OfType<JObject>())
        {
            string s = x.Value<string>("ref");
            if (!string.IsNullOrEmpty(s)) map[s] = XsmartModeOf(x);
        }
        return map;
    }

    /// <summary>
    /// Удалили загрузку — снимаем и подписку.
    /// 🔴 Без этого при автоскачивании следующая серия молча пересоздала бы карточку и папку:
    /// «удалил, а оно вернулось». PurgeCache про отдельный файл подписок не знает.
    /// </summary>
    internal static void XsmartForgetOnDelete(string sref)
    {
        if (string.IsNullOrEmpty(sref)) return;

        // ⚠️ Снятие подписки условное, а уборка очереди — НЕТ. Раньше здесь стоял ранний
        // return по «подписки нет», и у тайтла без подписки (обычный случай: скачали руками,
        // следить не просили) удаление карточки не снимало ни ключей очереди, ни поколения.
        // С персистентным журналом это стало дороже: пачка воскресала после рестарта —
        // ровно тот «удалил, а оно вернулось», ради которого метод и написан.
        bool had;
        lock (_xsWatchLock)
        {
            var arr = XsmartLoadWatch();
            var rec = XsmartFindWatch(arr, sref);
            had = rec != null;
            if (had) { rec.Remove(); XsmartSaveWatch(arr); }
        }
        lock (_xsEnqLock)
        {
            _xsGen[sref] = XsmartGenOf(sref) + 1;   // всё поставленное этим тайтлом — устарело
            foreach (string k in _xsQueued.Where(k => k.StartsWith(sref + ":", StringComparison.Ordinal)).ToList())
                _xsQueued.Remove(k);
            // 🔴 И намерения тоже — иначе восстановление на старте вернуло бы удалённое.
            XsmartWantsDropTitle(sref);
        }
        XsmartDropDiskKeys(sref);
        if (had) XsmartNet.Log("watch", "подписка снята вместе с загрузкой: " + sref);
    }

    #endregion

    #region подписка

    internal sealed class XsmartWatchUpsertResult
    {
        public string seasonId;
        public int seasonNo;
        public int baseline;    // сколько серий сезона уже вышло — их НЕ качаем
        public string mode;     // "grab" | "notify"
        public bool created;
    }

    /// <summary>
    /// Создать или обновить подписку.
    /// ⚠️ Сбрасывает baseline на ТЕКУЩЕЕ состояние источника, поэтому для смены режима
    /// существующей подписки НЕ годится (для этого XsmartWatchSetModeOnDisk): иначе серия,
    /// вышедшая между тиком и нажатием, уходит в baseline, и в режиме «качаю» её уже никто
    /// не скачает.
    /// </summary>
    internal static XsmartWatchUpsertResult XsmartWatchUpsert(XsmartTitle t, string seasonId, int autoGrab)
    {
        var eps = t.items.Where(e => e.kind == XsmartKind.Episode).ToList();
        // Сезон не назвали — берём ПОСЛЕДНИЙ вышедший: новые серии бывают только там.
        string sid = !string.IsNullOrEmpty(seasonId) && eps.Any(e => e.seasonId == seasonId)
            ? seasonId
            : eps.OrderByDescending(e => e.seasonNo).FirstOrDefault()?.seasonId;

        var inSeason = eps.Where(e => e.seasonId == sid).ToList();
        int sno = inSeason.Count > 0 ? inSeason[0].seasonNo : 1;
        string sref = XsmartNet.Ref(t.cat, t.id);

        lock (_xsWatchLock)
        {
            var arr = XsmartLoadWatch();
            var rec = XsmartFindWatch(arr, sref);
            bool? prevAuto = rec?.Value<bool?>("autoGrab");   // снять ДО создания записи
            bool created = rec == null;
            if (created) { rec = new JObject { ["ref"] = sref }; arr.Add(rec); }

            rec["cat"] = t.cat;
            rec["id"] = t.id;
            rec["seasonId"] = sid;
            rec["seasonNo"] = sno;
            rec["titleRu"] = t.title ?? sref;
            rec["source"] = t.source;
            rec["autoGrab"] = XsmartAutoGrabFor(prevAuto, autoGrab);
            // Baseline: «Следить» качает только БУДУЩИЕ серии. Уже вышедшее — кнопкой «Скачать сезон».
            rec["known"] = new JObject
            {
                ["count"] = inSeason.Count,
                ["max"] = inSeason.Count > 0 ? inSeason.Max(e => e.epNo) : 0,
                ["keys"] = new JArray(inSeason.Select(e => e.epkey))
            };
            rec["lastChange"] = DateTime.UtcNow;
            rec["fails"] = 0;
            XsmartSaveWatch(arr);

            return new XsmartWatchUpsertResult
            {
                seasonId = sid, seasonNo = sno, baseline = inSeason.Count,
                mode = XsmartModeOf(rec), created = created
            };
        }
    }

    /// <summary>
    /// Подписка. autoGrab: 1 — качать новые серии («Загрузки»), 0 — только уведомлять
    /// (карточка тайтла), -1 — не указано (сохранить режим существующей записи).
    /// </summary>
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/xsmart/watch")]
    async public Task<ActionResult> XsmartWatchAdd(int cat, string id, string season = null, int autoGrab = -1)
    {
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;   // подписки живут только дома
        if (!XsmartNet.On) return XsmartErr("DISABLED");
        if (!XsmartNet.Valid(cat, id)) return XsmartErr("BAD_ID");

        var (t, err) = await XsmartTitleFor(cat, id, null);
        if (t == null) return XsmartErr(err ?? "UPSTREAM_DOWN");
        if (!t.series) return XsmartErr("NOT_FOUND", "Следить можно только за сериалом");

        var r = XsmartWatchUpsert(t, season, autoGrab);

        return XsmartJson(new JObject
        {
            ["ok"] = true, ["ref"] = XsmartNet.Ref(cat, id),
            ["season"] = r.seasonId, ["seasonNo"] = r.seasonNo,
            ["baseline"] = r.baseline,
            ["mode"] = r.mode, ["autoGrab"] = r.mode == "grab",
            ["message"] = r.mode == "grab"
                ? $"Слежу за сезоном {r.seasonNo}: новые серии буду качать сам. Уже вышедшие {r.baseline} не качаю — для них кнопка «Скачать»."
                : $"Слежу за сезоном {r.seasonNo}: сообщу о новых сериях, качать не буду. Скачивание включается в «Загрузках»."
        });
    }

    /// <summary>
    /// Сменить режим существующей подписки. Отдельный роут, а не повторная подписка,
    /// по двум причинам: (1) не трогает baseline — иначе серия, вышедшая между тиком и
    /// нажатием, была бы проглочена; (2) не ходит в сеть — «выключить качание» обязано
    /// работать, когда XSMART лежит.
    /// </summary>
    internal static bool XsmartWatchSetModeOnDisk(string sref, bool auto, out string mode, out int seasonNo)
    {
        mode = null; seasonNo = 0;
        lock (_xsWatchLock)
        {
            var arr = XsmartLoadWatch();
            var rec = XsmartFindWatch(arr, sref);
            if (rec == null) return false;
            rec["autoGrab"] = auto;
            rec["lastChange"] = DateTime.UtcNow;
            mode = XsmartModeOf(rec);
            seasonNo = rec.Value<int?>("seasonNo") ?? 1;
            XsmartSaveWatch(arr);
        }
        XsmartNet.Log("watch", "режим " + sref + " → " + mode);
        return true;
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/xsmart/watch/mode")]
    public ActionResult XsmartWatchMode(int cat, string id, int autoGrab = -1)
    {
        if (!XsmartNet.Valid(cat, id)) return XsmartErr("BAD_ID");
        if (autoGrab != 0 && autoGrab != 1) return XsmartErr("BAD_MODE");
        if (!XsmartWatchSetModeOnDisk(XsmartNet.Ref(cat, id), autoGrab == 1, out string mode, out int sno))
            return XsmartErr("NOT_WATCHED");

        return XsmartJson(new JObject
        {
            ["ok"] = true, ["ref"] = XsmartNet.Ref(cat, id), ["seasonNo"] = sno,
            ["mode"] = mode, ["autoGrab"] = mode == "grab",
            ["message"] = mode == "grab"
                ? "Новые серии буду качать сам. Уже вышедшие — кнопкой «Скачать»."
                : "Только уведомления: новые серии больше не качаются."
        });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/xsmart/watch/remove")]
    public ActionResult XsmartWatchRemove(int cat, string id)
    {
        if (!XsmartNet.Valid(cat, id)) return XsmartErr("BAD_ID");
        string sref = XsmartNet.Ref(cat, id);
        lock (_xsWatchLock)
        {
            var arr = XsmartLoadWatch();
            var rec = XsmartFindWatch(arr, sref);
            if (rec != null) { rec.Remove(); XsmartSaveWatch(arr); }
        }
        return XsmartJson(new JObject { ["ok"] = true, ["message"] = "Слежение выключено" });
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/xsmart/watch/list")]
    public ActionResult XsmartWatchList()
    {
        var arr = XsmartLoadWatch();
        return XsmartJson(new JObject
        {
            ["ok"] = true,
            ["items"] = new JArray(arr.OfType<JObject>().Select(x => new JObject
            {
                ["ref"] = x.Value<string>("ref"),
                ["cat"] = x.Value<int?>("cat") ?? 0,
                ["id"] = x.Value<string>("id"),
                ["season"] = x.Value<string>("seasonId"),
                ["seasonNo"] = x.Value<int?>("seasonNo") ?? 1,
                ["titleRu"] = x.Value<string>("titleRu"),
                ["mode"] = XsmartModeOf(x),
                ["autoGrab"] = x.Value<bool?>("autoGrab") ?? true,
                ["known"] = x.Value<JObject>("known")?.Value<int?>("count") ?? 0,
                ["fails"] = x.Value<int?>("fails") ?? 0
            }))
        });
    }

    #endregion

    #region суточный тик

    [HttpGet, AllowAnonymous]
    [Route("qdl/xsmart/watch/check")]
    async public Task<ActionResult> XsmartWatchCheck()
    {
        var res = await XsmartWatchTick(manual: true);
        return XsmartJson(res);
    }

    /// <summary>Сезоны тайтла одним запросом — отдельно, чтобы тик не тянул серии всех сезонов.</summary>
    static async Task<List<(string id, int number)>> XsmartLoadSeasons(int cat, string id, string source)
    {
        var res = new List<(string, int)>();
        string q = string.IsNullOrEmpty(source) ? "" : "?source=" + Uri.EscapeDataString(source);
        var sj = await XsmartNet.GetJson("/xsmart/seasons/" + cat + "/" + Uri.EscapeDataString(id) + q);
        if (XsmartNet.ErrOf(sj) != null) return null;              // null = «не спросили», а не «сезонов нет»
        if (sj["seasons"] is not JArray arr) return res;

        int n = 0;
        foreach (var s in arr.OfType<JObject>())
        {
            string sid = s.Value<string>("id");
            if (string.IsNullOrEmpty(sid)) continue;
            n++;
            res.Add((sid, s.Value<int?>("number") ?? n));
        }
        return res;
    }

    /// <summary>
    /// Тик слежения. Раз в сутки (решение владельца) + вручную через /qdl/xsmart/watch/check.
    ///
    /// Цена прохода — 2 запроса к прокси на тайтл (сезоны + серии отслеживаемого сезона), а не
    /// полный обход карточки: он стоил бы 2 + N и на сериале с 8 сезонами превращал суточный
    /// тик в десяток лишних запросов ради сезонов, за которыми никто не следит.
    ///
    /// loadSeasons/loadEpisodes — точки подмены сети (null = реальные запросы): у XsmartNet своя
    /// фабрика HttpClient без места под HttpMessageHandler, а именно в тике живут оба режима,
    /// переключение сезона и защита от повторных уведомлений.
    /// </summary>
    internal static async Task<JObject> XsmartWatchTick(
        bool manual = false,
        Func<int, string, string, Task<List<(string id, int number)>>> loadSeasons = null,
        Func<int, string, string, int, string, Task<List<XsmartEp>>> loadEpisodes = null)
    {
        loadSeasons ??= XsmartLoadSeasons;
        loadEpisodes ??= ((cat, id, sid, sno, src) => XsmartNet.LoadEpisodes(cat, id, sid, sno, src));

        var res = new JObject { ["ok"] = true };
        if (!XsmartNet.On) { res["skipped"] = "disabled"; return res; }

        // Ручная проверка не должна глушить суточный тик и наоборот: гейт без ожидания.
        if (!await _xsWatchGate.WaitAsync(0))
        {
            res["skipped"] = "занят другой проход";
            return res;
        }

        try
        {
            // Свип долгов — до цикла и без единого сетевого запроса. Страховка на случай,
            // когда воркер умер тихо, а до следующего рестарта далеко.
            try { res["swept"] = XsmartWantsSweep(); } catch { }

            var arr = XsmartLoadWatch();
            var recs = arr.OfType<JObject>().ToList();
            if (recs.Count == 0) { res["watched"] = 0; return res; }

            int probed = 0, changed = 0, queued = 0, failed = 0;

            foreach (var rec in recs)
            {
                if (!XsmartNet.On) break;
                int cat = rec.Value<int?>("cat") ?? 0;
                string id = rec.Value<string>("id");
                if (!XsmartNet.Valid(cat, id)) continue;

                string sref = XsmartNet.Ref(cat, id);
                string source = rec.Value<string>("source");
                string seasonId = rec.Value<string>("seasonId");
                int seasonNo = rec.Value<int?>("seasonNo") ?? 1;
                string titleRu = rec.Value<string>("titleRu");
                probed++;

                var seasons = await loadSeasons(cat, id, source);
                if (seasons == null)
                {
                    failed++;
                    rec["fails"] = (rec.Value<int?>("fails") ?? 0) + 1;
                    continue;
                }
                rec["fails"] = 0;

                // Вышел сезон СТАРШЕ отслеживаемого → уведомление + автопереключение.
                var newest = seasons.OrderByDescending(s => s.number).FirstOrDefault();
                bool switched = false;
                if (newest.id != null && newest.number > seasonNo
                    && (ModInit.conf?.xsmartWatchSeasonSwitch ?? true))
                {
                    XsmartNotifySeason(sref, cat, id, titleRu, newest.number);
                    seasonId = newest.id;
                    seasonNo = newest.number;
                    rec["seasonId"] = seasonId;
                    rec["seasonNo"] = seasonNo;
                    switched = true;
                    changed++;
                }

                var eps = await loadEpisodes(cat, id, seasonId, seasonNo, source);
                if (eps == null || eps.Count == 0)
                {
                    failed++;
                    rec["fails"] = (rec.Value<int?>("fails") ?? 0) + 1;
                    continue;
                }

                if (switched)
                {
                    // ⚠️ Baseline нового сезона = его ТЕКУЩЕЕ состояние, а НЕ пусто. Пустой
                    // baseline означает «все уже вышедшие серии нового сезона — новые», и
                    // подписка на сезон 1 мгновенно выкачивала бы весь сезон 3 (на jut это
                    // стоило 13 серий ≈ 6 ГБ одним тиком). Политика «Следить качает только
                    // БУДУЩИЕ серии» обязана переживать переключение.
                    rec["known"] = XsmartBaseline(eps);
                    rec["lastChange"] = DateTime.UtcNow;
                    continue;
                }

                var knownKeys = new HashSet<string>(
                    (rec.Value<JObject>("known")?["keys"] as JArray ?? new JArray())
                        .Select(x => x.Value<string>()).Where(x => x != null), StringComparer.Ordinal);

                bool grab = XsmartModeOf(rec) == "grab";

                var fresh = eps.Where(e => !knownKeys.Contains(e.epkey)).ToList();

                // 🔴 НЕПОГАШЕННЫЙ ДОЛГ — вторая причина не выходить отсюда. Гейт «нет новых →
                // выходим» существует, чтобы бэклог не поехал в очередь, и снимать его нельзя:
                // у сериала, где скачаны 2 сезона из 5, сверка с диском потянула бы всё.
                // Но серия, поставленная прошлым тиком и потерянная рестартом, уже сидит
                // в baseline и в fresh не попадёт НИКОГДА — до этой правки она пропадала молча.
                // Долг берём ИСКЛЮЧИТЕЛЬНО из журнала намерений: бэклога там нет по построению,
                // записи создаются только явным действием владельца или прошлым тиком.
                var owed = grab ? XsmartWantsOwedEps(sref) : new List<XsmartEp>();
                if (fresh.Count == 0 && owed.Count == 0) continue;
                if (fresh.Count > 0) changed++;

                // Снимок диска берём ОДИН раз на тайтл.
                var diskKeys = XsmartDiskKeys(sref);
                var toGrab = grab
                    ? fresh.Concat(owed)
                           .GroupBy(e => e.epkey, StringComparer.Ordinal).Select(g => g.First())
                           .Where(e => e.playable && !diskKeys.Contains(e.epkey)).ToList()
                    : new List<XsmartEp>();

                // ⚠️ Уведомляем ТОЛЬКО о fresh. Долг уже уведомляли при первой постановке —
                // повтор означал бы строку в ленте каждые сутки, пока портал лежит.
                if (fresh.Count > 0) XsmartNotifyNew(sref, cat, id, titleRu, fresh, grab);

                bool baselineHold = false;
                if (toGrab.Count > 0)
                {
                    string spaceErr = XsmartCheckSpace(toGrab.Count + _xsQueue.Count, sref);
                    if (spaceErr != null)
                    {
                        XsmartNotifyNoSpace(sref, cat, id, titleRu, spaceErr);
                        // 🔴 Baseline НЕ двигаем. Раньше он уезжал вперёд и здесь: серия
                        // исключалась из fresh навсегда, в очередь не попадала, .part на диске
                        // не оставляла — и терялась насовсем. У jut это давно закрыто, у XSMART
                        // канал был открыт.
                        baselineHold = true;
                    }
                    else queued += XsmartEnqueueWatched(cat, id, sref, source, titleRu, toGrab);
                }

                // Baseline двигаем в ОБОИХ режимах и ПОСЛЕ постановки: иначе «только уведомляю»
                // сообщал бы об одной и той же серии каждые сутки.
                if (!baselineHold)
                {
                    rec["known"] = XsmartBaseline(eps);
                    rec["lastChange"] = DateTime.UtcNow;
                }
            }

            // ⚠️ Штамп прохода — только если хоть один тайтл реально опросили. Безусловный
            // превратил бы «сеть лежит» в «новых серий нет» и убил бы догон (см. JutWatchOverdue).
            if (probed > 0) XsmartStampRun(arr);

            XsmartSaveWatch(arr);
            res["watched"] = recs.Count;
            res["probed"] = probed;
            res["changed"] = changed;
            res["queued"] = queued;
            res["failed"] = failed;
            res["manual"] = manual;
            return res;
        }
        catch (Exception ex)
        {
            XsmartNet.Log("watch", "тик: " + ex.Message);
            res["ok"] = false;
            res["error"] = ex.Message;
            return res;
        }
        finally { _xsWatchGate.Release(); }
    }

    internal static JObject XsmartBaseline(List<XsmartEp> eps) => new JObject
    {
        ["count"] = eps.Count,
        ["max"] = eps.Count > 0 ? eps.Max(e => e.epNo) : 0,
        ["keys"] = new JArray(eps.Select(e => e.epkey))
    };

    /// <summary>Отметка «проход состоялся». Единственный источник данных для догона.</summary>
    static void XsmartStampRun(JArray arr)
    {
        var now = DateTime.UtcNow;
        foreach (var rec in arr.OfType<JObject>()) rec["lastRun"] = now;
    }

    /// <summary>
    /// Догон пропущенных тиков — копия JutWatchOverdue. При суточном такте это обязательно:
    /// без него каждый рестарт контейнера сдвигает проверку на новые сутки, и при частых
    /// рестартах слежение не срабатывает вообще. У XSMART этого не было вовсе, а вместе
    /// с ним не было и самого поля lastRun.
    /// </summary>
    internal static bool XsmartWatchOverdue(TimeSpan period, out TimeSpan since)
    {
        since = TimeSpan.Zero;
        try
        {
            DateTime? last = null;
            foreach (var rec in XsmartLoadWatch().OfType<JObject>())
            {
                var v = rec.Value<DateTime?>("lastRun");
                if (v != null && (last == null || v > last)) last = v;
            }
            if (last == null) return false;
            since = DateTime.UtcNow - last.Value;
            return since > period * 1.5;
        }
        catch { return false; }
    }

    static int XsmartEnqueueWatched(int cat, string id, string sref, string source, string titleRu,
                                    List<XsmartEp> toGrab)
    {
        // 🔴 ФАЗА 1 — намерение на диск ДО постановки и, что важнее, ДО сдвига baseline.
        // Тогда после падения либо намерение на диске (восстановимо), либо baseline не
        // сдвинулся и следующий тик снова увидит серию как fresh. Третьего состояния нет.
        XsmartWantsCommit(sref, cat, id, source, titleRu, toGrab, "watch");

        bool freshBatch = XsmartPendingFor(sref) == 0;
        int put = 0;
        lock (_xsEnqLock)
        {
            int gen = XsmartGenOf(sref);
            foreach (var e in toGrab)
            {
                if (!_xsQueued.Add(XsmartQueueKey(sref, e.epkey))) continue;
                _xsQueue.Enqueue(new XsmartGrabItem
                {
                    cat = cat, id = id, sref = sref, source = source,
                    ep = e, titleRu = titleRu, gen = gen
                });
                put++;
            }
        }
        if (put > 0)
        {
            XsmartJobForBatch(sref, freshBatch, put);
            XsmartEnsureMetaFile(cat, id);   // карточка «в полёте» без меты ушла бы в enrich() клиента (qdl 2.114)
            XsmartKickWorker();
        }
        return put;
    }

    #endregion

    #region уведомления слежения

    // ⚠️ epkey уведомлений слежения не должен пересечься с ключами единиц (s1e7, film) и с
    // ключами пачки (start-*, batch-*): UNIQUE noti(seriesKey, epkey) схлопнул бы записи.

    static void XsmartNotifySeason(string sref, int cat, string id, string title, int seasonNo)
    {
        try
        {
            using var db = new SqlContext();
            string sk = "x" + sref;
            string epkey = "season-" + seasonNo;
            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == epkey)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0, hash = XsmartNet.Hash(cat, id),
                title = title ?? sref, season = seasonNo, episode = -1,
                kind = "SEASON", epkey = epkey,
                // «— слежу за ним» это про нас, а не про сезон (qdl 2.111)
                label = NotiRoute.Enabled ? NotiRoute.Season(seasonNo)
                                          : "Вышел сезон " + seasonNo + " — слежу за ним",
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
        }
        catch (Exception ex) { XsmartNet.Log("watch", "noti сезона: " + ex.Message); }
    }

    /// <summary>
    /// Новые серии. Одна строка на тик, а не на серию: за сутки могло выйти сразу несколько,
    /// и лента из пяти одинаковых строк — это шум, а не новость.
    /// </summary>
    static void XsmartNotifyNew(string sref, int cat, string id, string title,
                                List<XsmartEp> fresh, bool grab)
    {
        if (fresh == null || fresh.Count == 0) return;
        try
        {
            using var db = new SqlContext();
            string sk = "x" + sref;
            // Ключ дедупа — по МАКСИМАЛЬНОЙ новой серии: повторный тик с тем же результатом
            // (например, после ошибки записи baseline) не создаст вторую строку.
            string epkey = "new-s" + fresh[0].seasonNo + "e" + fresh.Max(e => e.epNo);

            string label = NotiRoute.Enabled
                ? NotiRoute.Episodes(fresh[0].seasonNo, fresh.Select(e => e.epNo))
                : (fresh.Count == 1
                    ? $"Новая серия: сезон {fresh[0].seasonNo} · серия {fresh.Max(e => e.epNo)}"
                    : $"Новых серий: {fresh.Count}") + (grab ? "" : " (только уведомляю)");
            if (string.IsNullOrEmpty(label)) return;

            // qdl 2.111: на АВТОКАЧКЕ «вышла» зрителю не показываем — он узнает о серии,
            // когда её можно смотреть (XsmartNotifyDone). Дедуп переезжает в seen: строки
            // в ленте больше нет, а ту, что была, съедала бы ретенция.
            if (NotiRoute.Enabled && grab)
            {
                if (db.seen.Any(x => x.seriesKey == sk && x.epkey == epkey)) return;
                db.seen.Add(new SeenModel { seriesKey = sk, epkey = epkey });
                db.SaveChanges();
                QdlEvents.Log(QdlEvents.CatWatch, title ?? sref, label + " — ставлю в очередь",
                              XsmartNet.Hash(cat, id), sk, key: epkey);
                return;
            }

            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == epkey)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0, hash = XsmartNet.Hash(cat, id),
                title = title ?? sref,
                season = fresh[0].seasonNo, episode = fresh.Max(e => e.epNo),
                kind = "NEW", epkey = epkey,
                label = label,
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
        }
        catch (Exception ex) { XsmartNet.Log("watch", "noti новых: " + ex.Message); }
    }

    /// <summary>
    /// Место кончилось. Молчать здесь нельзя: подписка в режиме «качаю» просто перестала бы
    /// качать, и понять это можно было бы только по логам контейнера.
    /// </summary>
    static void XsmartNotifyNoSpace(string sref, int cat, string id, string title, string why)
    {
        try
        {
            using var db = new SqlContext();
            string sk = "x" + sref;
            string epkey = "nospace-" + DateTime.UtcNow.ToString("yyyyMMdd");
            string label = "Не качаю новые серии: " + why;

            // qdl 2.111: нехватка места — забота владельца (его выбор), дедуп по дню в seen
            if (NotiRoute.Enabled)
            {
                if (db.seen.Any(x => x.seriesKey == sk && x.epkey == epkey)) return;
                db.seen.Add(new SeenModel { seriesKey = sk, epkey = epkey });
                db.SaveChanges();
                QdlEvents.Log(QdlEvents.CatSpace, title ?? sref, label, XsmartNet.Hash(cat, id), sk, key: epkey);
                return;
            }

            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == epkey)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0, hash = XsmartNet.Hash(cat, id),
                title = title ?? sref, season = -1, episode = -1,
                kind = "NOSPACE", epkey = epkey,
                label = label,
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
        }
        catch (Exception ex) { XsmartNet.Log("watch", "noti места: " + ex.Message); }
    }

    #endregion
}
