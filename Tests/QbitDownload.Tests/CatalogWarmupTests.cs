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
}
