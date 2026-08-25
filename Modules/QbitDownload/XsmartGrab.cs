using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Скачивание фильмов и серий XSMART в общий раздел «Загрузки».
//
// 🔥 Мост в «Загрузки» бесплатный — тот же, что у jut.su: /qdl/list строит карточку из
// local/<hash>.json при единственном условии ValidHash. Псевдо-infohash
// sha1("xsmart:<cat>:<id>") его проходит, поэтому без единой правки работают /qdl/stream,
// /qdl/episodes, /qdl/hls, коллекции и удаление.
//
// ⚠️ ИНВАРИАНТ ИЗОЛЯЦИИ: links/<hash>.json для xsmart НЕ создаём НИКОГДА (см. шапку Xsmart.cs).
//
// 🔴 ДВЕ ВЕТКИ ДОБЫЧИ БАЙТОВ, и выбор делается ПО ФАКТУ, а не по ожиданию. XSMART за один
// день 2026-08-24 переехал у сериалов с прямых MP4 на HLS того же контента (CONTRACT.md
// §2.10). Жёсткая связка «серия = MP4» означала бы, что после такого переезда скачивание
// молча ломается. Поэтому смотрим на Content-Type ответа прокси:
//   • не плейлист → байтовая качалка с Range (докачка с места, как у jut);
//   • плейлист    → посегментная качалка + локальный ремукс ffmpeg -c copy.
//
// Почему HLS не отдан ffmpeg целиком (`ffmpeg -i index.m3u8 -c copy out.mp4`): он НЕ умеет
// докачиваться. Обрыв на 90% 30-гигабайтного 4K-фильма означал бы полный перекач. Сегменты
// же нумерованы и стабильны между перерезолвами, поэтому докачка тривиальна.
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region модель очереди

    sealed class XsmartGrabItem
    {
        public int cat;
        public string id, sref, source, titleRu;
        public XsmartEp ep;
        // Отмены «поштучно» тут нет намеренно: единственный механизм — поколение (см. XsmartStale).
        // Два способа отменить одно и то же расходятся между собой раньше, чем успевают пригодиться.
        public int gen;                            // поколение отмены на момент постановки
    }

    sealed class XsmartGrabJob
    {
        public volatile string state = "queued";   // queued | running | done | error | canceled
        public volatile string file;
        public volatile string error;
        public volatile bool canceled;
        public long done, total;                   // байты текущего файла
        public int seg, segTotal;                  // сегменты HLS (0/0 у байтовой ветки)
        public int fileDone, filesTotal;
        public volatile bool agg;
        public volatile bool aggFlushed;
        public DateTime touched = DateTime.UtcNow;
    }

    static readonly ConcurrentQueue<XsmartGrabItem> _xsQueue = new();
    static readonly HashSet<string> _xsQueued = new(StringComparer.Ordinal);
    static readonly object _xsEnqLock = new();
    static readonly ConcurrentDictionary<string, XsmartGrabJob> _xsJobs = new();   // ref → job
    static readonly ConcurrentDictionary<string, int> _xsGen = new();              // ref → поколение
    static int _xsWorker = 0;

    static string XsmartQueueKey(string sref, string epkey) => sref + ":" + epkey;

    static int XsmartGenOf(string sref) => _xsGen.TryGetValue(sref, out int g) ? g : 0;

    /// <summary>
    /// «Элемент устарел»: отменён явно или пережил отмену своего тайтла.
    /// 🔥 Поколение нужно потому, что отмена НЕ вынимает элементы из ConcurrentQueue — она
    /// может только пометить их. Без поколения «отменил → сразу добавил заново» ломалось:
    /// старый элемент доходил до воркера и снимал ключ, только что поставленный НОВЫМ
    /// запросом, после чего серия молча не скачивалась (разобрано на jut, JutSuGrab.cs).
    /// </summary>
    static bool XsmartStale(XsmartGrabItem it) => it.gen != XsmartGenOf(it.sref);

    /// <summary>Сколько единиц этого тайтла ещё висит в очереди (включая текущую в работе).</summary>
    static int XsmartPendingFor(string sref)
    {
        string p = sref + ":";
        lock (_xsEnqLock)
        {
            int n = 0;
            foreach (string k in _xsQueued)
                if (k.StartsWith(p, StringComparison.Ordinal)) n++;
            return n;
        }
    }

    /// <summary>Уведомлять пачку одной строкой или каждой серией отдельно.</summary>
    internal static bool XsmartAggFor(bool freshBatch, int queued) => queued > 1 || !freshBatch;

    static XsmartGrabJob XsmartJobForBatch(string sref, bool freshBatch, int queued)
    {
        var job = _xsJobs.GetOrAdd(sref, _ => new XsmartGrabJob());
        if (freshBatch)
        {
            job.fileDone = 0; job.filesTotal = 0; job.done = 0; job.total = 0;
            job.seg = 0; job.segTotal = 0;
            job.error = null; job.canceled = false;
            job.agg = false; job.aggFlushed = false;
        }
        job.filesTotal += queued;
        // Ручка читается ОДИН раз на пачку (латч): выключение на лету действует на новые пачки,
        // а начатая доживает по своему режиму — иначе часть серий уведомит, часть нет.
        if ((ModInit.conf?.xsmartNotifyAggregate ?? true) && XsmartAggFor(freshBatch, queued)) job.agg = true;
        XsmartSetState(job, "queued");
        return job;
    }

    /// <summary>
    /// Запись состояния job. ⚠️ Молчит, пока взведён canceled: иначе единица, которая была уже
    /// в работе в момент отмены (её нет в очереди — она из неё вынута), доигрывала бы до конца
    /// и затирала "canceled" на "done".
    /// </summary>
    static void XsmartSetState(XsmartGrabJob job, string state)
    {
        job.touched = DateTime.UtcNow;
        if (job.canceled && state != "canceled") return;
        job.state = state;
    }

    static void XsmartPruneJobs()
    {
        var edge = DateTime.UtcNow.AddHours(-6);
        foreach (var kv in _xsJobs)
        {
            if (kv.Value.touched > edge) continue;
            if (kv.Value.state is "done" or "error" or "canceled")
                _xsJobs.TryRemove(kv.Key, out _);
        }
    }

    #endregion

    #region пути и имена

    internal static string XsmartDownloadRoot()
        => ModInit.conf?.xsmartDownloadsPath ?? "/downloads/xsmart";

    internal static string XsmartTitleDir(string sref) => Path.Combine(XsmartDownloadRoot(), sref);

    /// <summary>
    /// Имя файла. 🔴 В имени — ПОРЯДКОВЫЕ номера, а не id XSMART: «s32215e524438» на экране
    /// серий читалось бы как сезон 32215. Разбор s01e05 общий ParseEp делает бесплатно,
    /// но обратно мы читаем СВОИМ парсером (см. TryParseXsmartFileName) — он точен по построению.
    /// </summary>
    internal static string XsmartFileName(string sref, XsmartEp e, int quality)
    {
        string q = quality > 0 ? "." + quality + "p" : "";
        return e.kind == XsmartKind.Film
            ? $"{sref}.film{q}.mp4"
            : $"{sref}.s{e.seasonNo:00}e{e.epNo:00}{q}.mp4";
    }

    // Обратный разбор НАШЕГО имени — точная инверсия XsmartFileName. Якорь на КОНЕЦ базы имени
    // обязателен: префикс «<cat>-<id>» состоит из цифр, и незаякоренный поиск мог бы зацепиться
    // за него.
    static readonly Regex _xsNameEpRx =
        new(@"\.s(\d+)e(\d+)(?:\.\d{3,4}p)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex _xsNameFilmRx =
        new(@"\.film(?:\.\d{3,4}p)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Имя файла xsmart → (вид, сезон, номер). Общий ParseEp тут не годится и врёт:
    /// «&lt;ref&gt;.film.2160p» он читает как серию, а трёхзначные номера серий берёт не всегда.
    /// </summary>
    internal static bool TryParseXsmartFileName(string baseNoExt, out XsmartKind kind, out int season, out int num)
    {
        kind = XsmartKind.Episode; season = 1; num = -1;
        string s = baseNoExt ?? "";

        var m = _xsNameEpRx.Match(s);
        if (m.Success)
        {
            season = int.Parse(m.Groups[1].Value);
            num = int.Parse(m.Groups[2].Value);
            return true;
        }
        if (_xsNameFilmRx.IsMatch(s)) { kind = XsmartKind.Film; season = 0; num = 1; return true; }
        return false;
    }

    /// <summary>Ключ единицы по имени файла — тот же, что XsmartEp.epkey.</summary>
    internal static string XsmartKeyFromName(string baseNoExt)
        => TryParseXsmartFileName(baseNoExt, out var k, out int s, out int n)
           ? (k == XsmartKind.Film ? "film" : "s" + s + "e" + n)
           : null;

    // Снимок «что уже лежит на диске» — чтобы не делать Directory.Enumerate на каждую единицу
    // при постановке всего сериала (сотни серий = сотни обходов каталога).
    static readonly ConcurrentDictionary<string, HashSet<string>> _xsDisk = new(StringComparer.Ordinal);

    internal static HashSet<string> XsmartDiskKeys(string sref)
        => _xsDisk.GetOrAdd(sref, r =>
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (string f in Directory.EnumerateFiles(XsmartTitleDir(r), "*.mp4"))
                {
                    string k = XsmartKeyFromName(Path.GetFileNameWithoutExtension(f));
                    if (k != null) set.Add(k);
                }
            }
            catch { }
            return set;
        });

    internal static void XsmartDropDiskKeys(string sref) => _xsDisk.TryRemove(sref, out _);
    internal static void XsmartDropAllDiskKeys() => _xsDisk.Clear();

    #endregion

    #region кеш карточки тайтла

    static string XsmartCachePath(string sref)
    {
        string d = Path.Combine(XsmartNet.DataDir(), "title");
        try { Directory.CreateDirectory(d); } catch { }
        return Path.Combine(d, sref + ".json");
    }

    static JObject XsmartTitleJson(XsmartTitle t)
    {
        var arr = new JArray();
        foreach (var e in t.items)
            arr.Add(new JObject
            {
                ["kind"] = e.kind == XsmartKind.Film ? "film" : "episode",
                ["seasonNo"] = e.seasonNo, ["epNo"] = e.epNo,
                ["seasonId"] = e.seasonId, ["epId"] = e.epId,
                ["name"] = e.name, ["playable"] = e.playable
            });
        return new JObject
        {
            ["cat"] = t.cat, ["id"] = t.id, ["title"] = t.title, ["original"] = t.titleOrig,
            ["year"] = t.year, ["poster"] = t.poster, ["descr"] = t.descr,
            ["series"] = t.series, ["source"] = t.source, ["items"] = arr
        };
    }

    /// <summary>
    /// Восстановить тайтл из кеша, не ходя в сеть.
    /// 🔥 Ради этого метод и существует: «Скачать весь сериал» иначе платит 2 + N запросов
    /// к прокси (карточка + сезоны + серии каждого сезона) ВНУТРИ http-запроса клиента.
    /// ⚠️ TTL бесконечный намеренно: просроченный список серий для СКАЧИВАНИЯ годится —
    /// недостающее доберут суточный тик слежения и реконсиляция.
    /// </summary>
    internal static XsmartTitle XsmartTitleFromCache(int cat, string id)
    {
        try
        {
            string p = XsmartCachePath(XsmartNet.Ref(cat, id));
            if (!System.IO.File.Exists(p)) return null;
            var jo = JObject.Parse(System.IO.File.ReadAllText(p));
            if (jo["items"] is not JArray arr || arr.Count == 0) return null;

            var t = new XsmartTitle
            {
                cat = cat, id = id,
                title = jo.Value<string>("title"),
                titleOrig = jo.Value<string>("original"),
                year = jo.Value<int?>("year") ?? 0,
                poster = jo.Value<string>("poster"),
                descr = jo.Value<string>("descr"),
                series = jo.Value<bool?>("series") ?? false,
                source = jo.Value<string>("source")
            };
            foreach (var e in arr.OfType<JObject>())
                t.items.Add(new XsmartEp
                {
                    kind = e.Value<string>("kind") == "film" ? XsmartKind.Film : XsmartKind.Episode,
                    seasonNo = e.Value<int?>("seasonNo") ?? 1,
                    epNo = e.Value<int?>("epNo") ?? 0,
                    seasonId = e.Value<string>("seasonId"),
                    epId = e.Value<string>("epId"),
                    name = e.Value<string>("name"),
                    playable = e.Value<bool?>("playable") ?? true
                });
            return t.items.Count > 0 ? t : null;
        }
        catch { return null; }
    }

    internal static void XsmartCacheWrite(XsmartTitle t)
    {
        try { System.IO.File.WriteAllText(XsmartCachePath(XsmartNet.Ref(t.cat, t.id)),
                                          XsmartTitleJson(t).ToString(Newtonsoft.Json.Formatting.None)); }
        catch { }
    }

    /// <summary>Тайтл: сперва кеш, потом сеть. Свежий результат кешируем.</summary>
    static async Task<(XsmartTitle t, string err)> XsmartTitleFor(int cat, string id, string source, bool fresh = false)
    {
        if (!fresh)
        {
            var cached = XsmartTitleFromCache(cat, id);
            if (cached != null && (string.IsNullOrEmpty(source) || cached.source == source)) return (cached, null);
        }
        var (loaded, err) = await XsmartNet.LoadTitle(cat, id, source);
        if (loaded == null) return (null, err ?? "UPSTREAM_DOWN");
        XsmartCacheWrite(loaded);
        return (loaded, null);
    }

    #endregion

    #region ответы и постановка в очередь

    ActionResult XsmartJson(JToken payload)
    {
        SetHeadersNoCache();
        return ContentTo(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    // Ошибку отдаём 200 + {"ok":false,...} — как в JutSu.cs: клиент показывает текст тостом,
    // а не молчаливый «ничего не произошло» от провалившегося XHR.
    ActionResult XsmartErr(string code, string msg = null)
        => XsmartJson(new JObject { ["ok"] = false, ["error"] = code, ["message"] = msg ?? XsmartMessage(code) });

    static string XsmartMessage(string code) => code switch
    {
        "DISABLED" => "Скачивание XSMART выключено в настройках",
        "BAD_ID" => "Некорректный тайтл XSMART",
        "NOT_FOUND" => "Серия не найдена в списке",
        "NO_STREAM" => "Нет доступного потока",
        "UPSTREAM_EMPTY" => "XSMART ничего не вернул",
        "UPSTREAM_DOWN" => "XSMART недоступен",
        "AUTH_FAILED" => "Сессия XSMART не установлена",
        "BANNED" => "Устройство заблокировано XSMART",
        "NO_SPACE" => "На диске мало места",
        "NOT_WATCHED" => "Слежение за этим тайтлом не включено",
        _ => "Не получилось"
    };

    /// <summary>
    /// 🔥 Три исхода, которые иначе неотличимы: «поставлено», «уже на диске», «уже в очереди».
    /// Все три дают queued=0, и на jut это уже выливалось в тост «В очереди: 0» — повторное
    /// нажатие выглядело так, будто ничего не произошло.
    /// </summary>
    internal static string XsmartQueueMessage(int queued, int already, int duplicate, int skipped, int pending)
    {
        if (queued > 0)
        {
            string s = "Поставлено в очередь: " + queued;
            if (already > 0) s += " · уже на диске: " + already;
            if (duplicate > 0) s += " · уже в очереди: " + duplicate;
            if (skipped > 0) s += " · без потока: " + skipped;
            return s;
        }
        if (duplicate > 0) return "Уже в очереди (" + duplicate + ") — качается, осталось " + pending;
        if (already > 0) return "Всё уже скачано (" + already + ")";
        if (skipped > 0) return "У этих серий нет играбельного источника";
        return "Нечего скачивать";
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/xsmart/download")]
    async public Task<ActionResult> XsmartDownload(int cat, string id, string season = null,
                                                   string episode = null, string source = null,
                                                   string scope = "one")
    {
        // На реплику контент приезжает мостом из дома, а не качается вторым сервером независимо.
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;
        if (!XsmartNet.On) return XsmartErr("DISABLED");
        if (!XsmartNet.Valid(cat, id)) return XsmartErr("BAD_ID");
        if (!XsmartNet.ValidSource(source)) return XsmartErr("BAD_ID");

        var (t, err) = await XsmartTitleFor(cat, id, source);
        if (t == null) return XsmartErr(err ?? "UPSTREAM_DOWN");

        string sref = XsmartNet.Ref(cat, id);
        var want = new List<XsmartEp>();

        if (!t.series) want.AddRange(t.items);                       // кино: единица ровно одна
        else if (scope == "one")
        {
            var hit = t.items.FirstOrDefault(x => x.epId == episode
                                                  && (string.IsNullOrEmpty(season) || x.seasonId == season));
            if (hit == null) return XsmartErr("NOT_FOUND");
            want.Add(hit);
        }
        else if (scope == "season")
        {
            // Сезон не назвали — берём тот, которому принадлежит указанная серия, иначе первый.
            string sid = !string.IsNullOrEmpty(season) ? season
                       : t.items.FirstOrDefault(x => x.epId == episode)?.seasonId
                       ?? t.items.FirstOrDefault()?.seasonId;
            want.AddRange(t.items.Where(x => x.seasonId == sid));
        }
        else want.AddRange(t.items);

        // «Новая пачка» = этого тайтла в очереди сейчас ничего нет. Только в этот момент можно
        // обнулять счётчики: иначе прогресс складывался бы с прошлым прогоном и врал (>100%).
        bool freshBatch = XsmartPendingFor(sref) == 0;

        // ⚠️ Отсев ДО гарда места: 60 серий требовали бы места на 60, даже если 59 уже лежат.
        var disk = XsmartDiskKeys(sref);
        int already = 0, duplicate = 0, skipped = 0;
        var toGrab = new List<XsmartEp>();
        foreach (var e in want)
        {
            if (disk.Contains(e.epkey)) { already++; continue; }
            if (!e.playable) { skipped++; continue; }
            toGrab.Add(e);
        }

        string freeErr = XsmartCheckSpace(toGrab.Count + _xsQueue.Count, sref);
        if (freeErr != null) return XsmartErr("NO_SPACE", freeErr);

        int queued = 0;
        int gen = XsmartGenOf(sref);
        XsmartGrabJob job = null;
        foreach (var e in toGrab)
        {
            lock (_xsEnqLock)
            {
                if (!_xsQueued.Add(XsmartQueueKey(sref, e.epkey))) { duplicate++; continue; }
            }
            _xsQueue.Enqueue(new XsmartGrabItem
            {
                cat = cat, id = id, sref = sref, source = t.source,
                ep = e, titleRu = t.title, gen = gen
            });
            queued++;
        }

        if (queued > 0)
        {
            job = XsmartJobForBatch(sref, freshBatch, queued);
            // Мета/постер пишем СРАЗУ: иначе первые минуты скачивания выглядят как «ничего не происходит»
            await XsmartEnsureMeta(t);
            XsmartNotifyStart(sref, t.title, queued);
            XsmartKickWorker();
        }

        return XsmartJson(new JObject
        {
            ["ok"] = true, ["queued"] = queued, ["already"] = already,
            ["duplicate"] = duplicate, ["skipped"] = skipped,
            ["pending"] = XsmartPendingFor(sref),
            ["hash"] = XsmartNet.Hash(cat, id), ["scope"] = scope,
            ["message"] = XsmartQueueMessage(queued, already, duplicate, skipped, XsmartPendingFor(sref))
        });
    }

    /// <summary>
    /// Гард свободного места. Важен не ради экономии: на том же диске живут торренты
    /// qBittorrent и записи регистратора — забитый под ноль диск ломает их, а не только нас.
    /// </summary>
    static string XsmartCheckSpace(int files, string sref = null)
    {
        if (files <= 0) return null;
        try
        {
            string root = XsmartDownloadRoot();
            Directory.CreateDirectory(root);
            var di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? "/");
            long freeGb = di.AvailableFreeSpace / (1024L * 1024 * 1024);
            int min = Math.Max(1, ModInit.conf?.xsmartMinFreeGb ?? 30);
            double perFile = XsmartAvgFileGb(sref);
            long needGb = Math.Max(1, (long)Math.Ceiling(files * perFile));
            if (freeGb - needGb < min)
                return $"мало места: свободно {freeGb} ГБ, нужно ~{needGb} ГБ + резерв {min} ГБ";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Средний размер файла ЭТОГО тайтла по уже скачанному — оценка точнее любой константы:
    /// у серии 720p и у 4K-фильма она отличается на порядок. Фолбэк 2 ГБ, пока пусто.
    /// </summary>
    static double XsmartAvgFileGb(string sref)
    {
        const double fallback = 2.0;
        if (string.IsNullOrEmpty(sref)) return fallback;
        try
        {
            var parts = sref.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int cat)) return fallback;
            var loc = LoadLocal(XsmartNet.Hash(cat, parts[1]));
            if (loc?["files"] is JArray arr && arr.Count > 0)
            {
                long size = 0, n = 0;
                foreach (var f in arr)
                {
                    long s = f.Value<long?>("size") ?? 0;
                    if (s > 0) { size += s; n++; }
                }
                if (n > 0)
                {
                    double gb = size / (double)n / (1024 * 1024 * 1024);
                    if (gb > 0.01) return gb;
                }
            }
        }
        catch { }
        return fallback;
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/xsmart/download/status")]
    public ActionResult XsmartDownloadStatus(int cat = 0, string id = null)
    {
        XsmartPruneJobs();
        if (cat > 0 && !string.IsNullOrEmpty(id))
        {
            string sref = XsmartNet.Ref(cat, id);
            if (!_xsJobs.TryGetValue(sref, out var j))
                return XsmartJson(new JObject { ["ok"] = true, ["state"] = "idle", ["pending"] = 0 });
            return XsmartJson(XsmartJobJson(sref, j));
        }
        var arr = new JArray(_xsJobs.Select(kv => XsmartJobJson(kv.Key, kv.Value)));
        return XsmartJson(new JObject { ["ok"] = true, ["queue"] = _xsQueue.Count, ["jobs"] = arr });
    }

    static JObject XsmartJobJson(string sref, XsmartGrabJob j) => new JObject
    {
        ["ok"] = true,
        ["ref"] = sref,
        ["state"] = j.state,
        ["file"] = j.file,
        ["fileDone"] = j.fileDone,
        ["filesTotal"] = j.filesTotal,
        ["done"] = j.done,
        ["total"] = j.total,
        // У HLS общий размер заранее неизвестен (сегменты приходят по одному), поэтому там
        // честный прогресс — это сегменты, а не байты. Отдаём оба и не притворяемся.
        ["seg"] = j.seg,
        ["segTotal"] = j.segTotal,
        ["progress"] = j.segTotal > 0 ? Math.Round((double)j.seg / j.segTotal, 3)
                     : j.total > 0 ? Math.Round((double)j.done / j.total, 3) : 0,
        ["pending"] = XsmartPendingFor(sref),
        ["queueTotal"] = _xsQueue.Count,
        ["error"] = j.error
    };

    [HttpGet, AllowAnonymous]
    [Route("qdl/xsmart/download/cancel")]
    public ActionResult XsmartDownloadCancel(int cat, string id)
    {
        if (!XsmartNet.Valid(cat, id)) return XsmartErr("BAD_ID");
        string sref = XsmartNet.Ref(cat, id);
        lock (_xsEnqLock)
        {
            // Поколение вперёд — всё поставленное ДО этой секунды становится устаревшим
            // (XsmartStale). Из ConcurrentQueue элементы не вынуть, пометить можно только так.
            _xsGen[sref] = XsmartGenOf(sref) + 1;
            var drop = _xsQueued.Where(k => k.StartsWith(sref + ":", StringComparison.Ordinal)).ToList();
            foreach (string k in drop) _xsQueued.Remove(k);
        }
        if (_xsJobs.TryGetValue(sref, out var job))
        {
            job.canceled = true;
            job.state = "canceled";
            job.touched = DateTime.UtcNow;
        }
        return XsmartJson(new JObject { ["ok"] = true, ["message"] = "Скачивание отменено" });
    }

    #endregion

    #region воркер

    static void XsmartKickWorker()
    {
        // Один воркер: щадим и CDN, и шпиндель (на том же диске качает qBittorrent
        // и пишет регистратор). Плюс сессия XSMART одна — параллельные резолвы ей не нужны.
        if (Interlocked.CompareExchange(ref _xsWorker, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // ⚠️ Выключатель проверяется ДО TryDequeue, а не после. Иначе элемент успевали
                // вынуть и выйти по break, не сняв ключ в _xsQueued: ключ протекал навсегда,
                // а finally тут же перезапускал воркер — busy-loop, молча сливающий очередь
                // (ровно этим болел jut, JutSuGrab.cs).
                while (XsmartNet.On && _xsQueue.TryDequeue(out var it))
                {
                    if (XsmartStale(it)) { XsmartDoneWith(it); continue; }
                    try { await XsmartGrabOne(it); }
                    catch (Exception ex)
                    {
                        XsmartNet.Log("grab", it.sref + " " + it.ep.epkey + ": " + ex.Message);
                        if (_xsJobs.TryGetValue(it.sref, out var j))
                        {
                            j.error = ex.Message;
                            XsmartSetState(j, "error");
                        }
                    }
                    finally { XsmartDoneWith(it); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _xsWorker, 0);
                // Добор при гонке: пока сбрасывали флаг, могли поставить новый элемент.
                // Гейт по XsmartNet.On обязателен — без него это тот самый busy-loop.
                if (XsmartNet.On && !_xsQueue.IsEmpty) XsmartKickWorker();
                else XsmartPruneJobs();
            }
        });
    }

    /// <summary>
    /// Единица отработана: снять ключ и, если очередь тайтла осушена, закрыть пачку одним
    /// уведомлением. 🔥 Единственная точка флаша агрегата — через неё проходят ВСЕ исходы
    /// (успех, ошибка после ретраев, отмена, устаревший элемент), поэтому дедуп между путями
    /// не нужен. Воркер один, значит и гонки нет.
    /// </summary>
    static void XsmartDoneWith(XsmartGrabItem it)
    {
        XsmartForget(it);
        if (XsmartPendingFor(it.sref) == 0 && _xsJobs.TryGetValue(it.sref, out var job))
            XsmartNotifyTitleDone(it, job);
    }

    static void XsmartForget(XsmartGrabItem it)
    {
        lock (_xsEnqLock)
        {
            // ⚠️ Ключ снимает только АКТУАЛЬНЫЙ элемент: у устаревшего тот же ключ мог быть
            // заново поставлен новым запросом, и сняв его, мы выкинули бы из дедупа чужую
            // живую серию — она бы не скачалась.
            if (it.gen == XsmartGenOf(it.sref))
                _xsQueued.Remove(XsmartQueueKey(it.sref, it.ep.epkey));
        }
    }

    /// <summary>Плейлист это или байты — решаем по Content-Type ответа прокси, а не по суффиксу пути.</summary>
    static bool XsmartIsPlaylist(HttpResponseMessage resp)
    {
        string ct = resp.Content.Headers.ContentType?.MediaType ?? "";
        return ct.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
            || ct.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase)
            || ct.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region качалка: диспетчер и байтовая ветка

    static async Task XsmartGrabOne(XsmartGrabItem it)
    {
        var job = _xsJobs.GetOrAdd(it.sref, _ => new XsmartGrabJob());
        XsmartSetState(job, "running");
        job.error = null;
        job.seg = 0; job.segTotal = 0; job.done = 0; job.total = 0;

        string dir = XsmartTitleDir(it.sref);
        Directory.CreateDirectory(dir);

        var st = await XsmartNet.Resolve(it.cat, it.id, it.ep, it.source);
        if (st.error != null)
        {
            job.error = st.error;
            XsmartSetState(job, "error");
            XsmartNet.Log("grab", it.sref + " " + it.ep.epkey + " → " + st.error);
            return;
        }

        string dst = Path.Combine(dir, XsmartFileName(it.sref, it.ep, st.quality));
        string part = dst + ".part";
        string side = dst + ".part.json";
        job.file = Path.GetFileName(dst);

        long have = 0, knownTotal = 0;
        string knownMod = null;
        try
        {
            if (System.IO.File.Exists(side))
            {
                var sj = JObject.Parse(await System.IO.File.ReadAllTextAsync(side));
                knownTotal = sj.Value<long?>("total") ?? 0;
                knownMod = sj.Value<string>("lastModified");
            }
            if (System.IO.File.Exists(part)) have = new FileInfo(part).Length;
        }
        catch { }

        int retries = Math.Max(1, ModInit.conf?.xsmartGrabRetries ?? 5);
        int[] backoff = { 5, 15, 60, 60, 60 };

        for (int attempt = 0; attempt < retries; attempt++)
        {
            if (!XsmartNet.On || XsmartStale(it)) { XsmartSetState(job, "canceled"); return; }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, st.url);
                if (have > 0) req.Headers.TryAddWithoutValidation("Range", "bytes=" + have + "-");

                // Idle-токен: перезаводится на КАЖДОМ прочитанном чанке (см. XsmartWriteStream).
                // ⚠️ Именно idle, а не общий таймаут — см. комментарий у XsmartNet.Media.
                int idleSec = Math.Max(0, ModInit.conf?.xsmartGrabIdleSec ?? 60);
                using var idle = new CancellationTokenSource();
                if (idleSec > 0) idle.CancelAfter(TimeSpan.FromSeconds(idleSec));

                using var resp = await XsmartNet.Media
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token);

                // 🔴 Токен рецепта мог протухнуть (раскладка источников у XSMART сменилась) —
                // прокси отвечает стелс-404. Перерезолв даёт свежий токен ТОГО ЖЕ рецепта.
                if ((int)resp.StatusCode is 403 or 404 or 410)
                {
                    var fresh = await XsmartNet.Resolve(it.cat, it.id, it.ep, it.source);
                    if (fresh.error != null || string.IsNullOrEmpty(fresh.url))
                    {
                        job.error = fresh.error ?? "NO_STREAM";
                        XsmartSetState(job, "error");
                        return;
                    }
                    st = fresh;
                    XsmartNet.Log("grab", "перевыпуск ссылки на " + have + " Б: " + it.sref + " " + it.ep.epkey);
                    continue;
                }
                if ((int)resp.StatusCode is not (200 or 206))
                    throw new Exception("HTTP " + (int)resp.StatusCode);

                // 🔴 Развилка ПО ФАКТУ. Прокси мог отдать плейлист там, где вчера были байты
                // (XSMART переезжал с MP4 на HLS за один день) — тогда уходим в другую ветку,
                // а недокачанный .part прошлого формата выбрасываем: дописывать в него сегменты
                // значило бы получить битый файл.
                if (XsmartIsPlaylist(resp))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    try { if (System.IO.File.Exists(part)) System.IO.File.Delete(part); } catch { }
                    try { if (System.IO.File.Exists(side)) System.IO.File.Delete(side); } catch { }
                    await XsmartGrabHls(it, job, dir, st, text);
                    return;
                }

                // ⚠️ total берём из Content-Range, а НЕ из Content-Length: при Range последний
                // равен длине ХВОСТА, и сравнение с сайдкаром было бы ложно-отрицательным всегда.
                long total = resp.Content.Headers.ContentRange?.Length
                             ?? resp.Content.Headers.ContentLength ?? 0;
                string mod = resp.Content.Headers.LastModified?.ToString("R");

                if (have > 0 && knownTotal > 0 && total > 0 && total != knownTotal)
                {
                    XsmartNet.Log("grab", "файл на CDN сменился — качаю заново: " + it.sref + " " + it.ep.epkey);
                    try { System.IO.File.Delete(part); } catch { }
                    have = 0; knownTotal = 0;
                    continue;
                }
                if (have > 0 && knownMod != null && mod != null && knownMod != mod)
                {
                    try { System.IO.File.Delete(part); } catch { }
                    have = 0; knownMod = null;
                    continue;
                }

                if (total > 0)
                {
                    knownTotal = total; knownMod = mod;
                    job.total = total;
                    try
                    {
                        await System.IO.File.WriteAllTextAsync(side, new JObject
                        {
                            ["total"] = total, ["lastModified"] = mod, ["quality"] = st.quality
                        }.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    catch { }
                }

                await XsmartWriteStream(resp, part, have, job, it, idle, idleSec);

                long got = new FileInfo(part).Length;
                if (knownTotal > 0 && got < knownTotal)
                {
                    have = got;
                    throw new Exception("недокачано " + got + "/" + knownTotal);
                }

                try { if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst); } catch { }
                System.IO.File.Move(part, dst);
                try { System.IO.File.Delete(side); } catch { }

                await XsmartFinishFile(it, dst, st.quality);
                return;
            }
            catch (Exception ex)
            {
                // ⚠️ Отмену и idle-таймаут различать ОБЯЗАТЕЛЬНО: оба прилетают сюда как
                // OperationCanceledException. Отмена окончательна, ретраить нельзя;
                // idle-таймаут — штатный обрыв, .part остаётся и докачивается бэкоффом.
                if (XsmartStale(it) || !XsmartNet.On) { XsmartSetState(job, "canceled"); return; }
                bool idleTimeout = ex is OperationCanceledException;
                job.error = idleTimeout ? "нет данных от CDN — обрыв по idle-таймауту" : ex.Message;
                if (attempt >= retries - 1)
                {
                    XsmartSetState(job, "error");
                    // .part остаётся — следующий запуск докачает с места
                    XsmartNet.Log("grab", "сдаюсь после " + retries + " попыток: " + it.sref + " "
                                          + it.ep.epkey + " — " + job.error);
                    return;
                }
                try { have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0; } catch { }
                await Task.Delay(TimeSpan.FromSeconds(backoff[Math.Min(attempt, backoff.Length - 1)]));
            }
        }
    }

    static async Task XsmartWriteStream(HttpResponseMessage resp, string part, long from,
                                        XsmartGrabJob job, XsmartGrabItem it,
                                        CancellationTokenSource idle = null, int idleSec = 0)
    {
        int pace = Math.Max(0, ModInit.conf?.xsmartGrabPaceMs ?? 0);
        byte[] buf = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var ct = idle?.Token ?? CancellationToken.None;
        try
        {
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var fs = new FileStream(part, from > 0 ? FileMode.Append : FileMode.Create,
                                          FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true);
            long done = from;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
            {
                // 🔥 Таймер сдвигается на КАЖДОМ чанке — это и делает таймаут idle'овым, а не
                // общим: здоровое медленное скачивание не рвётся, а зависшее соединение
                // больше не держит ЕДИНСТВЕННЫЙ воркер бесконечно.
                if (idleSec > 0) idle.CancelAfter(TimeSpan.FromSeconds(idleSec));

                // Выключатель проверяется и ВНУТРИ файла: иначе «откат» ещё часы качал бы 30 ГБ.
                if (XsmartStale(it) || !XsmartNet.On) break;
                await fs.WriteAsync(buf, 0, n, ct);
                done += n;
                job.done = done;
                if (pace > 0) await Task.Delay(pace, ct);
            }
            await fs.FlushAsync(CancellationToken.None);   // флашим ВСЕГДА: .part нужен для докачки
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    #endregion

    #region качалка: ветка HLS (сегменты + локальный ремукс)

    /// <summary>Строка плейлиста — адрес ресурса, а не тег и не пустая.</summary>
    static bool XsmartIsUriLine(string line)
        => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#", StringComparison.Ordinal);

    /// <summary>
    /// 🔴 Все URI обязаны быть НАШИМИ. Прокси переписывает каждый на /xsmart/stream/&lt;tok&gt;/…,
    /// и чужой адрес здесь означал бы дыру в инварианте origin-изоляции (CONTRACT.md §2.10).
    /// Молча качать по нему нельзя: это и есть тот самый прямой поход в CDN, которого мы избегаем.
    /// </summary>
    internal static bool XsmartOwnUri(string uri)
        => !string.IsNullOrEmpty(uri) && uri.StartsWith("/xsmart/stream/", StringComparison.Ordinal);

    /// <summary>
    /// Мастер-плейлист → адрес лучшего варианта.
    ///
    /// 🔴 Выбираем САМИ, потому что ffmpeg по умолчанию берёт ПЕРВЫЙ вариант мастера, а не
    /// лучший. На дорожке «Авто» (а её нам и отдаёт резолв как максимум качества) это молча
    /// дало бы 360p вместо 1080p — то есть ровно наоборот к требованию владельца.
    /// Возвращает (null, 0), если это не мастер, а обычный медиа-плейлист.
    /// </summary>
    internal static (string uri, int height) XsmartPickMasterVariant(string text)
    {
        var lines = (text ?? "").Split('\n');
        string bestUri = null;
        long bestBw = -1;
        int bestH = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;

            long bw = 0;
            var mb = Regex.Match(line, @"(?:AVERAGE-)?BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
            if (mb.Success) long.TryParse(mb.Groups[1].Value, out bw);
            int h = 0;
            var mr = Regex.Match(line, @"RESOLUTION=(\d+)x(\d+)", RegexOptions.IgnoreCase);
            if (mr.Success) int.TryParse(mr.Groups[2].Value, out h);

            // Адрес варианта — первая непустая НЕ-тег строка после тега.
            string uri = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                string cand = lines[j].Trim();
                if (cand.Length == 0) continue;
                if (cand.StartsWith("#", StringComparison.Ordinal)) continue;
                uri = cand;
                break;
            }
            if (uri == null) continue;

            // Ранжируем по битрейту, а при его отсутствии — по высоте картинки.
            long rank = bw > 0 ? bw : h;
            if (rank > bestBw) { bestBw = rank; bestUri = uri; bestH = h; }
        }
        return (bestUri, bestH);
    }

    /// <summary>Разбор медиа-плейлиста: сегменты, init-сегмент fMP4, признак шифрования.</summary>
    internal static (List<string> segs, string map, string encMethod) XsmartParseMedia(string text)
    {
        var segs = new List<string>();
        string map = null, enc = null;

        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, "URI=\"([^\"]*)\"");
                if (m.Success) map = m.Groups[1].Value;
                continue;
            }
            if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"METHOD=([A-Za-z0-9-]+)", RegexOptions.IgnoreCase);
                string method = m.Success ? m.Groups[1].Value : "UNKNOWN";
                if (!method.Equals("NONE", StringComparison.OrdinalIgnoreCase)) enc = method;
                continue;
            }
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            segs.Add(line);
        }
        return (segs, map, enc);
    }

    /// <summary>
    /// Плейлист с ЛОКАЛЬНЫМИ именами файлов — вход для ремукса.
    ///
    /// ⚠️ Переписываем ИСХОДНЫЙ текст, а не собираем плейлист заново: там живут #EXTINF,
    /// #EXT-X-DISCONTINUITY и прочие теги, от которых зависит корректная склейка. Собранный
    /// «по-своему» плейлист терял бы их молча.
    ///
    /// 🔴 Имена наши, порядковые. Сегменты XSMART называются «720.mp4:hls:seg-1-v1-a1.ts»,
    /// и такое имя ffmpeg разбирает как ПРОТОКОЛ «720.mp4» — вход не открылся бы вовсе.
    /// </summary>
    internal static string XsmartLocalPlaylist(string text, IReadOnlyList<string> localNames, string localMap)
    {
        var outLines = new List<string>();
        int i = 0;
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string t = line.Trim();

            if (t.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase) && localMap != null)
            {
                outLines.Add(Regex.Replace(line, "URI=\"[^\"]*\"", "URI=\"" + localMap + "\""));
                continue;
            }
            if (XsmartIsUriLine(line))
            {
                if (i < localNames.Count) outLines.Add(localNames[i++]);
                continue;
            }
            outLines.Add(line);
        }
        return string.Join("\n", outLines);
    }

    /// <summary>Локальное имя сегмента: порядковый номер + расширение оригинала.</summary>
    internal static string XsmartSegName(int index, string uri)
    {
        string tail = (uri ?? "").Split('?')[0];
        int slash = tail.LastIndexOf('/');
        if (slash >= 0) tail = tail.Substring(slash + 1);
        int dot = tail.LastIndexOf('.');
        string ext = dot > 0 && dot < tail.Length - 1 ? tail.Substring(dot) : ".ts";
        if (!Regex.IsMatch(ext, @"^\.[A-Za-z0-9]{1,5}$")) ext = ".ts";
        return "s" + index.ToString("00000") + ext;
    }

    /// <summary>Подпись набора сегментов — по ней решаем, годится ли начатая докачка.</summary>
    internal static string XsmartSegSig(IEnumerable<string> uris)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (string u in uris)
                foreach (char c in u ?? "") { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }

    static async Task<string> XsmartFetchText(string url)
    {
        using var resp = await XsmartNet.Media.GetAsync(url);
        if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// HLS: тянем сегменты по одному, потом склеиваем локальным ремуксом.
    ///
    /// Почему не «ffmpeg -i index.m3u8 -c copy out.mp4» одной командой: ffmpeg не умеет
    /// докачиваться. Обрыв на 90% 30-гигабайтного 4K-фильма означал бы полный перекач.
    /// Сегменты же лежат готовыми файлами, и повтор берёт ровно недостающие.
    /// </summary>
    static async Task XsmartGrabHls(XsmartGrabItem it, XsmartGrabJob job, string dir,
                                    XsmartStream st, string masterText)
    {
        string mediaText = masterText;
        int height = 0;

        var (vuri, vh) = XsmartPickMasterVariant(masterText);
        if (vuri != null)
        {
            if (!XsmartOwnUri(vuri)) throw new Exception("мастер-плейлист увёл на чужой хост");
            height = vh;
            mediaText = await XsmartFetchText(XsmartNet.Api + vuri);
            // Вложенный мастер второй раз НЕ разбираем: у XSMART его не бывает, а бесконечную
            // рекурсию так исключаем структурно, а не «на всякий случай» счётчиком.
        }

        var (segUris, mapUri, enc) = XsmartParseMedia(mediaText);
        if (enc != null)
            throw new Exception("поток зашифрован (" + enc + ") — такое не качаем");
        if (segUris.Count == 0)
            throw new Exception("в плейлисте нет сегментов");
        foreach (string u in segUris)
            if (!XsmartOwnUri(u)) throw new Exception("сегмент указывает на чужой хост");
        if (mapUri != null && !XsmartOwnUri(mapUri))
            throw new Exception("init-сегмент указывает на чужой хост");

        int quality = height > 0 ? height : st.quality;
        string dst = Path.Combine(dir, XsmartFileName(it.sref, it.ep, quality));
        string partsDir = dst + ".parts";
        Directory.CreateDirectory(partsDir);
        job.file = Path.GetFileName(dst);
        job.segTotal = segUris.Count;

        // Подпись набора: CDN мог перекодировать тайтл, и тогда старые куски не годятся.
        string sig = XsmartSegSig(segUris);
        string statePath = Path.Combine(partsDir, "state.json");
        try
        {
            if (System.IO.File.Exists(statePath))
            {
                var prev = JObject.Parse(await System.IO.File.ReadAllTextAsync(statePath));
                if (prev.Value<string>("sig") != sig)
                {
                    XsmartNet.Log("grab", "набор сегментов сменился — качаю заново: " + it.sref + " " + it.ep.epkey);
                    foreach (string f in Directory.EnumerateFiles(partsDir)) try { System.IO.File.Delete(f); } catch { }
                }
            }
        }
        catch { }

        var names = new List<string>(segUris.Count);
        for (int i = 0; i < segUris.Count; i++) names.Add(XsmartSegName(i, segUris[i]));

        string mapName = null;
        if (mapUri != null)
        {
            mapName = "init" + Path.GetExtension(XsmartSegName(0, mapUri));
            await XsmartFetchSegment(XsmartNet.Api + mapUri, Path.Combine(partsDir, mapName), job, it);
        }

        long bytes = 0;
        for (int i = 0; i < segUris.Count; i++)
        {
            if (XsmartStale(it) || !XsmartNet.On) { XsmartSetState(job, "canceled"); return; }

            string path = Path.Combine(partsDir, names[i]);
            // ⚠️ Готовность сегмента определяем ФАЙЛОМ, а не счётчиком в state.json: счётчик
            // мог не успеть записаться, а файл пишется через .tmp + Move, то есть появляется
            // только целиком. Так докачка не оставляет обрезанного куска в середине.
            if (!(System.IO.File.Exists(path) && new FileInfo(path).Length > 0))
                await XsmartFetchSegment(XsmartNet.Api + segUris[i], path, job, it);

            try { bytes += new FileInfo(path).Length; } catch { }
            job.seg = i + 1;
            job.done = bytes;

            if ((i & 31) == 31 || i == segUris.Count - 1)
                try
                {
                    await System.IO.File.WriteAllTextAsync(statePath, new JObject
                    {
                        ["sig"] = sig, ["count"] = segUris.Count, ["done"] = i + 1,
                        ["quality"] = quality, ["bytes"] = bytes
                    }.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { }
        }

        // Ремуксу нужен ВТОРОЙ экземпляр контента на диске — сегменты живут до конца склейки.
        string spaceErr = XsmartCheckRemuxSpace(bytes);
        if (spaceErr != null) throw new Exception(spaceErr);

        string localList = Path.Combine(partsDir, "local.m3u8");
        await System.IO.File.WriteAllTextAsync(localList, XsmartLocalPlaylist(mediaText, names, mapName));

        await XsmartRemux(localList, dst, job, it);

        try { Directory.Delete(partsDir, true); } catch { }
        await XsmartFinishFile(it, dst, quality);
    }

    /// <summary>
    /// Один сегмент: в .tmp и только потом Move. Атомарность здесь — не педантизм, а то самое
    /// свойство, на котором держится докачка: обрезанного файла в середине набора не бывает.
    /// </summary>
    static async Task XsmartFetchSegment(string url, string path, XsmartGrabJob job, XsmartGrabItem it)
    {
        int retries = Math.Max(1, ModInit.conf?.xsmartGrabRetries ?? 5);
        int[] backoff = { 2, 5, 15, 30, 30 };
        int idleSec = Math.Max(0, ModInit.conf?.xsmartGrabIdleSec ?? 60);
        string tmp = path + ".tmp";

        for (int attempt = 0; attempt < retries; attempt++)
        {
            if (XsmartStale(it) || !XsmartNet.On) throw new OperationCanceledException();
            try
            {
                using var idle = new CancellationTokenSource();
                if (idleSec > 0) idle.CancelAfter(TimeSpan.FromSeconds(idleSec));

                using var resp = await XsmartNet.Media.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);

                using (var src = await resp.Content.ReadAsStreamAsync(idle.Token))
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                               1 << 20, useAsync: true))
                {
                    byte[] buf = ArrayPool<byte>.Shared.Rent(256 * 1024);
                    try
                    {
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, idle.Token)) > 0)
                        {
                            if (idleSec > 0) idle.CancelAfter(TimeSpan.FromSeconds(idleSec));
                            await fs.WriteAsync(buf, 0, n, idle.Token);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                }

                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
                System.IO.File.Move(tmp, path);
                return;
            }
            catch (Exception ex)
            {
                try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
                if (XsmartStale(it) || !XsmartNet.On) throw new OperationCanceledException();
                if (attempt >= retries - 1) throw new Exception("сегмент не скачался: " + ex.Message);
                job.error = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(backoff[Math.Min(attempt, backoff.Length - 1)]));
            }
        }
    }

    /// <summary>
    /// Место под ремукс. Пик расхода = сегменты + готовый MP4, то есть примерно ДВА размера
    /// тайтла. Проверять это на старте очереди бессмысленно (тогда ещё неизвестно, будет ли
    /// ветка HLS вообще), поэтому проверка стоит здесь, когда цифра уже точная.
    /// </summary>
    static string XsmartCheckRemuxSpace(long bytes)
    {
        try
        {
            var di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(XsmartDownloadRoot())) ?? "/");
            long freeGb = di.AvailableFreeSpace / (1024L * 1024 * 1024);
            long needGb = Math.Max(1, bytes / (1024L * 1024 * 1024));
            int min = Math.Max(1, ModInit.conf?.xsmartMinFreeGb ?? 30);
            if (freeGb - needGb < min)
                return $"мало места под склейку: свободно {freeGb} ГБ, нужно ~{needGb} ГБ + резерв {min} ГБ";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Склейка сегментов в MP4 — ЧИСТЫЙ РЕМУКС (-c copy), картинка не пережимается.
    /// 🔴 Локальный ffmpeg, а НЕ хостовой NVENC-воркер: транскода тут нет вовсе, а воркер
    /// работает через общий диск хостовыми путями (PathMap) — тащить туда временную папку
    /// сегментов значило бы завести второй путь синхронизации на ровном месте.
    /// </summary>
    static async Task XsmartRemux(string localList, string dst, XsmartGrabJob job, XsmartGrabItem it)
    {
        string tmp = dst + ".tmp.mp4";

        // Порядок попыток не случаен. Без -bsf:a mp4-мукс сам вставляет aac_adtstoasc там, где
        // он нужен, и не мешает не-AAC дорожкам (AC3/EAC3 у 4K). Явный фильтр помогает в
        // обратном случае — когда автоподстановка не сработала, — но на AC3 он падает сразу.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (XsmartStale(it) || !XsmartNet.On) throw new OperationCanceledException();
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }

            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-loglevel", "error",
                // Вход — ЛОКАЛЬНЫЙ плейлист: сеть здесь уже не нужна, всё лежит рядом.
                "-allowed_extensions", "ALL",
                "-protocol_whitelist", "file,crypto,data",
                "-i", localList,
                "-map", "0:v:0", "-map", "0:a?",
                "-dn", "-sn", "-map_chapters", "-1"
            };
            if (attempt == 1) args.AddRange(new[] { "-bsf:a", "aac_adtstoasc" });
            args.AddRange(new[]
            {
                "-c", "copy",
                "-movflags", "+faststart",
                "-f", "mp4",
                "-progress", "pipe:1", "-nostats",
                tmp
            });

            var ff = FfJob.StartLocal(args);
            var wait = ff.WaitForExitAsync();
            while (!ff.HasExited)
            {
                var finished = await Task.WhenAny(wait, Task.Delay(2000));
                if (finished != wait && (XsmartStale(it) || !XsmartNet.On))
                {
                    ff.Kill();
                    throw new OperationCanceledException();
                }
            }
            await wait;

            if (ff.ExitCode == 0 && System.IO.File.Exists(tmp) && new FileInfo(tmp).Length > 0)
            {
                try { if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst); } catch { }
                System.IO.File.Move(tmp, dst);
                return;
            }

            XsmartNet.Log("remux", it.sref + " " + it.ep.epkey + " попытка " + (attempt + 1)
                                   + " → код " + ff.ExitCode + ": " + (ff.StderrTail ?? "").Trim());
        }

        try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
        // Сегменты НЕ трогаем: они дорого достались, а причина отказа может быть внешней
        // (кончилось место) — следующий запуск склеит их без единого сетевого запроса.
        throw new Exception("склейка не удалась (ffmpeg)");
    }

    #endregion

    #region маркер, мета, постер

    /// <summary>
    /// Ключ таймлайна одной единицы — БЕЗ префикса «qdltl:», его добавляет клиент.
    ///
    /// 🔴 Формат обязан совпасть с тем, что строит плагин раздела (normalize.timelineKey):
    /// только тогда прогресс онлайн-просмотра и скачанной копии — это одна и та же отметка.
    /// Поэтому здесь ИДЕНТИФИКАТОРЫ XSMART (s32215e524438), а не порядковые номера из имени файла.
    /// </summary>
    internal static string XsmartTlKey(int cat, string id, XsmartEp e)
    {
        string bas = "xsmart:" + cat + ":" + id;
        return e != null && e.kind == XsmartKind.Episode && !string.IsNullOrEmpty(e.seasonId)
            ? bas + ":s" + e.seasonId + "e" + e.epId
            : bas;
    }

    /// <summary>Единица тайтла по ключу имени файла (s1e5 / film) — из кеша карточки.</summary>
    static XsmartEp XsmartEpByKey(int cat, string id, string epkey)
    {
        var t = XsmartTitleFromCache(cat, id);
        return t?.items.FirstOrDefault(x => x.epkey == epkey);
    }

    /// <summary>Мету и постер пишем при постановке в очередь — карточка появляется сразу.</summary>
    static async Task XsmartEnsureMeta(XsmartTitle t)
    {
        string hash = XsmartNet.Hash(t.cat, t.id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetaPath(hash)));
            if (!System.IO.File.Exists(MetaPath(hash)))
            {
                // ⚠️ "source":"xsmart" — это ПОЯС 2 изоляции: IndexCrawler по нему пропускает
                // такие меты и не идёт за ними на трекеры.
                SaveMeta(hash, new JObject
                {
                    ["source"] = "xsmart",
                    ["xsmart_cat"] = t.cat,
                    ["xsmart_id"] = t.id,
                    ["title"] = t.title ?? XsmartNet.Ref(t.cat, t.id),
                    ["original_title"] = t.titleOrig,
                    ["year"] = t.year,
                    ["id"] = 0,
                    ["media_type"] = t.series ? "tv" : "movie",
                    ["overview"] = t.descr
                });
            }
        }
        catch { }

        // Постер обязан лежать по HASH-пути: /qdl/list смотрит строго /qdl-data/img/<hash>.jpg.
        try
        {
            string pp = PosterPath(hash);
            if (System.IO.File.Exists(pp) || string.IsNullOrEmpty(t.poster)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(pp));

            // Картинка тоже берётся у прокси (он её кеширует), а не у XSMART напрямую.
            string url = t.poster.StartsWith("/", StringComparison.Ordinal) ? XsmartNet.Api + t.poster : null;
            if (url == null) return;
            byte[] img = await XsmartNet.Media.GetByteArrayAsync(url);
            if (img != null && img.Length > 128)
            {
                await System.IO.File.WriteAllBytesAsync(pp, img);
                PosterWritten();   // снимок каталога img/ устарел — иначе has_poster врёт до рестарта (§BV)
            }
        }
        catch { }
    }

    static async Task XsmartFinishFile(XsmartGrabItem it, string dst, int quality)
    {
        string hash = XsmartNet.Hash(it.cat, it.id);
        try
        {
            // 🔥 Инкрементально: полный обход каталога на КАЖДУЮ серию — это O(N²) за прогон
            // (разобрано на jut). Известный список правим одной записью.
            var prev = LoadLocal(hash);
            var known = new SortedDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (prev?["files"] is JArray parr)
            {
                foreach (var f in parr.OfType<JObject>())
                {
                    string p = f.Value<string>("path");
                    if (!string.IsNullOrEmpty(p)) known[p] = f;
                }
            }
            else
            {
                foreach (string f in Directory.EnumerateFiles(XsmartTitleDir(it.sref), "*.mp4"))
                {
                    string key = XsmartKeyFromName(Path.GetFileNameWithoutExtension(f));
                    known[f.Replace('\\', '/')] = new JObject
                    {
                        ["name"] = Path.GetFileName(f),
                        ["path"] = f.Replace('\\', '/'),
                        ["size"] = SafeLen(f),
                        ["tl"] = XsmartTlKey(it.cat, it.id, key == null ? null : XsmartEpByKey(it.cat, it.id, key))
                    };
                }
            }

            known[dst.Replace('\\', '/')] = new JObject
            {
                ["name"] = Path.GetFileName(dst),
                ["path"] = dst.Replace('\\', '/'),
                ["size"] = SafeLen(dst),
                ["tl"] = XsmartTlKey(it.cat, it.id, it.ep)
            };

            var files = new JArray();
            long size = 0;
            int idx = 0;
            foreach (var kv in known)
            {
                kv.Value["index"] = idx++;
                files.Add(kv.Value);
                size += kv.Value.Value<long?>("size") ?? 0;
            }

            SaveLocal(hash, new JObject
            {
                ["name"] = it.titleRu ?? it.sref,
                ["dir"] = XsmartTitleDir(it.sref).Replace('\\', '/'),
                ["size"] = size,
                ["added"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["overlay"] = false,
                ["files"] = files,
                // Своё поле: LocalFiles лишнее игнорирует.
                ["xsmart"] = new JObject
                {
                    ["cat"] = it.cat,
                    ["id"] = it.id,
                    ["ref"] = it.sref,
                    ["tlPrefix"] = "xsmart:" + it.cat + ":" + it.id,
                    ["titleRu"] = it.titleRu,
                    ["source"] = it.source,
                    ["quality"] = quality
                }
            });

            JsonStore.ForgetDir(XsmartTitleDir(it.sref));   // в каталоге тайтла появился новый mp4
            XsmartDropDiskKeys(it.sref);                    // и снимок «что уже скачано» устарел
        }
        catch (Exception ex) { XsmartNet.Log("grab", "маркер: " + ex.Message); }

        // ⚠️ Обязательно: иначе /qdl/stream продолжит отдавать по устаревшему пути
        try { DropResolveCache(hash); } catch { }

        var job = _xsJobs.TryGetValue(it.sref, out var j) ? j : null;
        if (job != null)
        {
            job.fileDone++;
            // Порог 1, а не 0: текущая единица ещё числится в _xsQueued (XsmartForget зовётся
            // позже, в finally воркера). Иначе полностью скачанный первый тайтл навсегда
            // оставался бы "running", пока качается второй.
            XsmartSetState(job, XsmartPendingFor(it.sref) <= 1 ? "done" : "running");
        }
        XsmartNotifyDone(it, hash);
    }

    static long SafeLen(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    #endregion

    #region уведомления

    // Префикс ключа серии: «x» + <cat>-<id>. Не пересекается ни с торрентными t<tmdbId>/l<fnv>,
    // ни с jut-овским j<slug>, поэтому UNIQUE noti(seriesKey, epkey) ничего не схлопнет.
    static string XsmartSeriesKey(string sref) => "x" + sref;

    /// <summary>ref тайтла из ключа серии — нужен клиенту, чтобы тап по уведомлению открыл раздел.</summary>
    internal static string XsmartRefFromSeriesKey(string seriesKey)
    {
        if (string.IsNullOrEmpty(seriesKey) || seriesKey.Length < 2 || seriesKey[0] != 'x') return null;
        string sref = seriesKey.Substring(1);
        var parts = sref.Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], out int cat) && XsmartNet.Valid(cat, parts[1])
            ? sref : null;
    }

    static void XsmartNotifyStart(string sref, string title, int count)
    {
        try
        {
            using var db = new SqlContext();
            string sk = XsmartSeriesKey(sref);
            string dedup = "start-" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == dedup)) return;

            var parts = sref.Split('-');
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0,
                hash = XsmartNet.Hash(int.Parse(parts[0]), parts[1]),
                title = title ?? sref, season = -1, episode = -1,
                kind = "START", epkey = dedup,
                label = "В очереди на скачивание: " + count,
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
        }
        catch { }
    }

    static void XsmartNotifyDone(XsmartGrabItem it, string hash)
    {
        try
        {
            // 🔥 В режиме агрегата строки на серию НЕ пишутся: сериал на 60 серий давал бы
            // 60 записей в ленте и 60 тостов. Дедуп seen ведём ВСЕГДА — он про «уже
            // уведомляли», и на нём же держится «уже скачано».
            _xsJobs.TryGetValue(it.sref, out var job);
            bool agg = job?.agg == true;

            using var db = new SqlContext();
            string sk = XsmartSeriesKey(it.sref);
            if (!db.seen.Any(x => x.seriesKey == sk && x.epkey == it.ep.epkey))
                db.seen.Add(new SeenModel { seriesKey = sk, epkey = it.ep.epkey });

            if (!agg && !db.noti.Any(x => x.seriesKey == sk && x.epkey == it.ep.epkey))
            {
                bool film = it.ep.kind == XsmartKind.Film;
                db.noti.Add(new NotiModel
                {
                    seriesKey = sk, seriesId = 0, hash = hash,
                    title = it.titleRu ?? it.sref,
                    season = film ? -1 : it.ep.seasonNo,
                    episode = film ? -1 : it.ep.epNo,
                    kind = film ? "FILM" : null,
                    epkey = it.ep.epkey,
                    label = film ? "Фильм скачан" : $"Сезон {it.ep.seasonNo} · серия {it.ep.epNo}",
                    created = DateTime.UtcNow, read = false
                });
            }
            db.SaveChanges();
            if (!agg) XsmartPushDone(it.sref);
            Console.WriteLine("[QbitDownload] xsmart/grab: скачано " + it.sref + " " + it.ep.epkey);
        }
        catch (Exception ex) { XsmartNet.Log("grab", "noti: " + ex.Message); }
    }

    /// <summary>
    /// Одна строка на всю пачку. Зовётся из finally воркера, когда очередь тайтла осушена
    /// (успехом, ошибкой или отменой), поэтому путь ровно один; идемпотентность — aggFlushed.
    /// ⚠️ epkey батча не должен пересечься ни с ключами единиц (s1e7, film), ни с ключами
    /// слежения (new-*, season-*, start-*).
    /// </summary>
    static void XsmartNotifyTitleDone(XsmartGrabItem it, XsmartGrabJob job)
    {
        if (job == null) return;
        lock (job)
        {
            // fileDone == 0 — качать было нечего или всё упало: «скачано 0» это шум, а не новость
            if (!job.agg || job.aggFlushed || job.fileDone <= 0) return;
            job.aggFlushed = true;
        }

        try
        {
            int done = job.fileDone, total = job.filesTotal;
            using var db = new SqlContext();
            db.noti.Add(new NotiModel
            {
                seriesKey = XsmartSeriesKey(it.sref), seriesId = 0,
                hash = XsmartNet.Hash(it.cat, it.id),
                title = it.titleRu ?? it.sref,
                season = -1, episode = -1,
                kind = "TITLE",
                epkey = "batch-" + DateTime.UtcNow.Ticks,
                // Недокачанное честно видно: отмена и «сдался после ретраев» дают N < M
                label = done < total ? $"Скачано серий: {done} из {total}" : $"Скачано серий: {done}",
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
            Console.WriteLine("[QbitDownload] xsmart/grab: тайтл " + it.sref + " — скачано " + done + "/" + total);
        }
        catch (Exception ex) { XsmartNet.Log("grab", "noti тайтла: " + ex.Message); }
    }

    static DateTime _xsLastPush = DateTime.MinValue;
    static readonly object _xsPushLock = new();

    /// <summary>
    /// Коалесер WS-пуша. Строки в noti создаются на КАЖДУЮ единицу (на этом держится точность
    /// ленты), троттлится только СИГНАЛ: каждый PushNotiSignal заставляет КАЖДОГО клиента
    /// выгрузить всю ленту и напечатать тост.
    /// ⚠️ Последняя единица пачки пушится НЕМЕДЛЕННО, и «пачка кончилась» определяется по
    /// очереди ЭТОГО тайтла, а не по глобальной.
    /// </summary>
    static void XsmartPushDone(string sref)
    {
        int coalesce = Math.Max(0, ModInit.conf?.xsmartNotifyCoalesceSec ?? 300);
        bool last = XsmartPendingFor(sref) <= 1;

        if (coalesce == 0 || last)
        {
            lock (_xsPushLock) _xsLastPush = DateTime.UtcNow;
            PushNotiSignal(1);
            return;
        }
        lock (_xsPushLock)
        {
            if ((DateTime.UtcNow - _xsLastPush).TotalSeconds < coalesce) return;
            _xsLastPush = DateTime.UtcNow;
        }
        PushNotiSignal(1);
    }

    #endregion

    #region отметка в «Загрузках» и реконсиляция

    /// <summary>
    /// Отметка xsmart-карточки в /qdl/list: cat/id + режим подписки.
    /// Вынесено из Controller.List, чтобы контракт проверялся тестом (сам экшен требует
    /// живого qBittorrent и HttpContext). Режим кладём ВНУТРЬ xsmart, а не в корень:
    /// корневые поля общие с торрентной ветвью, там «watch» читалось бы как торрентное слежение.
    /// </summary>
    internal static void XsmartDecorateListItem(JObject item, JObject loc,
                                                IReadOnlyDictionary<string, string> modes)
    {
        if (loc?["xsmart"] is not JObject xs) return;
        string sref = xs.Value<string>("ref");
        if (string.IsNullOrEmpty(sref)) return;

        string mode = modes != null && modes.TryGetValue(sref, out string m) ? m : "off";
        item["xsmart"] = new JObject
        {
            ["cat"] = xs.Value<int?>("cat") ?? 0,
            ["id"] = xs.Value<string>("id"),
            ["ref"] = sref,
            ["watch"] = mode
        };
        item["watched"] = mode != "off";
    }

    /// <summary>
    /// Реконсиляция при старте: маркер приводим к тому, что реально лежит на диске, а
    /// незаконченное — обратно в очередь.
    ///
    /// 🔥 Нужна ровно из-за того, ради чего вся посегментная качалка и писалась: контейнер
    /// перезапускают часто (Roslyn-сборка модуля), и без этого прохода начатый 4K-фильм
    /// лежал бы мёртвым набором сегментов, пока кто-нибудь не нажмёт «Скачать» ещё раз.
    /// </summary>
    internal static async Task XsmartReconcile()
    {
        if (!XsmartNet.On) return;
        string root = XsmartDownloadRoot();
        if (!Directory.Exists(root)) return;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string sref = Path.GetFileName(dir);
            var parts = sref.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int cat) || !XsmartNet.Valid(cat, parts[1]))
                continue;
            string id = parts[1];

            try
            {
                XsmartDropDiskKeys(sref);
                var t = XsmartTitleFromCache(cat, id);

                // 1. Маркер по факту диска: файл могли докопировать руками, а серию — удалить.
                var files = new JArray();
                long size = 0;
                int idx = 0;
                foreach (string f in Directory.EnumerateFiles(dir, "*.mp4").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string key = XsmartKeyFromName(Path.GetFileNameWithoutExtension(f));
                    long len = SafeLen(f);
                    files.Add(new JObject
                    {
                        ["index"] = idx++,
                        ["name"] = Path.GetFileName(f),
                        ["path"] = f.Replace('\\', '/'),
                        ["size"] = len,
                        ["tl"] = XsmartTlKey(cat, id, key == null ? null : t?.items.FirstOrDefault(x => x.epkey == key))
                    });
                    size += len;
                }

                string hash = XsmartNet.Hash(cat, id);
                var loc = LoadLocal(hash);
                if (files.Count > 0)
                {
                    var xs = loc?["xsmart"] as JObject;
                    SaveLocal(hash, new JObject
                    {
                        ["name"] = xs?.Value<string>("titleRu") ?? t?.title ?? sref,
                        ["dir"] = dir.Replace('\\', '/'),
                        ["size"] = size,
                        ["added"] = loc?.Value<long?>("added") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["overlay"] = false,
                        ["files"] = files,
                        ["xsmart"] = new JObject
                        {
                            ["cat"] = cat, ["id"] = id, ["ref"] = sref,
                            ["tlPrefix"] = "xsmart:" + cat + ":" + id,
                            ["titleRu"] = xs?.Value<string>("titleRu") ?? t?.title,
                            ["source"] = xs?.Value<string>("source") ?? t?.source,
                            ["quality"] = xs?.Value<int?>("quality") ?? 0
                        }
                    });
                }

                // 2. Недокачанное — обратно в очередь. Единицу опознаём по имени НЕДОКАЧКИ:
                // «<ref>.s01e05.1080p.mp4.part» и «…​.mp4.parts» несут тот же ключ, что и готовый файл.
                if (t == null) continue;
                foreach (string leftover in Directory.EnumerateFileSystemEntries(dir)
                                                     .Where(x => x.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                                              || x.EndsWith(".parts", StringComparison.OrdinalIgnoreCase)))
                {
                    string baseName = Path.GetFileName(leftover);
                    baseName = baseName.Substring(0, baseName.LastIndexOf('.'));            // снять .part/.parts
                    string key = XsmartKeyFromName(Path.GetFileNameWithoutExtension(baseName));
                    if (key == null) continue;
                    if (XsmartDiskKeys(sref).Contains(key)) continue;                        // уже готово, хвост уберём ниже

                    var ep = t.items.FirstOrDefault(x => x.epkey == key);
                    if (ep == null) continue;

                    lock (_xsEnqLock)
                    {
                        if (!_xsQueued.Add(XsmartQueueKey(sref, key))) continue;
                    }
                    _xsQueue.Enqueue(new XsmartGrabItem
                    {
                        cat = cat, id = id, sref = sref, source = t.source,
                        ep = ep, titleRu = t.title, gen = XsmartGenOf(sref)
                    });
                    XsmartJobForBatch(sref, XsmartPendingFor(sref) <= 1, 1);
                    XsmartNet.Log("reconcile", "докачиваю " + sref + " " + key);
                }
            }
            catch (Exception ex) { XsmartNet.Log("reconcile", sref + ": " + ex.Message); }
        }

        if (!_xsQueue.IsEmpty) XsmartKickWorker();
        await Task.CompletedTask;
    }

    #endregion
}
