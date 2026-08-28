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

        // Группа общей истории (qdl 2.81) — колонкой прямо здесь, чтобы связка была видна там же,
        // где владелец раздаёт права. Это только поиск по индексу в памяти, без единого запроса в БД.
        var devices = Perms.List();
        foreach (var d in devices)
        {
            string gid = Groups.GroupOf((string)d["uid"]);
            d["group"] = gid ?? "";
            d["groupName"] = gid == null ? "" : Groups.NameOf(gid);
        }

        var payload = new JObject
        {
            ["enabled"] = ModInit.conf?.permsEnabled != false,
            ["features"] = new JArray(Perms.Features),
            ["devices"] = devices
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

    #region уборка следов тестовых устройств (qdl 2.64)

    // Прогон гейта поднимает 14 headless-браузеров и оставляет за собой следы в пяти
    // хранилищах (см. TestSandbox.cs). Эти две ручки — то, чем он за собой убирает.
    // 🔴 Обе умеют трогать ТОЛЬКО заведомо тестовые айди: решение принимает классификатор
    // Perms.IsTestDevice, а не запрос. Именованное устройство и устройство с правами
    // не убираются никогда — на любой запрос по ним ответ 403 и ноль удалений.

    /// <summary>Сухой прогон: что и в каком хранилище нашлось. Не пишет ничего.</summary>
    [HttpGet]
    [Route("/admin/d1v/api/test-purge")]
    public ActionResult TestPurgePreview(string uid)
        => RunTestPurge(uid, string.IsNullOrWhiteSpace(uid), false);

    /// <summary>
    /// Применить. Тело {"uid":"…"} — один айди (так ходит гейт после каждого прогона),
    /// {"all":true} — все тестовые сразу (разовая уборка руками, после сухого прогона).
    /// Пустое тело — ошибка: умолчание не должно быть разрушительным.
    /// </summary>
    [HttpPost]
    [Route("/admin/d1v/api/test-purge")]
    public ActionResult TestPurgeApply([FromBody] PurgeBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return RunTestPurge(body.uid, body.all, true);
    }

    ActionResult RunTestPurge(string uid, bool all, bool apply)
    {
        SetHeadersNoCache();

        var report = QbitController.TestPurge(uid, all, apply);
        string json = report.ToString(Newtonsoft.Json.Formatting.None);

        // Отказ отдаём 403 вместе с причиной: гейту нужно не «не вышло», а что именно.
        if (report["error"] != null)
        {
            HttpContext.Response.StatusCode = 403;
            return Content(json, "application/json; charset=utf-8");
        }

        return Content(json, "application/json; charset=utf-8");
    }

    #endregion

    #region группы устройств — общая история (qdl 2.81)

    // Связанные устройства делят одну историю просмотров: сервер подменяет им айди устройства
    // на айди группы на входе в /bookmark/*, /timecode/* и /reqinfo (Groups.cs). Здесь — только
    // управление составом; сама подмена и правила слияния живут в Groups.cs / GroupsHistory.cs.
    //
    // 🔴 Все операции, которые ПИШУТ историю, идут парой «сухой прогон → применить»: владелец
    // видит счётчики до того, как нажмёт кнопку. Та же традиция, что у history-backfill и
    // test-purge выше.

    [HttpGet]
    [Route("/admin/d1v/api/groups")]
    public ActionResult GroupsList()
    {
        SetHeadersNoCache();

        var payload = new JObject
        {
            ["enabled"] = ModInit.conf?.groupsEnabled != false,
            ["replica"] = QbitController.ReplicaMode,
            ["groups"] = Groups.List(),
            ["devices"] = Perms.List()
        };
        return Content(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    [HttpPost]
    [Route("/admin/d1v/api/groups/create")]
    public ActionResult GroupCreate([FromBody] GroupNameBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        var deny = GroupsReplicaDeny(); if (deny != null) return deny;

        string gid = Groups.Create(body.name);
        SetHeadersNoCache();

        if (gid == null)
            return StatusCode(400, new { success = false, error = "не удалось создать группу (достигнут предел?)" });

        return Content("{\"success\":true,\"gid\":\"" + gid + "\"}", "application/json; charset=utf-8");
    }

    [HttpPost]
    [Route("/admin/d1v/api/groups/rename")]
    public ActionResult GroupRename([FromBody] GroupNameBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        var deny = GroupsReplicaDeny(); if (deny != null) return deny;

        return Done(Groups.Rename(body.gid, body.name));
    }

    /// <summary>
    /// Сухой прогон: что и куда сольётся. Ничего не пишет — админка показывает это в тосте
    /// перед связыванием, чтобы «сколько тайтлов уедет» было видно ДО, а не после.
    /// </summary>
    [HttpGet]
    [Route("/admin/d1v/api/groups/preview")]
    public ActionResult GroupPreview(string op, string gid, string uid, bool keepCopy = true)
        => RunGroupOp(op, gid, uid, keepCopy, apply: false);

    [HttpPost]
    [Route("/admin/d1v/api/groups/join")]
    public ActionResult GroupJoin([FromBody] GroupLinkBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return RunGroupOp("join", body.gid, body.uid, true, apply: true);
    }

    [HttpPost]
    [Route("/admin/d1v/api/groups/leave")]
    public ActionResult GroupLeave([FromBody] GroupLinkBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return RunGroupOp("leave", null, body.uid, body.keepCopy, apply: true);
    }

    [HttpPost]
    [Route("/admin/d1v/api/groups/delete")]
    public ActionResult GroupDelete([FromBody] GroupLinkBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return RunGroupOp("dissolve", body.gid, null, body.keepCopy, apply: true);
    }

    ActionResult RunGroupOp(string op, string gid, string uid, bool keepCopy, bool apply)
    {
        var deny = GroupsReplicaDeny(); if (deny != null) return deny;

        SetHeadersNoCache();

        JObject report;
        switch ((op ?? "").ToLowerInvariant())
        {
            case "join": report = Groups.Join(gid, uid, apply); break;
            case "leave": report = Groups.Leave(uid, keepCopy, apply); break;
            case "dissolve": report = Groups.Dissolve(gid, keepCopy, apply); break;
            default: return StatusCode(400, new { success = false, error = "неизвестная операция: " + op });
        }

        string json = report.ToString(Newtonsoft.Json.Formatting.None);

        // Отказ отдаём 400 вместе с причиной: админке нужно не «не вышло», а что именно.
        if (report["error"] != null)
        {
            HttpContext.Response.StatusCode = 400;
            return Content(json, "application/json; charset=utf-8");
        }

        return Content(json, "application/json; charset=utf-8");
    }

    /// <summary>
    /// 🔴 ТОЛЬКО ДОМ. Состав групп приезжает на реплику снимком вместе с историей
    /// (ReplicaHistory), и правка здесь была бы затёрта ближайшим тиком; хуже того, слияние
    /// поставило бы строке updated=now и домашняя копия навсегда стала бы «старее».
    /// Та же причина, по которой закрыт history-backfill.
    /// </summary>
    ActionResult GroupsReplicaDeny()
        => QbitController.ReplicaMode ? StatusCode(403, new { error = "replica role" }) : null;

    public class GroupNameBody { public string gid { get; set; } public string name { get; set; } }
    public class GroupLinkBody { public string gid { get; set; } public string uid { get; set; } public bool keepCopy { get; set; } = true; }

    #endregion

    public class GrantBody { public string uid { get; set; } public string feature { get; set; } public bool on { get; set; } }
    public class NameBody { public string uid { get; set; } public string name { get; set; } }
    public class UidBody { public string uid { get; set; } }
    public class PurgeBody { public string uid { get; set; } public bool all { get; set; } }


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
