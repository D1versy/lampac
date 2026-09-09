using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Models.Base;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Вкладка «Online» админки (OnlineAdmin.cs): прокси к контейнеру. Стережётся то, что ломается
// молча: мутации без анти-CSRF-маркера → 403; служебный токен читается из общего бинда и
// валидируется по форме; без токена/адреса — 503 с человеческой причиной (а не исключение).
// ─────────────────────────────────────────────────────────────────────────────
public class OnlineAdminTests
{
    static D1VAdminController Controller(bool marker = true)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("192.168.87.24:9118");
        ctx.Request.Method = "POST";
        ctx.Features.Set(new RequestModel { IP = "192.168.87.5", IsLocalRequest = true });
        if (marker) ctx.Request.Headers["X-D1V-Admin"] = "1";
        return new D1VAdminController { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    static int StatusOf(ActionResult r) => r switch
    {
        StatusCodeResult s => s.StatusCode,
        ObjectResult o => o.StatusCode ?? 200,
        ContentResult => 200,
        _ => 200,
    };

    [Fact]
    public async Task Мутации_без_маркера_X_D1V_Admin_отказ_403()
    {
        TestEnv.EnsureConf();
        var c = Controller(marker: false);
        Assert.Equal(403, StatusOf(await c.OnlineStart(new D1VAdminController.OnlineStartBody { url = "https://x/y.m3u8" })));
        Assert.Equal(403, StatusOf(await c.OnlineStop()));
        Assert.Equal(403, StatusOf(await c.OnlineVisibility(new D1VAdminController.OnlineVisibilityBody { hidden = true })));
        Assert.Equal(403, StatusOf(await c.OnlineRec(new D1VAdminController.OnlineRecBody { id = "2026-09-09_20-00-00", action = "export" })));
        Assert.Equal(403, StatusOf(await c.OnlineDownload(new D1VAdminController.OnlineStartBody { url = "https://x/y.mp4" })));
        Assert.Equal(403, StatusOf(await c.OnlineDownloadCancel()));
    }

    [Fact]
    public async Task Плохое_тело_400_без_похода_в_контейнер()
    {
        TestEnv.EnsureConf();
        var c = Controller();
        Assert.Equal(400, StatusOf(await c.OnlineStart(null)));
        Assert.Equal(400, StatusOf(await c.OnlineStart(new D1VAdminController.OnlineStartBody { url = "  " })));
        Assert.Equal(400, StatusOf(await c.OnlineDownload(null)));
        Assert.Equal(400, StatusOf(await c.OnlineDownload(new D1VAdminController.OnlineStartBody { url = " " })));
        Assert.Equal(400, StatusOf(await c.OnlineRec(new D1VAdminController.OnlineRecBody { id = "../x", action = "delete" })));
        Assert.Equal(400, StatusOf(await c.OnlineRec(new D1VAdminController.OnlineRecBody { id = "2026-09-09_20-00-00", action = "format" })));
    }

    [Fact]
    public void Токен_читается_из_общего_бинда_и_проверяется_по_форме()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qdl-tests", "online-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, ".ctl-token");

        Assert.Null(D1VAdminController.OnlineReadToken(f));                     // файла нет
        File.WriteAllText(f, "short\n");
        Assert.Null(D1VAdminController.OnlineReadToken(f));                     // не похоже на токен
        string tok = new string('a', 43);
        File.WriteAllText(f, tok + "\n");
        Assert.Equal(tok, D1VAdminController.OnlineReadToken(f));               // с переводом строки, как пишет контейнер
    }

    [Fact]
    public async Task Без_токена_в_бинде_503_с_причиной_а_не_исключение()
    {
        TestEnv.EnsureConf();
        string prevRoot = ModInit.conf.onlineDownloadsPath;
        string prevApi = ModInit.conf.onlineApi;
        ModInit.conf.onlineDownloadsPath = Path.Combine(Path.GetTempPath(), "qdl-tests", "nope-" + Guid.NewGuid().ToString("N"));
        ModInit.conf.onlineApi = "http://127.0.0.1:1";
        try
        {
            var (status, body) = await D1VAdminController.OnlineCtl("GET", "/online/ctl/status", null, 2);
            Assert.Equal(503, status);
            Assert.Contains("токена", body.Value<string>("message"));

            ModInit.conf.onlineApi = "";
            var (s2, b2) = await D1VAdminController.OnlineCtl("GET", "/online/ctl/status", null, 2);
            Assert.Equal(503, s2);
            Assert.Contains("onlineApi", b2.Value<string>("message"));
        }
        finally { ModInit.conf.onlineDownloadsPath = prevRoot; ModInit.conf.onlineApi = prevApi; }
    }

    [Fact]
    public async Task Контейнер_остановлен_503_с_пометкой_down()
    {
        TestEnv.EnsureConf();
        string dir = Path.Combine(Path.GetTempPath(), "qdl-tests", "online-down-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".ctl-token"), new string('b', 43));
        string prevRoot = ModInit.conf.onlineDownloadsPath;
        string prevApi = ModInit.conf.onlineApi;
        ModInit.conf.onlineDownloadsPath = dir;
        ModInit.conf.onlineApi = "http://127.0.0.1:1";   // порт 1 — connection refused
        try
        {
            var (status, body) = await D1VAdminController.OnlineCtl("GET", "/online/ctl/status", null, 3);
            Assert.Equal(503, status);
            Assert.True(body.Value<bool?>("down") ?? false, body.ToString());
            Assert.Contains("остановлен", body.Value<string>("message"));
        }
        finally { ModInit.conf.onlineDownloadsPath = prevRoot; ModInit.conf.onlineApi = prevApi; }
    }
}
