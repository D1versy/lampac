using Newtonsoft.Json.Linq;
using System.Linq;
using System.Net;
using System.Net.Http;
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

    // ─────────── IsJunkUrl: гигиена на приёме (qdl 2.65) ───────────
    // Инцидент 23.08.2026: ручной прогон замеров с LAN налил в LRU 61 запись из 128 —
    // адреса с shell-экранированием и кэш-бастером. Два из них апстрим отдавал 404 вечно
    // и держали строку «CUB каталог» красной.

    [Theory]
    [InlineData("/cub/tmdb./", "?sort=top\\&genre=27\\&email=&zzr=1")]   // экранированные амперсанды
    [InlineData("/cub/tmdb.cub.best/blocked&zzr=1", "")]                 // '&' уехал в ПУТЬ
    [InlineData("/cub/tmdb./x=1", "")]                                   // '=' в пути
    [InlineData("/cub/tmdb./\n/x", "")]                                  // управляющий символ
    [InlineData("/cub/tmdb./", "?sort=top\t")]                          // управляющий символ в query
    public void IsJunkUrl_Impossible_True(string path, string query)
        => Assert.True(CatalogWarmup.IsJunkUrl(path, query));

    [Theory]
    [InlineData("/cub/tmdb.cub.rip/blocked", "")]                        // 🔴 легальный ряд, апстрим 200
    [InlineData("/cub/tmdb./", "?cat=tv&sort=top&airdate=1980-1985&genre=35")]
    [InlineData("/cub/tmdb./top/fire/movie", "?page=1&email=")]
    [InlineData("/cub/tmdb./collections/3916", "?page=2&email=")]
    [InlineData("/cub/tmdb./", "?sort=now_playing&page=9&email=&zzr=1")] // zzr сам по себе не мусор: 200
    [InlineData(null, null)]
    public void IsJunkUrl_Legal_False(string path, string query)
        => Assert.False(CatalogWarmup.IsJunkUrl(path, query));

    [Theory]
    [InlineData("/cub/tmdb./", "?sort=top\\&genre=27\\&email=&zzr=1")]
    [InlineData("/cub/tmdb.cub.best/blocked&zzr=1", "")]
    public void IsRowUrl_Junk_False(string path, string query)
        => Assert.False(CatalogWarmup.IsRowUrl(path, query));

    [Fact]
    public void IsRowUrl_LegalBlocked_StillTrue()
    {
        // 🔴 Антирегрессия гигиены: /blocked строит qdl.js (DMCA-список), апстрим отдаёт 200.
        // Правило «'&' в пути» ломает только вариант с '&', сам ряд обязан выжить.
        Assert.True(CatalogWarmup.IsRowUrl("/cub/tmdb.cub.rip/blocked", ""));
        Assert.True(CatalogWarmup.IsRowUrl("/cub/tmdb.cub.rip/blocked", "?uid=diqituzn"));
    }

    // ─────────── IsRowPathQuery: тот же фильтр для склеенной строки из файла ───────────

    [Theory]
    [InlineData("/cub/tmdb./top/fire/movie?page=1&email=", true)]
    [InlineData("/cub/tmdb./?sort=top\\&genre=27\\&email=&zzr=1", false)]   // query теперь ДОХОДИТ до фильтра
    [InlineData("/cub/tmdb.cub.best/blocked&zzr=1", false)]
    [InlineData("/cub/tmdb./?query=dune", false)]
    [InlineData("/cub/tmdb./3/movie/550?api_key=k", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRowPathQuery_SplitsOnFirstQuestionMark(string pathQuery, bool expected)
        => Assert.Equal(expected, CatalogWarmup.IsRowPathQuery(pathQuery));

    // ─────────── ToTemplate: мусор не должен попасть в ШАБЛОН ───────────

    [Fact]
    public void ToTemplate_Junk_ReturnsNull()
    {
        // шаблон дороже ряда: он реплеится до catalogWarmupCardBudget раз за тик
        Assert.Null(CatalogWarmup.ToTemplate("/tmdb/api/3/movie/550", "?api_key=k\\&language=ru"));
        Assert.NotNull(CatalogWarmup.ToTemplate("/tmdb/api/3/movie/550", "?api_key=k&language=ru"));
    }

    // ─────────── Карантин: классификация кодов и переходы ───────────

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(410, true)]
    [InlineData(408, false)]   // таймаут апстрима — временное
    [InlineData(429, false)]   // лимит — временное
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(302, false)]
    [InlineData(0, false)]     // не дошли вовсе
    [InlineData(200, false)]
    public void IsPermanentUrlError_OnlyHardFourXX(int code, bool expected)
        => Assert.Equal(expected, CatalogWarmup.IsPermanentUrlError(code));

    [Fact]
    public void RowQuarantine_ThreeHardFourXX_Buries()
    {
        var s = (fails: 0, dead: false);
        s = CatalogWarmup.RowQuarantine(s.fails, s.dead, ok: false, code: 404, deadAfter: 3);
        Assert.Equal((1, false), s);
        s = CatalogWarmup.RowQuarantine(s.fails, s.dead, ok: false, code: 404, deadAfter: 3);
        Assert.Equal((2, false), s);
        s = CatalogWarmup.RowQuarantine(s.fails, s.dead, ok: false, code: 404, deadAfter: 3);
        Assert.Equal((3, true), s);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(408)]
    [InlineData(0)]
    public void RowQuarantine_ServiceTrouble_NeverBuries(int code)
    {
        var s = (fails: 0, dead: false);
        for (int i = 0; i < 10; i++)
            s = CatalogWarmup.RowQuarantine(s.fails, s.dead, ok: false, code: code, deadAfter: 3);

        Assert.Equal((0, false), s);   // счётчик даже не двигается: ряд ни при чём
    }

    [Fact]
    public void RowQuarantine_Success_Rehabilitates()
        => Assert.Equal((0, false), CatalogWarmup.RowQuarantine(fails: 7, dead: true, ok: true, code: 200, deadAfter: 3));

    [Fact]
    public void RowQuarantine_KillSwitch_CountsButNeverBuries()
    {
        var s = (fails: 0, dead: false);
        for (int i = 0; i < 10; i++)
            s = CatalogWarmup.RowQuarantine(s.fails, s.dead, ok: false, code: 404, deadAfter: 0);

        // счётчик ведём, чтобы после включения не начинать с нуля
        Assert.Equal((10, false), s);
    }

    // ─────────── «Лента» (qdl 2.84): распознавание, форма find/, разбор ответа ───────────

    [Theory]
    [InlineData("/cub/cub.red/api/feed/all", "")]
    [InlineData("/cub/cub.rip/api/feed/all", "?page=1")]
    [InlineData("/cub/CUB.RED/API/FEED/all", "")]              // регистр не важен
    public void IsFeedUrl_Feed_True(string path, string query)
        => Assert.True(CatalogWarmup.IsFeedUrl(path, query));

    [Theory]
    [InlineData("/cub/tmdb./", "?sort=now_playing")]           // ряд каталога — не лента
    [InlineData("/cub/red/api/checker", "")]
    [InlineData("/tmdb/api/3/movie/123", "?api_key=k")]
    [InlineData("/cub/cub.red/api/feed&zzr=1/all", "")]        // '&' в ПУТИ — мусор (IsJunkUrl)
    public void IsFeedUrl_Other_False(string path, string query)
        => Assert.False(CatalogWarmup.IsFeedUrl(path, query));

    [Fact]
    public void IsFeedPathQuery_SplitsQuery()
    {
        Assert.True(CatalogWarmup.IsFeedPathQuery("/cub/cub.red/api/feed/all?page=1"));
        Assert.True(CatalogWarmup.IsFeedPathQuery("/cub/cub.red/api/feed/all"));
        Assert.False(CatalogWarmup.IsFeedPathQuery("/cub/tmdb./?sort=top"));
        Assert.False(CatalogWarmup.IsFeedPathQuery(""));
    }

    [Fact]
    public void ToFindForm_TakesClientUrl_AndPlacesImdbPlaceholder()
    {
        // 🔥 Форму СНИМАЕМ с клиента, а не конструируем: бандл строит адрес динамически, и
        // рукописная реконструкция разъедется с ключом Staticache при обновлении фронта.
        string form = CatalogWarmup.ToFindForm("/tmdb/api/3/find/tt0903747",
                                               "?external_source=imdb_id&api_key=k&language=ru");
        Assert.Equal("/tmdb/api/3/find/{imdb}?external_source=imdb_id&api_key=k&language=ru", form);

        // вторая форма адреса (до XHR-патча) тоже понимается
        Assert.Equal("/cub/tmdb.cub.red/3/find/{imdb}?external_source=imdb_id",
                     CatalogWarmup.ToFindForm("/cub/tmdb.cub.red/3/find/tt123", "?external_source=imdb_id"));
    }

    [Theory]
    [InlineData("/tmdb/api/3/find/tt0903747", "?external_source=tvdb_id")]   // другой источник — не наш случай
    [InlineData("/tmdb/api/3/find/12345", "?external_source=imdb_id")]       // не imdb-идентификатор
    [InlineData("/tmdb/api/3/find/tt12x4", "?external_source=imdb_id")]      // мусор в id
    [InlineData("/tmdb/api/3/movie/123", "?external_source=imdb_id")]        // не find
    [InlineData("/tmdb/api/3/find/tt1", "?external_source=imdb_id&q=a\b")] // экранированный мусор (IsJunkUrl)
    public void ToFindForm_Other_Null(string path, string query)
        => Assert.Null(CatalogWarmup.ToFindForm(path, query));

    [Fact]
    public void ExtractImdbIds_FindsIdsAtAnyDepth_DedupsAndRespectsBudget()
    {
        // Форму ответа ленты не пиним: обходим документ и берём любое imdb_id вида tt<цифры>.
        string json = """
        {"secuses":true,"result":[
          {"id":1,"data":{"imdb_id":"tt0000001","name":"a"}},
          {"id":2,"data":{"imdb_id":"tt0000002"}},
          {"id":3,"data":{"imdb_id":"tt0000001"}},
          {"id":4,"data":{"imdb_id":"nm0000009"}},
          {"id":5,"data":{"imdb_id":null}},
          {"id":6,"card":{"deep":{"imdb_id":"tt0000003"}}}
        ]}
        """;
        var ids = CatalogWarmup.ExtractImdbIds(Encoding.UTF8.GetBytes(json), 10);

        Assert.Equal(new[] { "tt0000001", "tt0000002", "tt0000003" }, ids);   // дедуп + мусор отсеян

        Assert.Equal(2, CatalogWarmup.ExtractImdbIds(Encoding.UTF8.GetBytes(json), 2).Count);   // бюджет
        Assert.Empty(CatalogWarmup.ExtractImdbIds(Encoding.UTF8.GetBytes("{"), 10));            // битое тело
        Assert.Empty(CatalogWarmup.ExtractImdbIds(null, 10));
        Assert.Empty(CatalogWarmup.ExtractImdbIds(Encoding.UTF8.GetBytes(json), 0));
    }

    #region аудит номера страницы (qdl 2.112)
    // Второй сторож дефекта §DI/§DO. Он видит то, что реально отдаётся клиенту, включая HIT-ы,
    // которых сторож в контроллере CubProxy не видит принципиально: на HIT контроллер не
    // исполняется, а отравленная запись раздаётся именно с HIT-ов все три часа.

    static byte[] Row(int page, int total = 427, int cards = 2)
    {
        var sb = new StringBuilder();
        sb.Append("{\"page\":").Append(page).Append(",\"total_pages\":").Append(total).Append(",\"results\":[");
        for (int i = 0; i < cards; i++)
            sb.Append(i > 0 ? "," : "").Append("{\"id\":").Append(i + 1).Append("}");
        return Encoding.UTF8.GetBytes(sb.Append("]}").ToString());
    }

    static HttpResponseMessage Rs(string bucket = null, string id = null, string guard = null)
    {
        var rs = new HttpResponseMessage(HttpStatusCode.OK);
        if (bucket != null) rs.Headers.TryAddWithoutValidation("X-StatiCache-Bucket", bucket);
        if (id != null) rs.Headers.TryAddWithoutValidation("X-StatiCache-Id", id);
        if (guard != null) rs.Headers.TryAddWithoutValidation("X-QDL-Page", guard);
        return rs;
    }

    [Theory]
    [InlineData("/cub/tmdb./?sort=now_playing&page=1&email=", 1)]
    [InlineData("/cub/tmdb./?sort=now_playing&email=", 1)]          // ряд ГЛАВНОЙ ходит без page (§DI)
    [InlineData("/cub/tmdb./?sort=now_playing&page=31&email=", 31)]
    [InlineData("/cub/tmdb./top/hundred/movie", 1)]
    [InlineData("/cub/tmdb./?PAGE=5", 5)]
    public void Запрошенная_страница_читается_из_адреса(string pathQuery, int expected)
        => Assert.Equal(expected, CatalogWarmup.RequestedPage(pathQuery));

    [Theory]
    [InlineData("/cub/tmdb./?page=abc")]
    [InlineData("/cub/tmdb./?page=2&page=9")]     // дубль с разными значениями — судить нечего
    [InlineData("/cub/tmdb./?page=1.0")]
    public void Нечитаемая_страница_в_адресе_даёт_skip(string pathQuery)
        => Assert.Equal(CatalogWarmup.PageVerdict.Skip, CatalogWarmup.CheckPage(pathQuery, Row(1)));

    // Боевые случаи дословно: 04.09 ключ ряда держал page 11 на запрос первой страницы;
    // 05.09 перебор дал page=21 -> 2 и page=31 -> 3.
    [Theory]
    [InlineData("/cub/tmdb./?sort=now_playing&page=1&email=", 11)]
    [InlineData("/cub/tmdb./?sort=now_playing&email=", 11)]
    [InlineData("/cub/tmdb./?sort=now_playing&page=21&email=", 2)]
    [InlineData("/cub/tmdb./?sort=now_playing&page=31&email=", 3)]
    public void Чужая_страница_ловится_прогревом(string pathQuery, int got)
        => Assert.Equal(CatalogWarmup.PageVerdict.Mismatch, CatalogWarmup.CheckPage(pathQuery, Row(got)));

    [Fact]
    public void Совпадение_страницы_это_норма()
        => Assert.Equal(CatalogWarmup.PageVerdict.Match,
            CatalogWarmup.CheckPage("/cub/tmdb./?sort=now_playing&page=4&email=", Row(4)));

    [Fact]
    public void Кламп_за_последней_страницей_не_расхождение()
    {
        Assert.Equal(CatalogWarmup.PageVerdict.Skip, CatalogWarmup.CheckPage("/cub/tmdb./?page=99999", Row(1)));
        Assert.Equal(CatalogWarmup.PageVerdict.Skip, CatalogWarmup.CheckPage("/cub/tmdb./?page=99999", Row(427)));
        // а вот чужая страница 2 с total_pages=15 на запрос 18 на кламп не похожа
        Assert.Equal(CatalogWarmup.PageVerdict.Mismatch, CatalogWarmup.CheckPage("/cub/tmdb./?page=18", Row(2, total: 15)));
    }

    [Theory]
    [InlineData("[{\"id\":0}]")]                       // /blocked отдаёт массив
    [InlineData("{\"page\":1}")]                       // нет results
    [InlineData("{\"results\":[]}")]                   // нет page
    [InlineData("{\"page\":1.0,\"results\":[]}")]       // дробное — не читаем (единое правило с PageGuard)
    [InlineData("не json")]
    [InlineData("")]
    public void Чужая_форма_тела_прогревом_не_судится(string body)
        => Assert.Equal(CatalogWarmup.PageVerdict.Skip,
            CatalogWarmup.CheckPage("/cub/tmdb./?page=1", Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Пустое_тело_не_роняет_проверку()
        => Assert.Equal(CatalogWarmup.PageVerdict.Skip, CatalogWarmup.CheckPage("/cub/tmdb./?page=1", null));

    [Theory]
    [InlineData("/cub/tmdb./?sort=now_playing&email=", 1, true)]        // ряд главной
    [InlineData("/cub/tmdb./?sort=latest&page=1&email=", 1, true)]
    [InlineData("/cub/tmdb./?sort=now_playing&page=2&email=", 2, false)] // глубже — не главная
    [InlineData("/cub/tmdb./?sort=top&genre=18&page=1&email=", 1, false)] // жанровый ряд — не свежесть
    [InlineData("/cub/tmdb./top/hundred/movie?page=1", 1, false)]
    public void Ряд_главной_это_первая_страница_свежести(string pathQuery, int wanted, bool expected)
        => Assert.Equal(expected, CatalogWarmup.IsMainRow(pathQuery, wanted));

    // Под сверку идут только РЯДЫ: постеры, детали карточки и лента отсекаются селектором
    // HealthIdFor, тем же, что у пассивного хелса.
    [Theory]
    [InlineData("/tmdb/img/t/p/w300/a.jpg")]
    [InlineData("/tmdb/api/3/movie/125988")]
    [InlineData("/cub/tmdb./3/movie/125988")]
    [InlineData("/cub/cub.best/api/feed/all")]
    public void Постеры_и_карточки_под_сверку_страницы_не_попадают(string pathQuery)
        => Assert.NotEqual(HealthState.Ids.Cub, CatalogWarmup.HealthIdFor(pathQuery));

    // 🔥 Единственная реальная цена решения «два независимых сторожа» — что копии разъедутся.
    // Ловим машинно: обе линкуются в ЭТУ сборку, гоняем общий корпус через обе.
    [Theory]
    [InlineData("?sort=now_playing&page=1&email=", 1, 427, "Match")]
    [InlineData("?sort=now_playing&page=1&email=", 11, 427, "Mismatch")]
    [InlineData("?sort=now_playing&email=", 11, 427, "Mismatch")]
    [InlineData("?sort=now_playing&page=21&email=", 2, 427, "Mismatch")]
    [InlineData("?sort=now_playing&page=31&email=", 3, 427, "Mismatch")]
    [InlineData("?page=99999", 1, 427, "Skip")]
    [InlineData("?page=99999", 427, 427, "Skip")]
    [InlineData("?page=18", 2, 15, "Mismatch")]
    [InlineData("?page=427", 3, 427, "Mismatch")]
    [InlineData("?page=1", 11, -1, "Mismatch")]
    [InlineData("?page=abc", 1, 427, "Skip")]
    [InlineData("?page=2&page=9", 2, 427, "Skip")]
    [InlineData("top/hundred/movie", 1, 5, "Match")]
    public void Сторож_страницы_в_двух_модулях_судит_одинаково(string uri, int bodyPage, int total, string expected)
    {
        byte[] body = Row(bodyPage, total);
        string text = Encoding.UTF8.GetString(body);

        Assert.Equal(expected, CubProxy.PageGuard.Check(uri, text).ToString());
        Assert.Equal(expected, CatalogWarmup.CheckPage("/cub/tmdb./" + uri, body).ToString());
    }

    // Те же две копии на СЫРЫХ телах — ровно там, где они расходились до выравнивания парсеров
    // (дробные числа, строки, null, нестрогий JSON).
    [Theory]
    [InlineData("?page=1", "{\"page\":\"1\",\"total_pages\":427,\"results\":[]}", "Match")]
    [InlineData("?page=1", "{\"page\":1.0,\"total_pages\":427,\"results\":[]}", "Skip")]
    [InlineData("?page=1", "{\"page\":11.0,\"total_pages\":427,\"results\":[]}", "Skip")]
    [InlineData("?page=1", "{\"page\":1e0,\"results\":[]}", "Skip")]
    [InlineData("?page=99999", "{\"page\":1,\"total_pages\":427.0,\"results\":[]}", "Mismatch")]   // дробный total — как отсутствующий
    [InlineData("?page=1", "{\"page\":1,\"total_pages\":\"427\",\"results\":[]}", "Match")]
    [InlineData("?page=1", "{\"page\":1,\"total_pages\":null,\"results\":[]}", "Match")]
    [InlineData("?page=1", "{\"page\":1,\"total_pages\":0,\"results\":[]}", "Skip")]              // пустая лента
    [InlineData("?page=1", "{\"page\":11,\"total_pages\":0,\"results\":[{\"id\":1}]}", "Mismatch")]
    [InlineData("?page=1", "{\"page\":null,\"results\":[]}", "Skip")]
    [InlineData("?page=1", "[{\"page\":11}]", "Skip")]
    [InlineData("?page=1", "{\"page\":11,\"results\":{}}", "Skip")]
    [InlineData("?page=1", "{\"page\":99999999999,\"results\":[]}", "Skip")]
    [InlineData("?page=1", "{\"page\":\" 11 \",\"results\":[]}", "Mismatch")]                    // int.TryParse терпит пробелы — в обеих
    [InlineData("?page=1", "{'page':11,'results':[]}", "Mismatch")]                             // Newtonsoft терпит одинарные кавычки — в обеих
    [InlineData("?page=1", "{\"page\":11,\"results\":[],}", "Mismatch")]                        // и висячую запятую — в обеих
    [InlineData("?page=1", "{\"page\":11,\"results\":[]} x", "Skip")]                           // мусор после json — в обеих
    public void Сторож_страницы_в_двух_модулях_одинаков_на_сырых_телах(string uri, string body, string expected)
    {
        Assert.Equal(expected, CubProxy.PageGuard.Check(uri, body).ToString());
        Assert.Equal(expected, CatalogWarmup.CheckPage("/cub/tmdb./" + uri, Encoding.UTF8.GetBytes(body)).ToString());
    }

    // ── бухгалтерия аудита: то, что владелец реально увидит ────────────────────────────────

    [Fact]
    public void Аудит_считает_расхождения_и_публикует_снимок()
    {
        TestEnv.FreshCache();
        CatalogWarmup.ResetPageAuditForTests();
        const string H = "192.168.87.24:9118";

        CatalogWarmup.NotePage(H, "/cub/tmdb./?sort=now_playing&email=", Row(11), "application/json", miss: false, Rs("30", "abc"));
        CatalogWarmup.NotePage(H, "/cub/tmdb./?sort=latest&page=1&email=", Row(1), "application/json", miss: true, Rs());
        CatalogWarmup.NotePage(H, "/cub/tmdb./?sort=top&genre=18&page=3&email=", Row(7), "application/json", miss: false, Rs("2", "zzz"));

        // до публикации снимок описывает ПРОШЛЫЙ обход — то есть пуст
        Assert.Equal(0, (int)CatalogWarmup.PageHealthSnapshot()["checked"]);

        CatalogWarmup.PublishPageAuditForTests(rows: 86);
        var s = CatalogWarmup.PageHealthSnapshot();

        Assert.Equal(3, (int)s["checked"]);
        Assert.Equal(2, (int)s["bad"]);
        Assert.Equal(1, (int)s["badMain"]);      // ряд главной — отдельным счётчиком
        Assert.Equal(86, (int)s["rows"]);
        Assert.NotNull(s["at"]);

        var samples = ((JArray)s["samples"]).Select(x => (string)x).ToArray();
        Assert.Equal(2, samples.Length);
        Assert.Contains(samples, x => x.Contains("now_playing p1→11 HIT ГЛАВНАЯ"));
        Assert.Contains(samples, x => x.Contains("top p3→7 HIT"));

        var (items, total) = QdlEvents.Read(10);
        Assert.Equal(2, total);
        Assert.All(items, i => Assert.Equal(QdlEvents.CatDiag, i.Value<string>("cat")));
        Assert.Contains(items, i => i.Value<string>("text").Contains("static/30/abc*") && i.Value<string>("act").Contains("rm -f /c/static/30/abc*"));
    }

    [Fact]
    public void Аудит_не_судит_не_json_и_детали_карточки()
    {
        TestEnv.FreshCache();
        CatalogWarmup.ResetPageAuditForTests();

        CatalogWarmup.NotePage("h", "/cub/tmdb./?page=1", Row(11), "text/html", false, Rs());
        CatalogWarmup.NotePage("h", "/cub/tmdb./3/movie/1?page=1", Row(11), "application/json", false, Rs());
        CatalogWarmup.PublishPageAuditForTests(rows: 2);

        Assert.Equal(0, (int)CatalogWarmup.PageHealthSnapshot()["checked"]);
        Assert.Equal(0, QdlEvents.Read(10).total);
    }

    [Fact]
    public void Примеров_не_больше_восьми_а_счётчик_честный_и_журнал_не_спамит()
    {
        TestEnv.FreshCache();
        CatalogWarmup.ResetPageAuditForTests();

        for (int i = 0; i < 12; i++)
            CatalogWarmup.NotePage("h", "/cub/tmdb./?sort=top&genre=" + i + "&page=1", Row(11), "application/json", false, Rs("1", "id" + i));

        CatalogWarmup.PublishPageAuditForTests(rows: 12);
        var s = CatalogWarmup.PageHealthSnapshot();

        Assert.Equal(12, (int)s["bad"]);
        Assert.Equal(8, ((JArray)s["samples"]).Count);

        // первые 8 — отдельными строками с командой сноса, дальше одна сводная
        var (items, total) = QdlEvents.Read(50);
        Assert.Equal(9, total);
        Assert.Single(items, i => i.Value<string>("key") == "cubpage:summary");
    }

    [Fact]
    public void Аудит_считает_вмешательства_сторожа_на_своих_промахах()
    {
        TestEnv.FreshCache();
        CatalogWarmup.ResetPageAuditForTests();

        CatalogWarmup.NotePage("h", "/cub/tmdb./?sort=now_playing&page=2", Row(2), "application/json", true, Rs(guard: "healed"));
        CatalogWarmup.NotePage("h", "/cub/tmdb./?sort=now_playing&page=3", Row(3), "application/json", true, Rs(guard: "restored"));
        CatalogWarmup.NotePage("h", "/cub/tmdb./?sort=now_playing&page=4", Row(4), "application/json", true, Rs(guard: "match"));
        CatalogWarmup.PublishPageAuditForTests(rows: 3);

        var g = (JObject)CatalogWarmup.PageHealthSnapshot()["guard"];
        Assert.Equal(1, (int)g["healed"]);
        Assert.Equal(1, (int)g["restored"]);
        Assert.Equal(0, (int)g["mismatch"]);
    }

    [Fact]
    public void Повтор_того_же_адреса_и_хоста_в_журнал_не_дублируется_а_другой_хост_попадает()
    {
        TestEnv.FreshCache();
        CatalogWarmup.ResetPageAuditForTests();

        CatalogWarmup.NotePage("lan", "/cub/tmdb./?sort=now_playing&email=", Row(11), "application/json", false, Rs("1", "a"));
        CatalogWarmup.NotePage("lan", "/cub/tmdb./?sort=now_playing&email=", Row(11), "application/json", false, Rs("1", "a"));
        CatalogWarmup.NotePage("ext", "/cub/tmdb./?sort=now_playing&email=", Row(11), "application/json", false, Rs("2", "b"));

        // отравление ПО-ХОСТОВОЕ: у внешнего входа своя запись и своя команда сноса
        Assert.Equal(2, QdlEvents.Read(10).total);
    }

    [Fact]
    public void Сброс_снимка_не_привязан_к_ResetForTests()
    {
        // ResetForTests зовётся из Reload() при promote — снимок обхода этого процесса он трогать не должен
        CatalogWarmup.ResetPageAuditForTests();
        CatalogWarmup.NotePage("h", "/cub/tmdb./?sort=latest&page=1", Row(1), "application/json", false, Rs());
        CatalogWarmup.PublishPageAuditForTests(rows: 1);

        CatalogWarmup.ResetForTests();
        Assert.Equal(1, (int)CatalogWarmup.PageHealthSnapshot()["checked"]);

        CatalogWarmup.ResetPageAuditForTests();
        Assert.Equal(0, (int)CatalogWarmup.PageHealthSnapshot()["checked"]);
    }
    #endregion
}
