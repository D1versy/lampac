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
using System.Collections.Generic;
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
        revalidate: true
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
        // qdl 2.88: раньше отсюда уходил 302 на https://ws.cub.best — то есть наш сервер сам
        // отправлял клиента открывать сокет к третьей стороне. Штатный бандл сюда не ходит
        // (lampainit-invc.js гасит socket_use), но роут был открыт кому угодно. Закрыт.
        if (subdomain.Equals("ws"))
        {
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
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
                // 🔴 Ответ ПЕРСОНАЛЬНЫЙ, в общий кеш его класть нельзя (qdl 2.65). Ключ Staticache
                // считается как Scheme+Host+Path+Query и заголовков не включает (Core/Middlewares/
                // Staticache.cs), а роут помечен [Staticache(always: true, …)] — то есть без этой
                // строки авторизованный ответ ложился под АНОНИМНЫМ ключом и раздавался всем
                // клиентам (и реплике) до истечения TTL. Плюс ниже не фильтруется set-cookie.
                // Сейчас не эксплуатируется — наши клиенты заголовок token не шлют (permit.access
                // = token && account_use, аккаунта CUB у нас нет), но заряжено было.
                HttpContext.Features.Set(new StatiCacheEntry(default, false));

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

                #region фильтр рядов каталога по году (qdl 2.89)
                // Ряды «что сейчас / новинки» режем по году выпуска — порог глобальный, лежит
                // в файле на томе (пишет его QbitDownload, см. FilterStore). Топы, жанры,
                // коллекции и детали карточки не трогаем: список кандидатов — в RowFilter.
                //
                // 🔴 Почему фильтруем ЗДЕСЬ, в контроллере, а не в middleware: на промахе кеша
                // BodyWriter — это не сокет, а LazyMsm (буфер в памяти, BaseController), и
                // StaticacheWriter кладёт на диск ровно те байты, что записал контроллер. То есть
                // фильтрация тут автоматически означает «раз в TTL (3 ч)», а не на каждый запрос.
                // Любая точка до UseStaticacheWriter (EventListener.Middleware) закешироваться
                // не смогла бы вообще.
                var fconf = init.catalogFilter && RowFilter.IsCandidate(subdomain, uri)
                    ? FilterStore.Read(init.catalogFilterFile)
                    : default;

                bool filtering = fconf.enabled;

                // ── сторож номера страницы (qdl 2.112) ────────────────────────────────────
                // У CUB перед API свой кеш nginx, и он ИНОГДА отдаёт тело чужой страницы:
                // боевые замеры — page=1 → body page 11, page=21 → 2, page=31 → 3. Мы
                // примораживаем это на cache_api (3 ч) ОТДЕЛЬНО под каждый вход, и владелец
                // видит в топе главной одиннадцатую страницу живого потока. Разбор — §DI и §DO.
                //
                // Кандидатность гейтит БУФЕРИЗАЦИЮ, поэтому считается до похода в апстрим и
                // смотрит на поддомен, а не на content-type: картинки живут на imagetmdb/cdn
                // и под сторож не попадают по построению.
                bool guarding = init.pageGuard && PageGuard.IsCandidate(subdomain, uri);

                bool buffering = filtering || guarding;
                var fbuf = buffering ? new MemoryStream() : null;
                #endregion

                var result = await Http.BaseGetReaderAsync(
                    async e =>
                    {
                        using (var nbuf = new BufferPool())
                        {
                            int bytesRead;
                            while ((bytesRead = await e.stream.ReadAsync(nbuf.Memory, e.ct).ConfigureAwait(false)) > 0)
                            {
                                if (buffering)
                                {
                                    // Потолок буфера (qdl 2.112). Тело крупнее всего, что бывает у
                                    // tmdb.cub.* (замер боевого: ряды 7–22 КБ) — не копим дальше:
                                    // сливаем накопленное и дочитываем потоком, сторож и фильтр на
                                    // этом ответе просто не применяются. Заодно закрывает и прежнюю
                                    // НЕОГРАНИЧЕННУЮ буферизацию фильтра рядов.
                                    if (fbuf.Length + bytesRead > PageGuard.MaxBodyBytes)
                                    {
                                        BodyWriter.Write(fbuf.ToArray());
                                        fbuf.Dispose();
                                        fbuf = null;
                                        buffering = filtering = guarding = false;
                                        BodyWriter.Write(nbuf.Span.Slice(0, bytesRead));
                                        continue;
                                    }

                                    fbuf.Write(nbuf.Span.Slice(0, bytesRead));
                                }
                                else
                                    BodyWriter.Write(nbuf.Span.Slice(0, bytesRead));
                            }
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
                    // 🔴 qdl 2.88: тут стоял 302 на cub.best — самый широкий канал утечки во всём
                    // CubProxy: он покрывал ВЕСЬ префикс /cub/* (картинки, img/background/default.mp4,
                    // plugin/*) и срабатывал при любом таймауте апстрима. Содержимое мы выдумать не
                    // можем, поэтому честная ошибка — но НЕ адрес третьей стороны.
                    proxyManager?.Refresh();
                    HttpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
                }

                #region отдача: сторож страницы (qdl 2.112) + фильтр рядов (qdl 2.89)
                if (buffering)
                {
                    byte[] raw = fbuf.ToArray();
                    fbuf.Dispose();

                    bool json200 = result.success && result.response.StatusCode == HttpStatusCode.OK &&
                        (HttpContext.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false);

                    // одна декодировка на обоих потребителей — сторожа и фильтр
                    string text = json200 ? Encoding.UTF8.GetString(raw) : null;

                    #region сторож номера страницы (qdl 2.112)
                    // Дефект, замеры и правила — шапка PageGuard.cs и §DO. Здесь только сама врезка.
                    var judged = guarding && text != null
                        ? PageGuard.Judge(uri, text)
                        : (PageGuard.Verdict.Skip, null, null, -1);

                    if (judged.verdict == PageGuard.Verdict.Match)
                    {
                        // Заголовок нужен живой проверке (cubpage.mjs): без него «сторож отработал
                        // и всё сошлось» неотличимо от «сторож выключен». На HIT не воспроизводится —
                        // Staticache хранит только тело — и это ожидаемо.
                        HttpContext.Response.Headers[PageGuard.HeaderName] = "match";

                        // Копию заводим ТОЛЬКО на заведомо верном и НЕПУСТОМ ответе: пустая лента,
                        // подставленная потом вместо чужой страницы, выбросила бы ряд с главной
                        // (мина 2 из §CW).
                        if (judged.results > 0)
                            PageStore.Save(PageGuard.StoreKey(uri), text);
                    }
                    else if (judged.verdict == PageGuard.Verdict.Mismatch)
                    {
                        int wanted = judged.wanted ?? 0;
                        string skey = PageGuard.StoreKey(uri);

                        var (healed, fuseOpen) = await PageHeal(init, requri, uri, headers, proxy).ConfigureAwait(false);
                        string restored = healed == null && !fuseOpen
                            ? PageStore.Load(skey, wanted, init.pageGuardKeepMinutes)
                            : null;

                        var action = PageGuard.Decide(judged.verdict, fuseOpen, healed != null, restored != null);

                        // 🔴 ContentLength = null обязателен в ЛЮБОЙ ветке ниже, и по двум разным
                        // причинам. (1) Тело подменено — при рассинхроне длины StaticacheWriter
                        // срежет TTL записи до ОДНОЙ минуты (мина 1 из §CW). (2) Тело НЕ подменено,
                        // но подозрительно — при живом content-length StaticacheWriter навесит
                        // клиенту Cache-Control: public,max-age=86400,immutable, и подозрительный
                        // ответ осядет в кеше УСТРОЙСТВА на сутки, куда серверное «не кешировать»
                        // уже не дотянется.
                        HttpContext.Response.ContentLength = null;

                        // TTL «подозрительного, но верного» тела: кеш CUB по ключу сейчас нестабилен,
                        // общие 3 часа ему давать неразумно. 0 — не кешировать вовсе.
                        var shortTtl = init.pageGuardSuspectMinutes > 0
                            ? new StatiCacheEntry(DateTimeOffset.Now.AddMinutes(init.pageGuardSuspectMinutes))
                            : new StatiCacheEntry(default, false);

                        // ⚠️ Features.Set ниже — ПОСЛЕДНИЙ, он и выигрывает: StaticacheWriter читает
                        // фичу один раз, уже после выхода из контроллера, а положительный TTL
                        // выставлен выше по коду.
                        switch (action)
                        {
                            case PageGuard.Action.Healed:
                                text = healed;
                                raw = Encoding.UTF8.GetBytes(healed);
                                HttpContext.Response.Headers[PageGuard.HeaderName] = "healed";
                                PageStore.Save(skey, healed);
                                HttpContext.Features.Set(shortTtl);
                                break;

                            case PageGuard.Action.Fuse:
                                // Предохранитель открыт: «врут ВСЕ ответы — вероятнее ошиблись мы».
                                // Отдаём то, что дал CUB, копию не подставляем, кеш НЕ выключаем —
                                // иначе весь каталог стал бы некешируемым. Только короткий TTL.
                                HttpContext.Response.Headers[PageGuard.HeaderName] = "fuse";
                                HttpContext.Features.Set(shortTtl);
                                break;

                            case PageGuard.Action.Restored:
                                // Решение владельца: чужая страница не должна попасть в топ даже на
                                // один показ. Отдаём последнюю копию, у которой номер сходился.
                                text = restored;
                                raw = Encoding.UTF8.GetBytes(restored);
                                HttpContext.Response.Headers[PageGuard.HeaderName] = "restored";
                                HttpContext.Features.Set(new StatiCacheEntry(default, false));
                                break;

                            default:
                                HttpContext.Response.Headers[PageGuard.HeaderName] = "mismatch";
                                HttpContext.Features.Set(new StatiCacheEntry(default, false));
                                break;
                        }

                        LogGuard(action, uri, judged.wanted, judged.got, skey);
                    }
                    #endregion

                    string body = null;

                    if (filtering && text != null)
                    {
                        // 🔴 РОВНО ОДНА страница апстрима, один к одному (qdl 2.94). Здесь стоял
                        // добор соседних страниц (N, N+1, N+2) до целевых 20 карточек — он и давал
                        // дубли: хвост нашей страницы N брался с апстримной N+1, а наша N+1
                        // начиналась с той же N+1 с нуля. Замер боевого сервера: 4 / 8 / 5 повторов
                        // из 20 между соседними страницами, владелец видел это в «Ещё» как «каждый
                        // фильм двумя строчками». Короткую страницу лечит КЛИЕНТ (патчи
                        // grid-dedup-build/grid-dedup-next + насос gridPump в qdl.js), а НЕ добор.
                        try { body = RowFilter.Build(text, fconf); }
                        catch { body = null; }   // фильтр не имеет права уронить выдачу каталога
                    }

                    if (body != null)
                    {
                        // ⚠️ ОБЯЗАТЕЛЬНО: CopyResponseHeaders копирует content-length апстрима
                        // (он в белом списке). С переписанным телом это не только битый ответ
                        // клиенту — StaticacheWriter при рассинхроне длины срезает TTL записи
                        // до одной минуты, и «фильтруем раз в 3 часа» превратилось бы в
                        // «фильтруем и ходим в CUB каждую минуту».
                        HttpContext.Response.ContentLength = null;
                        BodyWriter.Write(Encoding.UTF8.GetBytes(body));
                    }
                    else
                        BodyWriter.Write(raw);   // не наша форма / резать нечего / осталось меньше порога
                }
                #endregion
            }
        }
    }
    #endregion


    #region PageHeal (qdl 2.112)
    // Предохранитель общий на процесс: считаем повторы и подтверждённые расхождения в окне
    // PageGuard.SlotMinutes. Само состояние — чистая запись, переходы — чистые функции в
    // PageGuard (иначе файл нельзя было бы линковать в тесты). Здесь только lock вокруг них.
    static readonly object _fuseLock = new();
    static PageGuard.Fuse _fuse;

    // Лог не чаще раза в минуту НА АДРЕС (кап словаря 256), а исход «зритель получил чужую
    // страницу» — всегда: он редкий и это красная линия владельца.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastLog = new();

    /// <summary>
    /// Один повтор мимо кеша CUB. Возвращает тело с ПРАВИЛЬНЫМ номером страницы (или null) и
    /// состояние предохранителя после учёта этого расхождения.
    ///
    /// 🔴 Повтор — это ровно тот же самый адрес плюс уникальный параметр: у CUB кеш на URL, и
    /// обойти его можно только так (§DI, «ловушка диагностики»). Ни page±1, ни склейки соседних
    /// страниц — именно добор давал дубли в «Ещё» и отменён в 2.94 (§DA).
    ///
    /// Заголовки передаём ТЕ ЖЕ (кука viewru + UA клиента), иначе это другой запрос. Повтор
    /// резервируется в потолке ДО похода, чтобы параллельные расхождения не перебирали потолок на
    /// величину параллелизма; «подтверждено» — только если повтор ВЕРНУЛ тело и номер снова чужой
    /// (упавший или выключенный повтор ничего не подтверждает).
    /// </summary>
    async Task<(string healed, bool fuseOpen)> PageHeal(ModuleConf init, string requri, string uri, List<HeadersModel> headers, WebProxy proxy)
    {
        bool mayRetry;

        lock (_fuseLock)
        {
            long slot = PageGuard.SlotOf(DateTime.UtcNow);

            // Клиент уже отвалился — лишний поход к CUB ему не поможет, а слот потолка съест.
            mayRetry = init.pageGuardRetry
                && !HttpContext.RequestAborted.IsCancellationRequested
                && PageGuard.MayRetry(_fuse, slot, init.pageGuardRetryCap, init.pageGuardOpenAfter);

            if (mayRetry)
                _fuse = PageGuard.Note(_fuse, slot, retried: true, confirmed: false);
        }

        string content = null;

        if (mayRetry)
        {
            try
            {
                string bust = PageGuard.BustUrl(requri, Guid.NewGuid().ToString("N").Substring(0, 12));

                // ⚠️ Свой таймаут короче основного: это ДОПОЛНИТЕЛЬНЫЙ поход, и ждать его столько
                // же, сколько основного, значит удвоить худшее время ответа клиенту.
                content = await Http.Get(bust, headers: headers, timeoutSeconds: 8, proxy: proxy).ConfigureAwait(false);
            }
            catch { content = null; }
        }

        // uri берём ИСХОДНЫЙ, без бастера: он и задаёт ожидаемый номер страницы
        bool healed = content != null && PageGuard.Check(uri, content) == PageGuard.Verdict.Match;
        bool confirmed = mayRetry && content != null && !healed;
        bool open;

        lock (_fuseLock)
        {
            // слот считаем заново: повтор мог занять до 8 с и перейти границу окна
            long slot = PageGuard.SlotOf(DateTime.UtcNow);
            _fuse = PageGuard.Note(_fuse, slot, retried: false, confirmed: confirmed);
            open = PageGuard.Open(_fuse, slot, init.pageGuardOpenAfter);
        }

        return (healed ? content : null, open);
    }

    /// <summary>Итог сторожа в stdout — единственный след на промахах, которого не видит аудит прогрева.</summary>
    void LogGuard(PageGuard.Action action, string uri, int? wanted, int? got, string key)
    {
        long now = DateTime.UtcNow.Ticks;

        if (action != PageGuard.Action.MismatchNoCache)
        {
            if (_lastLog.TryGetValue(key, out long prev) && now - prev < TimeSpan.TicksPerMinute)
                return;

            _lastLog[key] = now;

            if (_lastLog.Count > 256)
                foreach (var kv in _lastLog)
                    if (now - kv.Value > TimeSpan.TicksPerMinute * 10)
                        _lastLog.TryRemove(kv.Key, out _);
        }

        PageGuard.Fuse f;
        lock (_fuseLock) f = _fuse;

        Console.WriteLine($"[CubProxy] page guard: {uri} · просили {wanted}, пришла {got} → {action} · " +
            $"окно: повторов {f.retries}, подтверждено {f.confirmed}");
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
    // 🔴 qdl 2.88: было чёрным списком — и `location` с `set-cookie` в него не входили. Любой 30x,
    // который SocketsHttpHandler не разматывает сам (кросс-схемный https→http, превышение лимита
    // прыжков), уезжал клиенту ДОСЛОВНО, с чужим Location: то есть устройство шло на cub.best.
    // Чёрный список для этого негоден в принципе — он защищает только от того, что вспомнили.
    // Теперь белый, по образцу ProxyAPI.Utilities.cs:39-46: пропускаем ровно то, без чего ломается
    // тело и Range, остальное отбрасываем. Заодно 3xx не транслируем вовсе (см. ниже).
    static readonly FrozenSet<string> allowedResponseHeaders = new[]
    {
        "content-type",
        "content-length",
        "content-range",
        "accept-ranges",
        "cache-control",
        "last-modified",
        "expires",
        "vary",
        "age"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    static void CopyResponseHeaders(HttpContext context, HttpResponseMessage responseMessage)
    {
        var response = context.Response;

        // Статус 3xx без Location — сломанный ответ, а с чужим Location — утечка. Апстримные
        // редиректы штатно доедаются самим HttpClient (AllowAutoRedirect), так что сюда доходят
        // только те, за которыми он ходить отказался. Клиенту про них знать нечего.
        int status = (int)responseMessage.StatusCode;
        response.StatusCode = status is >= 300 and < 400 ? StatusCodes.Status502BadGateway : status;

        void UpdateHeaders(HttpHeaders headers)
        {
            if (headers == null)
                return;

            foreach (var header in headers)
            {
                if (!allowedResponseHeaders.Contains(header.Key))
                    continue;

                response.Headers[header.Key] = header.Value.ToArray();
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
