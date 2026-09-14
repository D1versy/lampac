using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Вкладка «Названия» админки /admin/d1v (qdl 2.118): журнал промахов и псевдонимы названий.
// Сами данные и вся логика — в TitleAliases.cs (своя Postgres lampac: title_alias / title_miss);
// здесь только ручки. Ими же пользуется периодический прогон Claude `/title-aliases` (репо
// медиасервера, .claude/skills/title-aliases): читает промахи, ПРОВЕРЯЕТ имя живым запросом
// (`check`), пишет подтверждённое (`add` с source=claude), закрывает промах (`resolve`).
//
// Гейты те же, что у остальной админки: [Authorization] на классе (рут-пароль), префикс /admin
// закрыт снаружи периметром, мутации — SameOrigin() (X-D1V-Admin + Origin).
// ─────────────────────────────────────────────────────────────────────────────
public partial class D1VAdminController
{
    [HttpGet]
    [Route("/admin/d1v/api/aliases/misses")]
    async public Task<ActionResult> AliasMisses(int open = 1, int limit = 300)
    {
        SetHeadersNoCache();
        var payload = await QbitController.AliasesAdminMisses(open == 1, limit);
        return Json(payload);
    }

    [HttpGet]
    [Route("/admin/d1v/api/aliases")]
    async public Task<ActionResult> AliasList(int tmdb_id = 0, int is_tv = 0)
    {
        SetHeadersNoCache();
        if (tmdb_id <= 0) return BadRequest();
        return Json(await QbitController.AliasesAdminList(tmdb_id, is_tv == 1));
    }

    /// <summary>Кнопка «Фикс»: готовый промпт для Claude по открытым промахам — plain text для копирования.</summary>
    [HttpGet]
    [Route("/admin/d1v/api/aliases/prompt")]
    async public Task<ActionResult> AliasPrompt(int limit = 60)
    {
        SetHeadersNoCache();
        string text = await QbitController.AliasesAdminPrompt(limit);
        return Content(text, "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Живая проверка имени тем же индексатором, что и поиск (широкий проход, как у добора).
    /// Это подтверждение, без которого прогон не имеет права записать имя.
    /// </summary>
    [HttpGet]
    [Route("/admin/d1v/api/aliases/check")]
    async public Task<ActionResult> AliasCheck(string query, int is_serial = 0)
    {
        SetHeadersNoCache();
        return Json(await QbitController.AliasesAdminCheck(query, is_serial));
    }

    [HttpPost]
    [Route("/admin/d1v/api/aliases/add")]
    async public Task<ActionResult> AliasAdd([FromBody] AliasBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();

        var (ok, message, alias) = await QbitController.AliasesAdminAdd(body.tmdb_id, body.is_tv, body.alias, body.source, body.note);
        var res = new JObject { ["ok"] = ok, ["message"] = message };
        if (alias != null) res["alias"] = alias.ToJson();
        return Json(res);
    }

    [HttpPost]
    [Route("/admin/d1v/api/aliases/delete")]
    async public Task<ActionResult> AliasDelete([FromBody] AliasBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();
        return Done(await QbitController.AliasesAdminDelete(body.tmdb_id, body.is_tv, body.norm));
    }

    [HttpPost]
    [Route("/admin/d1v/api/aliases/resolve")]
    async public Task<ActionResult> AliasResolve([FromBody] AliasBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();
        return Done(await QbitController.AliasesAdminResolve(body.tmdb_id, body.is_tv, body.by, body.note));
    }

    /// <summary>Пометка на промахе без закрытия («в рунете раздач нет» ≠ «имя не то»).</summary>
    [HttpPost]
    [Route("/admin/d1v/api/aliases/note")]
    async public Task<ActionResult> AliasNote([FromBody] AliasBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();
        return Done(await QbitController.AliasesAdminNote(body.tmdb_id, body.is_tv, body.note));
    }

    /// <summary>Перечитать TMDB/Shikimori для карточки; ручные и подтверждённые имена остаются.</summary>
    [HttpPost]
    [Route("/admin/d1v/api/aliases/refresh")]
    async public Task<ActionResult> AliasRefresh([FromBody] AliasBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null || body.tmdb_id <= 0) return BadRequest();
        return Json(await QbitController.AliasesAdminRefresh(body.tmdb_id, body.is_tv, body.title, body.title_original, body.year));
    }

    ActionResult Json(JObject payload)
        => Content(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");

    public class AliasBody
    {
        public int tmdb_id { get; set; }
        public bool is_tv { get; set; }
        public string alias { get; set; }
        public string norm { get; set; }
        public string source { get; set; }
        public string note { get; set; }
        public string by { get; set; }
        public string title { get; set; }
        public string title_original { get; set; }
        public int year { get; set; }
    }
}
