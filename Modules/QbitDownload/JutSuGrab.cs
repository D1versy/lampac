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
    }

    sealed class JutGrabJob
    {
        public volatile string state = "queued";   // queued | running | done | error | canceled
        public volatile string file;
        public volatile string error;
        public long done, total;
        public int fileDone, filesTotal;
    }

    static readonly ConcurrentQueue<JutGrabItem> _jutQueue = new();
    static readonly HashSet<string> _jutQueued = new(StringComparer.Ordinal);
    static readonly object _jutEnqLock = new();
    static readonly ConcurrentDictionary<string, JutGrabJob> _jutJobs = new();   // slug → job
    static int _jutWorker = 0;

    static string JutQueueKey(string slug, string epkey) => slug + ":" + epkey;

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
    static bool JutHaveFile(string slug, JutEp e)
    {
        try
        {
            string dir = JutTitleDir(slug);
            if (!Directory.Exists(dir)) return false;
            // Качество в имени может отличаться (1080p/720p) — сверяем по префиксу без качества
            string prefix = JutFileName(slug, e, 0).Replace(".mp4", "");
            foreach (string f in Directory.EnumerateFiles(dir, "*.mp4"))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    #endregion

    #region постановка в очередь

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/jut/download")]
    async public Task<ActionResult> JutDownload(string slug, int season = 0, int ep = 0,
                                                string kind = null, string scope = "one")
    {
        if (!JutOn) return JutErr("DISABLED");
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");

        var (t, err) = await JutLoadTitle(slug, false);
        if (t == null) return JutErr(err ?? "SITE_DOWN");

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

        // Гард свободного места. Важно не ради экономии: на D: живут торренты qBittorrent
        // и записи регистратора — забитый под ноль диск ломает их, а не только нас.
        string freeErr = JutCheckSpace(want.Count);
        if (freeErr != null) return JutErr("NO_SPACE", freeErr);

        int queued = 0, already = 0;
        var job = _jutJobs.GetOrAdd(slug, _ => new JutGrabJob());
        foreach (var e in want)
        {
            if (JutHaveFile(slug, e)) { already++; continue; }
            lock (_jutEnqLock)
            {
                if (!_jutQueued.Add(JutQueueKey(slug, e.epkey))) continue;
            }
            _jutQueue.Enqueue(new JutGrabItem
            {
                slug = slug, season = e.season, ep = e.num, kind = JutKindParam(e.kind),
                epkey = e.epkey, titleRu = t.titleRu
            });
            queued++;
        }

        job.filesTotal += queued;
        if (queued > 0)
        {
            job.state = "queued";
            // Мета/постер пишем СРАЗУ: иначе первые минуты скачивания выглядят как «ничего не происходит»
            await JutEnsureMeta(slug, t);
            JutNotifyStart(slug, t.titleRu, queued);
            JutKickWorker();
        }

        return JutJson(new JObject
        {
            ["ok"] = true, ["queued"] = queued, ["already"] = already,
            ["hash"] = JutNet.Hash(slug), ["scope"] = scope
        });
    }

    static string JutCheckSpace(int files)
    {
        try
        {
            string root = JutDownloadRoot();
            Directory.CreateDirectory(root);
            var di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? "/");
            long freeGb = di.AvailableFreeSpace / (1024L * 1024 * 1024);
            int min = Math.Max(1, ModInit.conf?.jutMinFreeGb ?? 20);
            // серия 1080p ≈ 0.5 ГБ
            long needGb = Math.Max(1, files / 2);
            if (freeGb - needGb < min)
                return $"мало места: свободно {freeGb} ГБ, нужно ~{needGb} ГБ + резерв {min} ГБ";
        }
        catch { }
        return null;
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/download/status")]
    public ActionResult JutDownloadStatus(string slug = null)
    {
        if (!string.IsNullOrEmpty(slug))
        {
            if (!_jutJobs.TryGetValue(slug, out var j))
                return JutJson(new JObject { ["ok"] = true, ["state"] = "idle" });
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
        ["error"] = j.error
    };

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/download/cancel")]
    public ActionResult JutDownloadCancel(string slug)
    {
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");
        lock (_jutEnqLock)
        {
            foreach (string k in _jutQueued.Where(x => x.StartsWith(slug + ":", StringComparison.Ordinal)).ToList())
                _jutQueued.Remove(k);
        }
        foreach (var it in _jutQueue) if (it.slug == slug) it.cancel = true;
        if (_jutJobs.TryGetValue(slug, out var j)) { j.state = "canceled"; }
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
            try
            {
                while (_jutQueue.TryDequeue(out var it))
                {
                    // Выключатель обязан работать как откат: проверяем МЕЖДУ файлами
                    if (ModInit.conf?.jutEnable != true) break;
                    if (it.cancel) { JutForget(it); continue; }
                    try { await JutGrabOne(it); }
                    catch (Exception ex)
                    {
                        JutNet.Log("grab", it.slug + " " + it.epkey + ": " + ex.Message);
                        if (_jutJobs.TryGetValue(it.slug, out var j)) { j.state = "error"; j.error = ex.Message; }
                    }
                    finally { JutForget(it); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _jutWorker, 0);
                // Добор при гонке: пока сбрасывали флаг, могли поставить новый элемент
                if (!_jutQueue.IsEmpty) JutKickWorker();
            }
        });
    }

    static void JutForget(JutGrabItem it)
    {
        lock (_jutEnqLock) _jutQueued.Remove(JutQueueKey(it.slug, it.epkey));
    }

    static async Task JutGrabOne(JutGrabItem it)
    {
        var job = _jutJobs.GetOrAdd(it.slug, _ => new JutGrabJob());
        job.state = "running";
        job.error = null;

        string dir = JutTitleDir(it.slug);
        Directory.CreateDirectory(dir);

        string token = JutNet.MakeToken(it.slug, it.season, it.ep, it.kind, 0);
        var link = await JutNet.EnsureLink(token, force: false);
        if (link == null || link.error != null)
        {
            job.state = "error";
            job.error = link?.error ?? "NOT_FOUND";
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
            if (ModInit.conf?.jutEnable != true || it.cancel) { job.state = "canceled"; return; }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, link.url);
                req.Headers.TryAddWithoutValidation("User-Agent", JutNet.Ua);   // 🔥 тот же UA
                if (have > 0) req.Headers.TryAddWithoutValidation("Range", "bytes=" + have + "-");

                using var resp = await JutNet.Media(link.exitId)
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Gone)
                {
                    // Токен протух посреди 489-МиБ файла — перевыпуск ТЕМ ЖЕ выходом, качаем дальше
                    link = await JutNet.EnsureLink(token, force: true);
                    if (link == null || link.error != null) { job.state = "error"; job.error = "403"; return; }
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

                await JutWriteStream(resp, part, have, job, it);

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
                job.state = _jutQueue.IsEmpty ? "done" : "running";
                await JutFinishFile(it, dst, link.quality);
                return;
            }
            catch (Exception ex)
            {
                if (it.cancel) { job.state = "canceled"; return; }
                job.error = ex.Message;
                if (attempt >= retries - 1)
                {
                    job.state = "error";
                    // .part остаётся — следующий запуск докачает с места
                    JutNet.Log("grab", "сдаюсь после " + retries + " попыток: " + it.slug + " " + it.epkey + " — " + ex.Message);
                    return;
                }
                try { have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0; } catch { }
                await Task.Delay(TimeSpan.FromSeconds(backoff[Math.Min(attempt, backoff.Length - 1)]));
            }
        }
    }

    static async Task JutWriteStream(HttpResponseMessage resp, string part, long from,
                                     JutGrabJob job, JutGrabItem it)
    {
        int pace = Math.Max(0, ModInit.conf?.jutGrabPaceMs ?? 0);
        byte[] buf = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var src = await resp.Content.ReadAsStreamAsync();
            using var fs = new FileStream(part, from > 0 ? FileMode.Append : FileMode.Create,
                                          FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true);
            long done = from;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                // Выключатель проверяется и ВНУТРИ файла: иначе «откат» ещё часы качал бы 12 ГБ
                if (it.cancel || ModInit.conf?.jutEnable != true) break;
                await fs.WriteAsync(buf, 0, n);
                done += n;
                job.done = done;
                // Мягкий кап: тот же шпиндель занят qBittorrent и записью регистратора
                if (pace > 0) await Task.Delay(pace);
            }
            await fs.FlushAsync();
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
                await System.IO.File.WriteAllTextAsync(MetaPath(hash), meta.ToString(Newtonsoft.Json.Formatting.None));
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
            var files = new JArray();
            long size = 0;
            int idx = 0;
            foreach (string f in Directory.EnumerateFiles(JutTitleDir(it.slug), "*.mp4")
                                          .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(f);
                files.Add(new JObject
                {
                    ["index"] = idx++, ["name"] = fi.Name,
                    ["path"] = f.Replace('\\', '/'), ["size"] = fi.Length
                });
                size += fi.Length;
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
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath(hash)));
            await System.IO.File.WriteAllTextAsync(LocalPath(hash),
                marker.ToString(Newtonsoft.Json.Formatting.None));
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
            PushNotiSignal(1);
            Console.WriteLine("[QbitDownload] jut/grab: скачано " + it.slug + " " + it.epkey);
        }
        catch (Exception ex) { JutNet.Log("grab", "noti: " + ex.Message); }
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
                        kind = JutKindParam(e.kind), epkey = e.epkey, titleRu = slug
                    });
                    added++;
                }
            }
            if (added > 0)
            {
                Console.WriteLine("[QbitDownload] jut/reconcile: добрано недокачанных файлов — " + added);
                JutKickWorker();
            }
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
