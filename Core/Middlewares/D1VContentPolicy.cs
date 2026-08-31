using Microsoft.AspNetCore.Http;
using Shared;
using System;
using System.Threading.Tasks;

namespace Core.Middlewares;

// ── D1Vision: Content-Security-Policy на отдаваемые документы (qdl 2.88) ───────────────────────
//
// Зачем. Инвариант «клиент ходит ТОЛЬКО на наш сервер» до сих пор держался ВЫРЕЗАНИЕМ источников
// в самой странице: lampainit-invc.js гасит зеркала/гео/сокет/аккаунт/скринсейвер, AppPatch режет
// колокольчик, преролл, shots, «Релизы». Работает — но это список известных мест. Стоит апстриму
// добавить новый putScriptAsync, или нашему якорю уехать при обновлении вендора, и утечка вернётся
// молча: заметить её можно только замером. CSP закрывает КЛАСС целиком, а не перечисленные случаи.
//
// 🔴 Это НЕ защита от XSS. 'unsafe-inline' и 'unsafe-eval' здесь обязательны (см. ниже), а с ними
// CSP от инъекций не спасает. Он тут ровно одно: СЕТЕВОЙ БЕЛЫЙ СПИСОК ИСТОЧНИКОВ. Не надо потом
// «доводить до ума», убирая unsafe — сломается загрузка страницы, а не улучшится безопасность.
//
// Почему middleware, а не заголовок в контроллере: Staticache на кэш-хите сам формирует ответ
// (SendFileAsync + свои заголовки) и произвольные заголовки контроллера НЕ реплеит — CSP пропал бы
// на 5 минут из каждых 5. Поэтому стоим до UseStaticache, рядом с периметром.
//
// Почему не в Caddy: LAN ходит на :9118 напрямую, мимо edge. Вторая копия политики разошлась бы
// с первой — тот же класс расхождений, что фикстуры паритета ловили на 127.0.0.2.
public class D1VContentPolicy
{
    readonly RequestDelegate _next;

    public D1VContentPolicy(RequestDelegate next) => _next = next;

    // Что обязано быть разрешено и почему (проверено по отдаваемой странице):
    //
    //   script-src 'unsafe-inline'  — в index.html ТРИ инлайн-блока, и без них белый экран:
    //                                 два document.write (css/app.css + preload app.min.js) и весь
    //                                 загрузчик putScript. Тело index.html сервер генерит на лету,
    //                                 так что nonce теоретически возможен — но он не переживёт
    //                                 Staticache, который отдаёт сохранённое тело со старым nonce.
    //   script-src 'unsafe-eval'    — RCH: Core/plugins/invc-rch_nws.js исполняет eval(data),
    //                                 присланный сервером по /nws. Без него мертвы RCH-балансеры.
    //   style-src  'unsafe-inline'  — lampainit-invc.js вставляет <style> через DOM (сокрытие
    //                                 промо CUB, замков, рекламы). Без разрешения весь спрятанный
    //                                 UI вернётся на экран — видимый откат работы 2.84/2.87.
    //   img-src    img.youtube.com  — превью трейлеров. Осознанное исключение владельца.
    //   connect-src ws: wss:        — сокет /nws (RCH).
    //   media-src / worker-src blob: — MSE-плеер и потоки в браузерном контуре.
    //
    // frame-src 'self': вложенный iframe youtube.com/embed живёт не здесь, а внутри НАШЕЙ страницы
    // /lampa-main/youtube.html — у неё отдельный, более широкий CSP (см. YoutubeBridgePolicy).
    //
    // 🔴 {xsmart} — не украшение, без него раздел XSMART мёртв. Его плагин, каталог и картинки
    // отдаёт ОТДЕЛЬНЫЙ контейнер (xsmart-proxy) и в локалке — на своём порту 9140. Для CSP это
    // ДРУГОЙ origin: 'self' сверяет схему+хост+ПОРТ, так что script-src 'self' режет
    // <script src="http://192.168.87.24:9140/xsmart/xsmart.js"> начисто, а на экране это выглядит
    // как «просто пропал пункт меню» (поймал xsmartcheck при первом включении CSP).
    // Снаружи адрес тот же, что у Lampa (Caddy проксирует /xsmart/*) — там хватает 'self'.
    // Развилка ниже намеренно повторяет isLanHost() из lampainit-invc.js: одно понятие «дом».
    const string DefaultPolicy =
        "default-src 'self'; " +
        "script-src 'self' {xsmart} 'unsafe-inline' 'unsafe-eval'; " +
        "style-src 'self' {xsmart} 'unsafe-inline'; " +
        "img-src 'self' {xsmart} data: blob: https://img.youtube.com; " +
        "media-src 'self' {xsmart} blob:; " +
        "connect-src 'self' {xsmart} ws: wss:; " +
        "frame-src 'self'; " +
        "font-src 'self'; " +
        "manifest-src 'self'; " +
        "worker-src 'self' blob:; " +
        "frame-ancestors 'none'";

