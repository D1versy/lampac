using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Shared;
using Shared.Attributes;
using Shared.Models.Base;
using Shared.Services;
using Shared.Services.Pools;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace TmdbProxy;

public class TmdbProxyController : BaseController
{
    #region static
    static readonly HttpClient http2ApiClient = FriendlyHttp.CreateHttp2Client();
    static readonly HttpClient http2ImgClient = FriendlyHttp.CreateHttp2Client();

    const string tmdbApiHost = "api.themoviedb.org";
    const string tmdbImgHost = "image.tmdb.org";

    static readonly IReadOnlyList<HeadersModel> headersImg = HeadersModel.Init(
        // используем старый ua что-бы гарантировать image/jpeg вместо image/webp
        ("Accept", "image/jpeg,image/png,image/*;q=0.8,*/*;q=0.5"),
        ("User-Agent", "Mozilla/5.0 (Windows NT 6.2; WOW64) AppleWebKit/534.57.2 (KHTML, like Gecko) Version/5.1.7 Safari/534.57.2"),
        ("Cache-Control", "max-age=0")
    );
    #endregion

    #region tmdbproxy.js
    [HttpGet, AllowAnonymous]
    [Staticache(
        cacheMinutes: 10,
        always: true,
        revalidate: true
    )]
    [Route("tmdbproxy.js")]
    [Route("tmdbproxy/js/{token}")]
    public ActionResult TmdbProxy(string token)
    {
        string plugin = FileCache.ReadAllText($"{ModInit.modpath}/plugin.js", "tmdbproxy.js")
            .Replace("{localhost}", host)
            .Replace("{token}", HttpUtility.UrlEncode(token));

        return ContentTo(plugin, "application/javascript; charset=utf-8");
    }
    #endregion

    #region LegacyRoutes
    [HttpGet]
    [Route("tmdb/http:/{*suffix}")]
    [Route("tmdb/https:/{*suffix}")]
    [Route("tmdb/api.themoviedb.org/{*suffix}")]
    [Route("tmdb/image.tmdb.org/{*suffix}")]
    public RedirectResult LegacyRoutes()
    {
        ReadOnlySpan<char> path = HttpContext.Request.Path.Value
            .AsSpan()
            .Slice(6);

        string prefix = "api";

        if (path.StartsWith("https:"))
            path = path.Slice(8);
        else if (path.StartsWith("http:"))
            path = path.Slice(7);

        if (path.StartsWith(tmdbImgHost))
        {
            prefix = "img";
            path = path.Slice(tmdbImgHost.Length + 1);
        }
        else
            path = path.Slice(tmdbApiHost.Length + 1);

        if (path.StartsWith("/"))
            path = path.Slice(1);

        if (path.EndsWith('/'))
            path = path[..^1];

        return Redirect($"{host}/tmdb/{prefix}/{path}{HttpContext.Request.QueryString.Value}");
    }
    #endregion

    #region API
    // ignoreQueryKeys api_key (qdl 2.19): апстриму всё равно уходит СЕРВЕРНЫЙ ключ (см. ApiKey),
    // ответ от клиентского ключа не зависит → держать его в ключе кеша значило бы плодить бакеты
    // (публичный ключ бандла + свой ключ Android-оболочки + прогрев каталога = три копии одного JSON).
    [HttpGet]
    [Staticache(
        always: true,
        setHeadersNoCache: true,
        skipUids: true,
        queryKeys = [".*"],
        ignoreQueryKeys = ["api_key"]
    )]
    [Route("tmdb/api/{*suffix}")]
    async public Task TmdbAPI()
    {
        HttpContext.Response.ContentType = "application/json; charset=utf-8";

        var proxyManager = ModInit.conf.proxyapi?.useproxy == true
            ? new ProxyManager("tmdb_api", ModInit.conf.proxyapi)
            : null;

        ReadOnlySpan<char> path = RequestPath(HttpContext.Request.Path.Value, "/tmdb/api/");

        var result = await Http.BaseGetReaderAsync(
            e => CopyStream(BodyWriter, e.stream, e.ct),
            url: RequestUri(tmdbApiHost, path, HttpContext.Request.Query, forceApiKey: ApiKey()),
            timeoutSeconds: 15,
            httpversion: ModInit.conf.httpversion,
            proxy: proxyManager?.Get(),
            statusCodeOK: false,
            httpClient: ModInit.conf.httpversion == 2
                ? http2ApiClient
                : null
        ).ConfigureAwait(false);

        if (result.success)
        {
            int statusCode = (int)result.response.StatusCode;
            HttpContext.Response.StatusCode = statusCode;

            if (statusCode == 200)
            {
                proxyManager?.Success();

                if (ModInit.conf.cache_api > 0)
                    HttpContext.Features.Set(new StatiCacheEntry(DateTimeOffset.Now.AddMinutes(ModInit.conf.cache_api)));
            }
            else
                NoCache();
        }
        else
        {
            proxyManager?.Refresh();
            HttpContext.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            HttpContext.Response.BodyWriter.Write("{\"status_code\":1,\"status_message\":\"408 Request Timeout\",\"success\":false}"u8);
            NoCache();
        }
    }
    #endregion

