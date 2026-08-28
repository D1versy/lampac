using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Сезоны одного сериала — ОДНА карточка «Загрузок» (qdl 2.78, SeriesMerge.cs).
///
/// Жалоба владельца (карточка 229564 «Телохранители»): две визуально одинаковые карточки в
/// «Загрузках», отличаются только сезоном внутри. И вторая раздача была НЕДОСТИЖИМА с полной
/// карточки: findDownload берёт первое совпадение по TMDB id.
///
/// 🔒 Инварианты, которые здесь заперты:
///   • группа — строго по TMDB id + media_type=tv; фильм и чужой сериал не склеиваются;
///   • jut.su/XSMART в группы не входят (свой контур подписки, один маркер на тайтл);
///   • главная часть СТАБИЛЬНА (самая ранняя по added) — на её hash висят постер, озвучка и кеши;
///   • /qdl/episodes по ЛЮБОМУ хешу группы отдаёт сезон за сезоном по сериям;
///   • одна и та же серия в двух раздачах остаётся ОДНОЙ записью (иначе дубли в плейлисте);
///   • mergeSeasons=false возвращает прежнее поведение целиком.
/// </summary>
public class SeriesMergeTests
{
    const string H1 = "1111111111111111111111111111111111111111";   // сезон 1
    const string H2 = "2222222222222222222222222222222222222222";   // сезон 2
    const string H3 = "3333333333333333333333333333333333333333";
    const int ID = 229564;

    const string N1 = "Телохранители / Сезон: 1 / Серии: 1-16 из 16 (Константин Смирнов) [2023, Комедия, HDTV 1080p]";
    const string N2 = "Телохранители / Сезон: 2 / Серии: 1-16 из 16 (Владимир Битоков) [2024-2025, Комедия, HDTV 1080p]";

    static string Torrent(string hash, string name, long addedOn, double progress = 1.0, long size = 100)
        => $"{{\"hash\":\"{hash}\",\"name\":{JsonQ(name)},\"progress\":{progress.ToString(CultureInfo.InvariantCulture)},"
         + $"\"state\":\"{(progress >= 1 ? "queuedUP" : "downloading")}\",\"size\":{size},\"save_path\":\"/downloads\","
         + $"\"content_path\":\"/downloads/x\",\"added_on\":{addedOn},\"completion_on\":0}}";

    static string JsonQ(string s) => new JValue(s).ToString(Newtonsoft.Json.Formatting.None);

    static void Meta(string hash, int id, string mediaType = "tv", string title = "Телохранители")
        => Access.SaveMeta(hash, new JObject { ["id"] = id, ["media_type"] = mediaType, ["title"] = title });

    static async Task<JArray> RunList(string torrentsJson)
    {
        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", torrentsJson).BuildHandler());
        try
        {
            var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            return JArray.Parse(Assert.IsType<ContentResult>(await ctrl.List()).Content);
        }
        finally { Access.ResetQbitFake(); }
    }

    /// <summary>Ответ qBit «файлы раздачи» для каждого хеша отдельно (роутинг по подстроке URL).</summary>
    static async Task<JArray> RunEpisodes(string hash, params (string h, string files)[] routes)
    {
        var fake = new FakeQbit();
        foreach (var (h, files) in routes) fake.Json("files?hash=" + h, files);
        Access.SeedQbitFake(fake.BuildHandler());
        try
        {
            var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            return JArray.Parse(Assert.IsType<ContentResult>(await ctrl.Episodes(hash)).Content);
        }
        finally { Access.ResetQbitFake(); }
    }

    static string Files(string prefix, int season, int from, int to, double progress = 1.0, long size = 100)
    {
        var arr = new JArray();
        for (int e = from; e <= to; e++)
            arr.Add(new JObject
            {
                ["index"] = e - 1,
                ["name"] = $"{prefix}.S{season:00}.E{e:00}.1080p.mkv",
                ["size"] = size,
                ["progress"] = progress,
                ["priority"] = 1
            });
        return arr.ToString(Newtonsoft.Json.Formatting.None);
    }

