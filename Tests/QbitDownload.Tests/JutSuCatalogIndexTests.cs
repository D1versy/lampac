using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Снапшот-индекс каталога jut.su (qdl 2.38).
//
// Витрина order-by-add меняется ТОЛЬКО сверху (1-2 новинки в день), а листалась через
// пер-страничный кеш с TTL 30 минут — каждая страница снова платила ~1.1 с сайту.
// 🔥 Пер-страничный кеш с длинным TTL негоден принципиально: новинки СДВИГАЮТ ленту,
// и карточка со дна свежей p1 уезжает на старую p2 — тайтл пропадает из выдачи молча.
// Поэтому один упорядоченный список, страницы — нарезка из него.
//
// Канон: E:\Media-server\claude\jut\02-architecture.md §6
// ─────────────────────────────────────────────────────────────────────────────
public class JutSuCatalogIndexTests
{
    // ── помощники ─────────────────────────────────────────────────────────

    static JutCard Card(string slug, int episodes = 0, bool ongoing = false) => new JutCard
    {
        slug = slug, id = 1, titleRu = slug, titleOrig = slug,
        episodes = episodes, ongoing = ongoing
    };

    /// <summary>Сеть-сейм: страницы по 30 карточек из готового списка слагов.</summary>
    static Func<int, Task<(bool ok, JutCatalogPage page)>> Pages(params string[][] pages)
        => p =>
        {
            if (p < 1 || p > pages.Length) return Task.FromResult((true, new JutCatalogPage { hasNext = false }));
            var page = new JutCatalogPage
            {
                items = pages[p - 1].Select(s => Card(s)).ToList(),
                hasNext = p < pages.Length
            };
            return Task.FromResult((true, page));
        };

    static string[] Slugs(string prefix, int from, int count)
        => Enumerable.Range(from, count).Select(i => prefix + i).ToArray();

    static void Fresh()
    {
        TestEnv.FreshCache();
        ModInit.conf.jutEnable = true;
        ModInit.conf.jutCatalogIndex = true;
        ModInit.conf.jutCatalogSeedPaceMs = 0;      // тесты не спят
        ModInit.conf.jutCatalogSeedMaxPages = 60;
        ModInit.conf.jutCatalogHeadMaxPages = 5;
        ModInit.conf.jutCatalogReseedDays = 30;
        QbitController.JutIdxReset();
    }

    static JObject Serve(int page)
    {
        Assert.True(QbitController.JutIdxTryServe(page, out var payload), "страница обязана отдаваться из индекса");
        return payload;
    }

    // ── нарезка страниц ───────────────────────────────────────────────────

    [Fact]
    public async Task Индекс_нарезает_страницы_и_честно_ставит_hasNext()
    {
        Fresh();
        // 65 тайтлов = 2 полные страницы + хвост из 5
        await QbitController.JutCatalogTick(loadPage: Pages(Slugs("a", 1, 30), Slugs("b", 1, 30), Slugs("c", 1, 5)));

        var p1 = Serve(1);
        Assert.Equal(30, ((JArray)p1["items"]).Count);
        Assert.True(p1.Value<bool>("hasNext"));
        Assert.True(p1.Value<bool>("index"));

        var p3 = Serve(3);
        Assert.Equal(5, ((JArray)p3["items"]).Count);
        Assert.False(p3.Value<bool>("hasNext"));

        // Страница за концом — валидный пустой ответ, а не ошибка: клиентский load()
        // именно так и останавливает бесконечную ленту.
        var p4 = Serve(4);
        Assert.Empty((JArray)p4["items"]);
        Assert.False(p4.Value<bool>("hasNext"));
    }

    [Fact]
    public async Task Неполный_индекс_не_отдаётся()
    {
        Fresh();
        // Сайт лёг на второй странице: снапшот покрывает только начало витрины, и отдавать
        // его нельзя — граница со «живой» пагинацией сдвигается новинками (дубли/пропуски).
        await QbitController.JutCatalogTick(loadPage: p =>
            p == 1 ? Task.FromResult((true, new JutCatalogPage { items = Slugs("a", 1, 30).Select(s => Card(s)).ToList(), hasNext = true }))
                   : Task.FromResult<(bool, JutCatalogPage)>((false, null)));

        Assert.False(QbitController.JutIdxTryServe(1, out _));
    }

    [Fact]
    public async Task Выключенный_индекс_не_отдаётся()
    {
        Fresh();
        await QbitController.JutCatalogTick(loadPage: Pages(Slugs("a", 1, 5)));
        Assert.True(QbitController.JutIdxTryServe(1, out _));

        ModInit.conf.jutCatalogIndex = false;   // киллсвитч на лету → старый путь
        Assert.False(QbitController.JutIdxTryServe(1, out _));
    }

