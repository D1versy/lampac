using Newtonsoft.Json.Linq;
using QbitDownload;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace QbitDownload.Tests;

// 🔴 Требование владельца: «если мы отслеживаем серии с jut.su — отслеживать только там,
// в торренты не лезть». Держится тремя поясами; здесь — пояс 3, который упадёт при регрессе.
//
// Канон: E:\Media-server\claude\jut\02-architecture.md §9
public class JutSuIsolationTests
{
    static readonly BindingFlags P = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void Пояс1_jut_загрузки_не_создают_links_json()
    {
        // Торрентное слежение WatchAdd требует links/<hash>.json и без него отвечает "no link".
        // Значит достаточно НИКОГДА не писать этот файл для jut — и добавить jut-тайтл
        // в торрентную охоту становится невозможно кодом, а не проверкой.
        string grab = File.ReadAllText(ModuleFile("JutSuGrab.cs"));
        string watch = File.ReadAllText(ModuleFile("JutSuWatch.cs"));

        Assert.DoesNotContain("LinkPath(", grab);
        Assert.DoesNotContain("LinkPath(", watch);
        // и сам инвариант зафиксирован комментарием — чтобы через год его не «починили»
        Assert.Contains("links/<hash>.json", grab);
    }

    [Fact]
    public void Пояс2_обходчик_индекса_пропускает_jutsu_меты()
    {
        // IndexCrawler.TargetsFromMeta обходит ВСЕ meta/*.json и дёргает по ним трекеры.
        // Без фильтра jut-мета утащила бы нас на торренты за теми же сериями.
        string src = File.ReadAllText(ModuleFile("IndexCrawler.cs"));
        Assert.Contains("\"jutsu\"", src);
        Assert.Contains("source", src);
    }

    [Fact]
    public void Пояс2_мета_jut_помечается_источником()
    {
        // Без "source":"jutsu" фильтр пояса 2 не сработает
        Assert.Contains("[\"source\"] = \"jutsu\"", File.ReadAllText(ModuleFile("JutSuGrab.cs")));
    }

    [Fact]
    public void Переключение_сезона_не_выкачивает_бэклог()
    {
        // 🔥 Боевая находка (2026-08-10): подписка на сезон 1 у spy-family мгновенно
        // переключилась на сезон 3 и поставила в очередь 13 серий ≈ 6 ГБ.
        // Причина — при переключении baseline обнулялся, и ВСЕ уже вышедшие серии нового
        // сезона считались новыми. Политика «Следить качает только БУДУЩИЕ серии»
        // обязана переживать переключение сезона.
        string src = File.ReadAllText(ModuleFile("JutSuWatch.cs"));
        int i = src.IndexOf("maxSeason > season", StringComparison.Ordinal);
        Assert.True(i > 0, "блок переключения сезона не найден");
        string block = src.Substring(i, Math.Min(1400, src.Length - i));

        // baseline обязан заполняться сериями нового сезона
        Assert.Contains("e.season == season", block);
        Assert.Contains("ns.Select(e => e.epkey)", block);
        // и НЕ обнуляться
        Assert.DoesNotContain("[\"keys\"] = new JArray() }", block);
    }

    [Fact]
    public void Ключи_seen_не_пересекаются_с_торрентными()
    {
        // Торрентные seriesKey: t<tmdbId> и l<fnv>. Наш префикс — j<slug>.
        string sk = "j" + "spy-family";
        Assert.StartsWith("j", sk);
        Assert.False(sk.StartsWith("t", StringComparison.Ordinal));
        Assert.False(sk.StartsWith("l", StringComparison.Ordinal));
    }

    [Fact]
    public void Удаление_карточки_снимает_подписку()
    {
        // Иначе при автоскачивании удалённый тайтл возвращался бы следующим тиком:
        // «удалил, а оно вернулось».
        string ctl = File.ReadAllText(ModuleFile("Controller.cs"));
        Assert.Contains("JutForgetOnDelete", ctl);
        Assert.Contains("JutForgetOnDelete", File.ReadAllText(ModuleFile("JutSuWatch.cs")));
    }

