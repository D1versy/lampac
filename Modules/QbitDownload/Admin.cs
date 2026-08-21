using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// АДМИНКА ПРАВ D1Vision (qdl 2.54) — выдача скрытых разделов конкретным устройствам.
//
// Почему префикс /admin и почему это ВАЖНО: он даёт обе нужные защиты бесплатно, без единой
// правки ядра форка.
//   1. D1VPerimeter.ExternalDenyPrefixes содержит "/admin" → снаружи 404 ВСЕГДА, даже с валидным
//      ключом платформы (Core/Middlewares/D1VPerimeter.cs).
//   2. Accsdb считает авторизацией любой путь на /admin → требуется кука accspasswd == rootPasswd,
//      с бесплатным тротлингом 10 неверных паролей на IP в сутки (Core/Middlewares/Accsdb.cs).
// То есть «доступна из локалки по специальной куке» — ровно то, что просил владелец, и уже готовое.
//
// 🔴 Отдельный класс, а НЕ partial QbitController: атрибут [Authorization] на классе накрыл бы все
// роуты /qdl/* (контроллер partial) и положил бы разом всех клиентов.
//
// 🔴 CSRF. ModHeaders зеркалит Origin и отдаёт Access-Control-Allow-Credentials:true, поэтому любой
// сайт, открытый в браузере внутри локалки, может дёрнуть эти ручки вместе с нашей кукой. На всех
// мутациях: Origin (если он есть) обязан совпасть с Host, плюс обязателен заголовок X-D1V-Admin.
// Простой form-POST такой заголовок поставить не может, а preflight для него уйдёт на OPTIONS.
// ─────────────────────────────────────────────────────────────────────────────
[Authorization(redirectUri: "/admin/d1v/auth")]
public class D1VAdminController : BaseController
{
    #region страницы

    /// <summary>
    /// Форма входа. Своя, а не готовая /adminpanel/auth: та после успеха уводит на /adminpanel,
    /// и пришлось бы каждый раз доруливать адрес руками. Кука и пароль — те же самые.
    /// </summary>
    [HttpGet, AllowAnonymous]
    [Route("/admin/d1v/auth")]
    public ActionResult Auth()
    {
        return Html("auth.html");
    }

    [HttpGet]
    [Route("/admin/d1v")]
    public ActionResult Index()
    {
        return Html("d1v.html");
    }

    ActionResult Html(string file)
    {
        // ⚠️ HTML лежит в папке модуля и приезжает в контейнер только с образом (COPY . .) или
        // через docker cp — правка одного файла без этого просто не доедет.
        string path = Path.Combine(ModInit.modpath, "admin", file);
        if (!System.IO.File.Exists(path))
            return NotFound();

        SetHeadersNoCache();
        return Content(System.IO.File.ReadAllText(path, Encoding.UTF8), "text/html; charset=utf-8");
    }

    #endregion

    #region API

    [HttpGet]
    [Route("/admin/d1v/api/devices")]
    public ActionResult Devices()
    {
        SetHeadersNoCache();
        var payload = new JObject
        {
            ["enabled"] = ModInit.conf?.permsEnabled != false,
            ["features"] = new JArray(Perms.Features),
            ["devices"] = Perms.List()
        };
        return Content(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    [HttpPost]
    [Route("/admin/d1v/api/grant")]
    public ActionResult SetGrant([FromBody] GrantBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return Done(Perms.Grant(body.uid, body.feature, body.on));
    }

    [HttpPost]
    [Route("/admin/d1v/api/name")]
    public ActionResult SetName([FromBody] NameBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return Done(Perms.Rename(body.uid, body.name));
    }

    [HttpPost]
    [Route("/admin/d1v/api/forget")]
    public ActionResult ForgetDevice([FromBody] UidBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return Done(Perms.Forget(body.uid));
    }

    #region разовый бэкфилл «Истории просмотров» (qdl 2.61)

    /// <summary>Сухой прогон: что и кому легло бы. В БД не пишется ничего.</summary>
    [HttpGet]
    [Route("/admin/d1v/api/history-backfill")]
    async public Task<ActionResult> HistoryBackfillPreview() => await RunHistoryBackfill(false);

    /// <summary>Применить. Идемпотентно — гонять можно сколько угодно.</summary>
    [HttpPost]
    [Route("/admin/d1v/api/history-backfill")]
    async public Task<ActionResult> HistoryBackfillApply()
    {
        if (!SameOrigin()) return StatusCode(403);
        return await RunHistoryBackfill(true);
    }

    async Task<ActionResult> RunHistoryBackfill(bool apply)
    {
        // 🔴 ТОЛЬКО ДОМ. На реплике запись поставила бы строке updated=now, и домашняя копия
        // навсегда оказалась бы «старее» (ReplicaHistory.ApplyBookmarks сравнивает именно время) —
        // то есть история дома перестала бы доезжать сюда вообще. Читать тоже незачем: она
        // приезжает на реплику сама, ближайшим тиком репликации.
        if (QbitController.ReplicaMode)
            return StatusCode(403, new { error = "replica role" });

        SetHeadersNoCache();
        var report = await QbitController.HistoryBackfillRun(apply);
        return Content(report.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    #endregion

    public class GrantBody { public string uid { get; set; } public string feature { get; set; } public bool on { get; set; } }
    public class NameBody { public string uid { get; set; } public string name { get; set; } }
    public class UidBody { public string uid { get; set; } }

    ActionResult Done(bool success)
    {
        SetHeadersNoCache();
        return Content("{\"success\":" + (success ? "true" : "false") + "}", "application/json; charset=utf-8");
    }

    /// <summary>Анти-CSRF: заголовок-маркер обязателен, а Origin (если прислан) обязан совпасть с Host.</summary>
    bool SameOrigin()
    {
        if (!Request.Headers.ContainsKey("X-D1V-Admin"))
            return false;

        if (!Request.Headers.TryGetValue("Origin", out var origin) || origin.Count == 0 || string.IsNullOrEmpty(origin[0]))
            return true;   // same-origin fetch заголовок Origin на POST шлёт, но перестраховываемся

        return Uri.TryCreate(origin[0], UriKind.Absolute, out var u)
            && string.Equals(u.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