    [Fact]
    public async Task Отдаём_клоны_а_не_живые_карточки()
    {
        // 🔴 JutPosterStamp дописывает в карточку ответа поле pv. Отдай мы живой JObject —
        // pv запёкся бы в индекс, а параллельные запросы гонялись бы на общем JToken.
        Fresh();
        await QbitController.JutCatalogTick(loadPage: Pages(Slugs("a", 1, 3)));

        var payload = Serve(1);
        ((JObject)((JArray)payload["items"])[0])["pv"] = 1;

        var again = Serve(1);
        Assert.Null(((JObject)((JArray)again["items"])[0])["pv"]);
    }

    // ── сид ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Сид_резюмируется_с_курсора_и_не_дублирует_слаги()
    {
        Fresh();
        int calls = 0;
        // Сайт отвечает первыми двумя страницами и падает на третьей
        Func<int, Task<(bool ok, JutCatalogPage page)>> flaky = p =>
        {
            calls++;
            if (p <= 2)
                return Task.FromResult((true, new JutCatalogPage
                {
                    items = Slugs("p" + p + "-", 1, 30).Select(s => Card(s)).ToList(),
                    hasNext = true
                }));
            return Task.FromResult<(bool, JutCatalogPage)>((false, null));
        };

        var r1 = await QbitController.JutCatalogTick(loadPage: flaky);
        Assert.Equal("seed", r1.Value<string>("mode"));
        Assert.False(r1.Value<bool>("complete"));
        Assert.Equal(60, r1.Value<int>("items"));

        // Второй проход обязан начать с 3-й страницы, а не переливать первые две заново
        var seen = new List<int>();
        var r2 = await QbitController.JutCatalogTick(loadPage: p =>
        {
            seen.Add(p);
            return Task.FromResult((true, new JutCatalogPage
            {
                items = Slugs("p" + p + "-", 1, 10).Select(s => Card(s)).ToList(),
                hasNext = false
            }));
        });

        Assert.Equal(new[] { 3 }, seen);
        Assert.True(r2.Value<bool>("complete"));
        Assert.Equal(70, r2.Value<int>("items"));
        Assert.Equal(30, ((JArray)Serve(1)["items"]).Count);
    }

    [Fact]
    public async Task Повтор_слага_между_страницами_не_создаёт_дубля()
    {
        // Новинка, приехавшая между запросами страниц, сдвигает ленту — тот же тайтл
        // приходит и на p1, и на p2.
        Fresh();
        await QbitController.JutCatalogTick(loadPage: Pages(
            new[] { "a", "b", "c" },
            new[] { "c", "d" }));

        var all = ((JArray)Serve(1)["items"]).Select(x => x.Value<string>("slug")).ToList();
        Assert.Equal(new[] { "a", "b", "c", "d" }, all);
    }

    [Fact]
    public async Task Сид_упирается_в_кап_страниц()
    {
        // Предохранитель на случай вранья hasNext: без него сид ушёл бы в бесконечный обход.
        Fresh();
        ModInit.conf.jutCatalogSeedMaxPages = 3;
        int calls = 0;
        await QbitController.JutCatalogTick(loadPage: p =>
        {
            calls++;
            return Task.FromResult((true, new JutCatalogPage
            {
                items = Slugs("p" + p + "-", 1, 30).Select(s => Card(s)).ToList(),
                hasNext = true    // сайт «никогда не кончается»
            }));
        });

        Assert.Equal(3, calls);
        Assert.True(QbitController.JutIdxTryServe(1, out _), "индекс закрывается тем, что набрали");
    }

    // ── голова ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Голова_препендит_новинки_и_обновляет_знакомые_карточки()
    {
        Fresh();
        await QbitController.JutCatalogTick(loadPage: Pages(new[] { "old1", "old2", "old3" }));

        // На витрине появились два новых тайтла, а у old1 подрос счётчик серий
        var head = new JutCatalogPage
        {
            items = new List<JutCard> { Card("new1"), Card("new2"), Card("old1", episodes: 12), Card("old2") },
            hasNext = true
        };
        var res = await QbitController.JutCatalogTick(loadPage: _ => Task.FromResult((true, head)));

        Assert.Equal("head", res.Value<string>("mode"));
        Assert.Equal(2, res.Value<int>("added"));
        Assert.Equal(1, res.Value<int>("pages"));   // одной страницы хватило

        var items = ((JArray)Serve(1)["items"]).ToList();
        Assert.Equal(new[] { "new1", "new2", "old1", "old2", "old3" },
                     items.Select(x => x.Value<string>("slug")).ToArray());
        Assert.Equal(12, items.First(x => x.Value<string>("slug") == "old1").Value<int>("episodes"));
    }

    [Fact]
    public async Task Голова_ищет_знакомый_слаг_вглубь_и_сдаётся_на_ресид()
    {
        Fresh();
        ModInit.conf.jutCatalogHeadMaxPages = 2;
        await QbitController.JutCatalogTick(loadPage: Pages(new[] { "old1", "old2" }));

        // Витрина полностью разъехалась с индексом — клеить голову не к чему
        var res = await QbitController.JutCatalogTick(loadPage: p => Task.FromResult((true, new JutCatalogPage
        {
            items = new List<JutCard> { Card("x" + p + "a"), Card("x" + p + "b") },
            hasNext = true
        })));

        Assert.Equal(2, res.Value<int>("pages"));
        Assert.True(res.Value<bool?>("reseedScheduled") ?? false);
    }