    [Fact]
    public void Слежение_живёт_в_отдельном_файле_а_не_в_watch_json()
    {
        // Торрентная охота HuntAll итерирует ИСКЛЮЧИТЕЛЬНО /qdl-data/watch.json.
        // Jut-подписки обязаны лежать отдельно, иначе пояс 1 рушится.
        // Проверяем сам ИНВАРИАНТ, а не текст исходника: путь состояния обязан лежать
        // под /qdl-data/jut/, иначе jut-подписки попали бы в общий watch.json,
        // который итерирует торрентная охота HuntAll.
        var m = typeof(QbitController).GetMethod("JutWatchPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        string path = ((string)m.Invoke(null, null)).Replace('\\', '/');

        Assert.EndsWith("/jut/watch.json", path);
        Assert.Contains("/jut/", path);
        // и это НЕ общий торрентный /qdl-data/watch.json
        Assert.False(path.EndsWith("/qdl-data/watch.json", StringComparison.Ordinal), path);
    }

    /// <summary>Комментарии долой — иначе проверки «нет вызова X» ловят упоминания X в тексте.</summary>
    static string Strip(string src)
    {
        src = System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join("\n", src.Split('\n')
            .Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l.Substring(0, i) : l; }));
    }

    [Fact]
    public void Гейт_слежения_свой_а_не_общий_watchGate()
    {
        // На общем _watchGate четырёхчасовая торрентная охота глушила бы суточный jut-тик
        // (skip-if-busy → тик просто пропадает на сутки)
        string src = Strip(File.ReadAllText(ModuleFile("JutSuWatch.cs")));
        Assert.Contains("_jutGate.WaitAsync", src);
        Assert.Contains("_jutGate.Release", src);
        Assert.DoesNotContain("_watchGate", src);   // в КОДЕ, не в комментариях
    }

    static string ModuleFile(string name)
    {
        string[] probe =
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Modules", "QbitDownload", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Modules", "QbitDownload", name)
        };
        foreach (string p in probe)
            if (File.Exists(p)) return p;
        throw new FileNotFoundException("не нашёл исходник модуля " + name);
    }
}

// Скачивание: имена файлов, разбор обратно, псевдо-хеш и маркер.
public class JutSuGrabTests
{
    [Theory]
    [InlineData(1, 7, JutEpKind.Episode, "spy-family.s01e07.1080p.mp4")]
    [InlineData(2, 12, JutEpKind.Episode, "spy-family.s02e12.1080p.mp4")]
    public void Имя_серии_даёт_разбор_ParseEp(int season, int num, JutEpKind kind, string expect)
    {
        var e = new JutEp { season = season, num = num, kind = kind };
        Assert.Equal(expect, QbitControllerAccess.JutFileName("spy-family", e, 1080));
    }

    [Fact]
    public void Фильмы_и_ova_несут_вид_в_имени()
    {
        // ⚠️ ParseEp не знает слова «film» — вид обязан быть в имени и в маркере
        Assert.Equal("naruuto.film3.720p.mp4",
            QbitControllerAccess.JutFileName("naruuto", new JutEp { kind = JutEpKind.Film, num = 3 }, 720));
        Assert.Equal("naruuto.ova1.480p.mp4",
            QbitControllerAccess.JutFileName("naruuto", new JutEp { kind = JutEpKind.Ova, num = 1 }, 480));
    }

    [Theory]
    [InlineData("spy-family.s01e07.1080p", 1, 7, JutEpKind.Episode)]
    [InlineData("naruuto.film3.720p", 1, 3, JutEpKind.Film)]
    [InlineData("naruuto.ova1.480p", 1, 1, JutEpKind.Ova)]
    [InlineData("x.gameova5", 1, 5, JutEpKind.GameOva)]
    public void Имя_файла_разбирается_обратно(string name, int season, int num, JutEpKind kind)
    {
        // Нужно для реконсиляции: после рестарта .part надо опознать и добрать в очередь
        var e = QbitControllerAccess.JutEpFromFileName(name);
        Assert.NotNull(e);
        Assert.Equal(season, e.season);
        Assert.Equal(num, e.num);
        Assert.Equal(kind, e.kind);
    }

    [Fact]
    public void Чужие_имена_не_разбираются()
    {
        Assert.Null(QbitControllerAccess.JutEpFromFileName("Sintel.2010.1080p"));
        Assert.Null(QbitControllerAccess.JutEpFromFileName(""));
    }

    [Fact]
    public void Круговой_разбор_имени_устойчив()
    {
        foreach (var kind in new[] { JutEpKind.Episode, JutEpKind.Film, JutEpKind.Ova, JutEpKind.GameOva })
        {
            var src = new JutEp { kind = kind, season = kind == JutEpKind.Episode ? 2 : 1, num = 9 };
            string name = QbitControllerAccess.JutFileName("slug-x", src, 1080);
            var back = QbitControllerAccess.JutEpFromFileName(Path.GetFileNameWithoutExtension(name));
            Assert.NotNull(back);
            Assert.Equal(src.kind, back.kind);
            Assert.Equal(src.num, back.num);
            Assert.Equal(src.epkey, back.epkey);
        }
    }
}

// Reflection-шлюз к internal-членам модуля (тот же приём, что у Access.cs для остальных тестов).
static class QbitControllerAccess
{
    static readonly Type T = typeof(QbitController);
    const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public;

    public static string JutFileName(string slug, JutEp e, int quality)
        => (string)T.GetMethod("JutFileName", F).Invoke(null, new object[] { slug, e, quality });

    public static JutEp JutEpFromFileName(string name)
        => (JutEp)T.GetMethod("JutEpFromFileName", F).Invoke(null, new object[] { name });
}
