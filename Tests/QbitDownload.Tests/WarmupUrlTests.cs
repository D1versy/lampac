using System.Collections.Generic;
using System.Text;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.19: форма URL прогрева и новый патч бандла.
/// Главное, что здесь фиксируется, — инвариант «греем ровно то, что просит клиент»:
/// детали карточки клиент запрашивает как <c>/tmdb/api/3/movie|tv/&lt;id&gt;</c> (XHR-патч
/// lampainit-invc.js), а ключ Staticache = scheme+host+path+query БЕЗ нормализации путей,
/// поэтому прежняя форма <c>/cub/tmdb./3/…</c> грела бакет, который никто не спрашивает.
/// Плюс фон карточки (w1280) и заглушка рекламного преролла.
/// </summary>
public class WarmupUrlTests
{
    // ─────────── DetailPath: клиентская форма URL деталей ───────────

    [Fact]
    public void DetailPath_UsesTmdbApiForm()
    {
        Assert.Equal("/tmdb/api/3/movie/438631", CatalogWarmup.DetailPath(438631, tv: false));
        Assert.Equal("/tmdb/api/3/tv/1399", CatalogWarmup.DetailPath(1399, tv: true));
    }

    [Fact]
    public void DetailPath_NotCubProxyForm()
    {
        // регресс qdl 2.19: /cub/tmdb./3/… — другой Path → другой ключ Staticache, вечный MISS
        Assert.DoesNotContain("/cub/", CatalogWarmup.DetailPath(1, false));
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(200, true)]
    public void DetailPath_IsSeenByObserver(long id, bool tv)
    {
        // то, что греем, наблюдатель обязан узнавать как деталь — иначе шаблон query
        // перестанет обновляться с живого трафика
        Assert.True(CatalogWarmup.IsDetailUrl(CatalogWarmup.DetailPath(id, tv)));
    }

    // ─────────── IsDetailUrl: предикат наблюдателя видит обе формы ───────────

    [Theory]
    [InlineData("/tmdb/api/3/movie/438631")]   // форма клиента после XHR-патча
    [InlineData("/tmdb/api/3/tv/1399")]
    [InlineData("/TMDB/API/3/movie/1")]        // регистр пути не важен
    [InlineData("/cub/tmdb./3/movie/1084736")] // старая форма (клиенты без патча/фолбэк)
    [InlineData("/cub/tmdb.cub.rip/3/tv/1399")]
    public void IsDetailUrl_BothForms_True(string path)
        => Assert.True(CatalogWarmup.IsDetailUrl(path));

    [Theory]
    [InlineData("/tmdb/api/3/tv/1399/season/1")]   // сезоны — не деталь карточки
    [InlineData("/tmdb/api/3/person/123")]
    [InlineData("/tmdb/api/3/movie/")]
    [InlineData("/tmdb/api/3/discover/movie")]
    [InlineData("/tmdb/img/t/p/w300/abc.jpg")]     // картинки — другой конвейер
    [InlineData("/tmdb/api/")]
    [InlineData(null)]
    public void IsDetailUrl_Foreign_False(string path)
        => Assert.False(CatalogWarmup.IsDetailUrl(path));

    [Fact]
    public void IsRowUrl_TmdbApiIsNotARow()
    {
        // /tmdb/api никогда не ряд: наблюдатель не должен класть его в LRU рядов
        Assert.False(CatalogWarmup.IsRowUrl("/tmdb/api/3/movie/1", "?api_key=k"));
        Assert.False(CatalogWarmup.IsRowUrl("/tmdb/api/3/trending/all/day", ""));
    }

    // ─────────── ImgPath / BackdropPaths: фон карточки ───────────

    [Fact]
    public void ImgPath_BuildsProxyUrl()
    {
        Assert.Equal("/tmdb/img/t/p/w300/abc.jpg", CatalogWarmup.ImgPath("w300", "/abc.jpg"));
        Assert.Equal("/tmdb/img/t/p/w1280/abc.jpg", CatalogWarmup.ImgPath("w1280", "/abc.jpg"));
    }

