using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.45: кеш выдачи поиска раздач со stale-семантикой.
/// Главный инвариант под тестом — DeepClone: TorrentScoring.SortAndMark мутирует элементы,
/// и без копии первый же читатель испортил бы общий кеш.
/// </summary>
public class SearchCacheTests
{
    [Fact]
    public void Ключ_по_tmdb_id_стабилен()
        => Assert.Equal(SearchCache.Key("438631", "dune", 2021, 2), SearchCache.Key("438631", "dune", 2021, 2));

    [Fact]
    public void Ключ_по_tmdb_id_не_зависит_от_названия_и_года()
    {
        // id однозначен — по нему перепутать тайтл невозможно, остальное шум
        Assert.Equal(SearchCache.Key("438631", "dune", 2021, 2),
                     SearchCache.Key("438631", "совсем другое", 1999, 2));
    }

    [Fact]
    public void Сериальность_разводит_ключи()
    {
        // узкая и широкая ветки индексатора дают разную выдачу — мешать их нельзя
        Assert.NotEqual(SearchCache.Key("438631", "dune", 2021, 1),
                        SearchCache.Key("438631", "dune", 2021, 2));
    }

    [Fact]
    public void Без_id_ключ_считается_по_названию_и_году()
    {
        Assert.NotNull(SearchCache.Key(null, "dune", 2021, 1));
        Assert.NotEqual(SearchCache.Key(null, "dune", 2021, 1), SearchCache.Key(null, "dune", 1984, 1));
        Assert.NotEqual(SearchCache.Key(null, "dune", 2021, 1), SearchCache.Key(null, "matrix", 2021, 1));
    }

    [Fact]
    public void Ни_id_ни_названия_кешировать_нечего()
    {
        Assert.Null(SearchCache.Key(null, null, 0, 1));
        Assert.Null(SearchCache.Key("", "   ", 0, 1));
    }

    [Fact]
    public void Название_нечувствительно_к_регистру()
        => Assert.Equal(SearchCache.Key(null, "Dune", 2021, 1), SearchCache.Key(null, "dune", 2021, 1));

    // ── свежесть ──

    const int Fresh = 6;     // часов
    const int Stale = 7;     // дней
    const long Now = 1_800_000_000;

    [Fact]
    public void Свежий_снимок_отдаётся_молча()
        => Assert.Equal(SearchCache.Freshness.Fresh, SearchCache.Judge(Now - 3600, Now, Fresh, Stale));

    [Fact]
    public void Граница_шести_часов_ещё_свежая()
        => Assert.Equal(SearchCache.Freshness.Fresh, SearchCache.Judge(Now - Fresh * 3600, Now, Fresh, Stale));

    [Fact]
    public void За_границей_шести_часов_stale()
        => Assert.Equal(SearchCache.Freshness.Stale, SearchCache.Judge(Now - Fresh * 3600 - 1, Now, Fresh, Stale));

    [Fact]
    public void Граница_семи_дней_ещё_stale()
        => Assert.Equal(SearchCache.Freshness.Stale, SearchCache.Judge(Now - Stale * 86400, Now, Fresh, Stale));

    [Fact]
    public void Старше_семи_дней_промах()
        => Assert.Equal(SearchCache.Freshness.Miss, SearchCache.Judge(Now - Stale * 86400 - 1, Now, Fresh, Stale));

    [Fact]
    public void Нет_отметки_времени_промах()
        => Assert.Equal(SearchCache.Freshness.Miss, SearchCache.Judge(0, Now, Fresh, Stale));

    [Fact]
    public void Часы_уехали_назад_кеш_не_наказываем()
        // хост падает по питанию; после старта время может на секунды разъехаться
        => Assert.Equal(SearchCache.Freshness.Fresh, SearchCache.Judge(Now + 120, Now, Fresh, Stale));

    // ── дедуп фоновых обновлений ──

    [Fact]
    public void Одно_фоновое_обновление_на_ключ()
    {
        string k = "test-refresh-" + System.Guid.NewGuid().ToString("N");
        Assert.True(SearchCache.TryBeginRefresh(k));
        Assert.False(SearchCache.TryBeginRefresh(k));   // второй поток не должен пойти к трекерам
        SearchCache.EndRefresh(k);
        Assert.True(SearchCache.TryBeginRefresh(k));
        SearchCache.EndRefresh(k);
    }

    // ── инвариант №1: скоринг мутирует элементы ──

    [Fact]
    public void DeepClone_защищает_кеш_от_мутаций_скоринга()
    {
        var original = new JArray { new JObject { ["title"] = "a", ["sid"] = 10 } };
        var served = (JArray)original.DeepClone();

        // так делает TorrentScoring.SortAndMark: пишет поля прямо в элементы выдачи
        ((JObject)served[0])["score"] = 99;
        ((JObject)served[0])["watchable"] = true;

        Assert.Null(original[0]["score"]);
        Assert.Null(original[0]["watchable"]);
        Assert.Equal(99, (int)served[0]["score"]);
    }
}
