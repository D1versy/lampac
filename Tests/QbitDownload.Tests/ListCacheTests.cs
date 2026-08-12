using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Кеш собранного ответа /qdl/list.
//
// Ручка дёргается на КАЖДОЕ открытие любой карточки (qdl.js подписан на 'full') и
// пересобиралась с нуля: поход в qBittorrent + ~200-300 файловых операций + 62
// JObject.Parse + два захвата глобального _activityLock с дисковым I/O внутри.
// Замер на боевом 12.08.2026: 0.58-1.03 с в одиночку, 1.2 с под 4 параллельными
// клиентами. Каждая скачанная серия аниме добавляла файл в маркер и делала открытие
// ЛЮБОЙ карточки медленнее — это и есть «во время скачивания приложение подлагивает».
//
// Отставание прогресса на TTL согласовано с владельцем: «если вопрос в нескольких
// минутах — совсем не страшно».
// Канон: E:\Media-server\claude\jut\06-cache.md
// ─────────────────────────────────────────────────────────────────────────────
public class ListCacheTests
{
    const string HA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string HB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    static string Torrent(string hash, long addedOn)
        => $"{{\"hash\":\"{hash}\",\"name\":\"t-{hash[..4]}\",\"progress\":0.5,"
         + $"\"state\":\"downloading\",\"size\":100,\"save_path\":\"/downloads\","
         + $"\"content_path\":\"/downloads/t\",\"added_on\":{addedOn},\"completion_on\":0}}";

    static async Task<string> RunRaw(string torrentsJson)
    {
        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", torrentsJson).BuildHandler());
        try
        {
            var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var res = await ctrl.List();
            // Успех идёт через ContentTo (ContentResult), а отказ — через Json (JsonResult):
            // тест обязан различать их, иначе «не закешировали ошибку» проверяется вслепую.
            if (res is ContentResult cr) return cr.Content;
            return Newtonsoft.Json.JsonConvert.SerializeObject(Assert.IsType<JsonResult>(res).Value);
        }
        finally { Access.ResetQbitFake(); }
    }

    [Fact]
    public async Task Повторный_запрос_в_пределах_TTL_отдаётся_из_кеша()
    {
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 30;

        string first = await RunRaw("[" + Torrent(HA, 300) + "]");
        Assert.Single(JArray.Parse(first));

        // qBit отдаёт уже ДВА торрента, но ответ обязан прийти прежний — из кеша
        string second = await RunRaw("[" + Torrent(HA, 300) + "," + Torrent(HB, 400) + "]");
        Assert.Equal(first, second);
        Assert.Single(JArray.Parse(second));
    }

    [Fact]
    public async Task DropListCache_немедленно_показывает_новое_состояние()
    {
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 30;

        await RunRaw("[" + Torrent(HA, 300) + "]");
        QbitController.DropListCache();

        var after = JArray.Parse(await RunRaw("[" + Torrent(HA, 300) + "," + Torrent(HB, 400) + "]"));
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public async Task Нулевой_TTL_полностью_выключает_кеш()
    {
        // Киллсвитч на лету: listCacheSeconds = 0 → прежнее поведение без пересборки образа.
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 0;

        await RunRaw("[" + Torrent(HA, 300) + "]");
        var after = JArray.Parse(await RunRaw("[" + Torrent(HA, 300) + "," + Torrent(HB, 400) + "]"));
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public async Task Штамп_активности_сбрасывает_кеш()
    {
        // Порядок карточек задаёт activity. Если Touch не сбрасывает кеш, новая серия
        // не поднимет карточку до истечения TTL — ровно то, за чем следит §AG/qdl 2.28.
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 30;

        var first = JArray.Parse(await RunRaw("[" + Torrent(HA, 300) + "," + Torrent(HB, 100) + "]"));
        Assert.Equal(new[] { HA, HB }, first.Select(x => x.Value<string>("hash")));

        Access.ActivityTouch(HB, 500);      // охота докачала серию старой карточке

        var second = JArray.Parse(await RunRaw("[" + Torrent(HA, 300) + "," + Torrent(HB, 100) + "]"));
        Assert.Equal(new[] { HB, HA }, second.Select(x => x.Value<string>("hash")));
    }

    [Fact]
    public async Task Падение_qBittorrent_не_кешируется()
    {
        // 🔴 Инвариант деградации (claude/jut/02 §2): исключение qBit обнуляет ответ.
        // Закешировать "internal error" на TTL значило бы погасить «Загрузки» целиком.
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 30;

        string bad = await RunRaw("это не json");
        Assert.Contains("error", bad);

        string good = await RunRaw("[" + Torrent(HA, 300) + "]");
        Assert.DoesNotContain("internal error", good);
        Assert.Single(JArray.Parse(good));
    }

    [Fact]
    public async Task Кеш_переживает_только_свой_cachePath()
    {
        // Смена cachePath (перечит init.conf) обесценивает и РАМ-слой, и готовый ответ:
        // ключи горячего слоя — это пути к файлам.
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 30;
        await RunRaw("[" + Torrent(HA, 300) + "]");

        TestEnv.FreshCache();               // внутри — JsonStore.ResetForConfigReload + DropListCache
        var after = JArray.Parse(await RunRaw("[" + Torrent(HA, 300) + "," + Torrent(HB, 400) + "]"));
        Assert.Equal(2, after.Count);
    }
}
