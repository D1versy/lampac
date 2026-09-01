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

// ── D1VERSY LIVE → DETECTION — скриншоты срабатываний детектора ──────────────────────────────────
//
// Регистратор (C:\IPCamLive) кладёт каждое срабатывание в таблицу событий и рисует к нему кадр
// с рамками: `/api/detection/events` + `/api/detection/thumbnail/{id}`. Наружу он не проброшен,
// поэтому здесь тот же тонкий прокси, что и у остального Live: каталог + байты картинки.
//
// ⚠️ Времена событий — НАИВНЫЙ UTC, как и у записей (шапка Live.cs): контейнер регистратора поднят
// с TZ=UTC. Всё, что уходит клиенту, переводится в локальную зону, а «локальный день» разворачивается
// в UTC-окно, задевающее ДВЕ его даты.
//
// ⚠️ Лента густая: замер 01.09.2026 — 500 событий за 2 ч 50 мин (~170 в час, почти все `human`:
// кадр пишется на КАЖДОМ тике детектора, пока человек в кадре, то есть раз в 2 с). За сутки это
// тысячи карточек — отсюда обязательные фильтры по камере/типу/дню и курсорная подгрузка.
//
// ⚠️ Оригиналы кадров тяжёлые (замер: ~340 КБ, 720p JPEG с нарисованными рамками). В гриде их
// десятки, поэтому плиткам отдаём уменьшёнку в WebP, а полноэкранному просмотру — оригинал
// байт в байт: экономим на контейнере, а не на качестве.
public partial class QbitController
{
    #region разбор события

    /// <summary>Одно срабатывание детектора в нашем (уже нормализованном) виде.</summary>
    sealed class LiveEvt
    {
        public int id;
        public int camera;
        public string type;        // motion | human
        public double confidence;  // 0..1, у motion обычно пусто
        public DateTime startUtc;
        public int recording;      // 0 = событие не привязано к записи
        public bool hasThumb;
    }

    static LiveEvt ParseLiveEvt(JToken t)
    {
        if (t == null || t.Type != JTokenType.Object)
            return null;

        int id = (int?)t["id"] ?? 0;
        if (id <= 0 || !TryLiveUtc((string)t["timestamp"], out var utc))
            return null;

        string kind = ((string)t["event_type"] ?? "").Trim().ToLowerInvariant();

        return new LiveEvt
        {
            id = id,
            camera = (int?)t["camera_id"] ?? 0,
            type = kind == "human" ? "human" : "motion",
            confidence = (double?)t["confidence"] ?? 0,
            startUtc = utc,
            recording = (int?)t["recording_id"] ?? 0,
            hasThumb = !string.IsNullOrEmpty((string)t["thumbnail_path"])
        };
    }

