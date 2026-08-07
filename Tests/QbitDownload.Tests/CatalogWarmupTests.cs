using System.Text;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты чистых функций прогрева каталога (<c>CatalogWarmup</c>, qdl 2.15/2.16):
/// классификация URL (ряды наблюдаем, детали — только шаблон query), разбор карточек из
/// ответа ряда, построение прогревочных URL. Сам цикл (Staticache-ключи, реплей с Host,
/// бюджеты/ротация) проверяется вживую после деплоя.
/// </summary>
public class CatalogWarmupTests
{
    // ─────────── IsRowUrl: ряды каталога (наблюдаются и реплеятся) ───────────

    [Theory]
    [InlineData("/cub/tmdb.cub.rip", "?sort=releases&results=20&page=1")]        // ряд «Релизы» (host-форма)
    [InlineData("/cub/tmdb.cub.rip/", "?cat=movie&sort=latest&uhd=true")]
    [InlineData("/cub/tmdb./top/fire/movie", "")]                                 // серверная форма '{host}/cub/tmdb./'+u
    [InlineData("/cub/TMDB.cub.rip/top/hundred/movie", "")]                       // регистр не важен
    [InlineData("/cub/tmdb./", "?cat=tv&sort=top&airdate=1980-1985&genre=35")]    // генерённые ряды по годам/жанрам
    public void IsRowUrl_Rows_True(string path, string query)
        => Assert.True(CatalogWarmup.IsRowUrl(path, query));

    [Theory]
    [InlineData("/cub/tmdb./3/movie/12345", "?api_key=k")]     // деталь — НЕ ряд (в 2.15 вытесняла ряды из LRU)
    [InlineData("/cub/tmdb.cub.rip/3/tv/1399", "?api_key=k")]
    [InlineData("/cub/tmdb./3/discover/movie", "?api_key=k")]  // /3/ = tmdb-api passthrough, не cub-каталог
    [InlineData("/cub/tmdb./", "?query=dune")]                 // поиск — одноразовые URL
    [InlineData("/cub/red/api/checker", "")]                   // cub-API
    [InlineData("/tmdb/api/3/movie/1", "?api_key=k")]          // свой TMDB-прокси
    [InlineData(null, null)]
    public void IsRowUrl_Foreign_False(string path, string query)
        => Assert.False(CatalogWarmup.IsRowUrl(path, query));

    // ─────────── IsDetailUrl: детали карточек (снимаем шаблон query) ───────────

    [Theory]
    [InlineData("/cub/tmdb./3/movie/1084736")]
    [InlineData("/cub/tmdb.cub.rip/3/tv/1399")]
    public void IsDetailUrl_Details_True(string path)
        => Assert.True(CatalogWarmup.IsDetailUrl(path));

    [Theory]
    [InlineData("/cub/tmdb./3/tv/1399/season/1")]   // сезоны идут через /tmdb/api — не деталь
    [InlineData("/cub/tmdb./3/person/123")]
    [InlineData("/cub/tmdb./3/movie/")]
    [InlineData("/cub/tmdb.cub.rip", "")]
    [InlineData(null)]
    public void IsDetailUrl_Foreign_False(string path, string _ = null)
        => Assert.False(CatalogWarmup.IsDetailUrl(path));

    // ─────────── ExtractCards: разбор ответа ряда ───────────

    static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void ExtractCards_MovieAndTv_TypedByNameField()
    {
        var cards = CatalogWarmup.ExtractCards(Body("""
            {"page":1,"results":[
              {"id":100,"title":"Movie","poster_path":"/aaa.jpg"},
              {"id":200,"name":"Show","poster_path":"/bbb.jpg"},
              {"id":300,"original_name":"Orig show","poster_path":null}
            ]}
            """), 10);

        Assert.Equal(3, cards.Count);
        Assert.False(cards[0].tv);
        Assert.Equal("/aaa.jpg", cards[0].poster);
        Assert.True(cards[1].tv);
        Assert.True(cards[2].tv, "original_name тоже признак сериала");
        Assert.Null(cards[2].poster);
    }

    [Fact]
    public void ExtractCards_RespectsMax_AndSkipsGarbage()
    {
        var cards = CatalogWarmup.ExtractCards(Body("""
            {"results":[
              {"id":1,"title":"a"},{"id":0,"title":"без id не греем"},{"no_id":true},
              {"id":2,"title":"b"},{"id":3,"title":"c"}
            ]}
            """), 2);

        Assert.Equal(2, cards.Count);
        Assert.Equal(1, cards[0].id);
        Assert.Equal(2, cards[1].id);
    }

    [Theory]
    [InlineData("{}")]                       // нет results
    [InlineData("{\"results\":{}}")]         // results не массив
    [InlineData("не json вовсе")]            // мусор → пустой список, без исключений
    [InlineData("")]
    public void ExtractCards_BadBody_Empty(string json)
        => Assert.Empty(CatalogWarmup.ExtractCards(Body(json), 10));

    // ─────────── DefaultDetailQuery: посимвольное совпадение с клиентским URL ───────────

    [Fact]
    public void DefaultDetailQuery_MatchesBundleForm()
    {
        // форма из app.min.js full$1(): api_key + append_to_response + language (email/uid — SkipQueryKeys)
        Assert.Equal(
            "?api_key=k&append_to_response=content_ratings,release_dates,keywords,alternative_titles&language=ru",
            CatalogWarmup.DefaultDetailQuery("k"));
    }
}
