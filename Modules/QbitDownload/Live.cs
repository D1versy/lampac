using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

    async Task<JToken> LiveApiJson(string path, CancellationToken ct)
    {
        using var resp = await _liveApi.GetAsync(LiveBase() + path, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        return JToken.Parse(body);
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

    /// <summary>id → имя. Наружу отдаём ТОЛЬКО это: ip/логины камер клиенту не нужны.</summary>
    async Task<List<KeyValuePair<int, string>>> LiveCameraList(CancellationToken ct)
    {
        var list = new List<KeyValuePair<int, string>>();
        if (await LiveApiJson("/api/cameras/", ct).ConfigureAwait(false) is not JArray arr)
            return list;

        foreach (var c in arr)
        {
            int id = (int?)c["id"] ?? 0;
            if (id <= 0)
                continue;

            string name = ((string)c["name"] ?? "").Trim();
            list.Add(new KeyValuePair<int, string>(id, name.Length > 0 ? name : "Камера " + id));
        }
        return list;
    }

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

    #region /qdl/live/stream, /qdl/live/thumb — байтовый прокси регистратора

    // Записи регистратора — готовые mp4 с moov в начале (ingest_service ремуксит с +faststart
    // и только ПОСЛЕ этого заводит строку в БД), поэтому играем их напрямую: Range работает,
    // перемотка мгновенная, HLS-обвязка не нужна ни одному клиенту.
    [HttpGet, HttpHead, AllowAnonymous]
    [Route("qdl/live/stream")]
    async public Task<ActionResult> LiveStream(int id)
    {
        if (id <= 0)
            return BadRequest();

        return await LiveProxy($"/api/recordings/{id}/stream", passRange: true, timeout: null).ConfigureAwait(false);
    }

    // Кадр-превью. Первый запрос может подождать ffmpeg на стороне регистратора (дальше кэш).
    [HttpGet, HttpHead, AllowAnonymous]
    [Route("qdl/live/thumb")]
    async public Task<ActionResult> LiveThumb(int id)
    {
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
