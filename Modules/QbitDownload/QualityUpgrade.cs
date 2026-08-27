using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Апгрейд качества уже скачанного (qdl 2.77).
//
// 🔥 Зачем. Порталы выкладывают свежие серии РАНЬШЕ, чем дотранскодят высокие дорожки:
// «Телохранители» s2e11–s2e14 приехали в 360p (~130 МБ) при 720p (~530 МБ) у соседних
// серий — в момент скачивания портал отдавал единственную дорожку 360p. Дальше это
// становилось вечным: ключ серии качество НЕ различает (и это правильно — s1e5 в 720p
// и в 1080p одна и та же серия), поэтому «уже скачано» закрывало её навсегда,
// а `scope=all` отвечал «Всё уже скачано».
//
// 🔴 ТРИ ГРАБЛИ, каждая ломает механизм МОЛЧА:
//   1. Ключ диска нечувствителен к качеству → восстановление сняло бы upgrade-намерение
//      как исполненное. Закрыто оговоркой в XsmartWantsPut/JutWantsPut.
//   2. Разное качество = разное ИМЯ файла → File.Delete(dst) в качалке старую копию
//      не трогает, и на диске оказались бы обе, с одним ключом таймлайна. Закрыто явной
//      уборкой в *FinishFile, строго ПОСЛЕ успешного Move.
//   3. Портал так и не дотранскодил → бесконечная перекачка. Закрыто капом попыток
//      и перепроверкой не чаще раза в N дней.
//
// Устройство скопировано с уже работающего апгрейда постеров (JutSuPoster.cs): кеш решений
// на диске + протухание ОТРИЦАТЕЛЬНОГО решения. Положительное («потолок достигнут») живёт
// долго, отказ («портал пока отдаёт только 360p») — недолго, потому что новинки
// дотранскодят позже.
//
// Выключено по умолчанию: qualityTarget = 0 → ни одного запроса и ни одного байта.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Кеш решений «какой потолок портал давал в прошлый раз».</summary>
sealed class QualityCache
{
    readonly string _tag;
    readonly Func<string> _path;
    readonly object _lock = new();
    readonly Dictionary<string, JObject> _items = new(StringComparer.Ordinal);
    bool _loaded;

    public QualityCache(string tag, Func<string> path) { _tag = tag; _path = path; }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (JsonStore.ReadObject(_path())?["items"] is JObject items)
                foreach (var p in items.Properties())
                    if (p.Value is JObject o) _items[p.Name] = o;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] " + _tag + "/quality: чтение — " + ex.Message); }
    }

    void SaveLocked()
    {
        var items = new JObject();
        foreach (var kv in _items) items[kv.Key] = kv.Value;
        try { JsonStore.Write(_path(), new JObject { ["v"] = 1, ["items"] = items }); }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] " + _tag + "/quality: запись — " + ex.Message); }
    }

    /// <summary>Можно ли пропустить проверку: решение свежее и попытки не исчерпаны.</summary>
    public bool Skip(string key, int recheckDays, int maxUps)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (!_items.TryGetValue(key, out var o)) return false;
            if ((o.Value<int?>("ups") ?? 0) >= Math.Max(1, maxUps)) return true;   // кап — навсегда
            long at = o.Value<long?>("at") ?? 0;
            return Now - at < Math.Max(1, recheckDays) * 86400L;
        }
    }

    /// <summary>Записать исход пробы. up=true — мы поставили перекачку.</summary>
    public void Note(string key, int best, bool up)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var o = _items.TryGetValue(key, out var prev) ? prev : new JObject();
            o["best"] = best;
            o["at"] = Now;
            if (up) o["ups"] = (o.Value<int?>("ups") ?? 0) + 1;
            _items[key] = o;
            SaveLocked();
        }
    }

    public void Reset()
    {
        lock (_lock) { _items.Clear(); _loaded = false; }
    }
}

static class QualityCaches
{
    public static readonly QualityCache Xsmart =
        new QualityCache("xsmart", () => Path.Combine(XsmartNet.DataDir(), "quality.json"));
    public static readonly QualityCache Jut =
        new QualityCache("jut", () => Path.Combine(JutNet.JutDataDir(), "quality.json"));

    public static void ResetForTests() { Xsmart.Reset(); Jut.Reset(); }
}

