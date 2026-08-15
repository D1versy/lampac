using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Attributes;
using Shared.Models.Base;
using Shared.Services;
using Shared.Services.Pools;
using System;
using System.Buffers;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace CubProxy;

public class CubProxyController : BaseController
{
    #region cubproxy.js
    [HttpGet, AllowAnonymous]
    [Staticache(
        cacheMinutes: 10,
        always: true,
        setHeadersNoCache: true
    )]
    [Route("cubproxy.js")]
    [Route("cubproxy/js/{token}")]
    public ActionResult Plugin(string token)
    {
        string plugin = FileCache.ReadAllText($"{ModInit.modpath}/plugin.js", "cubproxy.js")
            .Replace("{localhost}", host)
            .Replace("{token}", HttpUtility.UrlEncode(token));

        return ContentTo(plugin, "application/javascript; charset=utf-8");
    }
    #endregion

    #region HttpPost
    [HttpPost, AllowAnonymous]
    [Route("cub/{*suffix}")]
    async public Task Bypass()
    {
        using (var ctsHttp = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted))
        {
            ctsHttp.CancelAfter(TimeSpan.FromSeconds(15));

            var init = ModInit.conf;

            string path = HttpContext.Request.Path.Value
                .Substring(5)
                .ToLowerAndTrim();

            int slashIndex = path.IndexOf('/');
            string uri = (slashIndex >= 0 ? path.Substring(slashIndex + 1) : path) + HttpContext.Request.QueryString.Value;

            #region checker
            if (path.StartsWith("api/checker") || uri.StartsWith("api/checker"))
            {
                var ct = HttpContext.Request.ContentType;
                if (ct != null && ct.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    using (var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8, false, leaveOpen: true))
                    {
                        string form = await reader.ReadToEndAsync(ctsHttp.Token);
                        await HttpContext.Response.WriteAsync(form.Split('=')[1], ctsHttp.Token);
                        return;
                    }
                }

                HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                HttpContext.Response.BodyWriter.Write("error"u8);
                return;
            }
            #endregion

            var proxyManager = init.useproxy
                ? new ProxyManager("cub_api", init)
                : null;

            var proxy = proxyManager?.Get();

            int dotIndex = path.IndexOf('.');
            string domain = GetDomain(dotIndex >= 0 ? path[..dotIndex] : string.Empty, init.domain);

            string requri = $"{init.scheme}://{domain}/{uri}";

            var client = FriendlyHttp.MessageClient(
                "proxyRedirect",
                Http.HandlerOrNull(requri, proxy),
                out bool disposeHttpClient,
                findNoRedirectClient: false
            );

