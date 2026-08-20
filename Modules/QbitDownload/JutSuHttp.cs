using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shared.Services;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Транспорт jut.su: UA, CP1251, пулы HttpClient по «выходу», прокси-фолбэк,
// пиннинг выхода и кеш видеоссылок.
//
// Почему не Shared.Services.Http (четыре причины сразу):
//  1. Http.HandlerOrNull подставляет globalproxy по regex URL ДАЖЕ при proxy:null (§BB.5) —
//     для пиннинга это яд: «прямой» запрос страницы ушёл бы через чужой выход.
//  2. AllowAutoRedirect там захардкожен true, а точному поиску нужен 301 БЕЗ follow.
//  3. Нет HttpCompletionOption.ResponseHeadersRead — стрим байтов так не сделать.
//  4. Подмешивает свой UA, а нам нужна ОДНА константа на весь пайплайн.
//
// Разведка и рунбук: E:\Media-server\claude\jut\
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Ответ транспорта. reached и authorized — РАЗНЫЕ вещи, см. инвариант в JutSuParse.</summary>
public sealed class JutResp
{
    public HttpStatusCode status;
    public string body;              // уже раскодировано из windows-1251
    public string location;          // Location при 301/302 в nofollow-режиме
    public string finalUrl;          // после follow — источник канонического слага
    public string exitId;            // 🔥 каким выходом получен — основа пиннинга
    public bool reached;             // дошли до jut.su (маркер валидной страницы), НЕ «залогинены»
    public string error;             // null | NOT_FOUND | SITE_DOWN

    public bool ok => reached && error == null;
}

/// <summary>
/// Прокси не ответил (или отдал не тот сайт) — сходить тем же запросом НАПРЯМУЮ, и наоборот.
///
/// Копия политики Modules/JacRed/Engine/ProxyFallback.cs. Именно копия, а не переиспользование:
/// CSharpEval.cs:247 компилирует модуль только с appReferences + собственной references/,
/// типы других модулей физически не видны.
///
/// Шесть инвариантов (нарушение любого = регресс):
///  1. Ретрай РОВНО ОДИН, циклов нет.
///  2. Прокси нет → ветка ретрая недостижима: второй запрос был бы копией первого.
///  3. Упали ОБА пути — лежит САЙТ, а не прокси: прямой не запоминаем, ретрай глушим на кулдаун.
///  4. Удачный direct запоминаем — следующий запрос сразу мимо прокси.
///  5. Кулдаун клампится снизу (30 с): 0 превратил бы каждый запрос в двойной.
///  6. Состояние в статике: ModInit.conf пересоздаётся целиком на каждый перечит init.conf,
///     поэтому Reset() зовётся из updateConf().
/// </summary>
public static class JutProxyFallback
{
    enum Mode
    {
        /// <summary>Прокси мёртв, прямой путь работает — идём сразу напрямую.</summary>
        Direct,
        /// <summary>Мертвы оба — лежит сайт. Ретрай не делаем.</summary>
        NoRetry
    }

    static readonly ConcurrentDictionary<string, (Mode mode, DateTime until)> _state = new();

    /// <summary>Подмена часов в тестах: кулдаун иначе не промотать.</summary>
    public static Func<DateTime> Now = () => DateTime.Now;

    public static void Reset() => _state.Clear();

    /// <summary>Для диагностики: none | Direct | NoRetry.</summary>
    public static (string mode, DateTime? until) State(string key)
    {
        if (_state.TryGetValue(key, out var s) && Now() < s.until) return (s.mode.ToString(), s.until);
        return ("none", null);
    }

    static Mode? ModeOf(string key)
    {
        if (!_state.TryGetValue(key, out var s)) return null;
        if (Now() < s.until) return s.mode;
        _state.TryRemove(key, out _);
        return null;
    }

