using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared;
using Shared.Models.Base;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Админка выдачи прав D1Vision (/admin/d1v) — единственное место, где скрытые разделы
/// Live/Rec открываются конкретному устройству.
///
/// 🔴 Анти-CSRF здесь не украшение. ModHeaders зеркалит Origin и отдаёт
/// Access-Control-Allow-Credentials:true, поэтому ЛЮБОЙ сайт, открытый в браузере внутри
/// локалки, может дёрнуть эти ручки вместе с нашей кукой. Защита держится на двух вещах:
/// обязательном заголовке X-D1V-Admin (простой form-POST его не поставит) и совпадении
/// Origin с Host. До этого файла ни одна из них не проверялась тестом.
/// </summary>
public class AdminTests
{
    static D1VAdminController Controller(string origin = null, bool marker = true,
                                         string host = "192.168.87.24:9118")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        ctx.Request.Method = "POST";
        ctx.Features.Set(new RequestModel { IP = "192.168.87.5", IsLocalRequest = true });

        if (marker) ctx.Request.Headers["X-D1V-Admin"] = "1";
        if (origin != null) ctx.Request.Headers["Origin"] = origin;

        return new D1VAdminController { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    static bool SameOrigin(D1VAdminController c) =>
        (bool)typeof(D1VAdminController)
            .GetMethod("SameOrigin", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(c, null);

    static int StatusOf(ActionResult r) => r switch
    {
        StatusCodeResult s => s.StatusCode,
        ObjectResult o => o.StatusCode ?? 200,
        ContentResult => 200,
        _ => 200,
    };

    // ══ анти-CSRF ═════════════════════════════════════════════════════════

    [Fact]
    public void A_request_without_the_marker_header_is_refused()
    {
        // Простой form-POST с чужого сайта заголовок поставить не может — на этом всё и держится.
        Assert.False(SameOrigin(Controller(marker: false)));
    }

    [Fact]
    public void A_same_origin_request_passes()
    {
        Assert.True(SameOrigin(Controller(origin: "http://192.168.87.24:9118")));
    }

    [Fact]
    public void A_request_without_Origin_passes_when_the_marker_is_present()
    {
        // Origin на POST шлют не все клиенты; маркера при этом достаточно.
        Assert.True(SameOrigin(Controller(origin: null)));
    }

    [Theory]
    [InlineData("http://evil.com")]
    [InlineData("http://192.168.87.24:9119")]       // другой порт
    [InlineData("http://192.168.87.25:9118")]       // другой хост
    public void A_foreign_Origin_is_refused(string origin)
    {
        Assert.False(SameOrigin(Controller(origin: origin)));
    }

    [Fact]
    public void Only_the_authority_is_compared_not_the_scheme()
    {
        // Сравнение идёт по authority: та же машина по https — та же машина.
        // Периметр от схемы не зависит, а внутри локалки её вообще нет.
        Assert.True(SameOrigin(Controller(origin: "https://192.168.87.24:9118")));
    }

    [Fact]
    public void A_malformed_Origin_is_refused()
    {
        Assert.False(SameOrigin(Controller(origin: "не url")));
    }

    [Fact]
    public void Origin_comparison_ignores_case()
    {
        Assert.True(SameOrigin(Controller(origin: "http://192.168.87.24:9118", host: "192.168.87.24:9118")));
    }

    // ══ мутации закрыты гардом ════════════════════════════════════════════

    [Fact]
    public void Grant_without_the_marker_is_403_and_changes_nothing()
    {
        TestEnv.FreshCache();
        var c = Controller(marker: false);

        var result = c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = Perms.FeatureLive, on = true
        });

        Assert.Equal(403, StatusOf(result));
        Assert.False(Perms.Allowed("device-1", Perms.FeatureLive));
    }

