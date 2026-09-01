using CubProxy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace QbitDownload.Tests;

// Фильтр рядов каталога CUB по году (qdl 2.89) — Modules/Proxy/CubProxy/RowFilter.cs.
//
// Что здесь под защитой, кроме самой отсечки:
//  • список кандидатов — фильтр обязан не трогать топы, жанры, коллекции и детали карточки;
//  • no-op на чужой форме тела (/blocked отдаёт МАССИВ, а не объект);
//  • нижний порог Floor — без него ряд молча исчезает с главной, а экран «Дальше» залипает;
//  • неприкосновенность total_pages — на ней висит вся пагинация.
public class CubRowFilterTests
{
    static readonly RowFilter.Conf Conf = new(enabled: true, movieYear: 2020, tvYear: 2010);

    static string Movie(int id, string date) => "{\"id\":" + id + ",\"title\":\"m" + id + "\",\"release_date\":\"" + date + "\"}";
    static string Tv(int id, string date) => "{\"id\":" + id + ",\"name\":\"t" + id + "\",\"first_air_date\":\"" + date + "\"}";

    static string Page(params string[] cards)
        => "{\"page\":1,\"total_pages\":15,\"total_results\":281,\"results\":[" + string.Join(",", cards) + "]}";

    static List<string> Ids(string json)
        => ((JArray)JObject.Parse(json)["results"]).Select(c => c["id"].ToString()).ToList();

    // ── кандидаты ───────────────────────────────────────────────────────────────────────────

    [Theory]
    // ряды «что сейчас / новинки» — фильтруем (адреса сняты с боевого catalog-warmup.json)
    [InlineData("?sort=now_playing&page=1&email=", true)]
    [InlineData("?sort=latest&email=", true)]
    [InlineData("?cat=&sort=latest&uhd=true&page=1&email=", true)]
    [InlineData("?cat=movie&sort=now&airdate=2026&page=1&email=", true)]
    [InlineData("?cat=anime&sort=airing&airdate=2026&page=1&email=", true)]
    [InlineData("?cat=movie&sort=now_playing&genre=14&page=1&email=", true)]
    // топы и жанры — НЕ трогаем: все жанровые ряды CUB ходят через sort=top
    [InlineData("?sort=top&genre=12&page=1&email=", false)]
    [InlineData("?cat=anime&sort=top&airdate=2019-2024&vote=6-8&page=1&email=", false)]
    // исторические подборки: у них sort нет вовсе. top/hundred/movie фильтр съедал бы 15 из 20
    [InlineData("top/hundred/movie?page=1&email=", false)]
    [InlineData("top/fire/movie?page=1&email=", false)]
    [InlineData("movie/popular?email=", false)]
    [InlineData("collections/3501?email=", false)]
    // /blocked — DMCA-список, к тому же массив
    [InlineData("blocked?uid=7kfrxzfr", false)]
    // passthrough TMDB-API: там results — это recommendations/similar внутри карточки
    [InlineData("3/movie/969681?api_key=x&language=ru", false)]
    [InlineData("3/tv/125988/recommendations?sort=latest", false)]
    // поиск не режем
    [InlineData("?sort=latest&query=%D0%BC%D0%B0%D1%82%D1%80%D0%B8%D1%86%D0%B0", false)]
    public void Кандидаты_только_ряды_свежести(string uri, bool expected)
        => Assert.Equal(expected, RowFilter.IsCandidate("tmdb", uri));

    [Fact]
    public void Кандидат_только_поддомен_tmdb()
    {
        Assert.False(RowFilter.IsCandidate("geo", "?sort=now_playing"));
        Assert.False(RowFilter.IsCandidate("", "?sort=now_playing"));
        Assert.False(RowFilter.IsCandidate(null, "?sort=now_playing"));
        Assert.False(RowFilter.IsCandidate("tmdb", null));
    }

    [Theory]
    [InlineData("?sort=now_playing&page=1", "now_playing")]
    [InlineData("?cat=movie&sort=top", "top")]
    [InlineData("?page=1&email=", "")]
    [InlineData("top/hundred/movie", "")]
    public void SortOf_достаёт_параметр(string uri, string expected)
        => Assert.Equal(expected, RowFilter.SortOf(uri));

    // ── отбор карточки ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Фильм_и_сериал_считаются_по_своим_порогам()
    {
        // «Ле-Ман» 1971 — ровно та карточка, из-за которой всё затевалось
        Assert.False(RowFilter.Keep(JObject.Parse(Movie(1, "1971-06-23")), Conf));
        Assert.True(RowFilter.Keep(JObject.Parse(Movie(2, "2026-07-29")), Conf));

        // сериалам порог свой и мягче: «Игра Престолов» 2011 обязана выжить при tvYear=2010
        Assert.True(RowFilter.Keep(JObject.Parse(Tv(3, "2011-04-17")), Conf));
        // ...а «Друзья» 1994 — нет
        Assert.False(RowFilter.Keep(JObject.Parse(Tv(4, "1994-09-22")), Conf));
    }

