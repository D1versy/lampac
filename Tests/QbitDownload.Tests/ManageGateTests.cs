using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.67 — право «Управление» (<see cref="Perms.FeatureManage"/>): транскод в MP4, удаление
/// загрузки с файлами и вся правка коллекций.
///
/// 🔴 Зачем эти тесты. До 2.67 перечисленные ручки были открыты ВСЕМ, кто дотянулся до /qdl
/// (все они [AllowAnonymous]), а «замком» служило сокрытие кнопки в интерфейсе — то есть защиты
/// не было вовсе. Теперь решение принимает сервер: либо устройству выдано право в /admin/d1v,
/// либо у запроса есть кука владельца qdl_unlock=1. Сломать это можно тихо и в одну строку,
/// поэтому здесь под тестом и сам гейт, и то, что он реально стоит в каждой из семи ручек,
/// и его порядок относительно гейта реплики.
///
/// Изоляция хранилища — как в <see cref="PermsTests"/>/<see cref="AdminTests"/>: каждый тест
/// начинается с <see cref="TestEnv.FreshCache"/>, иначе гранты уехали бы в боевой access.json.
/// </summary>
public class ManageGateTests
{
    // ── обвязка ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Контроллер с подставным контекстом. Готовый шлюз <see cref="LiveAccess.Controller"/> уже
    /// умеет класть куку и айди устройства — переопределяем только путь, чтобы в тесте про
    /// удаление в контексте не висел путь эфира (на решение гейта он не влияет).
    /// </summary>
    static QbitController Ctl(string uid = null, string cookie = null, string path = "/qdl/delete")
    {
        var c = LiveAccess.Controller(cookie: cookie, uid: uid);
        c.HttpContext.Request.Path = path;
        return c;
    }

    static int StatusOf(ActionResult r) => r switch
    {
        StatusCodeResult s => s.StatusCode,
        ObjectResult o => o.StatusCode ?? 200,
        ContentResult => 200,
        _ => 200,
    };

    static string ErrorOf(ActionResult r)
        => r is ObjectResult o && o.Value != null ? (string)JObject.FromObject(o.Value)["error"] : null;

    const string Dev = "dueq3shm";
    const string BadHash = "не-хеш";      // заведомо не проходит ValidHash — ручка обязана дойти до него
    const string GoodHash = "0123456789abcdef0123456789abcdef01234567";   // валидный: ответ решает гейт, а не валидация
    const string BadColId = "не-айди";    // то же для ValidColId

    // ══ набор фич ═════════════════════════════════════════════════════════════

    [Fact]
    public void Manage_добавлена_в_набор_фич_и_не_вытеснила_прежние()
    {
        // Набор — единственный белый список: Perms.Grant отказывает всему, чего в нём нет,
        // а админка рисует галочки ровно по нему. Забыть здесь строку = «право есть в коде,
        // но выдать его нечем».
        Assert.Contains(Perms.FeatureManage, Perms.Features);
        Assert.Contains(Perms.FeatureLive, Perms.Features);
        Assert.Contains(Perms.FeatureRec, Perms.Features);
        Assert.Equal(3, Perms.Features.Length);
        Assert.Equal("manage", Perms.FeatureManage);   // строка уходит в JSON и в админку — она контракт
    }

    [Fact]
    public void FeaturesOf_отдаёт_manage_клиенту()
    {
        // По этой карте qdl.js решает, рисовать ли шестерёнку и пункт «Удалить с файлами».
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);