    // Страница-мост трейлеров: она НАША по origin, но по делу обязана тянуть чужой iframe_api и
    // поднимать iframe youtube.com/embed. Отдельный документ — отдельная политика; главная от
    // этого не расширяется.
    const string YoutubeBridgePolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://www.youtube.com; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https://img.youtube.com https://i.ytimg.com; " +
        "frame-src https://www.youtube.com https://www.youtube-nocookie.com; " +
        "connect-src 'self'";

    public Task Invoke(HttpContext httpContext)
    {
        var conf = SafeConf();

        if (conf is not { enable: true })
            return _next(httpContext);

        // Ставим на ОТВЕТ, а не на запрос: до вызова следующего звена мы ещё не знаем, будет ли это
        // документ. Дешевле решить по пути — html отдают ровно две ручки.
        if (IsDocument(httpContext.Request.Path.Value))
        {
            string policy = IsYoutubeBridge(httpContext.Request.Path.Value)
                ? (conf.cspYoutube ?? YoutubeBridgePolicy)
                : (conf.csp ?? DefaultPolicy);

            policy = ExpandXsmart(policy, httpContext);

            if (!string.IsNullOrWhiteSpace(policy))
            {
                // Report-Only — режим обкатки: браузер только жалуется в консоль, ничего не режет.
                // Первое боевое включение без него почти наверняка что-нибудь уронит.
                httpContext.Response.Headers[conf.cspReportOnly
                    ? "Content-Security-Policy-Report-Only"
                    : "Content-Security-Policy"] = policy;
            }
        }

        return _next(httpContext);
    }

    /// <summary>
    /// Подставить origin контейнера xsmart-proxy вместо плейсхолдера {xsmart}.
    /// Дома это тот же хост на порту 9140, снаружи — наш же адрес (Caddy), там подставлять нечего.
    /// Правило «дом» — ровно как isLanHost() в lampainit-invc.js, чтобы понятие было одно.
    /// </summary>
    static string ExpandXsmart(string policy, HttpContext ctx)
    {
        if (policy == null || policy.IndexOf("{xsmart}", StringComparison.Ordinal) < 0)
            return policy;

        string host = ctx.Request.Host.Host;
        string origin = IsLanHost(host) ? $"http://{host}:9140" : string.Empty;

        return policy.Replace("{xsmart}", origin).Replace("  ", " ");
    }

    static bool IsLanHost(string host)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        if (host == "localhost" || host == "127.0.0.1" || host == "::1")
            return true;

        if (host.StartsWith("192.168.", StringComparison.Ordinal) || host.StartsWith("10.", StringComparison.Ordinal))
            return true;

        // 172.16.0.0/12
        if (host.StartsWith("172.", StringComparison.Ordinal))
        {
            int dot = host.IndexOf('.', 4);
            if (dot > 4 && int.TryParse(host.AsSpan(4, dot - 4), out int b) && b >= 16 && b <= 31)
                return true;
        }

        return false;
    }

    // Документы: корень (Lampa) и .html из вендоренного фронта. На картинки, JSON, m3u8 и прочее
    // CSP не действует в принципе — вешать его туда смысла нет.
    static bool IsDocument(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return true;

        return path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsYoutubeBridge(string path)
        => path != null && path.EndsWith("/youtube.html", StringComparison.OrdinalIgnoreCase);

    static Shared.Models.AppConf.D1vConf SafeConf()
    {
        try { return CoreInit.conf?.d1v; } catch { return null; }
    }
}