    static List<CatalogWarmup.Card> Cards(params string[] backdrops)
    {
        var list = new List<CatalogWarmup.Card>();
        for (int i = 0; i < backdrops.Length; i++)
            list.Add(new CatalogWarmup.Card(i + 1, false, "/p" + i + ".jpg", backdrops[i]));
        return list;
    }

    [Fact]
    public void BackdropPaths_TakesFirstN_InW1280()
    {
        var got = CatalogWarmup.BackdropPaths(Cards("/a.jpg", "/b.jpg", "/c.jpg", "/d.jpg"), 3);

        Assert.Equal(new[]
        {
            "/tmdb/img/t/p/w1280/a.jpg",
            "/tmdb/img/t/p/w1280/b.jpg",
            "/tmdb/img/t/p/w1280/c.jpg"
        }, got);
    }

    [Fact]
    public void BackdropPaths_SkipsMissing_AndRefillsBudget()
    {
        // карточки без backdrop_path не съедают бюджет — добираем следующими
        var got = CatalogWarmup.BackdropPaths(Cards(null, "", "no-slash.jpg", "/d.jpg", "/e.jpg"), 2);

        Assert.Equal(new[] { "/tmdb/img/t/p/w1280/d.jpg", "/tmdb/img/t/p/w1280/e.jpg" }, got);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BackdropPaths_ZeroBudget_Empty(int perRow)
        => Assert.Empty(CatalogWarmup.BackdropPaths(Cards("/a.jpg"), perRow));

    [Fact]
    public void BackdropPaths_NullCards_Empty()
        => Assert.Empty(CatalogWarmup.BackdropPaths(null, 3));

    [Fact]
    public void ExtractCards_ReadsBackdropPath()
    {
        var cards = CatalogWarmup.ExtractCards(Encoding.UTF8.GetBytes("""
            {"results":[
              {"id":1,"title":"m","poster_path":"/p.jpg","backdrop_path":"/b.jpg"},
              {"id":2,"name":"s","poster_path":"/p2.jpg"}
            ]}
            """), 10);

        Assert.Equal("/b.jpg", cards[0].backdrop);
        Assert.Null(cards[1].backdrop);
    }

    // ─────────── AppPatch: заглушка рекламного преролла ───────────

    // все 6 якорей + окружение, как в бандле (tt-rows встречается дважды)
    const string AllAnchors = @"
      this.icon = Head.addIcon(Template.string('icon_bell'), this.open.bind(this));
      this.classes.cub = new NoticeCub();
      if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;
      Timer.add(time_extract, extract);
      if (screen == 'category' && params.url == 'movie') return;
      other_lately_code();
      if (screen == 'category' && params.url == 'movie') return;
      other_recently_code();
      function show$5(data, call) {
        player_data = data;
        if (type.any) return call();
        IMA.loadSDK();
      }
    ";

    [Fact]
    public void PatchAppJs_Preroll_NeverShown()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        // ранний return из show$5: реклама не грузится, плеер стартует штатным колбэком
        Assert.Contains("function show$5(data, call) {return call();/*qdl-cut:preroll*/", result);
        Assert.DoesNotContain("function show$5(data, call) {\n        player_data", result);
    }

    [Fact]
    public void PatchAppJs_MarkerCount_IsSeven()
    {
        // инвариант отдаваемого бандла: 6 якорей, tt-rows режет два вхождения → 7 маркеров
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Equal(7, result.Split("/*qdl-cut:").Length - 1);
    }

    [Fact]
    public void PatchAppJs_Preroll_Idempotent()
    {
        // replacement начинается с самого якоря — второй прогон не должен патчить повторно
        string once = AppPatch.PatchAppJs(AllAnchors);
        Assert.Equal(once, AppPatch.PatchAppJs(once));
    }

    [Fact]
    public void PatchAppJs_PrerollAnchorMissing_NoThrow()
    {
        const string js = "var lauch = function(){}; lauch();";
        Assert.Equal(js, AppPatch.PatchAppJs(js));   // якоря нет → вход как есть (warn в лог)
    }
}
