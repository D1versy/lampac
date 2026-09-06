using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Память экрана поиска XSMART (qdl 2.114): «Недавнее» = что смотрели + что находили.
// Паритет с jut.su (JutSuHistory.cs), с одним отличием по устройству:
//
// 🔴 КАРТОЧКА ХРАНИТСЯ В САМОЙ ЗАПИСИ. У lampac нет каталога XSMART — карточки живут в
// отдельном контейнере xsmart-proxy, и ходить за каждой при каждом открытии поиска незачем.
// Плагин уже держит карточку в руках в обе точки записи (выдача поиска, старт серии) и шлёт
// её нам slim-формой контракта §2.3 (cat, id, title, poster…). Постер принимаем только
// НАШИМ путём /xsmart/… — инвариант «клиент не ходит наружу».
//
// 🔴 ПОЧЕМУ КЛИЕНТСКИЙ ВЫЗОВ, А НЕ «СЕРВЕР ВИДИТ ПОТОК». У jut просмотр пишется из
// /qdl/jut/stream без единой клиентской строки. Поток XSMART идёт из xsmart-proxy напрямую
// (/xsmart/stream/…), lampac его не видит — поэтому плагин зовёт /qdl/xsmart/history/touch
// перед Lampa.Player.play. Нативные плееры тут не мешают: старт серии всегда проходит через
// веб-слой плагина.
//
// Бакеты — те же, что у jut: устройство (lampac_unic_id из query uid=), через группу
// (Groups.Resolve → одна история на связанные устройства), санитайзер JutHistoryBucket,
// общий бакет _shared для безымянных. Реплика в дом не пишет. Песочница тестов чистит
// бакет устройства (TestSandbox.PurgeXsmartHistory), _shared — никогда.
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region хранилище

    const int XsWatchDedupSec = 300;   // повторный старт той же серии за 5 минут — не новый просмотр
    const int XsHistDevCap = 24;

    static string XsmartHistoryDir() => Path.Combine(XsmartNet.DataDir(), "history");
    static string XsmartHistoryPath(string bucket) => Path.Combine(XsmartHistoryDir(), bucket + ".json");

    static readonly ConcurrentDictionary<string, DateTime> _xsWatchSeen = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _xsHistLock = new();

    /// <summary>Бакет запроса: устройство → группа (общая история) → имя файла.</summary>
    internal static string XsmartHistoryBucketFor(string uid) => JutHistoryBucket(Groups.Resolve(uid));

    static JObject XsmartHistoryRead(string bucket)
    {
        var jo = JsonStore.ReadObject(XsmartHistoryPath(bucket)) ?? new JObject();
        if (jo["watched"] is not JObject) jo["watched"] = new JObject();
        if (jo["searched"] is not JObject) jo["searched"] = new JObject();
        return jo;
    }

    static void XsmartHistoryWrite(string bucket, JObject jo)
    {
        string path = XsmartHistoryPath(bucket);
        bool isNew = !JsonStore.Exists(path);
        jo["at"] = DateTime.UtcNow;
        if (isNew)
        {
            // WriteNow: прунинг устройств считает бакеты по ДИСКОВОМУ листингу (см. JutHistoryWrite)
            JsonStore.WriteNow(path, jo);
            JsonStore.ForgetDir(XsmartHistoryDir());
            XsmartHistoryPruneDevicesAsync();
        }
        else JsonStore.Write(path, jo);
    }

    static void XsmartHistoryPruneDevicesAsync() => _ = Task.Run(() =>
    {
        try
        {
            string dir = XsmartHistoryDir();
            var files = JsonStore.List(dir, "*.json")
                .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f), JutSharedBucket, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count <= XsHistDevCap) return;
            var stale = files
                .Select(f => (f, at: JsonStore.ReadObject(f)?["at"]?.Value<DateTime?>() ?? DateTime.MinValue))
                .OrderByDescending(x => x.at)
                .Skip(XsHistDevCap)
                .ToList();
            foreach (var (f, _) in stale) JsonStore.Remove(f);
            if (stale.Count > 0) { JsonStore.ForgetDir(dir); XsmartNet.Log("history", "забыто устройств: " + stale.Count); }
        }
        catch (Exception ex) { XsmartNet.Log("history", "прунинг устройств: " + ex.Message); }
    });

    /// <summary>Сброс дедуп-окна. Только для тестов: в бою окно живёт 5 минут.</summary>
    internal static void XsmartHistoryResetForTests() => _xsWatchSeen.Clear();

    #endregion

    #region карточка

    // Slim-карточка контракта §2.3 — ровно то, что нужно buildCard в плагине.
    static readonly string[] _xsCardFields = { "type", "title", "titleOrig", "year", "rating", "hdr", "country" };

    /// <summary>
    /// Карточка из того, что прислал плагин: cat/id обязаны быть валидными, постер — только наш
    /// путь. null = мусор, в историю не идёт.
    /// </summary>
    internal static JObject XsmartHistoryCard(JObject raw)
    {
        if (raw == null) return null;
        int cat = raw.Value<int?>("cat") ?? 0;
        string id = raw.Value<string>("id");
        if (!XsmartNet.Valid(cat, id)) return null;

        var c = new JObject { ["cat"] = cat, ["id"] = id };
        foreach (string f in _xsCardFields)
            if (raw[f] != null && raw[f].Type != JTokenType.Null) c[f] = raw[f];
        string title = raw.Value<string>("title");
        c["title"] = string.IsNullOrWhiteSpace(title) ? XsmartNet.Ref(cat, id) : title.Trim();
        if (c["title"].ToString().Length > 200) c["title"] = c["title"].ToString().Substring(0, 200);

        string poster = raw.Value<string>("poster");
        if (!string.IsNullOrEmpty(poster) && poster.StartsWith("/xsmart/", StringComparison.Ordinal) && poster.Length < 300)
            c["poster"] = poster;
        return c;
    }

    static string XsmartHistoryKey(JObject card) => XsmartNet.Ref(card.Value<int>("cat"), card.Value<string>("id"));

    #endregion

    #region запись

    /// <summary>Отметка просмотра — плагин зовёт перед стартом плеера.</summary>
    internal static bool XsmartHistoryTouchWatch(JObject raw, string uid)
    {
        if (ReplicaMode) return false;
        var card = XsmartHistoryCard(raw);
        if (card == null) return false;

        string bucket = XsmartHistoryBucketFor(uid);
        string key = XsmartHistoryKey(card);
        var now = DateTime.UtcNow;
        string dedupKey = bucket + "|" + key;   // ⚠️ бакет в ключе: просмотр на телефоне не глушит запись на ТВ
        if (_xsWatchSeen.TryGetValue(dedupKey, out var seen) && (now - seen).TotalSeconds < XsWatchDedupSec)
            return true;
        _xsWatchSeen[dedupKey] = now;
        if (_xsWatchSeen.Count > 512)
            foreach (var kv in _xsWatchSeen)
                if ((now - kv.Value).TotalSeconds > XsWatchDedupSec) _xsWatchSeen.TryRemove(kv.Key, out _);

        try
        {
            lock (_xsHistLock)
            {
                var jo = XsmartHistoryRead(bucket);
                var watched = (JObject)jo["watched"];
                int count = watched[key]?["count"]?.Value<int>() ?? 0;
                watched[key] = new JObject { ["at"] = now, ["count"] = count + 1, ["card"] = card };
                JutHistoryPrune(watched);
                XsmartHistoryWrite(bucket, jo);
            }
            return true;
        }
        catch (Exception ex) { XsmartNet.Log("history", "watch: " + ex.Message); return false; }
    }

    /// <summary>Первые карточки поисковой выдачи — чтобы экрану поиска было чем заполниться.</summary>
    internal static int XsmartHistoryRecordSearch(JArray items, string uid, int take = 12)
    {
        if (ReplicaMode || items == null || items.Count == 0) return 0;
        string bucket = XsmartHistoryBucketFor(uid);
        int put = 0;
        try
        {
            lock (_xsHistLock)
            {
                var jo = XsmartHistoryRead(bucket);
                var searched = (JObject)jo["searched"];
                var now = DateTime.UtcNow;
                foreach (var raw in items.OfType<JObject>().Take(take))
                {
                    var card = XsmartHistoryCard(raw);
                    if (card == null) continue;
                    searched[XsmartHistoryKey(card)] = new JObject { ["at"] = now, ["card"] = card };
                    put++;
                }
                if (put > 0)
                {
                    JutHistoryPrune(searched);
                    XsmartHistoryWrite(bucket, jo);
                }
            }
        }
        catch (Exception ex) { XsmartNet.Log("history", "search: " + ex.Message); }
        return put;
    }

    #endregion

    #region выдача

    /// <summary>
    /// Топ последних тайтлов устройства: просмотренные (свежие выше), затем искомые, затем
    /// добор из общего бакета. Дедуп по ref. Статический — чтобы порядок проверялся тестами.
    /// </summary>
    internal static JObject XsmartRecentPayload(int limit, string uid)
    {
        string bucket = XsmartHistoryBucketFor(uid);
        JObject own, shared = null;
        lock (_xsHistLock)
        {
            own = XsmartHistoryRead(bucket);
            if (bucket != JutSharedBucket) shared = XsmartHistoryRead(JutSharedBucket);
        }

        static IEnumerable<(string key, JObject rec)> Rows(JToken section)
            => (section as JObject)?.Properties()
                   .Select(p => (p.Name, p.Value as JObject))
                   .Where(x => x.Item2 != null)
                   .OrderByDescending(x => x.Item2["at"]?.Value<DateTime?>() ?? DateTime.MinValue)
               ?? Enumerable.Empty<(string, JObject)>();

        var items = new JArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Take(JToken section, string src)
        {
            foreach (var (key, rec) in Rows(section))
            {
                if (items.Count >= limit) return;
                if (!seen.Add(key)) continue;
                if (rec["card"] is not JObject card) continue;
                var c = (JObject)card.DeepClone();
                c["src"] = src;
                items.Add(c);
            }
        }

        Take(own["watched"], "watch");
        Take(own["searched"], "search");
        if (shared != null)
        {
            Take(shared["watched"], "watch");
            Take(shared["searched"], "search");
        }
        return new JObject { ["ok"] = true, ["total"] = items.Count, ["items"] = items };
    }

    #endregion

    #region роуты

    [HttpGet, AllowAnonymous]
    [Route("qdl/xsmart/recent")]
    public ActionResult XsmartRecent(int limit = 50)
    {
        if (!XsmartNet.On) return XsmartErr("DISABLED");
        return XsmartJson(XsmartRecentPayload(Math.Clamp(limit, 1, 100), requestInfo?.user_uid));
    }

    /// <summary>
    /// Тело: {"kind":"watch"|"search","items":[card…]}. Сырой JSON, а не [FromBody]: биндер
    /// проекта — System.Text.Json, а карточка тут — произвольный JObject.
    /// </summary>
    [HttpPost, AllowAnonymous]
    [Route("qdl/xsmart/history/touch")]
    async public Task<ActionResult> XsmartHistoryTouch()
    {
        if (!XsmartNet.On) return XsmartErr("DISABLED");
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;

        JObject body;
        try
        {
            using var sr = new StreamReader(Request.Body);
            string txt = await sr.ReadToEndAsync();
            if (txt.Length > 64 * 1024) return XsmartErr("BAD_ID", "Слишком большое тело");
            body = JObject.Parse(txt);
        }
        catch { return XsmartErr("BAD_ID", "Тело не JSON"); }

        string kind = body.Value<string>("kind");
        var items = body["items"] as JArray;
        string uid = requestInfo?.user_uid;
        int put;
        if (kind == "watch")
            put = XsmartHistoryTouchWatch(items?.OfType<JObject>().FirstOrDefault(), uid) ? 1 : 0;
        else if (kind == "search")
            put = XsmartHistoryRecordSearch(items, uid);
        else return XsmartErr("BAD_ID", "kind: watch | search");

        return XsmartJson(new JObject { ["ok"] = true, ["put"] = put });
    }

    #endregion
}
