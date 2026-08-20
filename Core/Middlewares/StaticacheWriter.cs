using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using Shared.Attributes;
using Shared.Models.AppConf;
using Shared.Models.Base;
using Shared.Services;
using Shared.Services.Pools;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Core.Middlewares;

public class StaticacheWriter
{
    private readonly RequestDelegate _next;

    public StaticacheWriter(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext httpContext)
    {
        var stc = httpContext.Features.Get<StaticacheFeature>();
        if (stc == null)
            return _next(httpContext);

        return WriterAsync(stc, httpContext);
    }

    async Task WriterAsync(StaticacheFeature stc, HttpContext httpContext)
    {
        using (var msm = new LazyMsm())
        {
            httpContext.Features.Set(msm);
            httpContext.Response.Headers["X-StatiCache-Status"] = "MISS";

            await _next(httpContext);

            bool isRedirect = httpContext.Response.StatusCode == StatusCodes.Status301MovedPermanently ||
                httpContext.Response.StatusCode == StatusCodes.Status302Found ||
                httpContext.Response.StatusCode == StatusCodes.Status307TemporaryRedirect ||
                httpContext.Response.StatusCode == StatusCodes.Status308PermanentRedirect;

            if (!isRedirect && !msm.IsEmpty && httpContext.Response.StatusCode != StatusCodes.Status206PartialContent)
            {
                var cacheEntry = httpContext.Features.Get<StatiCacheEntry>();

                #region Дожимаем httpContext
                var ex = cacheEntry?.ex ?? DateTimeOffset.Now.AddMinutes(stc.cacheMinutes);

                if (httpContext.Response.StatusCode != 200)
                    ex = DateTimeOffset.Now.AddMinutes(1);

                int contentLength = (int)(httpContext.Response?.ContentLength ?? 0);
                if (contentLength > 0 && contentLength != msm.Stream.Length)
                    ex = DateTimeOffset.Now.AddMinutes(1);

                string ext = "bin";
                bool isMedia = false;
                string contentType = httpContext.Response.ContentType;

                if (contentType != null)
                {
                    if (contentType.StartsWith("text/html"))
                        ext = "html";
                    else if (contentType.StartsWith("application/json"))
                        ext = "json";
                    else if (contentType.StartsWith("application/javascript"))
                        ext = "js";
                    else if (contentType.StartsWith("text/css"))
                        ext = "css";
                    else if (contentType.StartsWith("image/svg+xml"))
                        ext = "svg";
                    else if (contentType.StartsWith("image/png"))
                    {
                        ext = "png";
                        isMedia = true;
                    }
                    else if (contentType.StartsWith("image/jpeg"))
                    {
                        ext = "jpg";
                        isMedia = true;
                    }
                    else if (contentType.StartsWith("image/webp"))
                    {
                        ext = "webp";
                        isMedia = true;
                    }
                }

                if (isMedia && contentLength == 0)
                {
                    contentLength = (int)msm.Stream.Length;
                    httpContext.Response.ContentLength = contentLength;
                }

                // immutable-эндпоинты (versioned-URL ?v=) — вечный клиентский кэш и на MISS-пути тоже;
                // js/html идут chunked (contentLength=0), общий путь ниже их не покрывает.
                // Гейт по наличию ?v: прямой запрос БЕЗ версии вечно кэшировать нельзя. Исключение
                // (qdl 2.16): attr без queryKeys = content-addressed URL (постеры) — immutable всегда.
                var stAttr = httpContext.GetEndpoint()?.Metadata?.GetMetadata<StaticacheAttribute>();
                if (stAttr?.immutable == true && httpContext.Response.StatusCode == 200 && (stAttr.queryKeys == null || httpContext.Request.Query.ContainsKey("v")))
                    httpContext.Response.Headers[HeaderNames.CacheControl] = "public,max-age=31536000,immutable";
                // qdl 2.53: revalidate уже поставил no-cache в Staticache до ветвления HIT/MISS —
                // не перебиваем его вечным max-age.
                //
                // 🔴 If-None-Match проверяем и ЗДЕСЬ, а не только на HIT-пути. Кеш этих роутов живёт
                // 10–20 минут, а клиент стартует раз в сутки — то есть в проде почти каждый старт
                // приходит именно на MISS. Без этой ветки ревалидация давала бы 304 только в
                // редком окне «кто-то дёрнул ручку минуту назад», а обычный старт по-прежнему качал
                // бы тело целиком. Тег считаем от буфера ответа — файл ниже пишется из него же,
                // поэтому HIT потом посчитает ровно такой же (Staticache.BodyEtag/FileEtag).
                else if (stAttr?.revalidate == true) { }
                else if (contentLength > 0)
                    httpContext.Response.Headers[HeaderNames.CacheControl] = "public,max-age=86400,immutable";
                #endregion

                string revalidateEtag = null;
                bool notModified = false;

                if (stAttr?.revalidate == true && httpContext.Response.StatusCode == 200)
                {
                    revalidateEtag = Staticache.BodyEtag(msm.Stream.GetReadOnlySequence());
                    httpContext.Response.Headers[HeaderNames.ETag] = revalidateEtag;

                    if (Staticache.IfNoneMatchHit(httpContext.Request.Headers[HeaderNames.IfNoneMatch], revalidateEtag))
                    {
                        notModified = true;
                        httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                        httpContext.Response.Headers[HeaderNames.Vary] = "Accept-Encoding";
                        httpContext.Response.ContentLength = null;
                    }
                }

                #region Сбрасываем поток клиенту
                msm.Stream.Position = 0;

                if (notModified)
                {
                    // тело клиенту не нужно, но на диск запись ниже всё равно кладём:
                    // работа уже сделана, а следующему клиенту она пригодится
                    await httpContext.Response.CompleteAsync();
                }
                else if (contentLength > 0)
                {
                    /// один overhead "write && flush" при наличии content-length
                    await msm.Stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
                }
                else
                {
                    /// что-бы не было два overhead "write > flush", пишем через BodyWriter
                    var bodyWriter = httpContext.Response.BodyWriter;

                    do
                    {
                        int chunkSize = PoolInvk.ChunkSizeBodyWriter((int)(msm.Stream.Length - msm.Stream.Position));
                        Span<byte> destination = bodyWriter.GetSpan(chunkSize);

                        int bytesRead = msm.Stream.Read(destination);
                        if (bytesRead == 0)
                            break;

                        bodyWriter.Advance(bytesRead);
                    }
                    while (msm.Stream.Position < msm.Stream.Length);

                    /// write && flush в один overhead
                    await httpContext.Response.CompleteAsync();
                }
                #endregion

                #region Сохраняем на диск
                if ((cacheEntry != null && cacheEntry.saveCache == false) || DateTimeOffset.Now > ex)
                    return;

                string cachekey = stc.cachekey;
                var sm = new SemaphorManager(cachekey, TimeSpan.FromSeconds(5));

                try
                {
                    bool _acquired = await sm.WaitAsync();
                    if (!_acquired)
                        return;

                    long exTicks = ex.ToUnixTimeMilliseconds();
                    string cachefile = Staticache.GetFilePath(cachekey, exTicks, contentLength, ext);

                    using (SafeFileHandle handle = File.OpenHandle(cachefile,
                        FileMode.Create, FileAccess.Write, FileShare.None,
                        FileOptions.Asynchronous | FileOptions.SequentialScan,
                        preallocationSize: msm.Stream.Length
                    ))
                    {
                        ReadOnlySequence<byte> sequence = msm.Stream.GetReadOnlySequence();

                        if (sequence.IsSingleSegment)
                        {
                            await RandomAccess.WriteAsync(handle, sequence.First, fileOffset: 0);
                        }
                        else
                        {
                            List<ReadOnlyMemory<byte>> buffers = new();

                            foreach (ReadOnlyMemory<byte> segment in sequence)
                            {
                                if (!segment.IsEmpty)
                                    buffers.Add(segment);
                            }

                            await RandomAccess.WriteAsync(handle, buffers, fileOffset: 0);
                        }
                    }

                    // statusCode берём НЕ из ответа: при 304 в кеш ушёл бы «пустой» ответ,
                    // и все следующие клиенты получали бы 304 без тела вместо самого тела.
                    var model = new StaticacheCacheModel(exTicks, ext,
                        (short)(notModified ? StatusCodes.Status200OK : httpContext.Response.StatusCode),
                        contentLength, etag: revalidateEtag);
                    Staticache.cacheFiles[cachekey] = model;

                    // qdl 2.16: brotli-сайдкар — фоново (финальный flush хвоста идёт после выхода из
                    // middleware, синхронное сжатие задержало бы его; msm к старту задачи disposed —
                    // CompressBr читает только что записанный raw-файл). Гейты: 200, сжимаемый ext,
                    // ≥1 КБ, TTL ≥ 2 мин (отсекает и ветки «не-200/length mismatch → 1 минута»).
                    long rawLen = msm.Stream.Length;
                    // при 304 в Response.StatusCode лежит 304, а на диск лёг полноценный ответ —
                    // сайдкар ему нужен так же, иначе следующий HIT уйдёт на динамическое сжатие
                    if ((notModified || httpContext.Response.StatusCode == 200) && rawLen >= 1024
                        && ext is "html" or "json" or "js" or "css" or "svg"
                        && ex >= DateTimeOffset.Now.AddMinutes(2))
                    {
                        _ = Task.Run(() => Staticache.CompressBr(cachekey, model, cachefile, rawLen));
                    }
                }
                catch (Exception exception)
                {
                    Serilog.Log.Error(exception, "CatchId={CatchId}", "id_23a31ad1");
                }
                finally
                {
                    sm.Release();
                }
                #endregion
            }
        }
    }
}