    [Fact]
    public void Признак_сериала_это_first_air_date_а_не_last_air_date()
    {
        // 🔴 last_air_date есть у ВСЕХ карточек ряда, включая фильмы (проверено на боевом):
        // если считать сериалом по нему, фильм 1971 года прошёл бы по мягкому порогу сериалов.
        var film = JObject.Parse("{\"id\":5,\"release_date\":\"1971-06-23\",\"last_air_date\":\"2011-01-01\"}");
        Assert.False(RowFilter.Keep(film, Conf));
    }

    [Fact]
    public void Карточка_без_даты_остаётся()
    {
        // такие есть в живой выдаче ?sort=latest — резать живую новинку хуже, чем пропустить старьё
        Assert.True(RowFilter.Keep(JObject.Parse("{\"id\":6,\"title\":\"без даты\"}"), Conf));
        Assert.True(RowFilter.Keep(JObject.Parse("{\"id\":7,\"release_date\":\"\"}"), Conf));
        Assert.True(RowFilter.Keep(JObject.Parse("{\"id\":8,\"release_date\":\"неизвестно\"}"), Conf));
    }

    // ── сборка тела ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Режет_старое_и_сохраняет_порядок()
    {
        // Живых должно остаться не меньше Floor, иначе сработает защита «отдаём как есть»
        // и мы бы проверяли не отсечку, а её (для этого есть отдельный тест ниже).
        string page = Page(
            Movie(1, "2026-01-01"), Movie(2, "1971-06-23"), Tv(3, "2011-04-17"),
            Movie(4, "2003-10-08"), Tv(5, "1994-09-22"), Movie(6, "2024-05-05"),
            Movie(7, "1956-12-28"), Tv(8, "2022-01-01"), Movie(9, "2021-03-03"),
            Movie(10, "2012-01-01"), Tv(11, "2015-01-01"));

        string body = RowFilter.Build(new[] { page }, Conf);

        Assert.NotNull(body);
        Assert.Equal(new[] { "1", "3", "6", "8", "9", "11" }, Ids(body));
    }

    [Fact]
    public void Total_pages_не_пересчитывается()
    {
        // 🔴 На total_pages висит кнопка «Дальше» (гейт pages > 1) и весь цикл догрузки.
        // Пересчёт под фильтр сломал бы пагинацию, а CUB и сам отдаёт их нестабильно.
        string page = Page(Movie(1, "2026-01-01"), Movie(2, "1971-01-01"), Tv(3, "2024-01-01"),
                           Tv(4, "2023-01-01"), Movie(5, "2025-01-01"), Movie(6, "2022-01-01"));

        var o = JObject.Parse(RowFilter.Build(new[] { page }, Conf));

        Assert.Equal(15, (int)o["total_pages"]);
        Assert.Equal(281, (int)o["total_results"]);
        Assert.Equal(1, (int)o["page"]);
    }

    [Fact]
    public void Выключенный_фильтр_не_трогает_тело()
        => Assert.Null(RowFilter.Build(new[] { Page(Movie(1, "1971-01-01")) }, new RowFilter.Conf(false, 2020, 2010)));

    [Fact]
    public void Резать_нечего_тело_не_переписываем()
    {
        // Экономия не косметическая: не переписав тело, мы не трогаем Content-Length.
        string page = Page(Movie(1, "2026-01-01"), Tv(2, "2024-01-01"));
        Assert.Null(RowFilter.Build(new[] { page }, Conf));
    }

    [Fact]
    public void Чужая_форма_тела_остаётся_нетронутой()
    {
        // /blocked отдаёт МАССИВ — на нём фильтр обязан быть строго no-op
        Assert.Null(RowFilter.Build(new[] { "[{\"id\":1,\"release_date\":\"1971-01-01\"}]" }, Conf));
        Assert.Null(RowFilter.Build(new[] { "{\"ok\":true}" }, Conf));
        Assert.Null(RowFilter.Build(new[] { "не json" }, Conf));
        Assert.Null(RowFilter.Build(new[] { "" }, Conf));
        Assert.Null(RowFilter.Build(null, Conf));

        Assert.Equal(-1, RowFilter.CountKept("[]", Conf));
        Assert.Equal(-1, RowFilter.CountKept("мусор", Conf));
    }

