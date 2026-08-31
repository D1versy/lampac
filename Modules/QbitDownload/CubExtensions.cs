using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
//
// 🔴 qdl 2.88: промах вендора БОЛЬШЕ НЕ редиректит клиента на cub.best. За файлом идёт сервер
// (FetchAndCache), кладёт в записываемый кеш и отдаёт со своего адреса. Причина: редирект уводил
// устройство на третью сторону, а Theme.set дописывал к тому URL ещё и `?token=` аккаунта.
public partial class QbitController : BaseController
{
    static string CubExtDir => ModInit.conf?.cubExtPath ?? "/lampac/wwwroot/cubext";

    // Записываемый кеш дотянутого (qdl 2.88): том витрины смонтирован :ro.
    static string CubExtCacheDir => ModInit.conf?.cubExtCachePath ?? "/lampac/cache/cubext";

    // Два вида ассетов витрины: тема (CSS) и скринсейвер (MP4). Порядок важен — FetchAndCache
    // выбирает по индексу: [0] тема, [1] видео.
    static readonly (string sub, string ext, string mime)[] CubExtKinds =
    [
        ("theme", "css", "text/css; charset=utf-8"),
        ("screensaver", "mp4", "video/mp4")
    ];

    // Локальный каталог витрины: у всех элементов premium=0 (контент лежит у нас — проверять
    // нечего), image/link указывают на наш хост. Нет файла → прозрачно уходим в CubProxy.
    [HttpGet, AllowAnonymous]
    [Route("cub/red/api/extensions/list")]
    [Route("cub/{mirror}/api/extensions/list")]
    public ActionResult CubExtensionsList(string mirror)
    {
        string file = Path.Combine(CubExtDir, "list.json");
        if (!System.IO.File.Exists(file))
        {
            // qdl 2.88: раньше отсюда уходил 302 на cub.best, и клиент получал каталог с ЧУЖИМИ
            // ссылками в image/link — а потом сохранял их в localStorage['plugins'] навсегда и
            // грузил <script>-ом с чужого хоста на каждом старте. Отдаём пустую витрину: показывать
            // нечего, но и уводить некуда. Состояние ненормальное — его видно в хелсе.
            Console.WriteLine("[QbitDownload] cub-ext: нет list.json → отдаю пустую витрину (прогнать scripts/vendor-cub-extensions.ps1)");
            HttpContext.Response.Headers["Cache-Control"] = "no-store";
            return ContentTo("{\"secuses\":true,\"results\":[]}", "application/json; charset=utf-8");
        }

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
    async public Task<ActionResult> CubExtensionAsset(string mirror, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IsExtensionId(id))
            return NotFound();

        // 1) вендор (том :ro) → 2) дотянутое ранее → 3) сходить самим
        foreach (string dir in new[] { CubExtDir, CubExtCacheDir })
        {
            var hit = TryLocal(dir, id);
            if (hit != null)
                return hit;
        }

        return await FetchAndCache(id).ConfigureAwait(false);
    }

