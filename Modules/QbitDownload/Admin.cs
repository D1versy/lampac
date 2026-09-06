using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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

    /// <summary>История просмотров одного пользователя. Открывается кликом по айди в списке устройств.</summary>
    [HttpGet]
    [Route("/admin/d1v/history")]
    public ActionResult HistoryPage()
    {
        return Html("history.html");
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

    /// <summary>
    /// Дослить историю уже связанных устройств (см. Groups.Resync). Нужна, когда в подмену
    /// добавили новое хранилище после того, как группы уже были собраны.
    /// </summary>
    [HttpPost]
    [Route("/admin/d1v/api/groups/resync")]
    public ActionResult GroupResync([FromBody] GroupLinkBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        return RunGroupOp("resync", body.gid, null, true, apply: true);
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
            case "resync": report = Groups.Resync(gid, apply); break;
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

    #region история просмотров (qdl 2.105)

    // Только чтение: ни очистки, ни удаления пунктов. Поэтому нет ни SameOrigin() (он стережёт
    // мутации — так же устроены read-only соседи Devices и GroupsList), ни GroupsReplicaDeny()
    // (на реплике те же строки лежат снимком с дома, и смотреть их законно; XSMART-резолв там
    // сам отключится по пустому xsmartApi). Сбор данных — QbitController.AdminHistory.

    [HttpGet]
    [Route("/admin/d1v/api/history")]
    async public Task<ActionResult> DeviceHistory(string uid)
    {
        SetHeadersNoCache();

        // Пустой uid — это не «покажи хоть чью-нибудь историю». Умолчания здесь быть не должно.
        if (Perms.NormUid(uid) == null)
            return StatusCode(400, new { error = "нужен айди устройства" });

        var report = await QbitController.AdminHistory(uid);
        if (report == null)
            return StatusCode(404, new { error = "устройство не найдено" });

        return Content(report.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    #endregion

    #region уведомления — журнал событий (qdl 2.111)

    // Зритель видит в ленте только «вышла новая серия/сезон» и итог пачки; всё служебное —
    // постановка в очередь, смена раздачи, охота, качество, диагностика — приезжает сюда
    // (EventLog.cs, кольцо в <cachePath>/events.json). Предложения сменить раздачу, которые
    // раньше выскакивали зрителю диалогом, тоже здесь — строкой с кнопкой «Переключить».

    /// <summary>
    /// Сколько записей отдаём владельцу разом. Зрителю лента режется отдельно и короче
    /// (notiFeedLimit, 50) — решение владельца: «в админке показывай последние 100
    /// уведомлений, а клиентам 50». Фильтрация — на клиенте, как на «Доступах».
    /// </summary>
    const int AdminEventsCap = 100;

    /// <summary>
    /// ВСЕ уведомления одним списком: журнал служебных событий (events.json) + то, что реально
    /// видели зрители (таблица noti).
    ///
    /// 🔴 Почему слияние, а не только журнал. Журнал копится с момента выката, а лента зрителей
    /// существует давно — сразу после деплоя вкладка была пуста, и владелец видел только
    /// предложения переключить раздачу. Строки зрителя в журнал НЕ дублируются: источник правды
    /// по ним — сама noti, иначе одно событие лежало бы в двух местах и вдвое быстрее вытесняло
    /// кольцо.
    /// </summary>
    [HttpGet]
    [Route("/admin/d1v/api/events")]
    public ActionResult Events(int limit = AdminEventsCap)
    {
        SetHeadersNoCache();
        int cap = Math.Clamp(limit, 1, AdminEventsCap);

        var (jrn, jrnTotal) = QdlEvents.Read(cap);
        var rows = new List<(DateTime at, JObject o)>();

        foreach (var t in jrn.OfType<JObject>())
            rows.Add((ParseAt(t.Value<string>("at")), t));

        int notiTotal = 0;
        try
        {
            using var db = new QbitDownload.SqlContext();
            notiTotal = db.noti.Count();
            foreach (var n in db.noti.OrderByDescending(x => x.Id).Take(cap).ToList())
            {
                var o = new JObject
                {
                    // SQLite теряет Kind — как и в /qdl/notifications, проставляем UTC явно
                    ["at"] = DateTime.SpecifyKind(n.created, DateTimeKind.Utc).ToString("o"),
                    ["cat"] = NotiCat(n.kind),
                    ["title"] = n.title ?? "",
                    ["text"] = n.label ?? ""
                };
                if (!string.IsNullOrEmpty(n.hash)) o["hash"] = n.hash;
                if (!string.IsNullOrEmpty(n.kind)) o["kind"] = n.kind;
                o["read"] = n.read;
                rows.Add((DateTime.SpecifyKind(n.created, DateTimeKind.Utc), o));
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] admin events noti: " + ex.Message); }

        var items = new JArray();
        foreach (var r in rows.OrderByDescending(x => x.at).Take(cap)) items.Add(r.o);

        var res = new JObject
        {
            ["enabled"] = QdlEvents.Enabled,
            ["replica"] = QbitController.ReplicaMode,
            // Отдаём не больше кэпа — но ВСЕГДА говорим, сколько было всего: молча резать нельзя
            ["total"] = jrnTotal + notiTotal,
            ["shown"] = items.Count,
            ["cap"] = cap,
            ["items"] = items
        };
        return Content(res.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    /// <summary>
    /// Категория для строки ленты. «user» — только то, что зритель реально видит СЕЙЧАС; строки
    /// служебных видов в таблице ещё лежат (их писал прежний код, ретенция съест не скоро), и
    /// валить их в одну кучу с «вышла новая серия» значило бы соврать в фильтре.
    /// </summary>
    static string NotiCat(string kind)
    {
        if (NotiRoute.UserKind(kind)) return QdlEvents.CatUser;
        switch ((kind ?? "").ToUpperInvariant())
        {
            case "START": return QdlEvents.CatDownload;
            case "SWITCH": case "INFO": return QdlEvents.CatRelease;
            case "NOSPACE": return QdlEvents.CatSpace;
            default: return QdlEvents.CatDiag;
        }
    }

    static DateTime ParseAt(string iso)
        => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUniversalTime() : DateTime.MinValue;

    /// <summary>
    /// Вкладка «Решения»: то, что ждёт ответа владельца. Сейчас это только предложения сменить
    /// раздачу на более полную — раньше они выскакивали ЗРИТЕЛЮ диалогом посреди ленты.
    /// Отдельно от журнала намеренно: журнал читают, решения — принимают, и терять их в
    /// стострочной ленте нельзя.
    /// </summary>
    [HttpGet]
    [Route("/admin/d1v/api/decisions")]
    async public Task<ActionResult> Decisions()
    {
        SetHeadersNoCache();
        var res = new JObject
        {
            ["replica"] = QbitController.ReplicaMode,
            ["pending"] = QbitController.AdminPendingSwitches(),
            // замены в ходу (Successor.cs, qdl 2.115): решения не требуют, показываются ради видимости
            ["successors"] = await QbitController.AdminPendingSuccessors()
        };
        return Content(res.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    /// <summary>
    /// Принять/отклонить смену раздачи. 🔴 Только дом: на реплике слежения нет по построению,
    /// а ExecuteSwitch трогает qBittorrent.
    /// </summary>
    [HttpPost]
    [Route("/admin/d1v/api/events/switch")]
    async public Task<ActionResult> EventsSwitch([FromBody] SwitchBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        var ro = GroupsReplicaDeny(); if (ro != null) return ro;
        if (body == null || string.IsNullOrWhiteSpace(body.hash)) return BadRequest();

        var (ok, switched, newHash, err) = await QbitController.WatchSwitchApply(body.hash, body.accept);
        SetHeadersNoCache();
        var res = new JObject { ["success"] = ok, ["switched"] = switched };
        if (newHash != null) res["hash"] = newHash;
        if (err != null) res["error"] = err;
        return Content(res.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    [HttpPost]
    [Route("/admin/d1v/api/events/clear")]
    public ActionResult EventsClear()
    {
        if (!SameOrigin()) return StatusCode(403);
        QdlEvents.Clear();
        return Done(true);
    }

    public class SwitchBody { public string hash { get; set; } public bool accept { get; set; } }

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
