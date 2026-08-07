using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Services;
using System;
using System.IO;

namespace QbitDownload;

// ── Витрина расширений CUB локально (qdl 2.17; карта — E:\Media-server\claude\11) ──
// Темы и скринсейверы Lampa подключаются НЕ через Lampa.Reguest, а нативно браузером:
//   Theme.set():        <link rel="stylesheet" href="<cub>/extensions/<id>?token=...">
//   Screensaver.link:   тот же вид URL, отдаётся MP4
// (app.min.js:33656-33662 и 33770 вендора). request_before на них не стреляет — cubproxy.js
// бессилен. Хост в бандле сервер уже переписывает на наш (`{host}/cub/red`), но CubProxy отдавал
// 500 на 302-редирект cub → cdn.cub.bz, поэтому НЕ РАБОТАЛА НИ ОДНА тема/скринсейвер; премиумные
// сверх того гейтились токеном. Разведка показала: с CDN всё отдаётся БЕЗ токена — значит держим
// копию у себя (scripts/vendor-cub-extensions.ps1 → том cubExtPath) и отвечаем сами.
//
// Маршруты специфичнее, чем catch-all CubProxy ("cub/{*suffix}"), поэтому перехватывают его
// на этих двух путях; всё остальное cub-API продолжает ходить через прокси как раньше.
public partial class QbitController : BaseController
{
    static string CubExtDir => ModInit.conf?.cubExtPath ?? "/lampac/wwwroot/cubext";

    // Локальный каталог витрины: у всех элементов premium=0 (контент лежит у нас — проверять
    // нечего), image/link указывают на наш хост. Нет файла → прозрачно уходим в CubProxy.
    [HttpGet, AllowAnonymous]
    [Route("cub/red/api/extensions/list")]
    [Route("cub/{mirror}/api/extensions/list")]
    public ActionResult CubExtensionsList(string mirror)
    {
        string file = Path.Combine(CubExtDir, "list.json");
        if (!System.IO.File.Exists(file))
            return CubExtProxyFallback("api/extensions/list");

        // одноаргументная форма: ключ кеша = путь (двухаргументная включает механику
        // plugins/override/<name>, которая тут только запутывала бы)
        string json = FileCache.ReadAllText(file).Replace("{localhost}", host);

        // Каталог меняется только прогоном вендор-скрипта, но URL без версии — час это компромисс
        // между «не долбить сервер» и «увидеть обновление витрины без ручной чистки кеша».
        HttpContext.Response.Headers["Cache-Control"] = "public,max-age=3600";
        return ContentTo(json, "application/json; charset=utf-8");
    }

    // Ассет элемента витрины: CSS темы или MP4 скринсейвера. Range обязателен — MP4 крутится
    // в <video> заставки. Имя файла = id, содержимое неизменно → immutable-год.
    [HttpGet, AllowAnonymous]
    [Route("cub/red/extensions/{id}")]
    [Route("cub/{mirror}/extensions/{id}")]
    public ActionResult CubExtensionAsset(string mirror, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NotFound();

        if (!IsExtensionId(id))
            return CubExtProxyFallback($"extensions/{id}");   // не наш формат — пусть решает upstream

        foreach (var (sub, ext, mime) in new[]
        {
            ("theme", "css", "text/css; charset=utf-8"),
            ("screensaver", "mp4", "video/mp4")
        })
        {
            string full = ConfinedCombine(CubExtDir, $"{sub}/{id}.{ext}");
            if (full != null && System.IO.File.Exists(full))
            {
                HttpContext.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
                return PhysicalFile(full, mime, enableRangeProcessing: true);
            }
        }

        return CubExtProxyFallback($"extensions/{id}");
    }

    // id элемента витрины — только цифры (в каталоге это int). Всё прочее (в т.ч. попытки
    // подсунуть путь) уходит в фолбэк, до файловой системы не доходя. Покрыто тестами.
    public static bool IsExtensionId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 12)
            return false;

        foreach (char c in id)
            if (c < '0' || c > '9')
                return false;

        return true;
    }

    // Фолбэк, когда вендор пуст/неполон (свежий клон репо: MP4 гитигнорятся): отправляем клиента
    // на upstream-cub напрямую. Да, это внешний запрос — но лучше рабочая тема, чем битая; лечится
    // прогоном scripts/vendor-cub-extensions.ps1. Именно ЭТОТ путь показывает, что вендор неполон.
    ActionResult CubExtProxyFallback(string path)
    {
        string scheme = null, domain = null;
        try
        {
            scheme = CoreInit.conf.cub?.scheme;
            domain = CoreInit.conf.cub?.domain;
        }
        catch { }

        if (string.IsNullOrEmpty(domain))
            return NotFound();

        Console.WriteLine($"[QbitDownload] cub-ext: нет локальной копии '{path}' → редирект на upstream (прогнать scripts/vendor-cub-extensions.ps1)");
        return Redirect($"{(string.IsNullOrEmpty(scheme) ? "https" : scheme)}://{domain}/{path}");
    }
}
