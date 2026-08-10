using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Слежение за новыми сериями — ТОЛЬКО по jut.su.
//
// 🔴 ТРЕБОВАНИЕ ВЛАДЕЛЬЦА: за сериями jut-тайтлов в торренты НЕ ХОДИМ. Держится тремя поясами:
//
//  Пояс 1 (структурный, главный). Торрентная охота EpisodeHunter.HuntAll итерирует
//    ИСКЛЮЧИТЕЛЬНО /qdl-data/watch.json. Jut-подписки живут в ОТДЕЛЬНОМ файле
//    /qdl-data/jut/watch.json. Добавить jut-тайтл в торрентное слежение невозможно КОДОМ:
//    WatchAdd (Controller.cs:2950) требует links/<hash>.json, а JutSuGrab его не создаёт никогда.
//  Пояс 2. IndexCrawler.TargetsFromMeta пропускает меты с "source":"jutsu".
//  Пояс 3. Тесты JutIsolationTests.
//
// Единица слежения — (тайтл, СЕЗОН), тик раз в сутки (решение владельца).
// Устройство: E:\Media-server\claude\jut\02-architecture.md §9
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region состояние

    static readonly SemaphoreSlim _jutGate = new SemaphoreSlim(1, 1);
    static readonly object _jutWatchLock = new();

    static string JutWatchPath() => Path.Combine(JutNet.JutDataDir(), "watch.json");

    static JArray JutLoadWatch()
    {
        try
        {
            string p = JutWatchPath();
            if (System.IO.File.Exists(p)) return JArray.Parse(System.IO.File.ReadAllText(p));
        }
        catch (Exception ex) { JutNet.Log("watch", "чтение: " + ex.Message); }
        return new JArray();
    }

    /// <summary>
    /// Множество слагов под подпиской — для отметки карточек в /qdl/list.
    /// Читается ОДИН раз на запрос списка (не на карточку): файл маленький, но список
    /// «Загрузок» перебирает десятки маркеров, и чтение на каждый было бы лишним IO.
    /// Ошибка чтения → пустое множество: «не знаю» здесь честнее, чем «не следим»,
    /// но обе трактовки безопасны — статус только рисует пункт меню.
    /// </summary>
    internal static HashSet<string> JutWatchedSlugs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in JutLoadWatch().OfType<JObject>())
        {
            string s = x.Value<string>("slug");
            if (!string.IsNullOrEmpty(s)) set.Add(s);
        }
        return set;
    }

    static void JutSaveWatch(JArray arr)
    {
        try
        {
            Directory.CreateDirectory(JutNet.JutDataDir());
            System.IO.File.WriteAllText(JutWatchPath(), arr.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception ex) { JutNet.Log("watch", "запись: " + ex.Message); }
    }

    static JObject JutFindWatch(JArray arr, string slug)
        => arr.OfType<JObject>().FirstOrDefault(x =>
               string.Equals(x.Value<string>("slug"), slug, StringComparison.OrdinalIgnoreCase));

    #endregion

    #region роуты слежения

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/jut/watch")]
    async public Task<ActionResult> JutWatchAdd(string slug, int season = 0, int autoGrab = -1)
    {
        if (!JutOn) return JutErr("DISABLED");
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");

        var (t, err) = await JutLoadTitle(slug, false);
        if (t == null) return JutErr(err ?? "SITE_DOWN");

        var eps = t.items.Where(e => e.kind == JutEpKind.Episode).ToList();
        int s = season > 0 ? season : (eps.Count > 0 ? eps.Max(e => e.season) : 1);
        var inSeason = eps.Where(e => e.season == s).ToList();

        lock (_jutWatchLock)
        {
            var arr = JutLoadWatch();
            var rec = JutFindWatch(arr, slug);
            if (rec == null) { rec = new JObject { ["slug"] = slug }; arr.Add(rec); }

            rec["season"] = s;
            rec["titleRu"] = t.titleRu ?? slug;
            rec["ongoing"] = t.ongoing;
            rec["autoGrab"] = autoGrab >= 0 ? autoGrab == 1 : (ModInit.conf?.jutWatchAutoGrab ?? true);
            // Baseline: «Следить» качает только БУДУЩИЕ серии. Уже вышедшее — кнопкой «Скачать сезон».
            rec["known"] = new JObject
            {
                ["count"] = inSeason.Count,
                ["max"] = inSeason.Count > 0 ? inSeason.Max(e => e.num) : 0,
                ["keys"] = new JArray(inSeason.Select(e => e.epkey))
            };
            rec["lastChange"] = DateTime.UtcNow;
            rec["fails"] = 0;
            JutSaveWatch(arr);
        }

        return JutJson(new JObject
        {
            ["ok"] = true, ["slug"] = slug, ["season"] = s,
            ["baseline"] = inSeason.Count,
            ["message"] = $"Слежу за сезоном {s}. Уже вышедшие {inSeason.Count} серий не качаю — для них кнопка «Скачать сезон»."
        });
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/jut/watch/remove")]
    public ActionResult JutWatchRemove(string slug)
    {
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");
        lock (_jutWatchLock)
        {
            var arr = JutLoadWatch();
            var rec = JutFindWatch(arr, slug);
            if (rec != null) { rec.Remove(); JutSaveWatch(arr); }
        }
        return JutJson(new JObject { ["ok"] = true });
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/watch/list")]
    public ActionResult JutWatchList()
    {
        var arr = JutLoadWatch();
        return JutJson(new JObject
        {
            ["ok"] = true,
            ["items"] = new JArray(arr.OfType<JObject>().Select(x => new JObject
            {
                ["slug"] = x.Value<string>("slug"),
                ["season"] = x.Value<int?>("season") ?? 1,
                ["titleRu"] = x.Value<string>("titleRu"),
                ["ongoing"] = x.Value<bool?>("ongoing") ?? false,
                ["autoGrab"] = x.Value<bool?>("autoGrab") ?? true,
                ["known"] = x.Value<JObject>("known")?.Value<int?>("count") ?? 0,
                ["lastRun"] = x.Value<string>("lastRun"),
                ["lastChange"] = x.Value<string>("lastChange")
            }))
        });
    }

    /// <summary>
    /// Снять подписку и следы при удалении карточки из «Загрузок». Зовётся из /qdl/delete:
    /// подписка живёт в отдельном файле, PurgeCache о ней не знает, и без этого при
    /// автоскачивании удалённый тайтл возвращался бы следующим тиком.
    /// </summary>
    internal static void JutForgetOnDelete(string slug)
    {
        if (!JutSuParse.IsValidSlug(slug)) return;
        try
        {
            lock (_jutWatchLock)
            {
                var arr = JutLoadWatch();
                var rec = JutFindWatch(arr, slug);
                if (rec != null) { rec.Remove(); JutSaveWatch(arr); }
            }
            // Отменяем то, что ещё стоит в очереди на этот тайтл
            lock (_jutEnqLock)
            {
                foreach (string k in _jutQueued.Where(x => x.StartsWith(slug + ":", StringComparison.Ordinal)).ToList())
                    _jutQueued.Remove(k);
            }
            foreach (var it in _jutQueue) if (it.slug == slug) it.cancel = true;
            _jutJobs.TryRemove(slug, out _);

            // База отсечения: иначе после повторной подписки серии считались бы «уже виденными»
            using var db = new SqlContext();
            string sk = "j" + slug;
            var seen = db.seen.Where(x => x.seriesKey == sk).ToList();
            if (seen.Count > 0) { db.seen.RemoveRange(seen); db.SaveChanges(); }
            Console.WriteLine("[QbitDownload] jut/watch: слежение снято при удалении — " + slug);
        }
        catch (Exception ex) { JutNet.Log("watch", "forget: " + ex.Message); }
    }

    /// <summary>Ручной прогон: суточный такт ждать невозможно.</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/watch/check")]
    async public Task<ActionResult> JutWatchCheckNow()
    {
        if (!JutOn) return JutErr("DISABLED");
        var res = await JutWatchTick(manual: true);
        return JutJson(res);
    }

    #endregion

    #region тик

    /// <summary>
    /// Тик слежения. Дешёвый общий опрос: ОДИН запрос /anime/ongoing/ отвечает, у кого вырос
    /// счётчик серий; у остальных страницу тайтла не открываем вообще.
    ///
    /// Гейт СВОЙ (_jutGate), не общий _watchGate: jut-контур не трогает ни watch.json торрентов,
    /// ни qBittorrent — конкурировать не с чем, а на общем гейте четырёхчасовая торрентная охота
    /// глушила бы суточный тик (skip-if-busy → тик пропадает на сутки).
    /// </summary>
    internal static async Task<JObject> JutWatchTick(bool manual = false)
    {
        var res = new JObject { ["ok"] = true };
        if (ModInit.conf?.jutEnable != true) { res["skipped"] = "disabled"; return res; }

        if (!await _jutGate.WaitAsync(0))
        {
            res["skipped"] = "занят другой проход";
            return res;
        }

        try
        {
            var arr = JutLoadWatch();
            var recs = arr.OfType<JObject>().ToList();
            if (recs.Count == 0) { res["watched"] = 0; return res; }

            // 1) один запрос: slug → число серий
            var ongoing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var resp = await JutNet.PostForm(JutNet.Host + "/anime/ongoing/",
                "ajax_load=yes&start_from_page=1&show_search=&anime_of_user=", JutSuParse.ReachedCatalog);
            bool ongoingOk = resp != null && resp.reached;
            if (ongoingOk)
                foreach (var c in JutSuParse.ParseCatalog(resp.body).items)
                    if (!string.IsNullOrEmpty(c.slug)) ongoing[c.slug] = c.episodes;

            int budget = Math.Max(1, ModInit.conf?.jutWatchTitlesPerTick ?? 30);
            int probed = 0, changed = 0, queued = 0, failed = 0;

            foreach (var rec in recs)
            {
                if (ModInit.conf?.jutEnable != true) break;
                string slug = rec.Value<string>("slug");
                if (!JutSuParse.IsValidSlug(slug)) continue;

                int knownCount = rec.Value<JObject>("known")?.Value<int?>("count") ?? 0;
                bool mustProbe = !ongoingOk                            // список не получили — проверяем сами
                                 || !ongoing.TryGetValue(slug, out int now)
                                 || now != knownCount;

                if (!mustProbe) continue;
                if (probed >= budget) break;
                probed++;

                var (t, err) = await JutLoadTitleStatic(slug);
                if (t == null)
                {
                    failed++;
                    rec["fails"] = (rec.Value<int?>("fails") ?? 0) + 1;
                    continue;
                }
                rec["fails"] = 0;
                rec["ongoing"] = t.ongoing;
                if (string.IsNullOrEmpty(rec.Value<string>("titleRu"))) rec["titleRu"] = t.titleRu;

                int season = rec.Value<int?>("season") ?? 1;
                var eps = t.items.Where(e => e.kind == JutEpKind.Episode).ToList();

                // Вышел сезон СТАРШЕ отслеживаемого → уведомление + автопереключение
                int maxSeason = eps.Count > 0 ? eps.Max(e => e.season) : season;
                if (maxSeason > season && (ModInit.conf?.jutWatchSeasonSwitch ?? true))
                {
                    JutNotifySeason(slug, t.titleRu, maxSeason);
                    season = maxSeason;
                    rec["season"] = season;

                    // ⚠️ Baseline нового сезона = его ТЕКУЩЕЕ состояние, а НЕ пусто.
                    // Пустой baseline означает «все уже вышедшие серии нового сезона — новые»,
                    // и подписка на сезон 1 мгновенно выкачивала бы весь сезон 3.
                    // Проверено на живом сервере: 13 серий ≈ 6 ГБ ушли в очередь одним тиком.
                    // Политика «Следить качает только БУДУЩИЕ серии» обязана переживать переключение;
                    // вышедшее берётся кнопкой «Скачать сезон».
                    var ns = eps.Where(e => e.season == season).ToList();
                    rec["known"] = new JObject
                    {
                        ["count"] = ns.Count,
                        ["max"] = ns.Count > 0 ? ns.Max(e => e.num) : 0,
                        ["keys"] = new JArray(ns.Select(e => e.epkey))
                    };
                    rec["lastChange"] = DateTime.UtcNow;
                }

                var inSeason = eps.Where(e => e.season == season).ToList();
                var knownKeys = new HashSet<string>(
                    (rec.Value<JObject>("known")?["keys"] as JArray ?? new JArray())
                        .Select(x => x.Value<string>()).Where(x => x != null), StringComparer.Ordinal);

                // 🔥 Что качать — это diff(сайт, ДИСК), а не diff с known: рестарт между
                // постановкой в очередь и завершением файла иначе терял бы серию навсегда.
                var fresh = inSeason.Where(e => !knownKeys.Contains(e.epkey)).ToList();
                var toGrab = inSeason.Where(e => !JutHaveFile(slug, e) && !knownKeys.Contains(e.epkey)).ToList();

                if (fresh.Count > 0)
                {
                    changed++;
                    rec["lastChange"] = DateTime.UtcNow;
                    foreach (var e in fresh) JutNotifyNewEpisode(slug, t.titleRu, e);

                    bool auto = rec.Value<bool?>("autoGrab") ?? (ModInit.conf?.jutWatchAutoGrab ?? true);
                    if (auto && toGrab.Count > 0)
                    {
                        string space = JutCheckSpace(toGrab.Count);
                        if (space != null) JutNet.Log("watch", slug + ": автоскачивание отменено — " + space);
                        else
                        {
                            foreach (var e in toGrab)
                            {
                                lock (_jutEnqLock)
                                {
                                    if (!_jutQueued.Add(JutQueueKey(slug, e.epkey))) continue;
                                }
                                _jutQueue.Enqueue(new JutGrabItem
                                {
                                    slug = slug, season = e.season, ep = e.num,
                                    kind = JutKindParam(e.kind), epkey = e.epkey, titleRu = t.titleRu
                                });
                                queued++;
                            }
                            if (queued > 0) { await JutEnsureMeta(slug, t); JutKickWorker(); }
                        }
                    }

                    rec["known"] = new JObject
                    {
                        ["count"] = inSeason.Count,
                        ["max"] = inSeason.Count > 0 ? inSeason.Max(e => e.num) : 0,
                        ["keys"] = new JArray(inSeason.Select(e => e.epkey))
                    };
                }
                else if (inSeason.Count != knownCount)
                {
                    // счётчик разошёлся без новых ключей (серию сняли) — просто выравниваем
                    rec["known"]["count"] = inSeason.Count;
                }
            }

            // ⚠️ lastRun ТОЛЬКО на удачном проходе: безусловный штамп превращал пустую
            // выдачу в «новых серий нет» (урок §AV).
            if (ongoingOk || probed > 0)
                foreach (var rec in recs) rec["lastRun"] = DateTime.UtcNow;

            lock (_jutWatchLock) JutSaveWatch(arr);

            Console.WriteLine($"[QbitDownload] jut/watch: ongoing={(ongoingOk ? "ok" : "fail")}, " +
                              $"отслеживается {recs.Count}, опрошено {probed}, изменилось {changed}, " +
                              $"в очередь {queued}, ошибок {failed}");

            res["watched"] = recs.Count;
            res["probed"] = probed;
            res["changed"] = changed;
            res["queued"] = queued;
            res["failed"] = failed;
            res["ongoingList"] = ongoingOk;
            return res;
        }
        catch (Exception ex)
        {
            JutNet.Log("watch", "тик: " + ex.Message);
            res["ok"] = false;
            res["error"] = ex.Message;
            return res;
        }
        finally { _jutGate.Release(); }
    }

    // Статический доступ к загрузке тайтла из фонового тика (там нет экземпляра контроллера).
    static async Task<(JutTitle, string)> JutLoadTitleStatic(string slug)
    {
        var resp = await JutNet.Get(JutNet.Host + "/" + slug + "/",
                                    h => JutSuParse.ReachedTitle(h) || JutSuParse.ReachedSection(h));
        if (resp == null || !resp.reached) return (null, resp?.error ?? "SITE_DOWN");

        var t = JutSuParse.ParseTitle(resp.body, slug);
        if (t.isHub)
        {
            var all = new List<JutEp>();
            foreach (string section in t.hubSections.Take(24))
            {
                string u = section.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? section : JutNet.Host + section;
                var sr = await JutNet.Get(u, JutSuParse.ReachedSection);
                if (sr == null || !sr.reached) continue;
                all.AddRange(JutSuParse.ParseEpisodeList(JutSuParse.Strip(sr.body)));
            }
            t.items = all.GroupBy(e => e.url).Select(g => g.First())
                         .OrderBy(e => e.kind).ThenBy(e => e.season).ThenBy(e => e.num).ToList();
        }
        return (t, null);
    }

    #endregion

    #region уведомления

    static void JutNotifyNewEpisode(string slug, string title, JutEp e)
    {
        try
        {
            using var db = new SqlContext();
            string sk = "j" + slug;
            string key = "new-" + e.epkey;
            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == key)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0, hash = JutNet.Hash(slug),
                title = title ?? slug,
                season = e.kind == JutEpKind.Episode ? e.season : -1,
                episode = e.num,
                kind = e.kind == JutEpKind.Episode ? null : e.kind.ToString().ToUpperInvariant(),
                epkey = key,
                label = e.kind == JutEpKind.Episode
                    ? $"jut.su · сезон {e.season} · серия {e.num}"
                    : $"jut.su · {e.kind} {e.num}",
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
        }
        catch (Exception ex) { JutNet.Log("watch", "noti: " + ex.Message); }
    }

    static void JutNotifySeason(string slug, string title, int season)
    {
        try
        {
            using var db = new SqlContext();
            string sk = "j" + slug;
            string key = "season-" + season;
            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == key)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0, hash = JutNet.Hash(slug),
                title = title ?? slug, season = season, episode = -1,
                kind = "SEASON", epkey = key,
                label = $"jut.su · вышел сезон {season} — слежу за ним",
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
            Console.WriteLine("[QbitDownload] jut/watch: " + slug + " → переключился на сезон " + season);
        }
        catch { }
    }

    #endregion

    #region планирование

    /// <summary>
    /// Догон пропущенных тиков. При суточном такте это ОБЯЗАТЕЛЬНО: без него каждый рестарт
    /// контейнера (дорогой — Roslyn-компиляция модулей) сдвигал бы проверку на новые сутки,
    /// и при частых рестартах слежение не срабатывало бы вообще.
    /// </summary>
    internal static bool JutWatchOverdue(TimeSpan period, out TimeSpan since)
    {
        since = TimeSpan.Zero;
        try
        {
            var arr = JutLoadWatch();
            DateTime? last = null;
            foreach (var rec in arr.OfType<JObject>())
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

    #endregion
}