public partial class QbitController
{
    #region общее

    static int QualityTargetXs => Math.Max(0, ModInit.conf?.xsmartQualityTarget ?? 0);
    static int QualityTargetJut => Math.Max(0, ModInit.conf?.jutQualityTarget ?? 0);
    static int QualityPerTick => Math.Max(1, ModInit.conf?.qualityPerTick ?? 20);
    static int QualityMaxUps => Math.Max(1, ModInit.conf?.qualityMaxUpgrades ?? 3);
    static int QualityRecheck => Math.Max(1, ModInit.conf?.qualityRecheckDays ?? 7);

    /// <summary>Одна строка отчёта скана.</summary>
    sealed class QualityRow
    {
        public string ep;
        public int have, best;
        public bool upgradable;
        public string note;
    }

    static JObject QualityReport(List<QualityRow> rows, int queued, double avgGb)
    {
        int up = rows.Count(r => r.upgradable);
        return new JObject
        {
            ["ok"] = true,
            ["scanned"] = rows.Count,
            ["upgradable"] = up,
            ["queued"] = queued,
            ["bytes"] = up > 0 ? $"~{Math.Round(up * avgGb, 1)} ГБ" : "0",
            ["items"] = new JArray(rows.Select(r => new JObject
            {
                ["ep"] = r.ep, ["have"] = r.have, ["best"] = r.best,
                ["upgradable"] = r.upgradable, ["note"] = r.note
            }))
        };
    }

    #endregion

    #region XSMART