        var map = Perms.FeaturesOf(Dev);
        Assert.True(map.ContainsKey(Perms.FeatureManage));
        Assert.True(map[Perms.FeatureManage]);
        Assert.False(map[Perms.FeatureLive]);
    }

    // ══ гранты ════════════════════════════════════════════════════════════════

    [Fact]
    public void Grant_manage_выдаёт_и_снимает()
    {
        TestEnv.FreshCache();
        Assert.False(Perms.Allowed(Dev, Perms.FeatureManage));

        Assert.True(Perms.Grant(Dev, Perms.FeatureManage, true));
        Assert.True(Perms.Allowed(Dev, Perms.FeatureManage));

        Assert.True(Perms.Grant(Dev, Perms.FeatureManage, false));
        Assert.False(Perms.Allowed(Dev, Perms.FeatureManage));
    }

    [Fact]
    public void Эфир_и_записи_не_открывают_управление()
    {
        // 🔴 Разные решения владельца. «Пусть смотрит камеры» не должно означать «пусть удаляет
        // фильмы с диска» — иначе право деградирует до одного общего флага «свой».
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureLive, true);
        Perms.Grant(Dev, Perms.FeatureRec, true);

        Assert.False(Perms.Allowed(Dev, Perms.FeatureManage));
        Assert.Equal(403, StatusOf(LiveAccess.ManageDenied(Ctl(uid: Dev))));
    }

    [Fact]
    public void Управление_не_открывает_эфир_и_записи()
    {
        // Обратная сторона того же: «может удалять» не делает камеры видимыми.
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);

        Assert.False(Perms.Allowed(Dev, Perms.FeatureLive));
        Assert.False(Perms.Allowed(Dev, Perms.FeatureRec));
        Assert.True(LiveAccess.LiveDenied(Ctl(uid: Dev), Perms.FeatureLive));
    }

    // ══ ManageDenied — сам гейт ═══════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]     // после NormUid пусто
    public void Без_айди_устройства_управление_закрыто(string uid)
    {
        // Нет айди — нет устройства, а значит и права. Общего бакета «для всех безымянных»
        // здесь быть не может: он открыл бы удаление вообще всем.
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);   // чужой грант на это не влияет

        Assert.Equal(403, StatusOf(LiveAccess.ManageDenied(Ctl(uid: uid))));
    }

    [Fact]
    public void Устройство_без_гранта_получает_403_с_внятной_причиной()
    {
        // 403, а не 404 как в Live.cs: прятать нечего (кнопка и так не нарисована), зато клиенту
        // нужен повод показать тост вместо тихого ничего.
        TestEnv.FreshCache();
        Perms.Touch(new Shared.Models.Base.RequestModel { user_uid = Dev, UserAgent = "d1vision_mac/1.0.9" }, force: true);

        var denied = LiveAccess.ManageDenied(Ctl(uid: Dev));
        Assert.Equal(403, StatusOf(denied));
        Assert.Equal("нет права управления", ErrorOf(denied));
    }

    [Fact]
    public void Устройство_с_грантом_проходит()
    {
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);

        Assert.Null(LiveAccess.ManageDenied(Ctl(uid: Dev)));
    }

    [Fact]
    public void Кука_владельца_открывает_управление_без_всякого_гранта()
    {
        // 🔴 Мастер-ключ оставлен сознательно и это не дыра: гейт удаления, до которого нельзя
        // добраться, потеряв access.json, был бы хуже прежнего состояния. Куку ставит только тот,
        // у кого есть консоль браузера на нашем origin, а снаружи браузер не проходит периметр.
        TestEnv.FreshCache();

        Assert.Null(LiveAccess.ManageDenied(Ctl(cookie: "qdl_unlock=1")));
        Assert.Null(LiveAccess.ManageDenied(Ctl(uid: "неизвестное-устройство", cookie: "qdl_unlock=1")));
        Assert.True(LiveAccess.ManageCookie(Ctl(cookie: "other=x; qdl_unlock=1; more=y")));
    }

    [Theory]
    [InlineData("qdl_unlock=0")]
    [InlineData("qdl_unlock=")]
    [InlineData("qdl_unlock=true")]
    [InlineData("qdl_unlock=11")]
    [InlineData("xqdl_unlock=1")]      // чужое имя с нашим хвостом
    [InlineData("qdl_unlock_x=1")]     // чужое имя с нашим началом
    [InlineData("qdl=1")]
    public void Мусорная_кука_ключом_не_является(string cookie)
    {
        // Сравнение строгое и по полному имени: «похоже на разблокировку» открывать удаление
        // не имеет права.
        TestEnv.FreshCache();

        Assert.False(LiveAccess.ManageCookie(Ctl(cookie: cookie)));
        Assert.Equal(403, StatusOf(LiveAccess.ManageDenied(Ctl(cookie: cookie))));
    }

    [Fact]
    public void Киллсвитч_permsEnabled_false_открывает_управление_всем()
    {
        // Аварийный выключатель прав целиком (fail-open) — тот же, что у Live/Rec. Он вернёт
        // поведение до 2.54/2.67, если хранилище прав вдруг станет недоступно.
        TestEnv.FreshCache();
        try
        {
            ModInit.conf.permsEnabled = false;

            Assert.Null(LiveAccess.ManageDenied(Ctl()));                     // вообще без айди
            Assert.Null(LiveAccess.ManageDenied(Ctl(uid: "кто-угодно")));    // без гранта
        }
        finally { ModInit.conf.permsEnabled = true; }
    }

    // ══ ручки: гейт реально стоит и реально пропускает ════════════════════════

    [Fact]
    public async Task Удаление_без_права_отказывает_и_ничего_не_делает()
    {
        TestEnv.FreshCache();
        var r = await Ctl(uid: Dev).Delete(GoodHash, deleteFiles: true);

        Assert.Equal(403, StatusOf(r));
        Assert.Equal("нет права управления", ErrorOf(r));
    }

    [Fact]
    public async Task Удаление_с_правом_доходит_до_валидации_аргументов()
    {
        // 🔴 Гейт обязан стоять ПЕРЕД валидацией, но не вместо неё: с правом запрос идёт дальше
        // и спотыкается уже о невалидный хеш (400), а не о права (403). Так проверяем, что
        // ManageDenied не съедает запрос у того, кому можно.
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);

        var r = await Ctl(uid: Dev).Delete(BadHash);
        Assert.Equal(400, StatusOf(r));
        Assert.Equal("invalid hash", ErrorOf(r));
    }

    [Fact]
    public async Task Транскод_без_права_отказывает()
    {
        TestEnv.FreshCache();
        var r = await Ctl(uid: Dev, path: "/qdl/transcode").Transcode(GoodHash);

        Assert.Equal(403, StatusOf(r));
        Assert.Equal("нет права управления", ErrorOf(r));
    }

    [Fact]
    public async Task Транскод_с_правом_доходит_до_валидации_аргументов()
    {
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);

        var r = await Ctl(uid: Dev, path: "/qdl/transcode").Transcode(BadHash);
        Assert.Equal(400, StatusOf(r));
    }

    [Fact]
    public async Task Транскод_по_куке_владельца_доходит_до_валидации()
    {
        // Браузер владельца остаётся рабочим входом и после 2.67 — иначе фича сломала бы
        // единственный способ управлять сервером без записи в access.json.
        TestEnv.FreshCache();

        var r = await Ctl(cookie: "qdl_unlock=1", path: "/qdl/transcode").Transcode(BadHash);
        Assert.Equal(400, StatusOf(r));
    }

    [Fact]
    public void Все_пять_ручек_коллекций_закрыты_без_права()
    {
        TestEnv.FreshCache();

        Assert.Equal(403, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/create").CollectionsCreate("сборник", "aaa,bbb")));
        Assert.Equal(403, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/add").CollectionsAdd("c1", "aaa")));
        Assert.Equal(403, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/remove").CollectionsRemove("c1", "aaa")));
        Assert.Equal(403, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/update").CollectionsUpdate("c1", "новое имя")));
        Assert.Equal(403, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/dissolve").CollectionsDissolve("c1")));
    }

    [Fact]
    public void Все_пять_ручек_коллекций_с_правом_доходят_до_валидации()
    {
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);

        // Аргументы заведомо негодные: важно, что ответ — 400 «плохой запрос», а не 403 «нет права».
        Assert.Equal(400, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/create").CollectionsCreate("сборник", null)));
        Assert.Equal(400, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/add").CollectionsAdd(BadColId, BadHash)));
        Assert.Equal(400, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/remove").CollectionsRemove(BadColId, BadHash)));
        Assert.Equal(400, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/update").CollectionsUpdate(BadColId, "новое имя")));
        Assert.Equal(400, StatusOf(Ctl(uid: Dev, path: "/qdl/collections/dissolve").CollectionsDissolve(BadColId)));
    }

    [Fact]
    public void Чтение_коллекций_правом_не_гейтится()
    {
        // 🔴 Гейт стоит только на мутациях. Закрыв GET /qdl/collections, мы бы развалили
        // «Загрузки» у всех, кому просто нечего удалять.
        TestEnv.FreshCache();

        Assert.NotEqual(403, StatusOf(Ctl(uid: Dev, path: "/qdl/collections").CollectionsList()));
    }

    // ══ порядок гейтов: реплика раньше прав ═══════════════════════════════════

    [Fact]
    public async Task На_реплике_отвечает_гейт_реплики_а_не_гейт_прав()
    {
        // 🔴 Порядок принципиален, и виден он именно на устройстве БЕЗ права: при верном порядке
        // реплика отвечает «только чтение» (роль сервера), при переставленных строках — «нет права
        // управления». Второе отправило бы владельца чинить права вместо того, чтобы понять, что
        // он вообще не на том сервере. Реплика read-only ПО РОЛИ, и её отказ не зависит от грантов.
        TestEnv.FreshCache();

        string prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";

            var del = await Ctl(uid: Dev).Delete(GoodHash, deleteFiles: true);
            Assert.Equal(403, StatusOf(del));
            Assert.Equal("сервер-реплика: только чтение", ErrorOf(del));

            var tc = await Ctl(uid: Dev, path: "/qdl/transcode").Transcode(GoodHash);
            Assert.Equal(403, StatusOf(tc));
            Assert.Equal("сервер-реплика: только чтение", ErrorOf(tc));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }

    [Fact]
    public async Task Право_управления_не_делает_реплику_записываемой()
    {
        // Устройство с правом «Управление» дома не превращается в того, кому можно писать
        // на второй площадке: право глобальное, а роль сервера — местная.
        TestEnv.FreshCache();
        Perms.Grant(Dev, Perms.FeatureManage, true);

        string prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";

            var del = await Ctl(uid: Dev).Delete(GoodHash, deleteFiles: true);
            Assert.Equal("сервер-реплика: только чтение", ErrorOf(del));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }

    [Fact]
    public async Task На_реплике_отказ_не_зависит_и_от_куки_владельца()
    {
        // Мастер-ключ владельца открывает права, а не роль сервера.
        TestEnv.FreshCache();
        string prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";

            var r = await Ctl(cookie: "qdl_unlock=1").Delete(GoodHash);
            Assert.Equal("сервер-реплика: только чтение", ErrorOf(r));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }

    // ══ инвариант по исходнику ════════════════════════════════════════════════

    /// <summary>Семь ручек, которые 2.67 закрыла правом «Управление».</summary>
    static readonly string[] GatedRoutes =
    {
        "qdl/delete",
        "qdl/transcode",
        "qdl/collections/create",
        "qdl/collections/add",
        "qdl/collections/remove",
        "qdl/collections/update",
        "qdl/collections/dissolve",
    };

    /// <summary>Корень репозитория от папки сборки — тем же пробингом, что в ModuleRegistryTests.</summary>
    static string Resolve(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        throw new FileNotFoundException("не нашёл " + relative + " от " + AppContext.BaseDirectory);
    }

    /// <summary>Тело метода ручки: от её [Route(...)] до следующего атрибута маршрута в файле.</summary>
    static string HandlerBody(string src, string route)
    {
        int at = src.IndexOf("Route(\"" + route + "\")", StringComparison.Ordinal);
        Assert.True(at >= 0, "в Controller.cs не нашлась ручка " + route);

        int next = src.IndexOf("Route(\"", at + 1, StringComparison.Ordinal);
        return next < 0 ? src.Substring(at) : src.Substring(at, next - at);
    }

    [Fact]
    public void Гейт_управления_стоит_во_всех_семи_ручках()
    {
        // Поведенческие тесты выше проверяют то же самое, но сторож по исходнику дешевле и
        // переживёт рефакторинг, при котором ручку станет неудобно звать напрямую.
        string src = File.ReadAllText(Resolve(Path.Combine("Modules", "QbitDownload", "Controller.cs")));

        foreach (string route in GatedRoutes)
            Assert.Contains("ManageDenied()", HandlerBody(src, route), StringComparison.Ordinal);
    }

    [Fact]
    public void Гейт_управления_стоит_ПОСЛЕ_гейта_реплики()
    {
        // 🔴 Дешёвый и надёжный страж от перестановки строк местами: там, где в ручке есть оба
        // гейта, ответ «сервер-реплика: только чтение» обязан выигрывать у ответа про права.
        // Ловится это иначе только на живой реплике — то есть уже у владельца.
        string src = File.ReadAllText(Resolve(Path.Combine("Modules", "QbitDownload", "Controller.cs")));

        int checkedRoutes = 0;
        foreach (string route in GatedRoutes)
        {
            string body = HandlerBody(src, route);
            int ro = body.IndexOf("ReplicaReadOnlyDeny()", StringComparison.Ordinal);
            if (ro < 0) continue;                       // ручка без гейта реплики — сравнивать нечего

            int mg = body.IndexOf("ManageDenied()", StringComparison.Ordinal);
            Assert.True(ro < mg, route + ": ManageDenied() обязан идти ПОСЛЕ ReplicaReadOnlyDeny()");
            checkedRoutes++;
        }

        // ⚠️ Сторож не должен молча выродиться в пустой цикл, если гейт реплики из ручек уедет.
        Assert.True(checkedRoutes >= 2, "оба гейта сразу не нашлись ни в одной ручке — сторож ослеп");
    }

    [Fact]
    public void Гейт_управления_ничего_не_гейтит_в_чтении()
    {
        // Список загрузок и сами файлы — не «управление». Появление ManageDenied() в /qdl/list
        // или /qdl/stream означало бы, что фича закрыла просмотр для всех.
        string src = File.ReadAllText(Resolve(Path.Combine("Modules", "QbitDownload", "Controller.cs")));

        foreach (string route in new[] { "qdl/list", "qdl/stream", "qdl/collections" })
            Assert.DoesNotContain("ManageDenied()", HandlerBody(src, route), StringComparison.Ordinal);
    }

    // ══ уведомления на реплике: лента там зеркало дома ═════════════════════════

    [Fact]
    public async Task На_реплике_очистка_и_сканер_уведомлений_отвечают_403()
    {
        // Лента на реплике целиком привезена с дома (ReplicaNoti.cs). Очистка снесла бы зеркало
        // до следующей смены домашней сигнатуры, а сканер полез бы в её qBittorrent и написал бы
        // в ту же таблицу строки со своими Id.
        TestEnv.FreshCache();

        string prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";

            var clear = Ctl(uid: Dev, path: "/qdl/notifications/clear").NotificationsClear();
            Assert.Equal(403, StatusOf(clear));
            Assert.Equal("сервер-реплика: только чтение", ErrorOf(clear));

            var scan = await Ctl(uid: Dev, path: "/qdl/notifications/scan").NotificationsScan();
            Assert.Equal(403, StatusOf(scan));
            Assert.Equal("сервер-реплика: только чтение", ErrorOf(scan));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }

    [Fact]
    public void Чтение_ленты_и_отметка_прочитанного_на_реплике_работают()
    {
        // 🔴 Без этого бейдж на tv2 не погасить никогда: «прочитано» там местное состояние
        // просмотра ленты, а не правка источника правды — оно и домой не уезжает.
        TestEnv.FreshCache();

        string prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";

            Assert.NotEqual(403, StatusOf(Ctl(uid: Dev, path: "/qdl/notifications").Notifications()));
            Assert.NotEqual(403, StatusOf(Ctl(uid: Dev, path: "/qdl/notifications/read").NotificationsRead()));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }

    [Fact]
    public void Гейт_реплики_стоит_ровно_в_мутациях_уведомлений()
    {
        // Сторож по исходнику: перестановка строк местами или новая ручка ленты без гейта иначе
        // ловятся только на живой реплике.
        string src = File.ReadAllText(Resolve(Path.Combine("Modules", "QbitDownload", "Controller.cs")));

        foreach (string route in new[] { "qdl/notifications/clear", "qdl/notifications/scan" })
            Assert.Contains("ReplicaReadOnlyDeny()", HandlerBody(src, route), StringComparison.Ordinal);

        foreach (string route in new[] { "qdl/notifications", "qdl/notifications/read" })
            Assert.DoesNotContain("ReplicaReadOnlyDeny()", HandlerBody(src, route), StringComparison.Ordinal);
    }
}