    [Fact]
    public void Rename_without_the_marker_is_403()
    {
        var result = Controller(marker: false)
            .SetName(new D1VAdminController.NameBody { uid = "device-1", name = "кухня" });

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public void Forget_without_the_marker_is_403()
    {
        var result = Controller(marker: false)
            .ForgetDevice(new D1VAdminController.UidBody { uid = "device-1" });

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public void A_missing_body_is_a_bad_request_not_a_crash()
    {
        Assert.Equal(400, StatusOf(Controller().SetGrant(null)));
        Assert.Equal(400, StatusOf(Controller().SetName(null)));
        Assert.Equal(400, StatusOf(Controller().ForgetDevice(null)));
    }

    // ══ выдача и отзыв прав ═══════════════════════════════════════════════

    [Fact]
    public void Granting_a_feature_opens_it_for_that_device_only()
    {
        TestEnv.FreshCache();
        var c = Controller();

        c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = Perms.FeatureLive, on = true
        });

        Assert.True(Perms.Allowed("device-1", Perms.FeatureLive));
        Assert.False(Perms.Allowed("device-2", Perms.FeatureLive));
    }

    [Fact]
    public void Features_are_granted_separately()
    {
        // «Эфир» не открывает «записи»: это разные разделы и разные решения владельца.
        TestEnv.FreshCache();
        var c = Controller();

        c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = Perms.FeatureLive, on = true
        });

        Assert.True(Perms.Allowed("device-1", Perms.FeatureLive));
        Assert.False(Perms.Allowed("device-1", Perms.FeatureRec));
    }

    [Fact]
    public void Revoking_closes_the_section_again()
    {
        TestEnv.FreshCache();
        var c = Controller();
        var body = new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = Perms.FeatureRec, on = true
        };

        c.SetGrant(body);
        Assert.True(Perms.Allowed("device-1", Perms.FeatureRec));

        body.on = false;
        c.SetGrant(body);
        Assert.False(Perms.Allowed("device-1", Perms.FeatureRec));
    }

    [Fact]
    public void An_unknown_feature_is_refused()
    {
        // Белый список вместо «всё кроме»: опечатка не должна создавать право-призрак.
        TestEnv.FreshCache();
        var c = Controller();

        var result = c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = "такого-нет", on = true
        });

        Assert.Contains("\"success\":false", ((ContentResult)result).Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void A_degenerate_uid_is_refused(string uid)
    {
        // 🔴 NormUid — не гигиена, а защита: «.» и «..» иначе уехали бы в путь файла прав.
        TestEnv.FreshCache();
        var c = Controller();

        var result = c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = uid, feature = Perms.FeatureLive, on = true
        });

        Assert.Contains("\"success\":false", ((ContentResult)result).Content);
    }

    [Fact]
    public void Forgetting_a_device_revokes_everything_it_had()
    {
        TestEnv.FreshCache();
        var c = Controller();
        c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = Perms.FeatureLive, on = true
        });

        c.ForgetDevice(new D1VAdminController.UidBody { uid = "device-1" });

        Assert.False(Perms.Allowed("device-1", Perms.FeatureLive));
    }

    [Fact]
    public void Forgetting_is_idempotent()
    {
        // headless-проверки зовут её и при старте (защитно), и при выходе.
        TestEnv.FreshCache();
        var c = Controller();

        c.ForgetDevice(new D1VAdminController.UidBody { uid = "never-existed" });
        var ex = Record.Exception(() => c.ForgetDevice(new D1VAdminController.UidBody { uid = "never-existed" }));

        Assert.Null(ex);
    }

    // ══ список устройств ══════════════════════════════════════════════════

    [Fact]
    public void The_device_list_reports_the_killswitch_and_the_feature_set()
    {
        TestEnv.FreshCache();
        var payload = JObject.Parse(((ContentResult)Controller().Devices()).Content);

        Assert.True(payload["enabled"]!.Value<bool>());
        var features = payload["features"]!.Select(f => (string)f).ToArray();
        Assert.Contains(Perms.FeatureLive, features);
        Assert.Contains(Perms.FeatureRec, features);
        // qdl 2.67: по этому списку админка рисует галочки. Не отдать «manage» — значит
        // получить право, которое живёт в коде, но выдать его владельцу нечем.
        Assert.Contains(Perms.FeatureManage, features);
    }

    [Fact]
    public void Granting_manage_opens_the_management_actions_for_that_device()
    {
        // 🔴 Единственный штатный вход в право «Управление» (удаление с файлами, транскод,
        // коллекции). Второй — кука владельца, но её у приложений нет и быть не может.
        TestEnv.FreshCache();
        var c = Controller();

        c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = Perms.FeatureManage, on = true
        });

        Assert.True(Perms.Allowed("device-1", Perms.FeatureManage));
        Assert.False(Perms.Allowed("device-2", Perms.FeatureManage));
        Assert.False(Perms.Allowed("device-1", Perms.FeatureLive));   // соседние разделы не открылись
    }

    [Fact]
    public void A_granted_device_shows_up_in_the_list()
    {
        TestEnv.FreshCache();
        var c = Controller();
        c.SetGrant(new D1VAdminController.GrantBody
        {
            uid = "device-1", feature = Perms.FeatureLive, on = true
        });

        var payload = JObject.Parse(((ContentResult)c.Devices()).Content);
        string json = payload["devices"]!.ToString();

        Assert.Contains("device-1", json);
    }

    [Fact]
    public void The_device_list_is_never_cached()
    {
        // Админка обязана показывать текущее состояние: закешированный список
        // выглядел бы как «право не выдалось».
        var c = Controller();
        c.Devices();

        Assert.True(c.Response.Headers.ContainsKey("Cache-Control"));
    }

    // ══ бэкфилл истории ═══════════════════════════════════════════════════

    [Fact]
    public async Task History_backfill_is_refused_on_a_replica()
    {
        // 🔴 Запись на реплике поставила бы строке updated=now, и домашняя копия навсегда
        // осталась бы «старее» — история дома перестала бы доезжать сюда вообще.
        TestEnv.EnsureConf();
        string prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";
            var result = await Controller().HistoryBackfillApply();
            Assert.Equal(403, StatusOf(result));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }

    [Fact]
    public async Task History_backfill_apply_requires_the_marker()
    {
        var result = await Controller(marker: false).HistoryBackfillApply();
        Assert.Equal(403, StatusOf(result));
    }

    // ══ уборка следов тестовых устройств (qdl 2.64) ═══════════════════════

    [Fact]
    public void Test_purge_apply_requires_the_marker()
    {
        var result = Controller(marker: false)
            .TestPurgeApply(new D1VAdminController.PurgeBody { all = true });

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public void Test_purge_apply_refuses_a_foreign_origin()
    {
        var result = Controller(origin: "http://evil.com")
            .TestPurgeApply(new D1VAdminController.PurgeBody { all = true });

        Assert.Equal(403, StatusOf(result));
    }

    [Fact]
    public void Test_purge_apply_with_an_empty_body_deletes_nothing()
    {
        // 🔴 Умолчание не имеет права быть разрушительным: пустое тело — это ошибка,
        // а не «убрать всё».
        TestEnv.FreshCache();
        var c = Controller();
        var result = c.TestPurgeApply(new D1VAdminController.PurgeBody());

        Assert.Equal(403, c.Response.StatusCode);
        Assert.Contains("нужен uid", (result as ContentResult)?.Content ?? "");
    }

    [Fact]
    public void Test_purge_refuses_a_device_that_is_not_a_test_one()
    {
        TestEnv.FreshCache();
        Perms.Touch(new RequestModel { user_uid = "dueq3shm", UserAgent = "lampa_client d1vision_ios/1.0.13-524" },
                    force: true);

        var c = Controller();
        c.TestPurgeApply(new D1VAdminController.PurgeBody { uid = "dueq3shm" });

        Assert.Equal(403, c.Response.StatusCode);
        Assert.True(Perms.Known("dueq3shm"));
    }

    [Fact]
    public void Test_purge_preview_never_writes()
    {
        TestEnv.FreshCache();
        Perms.Touch(new RequestModel { user_uid = Perms.TestUidPrefix + "ab12cd34", UserAgent = "HeadlessChrome/139" },
                    force: true);

        var c = Controller();
        var json = (c.TestPurgePreview(null) as ContentResult)?.Content ?? "";

        Assert.Contains("\"apply\":false", json);
        Assert.True(Perms.Known(Perms.TestUidPrefix + "ab12cd34"));
    }

    // ══ вкладка «Уведомления» — журнал событий (qdl 2.111) ════════════════

    [Fact]
    public void Events_отдают_журнал_и_честный_счётчик()
    {
        TestEnv.FreshCache();
        for (int i = 0; i < 5; i++) QdlEvents.Log(QdlEvents.CatHunt, "Тайтл", "событие " + i);

        var c = Controller();
        var json = (c.Events() as ContentResult)?.Content ?? "";
        var o = JObject.Parse(json);

        Assert.True(o.Value<bool>("enabled"));
        Assert.Equal(5, o.Value<int>("total"));
        Assert.Equal(5, o.Value<int>("shown"));
        Assert.Equal(5, (o["items"] as JArray).Count);
        Assert.NotNull(o["pending"]);
    }

    [Fact]
    public void Events_режут_выдачу_но_total_говорят_полный()
    {
        // Традиция дома (AdminHistory): отдаём не больше — но ВСЕГДА говорим, сколько было всего.
        TestEnv.FreshCache();
        for (int i = 0; i < 12; i++) QdlEvents.Log(QdlEvents.CatDiag, "Поиск раздач", "строка " + i);

        var o = JObject.Parse((Controller().Events(4) as ContentResult).Content);
        Assert.Equal(12, o.Value<int>("total"));
        Assert.Equal(4, o.Value<int>("shown"));
    }

    [Fact]
    public void Events_это_чтение_и_маркера_не_требуют()
    {
        // read-only соседи (Devices, GroupsList, DeviceHistory) SameOrigin тоже не спрашивают
        TestEnv.FreshCache();
        var c = Controller(marker: false);
        Assert.IsType<ContentResult>(c.Events());
    }

    [Fact]
    async public Task Переключение_раздачи_без_маркера_отказано()
    {
        TestEnv.FreshCache();
        var c = Controller(marker: false);
        var r = await c.EventsSwitch(new D1VAdminController.SwitchBody { hash = new string('a', 40), accept = true });
        Assert.Equal(403, StatusOf(r));
    }

    [Fact]
    async public Task Переключение_раздачи_с_чужого_Origin_отказано()
    {
        TestEnv.FreshCache();
        var c = Controller(origin: "http://evil.example");
        var r = await c.EventsSwitch(new D1VAdminController.SwitchBody { hash = new string('a', 40), accept = true });
        Assert.Equal(403, StatusOf(r));
    }

    [Fact]
    async public Task Переключение_без_тела_это_400()
    {
        TestEnv.FreshCache();
        var r = await Controller().EventsSwitch(null);
        Assert.Equal(400, StatusOf(r));
    }

    [Fact]
    async public Task Переключение_несуществующей_раздачи_отвечает_ошибкой_а_не_падает()
    {
        TestEnv.FreshCache();
        var c = Controller();
        var json = ((await c.EventsSwitch(new D1VAdminController.SwitchBody
        { hash = new string('a', 40), accept = false })) as ContentResult)?.Content ?? "";
        var o = JObject.Parse(json);

        Assert.False(o.Value<bool>("success"));
        Assert.Equal("not watched", o.Value<string>("error"));
    }

    [Fact]
    public void Очистка_журнала_без_маркера_отказана()
    {
        TestEnv.FreshCache();
        QdlEvents.Log(QdlEvents.CatHunt, "Тайтл", "событие");

        Assert.Equal(403, StatusOf(Controller(marker: false).EventsClear()));
        Assert.Equal(1, QdlEvents.Read(10).total);   // и журнал цел
    }

    [Fact]
    public void Очистка_журнала_с_маркером_работает()
    {
        TestEnv.FreshCache();
        QdlEvents.Log(QdlEvents.CatHunt, "Тайтл", "событие");

        Assert.Equal(200, StatusOf(Controller().EventsClear()));
        Assert.Equal(0, QdlEvents.Read(10).total);
    }
}

