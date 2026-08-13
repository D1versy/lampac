using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Локальная (jut) ветка /qdl/episodes: разбор НАШИХ имён файлов, ключ таймлайна и ПОРЯДОК.
//
// Жалоба владельца 14.08.2026: «посмотрел серию с jut.su — отметилась, а „Продолжить“ ведёт
// на первую». Одна из причин жила здесь: ветка отдавала файлы в порядке маркера, то есть
// лексикографически по пути (s1e100 между s1e10 и s1e11, film/ova в НАЧАЛЕ списка), а клиент
// считал «Продолжить» по индексам массива, доверяя серверной сортировке.
//
// Рядом — вторая мина: общий ParseEp не знает наших имён. «<slug>.film1.1080p» он читал как
// серию 1, и фильм получал ключ таймлайна ПЕРВОЙ СЕРИИ вместе с её отметкой просмотра;
// «ova2» давал kind=OVA, то есть строку без ключа вовсе; серии ≥1000 не брались из-за \d{1,3}.
//
// 🔒 Главный инвариант файла: ключ обычной серии НЕ ИЗМЕНИЛСЯ. Смена ключа = молча обнулённые
// отметки просмотра у владельца, поэтому равенство «s01e07 → s1e7 → jut:<slug>:s1e7» проверяется
// таблицей, а не рассуждением.
// ─────────────────────────────────────────────────────────────────────────────
public class JutEpisodesOrderTests
{
    const string SLUG = "liar-game";
    const string HASH = "25b5a042026866145fdd54fc111ca7c3537e336d";

    // ── разбор имени: точная инверсия JutFileName ────────────────────────────

    [Theory]
    [InlineData(JutEpKind.Episode, 1, 7, 1080)]
    [InlineData(JutEpKind.Episode, 2, 13, 720)]
    [InlineData(JutEpKind.Episode, 1, 1085, 1080)]   // One Piece: ParseEp такие не брал (\d{1,3})
    [InlineData(JutEpKind.Episode, 1, 3, 0)]         // без качества
    [InlineData(JutEpKind.Film, 1, 2, 1080)]
    [InlineData(JutEpKind.Ova, 1, 4, 720)]
    [InlineData(JutEpKind.GameOva, 1, 1, 1080)]
    [InlineData(JutEpKind.Special, 1, 5, 360)]
    public void Имя_файла_разбирается_обратно_в_то_же_самое(JutEpKind kind, int season, int num, int quality)
    {
        string name = QbitController.JutFileName(SLUG, new JutEp { kind = kind, season = season, num = num }, quality);
        Assert.True(QbitController.TryParseJutFileName(Path.GetFileNameWithoutExtension(name),
                                                       out var k, out int s, out int n),
                    "не разобралось: " + name);
        Assert.Equal(kind, k);
        Assert.Equal(num, n);
        if (kind == JutEpKind.Episode) Assert.Equal(season, s);
    }

