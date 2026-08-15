using QbitDownload;
using System.Text;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.45: шаблоны запросов карточки, снятые с живого клиента (CatalogWarmup v3).
///
/// Почему это важно тестировать. Ключ Staticache = Scheme+Host+Path+Query, нормализации путей в
/// нём нет. Ошибка в шаблоне на один символ = прогрев греет бакет, который никто не спрашивает,
/// и «details miss» в логах никогда не сходится к нулю — ровно то, что уже случалось (§AV.4).
/// </summary>
public class WarmTemplateTests
{
    [Fact]
    public void Деталь_фильма_превращается_в_шаблон()
    {
        var t = CatalogWarmup.ToTemplate("/tmdb/api/3/movie/550", "?api_key=K&language=ru");
        Assert.NotNull(t);
        Assert.Equal("movie", t.kind);
        Assert.Equal("/tmdb/api/3/{k}/{id}?api_key=K&language=ru", t.form);
    }

    [Fact]
    public void Подресурсы_карточки_шаблонизируются()
    {
        foreach (string sub in new[] { "credits", "recommendations", "similar", "videos" })
        {
            var t = CatalogWarmup.ToTemplate("/tmdb/api/3/tv/1396/" + sub, "?api_key=K&language=ru");
            Assert.NotNull(t);
            Assert.Equal("tv", t.kind);
            Assert.Equal("/tmdb/api/3/{k}/{id}/" + sub + "?api_key=K&language=ru", t.form);
        }
    }

    [Fact]
    public void Номер_сезона_выносится_в_плейсхолдер()
    {
        // иначе шаблон навсегда запомнил бы сезон ОДНОГО сериала и грел бы его всем подряд
        var t = CatalogWarmup.ToTemplate("/tmdb/api/3/tv/1396/season/5", "?api_key=K&language=ru");
        Assert.NotNull(t);
        Assert.Equal("/tmdb/api/3/{k}/{id}/season/{s}?api_key=K&language=ru", t.form);
    }

    [Fact]
    public void Реакции_cub_шаблонизируются()
    {
        var t = CatalogWarmup.ToTemplate("/cub/cub.red/api/reactions/get/movie_550", "");
        Assert.NotNull(t);
        Assert.Equal("movie", t.kind);
        Assert.Equal("/cub/cub.red/api/reactions/get/{k}_{id}", t.form);
    }

    [Theory]
    [InlineData("/tmdb/api/3/movie/popular", "?api_key=K")]       // ряд, а не карточка
    [InlineData("/tmdb/api/3/person/123", "?api_key=K")]          // персона
    [InlineData("/tmdb/api/3/collection/123", "?api_key=K")]      // коллекция — id не карточки
    [InlineData("/tmdb/api/3/movie/550/rating", "?api_key=K")]    // не в белом списке суффиксов
    [InlineData("/tmdb/api/3/tv/1396/season/1/episode/2", "?api_key=K")]
    [InlineData("/tmdb/api/3/search/movie", "?api_key=K&query=abc")]
    [InlineData("/qdl/list", "")]
    public void Непривязанное_к_карточке_шаблоном_не_становится(string path, string query)
        => Assert.Null(CatalogWarmup.ToTemplate(path, query));

    [Fact]
    public void Поисковый_запрос_отсекается()
        => Assert.Null(CatalogWarmup.ToTemplate("/tmdb/api/3/movie/550", "?api_key=K&query=abc"));

    [Fact]
    public void Инстанцирование_подставляет_вид_id_и_сезон()
    {
        Assert.Equal("/tmdb/api/3/movie/550?api_key=K",
            CatalogWarmup.Instantiate("/tmdb/api/3/{k}/{id}?api_key=K", tv: false, id: 550, season: 0));

        Assert.Equal("/tmdb/api/3/tv/1396/season/3?api_key=K",
            CatalogWarmup.Instantiate("/tmdb/api/3/{k}/{id}/season/{s}?api_key=K", tv: true, id: 1396, season: 3));

        Assert.Equal("/cub/cub.red/api/reactions/get/tv_1396",
            CatalogWarmup.Instantiate("/cub/cub.red/api/reactions/get/{k}_{id}", tv: true, id: 1396, season: 0));
    }

    [Fact]
    public void Круг_замыкается_клиентский_url_восстанавливается_из_шаблона()
    {
        // главный инвариант: снять шаблон и подставить ту же карточку → байт-в-байт исходный URL
        const string path = "/tmdb/api/3/tv/1396/credits";
        const string query = "?api_key=K&language=ru";

        var t = CatalogWarmup.ToTemplate(path, query);
        Assert.Equal(path + query, CatalogWarmup.Instantiate(t.form, tv: true, id: 1396, season: 0));
    }

    // ── SeasonForWarm: повторяет Utils.countSeasons из бандла ──

    static byte[] J(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Сезоны_считаются_только_непустые()
        => Assert.Equal(2, CatalogWarmup.SeasonForWarm(J(
            "{\"number_of_seasons\":9,\"seasons\":[{\"episode_count\":0},{\"episode_count\":10},{\"episode_count\":8}]}")));

    [Fact]
    public void Счёт_не_превышает_number_of_seasons()
        => Assert.Equal(1, CatalogWarmup.SeasonForWarm(J(
            "{\"number_of_seasons\":1,\"seasons\":[{\"episode_count\":10},{\"episode_count\":8}]}")));

    [Fact]
    public void Без_number_of_seasons_кап_не_применяется()
        // в JS `count > undefined` == false, то есть кап молча не срабатывает — повторяем,
        // иначе на битом ответе разъедемся с клиентом в номере сезона
        => Assert.Equal(2, CatalogWarmup.SeasonForWarm(J(
            "{\"seasons\":[{\"episode_count\":10},{\"episode_count\":8}]}")));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"seasons\":[]}")]
    [InlineData("не json")]
    [InlineData("[]")]
    public void Мусор_даёт_ноль_сезонов(string body)
        => Assert.Equal(0, CatalogWarmup.SeasonForWarm(J(body)));

    [Fact]
    public void Обе_дефолтные_формы_деталей_различаются_external_ids()
    {
        string a = CatalogWarmup.DefaultDetailQuery("K");
        string b = CatalogWarmup.DefaultDetailQueryExternalIds("K");

        Assert.DoesNotContain("external_ids", a);   // full$1 — карточка с главной (source:'cub')
        Assert.Contains("external_ids", b);         // full$3 — поиск/«Загрузки» (source:'tmdb')
        Assert.NotEqual(a, b);
    }
}
