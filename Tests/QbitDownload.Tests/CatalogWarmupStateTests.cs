using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Состояние прогрева каталога: LRU (<c>Note</c>), карантин мёртвых адресов, персист
/// (<c>Save</c>/<c>Load</c>) и приём рядов от дома на реплике (<c>ImportRowPaths</c>).
///
/// До qdl 2.65 всё это не было покрыто ничем: тесты знали только чистые предикаты. Цена
/// незакрытости — боевой инцидент 23.08.2026 (ручной прогон замеров налил 61 мусорную запись
/// из 128 в LRU, два адреса из них держали строку «CUB каталог» красной) и тихий баг с _dirty,
/// из-за которого подвинутый клиентом lastSeen не доезжал до диска.
///
/// ⚠️ Статика класса общая на весь прогон — каждый кейс начинает с CatalogWarmup.ResetForTests()
/// и TestEnv.FreshCache() (свой cachePath → свой StorePath).
/// </summary>
public class CatalogWarmupStateTests
{
    const string H = "192.168.87.24:9118";

    static void Fresh()
    {
        TestEnv.FreshCache();
        CatalogWarmup.ResetForTests();

        // ⚠️ ModInit.conf — общий на весь прогон, а кейсы ниже крутят кап LRU. Без возврата
        // к дефолтам «маленький кап» протёк бы в соседние классы (ReplicaTests тоже зовёт Note).
        ModInit.conf.catalogWarmupMaxUrls = 128;
        ModInit.conf.catalogWarmupDeadAfter = 3;
        ModInit.conf.catalogWarmupDeadRetryHours = 24;
    }

    static string Store() => Path.Combine(ModInit.conf.cachePath, "catalog-warmup.json");

    // ─────────── Note: _dirty и LRU ───────────

    [Fact]
    public void Note_NewRow_MarksDirty()
    {
        Fresh();
        CatalogWarmup.Note("http", H, "/cub/tmdb./top/fire/movie?page=1");
        Assert.True(CatalogWarmup.DirtyForTests);
    }

    [Fact]
    public void Note_ExistingRow_MarksDirty()
    {
        // 🔴 Регресс на баг до 2.65: ранний `if (!added) return;` стоял ВЫШЕ `_dirty = true`,
        // поэтому подвинутый живым клиентом lastSeen не доезжал до диска. После падения по
        // питанию (хост падает ~23 раза в месяц) прунинг мог выкинуть активно используемый ряд.
        Fresh();
        CatalogWarmup.Note("http", H, "/cub/tmdb./top/fire/movie?page=1");

        CatalogWarmup.DirtyForTests = false;
        CatalogWarmup.Note("http", H, "/cub/tmdb./top/fire/movie?page=1");   // тот же URL повторно

        Assert.True(CatalogWarmup.DirtyForTests);
        Assert.Single(CatalogWarmup.RowPathsForTests());
    }

    [Fact]
    public void Note_Lru_EvictsOldestWhenNothingQuarantined()
    {
        Fresh();
        ModInit.conf.catalogWarmupMaxUrls = 8;   // кламп в Note — Math.Max(8, …)

        for (int i = 0; i < 9; i++)
            CatalogWarmup.Note("http", H, "/cub/tmdb./collections/" + i + "?email=");

        var paths = CatalogWarmup.RowPathsForTests();
        Assert.Equal(8, paths.Count);
        Assert.DoesNotContain("/cub/tmdb./collections/0?email=", paths);   // самый давний
    }

    [Fact]
    public void Note_Lru_EvictsQuarantinedBeforeFresh()
    {
        // Мёртвый адрес не должен вытеснять живой ряд «по возрасту»: именно так мусорная пачка
        // 23.08 занимала половину LRU и выдавливала настоящие ряды главной.
        Fresh();
        ModInit.conf.catalogWarmupMaxUrls = 8;

        for (int i = 0; i < 8; i++)
            CatalogWarmup.Note("http", H, "/cub/tmdb./collections/" + i + "?email=");

        // самый СВЕЖИЙ из восьми отправляем в карантин
        Assert.True(CatalogWarmup.MarkDeadForTests("http", H, "/cub/tmdb./collections/7?email=", DateTime.UtcNow));

        CatalogWarmup.Note("http", H, "/cub/tmdb./top/fire/movie?page=1");   // девятый → нужно вытеснение

        var paths = CatalogWarmup.RowPathsForTests();
        Assert.Equal(8, paths.Count);
        Assert.DoesNotContain("/cub/tmdb./collections/7?email=", paths);   // ушёл карантинный…
        Assert.Contains("/cub/tmdb./collections/0?email=", paths);         // …а не самый давний живой
    }

    [Fact]
    public void Note_RejectsNothing_FilterLivesInCaller()
    {
        // Note — примитив записи, фильтрация стоит на входах (OnRequest/Load/ImportRowPaths).
        // Тест фиксирует границу ответственности, чтобы её не «починили» дублированием.
        Fresh();
        CatalogWarmup.Note("http", H, "/cub/tmdb.cub.best/blocked&zzr=1");
        Assert.Single(CatalogWarmup.RowPathsForTests());
    }

    // ─────────── Load: накопленный мусор отсеивается при старте ───────────