    /// <summary>
    /// Страница событий. Курсор — id события (у регистратора это `before_id`), он же возвращается
    /// наружу отдельным полем.
    /// 🔴 Курсор — минимальный id ОТДАННОГО события, а не сырого ответа. В режиме дня мы спрашиваем
    /// ДВЕ UTC-даты и сливаем их: у «вчерашней» ленты id заведомо меньше, и курсор по сырому
    /// минимуму перепрыгнул бы через тысячи ещё не показанных событий (замер на боевых данных:
    /// страница 453934…453933, а сырой минимум 451618 — дыра в 2300 событий).
    /// 🔴 Но когда отдавать нечего (вся страница легла за окно локальных суток), брать НЕЧЕГО —
    /// тогда и только тогда курсором становится сырой минимум: всё, что было в ответе, уже
    /// рассмотрено и отвергнуто, перешагнуть через него безопасно. Без этой ветки лента вставала бы.
    /// </summary>
    async Task<(List<LiveEvt> items, bool hasNext, int cursor)> LiveDetectFetch(
        int before, int limit, int camera, string type, DateTime? day, TimeZoneInfo tz, CancellationToken ct)
    {
        string q = "?limit=" + limit;
        if (before > 0) q += "&before_id=" + before;
        if (camera > 0) q += "&camera_id=" + camera;
        if (type != null) q += "&event_type=" + type;

        var res = new List<LiveEvt>();
        var seen = new HashSet<int>();
        int rawMin = 0, raw = 0;
        bool full = false;

        void take(JArray arr, DateTime? from, DateTime? to)
        {
            if (arr == null)
                return;

            if (arr.Count >= limit)
                full = true;

            foreach (var t in arr)
            {
                raw++;
                int rawId = (int?)t["id"] ?? 0;
                if (rawId > 0 && (rawMin == 0 || rawId < rawMin))
                    rawMin = rawId;

                var e = ParseLiveEvt(t);
                if (e == null)
                    continue;
                if (from.HasValue && (e.startUtc < from.Value || e.startUtc >= to.Value))
                    continue;
                if (seen.Add(e.id))
                    res.Add(e);
            }
        }

        // Сплошная лента: один запрос, регистратор сам отдаёт свежие сверху.
        if (day == null)
        {
            take(await LiveApiJson("/api/detection/events" + q, ct).ConfigureAwait(false) as JArray, null, null);
        }
        else
        {
            // Локальные сутки задевают ДВЕ UTC-даты регистратора (его фильтр `date` режет UTC-сутки),
            // поэтому спрашиваем обе и режем окном сами — тот же приём, что в LiveDayRecs.
            var (from, to) = LiveDayWindow(day.Value, tz);
            for (var d = from.Date; d <= to.AddTicks(-1).Date; d = d.AddDays(1))
            {
                take(await LiveApiJson($"/api/detection/events{q}&date={LiveDayKey(d)}", ct).ConfigureAwait(false) as JArray,
                     from, to);
            }
        }

        res.Sort((a, b) => b.id.CompareTo(a.id));

        bool more = full || res.Count > limit;
        if (res.Count > limit)
            res = res.GetRange(0, limit);

        return (res, more && raw > 0, LiveDetectCursor(res, rawMin));
    }

    /// <summary>Курсор следующей страницы — по отданному хвосту; отдавать нечего → по сырому минимуму.</summary>
    static int LiveDetectCursor(List<LiveEvt> kept, int rawMin)
        => kept != null && kept.Count > 0 ? kept[kept.Count - 1].id : rawMin;

    #endregion

    #region /qdl/live/detect — лента срабатываний

    [HttpGet, AllowAnonymous]
    [Route("qdl/live/detect")]
    async public Task<ActionResult> LiveDetectList(int before = 0, int limit = 60, int camera = 0, string type = null, string date = null)
    {
        if (LiveDenied(Perms.FeatureLive)) return NotFound();

        var tz = LiveTz();
        if (!TryLiveDay(date, tz, out var day))
            return LiveErr("Неверная дата");

        bool byDay = !string.IsNullOrWhiteSpace(date);
        limit = Math.Clamp(limit, 1, 200);

        type = (type ?? "").Trim().ToLowerInvariant();
        if (type != "motion" && type != "human")
            type = null;

        var ct = HttpContext.RequestAborted;
        var today = LiveToday(tz);

        List<LiveCam> cams;
        List<LiveEvt> evts;
        bool hasNext;
        int cursor;
        try
        {
            cams = await LiveCameraListFull(ct).ConfigureAwait(false);
            (evts, hasNext, cursor) = await LiveDetectFetch(before, limit, camera, type, byDay ? day : (DateTime?)null, tz, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[qdl/live] detect: {Error}", ex.Message);
            return LiveErr("Регистратор недоступен");
        }

        var names = new Dictionary<int, string>();
        foreach (var c in cams)
            names[c.id] = c.name;

        var items = new JArray();
        foreach (var e in evts)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(e.startUtc, tz);
            items.Add(new JObject
            {
                ["id"] = e.id,
                ["camera"] = e.camera,
                ["cameraName"] = names.TryGetValue(e.camera, out string n) ? n : ("Камера " + e.camera),
                ["type"] = e.type,
                ["confidence"] = e.confidence > 0 ? (int)Math.Round(e.confidence * 100) : 0,
                ["time"] = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                ["day"] = LiveDayKey(local.Date),
                ["dayLabel"] = LiveDayLabel(local.Date, today),
                ["recording"] = e.recording > 0 ? e.recording : 0,
                ["thumb"] = e.hasThumb
            });
        }

        // Детекция у upload-камер выключена на регистраторе принудительно (его же
        // isCameraSelectableForDetection), поэтому в фильтр они не попадают.
        var picker = new JArray(cams.Where(c => !c.IsUpload)
                                    .Select(c => new JObject { ["id"] = c.id, ["name"] = c.name }));

        return LiveJsonOut(new JObject
        {
            ["items"] = items,
            ["hasNext"] = hasNext,
            ["cursor"] = cursor,
            ["date"] = byDay ? LiveDayKey(day) : null,
            ["label"] = byDay ? LiveDayLabel(day, today) : null,
            ["today"] = LiveDayKey(today),
            ["cameras"] = picker
        });
    }