    public static async Task<T> Run<T>(
        string key,
        Func<WebProxy> getProxy,
        Func<WebProxy, Task<T>> send,
        Func<T, bool> ok,
        bool enabled,
        int cooldownSeconds,
        Action<string, string> log = null) where T : class
    {
        if (!enabled)
        {
            // выключили на лету — вердикт не должен залипнуть до конца кулдауна
            _state.TryRemove(key, out _);
            return await send(getProxy?.Invoke());
        }

        var mode = ModeOf(key);

        if (mode == Mode.Direct)
        {
            T direct = await send(null);
            if (!ok(direct)) _state.TryRemove(key, out _);   // и прямой сдох — вернёмся к прокси
            return direct;
        }

        WebProxy proxy = getProxy?.Invoke();
        T res = await send(proxy);
        if (ok(res)) return res;

        // ⚠️ Инвариант 2: без прокси второй запрос — побайтовая копия первого.
        if (proxy == null || mode == Mode.NoRetry) return res;

        T retry = await send(null);
        bool good = ok(retry);

        int cd = Math.Max(30, cooldownSeconds);
        _state[key] = (good ? Mode.Direct : Mode.NoRetry, Now().AddSeconds(cd));

        log?.Invoke(key, good
            ? $"прокси не ответил, прямой путь ok — идём мимо прокси {cd}с"
            : $"прокси и прямой путь оба пусты — лежит сайт, ретрай выключен на {cd}с");

        return good ? retry : (res ?? retry);
    }
}

/// <summary>Кешированная видеоссылка. Токен самоописывающий — переживает рестарт (см. MakeToken).</summary>
public sealed class JutLink
{
    public string token;
    public string slug;
    public int season;
    public int ep;
    public string kind;
    public int quality;          // фактически выбранное качество
    public string url;           // https://rNNNNNN.yandexwebcache.org/.../ep.1080.<hex>.mp4?hash=...
    public string exitId;        // 🔥 пин: каким выходом получена страница
    public string ua;            // UA, которым получена (смена UA инвалидирует ссылку)
    public DateTime issued;
    public int reissues;
    public int[] available = Array.Empty<int>();
    public double duration;
    public double outro;
    // Границы опенинга со страницы серии. «Есть интро» — только по introEnd (introStart=0 валиден).
    public double introStart;
    public double introEnd;
    public string error;         // NOT_AUTHORIZED | NOT_FOUND | SITE_DOWN | PARSE

    public bool hasIntro => introEnd > 0 && introEnd > introStart;
}

public static class JutNet
{
    #region UA и кодировка

    // 🔥 ОДНА константа UA на весь пайплайн (страница + байты у CDN).
    // hash в CDN-ссылке криптографически связан с UA, которым запрошена HTML-страница
    // (доказано пробами: страница под Firefox-UA → байты только под Firefox-UA).
    // Рассинхрон = 403 на всём видео. Смена значения инвалидирует ВСЕ выданные ссылки.
    public const string DefaultUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";

    public static string Ua
    {
        get
        {
            string v = ModInit.conf?.jutUserAgent;
            return string.IsNullOrWhiteSpace(v) ? DefaultUa : v.Trim();
        }
    }

    static readonly Encoding Cp1251 = ResolveCp1251();