    [Fact]
    public void Load_DropsJunkRows_FromExistingFile()
    {
        // 🔴 Слепок боевого /qdl-data/catalog-warmup.json на 24.08.2026. Именно этот механизм
        // избавляет от ручной хирургии по тому: мусор не переживает первый старт нового образа.
        Fresh();
        WriteStore(new
        {
            ver = 3,
            rows = new object[]
            {
                Row("/cub/tmdb./top/fire/movie?page=1&email="),                 // живой
                Row("/cub/tmdb.cub.rip/blocked"),                               // живой (qdl.js, апстрим 200)
                Row("/cub/tmdb./?sort=top\\&genre=27\\&email=&zzr=1"),          // shell-экранирование
                Row("/cub/tmdb.cub.best/blocked&zzr=1"),                        // '&' в пути, апстрим 404
                Row("/cub/tmdb./3/movie/550?api_key=k"),                        // деталь, не ряд
                Row("/cub/tmdb./?query=dune")                                   // поиск
            }
        });

        CatalogWarmup.Load();

        Assert.Equal(
            new[] { "/cub/tmdb./top/fire/movie?page=1&email=", "/cub/tmdb.cub.rip/blocked" },
            CatalogWarmup.RowPathsForTests());
    }

    [Fact]
    public void Load_FileWithoutQuarantineFields_TreatsRowsAsAlive()
    {
        // Обратная совместимость: в файле от старого образа полей карантина нет вовсе.
        Fresh();
        WriteStore(new { ver = 3, rows = new object[] { Row("/cub/tmdb./top/fire/movie?page=1") } });

        CatalogWarmup.Load();

        var st = CatalogWarmup.RowStateForTests("http", H, "/cub/tmdb./top/fire/movie?page=1");
        Assert.True(st.found);
        Assert.False(st.dead);
        Assert.Equal(0, st.fails);
    }

    [Fact]
    public void Load_V2BareArray_StillWorks()
    {
        // v2 — голый массив рядов. Ветку чтения не ломаем: иначе прогрев после обновления
        // начал бы с нуля и первые сутки грел вслепую.
        Fresh();
        File.WriteAllText(Store(), JsonSerializer.Serialize(new object[]
        {
            Row("/cub/tmdb./top/hundred/movie?page=1"),
            Row("/cub/tmdb.cub.best/blocked&zzr=1")     // фильтр работает и для v2
        }));

        CatalogWarmup.Load();

        Assert.Equal(new[] { "/cub/tmdb./top/hundred/movie?page=1" }, CatalogWarmup.RowPathsForTests());
    }

    [Fact]
    public void Load_BrokenFile_DoesNotThrow()
    {
        Fresh();
        File.WriteAllText(Store(), "{ это не json");

        CatalogWarmup.Load();   // молча пережить: питание падает, файл может быть обрезан

        Assert.Empty(CatalogWarmup.RowPathsForTests());
    }

    // ─────────── Save/Load: карантин переживает рестарт ───────────

    [Fact]
    public void SaveLoad_RoundTrip_KeepsQuarantine()
    {
        Fresh();
        string dir = ModInit.conf.cachePath;

        CatalogWarmup.Note("http", H, "/cub/tmdb./top/fire/movie?page=1");
        CatalogWarmup.Note("http", H, "/cub/tmdb./trailers/short/trailers/added?page=1");
        Assert.True(CatalogWarmup.MarkDeadForTests("http", H, "/cub/tmdb./trailers/short/trailers/added?page=1",
                                                   new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc), fails: 3));
        CatalogWarmup.Save();

        CatalogWarmup.ResetForTests();      // «рестарт процесса»
        ModInit.conf.cachePath = dir;       // тот же том
        CatalogWarmup.Load();

        var dead = CatalogWarmup.RowStateForTests("http", H, "/cub/tmdb./trailers/short/trailers/added?page=1");
        Assert.True(dead.found);
        Assert.True(dead.dead);
        Assert.Equal(3, dead.fails);

        var alive = CatalogWarmup.RowStateForTests("http", H, "/cub/tmdb./top/fire/movie?page=1");
        Assert.True(alive.found);
        Assert.False(alive.dead);
    }

    // ─────────── Реплика: экспорт/импорт ───────────

    [Fact]
    public void ExportRowPaths_SkipsQuarantined()
    {
        // Незачем засевать реплике адреса, которые у нас уже мертвы — она потратит на них
        // свой канал ровно так же, как тратили мы.
        Fresh();
        CatalogWarmup.Note("http", H, "/cub/tmdb./top/fire/movie?page=1");
        CatalogWarmup.Note("http", H, "/cub/tmdb./collections/3916?email=");
        CatalogWarmup.MarkDeadForTests("http", H, "/cub/tmdb./collections/3916?email=", DateTime.UtcNow);

        Assert.Equal(new[] { "/cub/tmdb./top/fire/movie?page=1" }, CatalogWarmup.ExportRowPaths());
    }

    [Fact]
    public void ImportRowPaths_RejectsNonRowPaths()
    {
        // 🔴 До 2.65 импорт принимал ЛЮБОЙ путь на '/', а Load() потом их выбрасывал: реплика
        // грела мусор до первого рестарта, после чего он молча исчезал.
        Fresh();

        int n = CatalogWarmup.ImportRowPaths(new List<string>
        {
            "/cub/tmdb./top/fire/movie?page=1",     // ряд
            "/cub/tmdb./3/discover/movie?page=1",   // деталь/passthrough — не ряд
            "/cub/tmdb.cub.best/blocked&zzr=1",     // мусор
            "относительный/путь",                   // не начинается с '/'
            ""
        }, "https", "tv2.d1versy.com");

        Assert.Equal(1, n);
        Assert.Equal(new[] { "/cub/tmdb./top/fire/movie?page=1" }, CatalogWarmup.RowPathsForTests());
    }

    // ─────────── хелперы ───────────

    static object Row(string pathQuery) => new { scheme = "http", host = H, pathQuery, lastSeen = DateTime.UtcNow };

    static void WriteStore(object state) => File.WriteAllText(Store(), JsonSerializer.Serialize(state));
}