    #region IMG
    // qdl 2.16: URL content-addressed (t/p/<size>/<hash>.jpg — содержимое неизменно) → год+immutable
    // вместо суточного max-age: сетка постеров не перекачивается клиентом раз в сутки.
    //
    // 🔴 qdl 2.88: раньше на отказ апстрима тут стоял `Redirect(uri)` — и это была САМАЯ ЧАСТАЯ
    // утечка во всём проекте. proxy_tmdb форсится каждую загрузку (lampainit-invc.js), значит через
    // эту ручку идут ВСЕ постеры и фоны; отказом же считался любой не-200 (statusCodeOK по умолчанию
    // true, Http.cs:712), включая рядовой 404 на битый poster_path. То есть наш сервер сам говорил
    // устройству «сходи на image.tmdb.org». Теперь отдаём свою заглушку — см. PlaceholderImage.
    // NoCache() обязателен: без него immutable-ветка залипила бы заглушку на год.
    [HttpGet]
    [Staticache(always: true, immutable: true)]
    [Route("tmdb/img/{*suffix}")]
    async public Task TmdbIMG()
    {
        ReadOnlySpan<char> path = RequestPath(HttpContext.Request.Path.Value, "/tmdb/img/");
        string uri = RequestUri(tmdbImgHost, path, HttpContext.Request.Query);

        var proxyManager = ModInit.conf.proxyimg?.useproxy == true
            ? new ProxyManager("tmdb_img", ModInit.conf.proxyimg)
            : null;

        var result = await Http.BaseGetReaderAsync(
            e => CopyStream(BodyWriter, e.stream, e.ct),
            url: uri,
            headers: headersImg,
            timeoutSeconds: 15,
            httpversion: ModInit.conf.httpversion,
            proxy: proxyManager?.Get(),
            httpClient: ModInit.conf.httpversion == 2
                ? http2ImgClient
                : null
        ).ConfigureAwait(false);

        if (result.success)
        {
            if (result.response.StatusCode == HttpStatusCode.OK)
            {
                proxyManager?.Success();

                if (ModInit.conf.cache_img > 0)
                    HttpContext.Features.Set(new StatiCacheEntry(DateTimeOffset.Now.AddMinutes(ModInit.conf.cache_img)));
            }
            else
            {
                proxyManager?.Refresh();
                NoCache();
            }

            if (result.response.Content.Headers.ContentLength.HasValue)
                HttpContext.Response.ContentLength = result.response.Content.Headers.ContentLength.Value;

            if (result.response.Content.Headers.TryGetValues("Content-Type", out var _contentType))
                HttpContext.Response.ContentType = _contentType?.FirstOrDefault();
            else
            {
                string p = HttpContext.Request.Path.Value;
                HttpContext.Response.ContentType = p.Contains(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : p.Contains(".svg", StringComparison.OrdinalIgnoreCase)
                        ? "image/svg+xml"
                        : "image/jpeg";
            }
        }
        else
        {
            proxyManager?.Refresh();
            NoCache();
            await PlaceholderImage.WriteAsync(HttpContext).ConfigureAwait(false);
        }
    }
    #endregion

    #region Helpers
    // Запрет записи ответа в Staticache (qdl 2.19).
    // Зачем: StaticacheWriter пишет на диск ЛЮБОЙ не-редирект с непустым телом — для не-200 он лишь
    // укорачивает TTL до 1 минуты (StaticacheWriter.cs: `if (StatusCode != 200) ex = now+1min`),
    // а Staticache на HIT возвращает сохранённый statusCode. Из-за этого 401/404/429 от TMDB
    // «залипали» на минуту: повторный запрос отдавался за 2 мс с X-StatiCache-Status: HIT и тем же
    // кодом, не доходя до апстрима. saveCache:false — единственный штатный способ это отменить
    // (StaticacheWriter уважает флаг ДО записи файла), тело клиенту при этом отдаётся как обычно.
    void NoCache() => HttpContext.Features.Set(new StatiCacheEntry(default, false));

    // Серверный ключ TMDB: секция "tmdb" init.conf → фолбэк на общий cub.api_key (он же зашит
    // в бандле Lampa). null = ключа нет, идём с тем, что прислал клиент (старое поведение).
    static string ApiKey()
    {
        string key = ModInit.conf?.api_key;

        if (string.IsNullOrWhiteSpace(key))
        {
            try { key = CoreInit.conf.cub?.api_key; } catch { key = null; }
        }

        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    static ReadOnlySpan<char> RequestPath(string pathString, string route)
    {
        ReadOnlySpan<char> path = pathString
            .AsSpan()
            .Slice(route.Length - 1);

        if (path.StartsWith("//"))
            path = path.Slice(1);

        if (path.EndsWith('/'))
            path = path[..^1];

        return path;
    }

    // forceApiKey != null → клиентский api_key выбрасывается, апстриму уходит серверный
    // (в т.ч. когда клиент ключа не прислал вовсе). Форма ответа не меняется.
    static string RequestUri(ReadOnlySpan<char> host, ReadOnlySpan<char> path, IQueryCollection query, string forceApiKey = null)
    {
        var uri = StringBuilderPool.ThreadInstance;

        uri.Append("https://")
           .Append(host)
           .Append(path);

        var firstArg = true;

        foreach (var q in query)
        {
            if (CoreInit.SkipQueryKeys.Contains(q.Key))
                continue;

            if (forceApiKey != null && q.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase))
                continue;

            var values = q.Value;
            if (values.Count == 0)
                continue;

            for (int i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (string.IsNullOrEmpty(value))
                    continue;

                uri.Append(firstArg ? '?' : '&');
                uri.Append(q.Key).Append('=').Append(value);
                firstArg = false;
            }
        }

        if (forceApiKey != null)
        {
            uri.Append(firstArg ? '?' : '&');
            uri.Append("api_key=").Append(forceApiKey);
        }

        return uri.ToString();
    }

    async static Task CopyStream(IBufferWriter<byte> bodyWriter, Stream stream, CancellationToken ct)
    {
        using (var byteBuf = new BufferPool())
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(byteBuf.Memory, ct)) > 0)
                bodyWriter.Write(byteBuf.Span.Slice(0, bytesRead));
        }
    }
    #endregion
}
