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
//
// ДВА РЕЖИМА подписки (решение владельца), режим живёт в поле autoGrab записи:
//   "notify" (autoGrab:false) — включается ТОЛЬКО с экрана карточки тайтла: уведомляем
//        о новых сериях и не качаем ничего. Смысл: следить за онгоингом, которого нет на диске.
//   "grab"   (autoGrab:true)  — включается ТОЛЬКО из «Загрузок» (то есть на уже скачанном):
//        уведомляем И качаем новые серии сами.
// Режим гейтит ИСКЛЮЧИТЕЛЬНО постановку в очередь: уведомления и продвижение baseline
// одинаковы в обоих режимах.
//
// ⚠️ Запись без поля autoGrab = "grab". Такие подписки создавались, когда автоскачивание
// было единственным поведением; трактовать их как "notify" значило бы молча выключить
// скачивание у живых подписок — без единой строки в логе.
//
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
    /// slug → режим подписки ("grab" | "notify") для отметки карточек в /qdl/list.
    /// Читается ОДИН раз на запрос списка (не на карточку): файл маленький, но список
    /// «Загрузок» перебирает десятки маркеров, и чтение на каждый было бы лишним IO.
    /// Ошибка чтения → пустая карта: «не знаю» здесь честнее, чем «не следим»,
    /// но обе трактовки безопасны — статус только рисует пункт меню.
    /// </summary>
    internal static Dictionary<string, string> JutWatchModes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in JutLoadWatch().OfType<JObject>())
        {
            string s = x.Value<string>("slug");
            if (!string.IsNullOrEmpty(s)) map[s] = JutModeOf(x);
        }
        return map;
    }

    /// <summary>
    /// Режим подписки: "grab" — новые серии качаем сами, "notify" — только уведомляем.
    /// Поля нет → "grab" (см. шапку файла: молча выключать скачивание у живых подписок нельзя).
    /// Конфиг здесь НЕ участвует осознанно: правка jutWatchAutoGrab не должна
    /// переопределять уже выбранный пользователем режим.
    /// </summary>
    internal static string JutModeOf(JObject rec)
        => (rec?.Value<bool?>("autoGrab") ?? true) ? "grab" : "notify";

    /// <summary>
    /// Режим для записи: явный параметр UI > уже сохранённый режим > дефолт конфига.
    /// Чистая функция отдельно от роута, потому что JutWatchAdd сетевой (JutLoadTitle),
    /// а ломалась именно эта развилка: повторный вызов без параметра затирал режим
    /// дефолтом конфига, то есть молча включал автоскачивание.
    /// </summary>
    internal static bool JutAutoGrabFor(bool? prev, int autoGrab)
        => autoGrab >= 0 ? autoGrab == 1 : (prev ?? (ModInit.conf?.jutWatchAutoGrab ?? true));

    /// <summary>
    /// Отметка jut-карточки в /qdl/list: slug + режим подписки.
    /// Вынесено из Controller.List, чтобы контракт проверялся тестом (сам экшен требует
    /// живого qBittorrent и HttpContext).
    /// Режим кладём ВНУТРЬ jut, а не в корень: корневые поля общие с торрентной ветвью,
    /// там «watch» читалось бы как торрентное слежение. watched остаётся bool — на нём
    /// держатся отметка в гриде и уже закешированный старый клиент.
    /// </summary>
    internal static void JutDecorateListItem(JObject item, JObject loc, IReadOnlyDictionary<string, string> modes)
    {
        string jslug = (loc?["jut"] as JObject)?.Value<string>("slug");
        if (string.IsNullOrEmpty(jslug)) return;
        string jmode = modes != null && modes.TryGetValue(jslug, out string m) ? m : "off";
        item["jut"] = new JObject { ["slug"] = jslug, ["watch"] = jmode };
        item["watched"] = jmode != "off";
    }

    /// <summary>
    /// slug из ключа серии jut ("j" + slug). Нужен /qdl/notifications: в режиме «только
    /// уведомления» карточки в «Загрузках» нет вовсе, а hash необратим (sha1("jutsu:"+slug)),
    /// поэтому клиенту нужен явный slug — иначе тап по уведомлению уходил в торрентную
    /// ветку и открывал плеер по мёртвому URL.
    /// Торрентные ключи (t&lt;tmdbId&gt;, l&lt;fnv&gt;) наружу не отдаём: гейт по 'j' + IsValidSlug.
    /// </summary>
    internal static string JutSlugFromSeriesKey(string seriesKey)
    {
        if (string.IsNullOrEmpty(seriesKey) || seriesKey.Length < 2 || seriesKey[0] != 'j') return null;
        string slug = seriesKey.Substring(1);
        return JutSuParse.IsValidSlug(slug) ? slug : null;
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

    /// <summary>Итог подписки — чтобы роут остался тонким, а логика проверялась без сети.</summary>
    internal sealed class JutWatchUpsertResult
    {
        public int season;
        public int baseline;    // сколько серий сезона уже вышло — их НЕ качаем
        public string mode;     // "grab" | "notify"
        public bool created;
    }

    /// <summary>
    /// Создать или обновить подписку.
    /// ⚠️ Сбрасывает baseline (known) на ТЕКУЩЕЕ состояние сайта, поэтому для смены режима
    /// существующей подписки НЕ годится — для этого JutWatchSetModeOnDisk: иначе серия,
    /// вышедшая между тиком и нажатием, уходит в baseline, и в режиме «качаю» её никто
    /// уже не скачает.
    /// </summary>
    internal static JutWatchUpsertResult JutWatchUpsert(string slug, JutTitle t, int season, int autoGrab)
    {
        var eps = t.items.Where(e => e.kind == JutEpKind.Episode).ToList();
        int s = season > 0 ? season : (eps.Count > 0 ? eps.Max(e => e.season) : 1);
        var inSeason = eps.Where(e => e.season == s).ToList();

        lock (_jutWatchLock)
        {
            var arr = JutLoadWatch();
            var rec = JutFindWatch(arr, slug);
            bool? prevAuto = rec?.Value<bool?>("autoGrab");   // снять ДО создания записи
            bool created = rec == null;
            if (created) { rec = new JObject { ["slug"] = slug }; arr.Add(rec); }

            rec["season"] = s;
            rec["titleRu"] = t.titleRu ?? slug;
            rec["ongoing"] = t.ongoing;
            rec["autoGrab"] = JutAutoGrabFor(prevAuto, autoGrab);
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

            return new JutWatchUpsertResult
            {
                season = s, baseline = inSeason.Count,
                mode = JutModeOf(rec), created = created
            };
        }
    }

    /// <summary>
    /// Подписка. autoGrab: 1 — качать новые серии («Загрузки»), 0 — только уведомлять
    /// (карточка тайтла), -1 — не указано (сохранить режим существующей записи).
    /// </summary>
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/jut/watch")]
    async public Task<ActionResult> JutWatchAdd(string slug, int season = 0, int autoGrab = -1)
    {
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;   // подписки живут только дома
        if (!JutOn) return JutErr("DISABLED");
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");

        var (t, err) = await JutLoadTitle(slug, false);
        if (t == null) return JutErr(err ?? "SITE_DOWN");

        var r = JutWatchUpsert(slug, t, season, autoGrab);

        return JutJson(new JObject
        {
            ["ok"] = true, ["slug"] = slug, ["season"] = r.season,
            ["baseline"] = r.baseline,
            ["mode"] = r.mode, ["autoGrab"] = r.mode == "grab",
            ["message"] = r.mode == "grab"
                ? $"Слежу за сезоном {r.season}: новые серии буду качать сам. Уже вышедшие {r.baseline} не качаю — для них кнопка «Скачать»."
                : $"Слежу за сезоном {r.season}: сообщу о новых сериях, качать не буду. Скачивание включается в «Загрузках»."
        });
    }

    /// <summary>
    /// Сменить режим существующей подписки. Отдельный роут, а не повторный /qdl/jut/watch,
    /// по двум причинам: (1) не трогает baseline — иначе серия, вышедшая между тиком и
    /// нажатием, была бы проглочена; (2) не ходит в сеть — «выключить качание» обязано
    /// работать, когда jut.su лежит (JutWatchAdd отваливается на SITE_DOWN до записи).
    /// </summary>
    internal static bool JutWatchSetModeOnDisk(string slug, bool auto, out string mode, out int season)
    {
        mode = null; season = 0;
        lock (_jutWatchLock)
        {
            var arr = JutLoadWatch();
            var rec = JutFindWatch(arr, slug);
            if (rec == null) return false;
            rec["autoGrab"] = auto;
            rec["lastChange"] = DateTime.UtcNow;
            mode = JutModeOf(rec);
            season = rec.Value<int?>("season") ?? 1;
            JutSaveWatch(arr);
        }
        Console.WriteLine("[QbitDownload] jut/watch: режим " + slug + " → " + mode);
        return true;
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/jut/watch/mode")]
    public ActionResult JutWatchMode(string slug, int autoGrab = -1)
    {
        if (!JutSuParse.IsValidSlug(slug)) return JutErr("BAD_SLUG");
        if (autoGrab != 0 && autoGrab != 1) return JutErr("BAD_MODE");
        if (!JutWatchSetModeOnDisk(slug, autoGrab == 1, out string mode, out int season))
            return JutErr("NOT_WATCHED");

        return JutJson(new JObject
        {
            ["ok"] = true, ["slug"] = slug, ["season"] = season,
            ["mode"] = mode, ["autoGrab"] = mode == "grab",
            ["message"] = mode == "grab"
                ? "Новые серии буду качать сам. Уже вышедшие — кнопкой «Скачать»."
                : "Только уведомления: новые серии больше не качаются."
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
                // autoGrab оставлен для диагностики и старого клиента; режим — в mode
                ["autoGrab"] = x.Value<bool?>("autoGrab") ?? true,
                ["mode"] = JutModeOf(x),
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
            // Отменяем то, что ещё стоит в очереди на этот тайтл.
            // Поколение вперёд — как в JutDownloadCancel: элементы из ConcurrentQueue не вынуть,
            // а серия, уже качающаяся в этот момент, вообще не видна обходом ниже.
            lock (_jutEnqLock)
            {
                _jutGen[slug] = JutGenOf(slug) + 1;
                foreach (string k in _jutQueued.Where(x => x.StartsWith(slug + ":", StringComparison.Ordinal)).ToList())
                    _jutQueued.Remove(k);
                // 🔴 И намерения — иначе восстановление на старте вернуло бы удалённое.
                JutWantsDropTitle(slug);
            }
            foreach (var it in _jutQueue) if (it.slug == slug) it.cancel = true;
            _jutJobs.TryRemove(slug, out _);

            // База отсечения: иначе после повторной подписки серии считались бы «уже виденными»
            using var db = new SqlContext();
            string sk = "j" + slug;
            var seen = db.seen.Where(x => x.seriesKey == sk).ToList();
            if (seen.Count > 0) { db.seen.RemoveRange(seen); db.SaveChanges(); }
            Console.WriteLine("[QbitDownload] jut/watch: слежение снято при удалении — " + slug);
            JutPurgePartials(slug);
        }
        catch (Exception ex) { JutNet.Log("watch", "forget: " + ex.Message); }
    }

    /// <summary>
    /// 🔥 Недокачанные .part при удалении карточки. Маркер о них не знает (он перечисляет только
    /// готовые файлы), поэтому DeleteLocalFiles их не трогал — а JutReconcile на следующем старте
    /// добирает ИМЕННО .part и молча воскрешал удалённый тайтл вместе с карточкой.
    /// Поймано на боевом прогоне 12.08.2026 (отмена на середине сезона → удаление карточки).
    /// Пустой каталог тайтла убираем следом; непустой (лежит что-то чужое) не трогаем.
    /// </summary>
    internal static void JutPurgePartials(string slug)
    {
        try
        {
            string dir = JutTitleDir(slug);
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.EnumerateFiles(dir, "*.part*"))
                try { System.IO.File.Delete(f); } catch { }
            // Хвосты убрали — снимаем и намерения: иначе свип поставил бы их заново.
            JutWantsDropTitle(slug);
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                try { Directory.Delete(dir); } catch { }
            JsonStore.ForgetDir(dir);
        }
        catch (Exception ex) { JutNet.Log("watch", "уборка .part: " + ex.Message); }
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
    ///
    /// loadOngoing/loadTitle — точки подмены сети (null = реальные запросы). Нужны тестам:
    /// у JutNet своя фабрика HttpClient без места под HttpMessageHandler, поэтому иначе тик
    /// не прогнать вообще, а именно в нём живут оба режима и защита от бэклога.
    /// </summary>
    internal static async Task<JObject> JutWatchTick(
        bool manual = false,
        Func<Task<(bool ok, Dictionary<string, int> counts)>> loadOngoing = null,
        Func<string, Task<(JutTitle title, string error)>> loadTitle = null)
    {
        loadOngoing ??= JutLoadOngoingStatic;
        loadTitle ??= JutLoadTitleStatic;

        var res = new JObject { ["ok"] = true };
        if (ModInit.conf?.jutEnable != true) { res["skipped"] = "disabled"; return res; }

        if (!await _jutGate.WaitAsync(0))
        {
            res["skipped"] = "занят другой проход";
            return res;
        }

        // Тик слежения — фоновая работа: свой гейт, чтобы полный опрос тайтлов (до 30 за проход,
        // каждая страница ≈ 1.1 с) не отбирал слоты у карточек и плеера.
        using var bg = JutNet.BackgroundScope();
        try
        {
            // Свип долгов — до цикла и без единого сетевого запроса. Страховка на случай,
            // когда воркер умер тихо, а до следующего рестарта далеко.
            try { res["swept"] = JutWantsSweep(); } catch { }

            var arr = JutLoadWatch();
            var recs = arr.OfType<JObject>().ToList();
            if (recs.Count == 0) { res["watched"] = 0; return res; }

            // 1) один запрос: slug → число серий
            var (ongoingOk, ongoing) = await loadOngoing();

            // Пиггибек: счётчик серий у онгоингов в снапшоте каталога обновляем этим же ответом —
            // ноль дополнительных запросов к сайту (иначе витрина показывала бы «12 серий»
            // у тайтла, где их уже 15, до следующего полного пересбора).
            if (ongoingOk) { try { JutCatalogOngoingUpdate(ongoing); } catch { } }

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
                                 || now != knownCount
                                 // ⚠️ Непогашенный долг опрашиваем всегда: совпадение счётчика
                                 // с baseline не значит, что серия доехала до диска. Свип
                                 // покрывает этот случай и без сети, но гейт, который в принципе
                                 // не может увидеть долг, — мина под будущую правку.
                                 || JutWantsHas(slug);

                if (!mustProbe) continue;
                if (probed >= budget) break;
                probed++;

                var (t, err) = await loadTitle(slug);
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
                // ⚠️ Снимок диска берём ОДИН раз на тайтл: JutHaveFile внутри Where давал
                // Directory.EnumerateFiles на каждую серию — O(N×M) на каждом тике, и так
                // для каждого из 30 тайтлов бюджета.
                var diskKeys = JutDiskKeys(slug);

                // Режим гейтит ТОЛЬКО скачивание: уведомления уходят в обоих режимах,
                // «только уведомления» — это и есть весь смысл подписки с карточки тайтла.
                bool auto = JutModeOf(rec) == "grab";

                // 🔴 НЕПОГАШЕННЫЙ ДОЛГ. Комментарий выше декларировал diff(сайт, ДИСК), но код
                // ниже фильтровал ещё и `!knownKeys.Contains(...)` — то есть сверку с диском
                // обезвреживал сам же фильтр: серия, попавшая в baseline (а туда её кладут СРАЗУ
                // после постановки, независимо от того, доехала она до файла), в toGrab больше
                // не попадала НИКОГДА. Условие снято, а чтобы вместе с ним не поехал бэклог
                // (у тайтла, где скачаны 2 сезона из 5, сверка с диском потянула бы всё),
                // добор берётся ИСКЛЮЧИТЕЛЬНО из журнала намерений: бэклога там нет
                // по построению — записи создаёт только владелец или прошлый тик.
                var owedKeys = auto
                    ? new HashSet<string>(JutWantsOwedEps(slug).Select(x => x.epkey), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                var toGrab = fresh
                    .Concat(inSeason.Where(e => owedKeys.Contains(e.epkey)))
                    .GroupBy(e => e.epkey, StringComparer.Ordinal).Select(g => g.First())
                    .Where(e => !diskKeys.Contains(JutEpKey(slug, e)))
                    .ToList();

                if (fresh.Count > 0 || (auto && toGrab.Count > 0))
                {
                    if (fresh.Count > 0)
                    {
                        changed++;
                        rec["lastChange"] = DateTime.UtcNow;
                    }

                    // «качаю» в тексте — только про серии, которые ДЕЙСТВИТЕЛЬНО поедут в очередь:
                    // серия, уже лежащая на диске, в toGrab не попадает, и обещать по ней
                    // скачивание было бы врать.
                    // ⚠️ Уведомляем ТОЛЬКО о fresh: долг уже уведомляли при первой постановке,
                    // повтор означал бы строку в ленте каждые сутки, пока сайт лежит.
                    var grabKeys = new HashSet<string>(toGrab.Select(x => x.epkey), StringComparer.Ordinal);
                    if (NotiRoute.Enabled)
                        JutNotifyNewEpisodes(slug, t.titleRu, fresh, auto);
                    else
                        foreach (var e in fresh)
                            JutNotifyNewEpisode(slug, t.titleRu, e, auto && grabKeys.Contains(e.epkey));

                    bool baselineHold = false;
                    if (auto && toGrab.Count > 0)
                    {
                        string space = JutCheckSpace(toGrab.Count + _jutQueue.Count, slug);
                        if (space != null)
                        {
                            JutNet.Log("watch", slug + ": автоскачивание отменено — " + space);
                            // 🔥 Baseline НЕ двигаем: toGrab исключает known.keys, и сдвинутый
                            // baseline вычёркивал бы серию из скачивания НАВСЕГДА (доберёт разве что
                            // JutReconcile, и только если остался .part). Плюс молчаливый отказ
                            // в режиме «качаю» недопустим — тревога в колокольчик.
                            JutNotifyNoSpace(slug, t.titleRu, toGrab.Count, space);
                            baselineHold = true;
                        }
                        else
                        {
                            // счётчик ЭТОГО тайтла: общий queued растёт по всем, и на нём мета
                            // писалась бы соседнему тайтлу, ничего не поставившему в очередь
                            // 🔴 ФАЗА 1 — намерение на диск ДО постановки и ДО сдвига baseline.
                            // Тогда после падения либо намерение на диске (восстановимо), либо
                            // baseline не сдвинулся и следующий тик снова увидит серию как fresh.
                            JutWantsCommit(slug, t.titleRu, toGrab, "watch");

                            bool freshBatch = JutPendingFor(slug) == 0;
                            int q = 0;
                            lock (_jutEnqLock)
                            {
                                int gen = JutGenOf(slug);
                                foreach (var e in toGrab)
                                {
                                    if (!_jutQueued.Add(JutQueueKey(slug, e.epkey))) continue;
                                    _jutQueue.Enqueue(new JutGrabItem
                                    {
                                        slug = slug, season = e.season, ep = e.num,
                                        kind = JutKindParam(e.kind), epkey = e.epkey, titleRu = t.titleRu,
                                        gen = gen
                                    });
                                    q++;
                                }
                            }
                            queued += q;
                            // Job у автокачки раньше не заводился вовсе — /qdl/jut/download/status
                            // показывал «fileDone/0». Заодно это задаёт режим уведомлений пачки:
                            // одна вышедшая серия уведомит как серия, догон нескольких — одной строкой.
                            if (q > 0) { JutJobForBatch(slug, freshBatch, q); await JutEnsureMeta(slug, t); JutKickWorker(); }
                        }
                    }

                    // Baseline двигаем только когда было что двигать: проход, вызванный одним
                    // лишь непогашенным долгом, состояние источника не менял.
                    if (!baselineHold && fresh.Count > 0)
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

    /// <summary>
    /// Дешёвый общий опрос: ОДИН запрос отвечает, у кого вырос счётчик серий.
    /// Вынесено из тика ради точки подмены сети (см. параметры JutWatchTick).
    /// </summary>
    static async Task<(bool ok, Dictionary<string, int> counts)> JutLoadOngoingStatic()
    {
        var ongoing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var resp = await JutNet.PostForm(JutNet.Host + "/anime/ongoing/",
            "ajax_load=yes&start_from_page=1&show_search=&anime_of_user=", JutSuParse.ReachedCatalog);
        bool ok = resp != null && resp.reached;
        if (ok)
            foreach (var c in JutSuParse.ParseCatalog(resp.body).items)
                if (!string.IsNullOrEmpty(c.slug)) ongoing[c.slug] = c.episodes;
        return (ok, ongoing);
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

    /// <summary>
    /// «Вышла новая серия».
    /// 🔥 kind = "NEW" обязателен и для film/ova тоже: клиент печатает все ЗНАКОМЫЕ виды
    /// (null, OVA, FILM, …) как «скачана», и с прежним kind уведомление врало про серию,
    /// которой на диске нет — а в режиме «только уведомления» её там не будет никогда.
    /// Вид серии остаётся в label.
    /// ⚠️ epkey = "new-" + epkey НЕ менять: совпадение с epkey из JutNotifyDone схлопнет
    /// «вышла» и «скачана» в одну запись по UNIQUE noti(seriesKey, epkey).
    /// </summary>
    /// <summary>
    /// Волна новых серий ОДНОЙ строкой (qdl 2.111). Раньше здесь стоял цикл по fresh, и за
    /// сутки простоя сайта «Игра лжецов» с «Тяжёлым рыцарем» давали по строке на каждую серию.
    /// У XSMART та же задача была решена одной строкой на тик с самого начала
    /// (XsmartWatch.XsmartNotifyNew) — здесь просто не было сделано.
    ///
    /// На АВТОКАЧКЕ строка уходит в журнал владельца, а не зрителю: зритель узнаёт о серии,
    /// когда её можно смотреть (JutNotifyDone/JutNotifyTitleDone) — решение владельца. В режиме
    /// «только уведомляю» качать нечего, и «вышла» — единственный способ сообщить о серии,
    /// поэтому там строка идёт в ленту.
    /// </summary>
    static void JutNotifyNewEpisodes(string slug, string title, List<JutEp> fresh, bool auto)
    {
        if (fresh == null || fresh.Count == 0) return;
        try
        {
            using var db = new SqlContext();
            string sk = "j" + slug;
            int pushed = 0;

            // Обычные серии складываются в одну волну; OVA/FILM остаются отдельными строками —
            // их мало, и в счётчик серий «OVA 2» не сложить.
            var eps = fresh.Where(x => x.kind == JutEpKind.Episode).ToList();
            if (eps.Count > 0)
            {
                var nums = eps.Select(x => x.num).ToList();
                int season = eps.Select(x => x.season).Distinct().Count() == 1 ? eps[0].season : -1;
                string key = "newwave-" + (season >= 0 ? "s" + season : "") + "e" + nums.Max();
                string text = season >= 0
                    ? NotiRoute.Episodes(season, nums)
                    : "Вышло новых серий: " + nums.Distinct().Count();
                if (JutNewRow(db, sk, slug, title, season, nums.Max(), key, text, auto)) pushed++;
            }

            foreach (var e in fresh.Where(x => x.kind != JutEpKind.Episode))
                if (JutNewRow(db, sk, slug, title, -1, e.num, "newwave-" + e.epkey,
                              "Вышло: " + e.kind + " " + e.num, auto)) pushed++;

            db.SaveChanges();
            if (pushed > 0) PushNotiSignal(pushed);
        }
        catch (Exception ex) { JutNet.Log("watch", "noti волны: " + ex.Message); }
    }

    /// <summary>
    /// Одна строка волны. Возвращает true, если строка ушла ЗРИТЕЛЮ (то есть нужен WS-пуш).
    /// 🔴 Дедуп в seen, а не в noti: строки в ленте может не быть вовсе (автокачка), а ту, что
    /// есть, чистит NotiPrune — после чистки волна пришла бы заново. seen не чистится никогда,
    /// префикс «newwave-» с эпизодными ключами jut (s1e7) не пересекается.
    /// </summary>
    static bool JutNewRow(SqlContext db, string sk, string slug, string title,
                          int season, int ep, string key, string text, bool auto)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (db.seen.Any(x => x.seriesKey == sk && x.epkey == key)) return false;
        db.seen.Add(new SeenModel { seriesKey = sk, epkey = key });

        if (auto)
        {
            QdlEvents.Log(QdlEvents.CatWatch, title ?? slug, text + " — ставлю в очередь",
                          JutNet.Hash(slug), sk, key: key);
            return false;
        }

        db.noti.Add(new NotiModel
        {
            seriesKey = sk, seriesId = 0, hash = JutNet.Hash(slug),
            title = title ?? slug, season = season, episode = ep,
            kind = "NEW", epkey = key, label = text,
            created = DateTime.UtcNow, read = false
        });
        QdlEvents.Log(QdlEvents.CatUser, title ?? slug, text, JutNet.Hash(slug), sk, key: key);
        return true;
    }

    static void JutNotifyNewEpisode(string slug, string title, JutEp e, bool auto)
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
                kind = "NEW",
                epkey = key,
                label = (e.kind == JutEpKind.Episode
                    ? $"jut.su · сезон {e.season} · серия {e.num} — вышла"
                    : $"jut.su · {e.kind} {e.num} — вышла") + (auto ? ", качаю" : ""),
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
        }
        catch (Exception ex) { JutNet.Log("watch", "noti: " + ex.Message); }
    }

    /// <summary>
    /// Нехватка места в режиме «качаю». Раньше это была ОДНА строка в лог: владелец ждал
    /// серию, которой не будет, и узнать об этом было негде.
    /// Дедуп по ДНЮ: /qdl/jut/watch/check дёргается руками сколько угодно раз.
    /// </summary>
    static void JutNotifyNoSpace(string slug, string title, int files, string reason)
    {
        try
        {
            using var db = new SqlContext();
            string sk = "j" + slug;
            string key = "nospace-" + DateTime.UtcNow.ToString("yyyyMMdd");
            string label = $"Новые серии ({files}) не скачаны — {reason}";

            // qdl 2.111: нехватка места — забота владельца, не зрителя (его выбор). Дедуп
            // по дню переехал в seen: в ленте строки больше нет, а ретенция её всё равно съела бы.
            if (NotiRoute.Enabled)
            {
                if (db.seen.Any(x => x.seriesKey == sk && x.epkey == key)) return;
                db.seen.Add(new SeenModel { seriesKey = sk, epkey = key });
                db.SaveChanges();
                QdlEvents.Log(QdlEvents.CatSpace, title ?? slug, label, JutNet.Hash(slug), sk, key: key);
                return;
            }

            if (db.noti.Any(x => x.seriesKey == sk && x.epkey == key)) return;
            db.noti.Add(new NotiModel
            {
                seriesKey = sk, seriesId = 0, hash = JutNet.Hash(slug),
                title = title ?? slug, season = -1, episode = -1,
                kind = "NOSPACE", epkey = key,
                label = label,
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
                // «jut.su ·» и «слежу за ним» — кухня: зрителю важен факт выхода сезона,
                // откуда он приехал и что мы с ним делаем, он не спрашивал (qdl 2.111).
                label = NotiRoute.Enabled ? NotiRoute.Season(season) : $"jut.su · вышел сезон {season} — слежу за ним",
                created = DateTime.UtcNow, read = false
            });
            db.SaveChanges();
            PushNotiSignal(1);
            QdlEvents.Log(QdlEvents.CatUser, title ?? slug, NotiRoute.Season(season), JutNet.Hash(slug), sk, key: key);
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