    static Encoding ResolveCp1251()
    {
        try
        {
            // Core/Program.cs уже регистрирует провайдер, но модуль должен быть самодостаточен
            // (и линкуется в тесты, где Program не выполняется). Регистрация идемпотентна.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
        catch { return Encoding.UTF8; }   // деградация вместо падения
    }

    public static string Host => (ModInit.conf?.jutHost ?? "https://jut.su").TrimEnd('/');

    #endregion

    #region пулы HttpClient по выходу

    // Ключ пулов — exitId («direct» или адрес прокси). Так пиннинг выхода получается бесплатно.
    static readonly ConcurrentDictionary<string, HttpClient> _api = new();
    static readonly ConcurrentDictionary<string, HttpClient> _media = new();
    static readonly ConcurrentDictionary<string, HttpClient> _noRedirect = new();

    static SemaphoreSlim _gate;
    static SemaphoreSlim _bgGate;
    static readonly object _gateLock = new();

    /// <summary>
    /// Признак «этот запрос — фоновая работа». AsyncLocal, а не параметр: EnsureLink зовётся
    /// из воркера скачивания транзитивно через Resolve, и протаскивать флаг пришлось бы
    /// через четыре сигнатуры. AsyncLocal течёт по продолжениям — ровно то, что нужно.
    /// ⚠️ Никогда не ставить из контроллера: интерактивный запрос уедет в фоновый гейт.
    /// </summary>
    static readonly AsyncLocal<bool> _bg = new();

    /// <summary>
    /// Пометить текущий асинхронный поток фоновым. Оборачивать: воркер скачивания,
    /// тик слежения, очередь постеров, прогрев кеша тайтлов.
    /// </summary>
    public static IDisposable BackgroundScope()
    {
        bool prev = _bg.Value;
        _bg.Value = true;
        return new BgScope(prev);
    }

    sealed class BgScope : IDisposable
    {
        readonly bool _prev;
        public BgScope(bool prev) { _prev = prev; }
        public void Dispose() { _bg.Value = _prev; }
    }

    /// <summary>
    /// Гейт запросов к jut.su. Меняется рестартом (пересоздавать семафор на лету небезопасно).
    ///
    /// 🔥 Гейтов ДВА. Раньше один SemaphoreSlim(jutMaxConcurrent=3) обслуживал всё сразу:
    /// каталог, карточку, resolve плеера, воркер скачивания, тик слежения и апгрейд постеров.
    /// Значит фоновая качалка отбирала слоты у интерактивных запросов, и открытие карточки
    /// jut.su вставало в очередь за скачиванием — ровно жалоба владельца «во время скачивания
    /// приложение подлагивает». Один запрос к сайту стоит ~1.1 с (замер /qdl/jut/diag),
    /// так что три занятых слота — это секунды ожидания на карточке.
    /// </summary>
    static SemaphoreSlim Gate()
    {
        if (_bg.Value)
        {
            if (_bgGate != null) return _bgGate;
            lock (_gateLock) return _bgGate ??= new SemaphoreSlim(Math.Max(1, ModInit.conf?.jutBgConcurrent ?? 1));
        }
        if (_gate != null) return _gate;
        lock (_gateLock) return _gate ??= new SemaphoreSlim(Math.Max(1, ModInit.conf?.jutMaxConcurrent ?? 3));
    }

    public static string ExitId(WebProxy p) => p?.Address == null ? "direct" : p.Address.ToString();

    static HttpClient Build(WebProxy proxy, bool follow, TimeSpan? total, TimeSpan connect)
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = follow,          // follow нужен для 301-алиасов слагов
            MaxAutomaticRedirections = 5,
            ConnectTimeout = connect,
            UseCookies = false,                  // куки ставим заголовком сами — и ТОЛЬКО на jut.su
            UseProxy = proxy != null,
            Proxy = proxy,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(h) { Timeout = total ?? Timeout.InfiniteTimeSpan };
    }

    static HttpClient Api(WebProxy p) => _api.GetOrAdd(ExitId(p),
        _ => Build(p, follow: true,
                   total: TimeSpan.FromSeconds(Math.Max(5, ModInit.conf?.jutTimeoutSec ?? 20)),
                   connect: TimeSpan.FromSeconds(8)));

    static HttpClient NoRedirect(WebProxy p) => _noRedirect.GetOrAdd(ExitId(p),
        _ => Build(p, follow: false,
                   total: TimeSpan.FromSeconds(Math.Max(5, ModInit.conf?.jutTimeoutSec ?? 20)),
                   connect: TimeSpan.FromSeconds(8)));

    /// <summary>
    /// Медиа-клиент. ⚠️ Timeout.InfiniteTimeSpan ОБЯЗАТЕЛЕН: дефолтные 100 с HttpClient.Timeout
    /// режут чтение ТЕЛА, и ResponseHeadersRead от этого не спасает (§AL, грабля 4) — просмотр
    /// рвался бы на 100-й секунде, а скачивание 489 МиБ сжигало бы ретраи. Обрыв клиента ловим
    /// через HttpContext.RequestAborted, а не таймаутом.
    /// </summary>
    public static HttpClient Media(string exitId) => _media.GetOrAdd(exitId ?? "direct",
        id => Build(ProxyFor(id), follow: true, total: null, connect: TimeSpan.FromSeconds(10)));

    static WebProxy ProxyFor(string exitId)
    {
        if (string.IsNullOrEmpty(exitId) || exitId == "direct") return null;
        try { return new WebProxy(new Uri(exitId)); } catch { return null; }
    }

    static ProxyManager Pm() => new ProxyManager("jutsu", ModInit.conf?.jutProxy ?? new JutProxyConf());

    static bool FallbackEnabled => ModInit.conf?.jutProxyFallbackEnable ?? true;

