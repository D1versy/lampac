using Microsoft.AspNetCore.Http;
using System.Buffers;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Shared;
using Shared.Attributes;
using Shared.Models.AppConf;
using Shared.Models.Base;
using Shared.Models.Events;
using Shared.Services.Buckets;
using Shared.Services.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Middlewares;

public class Staticache
{
    #region static
    static readonly Serilog.ILogger Log = Serilog.Log.ForContext<Staticache>();

    public readonly static ConcurrentDictionary<string, StaticacheCacheModel> cacheFiles = new();

    static readonly Timer cleanupTimer = new Timer(cleanup, null, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));

    static StaticachePreparedRoute[] preparedRoutes = Array.Empty<StaticachePreparedRoute>();

    public static void Initialization()
    {
        #region load cache files
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        string cacheDir = Path.Combine("cache", "static");
        BucketFolders.Create(cacheDir);

        List<string> brSidecars = null;   // qdl 2.16: ".br"-сайдкары собираем отдельно (см. ниже)

        foreach (string inFile in Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                // qdl 2.16: сайдкар "<raw>.br" НЕ парсить как самостоятельную запись —
                // int.Parse("26362.jpg") упал бы в catch и удалил валидный файл
                if (inFile.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
                {
                    (brSidecars ??= new()).Add(inFile);
                    continue;
                }

                /// cache\static\62\-DDVxczeTgFWOm32NktG6A-1779890234674_26362.jpg
                ReadOnlySpan<char> fileName = inFile.AsSpan();

                /// cacheKey-<time>_<length>.<type>
                fileName = fileName.Slice(fileName.LastIndexOfAny('\\', '/') + 1);

                int dotIndex = fileName.LastIndexOf('.');

                /// jpg
                string ext = fileName.Slice(dotIndex + 1).ToString();

                /// cacheKey-<time>_<length>
                fileName = fileName.Slice(0, dotIndex);

                int dashIndex = fileName.LastIndexOf('-');

                // DDVxczeTgFWOm32NktG6A
                string cachekey = new string(fileName.Slice(0, dashIndex));

                int underIndex = fileName.LastIndexOf('_');

                /// 26362
                int contentLength = int.Parse(fileName.Slice(underIndex + 1));

                /// 1779890234674
                long unixTime = long.Parse(fileName.Slice(0, underIndex).Slice(dashIndex + 1));

                if (now > unixTime || string.IsNullOrEmpty(cachekey) || string.IsNullOrEmpty(ext))
                {
                    deleteFile(inFile);
                    deleteFile(inFile + ".br");   // qdl 2.16: сайдкар протухшего — тоже
                    continue;
                }

                var model = new StaticacheCacheModel(unixTime, ext, 200, contentLength);

                // qdl 2.16: .br пережил рестарт — подхватываем (порядок Enumerate не важен, смотрим на ФС)
                string brFile = inFile + ".br";
                if (File.Exists(brFile))
                    model = model with { brLength = (int)new FileInfo(brFile).Length };

                cacheFiles.TryAdd(cachekey, model);
            }
            catch
            {
                deleteFile(inFile);
            }
        }

        // qdl 2.16: сироты — .br, чей raw удалён (протух/битое имя) + догенерация .br для валидных
        // записей без сайдкара (иначе immutable app.min.js остался бы на пережиме-q1 до протухания).
        // Один фоновый поток, последовательно — на 24 ядрах незаметно.
        List<string> orphanBr = null;
        if (brSidecars != null)
            foreach (string brFile in brSidecars)
                if (!File.Exists(brFile[..^3]))
                    (orphanBr ??= new()).Add(brFile);

        _ = Task.Run(() =>
        {
            if (orphanBr != null)
                foreach (string brFile in orphanBr)
                    deleteFile(brFile);

            foreach (var c in cacheFiles)
            {
                if (c.Value.brLength > 0 || c.Value.statusCode != 200 || c.Value.ext is not ("html" or "json" or "js" or "css" or "svg"))
                    continue;

                try
                {
                    string raw = GetFilePath(c.Key, c.Value.ex, c.Value.contentLength, c.Value.ext);
                    long rawLen = new FileInfo(raw).Length;
                    if (rawLen >= 1024)
                        CompressBr(c.Key, c.Value, raw, rawLen);
                }
                catch { }
            }
        });
        #endregion

        void UpdateRoutes()
        {
            var routes = CoreInit.conf.Staticache.routes;
            if (routes == null || routes.Count == 0)
                preparedRoutes = Array.Empty<StaticachePreparedRoute>();
            else
            {
                preparedRoutes = routes.Select(r => new StaticachePreparedRoute
                {
                    Route = r,
                    PathRegex = r.pathRex != null
                        ? new Regex(
                            r.pathRex,
                            RegexOptions.IgnoreCase |
                            RegexOptions.CultureInvariant |
                            RegexOptions.Compiled,
                            TimeSpan.FromMilliseconds(100)
                        )
                        : null
                }).ToArray();
            }
        }

        UpdateRoutes();
        EventListener.UpdateInitFile += UpdateRoutes;
    }

    static void cleanup(object state)
    {
        try
        {
            var cutoff = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            foreach (var _c in cacheFiles)
            {
                if (_c.Value.ex > cutoff)
                    continue;

                if (cacheFiles.TryRemove(_c.Key, out _))
                {
                    string cachefile = GetFilePath(_c.Key, _c.Value.ex, _c.Value.contentLength, _c.Value.ext);
                    deleteFile(cachefile);
                    deleteFile(cachefile + ".br");   // qdl 2.16: File.Delete по несуществующему пути — no-op
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CatchId={CatchId}", "id_h3352g2f");
        }
    }

    static void deleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CatchId={CatchId}", "id_wfl5s3rn");
        }
    }

    // ── qdl 2.16: precompressed brotli-сайдкар ──
    // Жмём ОДИН раз (фоново, q9: для 2 МБ ~100-150 мс на ядро, разово) вместо пережима q1 на каждом
    // HIT (ResponseCompressionBody даже давал размер ХУЖЕ MISS из-за поблочной подачи SendFile).
    // Атомарность: .tmp → File.Move; флаг в реестре — ТОЛЬКО после move через TryUpdate с comparand:
    // если запись успела смениться (новый ex/len), наш .br — сирота, его подчистит startup-sweep.
    public static void CompressBr(string cachekey, StaticacheCacheModel model, string rawFile, long rawLen)
    {
        string brFile = rawFile + ".br", tmp = brFile + ".tmp";
        try
        {
            using (var src = File.OpenRead(rawFile))
            using (var dst = File.Create(tmp))
            using (var br = new System.IO.Compression.BrotliStream(dst, new System.IO.Compression.BrotliCompressionOptions { Quality = 9 }))
                src.CopyTo(br);

            long len = new FileInfo(tmp).Length;
            if (len == 0 || len >= rawLen)
            {
                deleteFile(tmp);   // несжимаемое — остаёмся на raw
                return;
            }

            File.Move(tmp, brFile, overwrite: true);

            if (!cacheFiles.TryUpdate(cachekey, model with { brLength = (int)len }, model))
                deleteFile(brFile);
        }
        catch (Exception ex)
        {
            deleteFile(tmp);
            Log.Error(ex, "CatchId={CatchId}", "id_stcbr01");
        }
    }

    // Быстрый путь без парсера для типичного "gzip, deflate, br"; точный разбор q-values —
    // только при наличии ';' (чтобы не отдать br клиенту с "br;q=0").
    static bool AcceptsBr(HttpRequest req)
    {
        var ae = req.Headers.AcceptEncoding;
        if (ae.Count == 0)
            return false;

        string s = ae.ToString();
        if (!s.Contains("br", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!s.Contains(';'))
            return true;

        return StringWithQualityHeaderValue.TryParseList(ae, out var list)
            && list.Any(v => v.Value.Equals("br", StringComparison.OrdinalIgnoreCase) && (v.Quality ?? 1) > 0);
    }
    #endregion

    private readonly RequestDelegate _next;

    public Staticache(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext httpContext)
    {
        if (!HttpMethods.IsGet(httpContext.Request.Method))
            return _next(httpContext);

        var requestInfo = httpContext.Features.Get<RequestModel>();
        if (requestInfo.AesGcmKey != null || requestInfo.IsProxyRequest || requestInfo.IsProxyImg)
            return _next(httpContext);

        var endpoint = httpContext.GetEndpoint();
        var staticache = endpoint?.Metadata?.GetMetadata<StaticacheAttribute>();

        if (staticache == null)
            return _next(httpContext);

        if (staticache.setHeadersNoCache)
            WriteNoCache(httpContext);
        // qdl 2.53: revalidate — «храни, но каждый раз переспрашивай». Ставим ЗДЕСЬ, до ветвления
        // HIT/MISS, чтобы заголовок был одинаковый на обоих путях. 🔴 Именно no-cache, а НЕ
        // no-store: последний запрещает хранить и убивает саму ревалидацию (грабля из
        // QbitDownload/HttpCache). ETag добавляется ниже, на HIT-пути, где есть файл тела.
        else if (staticache.revalidate)
            httpContext.Response.Headers[HeaderNames.CacheControl] = "no-cache";

        var init = CoreInit.conf.Staticache;

        #region EventListener
        if (EventListener.Staticache != null)
        {
            var em = new EventStaticache(httpContext, requestInfo);

            foreach (Func<EventStaticache, bool> handler in EventListener.Staticache.GetInvocationList())
            {
                if (!handler(em))
                    return _next(httpContext);
            }
        }
        #endregion

        bool customRoute = false;
        StaticacheRoute route = default;
        string path = httpContext.Request.Path.Value;

        #region init routes
        if (init.enable)
        {
            foreach (var p in preparedRoutes)
            {
                var r = p.Route;

                if ((r.path != null && path.Equals(r.path, StringComparison.OrdinalIgnoreCase))
                    || (r.pathRex != null && p.PathRegex.IsMatch(path)))
                {
                    customRoute = true;
                    route = r;
                    break;
                }
            }
        }
        #endregion

        if (staticache.always == false)
        {
            if (customRoute == false)
            {
                // endpoint или настройки init требует явный routes
                if (init.enable == false || staticache.manually || init.manually)
                    return _next(httpContext);
            }

            if (init.minimalCacheMinutes > staticache.cacheMinutes)
                return _next(httpContext);
        }

        if (init.disabledPaths != null && init.disabledPaths.Contains(path))
            return _next(httpContext);

        if (0 >= route.cacheMinutes)
            route.cacheMinutes = staticache.cacheMinutes;

        if (route.queryKeys == null)
            route.queryKeys = staticache.queryKeys;

        if (route.ignoreQueryKeys == null)
            route.ignoreQueryKeys = staticache.ignoreQueryKeys;

        var parameters = endpoint.Metadata
            .GetMetadata<ControllerActionDescriptor>()?
            .Parameters;

        string cachekey = getQueryKeys(httpContext, route.skipUids || staticache.skipUids, parameters, route.queryKeys, route.ignoreQueryKeys);

        if (cacheFiles.TryGetValue(cachekey, out StaticacheCacheModel _r))
        {
            httpContext.Response.StatusCode = _r.statusCode;
            httpContext.Response.Headers["X-StatiCache-Status"] = "HIT";
            httpContext.Response.Headers["X-StatiCache-Bucket"] = BucketFolders.Name(cachekey[0]);
            httpContext.Response.Headers["X-StatiCache-Id"] = cachekey;

            string ext = _r.ext switch
            {
                "html" => "text/html; charset=utf-8",
                "json" => "application/json; charset=utf-8",
                "js" => "application/javascript; charset=utf-8",
                "css" => "text/css; charset=utf-8",
                "png" => "image/png",
                "jpg" => "image/jpeg",
                "svg" => "image/svg+xml",
                "webp" => "image/webp",
                _ => "application/octet-stream"
            };

            if (_r.contentLength > 0)
                httpContext.Response.ContentLength = _r.contentLength;

            // immutable-эндпоинты (versioned-URL ?v=) кэшируются у клиента навсегда независимо от
            // contentLength: js/html пишутся chunked (без длины), и общий "contentLength > 0"-путь
            // их не покрывает — из-за этого app.min.js исторически ходил вообще без Cache-Control.
            // Гейт по наличию ?v: прямой запрос БЕЗ версии вечно кэшировать нельзя. Исключение
            // (qdl 2.16): у attr'а НЕТ queryKeys → URL content-addressed (постеры /tmdb/img/<hash>),
            // версия не нужна — immutable всегда.
            if (staticache.immutable && _r.statusCode == 200 && (staticache.queryKeys == null || httpContext.Request.Query.ContainsKey("v")))
                httpContext.Response.Headers[HeaderNames.CacheControl] = "public,max-age=31536000,immutable";
            // revalidate уже поставил no-cache выше — сюда не проваливаемся, иначе ветка
            // "contentLength > 0" перебила бы его вечным max-age.
            else if (staticache.revalidate) { }
            else if (_r.contentLength > 0)
                httpContext.Response.Headers[HeaderNames.CacheControl] = "public,max-age=86400,immutable";
            else if (ext is "json" or "html")
                WriteNoCache(httpContext);

            httpContext.Response.ContentType = ext;

            string file = GetFilePath(cachekey, _r.ex, _r.contentLength, _r.ext);

            #region revalidate: ETag / 304
            // Плагины lampac (online.js, sisi.js, sync.js, …) не имеют versioned-URL: их адреса
            // лежат в localStorage клиента с первого запуска, менять их нельзя. Раньше они шли с
            // no-store и качались целиком каждый старт (~47 КБ br на восьмерых), причём showApp()
            // ждёт именно их — он зовётся из Plugins.load(showApp).
            //
            // ETag считается ЛЕНИВО от байтов raw-файла и кладётся в модель: TTL-пересчёт записи
            // его не меняет (тело то же — тот же хеш), поэтому 304 работает и через сутки. Хеш
            // берётся один раз на запись за жизнь процесса; после рестарта модели поднимаются
            // сканом каталога без etag — первая отдача его и посчитает.
            //
            // ⚠️ ETag СЛАБЫЙ (W/): при Accept-Encoding: br то же тело уезжает сайдкаром, побайтовой
            // идентичности представления нет. Vary: Accept-Encoding ставится ниже.
            if (staticache.revalidate && _r.statusCode == 200)
            {
                string etag = _r.etag;

                if (etag == null)
                {
                    try
                    {
                        etag = FileEtag(file);
                        cacheFiles.TryUpdate(cachekey, _r with { etag = etag }, _r);
                    }
                    catch { etag = null; }   // файл вытеснили из-под нас — просто отдаём тело
                }

                if (etag != null)
                {
                    httpContext.Response.Headers[HeaderNames.ETag] = etag;

                    if (IfNoneMatchHit(httpContext.Request.Headers[HeaderNames.IfNoneMatch], etag))
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                        httpContext.Response.Headers[HeaderNames.Vary] = "Accept-Encoding";
                        httpContext.Response.ContentLength = null;
                        return Task.CompletedTask;
                    }
                }
            }
            #endregion

            // qdl 2.16: готовый brotli-сайдкар мимо ResponseCompression: Content-Encoding ДО записи
            // тела → ShouldCompressResponse видит его и уходит в pass-through (нативный SendFile,
            // Content-Length сохраняется). Vary middleware в pass-through НЕ ставит — ставим сами.
            if (_r.brLength > 0 && AcceptsBr(httpContext.Request))
            {
                httpContext.Response.Headers[HeaderNames.ContentEncoding] = "br";
                httpContext.Response.Headers[HeaderNames.Vary] = "Accept-Encoding";
                httpContext.Response.ContentLength = _r.brLength;   // точная длина даже для бывших chunked
                return httpContext.Response.SendFileAsync(file + ".br");
            }

            return httpContext.Response.SendFileAsync(file);
        }
        else
        {
            httpContext.Features.Set(new StaticacheFeature(route.cacheMinutes, cachekey));
            return _next(httpContext);
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string getQueryKeys(HttpContext httpContext, bool skipUids, IList<ParameterDescriptor> parameters, string[] queryKeys, string[] ignoreQueryKeys)
    {
        var hash = Fnv1a.Empty;

        Fnv1a.Append(ref hash, httpContext.Request.Scheme);
        Fnv1a.Append(ref hash, httpContext.Request.Host.Value);
        Fnv1a.Append(ref hash, httpContext.Request.Path.Value);

        if (httpContext.Request.Query.TryGetValue("rjson", out StringValues rjson) && rjson.Count > 0)
            Fnv1a.Append(ref hash, rjson[0]);

        if (queryKeys != null && queryKeys.Length > 0)
        {
            if (queryKeys.Length == 1 && queryKeys[0] == ".*")
            {
                foreach (var q in httpContext.Request.Query.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    string key = q.Key;

                    if (skipUids && CoreInit.SkipQueryKeys.Contains(key))
                        continue;

                    if (ignoreQueryKeys != null && ignoreQueryKeys.Contains(key))
                        continue;

                    Fnv1a.Append(ref hash, key);
                    Fnv1a.Append(ref hash, q.Value);
                }
            }
            else
            {
                foreach (string key in queryKeys)
                    QueryAppend(ref hash, key, httpContext, skipUids, ignoreQueryKeys);
            }
        }
        else if (parameters != null && parameters.Count > 0)
        {
            foreach (var param in parameters)
                QueryAppend(ref hash, param.Name, httpContext, skipUids, ignoreQueryKeys);
        }

        return Fnv1a.Base64Url(hash);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueryAppend(ref Fnv1aHash hash, string key, HttpContext httpContext, bool skipUids, string[] ignoreQueryKeys)
    {
        if (key == null)
            return;

        if (skipUids && CoreInit.SkipQueryKeys.Contains(key))
            return;

        if (ignoreQueryKeys != null && ignoreQueryKeys.Contains(key))
            return;

        if (httpContext.Request.Query.TryGetValue(key, out StringValues value) && value.Count > 0)
        {
            Fnv1a.Append(ref hash, key);
            Fnv1a.Append(ref hash, value[0]);
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetFilePath(string cachekey, long ex, int length, string ext)
        => Path.Combine("cache", "static", BucketFolders.Name(cachekey[0]), $"{cachekey}-{ex}_{length}.{ext}");


    /// <summary>
    /// Слабый ETag тела для revalidate-роутов. HIT считает его от байтов кеш-файла, MISS — от
    /// буфера ответа; файл пишется из того же буфера, поэтому значения совпадают побайтово —
    /// иначе первый же MISS выдал бы клиенту новый тег на неизменившееся тело.
    /// </summary>
    public static string BodyEtag(ReadOnlySequence<byte> body)
    {
        var hash = Fnv1a.Empty;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            if (!segment.IsEmpty)
                Fnv1a.Append(ref hash, segment.Span);
        }
        return "W/" + '"' + Fnv1a.Base64Url(hash) + '"';
    }

    public static string FileEtag(string file)
        => BodyEtag(new ReadOnlySequence<byte>(File.ReadAllBytes(file)));

    /// <summary>
    /// Слабое сравнение If-None-Match по RFC 9110 §8.8.3.2: сопоставляются только «непрозрачные»
    /// части тегов, префикс W/ игнорируется. Заголовок может нести список через запятую и "*".
    /// Тот же алгоритм, что в QbitDownload/HttpCache для /qdl/list — продублирован намеренно:
    /// Core не может ссылаться на модуль.
    /// </summary>
    public static bool IfNoneMatchHit(string header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(etag))
            return false;

        string mine = Opaque(etag);
        if (mine == null)
            return false;

        if (header.Trim() == "*")
            return true;

        foreach (var part in header.Split(','))
        {
            string other = Opaque(part);
            if (other != null && string.Equals(other, mine, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>`W/"abc"` и `"abc"` → `abc`; всё, что не похоже на entity-tag → null.</summary>
    private static string Opaque(string tag)
    {
        if (tag == null)
            return null;

        var t = tag.AsSpan().Trim();

        if (t.StartsWith("W/"))
            t = t[2..].TrimStart();

        if (t.Length < 2 || t[0] != '"' || t[^1] != '"')
            return null;

        return t[1..^1].ToString();
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteNoCache(HttpContext httpContext)
    {
        httpContext.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate"; // HTTP 1.1.
        httpContext.Response.Headers["Pragma"] = "no-cache"; // HTTP 1.0.
        httpContext.Response.Headers["Expires"] = "0"; // Proxies.
    }
}
