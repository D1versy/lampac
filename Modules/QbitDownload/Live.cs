using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ── D1VERSY LIVE — записи с домашнего видеорегистратора (проект IPCamLive, C:\IPCamLive) ────────
//
// Регистратор живёт ТОЛЬКО в LAN (http://192.168.87.24 → nginx → FastAPI) и наружу не проброшен,
// а клиенты D1Vision снаружи ходят исключительно на наш origin (tv.d1versy.com:9443 → Caddy →
// lampac). Поэтому здесь тонкий прокси: каталог (какие камеры и что записали за день) + байтовый
// прокси mp4/jpeg. LAN-адрес регистратора клиенту НИКОГДА не уходит (чек-лист claude/08).
// Периметр (D1VPerimeter) покрывает эти пути автоматически — отдельный ключ не нужен, нативные
// плееры подписывают URL сами (D1VAuth), а mp4 — один URL без относительных под-ресурсов, так что
// подпись сегментов (как у /qdl/hls) здесь не требуется.
//
// ⚠️ Времена регистратора — НАИВНЫЙ UTC: контейнер ipcam-backend поднят с TZ=UTC, datetime.now()
// пишет UTC, SQLite срезает зону (его же фронт парсит их как UTC — frontend/src/lib/utils.ts:33).
// Всё, что уходит клиенту, здесь переводится в локальную зону (TZ контейнера = Europe/Moscow),
// а «локальный день» разворачивается в UTC-окно, которое может задевать ДВЕ UTC-даты регистратора.
//
// ⚠️ Сам IPCamLive мы не трогаем (рестарт бэкенда прервал бы запись), поэтому агрегат
// «какие камеры писали в этот день» собирается веером запросов отсюда.
public partial class QbitController
{
    #region инфраструктура (клиенты, конфиг, время)

