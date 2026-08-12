using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Скачивание аниме с jut.su в общий раздел «Загрузки».
//
// 🔥 Мост в «Загрузки» бесплатный: /qdl/list строит карточку из local/<hash>.json при
// единственном условии ValidHash. Псевдо-infohash sha1("jutsu:"+slug) его проходит, поэтому
// без единой правки работают /qdl/stream, /qdl/episodes, /qdl/hls, коллекции, удаление.
//
// ⚠️ ИНВАРИАНТ ИЗОЛЯЦИИ: links/<hash>.json для jut НЕ создаём НИКОГДА. На этом держится
// пояс 1 — WatchAdd (Controller.cs:2950) без этого файла отвечает {"success":false,"no link"},
// то есть добавить jut-тайтл в ТОРРЕНТНУЮ охоту физически невозможно. Не «на всякий случай»
// дописать сюда LinkPath — это тихо сломает требование владельца «за сериями jut в торренты не лезть».
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region модель очереди

    sealed class JutGrabItem
    {
        public string slug, kind, dstPath, partPath;
        public int season, ep;
        public string epkey;
        public string titleRu;
        public volatile bool cancel;
        public int gen;                            // поколение отмены на момент постановки (см. JutStale)
    }

    sealed class JutGrabJob
    {
        public volatile string state = "queued";   // queued | running | done | error | canceled
        public volatile string file;
        public volatile string error;
        public volatile bool canceled;             // взведён отменой; гасит запись state до новой пачки
        public long done, total;
        public int fileDone, filesTotal;
        public DateTime touched = DateTime.UtcNow; // для уборки терминальных job (иначе словарь пухнет)
    }

    static readonly ConcurrentQueue<JutGrabItem> _jutQueue = new();
    static readonly HashSet<string> _jutQueued = new(StringComparer.Ordinal);
    static readonly object _jutEnqLock = new();
    static readonly ConcurrentDictionary<string, JutGrabJob> _jutJobs = new();   // slug → job
    static readonly ConcurrentDictionary<string, int> _jutGen = new();           // slug → поколение отмены
    static int _jutWorker = 0;

    static string JutQueueKey(string slug, string epkey) => slug + ":" + epkey;

    static int JutGenOf(string slug) => _jutGen.TryGetValue(slug, out int g) ? g : 0;

    /// <summary>
    /// «Элемент устарел»: отменён явно или пережил отмену своего тайтла.
    /// 🔥 Поколение нужно именно потому, что отмена НЕ вынимает элементы из ConcurrentQueue —
    /// она может только пометить их. Без поколения последовательность «отменил → сразу добавил
    /// заново» ломалась: старый элемент доходил до воркера и его JutForget снимал ключ,
    /// только что поставленный НОВЫМ запросом, после чего серия молча не скачивалась.
    /// </summary>
    static bool JutStale(JutGrabItem it) => it.cancel || it.gen != JutGenOf(it.slug);

    /// <summary>Сколько серий этого тайтла ещё висит в очереди (включая текущую в работе).</summary>
    static int JutPendingFor(string slug)
    {
        string p = slug + ":";
        lock (_jutEnqLock)
        {
            int n = 0;
            foreach (string k in _jutQueued)
                if (k.StartsWith(p, StringComparison.Ordinal)) n++;
            return n;
        }
    }

    /// <summary>
    /// Запись состояния job. ⚠️ Молчит, пока взведён canceled: иначе серия, которая была уже
    /// в работе в момент отмены (её нет в очереди — она из неё вынута, и пометить её
    /// JutDownloadCancel не мог), доигрывала до конца и затирала "canceled" на "done".
    /// </summary>
    static void JutSetState(JutGrabJob job, string state)
    {
        job.touched = DateTime.UtcNow;
        if (job.canceled && state != "canceled") return;
        job.state = state;
    }

    /// <summary>Уборка терминальных job: словарь живёт всё время процесса и иначе только растёт.</summary>
    static void JutPruneJobs()
    {
        var edge = DateTime.UtcNow.AddHours(-6);
        foreach (var kv in _jutJobs)
        {
            if (kv.Value.touched > edge) continue;
            if (kv.Value.state is "done" or "error" or "canceled")
                _jutJobs.TryRemove(kv.Key, out _);
        }
    }

    #endregion

    #region пути и имена

    internal static string JutDownloadRoot()
        => ModInit.conf?.jutDownloadsPath ?? "/downloads/jutsu";

    internal static string JutTitleDir(string slug) => Path.Combine(JutDownloadRoot(), slug);

    /// <summary>
    /// Имя даёт бесплатный разбор существующим ParseEp → s1e7 на экране серий.
    /// ⚠️ ParseEp не знает слова «film», поэтому фильмы/OVA несут kind в имени и в маркере.
    /// </summary>
    internal static string JutFileName(string slug, JutEp e, int quality)
    {
        string q = quality > 0 ? "." + quality + "p" : "";
        return e.kind switch
        {
            JutEpKind.Film => $"{slug}.film{e.num}{q}.mp4",
            JutEpKind.Ova => $"{slug}.ova{e.num}{q}.mp4",
            JutEpKind.GameOva => $"{slug}.gameova{e.num}{q}.mp4",
            JutEpKind.Special => $"{slug}.sp{e.num}{q}.mp4",
            _ => $"{slug}.s{e.season:00}e{e.num:00}{q}.mp4"
        };
    }

    static string JutKindParam(JutEpKind k) => k switch
    {
        JutEpKind.Film => "film",
        JutEpKind.Ova => "ova",
        JutEpKind.GameOva => "game-ova",
        JutEpKind.Special => "special",
        _ => "episode"
    };

    #endregion

    #region что качать = diff(сайт, диск)

    /// <summary>
    /// 🔥 Источник истины — сравнение «серии на сайте» с «файлы на диске», а НЕ known.keys
    /// слежения. Иначе рестарт между постановкой в очередь и завершением файла терял бы серию
    /// навсегда: суточный тик больше не считал бы её новой.
    /// </summary>
    /// <remarks>
    /// Канонический ключ серии = имя файла БЕЗ качества (1080p/720p у одной серии могут
    /// отличаться). Ключ с диска получаем тем же <see cref="JutEpFromFileName"/>, которым
    /// разбираем имена в реконсиляции — то есть сравнение симметрично по построению.
    /// </remarks>
    static string JutEpKey(string slug, JutEp e) => JutFileName(slug, e, 0);

    // Снимок ключей на диске, один на тайтл.
    sealed class JutDiskSnap { public DateTime at; public HashSet<string> keys; }
    static readonly ConcurrentDictionary<string, JutDiskSnap> _jutDisk =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 🔥 Один снимок каталога на весь запрос вместо Directory.EnumerateFiles НА КАЖДУЮ СЕРИЮ.
    /// Было O(N×M) обращений к ФС: «скачать весь тайтл» на 500 серий при 500 файлах на диске
    /// давало четверть миллиона syscall'ов внутри HTTP-запроса, на bind-маунте через drvfs.
    /// </summary>
    /// <remarks>
    /// ⚠️ Снимок обязан быть С TTL, а не бессрочным. Достаточно схлопнуть обращения внутри
    /// одного запроса/тика, а файлы появляются не только через JutFinishFile (реконсиляция,
    /// docker cp, ручное копирование) — бессрочный снимок в таких случаях залипал бы
    /// на «ничего не скачано» и заново качал уже лежащее.
    /// </remarks>
    static HashSet<string> JutDiskKeys(string slug)
    {
        if (_jutDisk.TryGetValue(slug, out var hit)
            && (DateTime.UtcNow - hit.at).TotalSeconds < 10)
            return hit.keys;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string dir = JutTitleDir(slug);
            if (Directory.Exists(dir))
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.mp4"))
                {
                    var e = JutEpFromFileName(Path.GetFileNameWithoutExtension(f));
                    if (e != null) keys.Add(JutEpKey(slug, e));
                }
            }
        }
        catch { }
        _jutDisk[slug] = new JutDiskSnap { at = DateTime.UtcNow, keys = keys };
        return keys;
    }

    static void JutDropDiskKeys(string slug) => _jutDisk.TryRemove(slug, out _);

    internal static void JutDropAllDiskKeys() => _jutDisk.Clear();

    /// <summary>
    /// ⚠️ Сравнение ТОЧНОЕ, а не по префиксу. Прежний StartsWith врал на трёхзначных номерах:
    /// имя строится как s{season:00}e{num:00}, поэтому «s01e100».StartsWith("s01e10") == true —
    /// проверка серии 10 видела файл серии 100 и отвечала «уже скачано». На длинных тайтлах
    /// часть серий молча не вставала в очередь и не скачивалась никогда. То же было
    /// у film1/film10 и ova1/ova10.
    /// </summary>
    static bool JutHaveFile(string slug, JutEp e) => JutDiskKeys(slug).Contains(JutEpKey(slug, e));

    #endregion

    #region постановка в очередь

    static JutEpKind JutKindFromString(string s) => (s ?? "").ToLowerInvariant() switch
    {
        "film" => JutEpKind.Film,
        "ova" => JutEpKind.Ova,
        "gameova" or "game-ova" => JutEpKind.GameOva,
        "special" => JutEpKind.Special,
        _ => JutEpKind.Episode
    };

    /// <summary>
    /// Восстановить тайтл из кеша (<c>title/&lt;slug&gt;.json</c>), не ходя в сеть.
    /// 🔥 Ради этого и живёт весь метод: /qdl/jut/download раньше звал JutLoadTitle напрямую,
    /// МИМО кеша, а для хаб-тайтла (Наруто и прочие длинные франшизы) это 1 + до 24
    /// ПОСЛЕДОВАТЕЛЬНЫХ запросов на jut.su через гейт из 3 слотов. Живой замер одного запроса
    /// к сайту — 1142 мс (/qdl/jut/diag), то есть до ~25 с блокирующей работы внутри HTTP-запроса
    /// при клиентском таймауте 45 с. Отсюда «сервер подлагивает, когда добавляю всё аниме».
    /// ⚠️ TTL берём бесконечный: просроченный список серий для СКАЧИВАНИЯ годится — недостающее
    /// доберут суточный тик слежения и реконсиляция. Свежесть тут не стоит 25 секунд ожидания.
    /// </summary>
    static JutTitle JutTitleFromCache(string slug)
    {
        try
        {
            var jo = JutCacheRead("title", slug, TimeSpan.MaxValue, out _);
            if (jo?["items"] is not JArray arr || arr.Count == 0) return null;

            var t = new JutTitle
            {
                slug = slug,
                titleRu = jo.Value<string>("title"),
                titleOrig = jo.Value<string>("original"),
                poster = jo.Value<string>("poster"),
                descr = jo.Value<string>("descr"),
                ongoing = jo.Value<bool?>("ongoing") ?? false
            };
            if (jo["years"] is JArray ys)
                foreach (var y in ys) { int v = y.Value<int?>() ?? 0; if (v > 0) t.years.Add(v); }

            foreach (var e in arr)
            {
                t.items.Add(new JutEp
                {
                    kind = JutKindFromString(e.Value<string>("kind")),
                    season = e.Value<int?>("season") ?? 1,
                    num = e.Value<int?>("ep") ?? 0,
                    url = e.Value<string>("url"),
                    name = e.Value<string>("name")
                });
            }
            return t.items.Count > 0 ? t : null;
        }
        catch { return null; }
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/jut/download")]
    async public Task<ActionResult> JutDownload(string slug, int season = 0, int ep = 0,
                                                string kind = null, string scope = "one")
    {
        if (!JutOn) return JutErr("DISABLED");
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");

        // Сначала кеш — он покрывает подавляющее большинство случаев: пользователь жмёт
        // «Скачать» с уже открытой карточки, а её открытие кеш и заполнило.
        var t = JutTitleFromCache(slug);
        if (t == null)
        {
            var (loaded, err) = await JutLoadTitle(slug, false);
            if (loaded == null) return JutErr(err ?? "SITE_DOWN");
            t = loaded;
            // положим в кеш, чтобы следующее «Скачать» уже не платило за сеть
            try { JutCacheWrite("title", slug, JutTitleJson(t)); } catch { }
        }

        var want = new List<JutEp>();
        if (scope == "one")
        {
            string k = string.IsNullOrEmpty(kind) ? "episode" : kind;
            var hit = t.items.FirstOrDefault(x => x.num == ep && JutKindParam(x.kind) == k
                                                  && (x.kind != JutEpKind.Episode || x.season == Math.Max(1, season)));
            if (hit == null) return JutErr("NOT_FOUND", "Серия не найдена в списке");
            want.Add(hit);
        }
        else if (scope == "season")
        {
            int s = Math.Max(1, season);
            want.AddRange(t.items.Where(x => x.kind == JutEpKind.Episode && x.season == s));
        }
        else want.AddRange(t.items);

        // «Новая пачка» = этого тайтла в очереди сейчас ничего нет. Только в этот момент можно
        // обнулять счётчики: иначе прогресс складывался бы с прошлым прогоном и врал (>100%).
        bool freshBatch = JutPendingFor(slug) == 0;

        // ⚠️ Отсев ДО гарда места. Раньше JutCheckSpace считался по want.Count, то есть по всему
        // тайтлу: 500 серий требовали 250 ГБ + резерв, даже если 499 уже лежали на диске, —
        // и запрос отбивался целиком, ничего не поставив. Теперь считаем только то, что реально
        // качать, и учитываем уже стоящее в очереди.
        var disk = JutDiskKeys(slug);
        int already = 0, duplicate = 0;
        var toGrab = new List<JutEp>();
        foreach (var e in want)
        {
            if (disk.Contains(JutEpKey(slug, e))) { already++; continue; }
            toGrab.Add(e);
        }

        // Гард свободного места. Важно не ради экономии: на D: живут торренты qBittorrent
        // и записи регистратора — забитый под ноль диск ломает их, а не только нас.
        string freeErr = JutCheckSpace(toGrab.Count + _jutQueue.Count, slug);
        if (freeErr != null) return JutErr("NO_SPACE", freeErr);

        int queued = 0;
        int gen = JutGenOf(slug);
        // ⚠️ Job НЕ создаём заранее. GetOrAdd до цикла оставлял фантом: у тайтла, где всё уже
        // скачано, в /qdl/jut/download/status появлялась запись state="queued" с filesTotal=0,
        // и статус врал про работу, которой нет.
        JutGrabJob job = null;
        foreach (var e in toGrab)
        {
            lock (_jutEnqLock)
            {
                if (!_jutQueued.Add(JutQueueKey(slug, e.epkey))) { duplicate++; continue; }
            }
            _jutQueue.Enqueue(new JutGrabItem
            {
                slug = slug, season = e.season, ep = e.num, kind = JutKindParam(e.kind),
                epkey = e.epkey, titleRu = t.titleRu, gen = gen
            });
            queued++;
        }

        if (queued > 0)
        {
            job = _jutJobs.GetOrAdd(slug, _ => new JutGrabJob());
            if (freshBatch)
            {
                job.fileDone = 0; job.filesTotal = 0; job.done = 0; job.total = 0;
                job.error = null; job.canceled = false;
            }
            job.filesTotal += queued;
            JutSetState(job, "queued");
            // Мета/постер пишем СРАЗУ: иначе первые минуты скачивания выглядят как «ничего не происходит»
            await JutEnsureMeta(slug, t);
            JutNotifyStart(slug, t.titleRu, queued);
            JutKickWorker();
        }

        return JutJson(new JObject
        {
            ["ok"] = true, ["queued"] = queued, ["already"] = already,
            ["duplicate"] = duplicate, ["pending"] = JutPendingFor(slug),
            ["hash"] = JutNet.Hash(slug), ["scope"] = scope,
            ["message"] = JutQueueMessage(queued, already, duplicate, JutPendingFor(slug))
        });
    }

    /// <summary>
    /// 🔥 Три исхода, которые раньше были неотличимы: «поставлено», «уже на диске», «уже в очереди».
    /// Все три давали queued=0 и клиентский тост «В очереди на скачивание: 0» (qdl.js), из-за чего
    /// повторное добавление выглядело как «ничего не произошло».
    /// </summary>
    static string JutQueueMessage(int queued, int already, int duplicate, int pending)
    {
        if (queued > 0)
        {
            string s = "Поставлено в очередь: " + queued;
            if (already > 0) s += " · уже на диске: " + already;
            if (duplicate > 0) s += " · уже в очереди: " + duplicate;
            return s;
        }
        if (duplicate > 0) return "Уже в очереди (" + duplicate + ") — качается, осталось " + pending;
        if (already > 0) return "Всё уже скачано (" + already + ")";
        return "Нечего скачивать";
    }

    /// <summary>
    /// Гард свободного места. <paramref name="files"/> — сколько РЕАЛЬНО качать (после отсева
    /// уже скачанного) плюс то, что стоит в очереди по другим тайтлам: раньше гард не знал
    /// про очередь вовсе и два больших тайтла подряд проходили оба.
    /// </summary>
    static string JutCheckSpace(int files, string slug = null)
    {
        if (files <= 0) return null;
        try
        {
            string root = JutDownloadRoot();
            Directory.CreateDirectory(root);
            var di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? "/");
            long freeGb = di.AvailableFreeSpace / (1024L * 1024 * 1024);
            int min = Math.Max(1, ModInit.conf?.jutMinFreeGb ?? 20);
            double perFile = JutAvgFileGb(slug);
            long needGb = Math.Max(1, (long)Math.Ceiling(files * perFile));
            if (freeGb - needGb < min)
                return $"мало места: свободно {freeGb} ГБ, нужно ~{needGb} ГБ + резерв {min} ГБ";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Средний размер серии ЭТОГО тайтла по уже скачанному — оценка точнее константы
    /// «0.5 ГБ на серию»: у 720p-тайтла она завышена вдвое, у длинных фильмов занижена.
    /// Фолбэк 0.5 ГБ, пока на диске ничего нет.
    /// </summary>
    static double JutAvgFileGb(string slug)
    {
        const double fallback = 0.5;
        if (string.IsNullOrEmpty(slug)) return fallback;
        try
        {
            var loc = LoadLocal(JutNet.Hash(slug));
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
    [Route("qdl/jut/download/status")]
    public ActionResult JutDownloadStatus(string slug = null)
    {
        JutPruneJobs();
        if (!string.IsNullOrEmpty(slug))
        {
            if (!_jutJobs.TryGetValue(slug, out var j))
                return JutJson(new JObject { ["ok"] = true, ["state"] = "idle", ["pending"] = 0 });
            return JutJson(JutJobJson(slug, j));
        }
        var arr = new JArray(_jutJobs.Select(kv => JutJobJson(kv.Key, kv.Value)));
        return JutJson(new JObject { ["ok"] = true, ["queue"] = _jutQueue.Count, ["jobs"] = arr });
    }

    static JObject JutJobJson(string slug, JutGrabJob j) => new JObject
    {
        ["ok"] = true,
        ["slug"] = slug,
        ["state"] = j.state,
        ["file"] = j.file,
        ["fileDone"] = j.fileDone,
        ["filesTotal"] = j.filesTotal,
        ["done"] = j.done,
        ["total"] = j.total,
        ["progress"] = j.total > 0 ? Math.Round((double)j.done / j.total, 3) : 0,
        // Осталось серий у ЭТОГО тайтла и место в общей очереди — без них «второй тайтл ждёт
        // первого» неотличимо от «ничего не добавилось».
        ["pending"] = JutPendingFor(slug),
        ["queueTotal"] = _jutQueue.Count,
        ["error"] = j.error
    };

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/download/cancel")]
    public ActionResult JutDownloadCancel(string slug)
    {
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");
        lock (_jutEnqLock)
        {
            // Поколение вперёд — всё поставленное ДО этой секунды становится устаревшим (JutStale),
            // включая серию, которая прямо сейчас в работе: её из очереди уже вынули и пометить
            // обходом ниже невозможно.
            _jutGen[slug] = JutGenOf(slug) + 1;
            foreach (string k in _jutQueued.Where(x => x.StartsWith(slug + ":", StringComparison.Ordinal)).ToList())
                _jutQueued.Remove(k);
        }
        foreach (var it in _jutQueue) if (it.slug == slug) it.cancel = true;
        if (_jutJobs.TryGetValue(slug, out var j)) { j.canceled = true; j.state = "canceled"; j.touched = DateTime.UtcNow; }
        return JutJson(new JObject { ["ok"] = true });
    }

    #endregion

    #region воркер

    static void JutKickWorker()
    {
        // Один воркер: щадим и CDN, и шпиндель (на том же диске качает qBittorrent и пишет регистратор)
        if (Interlocked.CompareExchange(ref _jutWorker, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            // Все запросы к jut.su изнутри воркера идут через ФОНОВЫЙ гейт: иначе качалка
            // отбирает слоты у карточек и плеера, и открытие карточки встаёт в очередь
            // за скачиванием (один запрос к сайту ≈ 1.1 с).
            using var bg = JutNet.BackgroundScope();
            try
            {
                // ⚠️ Выключатель проверяется ДО TryDequeue, а не после. Раньше элемент успевали
                // вынуть и выйти по break, не сняв ключ в _jutQueued: ключ протекал навсегда,
                // а finally тут же перезапускал воркер — тот выхватывал следующий элемент и
                // снова ломался о break. Получался busy-loop, молча сливавший всю очередь,
                // после которого те же серии нельзя было поставить заново до рестарта.
                while (JutOn && _jutQueue.TryDequeue(out var it))
                {
                    if (JutStale(it)) { JutForget(it); continue; }
                    try { await JutGrabOne(it); }
                    catch (Exception ex)
                    {
                        JutNet.Log("grab", it.slug + " " + it.epkey + ": " + ex.Message);
                        if (_jutJobs.TryGetValue(it.slug, out var j))
                        {
                            j.error = ex.Message;
                            JutSetState(j, "error");
                        }
                    }
                    finally { JutForget(it); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _jutWorker, 0);
                // Добор при гонке: пока сбрасывали флаг, могли поставить новый элемент.
                // Гейт по JutOn обязателен — без него это тот самый busy-loop.
                if (JutOn && !_jutQueue.IsEmpty) JutKickWorker();
                else JutPruneJobs();
            }
        });
    }

    static void JutForget(JutGrabItem it)
    {
        lock (_jutEnqLock)
        {
            // ⚠️ Ключ снимает только АКТУАЛЬНЫЙ элемент. У устаревшего (пережившего отмену)
            // тот же ключ мог быть заново поставлен новым запросом — сняв его, мы бы выкинули
            // из дедупа чужую живую серию, и она бы не скачалась.
            if (it.gen == JutGenOf(it.slug))
                _jutQueued.Remove(JutQueueKey(it.slug, it.epkey));
        }
    }

    static async Task JutGrabOne(JutGrabItem it)
    {
        var job = _jutJobs.GetOrAdd(it.slug, _ => new JutGrabJob());
        JutSetState(job, "running");
        job.error = null;

        string dir = JutTitleDir(it.slug);
        Directory.CreateDirectory(dir);

        string token = JutNet.MakeToken(it.slug, it.season, it.ep, it.kind, 0);
        var link = await JutNet.EnsureLink(token, force: false);
        if (link == null || link.error != null)
        {
            job.error = link?.error ?? "NOT_FOUND";
            JutSetState(job, "error");
            JutNet.Log("grab", it.slug + " " + it.epkey + " → " + job.error);
            return;
        }

        var epModel = new JutEp
        {
            season = it.season, num = it.ep,
            kind = it.kind switch
            {
                "film" => JutEpKind.Film, "ova" => JutEpKind.Ova,
                "game-ova" => JutEpKind.GameOva, "special" => JutEpKind.Special,
                _ => JutEpKind.Episode
            }
        };
        string dst = Path.Combine(dir, JutFileName(it.slug, epModel, link.quality));
        string part = dst + ".part";
        string side = dst + ".part.json";
        job.file = Path.GetFileName(dst);

        long have = 0;
        long knownTotal = 0;
        string knownMod = null;
        // Резюм: сайдкар помнит, ЧТО именно мы качали
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

        int retries = Math.Max(1, ModInit.conf?.jutGrabRetries ?? 5);
        int[] backoff = { 5, 15, 60, 60, 60 };

        for (int attempt = 0; attempt < retries; attempt++)
        {
            if (!JutOn || JutStale(it)) { JutSetState(job, "canceled"); return; }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, link.url);
                req.Headers.TryAddWithoutValidation("User-Agent", JutNet.Ua);   // 🔥 тот же UA
                if (have > 0) req.Headers.TryAddWithoutValidation("Range", "bytes=" + have + "-");

                // Idle-токен: перезаводится на КАЖДОМ прочитанном чанке (см. JutWriteStream).
                // ⚠️ Именно idle, а не общий таймаут: у медиа-клиента Timeout.InfiniteTimeSpan
                // обязателен (§AL грабля 4 — иначе 489-МиБ файл рвётся на 100-й секунде).
                int idleSec = Math.Max(0, ModInit.conf?.jutGrabIdleSec ?? 60);
                using var idle = new CancellationTokenSource();
                if (idleSec > 0) idle.CancelAfter(TimeSpan.FromSeconds(idleSec));

                using var resp = await JutNet.Media(link.exitId)
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token);

                if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Gone)
                {
                    // Токен протух посреди 489-МиБ файла — перевыпуск ТЕМ ЖЕ выходом, качаем дальше
                    link = await JutNet.EnsureLink(token, force: true);
                    if (link == null || link.error != null) { job.error = "403"; JutSetState(job, "error"); return; }
                    JutNet.Log("grab", "перевыпуск ссылки на " + have + " Б: " + it.slug + " " + it.epkey);
                    continue;
                }
                if ((int)resp.StatusCode is not (200 or 206))
                    throw new Exception("HTTP " + (int)resp.StatusCode);

                // ⚠️ total берём из Content-Range, а НЕ из Content-Length: при Range последний
                // равен длине ХВОСТА, и сравнение с сайдкаром было бы ложно-отрицательным всегда.
                long total = resp.Content.Headers.ContentRange?.Length
                             ?? resp.Content.Headers.ContentLength ?? 0;
                string mod = resp.Content.Headers.LastModified?.ToString("R");

                // «Файл на CDN сменился» — по total+Last-Modified. ETag НЕ используем: он
                // различается между поддоменами rNNNNNN, и правило «ETag не сошёлся → заново»
                // сбрасывало бы .part на каждом перевыпуске.
                if (have > 0 && knownTotal > 0 && total > 0 && total != knownTotal)
                {
                    JutNet.Log("grab", "файл на CDN сменился — качаю заново: " + it.slug + " " + it.epkey);
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
                            ["total"] = total, ["lastModified"] = mod,
                            ["exitId"] = link.exitId, ["quality"] = link.quality
                        }.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    catch { }
                }

                await JutWriteStream(resp, part, have, job, it, idle, idleSec);

                long got = new FileInfo(part).Length;
                if (knownTotal > 0 && got < knownTotal)
                {
                    have = got;
                    throw new Exception("недокачано " + got + "/" + knownTotal);
                }

                // Готово
                try { if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst); } catch { }
                System.IO.File.Move(part, dst);
                try { System.IO.File.Delete(side); } catch { }

                job.fileDone++;
                // ⚠️ Смотрим на очередь ЭТОГО тайтла, а не на глобальную: раньше при двух
                // тайтлах подряд полностью докачанный первый навсегда оставался "running",
                // пока качался второй. Текущая серия ещё числится в _jutQueued (JutForget
                // зовётся позже, в finally воркера), поэтому порог — 1, а не 0.
                JutSetState(job, JutPendingFor(it.slug) <= 1 ? "done" : "running");
                await JutFinishFile(it, dst, link.quality);
                return;
            }
            catch (Exception ex)
            {
                // ⚠️ Отмену и idle-таймаут различать ОБЯЗАТЕЛЬНО: оба прилетают сюда
                // как OperationCanceledException. Отмена окончательна, ретраить нельзя; idle-таймаут —
                // штатный обрыв, .part остаётся и докачивается обычным бэкоффом.
                if (JutStale(it) || !JutOn) { JutSetState(job, "canceled"); return; }
                bool idleTimeout = ex is OperationCanceledException;
                job.error = idleTimeout ? "нет данных от CDN — обрыв по idle-таймауту" : ex.Message;
                if (idleTimeout)
                    JutNet.Log("grab", "idle-таймаут на " + it.slug + " " + it.epkey + ", докачаем с места");
                if (attempt >= retries - 1)
                {
                    JutSetState(job, "error");
                    // .part остаётся — следующий запуск докачает с места
                    JutNet.Log("grab", "сдаюсь после " + retries + " попыток: " + it.slug + " " + it.epkey + " — " + job.error);
                    return;
                }
                try { have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0; } catch { }
                await Task.Delay(TimeSpan.FromSeconds(backoff[Math.Min(attempt, backoff.Length - 1)]));
            }
        }
    }

    static async Task JutWriteStream(HttpResponseMessage resp, string part, long from,
                                     JutGrabJob job, JutGrabItem it,
                                     CancellationTokenSource idle = null, int idleSec = 0)
    {
        int pace = Math.Max(0, ModInit.conf?.jutGrabPaceMs ?? 0);
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
                // 🔥 Таймер сдвигается на КАЖДОМ чанке — это и делает таймаут idle'овым,
                // а не общим: здоровое медленное скачивание не рвётся, а зависшее
                // соединение к CDN больше не держит ЕДИНСТВЕННЫЙ воркер бесконечно
                // (раньше вся очередь вставала до рестарта контейнера).
                if (idleSec > 0) idle.CancelAfter(TimeSpan.FromSeconds(idleSec));

                // Выключатель проверяется и ВНУТРИ файла: иначе «откат» ещё часы качал бы 12 ГБ.
                // JutStale, а не it.cancel: отмена тайтла, пришедшая пока серия уже качалась,
                // видна только через поколение — из очереди этот элемент давно вынут.
                if (JutStale(it) || !JutOn) break;
                await fs.WriteAsync(buf, 0, n, ct);
                done += n;
                job.done = done;
                // Мягкий кап: тот же шпиндель занят qBittorrent и записью регистратора
                if (pace > 0) await Task.Delay(pace, ct);
            }
            await fs.FlushAsync(CancellationToken.None);   // флашим ВСЕГДА: .part нужен для докачки
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    #endregion

    #region маркер, мета, постер, уведомления

    /// <summary>Мета и постер пишем при постановке в очередь — карточка появляется сразу.</summary>
    static async Task JutEnsureMeta(string slug, JutTitle t)
    {
        string hash = JutNet.Hash(slug);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetaPath(hash)));
            if (!System.IO.File.Exists(MetaPath(hash)))
            {
                // ⚠️ "source":"jutsu" — это ПОЯС 2 изоляции: IndexCrawler по нему пропускает
                // jut-тайтлы и не идёт за ними на трекеры.
                var meta = new JObject
                {
                    ["source"] = "jutsu",
                    ["slug"] = slug,
                    ["title"] = t.titleRu ?? slug,
                    ["original_title"] = t.titleOrig,
                    ["year"] = t.years.Count > 0 ? t.years[0] : 0,
                    ["id"] = 0,
                    ["media_type"] = "tv",
                    ["overview"] = t.descr
                };
                SaveMeta(hash, meta);
            }
        }
        catch { }

        // Постер обязан лежать по HASH-пути: /qdl/list смотрит строго /qdl-data/img/<hash>.jpg,
        // кеш каталога (jut/img/<slug>.jpg) для него не существует.
        try
        {
            string pp = PosterPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(pp));

            // Апгрейженный постер (460×690) в приоритете над квадратом 186×186 с jut.su.
            // Если апгрейд приедет ПОЗЖЕ — его положит сюда JutPosterSyncDownloads.
            string up = JutUpPosterPath(slug);
            if (JutHasUpPoster(slug))
            {
                System.IO.File.Copy(up, pp, true);
            }
            else if (!System.IO.File.Exists(pp) && !string.IsNullOrEmpty(t.poster))
            {
                byte[] img = await JutFetchImage(t.poster);
                if (img != null && img.Length > 128) await System.IO.File.WriteAllBytesAsync(pp, img);
            }
        }
        catch { }

        // Тайтл скачивают — значит он точно интересен: ставим в очередь апгрейда, если ещё не решён.
        JutPosterEnqueue(slug, t.titleRu, t.titleOrig, t.years, t.poster);
    }

    static async Task JutFinishFile(JutGrabItem it, string dst, int quality)
    {
        string hash = JutNet.Hash(it.slug);
        try
        {
            // 🔥 Инкрементально. Раньше здесь на КАЖДУЮ докачанную серию заново перечислялся
            // весь каталог тайтла + new FileInfo по всем N файлам — O(N²) за прогон, и это же
            // раздувало вход для /qdl/list. Теперь известный список правим одной записью,
            // а полный обход остаётся только фолбэком (первая серия, рестарт).
            var prev = LoadLocal(hash);
            var known = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (prev?["files"] is JArray parr)
            {
                foreach (var f in parr)
                {
                    string p = f.Value<string>("path");
                    if (!string.IsNullOrEmpty(p)) known[p] = f.Value<long?>("size") ?? 0;
                }
            }
            else
            {
                foreach (string f in Directory.EnumerateFiles(JutTitleDir(it.slug), "*.mp4"))
                    try { known[f.Replace('\\', '/')] = new FileInfo(f).Length; } catch { }
            }
            try { known[dst.Replace('\\', '/')] = new FileInfo(dst).Length; } catch { }

            var files = new JArray();
            long size = 0;
            int idx = 0;
            foreach (var kv in known)
            {
                files.Add(new JObject
                {
                    ["index"] = idx++, ["name"] = Path.GetFileName(kv.Key),
                    ["path"] = kv.Key, ["size"] = kv.Value
                });
                size += kv.Value;
            }

            var marker = new JObject
            {
                ["name"] = it.titleRu ?? it.slug,
                ["dir"] = JutTitleDir(it.slug).Replace('\\', '/'),
                ["size"] = size,
                ["added"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["overlay"] = false,
                ["files"] = files,
                // Своё поле: LocalFiles лишнее игнорирует. tlPrefix связывает прогресс
                // онлайн-просмотра с прогрессом скачанного (см. /qdl/episodes).
                ["jut"] = new JObject
                {
                    ["slug"] = it.slug,
                    ["tlPrefix"] = "jut:" + it.slug,
                    ["titleRu"] = it.titleRu,
                    ["quality"] = quality
                }
            };
            SaveLocal(hash, marker);
            JsonStore.ForgetDir(JutTitleDir(it.slug));   // в каталоге тайтла появился новый mp4
            JutDropDiskKeys(it.slug);                    // и снимок «что уже скачано» устарел
        }
        catch (Exception ex) { JutNet.Log("grab", "маркер: " + ex.Message); }

        // ⚠️ Обязательно: иначе /qdl/stream продолжит отдавать по устаревшему пути
        try { DropResolveCache(hash); } catch { }

        JutNotifyDone(it, hash);
    }

    static void JutNotifyStart(string slug, string title, int count)
    {
        try
        {
            using var db = new SqlContext();
            string sk = "j" + slug;
            string dedup = "start-" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == dedup)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0, hash = JutNet.Hash(slug),
                title = title ?? slug, season = -1, episode = -1,
                kind = "START", epkey = dedup,
                label = "В очереди на скачивание: " + count,
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
        }
        catch { }
    }

    static void JutNotifyDone(JutGrabItem it, string hash)
    {
        try
        {
            using var db = new SqlContext();
            string sk = "j" + it.slug;
            // Дедуп в существующей таблице seen; префикс j<slug> не пересекается
            // ни с торрентными t<tmdbId>, ни с l<fnv>
            if (!db.seen.Any(x => x.seriesKey == sk && x.epkey == it.epkey))
                db.seen.Add(new SeenModel { seriesKey = sk, epkey = it.epkey });

            if (!db.noti.Any(x => x.seriesKey == sk && x.epkey == it.epkey))
            {
                db.noti.Add(new NotiModel
                {
                    seriesKey = sk, seriesId = 0, hash = hash,
                    title = it.titleRu ?? it.slug,
                    season = it.kind == "episode" ? it.season : -1,
                    episode = it.ep,
                    kind = it.kind == "episode" ? null : it.kind.ToUpperInvariant(),
                    epkey = it.epkey,
                    label = it.kind == "episode"
                        ? $"Сезон {it.season} · серия {it.ep}"
                        : $"{it.kind} {it.ep}",
                    created = DateTime.UtcNow, read = false
                });
            }
            db.SaveChanges();
            JutPushDone(it.slug);
            Console.WriteLine("[QbitDownload] jut/grab: скачано " + it.slug + " " + it.epkey);
        }
        catch (Exception ex) { JutNet.Log("grab", "noti: " + ex.Message); }
    }

    static DateTime _jutLastPush = DateTime.MinValue;
    static readonly object _jutPushLock = new();

    /// <summary>
    /// Коалесер WS-пуша «серия скачана».
    ///
    /// 🔥 Строки в noti создаются на КАЖДУЮ серию (инвариант дедупа epkey + UNIQUE
    /// noti(seriesKey,epkey)) — центр уведомлений и бейдж остаются точными. Троттлится только
    /// СИГНАЛ клиентам: каждый PushNotiSignal рассылается по ВСЕМ WS-соединениям, и каждый
    /// клиент в ответ полностью выгружает /qdl/notifications и печатает тост. При скачивании
    /// целого аниме это 60 полных опросов ленты и 60 тостов на КАЖДОМ устройстве.
    ///
    /// 🔥 Клиент УЖЕ умеет агрегировать («Скачано новых серий: N», qdl.js) — просто в каждой
    /// пачке приезжал ровно один элемент. Поэтому правка чисто серверная, клиент не меняется.
    ///
    /// ⚠️ Последняя серия пачки пушится НЕМЕДЛЕННО (владелец должен сразу увидеть «готово»),
    /// и «пачка кончилась» определяется по очереди ЭТОГО тайтла, а не по глобальной _jutQueue:
    /// иначе последняя серия первого тайтла молчала бы, пока качается второй.
    /// </summary>
    static void JutPushDone(string slug)
    {
        int coalesce = Math.Max(0, ModInit.conf?.jutNotifyCoalesceSec ?? 300);
        bool last = JutPendingFor(slug) <= 1;

        if (coalesce == 0 || last)
        {
            lock (_jutPushLock) _jutLastPush = DateTime.UtcNow;
            PushNotiSignal(1);
            return;
        }

        lock (_jutPushLock)
        {
            if ((DateTime.UtcNow - _jutLastPush).TotalSeconds < coalesce) return;
            _jutLastPush = DateTime.UtcNow;
        }
        PushNotiSignal(1);
    }

    #endregion

    #region прогрев кеша тайтлов (джоба 2 раза в сутки)

    // Какие тайтлы открывали недавно — чтобы греть то, что реально смотрят.
    static readonly ConcurrentDictionary<string, DateTime> _jutSeen =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Отметить открытие тайтла. Зовётся из роута карточки, стоит ноль.</summary>
    internal static void JutTouchTitle(string slug)
    {
        if (!string.IsNullOrEmpty(slug)) _jutSeen[slug] = DateTime.UtcNow;
    }

    /// <summary>
    /// Прогрев кеша тайтлов — решение владельца: «обновлять кеш поставь джобу, пусть
    /// обновляется 2 раза в сутки», греем «скачанное + подписки + недавно открытое».
    ///
    /// 🔥 Зачем: промах TTL кеша тайтла заставляет ПЕРВОГО открывшего карточку ждать
    /// полный обход — для хаб-тайтла это 1 + до 24 последовательных запроса к jut.su
    /// по ~1.1 с (замер /qdl/jut/diag). Живой замер промаха на naruuto: 3.58 с.
    /// После прогрева карточка всегда тёплая и открывается за единицы миллисекунд.
    ///
    /// ⚠️ Идёт под ФОНОВЫМ гейтом: прогрев не имеет права отбирать слоты у карточек,
    /// которые открывает пользователь прямо сейчас.
    /// </summary>
    internal static async Task JutWarmTitles()
    {
        if (!JutOn) return;
        using var bg = JutNet.BackgroundScope();

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) скачанное — этих тайтлов может не быть в каталоге годами
        try
        {
            string root = JutDownloadRoot();
            if (Directory.Exists(root))
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    string s = Path.GetFileName(dir);
                    if (JutSuParse.IsValidSlug(s)) slugs.Add(s);
                }
        }
        catch { }

        // 2) подписки слежения
        try
        {
            foreach (var rec in JutLoadWatch().OfType<JObject>())
            {
                string s = rec.Value<string>("slug");
                if (!string.IsNullOrEmpty(s) && JutSuParse.IsValidSlug(s)) slugs.Add(s);
            }
        }
        catch { }

        // 3) недавно открытые
        int days = Math.Max(1, ModInit.conf?.jutWarmRecentDays ?? 7);
        var edge = DateTime.UtcNow.AddDays(-days);
        foreach (var kv in _jutSeen)
        {
            if (kv.Value < edge) { _jutSeen.TryRemove(kv.Key, out _); continue; }
            if (JutSuParse.IsValidSlug(kv.Key)) slugs.Add(kv.Key);
        }

        if (slugs.Count == 0) return;

        int cap = Math.Max(1, ModInit.conf?.jutWarmTitlesPerRun ?? 40);
        int pace = Math.Max(0, ModInit.conf?.jutWarmPaceMs ?? 1500);
        int warmed = 0, failed = 0;

        foreach (string slug in slugs.Take(cap))
        {
            if (!JutOn) break;
            try
            {
                var (t, _) = await JutLoadTitleStatic(slug);
                if (t == null) { failed++; continue; }
                JutCacheWrite("title", slug, JutTitleJson(t));
                warmed++;
            }
            catch { failed++; }
            // Темп: щадим и сайт, и бюджет прокси-выходов (§BB.5 — он конечен)
            if (pace > 0) await Task.Delay(pace);
        }

        Console.WriteLine($"[QbitDownload] jut/warm: прогрето {warmed}, не удалось {failed}, всего кандидатов {slugs.Count}");
    }

    #endregion

    #region реконсиляция на старте

    /// <summary>
    /// После рестарта очередь (in-proc) пуста, а на диске могли остаться .part и пробелы
    /// в подписанных сезонах. Без этого прохода «убить контейнер на 50% → докачалось»
    /// не работало бы вовсе.
    /// </summary>
    internal static async Task JutReconcile()
    {
        if (ModInit.conf?.jutEnable != true) return;
        try
        {
            string root = JutDownloadRoot();
            if (!Directory.Exists(root)) return;

            int added = 0;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string slug = Path.GetFileName(dir);
                if (!JutSuParse.IsValidSlug(slug)) continue;

                foreach (string part in Directory.EnumerateFiles(dir, "*.part"))
                {
                    var e = JutEpFromFileName(Path.GetFileNameWithoutExtension(part));
                    if (e == null) continue;
                    lock (_jutEnqLock)
                    {
                        if (!_jutQueued.Add(JutQueueKey(slug, e.epkey))) continue;
                    }
                    _jutQueue.Enqueue(new JutGrabItem
                    {
                        slug = slug, season = e.season, ep = e.num,
                        kind = JutKindParam(e.kind), epkey = e.epkey, titleRu = slug,
                        gen = JutGenOf(slug)
                    });
                    added++;
                }
            }
            if (added > 0)
                Console.WriteLine("[QbitDownload] jut/reconcile: добрано недокачанных файлов — " + added);

            // ⚠️ Кик безусловный, а не только при added > 0. Реконсиляция вызывается и на перечит
            // init.conf, то есть это единственная точка, где очередь оживает после включения
            // jutEnable обратно: воркер выходит по !JutOn и сам себя больше не перезапускает.
            if (!_jutQueue.IsEmpty) JutKickWorker();
        }
        catch (Exception ex) { JutNet.Log("reconcile", ex.Message); }
        await Task.CompletedTask;
    }

    static readonly Regex _jutNameRx = new(
        @"^(?<slug>.+?)\.(?:s(?<s>\d{1,3})e(?<e>\d{1,4})|(?<k>film|ova|gameova|sp)(?<n>\d{1,4}))(?:\.\d{3,4}p)?(?:\.mp4)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static JutEp JutEpFromFileName(string name)
    {
        var m = _jutNameRx.Match(name);
        if (!m.Success) return null;
        if (m.Groups["s"].Success)
            return new JutEp
            {
                kind = JutEpKind.Episode,
                season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
                num = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture)
            };
        return new JutEp
        {
            kind = m.Groups["k"].Value.ToLowerInvariant() switch
            {
                "film" => JutEpKind.Film, "ova" => JutEpKind.Ova,
                "gameova" => JutEpKind.GameOva, _ => JutEpKind.Special
            },
            season = 1,
            num = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)
        };
    }

    #endregion
}