    // ── /qdl/list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Сезоны_одного_сериала_становятся_одной_карточкой()
    {
        TestEnv.FreshCache();
        Meta(H1, ID); Meta(H2, ID);

        var list = await RunList("[" + Torrent(H2, N2, 200, size: 300) + "," + Torrent(H1, N1, 100, size: 100) + "]");

        var card = Assert.Single(list).ToObject<JObject>();
        Assert.Equal(H1, card.Value<string>("hash"));          // главная — самая ранняя по added
        Assert.Equal(400, card.Value<long?>("size"));          // размер суммируется
        Assert.Equal(100, card.Value<long?>("added"));         // карточка появилась с первой раздачей
        Assert.Equal(200, card.Value<long?>("activity"));      // всплывает по самой свежей части

        var parts = (JArray)card["parts"];
        Assert.Equal(2, parts.Count);
        Assert.Equal(new[] { H1, H2 }, parts.Select(p => p.Value<string>("hash")).ToArray());      // порядок — по сезонам
        Assert.Equal(new[] { 1, 2 }, parts.Select(p => p.Value<int>("season")).ToArray());
        Assert.Equal(new[] { 1, 2 }, ((JArray)card["seasons"]).Select(x => (int)x).ToArray());
    }

    [Fact]
    public async Task Прогресс_склеенной_карточки_взвешен_по_размеру()
    {
        TestEnv.FreshCache();
        Meta(H1, ID); Meta(H2, ID);

        // 20 ГБ скачаны целиком, 1 ГБ пуст — «половина сериала» была бы враньём
        var list = await RunList("[" + Torrent(H1, N1, 100, progress: 1.0, size: 20_000)
                                     + "," + Torrent(H2, N2, 200, progress: 0.0, size: 1_000) + "]");

        var card = Assert.Single(list).ToObject<JObject>();
        Assert.Equal(20_000d / 21_000d, card.Value<double>("progress"), 3);
        Assert.Equal("downloading", card.Value<string>("state"));   // недокачанная часть решает состояние
    }

    [Fact]
    public async Task Чужой_сериал_и_фильм_с_тем_же_номером_не_склеиваются()
    {
        TestEnv.FreshCache();
        Meta(H1, ID);
        Meta(H2, ID + 1);              // другой сериал
        Meta(H3, ID, "movie");         // тот же номер, но movie — у TMDB это ДРУГОЙ объект

        var list = await RunList("[" + Torrent(H1, N1, 100) + "," + Torrent(H2, N2, 200) + "," + Torrent(H3, "Фильм", 300) + "]");

        Assert.Equal(3, list.Count);
        Assert.All(list, x => Assert.Null(x["parts"]));
    }

    [Fact]
    public async Task Карточка_jut_su_в_группу_не_попадает()
    {
        string cache = TestEnv.FreshCache();
        string mp4 = Path.Combine(cache, "anime.s01e01.mp4");
        File.WriteAllText(mp4, "x");
        Access.SaveLocal(H2, new JObject
        {
            ["name"] = "Аниме",
            ["dir"] = cache.Replace('\\', '/'),
            ["size"] = 1,
            ["added"] = 50,
            ["files"] = new JArray { new JObject { ["index"] = 0, ["name"] = "anime.s01e01.mp4", ["path"] = mp4.Replace('\\', '/'), ["size"] = 1L } },
            ["jut"] = new JObject { ["slug"] = "anime", ["tlPrefix"] = "jut:anime" }
        });
        Meta(H1, ID); Meta(H2, ID);   // мета аниме случайно указывает на тот же сериал

        var list = await RunList("[" + Torrent(H1, N1, 100) + "]");

        Assert.Equal(2, list.Count);
        Assert.All(list, x => Assert.Null(x["parts"]));
    }