    /// <summary>
    /// Разбор каталога тайтла: ключ серии → лучшее качество на диске.
    /// ⚠️ have == int.MaxValue значит файл без суффикса («Авто»/мастер) — такой апгрейду
    /// не подлежит никогда, сравнивать не с чем.
    /// </summary>
    static Dictionary<string, int> XsmartDiskQualityMap(string sref)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (string f in Directory.EnumerateFiles(XsmartTitleDir(sref), "*.mp4"))
            {
                string b = Path.GetFileNameWithoutExtension(f);
                string k = XsmartKeyFromName(b);
                if (k == null) continue;
                int q = XsmartQualityFromName(b);
                int cur = q == 0 ? int.MaxValue : q;
                if (!map.TryGetValue(k, out int old) || cur > old) map[k] = cur;
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Проба одного тайтла. Возвращает отчёт; apply=true ставит перекачку через тот же
    /// журнал намерений, поэтому апгрейд наследует всю персистентность ядра бесплатно.
    /// </summary>
    internal static async Task<JObject> XsmartQualityScanTitle(int cat, string id, int min, bool apply, int budget)
    {
        string sref = XsmartNet.Ref(cat, id);
        var rows = new List<QualityRow>();
        var disk = XsmartDiskQualityMap(sref);
        if (disk.Count == 0) return QualityReport(rows, 0, 0);

        // 🔴 Дешёвый отсев ДО единого сетевого запроса. Суточный проход идёт по десяткам
        // тайтлов, и у большинства апгрейдить нечего — дёргать за карточкой каждый из них
        // означало бы платить порталу за заведомо пустую работу (и одной сессией XSMART).
        var candidates = new List<string>();
        foreach (var kv in disk.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (kv.Value == int.MaxValue)
            { rows.Add(new QualityRow { ep = kv.Key, have = 0, best = 0, note = "«Авто», не сравниваем" }); continue; }
            if (kv.Value >= min)
            { rows.Add(new QualityRow { ep = kv.Key, have = kv.Value, best = kv.Value, note = "цель достигнута" }); continue; }
            if (QualityCaches.Xsmart.Skip(sref + ":" + kv.Key, QualityRecheck, QualityMaxUps))
            { rows.Add(new QualityRow { ep = kv.Key, have = kv.Value, best = 0, note = "решение свежее" }); continue; }
            if (candidates.Count >= budget)
            { rows.Add(new QualityRow { ep = kv.Key, have = kv.Value, best = 0, note = "бюджет проб исчерпан" }); continue; }
            candidates.Add(kv.Key);
        }
        if (candidates.Count == 0) return QualityReport(rows, 0, XsmartAvgFileGb(sref));

        string source = (LoadLocal(XsmartNet.Hash(cat, id))?["xsmart"] as JObject)?.Value<string>("source");
        var (t, _) = await XsmartTitleFor(cat, id, source);
        if (t == null) return new JObject { ["ok"] = false, ["error"] = "UPSTREAM_DOWN" };

        var toGrab = new List<XsmartEp>();
        foreach (string key in candidates)
        {
            var kv = new KeyValuePair<string, int>(key, disk[key]);
            var ep = t.items.FirstOrDefault(x => x.epkey == kv.Key);
            if (ep == null)
            { rows.Add(new QualityRow { ep = kv.Key, have = kv.Value, best = 0, note = "нет в списке источника" }); continue; }

            string ck = sref + ":" + kv.Key;
            var st = await XsmartNet.Resolve(cat, id, ep, t.source);
            // ⚠️ Резолв отдаёт максимум из того, что портал даёт СЕЙЧАС — ровно то,
            // что скачала бы обычная качалка. Отдельного контракта не нужно.
            int best = st.error == null ? st.quality : 0;
            bool up = best > kv.Value;
            rows.Add(new QualityRow
            {
                ep = kv.Key, have = kv.Value, best = best, upgradable = up,
                note = st.error ?? (up ? null : "портал пока не дотранскодил")
            });
            if (up) toGrab.Add(ep);
            // Сетевая ошибка решением не считается: иначе лежачий портал зафиксировал бы
            // «лучше нет» на неделю (та же дисциплина, что у апгрейда постеров).
            if (st.error == null) QualityCaches.Xsmart.Note(ck, best, up);
        }

        int queued = 0;
        if (apply && toGrab.Count > 0)
        {
            foreach (var g in toGrab.GroupBy(e => rows.First(r => r.ep == e.epkey).best))
                queued += XsmartWantsCommit(sref, cat, id, t.source, t.title, g, "upgrade", upgradeTo: g.Key);
            XsmartWantsSweep();
        }
        return QualityReport(rows, queued, XsmartAvgFileGb(sref));
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/xsmart/quality/scan")]
    async public Task<ActionResult> XsmartQualityScan(int cat, string id, int min = 0, int apply = 0)
    {
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;
        if (!XsmartNet.On) return XsmartErr("DISABLED");
        if (!XsmartNet.Valid(cat, id)) return XsmartErr("BAD_ID");
        int target = min > 0 ? min : QualityTargetXs;
        if (target <= 0) return XsmartErr("DISABLED", "Апгрейд качества выключен (xsmartQualityTarget = 0)");
        return XsmartJson(await XsmartQualityScanTitle(cat, id, target, apply == 1, QualityPerTick));
    }

    /// <summary>Фоновой проход по всем скачанным тайтлам XSMART. Бюджет проб общий.</summary>
    internal static async Task XsmartQualitySweep()
    {
        int target = QualityTargetXs;
        if (!XsmartNet.On || target <= 0) return;
        int budget = QualityPerTick, upgraded = 0;
        try
        {
            string root = XsmartDownloadRoot();
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (budget <= 0) break;
                var parts = Path.GetFileName(dir).Split('-');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int cat) || !XsmartNet.Valid(cat, parts[1])) continue;
                var rep = await XsmartQualityScanTitle(cat, parts[1], target, apply: true, budget: budget);
                budget -= rep.Value<int?>("scanned") ?? 0;
                upgraded += rep.Value<int?>("queued") ?? 0;
            }
            if (upgraded > 0) XsmartNet.Log("quality", "поставлено на перекачку — " + upgraded);
        }
        catch (Exception ex) { XsmartNet.Log("quality", ex.Message); }
    }

    #endregion

    #region jut.su

    static Dictionary<string, (JutEp ep, int q)> JutDiskQualityMap(string slug)
    {
        var map = new Dictionary<string, (JutEp, int)>(StringComparer.Ordinal);
        try
        {
            string dir = JutTitleDir(slug);
            if (!Directory.Exists(dir)) return map;
            foreach (string f in Directory.EnumerateFiles(dir, "*.mp4"))
            {
                string b = Path.GetFileNameWithoutExtension(f);
                var e = JutEpFromFileName(b);
                if (e == null) continue;
                e.slug = slug;
                int q = JutQualityFromName(b);
                int cur = q == 0 ? int.MaxValue : q;
                if (!map.TryGetValue(e.epkey, out var old) || cur > old.Item2) map[e.epkey] = (e, cur);
            }
        }
        catch { }
        return map;
    }

    internal static async Task<JObject> JutQualityScanTitle(string slug, int min, bool apply, int budget)
    {
        var rows = new List<QualityRow>();
        var disk = JutDiskQualityMap(slug);
        if (disk.Count == 0) return QualityReport(rows, 0, 0);

        string titleRu = LoadLocal(JutNet.Hash(slug))?["jut"]?.Value<string>("titleRu") ?? slug;
        var toGrab = new List<(JutEp ep, int best)>();
        int probes = 0;

        foreach (var kv in disk.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var (ep, have) = kv.Value;
            // Тот же дешёвый отсев, что у XSMART: страница тайтла на jut стоит ~1.1 с
            // и идёт через солвер — платить за неё, когда апгрейдить нечего, нельзя.
            if (have == int.MaxValue)
            { rows.Add(new QualityRow { ep = kv.Key, note = "«Авто», не сравниваем" }); continue; }
            if (have >= min)
            { rows.Add(new QualityRow { ep = kv.Key, have = have, best = have, note = "цель достигнута" }); continue; }

            string ck = slug + ":" + kv.Key;
            if (QualityCaches.Jut.Skip(ck, QualityRecheck, QualityMaxUps))
            { rows.Add(new QualityRow { ep = kv.Key, have = have, note = "решение свежее" }); continue; }
            if (probes >= budget)
            { rows.Add(new QualityRow { ep = kv.Key, have = have, note = "бюджет проб исчерпан" }); continue; }

            probes++;
            int best = 0; string err = null;
            try
            {
                string token = JutNet.MakeToken(slug, ep.season, ep.num, JutKindParam(ep.kind), 0);
                var link = await JutNet.EnsureLink(token);
                // available несёт ВСЮ лестницу, а не только выбранное — потолок честнее.
                best = link == null ? 0
                     : (link.available is { Length: > 0 } ? link.available.Max() : link.quality);
                if (link == null) err = "SITE_DOWN";
            }
            catch (Exception ex) { err = ex.Message; }

            bool up = best > have;
            rows.Add(new QualityRow
            {
                ep = kv.Key, have = have, best = best, upgradable = up,
                note = err ?? (up ? null : "сайт пока не отдаёт выше")
            });
            if (up) toGrab.Add((ep, best));
            if (err == null) QualityCaches.Jut.Note(ck, best, up);
        }

        int queued = 0;
        if (apply && toGrab.Count > 0)
        {
            foreach (var g in toGrab.GroupBy(x => x.best))
                queued += JutWantsCommit(slug, titleRu, g.Select(x => x.ep), "upgrade", upgradeTo: g.Key);
            JutWantsSweep();
        }
        return QualityReport(rows, queued, JutAvgFileGb(slug));
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/quality/scan")]
    async public Task<ActionResult> JutQualityScan(string slug, int min = 0, int apply = 0)
    {
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;
        if (!JutOn) return JutErr("DISABLED");
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");
        int target = min > 0 ? min : QualityTargetJut;
        if (target <= 0) return JutErr("DISABLED", "Апгрейд качества выключен (jutQualityTarget = 0)");
        return JutJson(await JutQualityScanTitle(slug, target, apply == 1, QualityPerTick));
    }

    internal static async Task JutQualitySweep()
    {
        int target = QualityTargetJut;
        if (!JutOn || target <= 0) return;
        int budget = QualityPerTick, upgraded = 0;
        try
        {
            string root = JutDownloadRoot();
            if (!Directory.Exists(root)) return;
            using var bg = JutNet.BackgroundScope();
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (budget <= 0) break;
                string slug = Path.GetFileName(dir);
                if (!JutSuParse.IsValidSlug(slug)) continue;
                var rep = await JutQualityScanTitle(slug, target, apply: true, budget: budget);
                budget -= rep.Value<int?>("scanned") ?? 0;
                upgraded += rep.Value<int?>("queued") ?? 0;
            }
            if (upgraded > 0) JutNet.Log("quality", "поставлено на перекачку — " + upgraded);
        }
        catch (Exception ex) { JutNet.Log("quality", ex.Message); }
    }

    #endregion
}
