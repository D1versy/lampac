using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Buffers;
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
// /qdl/jut/stream — единственный URL, который видит плеер.
//
// Отдать клиенту прямую ссылку на CDN НЕЛЬЗЯ физически: hash в ней криптографически
// связан с User-Agent, которым была запрошена HTML-страница, а у VLC/ExoPlayer/браузера
// свой UA → 403. Поэтому байты идут через нас.
//
// Образец — LiveProxy (Live.cs:1079). Четыре отличия, каждое выстрадано:
//  1. На CDN шлём ТОЛЬКО User-Agent + Range. Ни кук (dle_password на чужом хосте = утечка),
//     ни Referer (не нужен — проверено пробами).
//  2. If-Range / If-None-Match НЕ форвардим: перевыпуск даёт другой поддомен rNNNNNN,
//     ETag узла отличается → If-Range со старым ETag вернёт 200 с ПОЛНЫМ телом вместо 206,
//     и обычный seek превратится в перекачку 489 МиБ с нуля.
//  3. Content-Encoding НЕ форвардим обратно: CDN отдаёт mp4 без сжатия, а наш handler
//     декомпрессирует — проброшенный заголовок соврал бы клиенту.
//  4. Обрыв ТЕЛА в середине переоткрываем сами (см. ниже) — плеер держит одно соединение
//     часами, переживая и TTL токена, и ротацию поддомена.
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    // Range форвардим, валидаторы — нет (см. отличие 2).
    static readonly string[] _jutFwdResp =
        { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag" };

    static readonly Regex _jutRangeRx = new(@"bytes=(?<from>\d*)-(?<to>\d*)", RegexOptions.Compiled);
    static readonly Regex _jutContentRangeRx = new(@"bytes\s+(?<from>\d+)-(?<to>\d+)/(?<len>\d+|\*)", RegexOptions.Compiled);

    static SemaphoreSlim _jutStreamGate;
    static readonly object _jutStreamGateLock = new();

    /// <summary>
    /// Гейт только на СТАРТ запроса — отпускается сразу после получения заголовков.
    /// В Live.cs гейт держится на весь проксируемый ответ, но там записи короткие; здесь
    /// шесть зрителей залочили бы седьмого на весь фильм.
    /// </summary>
    static SemaphoreSlim JutStreamGate()
    {
        if (_jutStreamGate != null) return _jutStreamGate;
        lock (_jutStreamGateLock)
            return _jutStreamGate ??= new SemaphoreSlim(Math.Max(1, ModInit.conf?.jutStreamConcurrent ?? 6));
    }

    [HttpGet, HttpHead, AllowAnonymous]
    [Route("qdl/jut/stream")]
    async public Task<ActionResult> JutStream(string t)
    {
        if (!JutOn) return NotFound();

        var ct = HttpContext.RequestAborted;
        bool head = HttpMethods.IsHead(Request.Method);

        var link = await JutNet.EnsureLink(t, force: false, ct);
        if (link == null) return NotFound();                       // подделанный/мусорный токен
        if (link.error != null)
            return StatusCode(link.error == "NOT_AUTHORIZED" ? 403
                            : link.error == "NOT_FOUND" ? 404 : 502);

        // Разметка опенинга — в кеш (переживёт TTL ссылки), а сам факт просмотра — в историю.
        // HEAD-пробы плеера просмотром не считаем.
        JutSegStore(link);
        if (!head) JutHistoryTouchWatch(link.slug);

        string clientRange = Request.Headers.TryGetValue("Range", out var rv) ? rv.ToString() : null;

        HttpResponseMessage resp = null;
        var gate = JutStreamGate();
        await gate.WaitAsync(ct);
        bool released = false;
        try
        {
            // Ретрай РОВНО ОДИН: 403/410 = токен протух или сменился поддомен CDN.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                resp = await JutSendCdn(link, clientRange, head, ct);
                if (resp == null) return StatusCode(502);

                if ((resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Gone)
                    && attempt == 0)
                {
                    resp.Dispose();
                    resp = null;
                    // Перевыпуск идёт ТЕМ ЖЕ выходом (пиннинг внутри EnsureLink)
                    link = await JutNet.EnsureLink(t, force: true, ct);
                    if (link == null || link.error != null) return StatusCode(502);
                    JutNet.Log("stream", "перевыпуск ссылки " + link.slug + " " + link.season + "x" + link.ep);
                    continue;
                }
                break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception ex) { JutNet.Log("stream", ex.Message); return StatusCode(502); }
        finally
        {
            if (!released) { gate.Release(); released = true; }
        }

        if (resp == null) return StatusCode(502);

        using (resp)
        {
            Response.StatusCode = (int)resp.StatusCode;
            foreach (string h in _jutFwdResp)
            {
                if (resp.Content.Headers.TryGetValues(h, out var cv)) Response.Headers[h] = cv.ToArray();
                else if (resp.Headers.TryGetValues(h, out var hv)) Response.Headers[h] = hv.ToArray();
            }
            if (!Response.Headers.ContainsKey("Accept-Ranges"))
                Response.Headers["Accept-Ranges"] = "bytes";
            Response.Headers["Cache-Control"] = "no-store";

            // HEAD — тело не пишем (Content-Length при этом законен).
            if (head) return new EmptyResult();

            // Абсолютное смещение начала тела: нужно, чтобы переоткрыть с правильного места.
            // Полная длина оттуда же — по ней считается порог прогрева следующей серии.
            long absStart = 0, totalLen = 0;
            var crv = resp.Content.Headers.TryGetValues("Content-Range", out var cr) ? cr.FirstOrDefault() : null;
            if (crv != null)
            {
                var m = _jutContentRangeRx.Match(crv);
                if (m.Success)
                {
                    long.TryParse(m.Groups["from"].Value, out absStart);
                    long.TryParse(m.Groups["len"].Value, out totalLen);   // "*" → 0, порог не сработает
                }
            }
            else if (clientRange != null)
            {
                var m = _jutRangeRx.Match(clientRange);
                if (m.Success && m.Groups["from"].Value.Length > 0)
                    long.TryParse(m.Groups["from"].Value, out absStart);
            }
            if (totalLen == 0) totalLen = resp.Content.Headers.ContentLength ?? 0;

            await JutPumpBody(resp, link, t, absStart, totalLen, ct);
        }

        return new EmptyResult();
    }

    /// <summary>Запрос к CDN. ⚠️ Тот же UA, что забрал страницу — иначе гарантированный 403.</summary>
    async Task<HttpResponseMessage> JutSendCdn(JutLink link, string range, bool head, CancellationToken ct)
    {
        var req = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, link.url);
        req.Headers.TryAddWithoutValidation("User-Agent", JutNet.Ua);
        if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);
        // Ни Cookie, ни Referer, ни If-Range/If-None-Match — см. шапку файла.
        return await JutNet.Media(link.exitId)
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// Перекачка тела с переоткрытием. Плеер обычно открывает ОДНО соединение и читает его
    /// со скоростью воспроизведения — оно живёт часами и переживает и TTL токена (240 с),
    /// и ротацию поддомена. Обрыв апстрима на середине здесь — штатная ситуация, а не ошибка:
    /// статус и заголовки клиенту уже отправлены, поэтому «ретрай запроса» невозможен —
    /// возможно только молча продолжить дописывать байты с нужного офсета.
    /// </summary>
    async Task JutPumpBody(HttpResponseMessage first, JutLink link, string token,
                           long absStart, long totalLen, CancellationToken ct)
    {
        const int MaxReopen = 3;
        byte[] buf = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long written = 0;
        int reopened = 0;
        var current = first;
        bool ownsCurrent = false;   // first диспозится вызывающим

        // 🔥 Прогрев следующей серии вешаем на ПРОГРЕСС, а не на старт: ссылка живёт 240 с,
        // и прогретая в начале серии протухла бы задолго до автоперехода.
        int pct = Math.Clamp(ModInit.conf?.jutPrewarmAtPercent ?? 60, 10, 95);
        long warmAt = totalLen > 0 ? totalLen / 100 * pct : 0;
        bool warmFired = false;

        try
        {
            while (true)
            {
                try
                {
                    using var up = await current.Content.ReadAsStreamAsync(ct);
                    int n;
                    while ((n = await up.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        await Response.Body.WriteAsync(buf, 0, n, ct);
                        written += n;

                        if (!warmFired && warmAt > 0 && absStart + written >= warmAt)
                        {
                            warmFired = true;
                            JutPrewarmNext(link);
                        }
                    }
                    return;                                  // дочитали до конца
                }
                catch (OperationCanceledException) { return; }   // клиент ушёл — это норма
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) return;
                    if (reopened >= MaxReopen)
                    {
                        JutNet.Log("stream", "тело оборвалось окончательно после " + written + " Б: " + ex.Message);
                        return;
                    }
                    reopened++;
                    JutNet.Log("stream", "обрыв тела на " + (absStart + written) + " Б, переоткрываю (" + reopened + "/" + MaxReopen + ")");
                }

                // Переоткрываем ровно с того места, где оборвались.
                var fresh = await JutNet.EnsureLink(token, force: reopened > 1, ct);
                if (fresh == null || fresh.error != null) return;
                link = fresh;

                HttpResponseMessage next;
                try
                {
                    next = await JutSendCdn(link, "bytes=" + (absStart + written) + "-", head: false, ct);
                }
                catch { return; }

                if (next == null) return;
                if ((int)next.StatusCode is not (200 or 206))
                {
                    next.Dispose();
                    return;
                }

                if (ownsCurrent) current.Dispose();
                current = next;
                ownsCurrent = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            if (ownsCurrent) current.Dispose();
        }
    }
}