    // ── ресид ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ресид_вычищает_удалённые_тайтлы()
    {
        Fresh();
        await QbitController.JutCatalogTick(loadPage: Pages(new[] { "a", "b", "gone" }));

        // Ручной пересид (то же делает плановый по jutCatalogReseedDays)
        QbitController.JutIdxDropForReseed();
        var res = await QbitController.JutCatalogTick(manual: true,
            loadPage: Pages(new[] { "a", "b" }));

        Assert.Equal("reseed", res.Value<string>("mode"));
        Assert.Equal(new[] { "a", "b" },
                     ((JArray)Serve(1)["items"]).Select(x => x.Value<string>("slug")).ToArray());
    }

    [Fact]
    public async Task Оборванный_ресид_не_подменяет_живой_индекс()
    {
        // Лучше месяц старых данных, чем каталог, обрезанный на середине упавшим сайтом.
        Fresh();
        await QbitController.JutCatalogTick(loadPage: Pages(new[] { "a", "b", "c" }));
        QbitController.JutIdxDropForReseed();

        var res = await QbitController.JutCatalogTick(manual: true, loadPage: p =>
            p == 1 ? Task.FromResult((true, new JutCatalogPage { items = new List<JutCard> { Card("a") }, hasNext = true }))
                   : Task.FromResult<(bool, JutCatalogPage)>((false, null)));

        Assert.False(res.Value<bool>("complete"));
        Assert.Equal(3, ((JArray)Serve(1)["items"]).Count);
    }

    // ── пиггибек онгоингов ────────────────────────────────────────────────

    [Fact]
    public async Task Пиггибек_обновляет_счётчики_и_не_снимает_чужой_онгоинг()
    {
        // Карта строится по ПЕРВОЙ странице /anime/ongoing/ и может быть неполной:
        // «снять онгоинг у всех, кого не увидел» было бы враньём.
        Fresh();
        await QbitController.JutCatalogTick(loadPage: p => Task.FromResult((true, new JutCatalogPage
        {
            items = new List<JutCard> { Card("live", 10, ongoing: true), Card("other", 24, ongoing: true) },
            hasNext = false
        })));

        int n = QbitController.JutCatalogOngoingUpdate(new Dictionary<string, int> { ["live"] = 13 });
        Assert.Equal(1, n);

        var items = ((JArray)Serve(1)["items"]).ToList();
        Assert.Equal(13, items.First(x => x.Value<string>("slug") == "live").Value<int>("episodes"));
        Assert.True(items.First(x => x.Value<string>("slug") == "other").Value<bool>("ongoing"));
        Assert.Equal(24, items.First(x => x.Value<string>("slug") == "other").Value<int>("episodes"));
    }

    // ── персистентность и конфиг ──────────────────────────────────────────

    [Fact]
    public async Task Индекс_переживает_рестарт_через_файл()
    {
        Fresh();
        await QbitController.JutCatalogTick(loadPage: Pages(new[] { "a", "b" }));

        string path = Path.Combine(ModInit.conf.cachePath, "jut", "catalog-index.json");
        Assert.True(File.Exists(path), "снапшот обязан лежать файлом — иначе рестарт стоит полного сида");

        QbitController.JutIdxReset();   // «рестарт»: РАМ пуст, файл на месте
        Assert.Equal(new[] { "a", "b" },
                     ((JArray)Serve(1)["items"]).Select(x => x.Value<string>("slug")).ToArray());
    }

    [Fact]
    public void Смена_cachePath_забывает_РАМ_индекс()
    {
        // Ключ файла — путь; без сброса новый cachePath отдавал бы снапшот старого.
        string src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Modules", "QbitDownload", "ModInit.cs"));
        int i = src.IndexOf("JsonStore.ResetForConfigReload();", StringComparison.Ordinal);
        Assert.True(i > 0);
        Assert.Contains("JutIdxReset();", src.Substring(i, Math.Min(400, src.Length - i)));
    }

    [Fact]
    public void Дефолты_ручек_каталога()
    {
        var c = new ModuleConf();
        Assert.True(c.jutCatalogIndex);
        Assert.Equal(3000, c.jutCatalogSeedPaceMs);
        Assert.Equal(60, c.jutCatalogSeedMaxPages);
        Assert.Equal(6, c.jutCatalogHeadHours);
        Assert.Equal(5, c.jutCatalogHeadMaxPages);
        Assert.Equal(30, c.jutCatalogReseedDays);
    }

    [Fact]
    public async Task Выключенный_jutEnable_не_ходит_на_сайт()
    {
        Fresh();
        ModInit.conf.jutEnable = false;
        int calls = 0;
        var res = await QbitController.JutCatalogTick(loadPage: _ => { calls++; return Pages(new[] { "a" })(1); });
        Assert.Equal(0, calls);
        Assert.Equal("disabled", res.Value<string>("skipped"));
    }
}