    [Fact]
    public void Осталось_меньше_порога_отдаём_исходное_тело()
    {
        // 🔴 Ради этого инварианта всё и написано. Без него:
        //  • Api.partNext выбрасывает ряд с коротким/пустым results — ряд ИСЧЕЗАЕТ с главной;
        //  • экран «Дальше» грузит вторую страницу только от scroll.onEnd, а Scroll.isEnd()
        //    на незаполненном гриде даёт false — экран залипает навсегда.
        // Тест обязан ПОКРАСНЕТЬ, если убрать проверку Floor в RowFilter.Build.
        var cards = new List<string>();
        for (int i = 0; i < 20; i++)
            cards.Add(Movie(i + 1, i < RowFilter.Floor - 1 ? "2026-01-01" : "1990-01-01"));

        Assert.Null(RowFilter.Build(new[] { Page(cards.ToArray()) }, Conf));
    }

    [Fact]
    public void Ровно_на_пороге_фильтруем()
    {
        var cards = new List<string>();
        for (int i = 0; i < 20; i++)
            cards.Add(Movie(i + 1, i < RowFilter.Floor ? "2026-01-01" : "1990-01-01"));

        string body = RowFilter.Build(new[] { Page(cards.ToArray()) }, Conf);

        Assert.NotNull(body);
        Assert.Equal(RowFilter.Floor, Ids(body).Count);
    }

    // ── добор страниц ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Добор_склеивает_страницы_до_цели()
    {
        var p1 = new List<string>();
        var p2 = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            p1.Add(Movie(i + 1, i < 12 ? "2026-01-01" : "1990-01-01"));    // 12 живых
            p2.Add(Movie(100 + i, i < 12 ? "2025-01-01" : "1990-01-01"));  // ещё 12
        }

        string body = RowFilter.Build(new[] { Page(p1.ToArray()), Page(p2.ToArray()) }, Conf);
        var ids = Ids(body);

        Assert.Equal(RowFilter.Target, ids.Count);          // добрали ровно до цели, не больше
        Assert.Equal("1", ids[0]);                          // порядок первой страницы сохранён
        Assert.Contains("100", ids);                        // хвост пришёл со второй
    }

    [Fact]
    public void Добор_дедуплицирует_по_id()
    {
        // CUB между запросами переставляет выдачу — одна карточка легко попадает на обе страницы.
        var p1 = new List<string>();
        for (int i = 0; i < 20; i++)
            p1.Add(Movie(i + 1, i < 10 ? "2026-01-01" : "1990-01-01"));

        // вторая страница целиком повторяет живые карточки первой
        var p2 = new List<string>();
        for (int i = 0; i < 10; i++)
            p2.Add(Movie(i + 1, "2026-01-01"));

        var ids = Ids(RowFilter.Build(new[] { Page(p1.ToArray()), Page(p2.ToArray()) }, Conf));

        Assert.Equal(10, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("https://tmdb.cub.red/?sort=now_playing&page=1&email=", 1, "https://tmdb.cub.red/?sort=now_playing&page=2&email=")]
    [InlineData("https://tmdb.cub.red/?sort=now_playing&page=1&email=", 2, "https://tmdb.cub.red/?sort=now_playing&page=3&email=")]
    // страницы в адресе нет — база 1, дописываем
    [InlineData("https://tmdb.cub.red/?sort=latest&email=", 1, "https://tmdb.cub.red/?sort=latest&email=&page=2")]
    // многозначная страница
    [InlineData("https://tmdb.cub.red/?sort=latest&page=12", 1, "https://tmdb.cub.red/?sort=latest&page=13")]
    public void NextPageUrl_считает_соседнюю_страницу(string url, int offset, string expected)
        => Assert.Equal(expected, RowFilter.NextPageUrl(url, offset));

    [Fact]
    public void NextPageUrl_не_ведётся_на_чужой_параметр()
    {
        // "mypage=2" — не наш параметр: страницу дописываем отдельно, а чужой не трогаем
        string next = RowFilter.NextPageUrl("https://tmdb.cub.red/?sort=latest&mypage=2", 1);
        Assert.Contains("mypage=2", next);
        Assert.EndsWith("page=2", next);
    }

    // ── счётчик для контроллера ─────────────────────────────────────────────────────────────

    [Fact]
    public void CountKept_считает_выживших()
    {
        string page = Page(Movie(1, "2026-01-01"), Movie(2, "1971-01-01"), Tv(3, "2011-01-01"));
        Assert.Equal(2, RowFilter.CountKept(page, Conf));
    }

    [Fact]
    public void Итоговое_тело_остаётся_валидным_json()
    {
        string page = Page(Movie(1, "2026-01-01"), Movie(2, "1971-01-01"), Tv(3, "2024-01-01"),
                           Tv(4, "2023-01-01"), Movie(5, "2025-01-01"), Movie(6, "2022-01-01"));

        var o = JsonConvert.DeserializeObject<JObject>(RowFilter.Build(new[] { page }, Conf));
        Assert.IsType<JArray>(o["results"]);
    }
}