            try
            {
                using (var request = CreateProxyHttpRequest(HttpContext, new Uri(requri), requestInfo))
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctsHttp.Token).ConfigureAwait(false))
                    {
                        HttpContext.Response.Headers["X-Cache-Status"] = "bypass";
                        await CopyProxyHttpResponse(HttpContext, response, ctsHttp.Token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (disposeHttpClient)
                    client.Dispose();
            }
        }
    }
    #endregion

    #region HttpGet
    [HttpGet, AllowAnonymous]
    [Staticache(
        always: true,
        setHeadersNoCache: true,
        skipUids: true,
        queryKeys = [".*"]
    )]
    [Route("cub/{*suffix}")]
    public Task Proxy()
    {
        var init = ModInit.conf;

        string path = HttpContext.Request.Path.Value
            .Substring(5)
            .ToLowerAndTrim();

        int dotIndex = path.IndexOf('.');
        string subdomain = dotIndex >= 0 ? path[..dotIndex] : string.Empty;
        string domain = GetDomain(subdomain, init.domain);

        int slashIndex = path.IndexOf('/');
        string uri = (slashIndex >= 0 ? path.Substring(slashIndex + 1) : path) + HttpContext.Request.QueryString.Value;

        #region ws
        if (subdomain.Equals("ws"))
        {
            HttpContext.Response.Redirect($"https://{domain}{HttpContext.Request.QueryString.Value}");
            return Task.CompletedTask;
        }
        #endregion

        #region checker
        if (path.StartsWith("api/checker") || uri.StartsWith("api/checker"))
        {
            HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.BodyWriter.Write("ok"u8);
            return Task.CompletedTask;
        }
        #endregion

        #region blacklist
        if (uri.StartsWith("api/plugins/blacklist"))
        {
            HttpContext.Response.ContentType = "application/json; charset=utf-8";
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.BodyWriter.Write("[]"u8);
            return Task.CompletedTask;
        }
        #endregion

        #region ai metadata
        // qdl 2.45: CUB AI-метаданные. Апстрим стабильно отдаёт 500 «Метаданные не найдены»
        // (премиум-фича аккаунта, которого у нас нет) за 60–212 мс, изредка за 4 с, и это НЕ
        // кешируется — StaticacheWriter режет TTL не-200 до минуты. Клиент ждёт этот ответ на
        // КАЖДОМ открытии карточки: metadataGet идёт без params.cache, то есть и клиентского кеша
        // нет. Пустой объект — ровно то, что бандл подставляет в своём error-пути (`oncomplite({})`),
        // так что поведение UI не меняется, уходит только ожидание.
        // Основной глушитель — клиентский `disable_features.metadata = true` в lampainit-invc.js
        // (запрос вообще не уходит); эта ветка ловит клиентов со старым закешированным lampainit.
        if (init.stubAiMetadata && uri.StartsWith("api/ai/metadata/"))
        {
            HttpContext.Response.ContentType = "application/json; charset=utf-8";
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.BodyWriter.Write("{}"u8);
            return Task.CompletedTask;
        }
        #endregion

        #region metric
        if (uri.StartsWith("api/metric/") || uri.StartsWith("api/ad/stat"))
        {
            HttpContext.Response.ContentType = "application/json; charset=utf-8";
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.BodyWriter.Write("{\"secuses\":true}"u8);
            return Task.CompletedTask;
        }
        #endregion

        #region ads
        if (uri.StartsWith("api/ad/vast"))
        {
            return HttpContext.Response.WriteAsJsonAsync(new
            {
                secuses = true,
                ad = Array.Empty<string>(),
                day_of_month = DateTime.Now.Day,
                days_in_month = 31,
                month = DateTime.Now.Month
            }, HttpContext.RequestAborted);
        }
        #endregion

        return ProxyAsync(init, path, uri, subdomain, domain);
    }

    async Task ProxyAsync(ModuleConf init, string path, string uri, string subdomain, string domain)
    {
        using (var ctsHttp = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted))
        {
            ctsHttp.CancelAfter(TimeSpan.FromSeconds(15));

            #region geo
            if (subdomain.Equals("geo"))
            {
                string country = requestInfo.Country;
                if (country == null)
                    country = await mylocalip();

                await HttpContext.Response.WriteAsync(country ?? string.Empty, ctsHttp.Token);
                return;
            }
            #endregion

            var proxyManager = init.useproxy
                ? new ProxyManager("cub_api", init)
                : null;

            var proxy = proxyManager?.Get();
            string requri = $"{init.scheme}://{domain}/{uri}";

            if (HttpContext.Request.Headers.ContainsKey("token") || HttpContext.Request.Headers.ContainsKey("profile"))
            {
                #region bypass
                var client = FriendlyHttp.MessageClient(
                    "proxyRedirect",
                    Http.HandlerOrNull(requri, proxy),
                    out bool disposeHttpClient,
                    findNoRedirectClient: false
                );

                try
                {
                    using (var request = CreateProxyHttpRequest(HttpContext, new Uri(requri), requestInfo))
                    {
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctsHttp.Token).ConfigureAwait(false))
                        {
                            HttpContext.Response.Headers["X-Cache-Status"] = "bypass";
                            await CopyProxyHttpResponse(HttpContext, response, ctsHttp.Token).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    if (disposeHttpClient)
                        client.Dispose();
                }
                #endregion
            }
            else
            {
                #region headers
                var headers = HeadersModel.Init();

                if (subdomain == "tmdb")
                {
                    if (init.viewru)
                        headers.Add(new("cookie", "viewru=1"));

                    headers.Add(new("user-agent", HttpContext.Request.Headers.UserAgent.ToString()));
                }
                else
                {
                    foreach (var header in HttpContext.Request.Headers)
                    {
                        if (header.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
                            header.Key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
                            headers.Add(new(header.Key, header.Value.ToString()));
                    }
                }
                #endregion

                var result = await Http.BaseGetReaderAsync(
                    async e =>
                    {
                        using (var nbuf = new BufferPool())
                        {
                            int bytesRead;
                            while ((bytesRead = await e.stream.ReadAsync(nbuf.Memory, e.ct).ConfigureAwait(false)) > 0)
                                BodyWriter.Write(nbuf.Span.Slice(0, bytesRead));
                        }
                    },
                    url: requri,
                    headers: headers,
                    timeoutSeconds: 15,
                    proxy: proxy,
                    statusCodeOK: false
                ).ConfigureAwait(false);

                if (result.success)
                {
                    CopyResponseHeaders(HttpContext, result.response);

                    if (result.response.StatusCode == HttpStatusCode.OK)
                    {
                        proxyManager?.Success();

                        // qdl 2.45: реакции живут отдельным, длинным TTL. Общий cache_api=180
                        // подобран под РЯДЫ каталога (там свежесть важна), а реакции — почти
                        // статика: их дёргают на КАЖДОМ открытии карточки, и при 3 ч это восемь
                        // холодных промахов в сутки по 61 мс на каждую карточку. Прогрев карточек
                        // идёт циклом ~24 ч, то есть с TTL 3 ч реакции всё равно успевали остыть
                        // между тиками — поднимаем именно их.
                        // ⚠️ Через Staticache.routes в init.conf это НЕ настраивается: строка ниже
                        // ставит явный StatiCacheEntry на ответ, и он перебивает cacheMinutes роута
                        // (проверено замером — TTL оставался 179 мин).
                        int ttl = uri.StartsWith("api/reactions/get/") && init.cache_reactions > 0
                            ? init.cache_reactions
                            : init.cache_api;

                        if (ttl > 0)
                            HttpContext.Features.Set(new StatiCacheEntry(DateTimeOffset.Now.AddMinutes(ttl)));
                    }
                    else
                        proxyManager?.Refresh();

                    if (result.response.Content.Headers.TryGetValues("Content-Type", out var _contentType))
                        HttpContext.Response.ContentType = _contentType?.FirstOrDefault();
                    else
                    {
                        HttpContext.Response.ContentType = Path.GetExtension(HttpContext.Request.Path.Value) switch
                        {
                            ".jpg" or ".jpeg" => "image/jpeg",
                            ".png" => "image/png",
                            ".gif" => "image/gif",
                            ".webp" => "image/webp",
                            ".ico" => "image/x-icon",
                            ".svg" => "image/svg+xml",
                            ".mp4" => "video/mp4",
                            ".js" => "application/javascript",
                            ".css" => "text/css",
                            _ => "application/octet-stream"
                        };
                    }

                    if (result.response.Content.Headers.ContentLength.HasValue && !CoreInit.ContainsMimeTypes(HttpContext.Response.ContentType))
                        HttpContext.Response.ContentLength = result.response.Content.Headers.ContentLength.Value;
                }
                else
                {
                    proxyManager?.Refresh();
                    HttpContext.Response.StatusCode = StatusCodes.Status302Found;
                    HttpContext.Response.Redirect(requri);
                }
            }
        }
    }
    #endregion


    #region CreateProxyHttpRequest
    static readonly FrozenSet<string> excludedRequestHeaders = new[]
    {
        "host",
        "origin",
        "referer",
        "content-disposition",
        "accept-encoding"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    static HttpRequestMessage CreateProxyHttpRequest(HttpContext context, Uri uri, RequestModel requestInfo)
    {
        var request = context.Request;

        var requestMessage = new HttpRequestMessage();

        var requestMethod = request.Method;
        if (HttpMethods.IsPost(requestMethod))
        {
            var streamContent = new StreamContent(request.Body);
            requestMessage.Content = streamContent;
        }

        #region Headers
        foreach (var header in request.Headers)
        {
            string key = header.Key;

            if (excludedRequestHeaders.Contains(key))
                continue;

            if (key.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                if (requestMessage.Content?.Headers != null)
                    requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
        #endregion

        requestMessage.Headers.Host = uri.Authority;
        requestMessage.RequestUri = uri;
        requestMessage.Version = HttpVersion.Version11;

        requestMessage.Method = HttpMethods.IsGet(request.Method)
            ? HttpMethod.Get
            : HttpMethods.IsPost(request.Method)
                ? HttpMethod.Post
                : new HttpMethod(request.Method);

        return requestMessage;
    }
    #endregion

    #region CopyProxyHttpResponse
    async Task CopyProxyHttpResponse(HttpContext context, HttpResponseMessage responseMessage, CancellationToken ct)
    {
        var response = context.Response;
        CopyResponseHeaders(context, responseMessage);

        await using (var responseStream = await responseMessage.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested)
                return;

            using (var nbuf = new BufferPool())
            {
                int bytesRead;
                var memBuf = nbuf.Memory;

                while ((bytesRead = await responseStream.ReadAsync(memBuf, ct).ConfigureAwait(false)) > 0)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    await response.Body.WriteAsync(memBuf.Slice(0, bytesRead), ct).ConfigureAwait(false);
                }
            }
        }
    }
    #endregion

    #region CopyResponseHeaders
    static readonly FrozenSet<string> excludedResponseHeaders = new[]
    {
        "server",
        "transfer-encoding",
        "etag",
        "connection",
        "content-security-policy",
        "content-disposition"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    static void CopyResponseHeaders(HttpContext context, HttpResponseMessage responseMessage)
    {
        var response = context.Response;
        response.StatusCode = (int)responseMessage.StatusCode;

        void UpdateHeaders(HttpHeaders headers)
        {
            if (headers == null)
                return;

            foreach (var header in headers)
            {
                string key = header.Key;

                if (excludedResponseHeaders.Contains(key))
                    continue;

                if (key.StartsWith("x-", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("alt-", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (key.StartsWith("access-control", StringComparison.OrdinalIgnoreCase))
                    continue;

                var values = header.Value;
                response.Headers[key] = header.Value.ToArray();
            }
        }

        UpdateHeaders(responseMessage.Headers);
        UpdateHeaders(responseMessage.Content?.Headers);
    }
    #endregion


    #region Helpers
    static string GetDomain(string subdomain, string domain)
    {
        if (subdomain is "geo" or "tmdb" or "tmapi" or "apitmdb" or "imagetmdb" or "cdn" or "ad" or "ws")
        {
            var uri = StringBuilderPool.ThreadInstance;

            uri.Append(subdomain)
               .Append('.')
               .Append(domain);

            return uri.ToString();
        }

        return domain;
    }
    #endregion
}
