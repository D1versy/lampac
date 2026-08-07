using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты предиката сбора URL прогрева каталога (<c>CatalogWarmup.IsCatalogUrl</c>, qdl 2.15):
/// запоминаем только каталожные /cub/tmdb.* (ряды главной/коллекции), поиск и посторонние пути —
/// нет. Сам цикл прогрева (Staticache-ключи, реплей с Host) проверяется вживую после деплоя.
/// </summary>
public class CatalogWarmupTests
{
    [Theory]
    // формы каталога после серверных замен бандла и cubproxy.js
    [InlineData("/cub/tmdb./3/discover/movie", "?api_key=k&page=1")]                 // общий билдер '{host}/cub/tmdb./'+u
    [InlineData("/cub/tmdb.cub.rip", "?sort=releases&results=20&page=1")]            // ряд «Релизы» (host-форма без пути)
    [InlineData("/cub/tmdb.cub.rip/3/movie/now_playing", "?api_key=k&language=ru")]
    [InlineData("/cub/TMDB.cub.rip/3/trending/all/week", "")]                        // регистр не важен
    public void IsCatalogUrl_CatalogForms_True(string path, string query)
        => Assert.True(CatalogWarmup.IsCatalogUrl(path, query));

    [Theory]
    [InlineData("/cub/tmdb./3/search/movie", "?api_key=k&query=dune")]   // поиск — одноразовые URL
    [InlineData("/cub/tmdb./3/search/tv", "?QUERY=x")]                   // регистр параметра не важен
    public void IsCatalogUrl_Search_False(string path, string query)
        => Assert.False(CatalogWarmup.IsCatalogUrl(path, query));

    [Theory]
    [InlineData("/cub/red/api/checker", "")]              // cub-API, не каталог
    [InlineData("/cub/api/checker", "")]                  // локальная заглушка (наш же watch-пинг)
    [InlineData("/tmdb/api/3/movie/1", "?api_key=k")]     // TMDB-прокси — свой кеш, греть нечего
    [InlineData("/qdl/list", "")]
    [InlineData(null, null)]
    public void IsCatalogUrl_Foreign_False(string path, string query)
        => Assert.False(CatalogWarmup.IsCatalogUrl(path, query));
}
