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
//  • нижний порог Floor — без него ряд молча исчезает с главной (Api.partNext выбрасывает ряд
//    с пустым results целиком);
//  • 🔴 qdl 2.94: наша страница = РОВНО одна апстримная. Соседние страницы не пересекаются и
//    ничего не теряют — тесты ниже покраснеют, если кто-то вернёт добор или кап по количеству;
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

        string body = RowFilter.Build(page, Conf);

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

        var o = JObject.Parse(RowFilter.Build(page, Conf));

        Assert.Equal(15, (int)o["total_pages"]);
        Assert.Equal(281, (int)o["total_results"]);
        Assert.Equal(1, (int)o["page"]);
    }

    [Fact]
    public void Выключенный_фильтр_не_трогает_тело()
        => Assert.Null(RowFilter.Build(Page(Movie(1, "1971-01-01")), new RowFilter.Conf(false, 2020, 2010)));

    [Fact]
    public void Резать_нечего_тело_не_переписываем()
    {
        // Экономия не косметическая: не переписав тело, мы не трогаем Content-Length.
        string page = Page(Movie(1, "2026-01-01"), Tv(2, "2024-01-01"));
        Assert.Null(RowFilter.Build(page, Conf));
    }

    [Fact]
    public void Чужая_форма_тела_остаётся_нетронутой()
    {
        // /blocked отдаёт МАССИВ — на нём фильтр обязан быть строго no-op
        Assert.Null(RowFilter.Build("[{\"id\":1,\"release_date\":\"1971-01-01\"}]", Conf));
        Assert.Null(RowFilter.Build("{\"ok\":true}", Conf));
        Assert.Null(RowFilter.Build("не json", Conf));
        Assert.Null(RowFilter.Build("", Conf));
        Assert.Null(RowFilter.Build(null, Conf));
    }

    [Fact]
    public void Осталось_меньше_порога_отдаём_исходное_тело()
    {
        // 🔴 Ради этого инварианта всё и написано: Api.partNext выбрасывает ряд с пустым
        // results СОВСЕМ — ряд ИСЧЕЗАЕТ с главной, и это выглядит не как поломка, а как
        // «нет такого ряда». Тест обязан ПОКРАСНЕТЬ, если убрать проверку Floor в Build.
        // (Второе прежнее обоснование — «короткая страница не догрузится» — с 2.94 живёт на
        //  клиенте: патчи grid-dedup-* и насос gridPump в qdl.js.)
        var cards = new List<string>();
        for (int i = 0; i < 20; i++)
            cards.Add(Movie(i + 1, i < RowFilter.Floor - 1 ? "2026-01-01" : "1990-01-01"));

        Assert.Null(RowFilter.Build(Page(cards.ToArray()), Conf));
    }

    [Fact]
    public void Ровно_на_пороге_фильтруем()
    {
        var cards = new List<string>();
        for (int i = 0; i < 20; i++)
            cards.Add(Movie(i + 1, i < RowFilter.Floor ? "2026-01-01" : "1990-01-01"));

        string body = RowFilter.Build(Page(cards.ToArray()), Conf);

        Assert.NotNull(body);
        Assert.Equal(RowFilter.Floor, Ids(body).Count);
    }

    [Fact]
    public void Итоговое_тело_остаётся_валидным_json()
    {
        string page = Page(Movie(1, "2026-01-01"), Movie(2, "1971-01-01"), Tv(3, "2024-01-01"),
                           Tv(4, "2023-01-01"), Movie(5, "2025-01-01"), Movie(6, "2022-01-01"));

        var o = JsonConvert.DeserializeObject<JObject>(RowFilter.Build(page, Conf));
        Assert.IsType<JArray>(o["results"]);
    }

    // ── страница 1:1, без добора (qdl 2.94) ─────────────────────────────────────────────────

    /// <summary>Каталог из pages страниц по perPage карточек; выживает каждая survivalEvery-я.</summary>
    static List<string> Catalog(int pages, int perPage, int survivalEvery)
    {
        var all = new List<string>();
        int id = 1;
        for (int p = 0; p < pages; p++)
        {
            var cards = new List<string>();
            for (int i = 0; i < perPage; i++, id++)
                cards.Add(Movie(id, id % survivalEvery == 0 ? "1990-01-01" : "2026-01-01"));
            all.Add(Page(cards.ToArray()));
        }
        return all;
    }

    static List<string> Survivors(string page)
        => ((JArray)JObject.Parse(page)["results"])
            .Where(c => RowFilter.Keep(c, Conf)).Select(c => c["id"].ToString()).ToList();

    [Fact]
    public void Соседние_страницы_не_пересекаются()
    {
        // 🔴 Тот самый инвариант, ради которого убран добор. На боевом до 2.94 перекрытие
        // соседних страниц было 4 / 8 / 5 карточек из 20, и владелец видел в «Ещё» каждый
        // фильм двумя строчками. Тест обязан покраснеть, если кто-то вернёт склейку страниц.
        var catalog = Catalog(pages: 5, perPage: 20, survivalEvery: 5);
        var shown = catalog.Select(p => Ids(RowFilter.Build(p, Conf))).ToList();

        for (int i = 0; i + 1 < shown.Count; i++)
            Assert.Empty(shown[i].Intersect(shown[i + 1]));
    }

    [Fact]
    public void Проход_каталога_без_потерь_и_без_дублей()
    {
        // Требование владельца целиком: не терять И не дублировать. Проверяем состав И ПОРЯДОК.
        var catalog = Catalog(pages: 5, perPage: 20, survivalEvery: 5);
        var shown = catalog.SelectMany(p => Ids(RowFilter.Build(p, Conf))).ToList();
        var expected = catalog.SelectMany(p => Survivors(p)).ToList();

        Assert.Equal(expected, shown);
        Assert.Equal(shown.Count, shown.Distinct().Count());
    }

    [Fact]
    public void Кап_на_количество_отдаваемых_карточек_отсутствует()
    {
        // 🔴 Мина, ради которой Target удалён насовсем: при схеме 1:1 любой кап — это ВЕЧНАЯ
        // потеря карточек, потому что следующий запрос клиента уйдёт на страницу N+1, а не
        // на остаток N. Прежний Target=20 был безобиден только в паре с добором.
        // Живых заведомо БОЛЬШЕ прежнего Target=20 — иначе тест зелёный и с капом.
        var cards = new List<string>();
        for (int i = 0; i < 60; i++)
            cards.Add(Movie(i + 1, i % 3 == 0 ? "1990-01-01" : "2026-01-01"));

        Assert.Equal(40, Ids(RowFilter.Build(Page(cards.ToArray()), Conf)).Count);
    }

    [Fact]
    public void Повтор_внутри_страницы_снимается()
    {
        // Страховка от самого CUB: он иногда отдаёт одну карточку дважды в пределах страницы.
        string page = Page(Movie(1, "2026-01-01"), Movie(2, "2025-01-01"), Movie(1, "2026-01-01"),
                           Movie(3, "2024-01-01"), Movie(4, "2023-01-01"), Movie(5, "2022-01-01"));

        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, Ids(RowFilter.Build(page, Conf)));
    }
}
