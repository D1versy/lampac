using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Вкладка «Online» админки /admin/d1v (qdl 2.117): управление эфиром контейнера `online` из одного
// места с правами и хелсом — запуск по ссылке, стоп, записи (в «Загрузки» / переэфир / удалить)
// и чекбокс «раздел виден зрителям» (вместо `docker compose stop online`).
//
// Здесь только ПРОКСИ к ручкам /online/ctl/* контейнера. Пароля панели у lampac нет и не нужно:
// контейнер при старте кладёт служебный токен в файл на ОБЩЕМ бинде
// (<onlineDownloadsPath>/.ctl-token — его видят ровно два процесса), мы читаем его на каждый вызов
// и шлём заголовком X-Online-Token. Ни .env, ни init.conf второго секрета не держат.
//
// Гейты те же, что у остальной админки: [Authorization] на классе (рут-пароль), префикс /admin
// закрыт снаружи периметром, мутации — SameOrigin() (X-D1V-Admin + Origin). Контейнер остановлен →
// 503 с честной причиной, вкладка это показывает, а не «ошибка сети».
// ─────────────────────────────────────────────────────────────────────────────
public partial class D1VAdminController
{
    static readonly HttpClient _onlineHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(150) };

    internal static string OnlineTokenPath()
        => Path.Combine(ModInit.conf?.onlineDownloadsPath ?? "/downloads/online", ".ctl-token");

    /// <summary>Токен из общего бинда; null = контейнер ещё не рождал его (не поднимался ни разу).</summary>
    internal static string OnlineReadToken(string path = null)
    {
        try
        {
            string t = System.IO.File.ReadAllText(path ?? OnlineTokenPath()).Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(t, "^[A-Za-z0-9_-]{32,}$") ? t : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Вызов ручки контейнера. Возвращает (status, тело). Сеть легла / контейнер остановлен → 503
    /// с человеческим текстом — вкладка отличает это от отказа самого сервиса.
    /// </summary>
    internal static async Task<(int status, JObject body)> OnlineCtl(string method, string path, JObject payload = null, int timeoutSec = 120)
    {
        string api = ModInit.conf?.onlineApi;
        if (string.IsNullOrWhiteSpace(api))
            return (503, new JObject { ["ok"] = false, ["message"] = "onlineApi не настроен (init.conf)" });

        string token = OnlineReadToken();
        if (token == null)
            return (503, new JObject { ["ok"] = false, ["message"] = "служебного токена нет — контейнер online ещё не поднимался или бинд /downloads/online не общий" });

        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), api.TrimEnd('/') + path);
            req.Headers.TryAddWithoutValidation("X-Online-Token", token);
            if (payload != null)
                req.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var resp = await _onlineHttp.SendAsync(req, cts.Token);
            string txt = await resp.Content.ReadAsStringAsync(cts.Token);
            JObject body;
            try { body = string.IsNullOrWhiteSpace(txt) ? new JObject() : JObject.Parse(txt); }
            catch { body = new JObject { ["ok"] = false, ["message"] = "контейнер ответил не JSON (" + (int)resp.StatusCode + ")" }; }
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
        {
            bool refused = QbitController.OnlineRefused(ex);
            return (503, new JObject
            {
                ["ok"] = false,
                ["down"] = refused,
                ["message"] = refused ? "контейнер online остановлен (docker compose start online)" : "контейнер online не отвечает: " + HealthState.ShortErr(ex)
            });
        }
    }

    ActionResult OnlineJson(int status, JObject body)
    {
        SetHeadersNoCache();
        return StatusCode(status, body.ToString(Newtonsoft.Json.Formatting.None));
    }

    /// <summary>Всё для вкладки одним запросом: состояние, записи, видимость, версия yt-dlp.</summary>
    [HttpGet]
    [Route("/admin/d1v/api/online")]
    async public Task<ActionResult> OnlineOverview()
    {
        var (s1, st) = await OnlineCtl("GET", "/online/ctl/status", null, 10);
        if (s1 != 200) return OnlineJson(s1, st);
        var (s2, recs) = await OnlineCtl("GET", "/online/ctl/recordings", null, 10);
        var body = new JObject
        {
            ["ok"] = true,
            ["status"] = st,
            ["recordings"] = s2 == 200 ? (recs["items"] as JArray ?? new JArray()) : new JArray(),
            ["hidden"] = st.Value<bool?>("hidden") ?? false,
            ["replica"] = QbitController.ReplicaMode
        };
        return OnlineJson(200, body);
    }

    public class OnlineStartBody { public string url { get; set; } public string recId { get; set; } public string title { get; set; } }
    public class OnlineVisibilityBody { public bool hidden { get; set; } }
    public class OnlineRecBody { public string id { get; set; } public string action { get; set; } public string title { get; set; } }

    [HttpPost]
    [Route("/admin/d1v/api/online/start")]
    async public Task<ActionResult> OnlineStart([FromBody] OnlineStartBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null || (string.IsNullOrWhiteSpace(body.url) && string.IsNullOrWhiteSpace(body.recId))) return BadRequest();
        var payload = new JObject { ["title"] = body.title ?? "" };
        if (!string.IsNullOrWhiteSpace(body.recId)) payload["recId"] = body.recId; else payload["url"] = body.url.Trim();
        var (s, b) = await OnlineCtl("POST", "/online/ctl/start", payload, 150);
        return OnlineJson(s, b);
    }

    [HttpPost]
    [Route("/admin/d1v/api/online/stop")]
    async public Task<ActionResult> OnlineStop()
    {
        if (!SameOrigin()) return StatusCode(403);
        var (s, b) = await OnlineCtl("POST", "/online/ctl/stop", new JObject(), 60);
        return OnlineJson(s, b);
    }

    /// <summary>Чекбокс «раздел виден зрителям»: hidden=true → у всех исчезает пункт «Online», эфир идёт.</summary>
    [HttpPost]
    [Route("/admin/d1v/api/online/visibility")]
    async public Task<ActionResult> OnlineVisibility([FromBody] OnlineVisibilityBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null) return BadRequest();
        var (s, b) = await OnlineCtl("POST", "/online/ctl/visibility", new JObject { ["hidden"] = body.hidden }, 10);
        return OnlineJson(s, b);
    }

    /// <summary>Обновить рабочий yt-dlp на томе контейнера (`yt-dlp -U`).</summary>
    [HttpPost]
    [Route("/admin/d1v/api/online/ytdlp")]
    async public Task<ActionResult> OnlineYtdlpUpdate()
    {
        if (!SameOrigin()) return StatusCode(403);
        var (s, b) = await OnlineCtl("POST", "/online/ctl/ytdlp/update", new JObject(), 150);
        return OnlineJson(s, b);
    }

    /// <summary>Записи: export (в «Загрузки») · delete · rename · rebroadcast (эфир заново).</summary>
    [HttpPost]
    [Route("/admin/d1v/api/online/rec")]
    async public Task<ActionResult> OnlineRec([FromBody] OnlineRecBody body)
    {
        if (!SameOrigin()) return StatusCode(403);
        if (body == null || string.IsNullOrEmpty(body.id) || !QbitController.OnlineIdRx.IsMatch(body.id)) return BadRequest();

        switch (body.action)
        {
            case "export":
                { var (s, b) = await OnlineCtl("POST", "/online/ctl/rec/" + body.id + "/export", new JObject(), 30); return OnlineJson(s, b); }
            case "delete":
                { var (s, b) = await OnlineCtl("POST", "/online/ctl/rec/" + body.id + "/delete", new JObject(), 30); return OnlineJson(s, b); }
            case "rename":
                { var (s, b) = await OnlineCtl("POST", "/online/ctl/rec/" + body.id + "/rename", new JObject { ["title"] = body.title ?? "" }, 10); return OnlineJson(s, b); }
            case "rebroadcast":
                { var (s, b) = await OnlineCtl("POST", "/online/ctl/start", new JObject { ["recId"] = body.id }, 60); return OnlineJson(s, b); }
            default:
                return BadRequest();
        }
    }
}