    // Каталог: ответы мелкие, отвечает SQLite → короткий таймаут.
    static readonly HttpClient _liveApi = new HttpClient(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    // Медиа: общего таймаута НЕТ (часовая запись качается сколько нужно) — иначе HttpClient.Timeout
    // оборвал бы уже идущее видео; обрыв клиента ловим через HttpContext.RequestAborted.
    // Ограничен только коннект, чтобы выключенный регистратор не копил висящие запросы.
    static readonly HttpClient _liveMedia = new HttpClient(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    static string LiveBase()
    {
        string b = ModInit.conf?.liveUrl;
        if (string.IsNullOrWhiteSpace(b))
            b = "http://192.168.87.24";
        return b.Trim().TrimEnd('/');
    }

    static TimeZoneInfo LiveTz()
    {
        string id = ModInit.conf?.liveTimezone;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
            catch { }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>Наивный UTC регистратора (или строка с зоной) → DateTime kind=Utc.</summary>
    static bool TryLiveUtc(string s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        // AssumeUniversal — «2026-07-26T16:39:09.563288» без зоны это UTC регистратора;
        // AdjustToUniversal — если зона всё же пришла, приводим к UTC.
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return false;

        utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return true;
    }

    static DateTime LiveToday(TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;

    /// <summary>date=YYYY-MM-DD (локальная дата) или пусто = сегодня.</summary>
    static bool TryLiveDay(string s, TimeZoneInfo tz, out DateTime day)
    {
        day = LiveToday(tz);
        if (string.IsNullOrWhiteSpace(s))
            return true;

        if (!DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return false;

        day = d.Date;
        return true;
    }

    static DateTime LiveToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
            local = local.AddHours(1);   // «дыра» перевода часов: полуночи в этот день не существует
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// <summary>Локальные сутки → UTC-окно [from, to).</summary>
    static (DateTime from, DateTime to) LiveDayWindow(DateTime day, TimeZoneInfo tz)
        => (LiveToUtc(day.Date, tz), LiveToUtc(day.Date.AddDays(1), tz));

    static string LiveTime(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("HH:mm", CultureInfo.InvariantCulture);

    static readonly string[] _liveMonths = { "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря" };
    static readonly string[] _liveWdays = { "вс", "пн", "вт", "ср", "чт", "пт", "сб" };

    static string LiveDayLabel(DateTime day, DateTime today)
    {
        int diff = (int)(day.Date - today.Date).TotalDays;
        if (diff == 0) return "Сегодня";
        if (diff == -1) return "Вчера";
        if (diff == -2) return "Позавчера";
        return day.Day + " " + _liveMonths[day.Month - 1] + ", " + _liveWdays[(int)day.DayOfWeek];
    }

    static string LiveDayKey(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    ActionResult LiveJsonOut(JToken payload)
    {
        SetHeadersNoCache();
        return ContentTo(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    // Ошибку отдаём 200 + {"error":...}: клиент (qdl.js) показывает текст на экране,
    // а не молчаливый «пустой список» от провалившегося XHR.
    ActionResult LiveErr(string msg) => LiveJsonOut(new JObject { ["error"] = msg });

    #endregion

    #region запросы к регистратору

    // Веер запросов к регистратору шире, чем позволяет его nginx (`limit_req zone=api rate=30r/s`):
    // одна отрисовка списка камер — это 1 + 2×N запросов, а быстрое листание днями их складывает.
    // Проверено ревью: 13-way параллелизм ловит 429. Держим ширину узкой — регистратор пишет видео,
    // ему не до нашего веера.
    static readonly SemaphoreSlim _liveGate = new SemaphoreSlim(4);

    /// <summary>
    /// GET к регистратору. null — ЧЕСТНО пусто (404: «нет записей за эту дату»).
    /// Любой другой не-2xx — исключение: «не смог спросить» не должно выглядеть как «записей нет»,
    /// иначе камера молча пропадает из списка, а день отдаётся укороченным (и, что хуже, VOD-полным).
    /// </summary>
    async Task<JToken> LiveApiJson(string path, CancellationToken ct)
    {
        string url = LiveBase() + path;

        await _liveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using var resp = await _liveApi.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                if (!resp.IsSuccessStatusCode)
                {
                    // 429 (упёрлись в его лимит) и 5xx — разово переспрашиваем, дальше честно падаем.
                    bool retryable = resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                    if (retryable && attempt == 0)
                    {
                        await Task.Delay(400, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new HttpRequestException($"live api {(int)resp.StatusCode} {path}");
                }

                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(body) ? null : JToken.Parse(body);
            }
        }
        finally { _liveGate.Release(); }
    }

    /// <summary>Одна запись регистратора в нашем (уже нормализованном) виде.</summary>
    sealed class LiveRec
    {
        public int id;
        public int camera;
        public DateTime startUtc;
        public int seconds;     // 0 = длительность неизвестна
        public long size;
        public string trigger;
    }

    static LiveRec ParseLiveRec(JToken t)
    {
        if (t == null || t.Type != JTokenType.Object)
            return null;

        int id = (int?)t["id"] ?? 0;
        if (id <= 0 || !TryLiveUtc((string)t["start_time"], out var start))
            return null;

        // duration_seconds заполняется при закрытии сегмента; если пусто — считаем по end_time.
        int secs = (int?)t["duration_seconds"] ?? 0;
        if (secs <= 0 && TryLiveUtc((string)t["end_time"], out var end) && end > start)
            secs = (int)(end - start).TotalSeconds;

        return new LiveRec
        {
            id = id,
            camera = (int?)t["camera_id"] ?? 0,
            startUtc = start,
            seconds = Math.Max(0, secs),
            size = (long?)t["file_size_bytes"] ?? 0,
            trigger = (string)t["trigger_type"] ?? "continuous"
        };
    }

    /// <summary>Камера регистратора в нашем виде. Наружу уходят ТОЛЬКО эти поля: ip/логины клиенту не нужны.</summary>
    sealed class LiveCam
    {
        public int id;
        public string name;
        public string protocol;   // rtsp|onvif|mjpeg|hls|upload
        public bool isLive;       // осмысленно только у upload: рекордер сейчас пушит или нет

        public bool IsUpload => string.Equals(protocol, "upload", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Кого показывать в сетке эфира. Ровно правило оригинального Live View регистратора
    /// (frontend/src/lib/cameraVisibility.ts::isCameraVisibleOnLiveView): mac-рекордер существует,
    /// только пока приложение на маке пушит. Обычные камеры видны всегда — даже упавшие.
    /// </summary>
    static bool LiveWatchVisible(LiveCam c) => c != null && (!c.IsUpload || c.isLive);

    /// <summary>Все камеры регистратора с протоколом и признаком «сейчас пушит».</summary>
    async Task<List<LiveCam>> LiveCameraListFull(CancellationToken ct)
    {
        var list = new List<LiveCam>();
        if (await LiveApiJson("/api/cameras/", ct).ConfigureAwait(false) is not JArray arr)
            return list;

        foreach (var c in arr)
        {
            int id = (int?)c["id"] ?? 0;
            if (id <= 0)
                continue;

            string name = ((string)c["name"] ?? "").Trim();
            list.Add(new LiveCam
            {
                id = id,
                name = name.Length > 0 ? name : "Камера " + id,
                protocol = ((string)c["protocol"] ?? "").Trim(),
                isLive = (bool?)c["is_live"] ?? false
            });
        }
        return list;
    }

    /// <summary>id → имя. Совместимый вид для экранов записей (им протокол не нужен).</summary>
    async Task<List<KeyValuePair<int, string>>> LiveCameraList(CancellationToken ct)
        => (await LiveCameraListFull(ct).ConfigureAwait(false))
            .Select(c => new KeyValuePair<int, string>(c.id, c.name))
            .ToList();

    /// <summary>
    /// Записи камеры, НАЧАВШИЕСЯ внутри UTC-окна (= локальные сутки). Регистратор умеет фильтр
    /// только по своей (UTC) дате, а локальные сутки задевают до двух его дат — спрашиваем обе
    /// и режем окном сами. Старт-в-окне (а не пересечение) — чтобы сегмент не дублировался в двух днях.
    /// </summary>
    async Task<List<LiveRec>> LiveDayRecs(int cameraId, DateTime from, DateTime to, CancellationToken ct)
    {
        var seen = new HashSet<int>();
        var res = new List<LiveRec>();

        for (var d = from.Date; d <= to.AddTicks(-1).Date; d = d.AddDays(1))
        {
            if (await LiveApiJson($"/api/recordings/camera/{cameraId}/by-date?date={LiveDayKey(d)}", ct).ConfigureAwait(false) is not JArray arr)
                continue;

            foreach (var t in arr)
            {
                var r = ParseLiveRec(t);
                if (r == null || r.startUtc < from || r.startUtc >= to)
                    continue;
                if (seen.Add(r.id))
                    res.Add(r);
            }
        }

        res.Sort((a, b) => a.startUtc.CompareTo(b.startUtc));
        return res;
    }

    #endregion

    #region /qdl/live/cameras — камеры, у которых ЕСТЬ записи за день (только они)

    [HttpGet, AllowAnonymous]
    [Route("qdl/live/cameras")]
    async public Task<ActionResult> LiveCameras(string date = null)
    {
        if (LiveDenied(Perms.FeatureLive) && LiveDenied(Perms.FeatureRec)) return NotFound();

        var tz = LiveTz();
        if (!TryLiveDay(date, tz, out var day))
            return LiveErr("Неверная дата");

        var ct = HttpContext.RequestAborted;
        var today = LiveToday(tz);
        var (from, to) = LiveDayWindow(day, tz);

        List<KeyValuePair<int, string>> cams;
        List<LiveRec>[] perCam;
        try
        {
            cams = await LiveCameraList(ct).ConfigureAwait(false);
            if (cams.Count == 0)
                return LiveErr("Регистратор не отдал список камер");

            perCam = await Task.WhenAll(cams.Select(c => LiveDayRecs(c.Key, from, to, ct))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] cameras: {Error}", ex.Message);
            return LiveErr("Регистратор недоступен");
        }

        // Только камеры с записями за этот день; свежие сверху (последняя запись позже — выше).
        var rows = new List<(KeyValuePair<int, string> cam, List<LiveRec> recs)>();
        for (int i = 0; i < cams.Count; i++)
        {
            if (perCam[i].Count > 0)
                rows.Add((cams[i], perCam[i]));
        }
        rows.Sort((a, b) => b.recs[^1].startUtc.CompareTo(a.recs[^1].startUtc));

        var items = new JArray();
        foreach (var (cam, recs) in rows)
        {
            var last = recs[^1];
            items.Add(new JObject
            {
                ["id"] = cam.Key,
                ["name"] = cam.Value,
                ["count"] = recs.Count,
                ["first"] = LiveTime(recs[0].startUtc, tz),
                ["last"] = LiveTime(last.startUtc.AddSeconds(last.seconds), tz),
                ["seconds"] = recs.Sum(r => (long)r.seconds),
                ["thumb"] = last.id          // постер камеры = кадр из самой свежей записи дня
            });
        }

        return LiveJsonOut(new JObject
        {
            ["date"] = LiveDayKey(day),
            ["label"] = LiveDayLabel(day, today),
            ["today"] = LiveDayKey(today),
            ["total"] = cams.Count,          // сколько камер вообще есть (для «писали 2 из 6»)
            ["cameras"] = items
        });
    }

    #endregion

    #region /qdl/live/recordings — записи одной камеры за день

    [HttpGet, AllowAnonymous]
    [Route("qdl/live/recordings")]
    async public Task<ActionResult> LiveRecordings(int camera, string date = null)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        if (camera <= 0)
            return LiveErr("Не указана камера");

        var tz = LiveTz();
        if (!TryLiveDay(date, tz, out var day))
            return LiveErr("Неверная дата");

        var ct = HttpContext.RequestAborted;
        var today = LiveToday(tz);
        var (from, to) = LiveDayWindow(day, tz);

        string name = null;
        List<LiveRec> recs;
        try
        {
            var cams = await LiveCameraList(ct).ConfigureAwait(false);
            foreach (var c in cams)
            {
                if (c.Key == camera) { name = c.Value; break; }
            }
            recs = await LiveDayRecs(camera, from, to, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] recordings: {Error}", ex.Message);
            return LiveErr("Регистратор недоступен");
        }

        var items = new JArray();
        foreach (var r in recs)
        {
            items.Add(new JObject
            {
                ["id"] = r.id,
                ["start"] = LiveTime(r.startUtc, tz),
                ["end"] = LiveTime(r.startUtc.AddSeconds(r.seconds), tz),
                ["seconds"] = r.seconds,
                ["size"] = r.size,
                ["trigger"] = r.trigger
            });
        }

        return LiveJsonOut(new JObject
        {
            ["date"] = LiveDayKey(day),
            ["label"] = LiveDayLabel(day, today),
            ["camera"] = new JObject { ["id"] = camera, ["name"] = name ?? ("Камера " + camera) },
            ["items"] = items
        });
    }

    #endregion

    #region /qdl/live/days — дни, за которые записи вообще есть (для выбора дня)

    // Дёшево: один запрос последних N записей (desc) + группировка по локальной дате. Глубже
    // истории, чем влезло в limit, список не покажет — но стрелки «предыдущий день» в UI никуда
    // не деваются, так что это именно подсказка, а не единственный способ навигации.
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/days")]
    async public Task<ActionResult> LiveDays(int back = 0)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        var tz = LiveTz();
        var ct = HttpContext.RequestAborted;
        var today = LiveToday(tz);

        if (back <= 0)
            back = ModInit.conf?.liveDaysBack > 0 ? ModInit.conf.liveDaysBack : 14;
        back = Math.Clamp(back, 1, 90);

        var counts = new Dictionary<string, (int recs, HashSet<int> cams)>();
        try
        {
            if (await LiveApiJson("/api/recordings/?limit=500", ct).ConfigureAwait(false) is JArray arr)
            {
                foreach (var t in arr)
                {
                    var r = ParseLiveRec(t);
                    if (r == null)
                        continue;

                    var local = TimeZoneInfo.ConvertTimeFromUtc(r.startUtc, tz).Date;
                    if ((today - local).TotalDays >= back || local > today)
                        continue;

                    string key = LiveDayKey(local);
                    if (!counts.TryGetValue(key, out var acc))
                        counts[key] = acc = (0, new HashSet<int>());
                    acc.cams.Add(r.camera);
                    counts[key] = (acc.recs + 1, acc.cams);
                }
            }
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] days: {Error}", ex.Message);
            return LiveErr("Регистратор недоступен");
        }

        // Сегодня в списке всегда — даже если сегодня ещё ничего не писали.
        string todayKey = LiveDayKey(today);
        if (!counts.ContainsKey(todayKey))
            counts[todayKey] = (0, new HashSet<int>());

        var days = new JArray();
        foreach (var kv in counts.OrderByDescending(k => k.Key, StringComparer.Ordinal))
        {
            DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d);
            days.Add(new JObject
            {
                ["date"] = kv.Key,
                ["label"] = LiveDayLabel(d, today),
                ["count"] = kv.Value.recs,
                ["cameras"] = kv.Value.cams.Count
            });
        }

        return LiveJsonOut(new JObject { ["today"] = todayKey, ["days"] = days });
    }

    #endregion

    #region /qdl/live/feed — СКВОЗНАЯ лента: свежие записи сверху, старые подгружаются вниз

    // Жалоба владельца: «записи видно только за текущий месяц, до прошлого надо доклацать
    // стрелками по одному дню». Навигация в Rec крутилась вокруг ОДНОГО дня, а список дней
    // (/qdl/live/days) сервер резал окном liveDaysBack=14. Лента снимает вопрос целиком:
    // все записи всех камер подряд по убыванию времени, страницами.
    //
    // Регистратор такое уже умеет сам — `/api/recordings/?limit=&offset=` отдаёт
    // ORDER BY start_time DESC (recordings.py:31-46, индекс ix_recordings_camera_start),
    // так что это ОДИН запрос на страницу против 1 + 2×N у /qdl/live/cameras.

    // Страница ленты живёт полминуты: прокрутка не должна долбить регистратор — его nginx
    // режет /api/ лимитом rate=30r/s, на веере мы уже ловили 429. Ключ — offset|limit.
    static readonly ConcurrentDictionary<string, (DateTime exp, JObject payload)> _liveFeedCache = new();

    [HttpGet, AllowAnonymous]
    [Route("qdl/live/feed")]
    async public Task<ActionResult> LiveFeed(int offset = 0, int limit = 30)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);

        string key = offset + "|" + limit;
        if (_liveFeedCache.TryGetValue(key, out var hit) && hit.exp > DateTime.Now)
            return LiveJsonOut(hit.payload);

        var tz = LiveTz();
        var ct = HttpContext.RequestAborted;
        var today = LiveToday(tz);

        var recs = new List<LiveRec>();
        List<KeyValuePair<int, string>> cams;
        try
        {
            cams = await LiveCameraList(ct).ConfigureAwait(false);
            // +1 запись сверх страницы — чтобы честно ответить hasNext, а не гадать по размеру.
            if (await LiveApiJson($"/api/recordings/?limit={limit + 1}&offset={offset}", ct)
                    .ConfigureAwait(false) is JArray arr)
            {
                foreach (var t in arr)
                {
                    var r = ParseLiveRec(t);
                    if (r != null)
                        recs.Add(r);
                }
            }
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            // Не-2xx от регистратора — это «не смог спросить», а НЕ «записей нет»: молчаливый
            // пустой список читался бы как «архив кончился» и обрывал ленту (грабля §AL).
            Serilog.Log.Warning("[qdl/live] feed: {Error}", ex.Message);
            return LiveErr("Регистратор недоступен");
        }

        bool hasNext = recs.Count > limit;
        if (hasNext)
            recs.RemoveRange(limit, recs.Count - limit);

        // Порядок регистратора на веру не берём — сортируем сами.
        recs.Sort((a, b) => b.startUtc.CompareTo(a.startUtc));

        var names = new Dictionary<int, string>();
        foreach (var kv in cams)
            names[kv.Key] = kv.Value;

        var items = new JArray();
        foreach (var r in recs)
        {
            // Времена регистратора — наивный UTC (см. шапку файла): всё, что уходит клиенту,
            // конвертируем через liveTimezone. День нужен, чтобы из ленты можно было провалиться
            // в существующий экран дня — в JSON отдельной записи даты раньше не было вовсе.
            var localDay = TimeZoneInfo.ConvertTimeFromUtc(r.startUtc, tz).Date;
            items.Add(new JObject
            {
                ["id"] = r.id,
                ["camera"] = r.camera,
                ["cameraName"] = names.TryGetValue(r.camera, out var nm) ? nm : ("Камера " + r.camera),
                ["day"] = LiveDayKey(localDay),
                ["dayLabel"] = LiveDayLabel(localDay, today),
                ["start"] = LiveTime(r.startUtc, tz),
                ["end"] = LiveTime(r.startUtc.AddSeconds(r.seconds), tz),
                ["seconds"] = r.seconds,
                ["size"] = r.size,
                ["trigger"] = r.trigger
            });
        }

        var payload = new JObject
        {
            ["offset"] = offset,
            ["limit"] = limit,
            ["hasNext"] = hasNext,
            ["today"] = LiveDayKey(today),
            ["items"] = items
        };

        if (_liveFeedCache.Count > 64)
            _liveFeedCache.Clear();   // ключей мало и они предсказуемы — чистим целиком, без LRU
        _liveFeedCache[key] = (DateTime.Now.AddSeconds(30), payload);

        return LiveJsonOut(payload);
    }

    #endregion

    #region /qdl/live/day — ВЕСЬ ДЕНЬ ОДНОЙ ЗАПИСЬЮ (склейка в один HLS, один таймлайн)

    // Регистратор умеет склейку сам (`/api/recordings/camera/{id}/stitched.m3u8`): каждая запись
    // один раз ремуксится (stream copy, ~9 с на 17 мин видео) в TS-сегменты, а плейлист сшивает их
    // через EXT-X-DISCONTINUITY — плеер идёт по суткам без «следующего файла», с одним таймлайном.
    //
    // Но его плейлист группирует по ЕГО (UTC) суткам, а у нас день локальный: наше окно задевает
    // две его даты, и у клиента «сегодня» разъехалось бы с показанными временами. Поэтому берём
    // готовые посегментные индексы (`/hls/_vod/{rec}/index.m3u8`, их пишет тот же ремукс) и
    // собираем плейлист САМИ — ровно по нашему окну, в нашем порядке.
    //
    // Ремукс запускается ТОЛЬКО его stitched.m3u8 (там внутри kick_series), поэтому дёргаем его на
    // каждую задетую UTC-дату. Это же обновляет TTL-метку кэша (`.done` трогается при чтении), так
    // что уборщик регистратора (24 ч) не снесёт сегменты под играющим зрителем.
    //
    // Пока задние куски ещё ремуксятся, плейлист EVENT (без ENDLIST) — плеер его перечитывает и
    // лента растёт; когда готовы все, отдаём VOD + ENDLIST. Смотреть можно с первого готового куска.

    sealed class LiveSeg { public double dur; public string name; public int rec; }

    sealed class LiveDayBuild
    {
        // Плейлист хранится с плейсхолдерами {?d1v}/{&d1v} в сегментных строках: ключ периметра у каждого
        // зрителя свой (и в LAN его нет), а кэш общий — подставляем на отдаче, не в кэше.
        public string playlist;
        public double seconds;      // длительность готовой части
        public int ready;           // сколько кусков уже вошло
        public int total;           // сколько всего кусков за день
        public bool complete;       // все куски готовы (или битые) → VOD
    }

    // Готовый плейлист живёт секунды: EVENT-режим клиент перечитывает часто, а собирается он
    // из 6-10 запросов к регистратору. Ключ — камера+день.
    static readonly ConcurrentDictionary<string, (DateTime exp, LiveDayBuild build)> _liveDayCache = new();

    static string LiveSegName(string line)
    {
        int i = line.LastIndexOf('/');
        return i >= 0 ? line.Substring(i + 1) : line;
    }

    /// <summary>Сегменты одной записи из её индекса (пишет ffmpeg при ремуксе). null — индекса нет.</summary>
    async Task<List<LiveSeg>> LiveRecSegments(int recId, CancellationToken ct)
    {
        string body;
        try
        {
            using var resp = await _liveApi.GetAsync(LiveBase() + "/hls/_vod/" + recId + "/index.m3u8", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        var segs = new List<LiveSeg>();
        double pending = -1;

        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                string v = line.Substring(8).Trim().TrimEnd(',');
                int c = v.IndexOf(',');
                if (c >= 0)
                    v = v.Substring(0, c);
                pending = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : -1;
            }
            else if (line[0] != '#' && pending >= 0)
            {
                segs.Add(new LiveSeg { dur = pending, name = LiveSegName(line), rec = recId });
                pending = -1;
            }
        }

        return segs;
    }

    // База PTS записи: первая метка её первого сегмента. Нужна, чтобы сдвинуть кусок ровно на его
    // смещение в сутках (LiveTs). Кэш вечный и по записи, а не по дню: сегменты записи неизменны,
    // ремукс детерминированный (-c copy), так что даже после уборки TTL-кэша регистратора и
    // повторного ремукса метка будет та же. Запись — int → long?, реестр ничтожно мал.
    // ⚠️ Отрицательный ответ кэшируем тоже, иначе битая запись переспрашивалась бы на каждой сборке.
    static readonly ConcurrentDictionary<int, long?> _livePtsBase = new();

    async Task<long?> LiveRecPtsBase(int rec, CancellationToken ct)
    {
        if (_livePtsBase.TryGetValue(rec, out var hit))
            return hit;

        long? pts = null;
        try
        {
            // Тянем ПЕРВЫЕ 64 КБ: качать трёхмегабайтный сегмент ради одной метки незачем, а
            // /hls/_vod/ регистратор отдаёт статикой через nginx с честным 206.
            using var req = new HttpRequestMessage(HttpMethod.Get, LiveBase() + $"/hls/_vod/{rec}/seg_00000.ts");
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-65535");

            await _liveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var resp = await _liveApi.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    pts = LiveTs.FirstPts(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            }
            finally { _liveGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Не кэшируем: сбой сети — не приговор записи, на следующей сборке переспросим.
            Serilog.Log.Warning("[qdl/live] pts base {Rec}: {Error}", rec, ex.Message);
            return null;
        }

        _livePtsBase[rec] = pts;
        return pts;
    }

    /// <summary>Статус ремукса у регистратора: id записи → (готова, битая).</summary>
    // ⚠️ Пинок ОБЯЗАН нести `from`. Без него регистратор берёт ВСЕ записи своей UTC-даты и ремуксит
    // их по порядку с 00:00Z — а у локального дня первая задетая UTC-дата начинается в 21:00Z, то
    // есть почти все её записи принадлежат ПРЕДЫДУЩЕМУ локальному дню. У камеры, пишущей непрерывно,
    // зритель ждал бы, пока перемелется чужой день (и тот занял бы диск), прежде чем появится его
    // первый кусок. С `from` очередь начинается ровно с нашей первой записи.
    async Task<Dictionary<int, (bool ready, bool failed)>> LiveVodStatus(int camera, List<LiveRec> recs, DateTime from, DateTime to, CancellationToken ct)
    {
        var map = new Dictionary<int, (bool, bool)>();

        for (var d = from.Date; d <= to.AddTicks(-1).Date; d = d.AddDays(1))
        {
            // первая НАША запись, начавшаяся в эту UTC-дату; нет таких — дату вообще не трогаем
            LiveRec head = null;
            foreach (var r in recs)
            {
                if (r.startUtc.Date == d) { head = r; break; }
            }
            if (head == null)
                continue;

            string q = $"date={LiveDayKey(d)}&from={head.id}";

            // Пинок ремуксу (он же — «этот кэш ещё нужен» для TTL-уборщика регистратора).
            // Тело не нужно: свой плейлист мы собираем ниже. Провал не фатален — статус ниже покажет.
            try
            {
                await _liveGate.WaitAsync(ct).ConfigureAwait(false);
                try { using var kick = await _liveApi.GetAsync(LiveBase() + $"/api/recordings/camera/{camera}/stitched.m3u8?{q}", ct).ConfigureAwait(false); }
                finally { _liveGate.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (await LiveApiJson($"/api/recordings/camera/{camera}/stitched.json?{q}", ct).ConfigureAwait(false) is JObject j
                && j["items"] is JArray items)
            {
                foreach (var it in items)
                {
                    int id = (int?)it["recording_id"] ?? 0;
                    if (id > 0)
                        map[id] = ((bool?)it["ready"] ?? false, (bool?)it["failed"] ?? false);
                }
            }
        }

        return map;
    }

    /// <summary>Собрать плейлист «весь день одним файлом» по нашим локальным суткам.</summary>
    async Task<LiveDayBuild> LiveBuildDay(int camera, DateTime day, TimeZoneInfo tz, CancellationToken ct)
    {
        string key = camera + ":" + LiveDayKey(day);
        if (_liveDayCache.TryGetValue(key, out var hit) && hit.exp > DateTime.UtcNow)
            return hit.build;

        var (from, to) = LiveDayWindow(day, tz);
        var recs = await LiveDayRecs(camera, from, to, ct).ConfigureAwait(false);
        if (recs.Count == 0)
            return null;

        var status = await LiveVodStatus(camera, recs, from, to, ct).ConfigureAwait(false);

        var parts = new List<List<LiveSeg>>();
        bool complete = true;

        foreach (var r in recs)
        {
            status.TryGetValue(r.id, out var st);
            if (st.failed)
                continue;   // битый кусок пропускаем — честная дырка, следующий пойдёт с DISCONTINUITY

            if (!st.ready)
            {
                // Дальше плейлист не продолжаем: порядок сегментов обязан быть стабильным между
                // перечитываниями EVENT-плейлиста, иначе плеер поедет.
                complete = false;
                break;
            }

            var segs = await LiveRecSegments(r.id, ct).ConfigureAwait(false);
            if (segs == null || segs.Count == 0)
            {
                complete = false;
                break;
            }
            parts.Add(segs);
        }

        // Смещение каждого куска в сутках + признак «его метки удастся сдвинуть». С ними сегменты
        // отдаются уже приведёнными к сквозному времени дня, и разрыв (а с ним и EXT-X-DISCONTINUITY,
        // на котором libVLC терял глобальную позицию) исчезает вовсе — разбор в LiveTs.cs.
        double maxSeg = 0, total = 0;
        var offsets = new double[parts.Count];
        var shifted = new bool[parts.Count];

        for (int i = 0; i < parts.Count; i++)
        {
            offsets[i] = total;
            foreach (var s in parts[i])
            {
                if (s.dur > maxSeg) maxSeg = s.dur;
                total += s.dur;
            }
            shifted[i] = parts[i].Count > 0
                && (await LiveRecPtsBase(parts[i][0].rec, ct).ConfigureAwait(false)) != null;
        }

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(Math.Max(12, (int)Math.Ceiling(maxSeg))).Append('\n');
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");

        // Плейлист ВСЕГДА самозавершённый, даже когда сутки ещё домалываются и внутри лежит только
        // готовый префикс. EVENT без ENDLIST libVLC считает эфиром: length=0, ползунок схлопывается
        // в 0..1, драг-скраб мёртв, а Android вдобавок ВЫБРАСЫВАЕТ позицию просмотра
        // (finishWithResult требует pos>0 && dur>0). Ради «лента растёт сама» это слишком дорого;
        // префикс догоняет сутки прогревом. Стабильность порядка сегментов между перечитываниями
        // больше не нужна: VOD-плейлист плеер не перечитывает.
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");

        // {?d1v} / {&d1v} — место под ключ периметра и айди устройства, подставляется на КАЖДОЙ
        // отдаче (см. LiveSignDay). Нативные плееры (VLC) резолвят относительные URI без query
        // базового URL (RFC 3986), поэтому снаружи ключ обязан стоять в самой сегментной строке.
        // Плейсхолдера ДВА: у сдвинутых кусков в строке уже есть ?o=, и разделитель обязан стать
        // амперсандом.
        for (int i = 0; i < parts.Count; i++)
        {
            // Сдвинуть не вышло (кусок не разобрался) — честно возвращаем разрыв для ЭТОГО шва:
            // деградация до прежнего поведения на одном стыке лучше, чем поехавший таймлайн.
            if (i > 0 && !(shifted[i] && shifted[i - 1]))
                sb.Append("#EXT-X-DISCONTINUITY\n");

            string tail = shifted[i]
                ? "?o=" + offsets[i].ToString("0.#####", CultureInfo.InvariantCulture) + "{&d1v}"
                : "{?d1v}";

            foreach (var s in parts[i])
            {
                sb.Append("#EXTINF:").Append(s.dur.ToString("0.#####", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("/qdl/live/seg/").Append(s.rec).Append('/').Append(s.name).Append(tail).Append('\n');
            }
        }

        sb.Append("#EXT-X-ENDLIST\n");

        var build = new LiveDayBuild
        {
            playlist = sb.ToString(),
            seconds = total,
            ready = parts.Count,
            total = recs.Count,
            complete = complete
        };

        // Готовый день кэшируем дольше: он уже не изменится, а перечитывать плейлист плеер может.
        _liveDayCache[key] = (DateTime.UtcNow.AddSeconds(complete ? 60 : 4), build);
        return build;
    }

    /// <summary>Подставить query предъявителя в сегментные строки (в LAN без прав — просто убрать плейсхолдер).</summary>
    /// <remarks>
    /// 🔴 Плейсхолдера ДВА. У сдвинутых кусков в строке уже стоит ?o=&lt;смещение&gt;, и подстановка
    /// через ? дала бы «?o=123?d1v=…»: второй знак вопроса ушёл бы в имя параметра, ключ периметра
    /// не распознался бы, и снаружи каждый сегмент ловил бы 404.
    /// </remarks>
    string LiveSignDay(string playlist)
    {
        string q = LiveSegQuery();                                   // "" | "?d1v=…&uid=…"
        string amp = q.Length == 0 ? "" : "&" + q.Substring(1);

        return playlist.Replace("{?d1v}", q).Replace("{&d1v}", amp);
    }

    /// <summary>
    /// Query для сегментных URI: ключ периметра предъявителя + его айди устройства.
    /// 🔴 Оба обязаны стоять В САМОЙ строке сегмента: нативные плееры (VLC/ExoPlayer) резолвят
    /// относительные URI без query базового URL (RFC 3986) и не несут ни заголовков, ни cookie.
    /// Без uid здесь гейт прав (LiveDenied) отдавал бы 404 на каждый сегмент уже начатого просмотра.
    /// </summary>
    string LiveSegQuery()
    {
        var q = new StringBuilder();

        string d1v = LiveD1vKey();
        if (!string.IsNullOrEmpty(d1v))
            q.Append("d1v=").Append(Uri.EscapeDataString(d1v));

        string uid = Perms.NormUid(requestInfo?.user_uid);
        if (uid != null)
        {
            if (q.Length > 0) q.Append('&');
            q.Append("uid=").Append(Uri.EscapeDataString(uid));
        }

        return q.Length == 0 ? "" : "?" + q.ToString();
    }

    /// <summary>
    /// Гейт прав раздела. Отказ — пустой 404 (стелс, как у D1VPerimeter): отличать «нет доступа» от
    /// «нет такого пути» незачем, а разница в ответах помогала бы перебирать чужие айди.
    /// </summary>
    bool LiveDenied(string feature) => !Perms.Allowed(requestInfo?.user_uid, feature);

    string LiveD1vKey()
    {
        if (Request.Query.TryGetValue("d1v", out var q) && q.Count > 0 && !string.IsNullOrEmpty(q[0]))
            return q[0];

        string cn = CoreInit.conf?.d1v?.cookieName;
        if (!string.IsNullOrEmpty(cn) && Request.Cookies.TryGetValue(cn, out string cv))
            return cv;

        return null;
    }

    // Статус склейки: клиент опрашивает его, пока не появится первый готовый кусок, и показывает
    // «готовлю запись». Он же запускает ремукс — поэтому первый вызов и есть «пинок».
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/day")]
    async public Task<ActionResult> LiveDay(int camera, string date = null)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        if (camera <= 0)
            return LiveErr("Не указана камера");

        var tz = LiveTz();
        if (!TryLiveDay(date, tz, out var day))
            return LiveErr("Неверная дата");

        var ct = HttpContext.RequestAborted;
        var today = LiveToday(tz);

        LiveDayBuild build;
        string name = null;
        try
        {
            foreach (var c in await LiveCameraList(ct).ConfigureAwait(false))
            {
                if (c.Key == camera) { name = c.Value; break; }
            }
            build = await LiveBuildDay(camera, day, tz, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] day: {Error}", ex.Message);
            return LiveErr("Видеорегистратор недоступен");
        }

        if (build == null)
            return LiveJsonOut(new JObject { ["date"] = LiveDayKey(day), ["total"] = 0, ["ready"] = 0, ["empty"] = true });

        return LiveJsonOut(new JObject
        {
            ["date"] = LiveDayKey(day),
            ["label"] = LiveDayLabel(day, today),
            ["camera"] = new JObject { ["id"] = camera, ["name"] = name ?? ("Камера " + camera) },
            ["path"] = $"/qdl/live/day/{camera}/{LiveDayKey(day)}/stream.m3u8",
            ["ready"] = build.ready,
            ["total"] = build.total,
            ["complete"] = build.complete,
            ["seconds"] = (int)Math.Round(build.seconds)
        });
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/live/day/{camera:int}/{date}/stream.m3u8")]
    async public Task<ActionResult> LiveDayPlaylist(int camera, string date)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        var tz = LiveTz();
        if (camera <= 0 || string.IsNullOrEmpty(date) || !TryLiveDay(date, tz, out var day))
            return BadRequest();

        var ct = HttpContext.RequestAborted;

        LiveDayBuild build;
        try
        {
            build = await LiveBuildDay(camera, day, tz, ct).ConfigureAwait(false);

            // Первый кусок ещё ремуксится: ждём его тут, а не отдаём пустой плейлист (плеер на
            // пустом плейлисте сразу вываливается с ошибкой). Ремукс идёт много быстрее реального
            // времени, так что это секунды.
            // build == null — записей за день нет вовсе: ждать нечего, сразу 404 (иначе 30 холостых
            // секунд и полсотни лишних запросов к регистратору).
            for (int i = 0; i < 30 && build != null && build.ready == 0 && !build.complete && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                build = await LiveBuildDay(camera, day, tz, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] day playlist: {Error}", ex.Message);
            return StatusCode(502);
        }

        if (build == null || build.ready == 0)
            return NotFound();

        SetHeadersNoCache();
        return ContentTo(LiveSignDay(build.playlist), "application/vnd.apple.mpegurl");
    }

    static readonly Regex _liveSegRx = new Regex(@"\Aseg_\d{1,6}\.ts\z", RegexOptions.Compiled);

    /// <param name="o">
    /// Смещение куска в сутках, секунды. Есть — метки сегмента приводятся к сквозному времени дня
    /// (LiveTs). Нет (-1) — старый путь байт-в-байт: так помечены швы, для которых базу PTS достать
    /// не вышло, и там в плейлисте честно стоит EXT-X-DISCONTINUITY.
    /// </param>
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/seg/{rec:int}/{file}")]
    async public Task<ActionResult> LiveSegment(int rec, string file, double o = -1)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        if (rec <= 0 || file == null || !_liveSegRx.IsMatch(file))
            return BadRequest();

        string path = $"/hls/_vod/{rec}/{file}";

        // Санация o: значение приходит из URL. Больше суток — заведомо не наш плейлист;
        // сдвигать не станем, отдадим как есть (испортить этим можно только свой же таймлайн,
        // но молчаливо принимать мусор незачем).
        if (o < 0 || o > 172800 || double.IsNaN(o))
            return await LiveProxy(path, passRange: true, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        return await LiveSegmentShifted(path, rec, o).ConfigureAwait(false);
    }

    /// <summary>
    /// Сегмент со сдвинутыми метками — кирпич «сквозного таймлайна суток».
    /// </summary>
    /// <remarks>
    /// ⚠️ Range здесь НЕ форвардим и не объявляем: тело переписывается целиком, и байтовые диапазоны
    /// исходника к нему уже не относятся (отдать по чужому Range кусок переписанного файла — верный
    /// способ получить рассыпающееся видео). Плееры TS-сегменты по Range и не берут, качают целиком
    /// (~3 МБ), поэтому терять тут нечего.
    /// ⚠️ Буферизуем ЦЕЛИКОМ, а не потоково: метка может лежать на границе пакетов, а разбирать TS
    /// по кускам ради 3 МБ — сложность без выигрыша.
    /// </remarks>
    async Task<ActionResult> LiveSegmentShifted(string path, int rec, double offsetSec)
    {
        var ct = HttpContext.RequestAborted;

        long? basePts;
        byte[] body;
        try
        {
            basePts = await LiveRecPtsBase(rec, ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            using var resp = await _liveMedia.GetAsync(LiveBase() + path, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return StatusCode((int)resp.StatusCode);

            body = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();   // ушёл клиент / перемотка оборвала запрос
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] seg {Path}: {Error}", path, ex.Message);
            return StatusCode(502);
        }

        // База не достаётся (запись битая, ремукс снесли) — отдаём байт-в-байт: пусть таймлайн на
        // этом куске поедет, но видео у зрителя не пропадёт.
        if (basePts != null)
            LiveTs.Shift(body, LiveTs.Delta(LiveTs.Ticks(offsetSec), basePts.Value));

        Response.Headers["Accept-Ranges"] = "none";
        // Кэшировать у клиента можно и нужно (URL содержит и запись, и смещение — байты по нему
        // неизменны), но только приватно: в URL стоит айди устройства.
        Response.Headers["Cache-Control"] = "private, max-age=3600";

        return File(body, "video/mp2t");
    }

    #endregion

    #region /qdl/live/watch — D1versy Live: ЭФИР (живая сетка камер)

    // Живой просмотр у регистратора уже есть: POST /api/streams/{id}/start поднимает (или находит
    // общий) ffmpeg → rolling-HLS /hls/{id}/index.m3u8 (RTSP-камеры: seg_N.ts, таргет 2 с;
    // mac-рекордеры: fMP4 c EXT-X-MAP init.mp4 + seg_N.m4s — их эфир жив, только пока приложение
    // на маке пушит). Поток ОБЩИЙ на всех зрителей и не глушится по бездействию, stop мы не зовём
    // никогда (закрытие плеера у одного не должно ронять эфир другим). Здесь — тонкий прокси:
    // статус/старт + переписывание живого плейлиста на наши сегментные URL (c ключом периметра).

    /// <summary>Сетка: все камеры + кто сейчас в эфире.</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/watch")]
    async public Task<ActionResult> LiveWatch()
    {
        if (LiveDenied(Perms.FeatureLive)) return NotFound();

        var ct = HttpContext.RequestAborted;

        List<LiveCam> cams;
        JObject[] statuses;
        try
        {
            var all = await LiveCameraListFull(ct).ConfigureAwait(false);
            if (all.Count == 0)
                return LiveErr("Регистратор не отдал список камер");

            // Ровно правило оригинального Live View (isCameraVisibleOnLiveView): mac-рекордер
            // существует, только пока приложение на маке пушит, и его плитка «не в эфире»
            // не оживёт никогда — это были две мёртвые клетки из шести.
            // 🔴 RTSP-камеры не прячем НИКОГДА, даже при ready:false: у оригинала они висят
            // с «Connecting…», и это честнее исчезающей плитки.
            cams = all.Where(LiveWatchVisible).ToList();
            if (cams.Count == 0)
                return LiveJsonOut(new JObject { ["cameras"] = new JArray() });

            statuses = await Task.WhenAll(cams.Select(async c =>
            {
                try { return await LiveApiJson($"/api/streams/{c.id}/status", ct).ConfigureAwait(false) as JObject; }
                catch (OperationCanceledException) { throw; }
                catch { return null; }   // статус одной камеры упал → она «не в эфире», сетка живёт
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] watch: {Error}", ex.Message);
            return LiveErr("Регистратор недоступен");
        }

        var items = new JArray();
        for (int i = 0; i < cams.Count; i++)
        {
            var st = statuses[i];
            bool ready = st != null && ((bool?)st["ready"] ?? false);
            items.Add(new JObject
            {
                ["id"] = cams[i].id,
                ["name"] = cams[i].name,
                ["live"] = ready,
                ["running"] = st != null && ((bool?)st["is_running"] ?? false),
                ["upload"] = cams[i].IsUpload,
                // Готовый путь плейлиста для тех, кто уже в эфире: ffmpeg на регистраторе крутит
                // потоки 24/7, поэтому клиенту незачем дёргать /watch/start на каждую плитку.
                ["path"] = ready ? $"/qdl/live/watch/hls/{cams[i].id}/index.m3u8" : null
            });
        }

        // эфирные — первыми
        var sorted = new JArray(items.OrderByDescending(x => (bool)x["live"]).ThenBy(x => (int)x["id"]));
        return LiveJsonOut(new JObject { ["cameras"] = sorted });
    }

    /// <summary>Старт эфира камеры (идемпотентно) + статус. Клиент опрашивает до ready.</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/watch/start")]
    async public Task<ActionResult> LiveWatchStart(int camera)
    {
        if (LiveDenied(Perms.FeatureLive)) return NotFound();

        if (camera <= 0)
            return LiveErr("Не указана камера");

        var ct = HttpContext.RequestAborted;
        try
        {
            JObject st;
            await _liveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var resp = await _liveApi.PostAsync(LiveBase() + $"/api/streams/{camera}/start", null, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return LiveErr(resp.StatusCode == System.Net.HttpStatusCode.NotFound ? "Камера не найдена" : "Регистратор не смог запустить эфир");
                st = JObject.Parse(body);
            }
            finally { _liveGate.Release(); }

            bool ready = (bool?)st["ready"] ?? false;
            bool running = (bool?)st["is_running"] ?? false;

            // Сырой error регистратора — текст Python-исключения (пути, errno): клиенту его не
            // показываем (Noty рендерит как есть), в лог — целиком.
            string rawErr = (string)st["error"];
            if (!string.IsNullOrEmpty(rawErr))
                Serilog.Log.Warning("[qdl/live] camera {Camera} stream error: {Err}", camera, rawErr);

            return LiveJsonOut(new JObject
            {
                ["camera"] = camera,
                ["running"] = running,
                ["ready"] = ready,
                ["path"] = ready ? $"/qdl/live/watch/hls/{camera}/index.m3u8" : null,
                // у mac-рекордера running=false означает «приложение сейчас не пушит» — это не ошибка
                ["error"] = string.IsNullOrEmpty(rawErr) ? null : "Камера не отвечает"
            });
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] watch start: {Error}", ex.Message);
            return LiveErr("Регистратор недоступен");
        }
    }

    /// <summary>Живой плейлист камеры: сегментные URI → наши, с ключом периметра. Не кэшируется (rolling).</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/watch/hls/{camera:int}/index.m3u8")]
    async public Task<ActionResult> LiveWatchPlaylist(int camera)
    {
        if (LiveDenied(Perms.FeatureLive)) return NotFound();

        if (camera <= 0)
            return BadRequest();

        var ct = HttpContext.RequestAborted;
        string body = null;
        try
        {
            // Watchdog регистратора при подвисшем ffmpeg рестартует его, СНАЧАЛА удалив плейлист —
            // окно «плейлиста нет» ~4-8 с. Немедленный 404 уронил бы зрителя, поэтому коротко
            // переспрашиваем: для плеера это просто медленный ответ, эфир не рвётся.
            for (int i = 0; ; i++)
            {
                await _liveGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var resp = await _liveApi.GetAsync(LiveBase() + $"/hls/{camera}/index.m3u8", ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        break;
                    }
                }
                finally { _liveGate.Release(); }

                if (i >= 16)
                    return NotFound();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] watch playlist: {Error}", ex.Message);
            return StatusCode(502);
        }

        string sign = LiveSegQuery();
        string prefix = $"/qdl/live/watch/seg/{camera}/";

        var sb = new StringBuilder(body.Length + 512);
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) { sb.Append('\n'); continue; }

            if (line[0] == '#')
            {
                // fMP4: init-сегмент едет отдельной строкой-тегом — переписываем и его
                var m = Regex.Match(line, "^(#EXT-X-MAP:URI=\")([^\"]+)(\".*)$");
                sb.Append(m.Success ? m.Groups[1].Value + prefix + LiveSegName(m.Groups[2].Value) + sign + m.Groups[3].Value : line);
            }
            else
                sb.Append(prefix).Append(LiveSegName(line)).Append(sign);

            sb.Append('\n');
        }

        SetHeadersNoCache();
        return ContentTo(sb.ToString(), "application/vnd.apple.mpegurl");
    }

    static readonly Regex _liveWatchSegRx = new Regex(@"\A(seg_\d{1,10}\.(ts|m4s)|init\.mp4)\z", RegexOptions.Compiled);

    [HttpGet, AllowAnonymous]
    [Route("qdl/live/watch/seg/{camera:int}/{file}")]
    async public Task<ActionResult> LiveWatchSegment(int camera, string file)
    {
        if (LiveDenied(Perms.FeatureLive)) return NotFound();

        if (camera <= 0 || file == null || !_liveWatchSegRx.IsMatch(file))
            return BadRequest();

        return await LiveProxy($"/hls/{camera}/{file}", passRange: true, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    /// <summary>Превью тайла: кадр эфира (thumb.jpg), фолбек — постер последней записи камеры.</summary>
    // Выбор цели решаем ДО проксирования (дешёвый HEAD-пробник): LiveProxy пишет прямо в Response,
    // и после него переключиться на фолбек уже нельзя (тело начато).
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/watch/thumb")]
    async public Task<ActionResult> LiveWatchThumb(int camera)
    {
        if (LiveDenied(Perms.FeatureLive)) return NotFound();

        if (camera <= 0)
            return BadRequest();

        var ct = HttpContext.RequestAborted;
        try
        {
            bool hasLiveThumb;
            await _liveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Head, LiveBase() + $"/thumb/{camera}/thumb.jpg");
                using var resp = await _liveApi.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                hasLiveThumb = resp.IsSuccessStatusCode;
            }
            finally { _liveGate.Release(); }

            if (hasLiveThumb)
                return await LiveProxy($"/thumb/{camera}/thumb.jpg", passRange: false, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            // mac-рекордеры кадра эфира не имеют → постер самой свежей записи камеры
            if (await LiveApiJson($"/api/recordings/?camera_id={camera}&limit=1", ct).ConfigureAwait(false) is JArray arr && arr.Count > 0)
            {
                int rec = (int?)arr[0]["id"] ?? 0;
                if (rec > 0)
                    return await LiveProxy($"/api/recordings/{rec}/thumbnail", passRange: false, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Debug("[qdl/live] watch thumb: {Error}", ex.Message);
        }

        return NotFound();
    }

    #endregion

    #region /qdl/live/stream, /qdl/live/thumb — байтовый прокси регистратора

    // Записи регистратора — готовые mp4 с moov в начале (ingest_service ремуксит с +faststart
    // и только ПОСЛЕ этого заводит строку в БД), поэтому играем их напрямую: Range работает,
    // перемотка мгновенная, HLS-обвязка не нужна ни одному клиенту.
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/stream")]
    async public Task<ActionResult> LiveStream(int id)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        if (id <= 0)
            return BadRequest();

        return await LiveProxy($"/api/recordings/{id}/stream", passRange: true, timeout: null).ConfigureAwait(false);
    }

    // Кадр-превью. Первый запрос может подождать ffmpeg на стороне регистратора (дальше кэш).
    [HttpGet, AllowAnonymous]
    [Route("qdl/live/thumb")]
    async public Task<ActionResult> LiveThumb(int id)
    {
        if (LiveDenied(Perms.FeatureRec)) return NotFound();

        if (id <= 0)
            return BadRequest();

        return await LiveProxy($"/api/recordings/{id}/thumbnail", passRange: false, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    static readonly string[] _liveFwdReq = { "Range", "If-Range", "If-None-Match", "If-Modified-Since" };
    static readonly string[] _liveFwdResp = { "Content-Type", "Content-Length", "Content-Encoding", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag", "Cache-Control" };

    async Task<ActionResult> LiveProxy(string path, bool passRange, TimeSpan? timeout)
    {
        var ct = HttpContext.RequestAborted;
        using var cts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        cts?.CancelAfter(timeout.Value);
        var token = cts?.Token ?? ct;

        bool head = string.Equals(Request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
        using var req = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, LiveBase() + path);

        if (passRange)
        {
            foreach (string h in _liveFwdReq)
            {
                if (Request.Headers.TryGetValue(h, out var v))
                    req.Headers.TryAddWithoutValidation(h, v.ToArray());
            }
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _liveMedia.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();   // ушёл клиент
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] proxy {Path}: {Error}", path, ex.Message);
            return StatusCode(502);
        }

        using (resp)
        {
            Response.StatusCode = (int)resp.StatusCode;

            foreach (string h in _liveFwdResp)
            {
                if (resp.Content.Headers.TryGetValues(h, out var cv))
                    Response.Headers[h] = cv.ToArray();
                else if (resp.Headers.TryGetValues(h, out var rv))
                    Response.Headers[h] = rv.ToArray();
            }

            if (!Response.Headers.ContainsKey("Accept-Ranges") && passRange)
                Response.Headers["Accept-Ranges"] = "bytes";

            // HEAD — тело не пишем (Content-Length при этом законен). В остальных случаях тело
            // копируем ВСЕГДА, включая 404/416: иначе скопированный Content-Length не сойдётся
            // с нулём записанных байт и Kestrel превратит честный 404 в 500.
            if (head)
                return new EmptyResult();

            try
            {
                await resp.Content.CopyToAsync(Response.Body, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Перемотка/закрытие плеера рвёт поток на середине — это норма, не ошибка.
                Serilog.Log.Debug("[qdl/live] proxy body {Path}: {Error}", path, ex.Message);
            }
        }

        return new EmptyResult();
    }

    #endregion
}