    [Fact]
    public void Слаг_со_словом_film_или_ova_внутри_не_ломает_разбор()
    {
        // якорь на КОНЕЦ базы имени: иначе «film-noir» съедал бы разбор серии
        Assert.True(QbitController.TryParseJutFileName("film-noir-anime.s01e04.1080p", out var k, out int s, out int n));
        Assert.Equal(JutEpKind.Episode, k);
        Assert.Equal(1, s);
        Assert.Equal(4, n);

        Assert.True(QbitController.TryParseJutFileName("ova-drive.film2", out var k2, out _, out int n2));
        Assert.Equal(JutEpKind.Film, k2);
        Assert.Equal(2, n2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Some.Random.Release.1080p")]
    [InlineData("liar-game.s01e")]                 // номера нет
    [InlineData("liar-game.s01e07.extra")]         // хвост не наш
    public void Чужое_имя_не_притворяется_нашим(string baseNoExt)
        => Assert.False(QbitController.TryParseJutFileName(baseNoExt, out _, out _, out _));

    // ── /qdl/episodes: ключи и порядок ───────────────────────────────────────

    static async Task<JArray> Episodes(string hash)
    {
        var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var res = await ctrl.Episodes(hash);
        return JArray.Parse(Assert.IsType<ContentResult>(res).Content);
    }

    /// <summary>Маркер jut с реальными файлами на диске — ветка требует File.Exists.</summary>
    static void SeedJut(string dir, params string[] names)
    {
        Directory.CreateDirectory(dir);
        var files = new JArray();
        int i = 0;
        foreach (string n in names)
        {
            string p = Path.Combine(dir, n).Replace('\\', '/');
            File.WriteAllText(p, "x");
            files.Add(new JObject { ["index"] = i++, ["name"] = n, ["path"] = p, ["size"] = 1L });
        }
        Access.SaveLocal(HASH, new JObject
        {
            ["name"] = "Игра лжецов",
            ["dir"] = dir.Replace('\\', '/'),
            ["size"] = names.Length,
            ["added"] = 1_700_000_000L,
            ["overlay"] = false,
            ["files"] = files,
            ["jut"] = new JObject { ["slug"] = SLUG, ["tlPrefix"] = "jut:" + SLUG }
        });
    }

    static string FreshDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "qdl-tests", "jut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Ключ_обычной_серии_НЕ_поменялся()
    {
        TestEnv.FreshCache();
        SeedJut(FreshDir(), SLUG + ".s01e07.1080p.mp4", SLUG + ".s02e01.720p.mp4");
        var arr = await Episodes(HASH);

        var e7 = arr.OfType<JObject>().Single(x => x.Value<string>("name").Contains("s01e07"));
        Assert.Equal("s1e7", e7.Value<string>("epkey"));
        Assert.Equal("jut:" + SLUG + ":s1e7", e7.Value<string>("tl"));
        Assert.Equal(1, e7.Value<int?>("season"));
        Assert.Equal(7, e7.Value<int?>("episode"));

        var e21 = arr.OfType<JObject>().Single(x => x.Value<string>("name").Contains("s02e01"));
        Assert.Equal("s2e1", e21.Value<string>("epkey"));
        Assert.Equal("jut:" + SLUG + ":s2e1", e21.Value<string>("tl"));
    }

    [Fact]
    public async Task Фильм_больше_не_забирает_ключ_первой_серии()
    {
        TestEnv.FreshCache();
        SeedJut(FreshDir(), SLUG + ".film1.1080p.mp4", SLUG + ".s01e01.1080p.mp4");
        var arr = await Episodes(HASH);

        var film = arr.OfType<JObject>().Single(x => x.Value<string>("name").Contains("film1"));
        var ep1 = arr.OfType<JObject>().Single(x => x.Value<string>("name").Contains("s01e01"));
        Assert.Equal("film1", film.Value<string>("epkey"));
        Assert.Equal("jut:" + SLUG + ":film1", film.Value<string>("tl"));
        Assert.Equal("s1e1", ep1.Value<string>("epkey"));
        Assert.NotEqual(film.Value<string>("tl"), ep1.Value<string>("tl"));
        // у фильма нет номера серии — иначе он влезет в сериальную нумерацию клиента
        Assert.Null(film.Value<int?>("episode"));
    }

    [Fact]
    public async Task Серия_с_номером_больше_999_получает_ключ()
    {
        TestEnv.FreshCache();
        SeedJut(FreshDir(), "one-piece.s01e1085.1080p.mp4");
        var arr = await Episodes(HASH);
        Assert.Equal("s1e1085", arr.OfType<JObject>().Single().Value<string>("epkey"));
        Assert.Equal("jut:" + SLUG + ":s1e1085", arr.OfType<JObject>().Single().Value<string>("tl"));
    }

    [Fact]
    public async Task Порядок_серий_числовой_а_экстры_в_конце()
    {
        TestEnv.FreshCache();
        // вход намеренно в том порядке, в котором его отдаёт маркер (сортировка по ПУТИ)
        SeedJut(FreshDir(),
            SLUG + ".film1.1080p.mp4",
            SLUG + ".ova1.1080p.mp4",
            SLUG + ".s01e01.1080p.mp4",
            SLUG + ".s01e02.1080p.mp4",
            SLUG + ".s01e10.1080p.mp4",
            SLUG + ".s01e100.1080p.mp4",
            SLUG + ".s01e11.1080p.mp4",
            SLUG + ".s02e01.1080p.mp4");
        var arr = await Episodes(HASH);

        Assert.Equal(
            new[] { "s1e1", "s1e2", "s1e10", "s1e11", "s1e100", "s2e1", "film1", "ova1" },
            arr.OfType<JObject>().Select(x => x.Value<string>("epkey")).ToArray());
    }

    [Fact]
    public async Task Непонятный_файл_остаётся_в_конце_и_без_ключа()
    {
        TestEnv.FreshCache();
        SeedJut(FreshDir(), "trailer.mp4", SLUG + ".s01e01.1080p.mp4");
        var arr = await Episodes(HASH);

        Assert.Equal("s1e1", arr.OfType<JObject>().First().Value<string>("epkey"));
        var tail = arr.OfType<JObject>().Last();
        Assert.Equal("trailer.mp4", tail.Value<string>("name"));
        Assert.Null(tail.Value<string>("tl"));
    }
}