    // Отдать локальную копию, если она есть. Содержимое элемента витрины неизменно (id = версия),
    // поэтому immutable-год честен.
    ActionResult TryLocal(string dir, string id)
    {
        if (string.IsNullOrEmpty(dir))
            return null;

        foreach (var (sub, ext, mime) in CubExtKinds)
        {
            string full = ConfinedCombine(dir, $"{sub}/{id}.{ext}");
            if (full != null && System.IO.File.Exists(full))
            {
                HttpContext.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
                return PhysicalFile(full, mime, enableRangeProcessing: true);
            }
        }

        return null;
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

    // ── Дотяжка на лету (qdl 2.88) ─────────────────────────────────────────────────────────────
    // Раньше на промах вендора отсюда уходил `Redirect` на upstream-cub: клиент шёл на cub.best
    // сам, причём Theme.set дописывал к чужому URL `?token=` — то есть наружу уезжал ещё и токен
    // аккаунта. Обоснование было «лучше рабочая тема, чем битая», и оно верное — неверен был
    // способ. Теперь за файлом идёт СЕРВЕР: скачал, положил рядом, отдал со своего адреса.
    // Клиент чужого хоста не видит ни разу, а тема при этом работает.
    //
    // Промах всё так же означает «вендор неполон» — сигнал остаётся в логе и в хелсе, лечится
    // прогоном scripts/vendor-cub-extensions.ps1 (он же кладёт файлы в git-том, минуя этот кеш).
    async Task<ActionResult> FetchAndCache(string id)
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

        string url = $"{(string.IsNullOrEmpty(scheme) ? "https" : scheme)}://{domain}/extensions/{id}";

        var (array, response) = await DownloadFollowingRedirects(url).ConfigureAwait(false);

        if (array == null || array.Length == 0)
        {
            Console.WriteLine($"[QbitDownload] cub-ext: не смог дотянуть '{id}' с {domain} (прогнать scripts/vendor-cub-extensions.ps1)");
            return NotFound();
        }

        // Тип определяем по ответу, а не по расширению: у элемента витрины его в URL нет.
        // MP4 распознаём ещё и по сигнатуре ftyp — CDN иногда отдаёт octet-stream.
        string contentType = response?.Content?.Headers?.ContentType?.MediaType ?? string.Empty;
        bool isVideo = contentType.StartsWith("video", StringComparison.OrdinalIgnoreCase) || IsMp4(array);

        var (sub, ext, mime) = isVideo ? CubExtKinds[1] : CubExtKinds[0];

        // Пишем через временный файл: иначе параллельный запрос успеет прочитать половину CSS.
        try
        {
            string full = ConfinedCombine(CubExtCacheDir, $"{sub}/{id}.{ext}");
            if (full != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));

                string tmp = full + ".tmp";
                await System.IO.File.WriteAllBytesAsync(tmp, array).ConfigureAwait(false);
                System.IO.File.Move(tmp, full, overwrite: true);

                Interlocked.Increment(ref cubExtFetched);
                Console.WriteLine($"[QbitDownload] cub-ext: дотянул '{id}' ({sub}, {array.Length} Б) — вендор неполон, прогнать scripts/vendor-cub-extensions.ps1");
            }
        }
        catch (Exception ex)
        {
            // Не смогли сохранить — не беда, отдадим из памяти; в следующий раз сходим снова.
            Console.WriteLine($"[QbitDownload] cub-ext: не смог сохранить '{id}': {ex.Message}");
        }

        HttpContext.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
        return File(array, mime);
    }

    // 🔴 Редирект разматываем РУКАМИ, и это не перестраховка. cub отдаёт
    //   302 https://cub.best/extensions/196 → http://cdn.cub.bz/extensions/theme/196.css
    // — то есть ПОНИЖЕНИЕ схемы с https на http, а за такими SocketsHttpHandler не ходит принципиально
    // (защита от downgrade). Автоследование редиректам тут бесполезно: первый же прыжок его и роняет.
    // Это ровно та древняя грабля, из-за которой CubProxy когда-то отдавал 500 на темы (claude/06 §AT).
    //
    // Три прыжка с запасом; принимаем только http/https, чтобы Location не увёл в file:// или дальше.
    async static Task<(byte[] array, System.Net.Http.HttpResponseMessage response)> DownloadFollowingRedirects(string url)
    {
        System.Net.Http.HttpResponseMessage last = null;

        for (int hop = 0; hop < 3; hop++)
        {
            // statusCodeOK: false — иначе 302 вернётся как «неудача» с пустым телом и мы
            // не увидим Location, ради которого сюда и пришли.
            var (array, response) = await Http.BaseDownload(url, timeoutSeconds: 30, statusCodeOK: false).ConfigureAwait(false);
            last = response;

            int code = response == null ? 0 : (int)response.StatusCode;

            if (code is >= 300 and < 400)
            {
                var next = response.Headers?.Location;
                if (next == null)
                    return (null, response);

                var abs = next.IsAbsoluteUri ? next : new Uri(new Uri(url), next);
                if (abs.Scheme != "http" && abs.Scheme != "https")
                    return (null, response);

                url = abs.ToString();
                continue;
            }

            if (code != 200)
                return (null, response);

            return (array, response);
        }

        return (null, last);
    }

    static bool IsMp4(byte[] array)
        => array.Length >= 12 && array[4] == 'f' && array[5] == 't' && array[6] == 'y' && array[7] == 'p';

    /// <summary>Сколько раз пришлось дотягивать вместо вендора — строка хелса.</summary>
    public static int CubExtFetched => cubExtFetched;
    static int cubExtFetched;
}