    #endregion

    #region /qdl/live/detect/thumb — кадр события

    [HttpGet, AllowAnonymous]
    [Route("qdl/live/detect/thumb")]
    async public Task<ActionResult> LiveDetectThumb(int id, int w = 0)
    {
        if (LiveDenied(Perms.FeatureLive)) return NotFound();

        if (id <= 0)
            return BadRequest();

        // w не задан = полноэкранный просмотр: отдаём оригинал байт в байт.
        if (w <= 0)
            return await LiveProxy($"/api/detection/thumbnail/{id}", passRange: false, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        w = Math.Clamp(w, 160, 1920);
        var ct = HttpContext.RequestAborted;

        byte[] src;
        try
        {
            await _liveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var resp = await _liveApi.GetAsync(LiveBase() + $"/api/detection/thumbnail/{id}", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return NotFound();
                src = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            finally { _liveGate.Release(); }
        }
        catch (OperationCanceledException) { return new EmptyResult(); }
        catch (Exception ex)
        {
            Serilog.Log.Debug("[qdl/live] detect thumb: {Error}", ex.Message);
            return StatusCode(502);
        }

        byte[] small = LiveThumbResize(src, w);

        // Кадр события неизменен (файл пишется один раз при срабатывании) → у клиента он может
        // жить вечно. Сам регистратор отдаёт его с тем же immutable.
        Response.Headers["Cache-Control"] = "public, max-age=2592000, immutable";

        return small != null ? File(small, "image/webp") : File(src, "image/jpeg");
    }

    /// <summary>
    /// Уменьшёнка кадра детекции. null — «отдай оригинал».
    /// 🔥 Наружу не бросает никогда: сбой кодека обязан выродиться в исходную картинку, иначе
    /// грид молча зарастёт битыми плитками там, где раньше просто работал.
    /// </summary>
    static byte[] LiveThumbResize(byte[] src, int w)
    {
        if (src == null || src.Length == 0)
            return null;

        try
        {
            JutVipsEnsure();   // настройки NetVips на процесс — общие с апгрейдом постеров jut.su

            using var ms = new MemoryStream(src, writable: false);
            using var img = NetVips.Image.NewFromStream(ms, access: NetVips.Enums.Access.Sequential);
            if (img.Width <= 0 || img.Height <= 0)
                return null;
            if (img.Width <= w)
                return null;   // кадр и так мельче запрошенного — пережимать нечего

            using var outImg = img.ThumbnailImage(w, size: NetVips.Enums.Size.Down, crop: NetVips.Enums.Interesting.None);
            using var outMs = new MemoryStream();
            // keep, а НЕ strip: в NetVips 3.x параметра strip уже нет (см. JutPosterEncode).
            outImg.WebpsaveStream(outMs, q: 90, keep: NetVips.Enums.ForeignKeep.None);

            byte[] enc = outMs.ToArray();
            if (enc == null || enc.Length < 512 || enc.Length >= src.Length)
                return null;

            return enc;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug("[qdl/live] detect resize: {Error}", ex.Message);
            return null;
        }
    }

    #endregion
}