    [Fact]
    public async Task Киллсвитч_возвращает_две_карточки()
    {
        TestEnv.FreshCache();
        Meta(H1, ID); Meta(H2, ID);
        ModInit.conf.mergeSeasons = false;
        try
        {
            var list = await RunList("[" + Torrent(H1, N1, 100) + "," + Torrent(H2, N2, 200) + "]");
            Assert.Equal(2, list.Count);
            Assert.All(list, x => Assert.Null(x["parts"]));
        }
        finally { ModInit.conf.mergeSeasons = true; }
    }

    // ── /qdl/episodes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Плейлист_группы_идёт_сезон_за_сезоном_по_сериям()
    {
        TestEnv.FreshCache();
        Meta(H1, ID); Meta(H2, ID);

        // спрашиваем по хешу ВТОРОГО сезона — ответ обязан быть тем же, что и по первому
        var arr = await RunEpisodes(H2,
            (H1, Files("Show", 1, 1, 3)),
            (H2, Files("Show", 2, 1, 2)));

        Assert.Equal(new[] { "s1e1", "s1e2", "s1e3", "s2e1", "s2e2" },
                     arr.Select(x => x.Value<string>("epkey")).ToArray());
        // серия несёт СВОЙ hash — по нему клиент строит /qdl/stream (механика доноров охоты)
        Assert.Equal(new[] { H1, H1, H1, H2, H2 }, arr.Select(x => x.Value<string>("hash")).ToArray());
        // ключ таймлайна общий на сериал: прогресс не разъедется между сезонами
        Assert.Equal("t" + ID + ":s1e1", arr[0].Value<string>("tl"));
        Assert.Equal("t" + ID + ":s2e1", arr[3].Value<string>("tl"));
    }

    [Fact]
    public async Task Одна_серия_в_двух_раздачах_остаётся_одной_записью()
    {
        TestEnv.FreshCache();
        Meta(H1, ID); Meta(H2, ID);

        // тот же сезон перекачан второй раздачей: у H1 серия ещё качается, у H2 готова
        var arr = await RunEpisodes(H1,
            (H1, Files("Show", 1, 1, 2, progress: 0.4, size: 100)),
            (H2, Files("Show", 1, 1, 2, progress: 1.0, size: 90)));

        Assert.Equal(new[] { "s1e1", "s1e2" }, arr.Select(x => x.Value<string>("epkey")).ToArray());
        Assert.All(arr, x => Assert.Equal(H2, x.Value<string>("hash")));   // побеждает докачанная копия
    }

    [Fact]
    public async Task Одиночная_карточка_отвечает_как_раньше()
    {
        TestEnv.FreshCache();
        Meta(H1, ID);

        var arr = await RunEpisodes(H1, (H1, Files("Show", 1, 1, 2)));

        Assert.Equal(new[] { "s1e1", "s1e2" }, arr.Select(x => x.Value<string>("epkey")).ToArray());
        Assert.All(arr, x => Assert.Equal(H1, x.Value<string>("hash")));
    }

    [Fact]
    public async Task Киллсвитч_отдаёт_серии_только_своей_раздачи()
    {
        TestEnv.FreshCache();
        Meta(H1, ID); Meta(H2, ID);
        ModInit.conf.mergeSeasons = false;
        try
        {
            var arr = await RunEpisodes(H2, (H1, Files("Show", 1, 1, 3)), (H2, Files("Show", 2, 1, 2)));
            Assert.Equal(new[] { "s2e1", "s2e2" }, arr.Select(x => x.Value<string>("epkey")).ToArray());
        }
        finally { ModInit.conf.mergeSeasons = true; }
    }

    [Fact]
    public async Task Мёртвый_сиблинг_не_ломает_плейлист()
    {
        TestEnv.FreshCache();
        Meta(H1, ID); Meta(H2, ID);   // H2 в qBit больше нет (мету не подчистили)

        var arr = await RunEpisodes(H1, (H1, Files("Show", 1, 1, 2)));

        Assert.Equal(new[] { "s1e1", "s1e2" }, arr.Select(x => x.Value<string>("epkey")).ToArray());
    }
}