    /// <summary>
    /// Выбор выхода. useproxy:false + fallbackEnable → основной путь ПРЯМОЙ, а прокси включается
    /// сам, когда прямой перестал доходить (это и просил владелец).
    /// </summary>
    static Func<WebProxy> ProxyPicker()
    {
        var jp = ModInit.conf?.jutProxy;
        bool haveList = jp?.proxy?.list is { Length: > 0 };
        if (jp?.useproxy == true) return () => Pm().Get();
        // прокси не основной: отдаём его только как «второй шанс» внутри JutProxyFallback
        if (FallbackEnabled && haveList) return () => null;   // первый заход — прямой
        return null;
    }

    #endregion

    #region запросы

    static void ApplyHeaders(HttpRequestMessage req, bool withCookies)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        if (!withCookies) return;

        // ⚠️ Куки — ТОЛЬКО на jut.su. На CDN они не нужны, а dle_password на чужом хосте — утечка.
        string uid = ModInit.conf?.jutUserId, pwd = ModInit.conf?.jutPassword;
        if (!string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(pwd))
            req.Headers.TryAddWithoutValidation("Cookie",
                "dle_user_id=" + uid.Trim() + "; dle_password=" + pwd.Trim());
    }

    public static bool HasCookies =>
        !string.IsNullOrWhiteSpace(ModInit.conf?.jutUserId) &&
        !string.IsNullOrWhiteSpace(ModInit.conf?.jutPassword);

    static async Task<JutResp> Send(string url, string form, Func<string, bool> reached,
                                    bool follow, WebProxy proxy, CancellationToken ct)
    {
        var r = new JutResp { exitId = ExitId(proxy) };
        try
        {
            using var req = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, url);
            ApplyHeaders(req, withCookies: true);
            if (form != null)
                req.Content = new StringContent(form, Encoding.ASCII, "application/x-www-form-urlencoded");

            var client = follow ? Api(proxy) : NoRedirect(proxy);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

            r.status = resp.StatusCode;
            r.finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            if (resp.Headers.Location != null)
                r.location = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location.ToString()
                    : new Uri(new Uri(Host), resp.Headers.Location).ToString();

            // 404 — честный ответ сайта (у jut.su он с телом ~14 КБ), а не «не дошли»
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                r.reached = true;
                r.error = "NOT_FOUND";
                return r;
            }

            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                r.reached = true;                    // редирект — сайт отвечает
                return r;
            }

            byte[] raw = await resp.Content.ReadAsByteArrayAsync(ct);
            r.body = Cp1251.GetString(raw);          // ⚠️ сайт в windows-1251
            r.reached = reached == null ? resp.IsSuccessStatusCode : reached(r.body);
            if (!r.reached) r.error = "SITE_DOWN";
            return r;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            r.error = "SITE_DOWN";
            return r;
        }
        catch (Exception ex)
        {
            r.error = "SITE_DOWN";
            Log("net", ex.Message);
            return r;
        }
    }

    /// <summary>
    /// GET страницы jut.su через фолбэк-политику.
    /// <paramref name="reached"/> обязан отвечать «дошли ли до сайта», а НЕ «залогинены ли мы»:
    /// иначе протухшие куки сожгут бюджет прокси-выходов за один тик.
    /// </summary>
    public static Task<JutResp> Get(string url, Func<string, bool> reached = null,
                                    bool follow = true, CancellationToken ct = default)
        => Run(url, null, reached, follow, ct);

    /// <summary>POST form-urlencoded (AJAX каталога и точный поиск).</summary>
    public static Task<JutResp> PostForm(string url, string form, Func<string, bool> reached = null,
                                         bool follow = true, CancellationToken ct = default)
        => Run(url, form ?? "", reached, follow, ct);

    static async Task<JutResp> Run(string url, string form, Func<string, bool> reached,
                                   bool follow, CancellationToken ct)
    {
        var gate = Gate();
        await gate.WaitAsync(ct);
        try
        {
            var pm = Pm();
            var resp = await JutProxyFallback.Run<JutResp>(
                key: "jutsu",
                getProxy: ProxyPicker() ?? (ModInit.conf?.jutProxy?.proxy?.list is { Length: > 0 }
                                            ? () => pm.Get() : null),
                send: p => Send(url, form, reached, follow, p, ct),
                ok: x => x != null && x.reached,
                enabled: FallbackEnabled,
                cooldownSeconds: ModInit.conf?.jutProxyFallbackCooldownSeconds ?? 300,
                log: Log);

            if (resp != null)
            {
                if (resp.reached) { try { pm.Success(); } catch { } }
                else { try { pm.Refresh(); } catch { } }
            }

            // ── Пассивный хелс-чек (HealthState.cs) ──
            // Единственный выход ВСЕХ запросов к jut.su, поэтому наблюдение стоит здесь и
            // больше нигде. Критерий — reached, а не HTTP-код: 404 сайт отдаёт с телом (Send
            // ставит reached=true), это ответ живого сайта, а заглушку Cloudflare отсеивает
            // предикат reached — она не содержит маркеров страницы.
            try
            {
                if (resp != null && resp.reached) HealthState.Ok(HealthState.Ids.JutHost);
                else HealthState.Fail(HealthState.Ids.JutHost, "сайт не ответил");

                // Деградация: работаем, но не своим путём. Снятие обязательно — иначе ⚠️ залипнет.
                var (mode, _) = JutProxyFallback.State("jutsu");
                if (mode == "Direct") HealthState.Degraded(HealthState.Ids.JutHost, "прокси не отвечает — идём напрямую");
                else if (mode == "NoRetry") HealthState.Degraded(HealthState.Ids.JutHost, "падали оба пути — прокси и прямой");
                else HealthState.ClearDegraded(HealthState.Ids.JutHost);
            }
            catch { }

            return resp ?? new JutResp { error = "SITE_DOWN" };
        }
        finally { gate.Release(); }
    }

    internal static void Log(string tag, string msg)
        => Console.WriteLine("[QbitDownload] jut/" + tag + ": " + msg);

    #endregion

    #region самоописывающий токен стрима

    // 🔥 Токен обязан быть ВОССТАНОВИМЫМ, а не просто ключом словаря.
    // Иначе рестарт контейнера (частый и дорогой — Roslyn-компиляция) или эвикция по капу
    // убивают активный двухчасовой просмотр: плеер шлёт Range по мёртвому токену → 404,
    // и ни один ретрай не помогает.
    //
    // Формат: v1.<base64url(slug|season|ep|kind|quality)>.<base64url(HMAC-SHA256[0..11])>
    // Подпись нужна, чтобы токен нельзя было подделать в произвольный внешний URL.

    static readonly Regex _rxToken = new(@"^v1\.(?<p>[A-Za-z0-9_-]{1,200})\.(?<s>[A-Za-z0-9_-]{8,32})$",
                                         RegexOptions.Compiled);
    static byte[] _tokenKey;
    static readonly object _keyLock = new();

    static byte[] TokenKey()
    {
        if (_tokenKey != null) return _tokenKey;
        lock (_keyLock)
        {
            if (_tokenKey != null) return _tokenKey;
            // Ключ обязан пережить рестарт, иначе самоописывающий токен теряет смысл.
            try
            {
                string dir = JutDataDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "token.key");
                if (File.Exists(path))
                {
                    byte[] k = File.ReadAllBytes(path);
                    if (k.Length >= 32) return _tokenKey = k;
                }
                byte[] gen = RandomNumberGenerator.GetBytes(48);
                File.WriteAllBytes(path, gen);
                return _tokenKey = gen;
            }
            catch
            {
                // Диск недоступен — производный ключ. Стабилен, пока не меняли куки.
                using var sha = SHA256.Create();
                return _tokenKey = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    "jutsu-token|" + (ModInit.conf?.jutUserId ?? "") + "|" + (ModInit.conf?.jutPassword ?? "")));
            }
        }
    }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] UnB64(string s)
    {
        string t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }

    static string Sign(string payload)
    {
        using var h = new HMACSHA256(TokenKey());
        return B64(h.ComputeHash(Encoding.UTF8.GetBytes(payload))).Substring(0, 16);
    }

    /// <summary>Токен СТАБИЛЕН для (slug, season, ep, kind, quality): клиентский URL не меняется при перевыпуске.</summary>
    public static string MakeToken(string slug, int season, int ep, string kind, int quality)
    {
        string payload = string.Join("|", slug, season.ToString(CultureInfo.InvariantCulture),
                                     ep.ToString(CultureInfo.InvariantCulture),
                                     kind ?? "episode", quality.ToString(CultureInfo.InvariantCulture));
        string p = B64(Encoding.UTF8.GetBytes(payload));
        return "v1." + p + "." + Sign(p);
    }

    /// <summary>Разбор токена обратно. null = подделка/мусор.</summary>
    public static JutLink ParseToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var m = _rxToken.Match(token);
        if (!m.Success) return null;

        string p = m.Groups["p"].Value;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Sign(p)), Encoding.ASCII.GetBytes(m.Groups["s"].Value)))
            return null;

        string[] a;
        try { a = Encoding.UTF8.GetString(UnB64(p)).Split('|'); } catch { return null; }
        if (a.Length < 5) return null;
        if (!JutSuParse.IsValidSlug(a[0])) return null;

        return new JutLink
        {
            token = token,
            slug = a[0],
            season = int.TryParse(a[1], out int s) ? s : 1,
            ep = int.TryParse(a[2], out int e) ? e : 1,
            kind = a[3],
            quality = int.TryParse(a[4], out int q) ? q : 0
        };
    }

    #endregion

    #region кеш ссылок и пиннинг

    static readonly ConcurrentDictionary<string, JutLink> _links = new();
    static readonly ConcurrentDictionary<string, SemaphoreSlim> _linkGates = new();
    const int LinksCap = 512;

    /// <summary>Сброс при смене UA/конфига: hash вяжет UA, старые ссылки становятся мусором.</summary>
    public static void ResetLinks()
    {
        _links.Clear();
        _linkGates.Clear();
    }

    public static (int cached, int oldestAgeSec) LinksState()
    {
        var now = DateTime.UtcNow;
        int oldest = 0;
        foreach (var l in _links.Values)
        {
            int age = (int)(now - l.issued).TotalSeconds;
            if (age > oldest) oldest = age;
        }
        return (_links.Count, oldest);
    }

    static string EpisodeUrl(string slug, int season, int ep, string kind)
    {
        string k = string.IsNullOrEmpty(kind) ? "episode" : kind;
        // Фильмы/OVA всегда в корне слага, без season-N (проверено разведкой).
        if (k != "episode") return $"{Host}/{slug}/{k}-{ep}.html";
        return season > 1
            ? $"{Host}/{slug}/season-{season}/episode-{ep}.html"
            : $"{Host}/{slug}/season-1/episode-{ep}.html";
    }

    /// <summary>
    /// Ссылка на байты для токена. Восстанавливает параметры из самого токена, поэтому переживает
    /// рестарт. Single-flight: при протухании 2-3 параллельных соединения плеера не должны
    /// одновременно перезагружать страницу (это ещё и ×N расход бюджета выходов при фолбэке).
    /// </summary>
    public static async Task<JutLink> EnsureLink(string token, bool force = false, CancellationToken ct = default)
    {
        var seed = ParseToken(token);
        if (seed == null) return null;

        int ttl = Math.Max(30, ModInit.conf?.jutLinkTtlSec ?? 240);
        if (!force && _links.TryGetValue(token, out var hit)
            && hit.error == null
            && hit.ua == Ua                                  // смена UA обесценивает hash
            && (DateTime.UtcNow - hit.issued).TotalSeconds < ttl)
            return hit;

        var gate = _linkGates.GetOrAdd(token, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Пока ждали — кто-то мог уже перевыпустить
            if (!force && _links.TryGetValue(token, out var again)
                && again.error == null && again.ua == Ua
                && (DateTime.UtcNow - again.issued).TotalSeconds < ttl)
                return again;

            _links.TryGetValue(token, out var prev);
            var fresh = await Resolve(seed, prev?.exitId, ct);
            fresh.token = token;
            fresh.reissues = (prev?.reissues ?? 0) + (force ? 1 : 0);

            if (_links.Count > LinksCap) PruneLinks(ttl);
            _links[token] = fresh;
            return fresh;
        }
        finally { gate.Release(); }
    }

    static void PruneLinks(int ttl)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _links)
            if ((now - kv.Value.issued).TotalSeconds > ttl)
            {
                _links.TryRemove(kv.Key, out _);
                _linkGates.TryRemove(kv.Key, out _);
            }
    }

    /// <summary>Парсит страницу серии и выбирает качество. pinExit — держаться того же выхода.</summary>
    static async Task<JutLink> Resolve(JutLink seed, string pinExit, CancellationToken ct)
    {
        var link = new JutLink
        {
            slug = seed.slug, season = seed.season, ep = seed.ep,
            kind = seed.kind, ua = Ua, issued = DateTime.UtcNow
        };

        string url = EpisodeUrl(seed.slug, seed.season, seed.ep, seed.kind);
        JutResp resp;

        // Пин актуален только когда фолбэк реально увёл нас на прокси; при работе со своего IP
        // exitId == "direct" и ветка ниже эквивалентна обычному пути.
        if (!string.IsNullOrEmpty(pinExit) && pinExit != "direct" && ExitStillConfigured(pinExit))
            resp = await Send(url, null, JutSuParse.ReachedEpisode, follow: true, ProxyFor(pinExit), ct);
        else
            resp = await Get(url, JutSuParse.ReachedEpisode, follow: true, ct);

        if (resp == null || !resp.reached)
        {
            link.error = resp?.error == "NOT_FOUND" ? "NOT_FOUND" : "SITE_DOWN";
            return link;
        }

        var page = JutSuParse.ParseEpisode(resp.body);
        if (!page.ok)
        {
            link.error = page.error ?? "PARSE";
            if (link.error == "NOT_AUTHORIZED") AuthAlarm(false);
            return link;
        }
        AuthAlarm(true);

        var best = JutSuParse.PickQuality(page.videos, seed.quality > 0
                                          ? seed.quality
                                          : (ModInit.conf?.jutPreferredQuality ?? 0));
        if (best == null) { link.error = "PARSE"; return link; }

        link.url = best.url;
        link.quality = best.res;
        link.available = page.videos.Select(v => v.res).ToArray();
        link.duration = page.duration;
        link.outro = page.outro;
        link.introStart = page.introStart;
        link.introEnd = page.introEnd;
        link.exitId = resp.exitId;
        JutSegCounters.Note(page.hasIntro);
        return link;
    }

    static bool ExitStillConfigured(string exitId)
    {
        var list = ModInit.conf?.jutProxy?.proxy?.list;
        return list != null && list.Any(x => !string.IsNullOrEmpty(x) && exitId.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region канарейка авторизации

    // Протухшие куки глушат слежение МОЛЧА (reached=true, authorized=false). Один WARN в лог
    // никто не прочитает, поэтому тревога — на ПЕРЕХОДЕ состояния, один раз (антиспам §AZ.1).
    static bool? _authState;
    static DateTime _authWarn = DateTime.MinValue;
    public static bool? AuthOk => _authState;

    static void AuthAlarm(bool ok)
    {
        // Наблюдение — ДО гейта changed: тревога в лог нужна раз на переходе, а хелс-чеку нужна
        // свежая отметка времени на каждом резолве (у _authState её нет вовсе, и «✅ куки приняты»
        // мог висеть сколь угодно долго после того, как они протухли).
        try
        {
            if (ok) HealthState.Ok(HealthState.Ids.JutAuth);
            else HealthState.Fail(HealthState.Ids.JutAuth, "куки протухли — сайт подменяет ссылки");
        }
        catch { }

        bool changed = _authState != ok;
        _authState = ok;
        if (!changed) return;

        if (!ok)
        {
            if ((DateTime.UtcNow - _authWarn).TotalMinutes < 15) return;
            _authWarn = DateTime.UtcNow;
            Log("auth", "❌ куки jut.su не работают (страница отдаёт pixel.png). " +
                        "Обновить jutUserId/jutPassword в init.conf — см. claude/jut/03-runbook.md");
        }
        else Log("auth", "✅ авторизация на jut.su восстановлена");
    }

    #endregion

    #region пути и идентичность

    public static string JutDataDir()
        => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "jut");

    /// <summary>
    /// Псевдо-infohash не-торрентного источника: 40 hex, проходит ValidHash → карточка
    /// бесплатно живёт в общем разделе «Загрузки» (/qdl/list, /qdl/stream, /qdl/episodes...).
    /// ⚠️ links/&lt;hash&gt;.json для jut НЕ создавать никогда — на этом держится изоляция
    /// от торрентной охоты (WatchAdd без него отвечает "no link"). См. JutSuWatch.cs.
    /// </summary>
    public static string Hash(string slug)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes("jutsu:" + slug))).ToLowerInvariant();
    }

    #endregion
}
