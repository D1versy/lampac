using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.45: ETag/304 для горячих JSON-ручек (/qdl/list и соседи).
/// Проверяем ровно то, от чего зависит корректность 304: стабильность валидатора, его смену при
/// смене тела и слабое сравнение If-None-Match по RFC 9110.
/// </summary>
public class HttpCacheTests
{
    [Fact]
    public void Etag_стабилен_для_одного_и_того_же_тела()
        => Assert.Equal(HttpCache.Etag("[{\"a\":1}]"), HttpCache.Etag("[{\"a\":1}]"));

    [Fact]
    public void Etag_меняется_при_смене_тела()
        => Assert.NotEqual(HttpCache.Etag("[{\"a\":1}]"), HttpCache.Etag("[{\"a\":2}]"));

    [Fact]
    public void Etag_слабый()
        => Assert.StartsWith("W/\"", HttpCache.Etag("[]"));

    [Fact]
    public void Etag_null_для_null_тела()
        => Assert.Null(HttpCache.Etag(null));

    [Fact]
    public void Совпадение_даёт_304()
    {
        string etag = HttpCache.Etag("body");
        Assert.True(HttpCache.IfNoneMatchHit(etag, etag));
    }

    [Fact]
    public void Несовпадение_не_даёт_304()
        => Assert.False(HttpCache.IfNoneMatchHit(HttpCache.Etag("one"), HttpCache.Etag("two")));

    [Fact]
    public void Сравнение_слабое_префикс_W_игнорируется()
    {
        // клиент вправе прислать тег без W/ — по RFC это тот же валидатор при слабом сравнении
        string etag = HttpCache.Etag("body");
        string strong = etag.Substring(2);   // "\"...\""
        Assert.True(HttpCache.IfNoneMatchHit(strong, etag));
    }

    [Fact]
    public void Список_через_запятую_разбирается()
    {
        string etag = HttpCache.Etag("body");
        Assert.True(HttpCache.IfNoneMatchHit("W/\"aaa\", " + etag + " , W/\"bbb\"", etag));
    }

    [Fact]
    public void Звёздочка_совпадает_с_любым()
        => Assert.True(HttpCache.IfNoneMatchHit("*", HttpCache.Etag("body")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("мусор")]
    [InlineData("W/")]
    [InlineData("\"\"")]          // пустой тег бесполезен как валидатор
    public void Мусорный_заголовок_не_даёт_304(string header)
        => Assert.False(HttpCache.IfNoneMatchHit(header, HttpCache.Etag("body")));

    [Fact]
    public void Без_нашего_etag_совпадения_нет()
        => Assert.False(HttpCache.IfNoneMatchHit("W/\"aaa\"", null));
}
