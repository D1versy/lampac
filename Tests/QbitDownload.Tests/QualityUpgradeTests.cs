using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Апгрейд качества уже скачанного (QualityUpgrade.cs, qdl 2.77).
//
// Повод: «Телохранители» s2e11–s2e14 приехали в 360p при 720p у соседних серий —
// портал выложил свежие серии раньше, чем дотранскодил высокие дорожки. Ключ серии
// качества не различает, поэтому сами по себе они не обновились бы НИКОГДА.
//
// Здесь стерегутся ровно три места, где механизм ломается молча.
// ─────────────────────────────────────────────────────────────────────────────
public class QualityUpgradeTests
{
    static void File_(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "x");
    }

    // ── разбор качества из имени ──────────────────────────────────────────

    [Theory]
    [InlineData("3-5059049.s02e11.360p", 360)]
    [InlineData("3-5059049.s02e01.720p", 720)]
    [InlineData("3-5059049.film.2160p", 2160)]
    [InlineData("3-5059049.s02e11", 0)]        // «Авто»/мастер — суффикса нет
    public void Качество_читается_из_имени_XSMART(string baseName, int expect)
        => Assert.Equal(expect, QbitController.XsmartQualityFromName(baseName));

    [Fact]
    public void Ключ_серии_качества_не_различает()
    {
        // Это сознательно: s2e11 в 360p и в 720p — одна и та же серия, у неё один ключ
        // таймлайна и одна отметка просмотра. Именно поэтому апгрейду нужен ОТДЕЛЬНЫЙ
        // индекс качества, а не правка ключа.
        Assert.Equal("s2e11", QbitController.XsmartKeyFromName("3-5059049.s02e11.360p"));
        Assert.Equal("s2e11", QbitController.XsmartKeyFromName("3-5059049.s02e11.720p"));
    }

    [Fact]
    public void Качество_на_диске_берёт_максимум_а_Авто_считает_потолком()
    {
        var (_, downloads) = XsAccess.Env();
        string dir = Path.Combine(downloads, "3-400");
        File_(dir, "3-400.s01e01.360p.mp4");
        File_(dir, "3-400.s01e01.720p.mp4");
        File_(dir, "3-400.s01e02.480p.mp4");

        Assert.Equal(720, QbitController.XsmartDiskQualityOf("3-400", "s1e1"));
        Assert.Equal(480, QbitController.XsmartDiskQualityOf("3-400", "s1e2"));
        Assert.Equal(-1, QbitController.XsmartDiskQualityOf("3-400", "s1e3"));   // файла нет

        // 🔴 Файл без суффикса апгрейду не подлежит никогда: сравнивать не с чем, иначе
        // каждый HLS-файл выглядел бы вечно апгрейдируемым.
        File_(dir, "3-400.s01e04.mp4");
        Assert.Equal(int.MaxValue, QbitController.XsmartDiskQualityOf("3-400", "s1e4"));
    }

    // ── грабля №1: ключ диска не должен закрывать upgrade-намерение ────────

    [Fact]
    public void Upgrade_намерение_не_снимается_ключом_диска()
    {
        // 🔴 Самая острая грань. Обычное намерение лежащий на диске файл закрывает —
        // и правильно делает. Upgrade-намерение он закрывать НЕ должен, иначе механизм
        // просто не работает, и без единой ошибки в логах.
        var (_, downloads) = XsAccess.Env();
        using var pin = XsAccess.PinWorker();
        File_(Path.Combine(downloads, "3-401"), "3-401.s02e11.360p.mp4");

        var ep = WantsAccess.Ep("28260", 2, 11);
        QbitController.XsmartWantsCommit("3-401", 3, "401", "3", "Тайтл", new[] { ep }, "upgrade", upgradeTo: 720);

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        Assert.Contains(XsAccess.Queue(), x => x.epkey == "s2e11");
        Assert.True(DownloadWants.Xsmart.Has("3-401", "s2e11"));
    }

    [Fact]
    public void Достигнутое_качество_снимает_upgrade_намерение()
    {
        // Обратная сторона: как только на диске появилось 720p, долг обязан закрыться —
        // иначе перекачка пошла бы по кругу.
        var (_, downloads) = XsAccess.Env();
        using var pin = XsAccess.PinWorker();
        File_(Path.Combine(downloads, "3-402"), "3-402.s02e11.720p.mp4");

        var ep = WantsAccess.Ep("28260", 2, 11);
        QbitController.XsmartWantsCommit("3-402", 3, "402", "3", "Тайтл", new[] { ep }, "upgrade", upgradeTo: 720);

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        Assert.Empty(XsAccess.Queue());
        Assert.False(DownloadWants.Xsmart.HasTitle("3-402"));
    }

    // ── грабля №2: старая копия обязана уйти, но только после успеха ──────

    [Fact]
    public void Старая_копия_удаляется_только_при_апгрейде_и_только_после_успеха()
    {
        // Качество входит в ИМЯ, поэтому File.Delete(dst) в качалке старый «.360p.mp4»
        // не трогает — на диске остались бы обе копии с одним ключом таймлайна.
        var (_, downloads) = XsAccess.Env();
        string dir = Path.Combine(downloads, "3-403");
        File_(dir, "3-403.s02e11.360p.mp4");
        File_(dir, "3-403.s02e11.720p.mp4");          // «новый» файл уже переименован
        File_(dir, "3-403.s02e12.360p.mp4");          // соседняя серия — не трогать

        var ep = WantsAccess.Ep("28260", 2, 11);
        var killed = XsQuality.DropOldCopies("3-403", ep, upgradeTo: 720,
                                             dst: Path.Combine(dir, "3-403.s02e11.720p.mp4"));

        Assert.Single(killed);
        Assert.False(File.Exists(Path.Combine(dir, "3-403.s02e11.360p.mp4")));
        Assert.True(File.Exists(Path.Combine(dir, "3-403.s02e11.720p.mp4")));
        Assert.True(File.Exists(Path.Combine(dir, "3-403.s02e12.360p.mp4")));
    }

    [Fact]
    public void Обычная_загрузка_старых_копий_не_трогает()
    {
        // upgradeTo == 0 → уборки нет вовсе. Иначе обычная докачка сносила бы файлы,
        // которые никто не просил менять.
        var (_, downloads) = XsAccess.Env();
        string dir = Path.Combine(downloads, "3-404");
        File_(dir, "3-404.s02e11.360p.mp4");
        File_(dir, "3-404.s02e11.720p.mp4");

        var ep = WantsAccess.Ep("28260", 2, 11);
        var killed = XsQuality.DropOldCopies("3-404", ep, upgradeTo: 0,
                                             dst: Path.Combine(dir, "3-404.s02e11.720p.mp4"));

        Assert.Empty(killed);
        Assert.True(File.Exists(Path.Combine(dir, "3-404.s02e11.360p.mp4")));
    }

    // ── грабля №3: цикл, когда портал так и не дотранскодил ───────────────

    [Fact]
    public void Кеш_решений_гасит_повторную_пробу_на_неделю()
    {
        XsAccess.Env();
        ModInit.conf.qualityRecheckDays = 7;
        ModInit.conf.qualityMaxUpgrades = 3;

        Assert.False(QualityCaches.Xsmart.Skip("3-405:s2e11", 7, 3));
        QualityCaches.Xsmart.Note("3-405:s2e11", best: 360, up: false);
        Assert.True(QualityCaches.Xsmart.Skip("3-405:s2e11", 7, 3));

        // ⚠️ Отрицательное решение обязано ПРОТУХАТЬ: новинки дотранскодят позже, и «портал
        // отдаёт только 360p» верно ровно сегодня. Отматываем штамп на 10 дней назад
        // прямо в файле — заодно проверяется, что кеш переживает перезагрузку.
        string path = Path.Combine(XsmartNet.DataDir(), "quality.json");
        JsonStore.Flush();
        var raw = JObject.Parse(File.ReadAllText(path));
        raw["items"]["3-405:s2e11"]["at"] = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();
        File.WriteAllText(path, raw.ToString());
        JsonStore.Forget(path);
        QualityCaches.Xsmart.Reset();

        Assert.False(QualityCaches.Xsmart.Skip("3-405:s2e11", 7, 3));
    }

    [Fact]
    public void Кап_попыток_гасит_серию_навсегда()
    {
        // Иначе серия, которую портал отдаёт только в 480p при цели 720p, перекачивалась бы
        // раз в неделю вечно.
        XsAccess.Env();
        for (int i = 0; i < 3; i++) QualityCaches.Xsmart.Note("3-406:s2e11", best: 480, up: true);
        Assert.True(QualityCaches.Xsmart.Skip("3-406:s2e11", recheckDays: 0, maxUps: 3));
    }

    // ── границы ───────────────────────────────────────────────────────────

    [Fact]
    public void Выключенный_апгрейд_не_делает_ничего()
    {
        XsAccess.Env();
        ModInit.conf.xsmartQualityTarget = 0;
        var res = WantsAccess.Ctrl().XsmartQualityScan(3, "407").Result;
        var jo = JObject.Parse(WantsAccess.Body(res));
        Assert.False(jo.Value<bool>("ok"));
        Assert.Equal("DISABLED", jo.Value<string>("error"));
    }

    [Fact]
    public void Скан_без_apply_ничего_не_ставит()
    {
        // Разовая подтяжка — явная ручка с dry-run: молча съесть гигабайты трафика нельзя.
        var (_, downloads) = XsAccess.Env();
        using var pin = XsAccess.PinWorker();
        File_(Path.Combine(downloads, "3-408"), "3-408.s01e01.720p.mp4");

        // цель уже достигнута — сети не будет вовсе
        var rep = QbitController.XsmartQualityScanTitle(3, "408", min: 720, apply: false, budget: 5).Result;
        Assert.True(rep.Value<bool>("ok"));
        Assert.Equal(0, rep.Value<int?>("upgradable"));
        Assert.Equal(0, rep.Value<int?>("queued"));
        Assert.Empty(XsAccess.Queue());
    }

    [Fact]
    public void Jut_качество_читается_из_имени_с_паддингом()
    {
        Assert.Equal(1080, QbitController.JutQualityFromName("solo-leveling.s01e05.1080p"));
        Assert.Equal(0, QbitController.JutQualityFromName("solo-leveling.s01e05"));
    }

    [Fact]
    public void Jut_качество_на_диске_ищется_по_ключу_а_не_по_строке()
    {
        // Ключ диска у jut — имя файла С паддингом; серия 5 и серия 50 не должны
        // путаться (историческая грабля StartsWith).
        var (_, downloads) = JutWatchAccess.Env();
        string dir = Path.Combine(downloads, "q-anime");
        File_(dir, "q-anime.s01e05.480p.mp4");
        File_(dir, "q-anime.s01e50.1080p.mp4");

        var e5 = new JutEp { slug = "q-anime", kind = JutEpKind.Episode, season = 1, num = 5 };
        var e50 = new JutEp { slug = "q-anime", kind = JutEpKind.Episode, season = 1, num = 50 };
        Assert.Equal(480, QbitController.JutDiskQualityOf("q-anime", e5));
        Assert.Equal(1080, QbitController.JutDiskQualityOf("q-anime", e50));
    }
}

/// <summary>Доступ к private-static уборке старых копий.</summary>
static class XsQuality
{
    public static List<string> DropOldCopies(string sref, XsmartEp ep, int upgradeTo, string dst)
    {
        var t = typeof(QbitController).GetNestedType("XsmartGrabItem", BindingFlags.NonPublic);
        object it = Activator.CreateInstance(t);
        const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        t.GetField("sref", IF).SetValue(it, sref);
        t.GetField("ep", IF).SetValue(it, ep);
        t.GetField("upgradeTo", IF).SetValue(it, upgradeTo);
        return (List<string>)Access.Call("XsmartDropOldCopies", it, dst);
    }
}
