using System;
using System.IO;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// 🔴 Требование владельца, то же, что и для jut.su: за сериями раздела XSMART в ТОРРЕНТЫ
// не лезем. Держится тремя поясами; здесь — пояс 3, который упадёт при регрессе.
//
// Пояс 1 (структурный, главный): links/<hash>.json для xsmart не создаётся никогда, а без
//   него WatchAdd отвечает "no link" — добавить такой тайтл в торрентную охоту невозможно КОДОМ.
// Пояс 2: IndexCrawler.TargetsFromMeta пропускает меты с "source":"xsmart".
public class XsmartIsolationTests
{
    [Fact]
    public void Пояс1_загрузки_xsmart_не_создают_links_json()
    {
        string grab = File.ReadAllText(ModuleFile("XsmartGrab.cs"));
        string watch = File.ReadAllText(ModuleFile("XsmartWatch.cs"));
        string net = File.ReadAllText(ModuleFile("Xsmart.cs"));

        Assert.DoesNotContain("LinkPath(", grab);
        Assert.DoesNotContain("LinkPath(", watch);
        Assert.DoesNotContain("LinkPath(", net);
        // и сам инвариант зафиксирован комментарием — чтобы через год его не «починили»
        Assert.Contains("links/<hash>.json", net);
    }

    [Fact]
    public void Пояс2_обходчик_индекса_пропускает_обоих_не_торрентных()
    {
        // Множество, а не сравнение с одной строкой: каждый новый не-торрентный раздел
        // обязан попадать сюда, иначе обходчик тихо начнёт искать его тайтлы на трекерах.
        Assert.True(QbitController.NonTorrentSource("jutsu"));
        Assert.True(QbitController.NonTorrentSource("xsmart"));
        Assert.True(QbitController.NonTorrentSource("XSMART"));      // сравнение регистронезависимое
        Assert.False(QbitController.NonTorrentSource("rutracker"));
        Assert.False(QbitController.NonTorrentSource(null));
    }

    [Fact]
    public void Пояс2_мета_xsmart_помечается_источником()
    {
        // Без "source":"xsmart" фильтр пояса 2 не сработает.
        Assert.Contains("[\"source\"] = \"xsmart\"", File.ReadAllText(ModuleFile("XsmartGrab.cs")));
    }

    [Fact]
    public void Удаление_загрузки_снимает_подписку()
    {
        // 🔴 Иначе при автоскачивании следующая серия молча пересоздаст карточку и папку:
        // «удалил, а оно вернулось». PurgeCache про отдельный файл подписок не знает.
        string ctrl = File.ReadAllText(ModuleFile("Controller.cs"));
        int i = ctrl.IndexOf("XsmartForgetOnDelete", StringComparison.Ordinal);
        Assert.True(i > 0, "в /qdl/delete нет снятия подписки XSMART");
    }

    [Fact]
    public void Скачивание_ходит_только_в_свой_прокси()
    {
        // Инвариант №1 раздела: ни одного прямого обращения к хостам XSMART/CDN.
        foreach (string f in new[] { "Xsmart.cs", "XsmartGrab.cs", "XsmartWatch.cs" })
        {
            string src = File.ReadAllText(ModuleFile(f));
            Assert.DoesNotContain("xsmart.tv", src);
            Assert.DoesNotContain("daycamp", src);
            Assert.DoesNotContain("zerocdn", src);
        }
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
