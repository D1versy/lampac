using CubProxy;
using System;
using Xunit;

namespace QbitDownload.Tests;

// Сторож номера страницы в рядах каталога CUB (qdl 2.112) — Modules/Proxy/CubProxy/PageGuard.cs.
//
// Дефект (§DI, §DO): у CUB перед API свой кеш nginx, и он периодически отдаёт тело ЧУЖОЙ
// страницы. Мы примораживаем это на cache_api (3 ч) отдельно под каждый вход, и владелец видит
// в топе главной одиннадцатую страницу живого потока.
//
// Что здесь под защитой:
//  • 🔴 адрес БЕЗ параметра page означает первую страницу — именно так ходит ряд ГЛАВНОЙ, и
//    именно он был пострадавшим. Проверять только явный page= значит не закрыть главный симптом;
//  • запрос за пределы total_pages — законный кламп апстрима, а НЕ отравление: без этого правила
//    сторож молотил бы повторы на каждом заходе за край ленты;
//  • чужая форма тела (у /blocked это МАССИВ) — строго no-op, как и у RowFilter;
//  • кандидатность гейтит буферизацию, поэтому картинки и детали карточки в неё попадать не должны;
//  • 🔴 повтор не смеет менять page: добор соседних страниц отменён в 2.94 (§DA);
//  • предохранитель открывается на потоке расхождений и не залипает между окнами.
public class CubPageGuardTests
{
    static string Row(int page, int totalPages = 427, int cards = 3)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"page\":").Append(page)
          .Append(",\"total_pages\":").Append(totalPages)
          .Append(",\"results\":[");

        for (int i = 0; i < cards; i++)
            sb.Append(i > 0 ? "," : "").Append("{\"id\":").Append(i + 1).Append(",\"title\":\"m\"}");

        return sb.Append("]}").ToString();
    }

    // ── кандидаты ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("tmdb", "?sort=now_playing&email=", true)]                    // ряд главной
    [InlineData("tmdb", "?sort=now_playing&page=1&email=", true)]             // экран «Ещё»
    [InlineData("tmdb", "top/hundred/movie?page=1&email=", true)]
    [InlineData("tmdb", "?cat=anime&sort=top&genre=18&page=1&email=", true)]
    [InlineData("tmdb", "3/movie/125988?api_key=x", false)]                   // детали карточки
    [InlineData("tmdb", "api/3/tv/1399/season/1", false)]
    [InlineData("tmdb", "?sort=top&query=укрытие", false)]                    // поиск
    [InlineData("imagetmdb", "t/p/w300/abc.jpg", false)]                      // картинки
    [InlineData("cdn", "extensions/theme/196.css", false)]
    [InlineData("geo", "", false)]
    [InlineData("", "api/reactions/get/movie_1", false)]
    public void Кандидаты_только_json_api_каталога(string subdomain, string uri, bool expected)
        => Assert.Equal(expected, PageGuard.IsCandidate(subdomain, uri));

    [Fact]
    public void Кандидат_не_падает_на_пустом_адресе()
    {
        Assert.False(PageGuard.IsCandidate("tmdb", null));
        Assert.False(PageGuard.IsCandidate(null, "?page=1"));
    }

    // ── запрошенная страница ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("?sort=now_playing&page=1&email=", 1)]
    [InlineData("?sort=now_playing&page=31&email=", 31)]
    [InlineData("?page=7", 7)]
    public void Страница_читается_из_запроса(string uri, int expected)
        => Assert.Equal(expected, PageGuard.RequestedPage(uri));

    // 🔴 Ряд ГЛАВНОЙ ходит без page (§DI): «Ещё» — на ?sort=now_playing&page=1&email=, а сам ряд —
    // на ?sort=now_playing&email=. Это РАЗНЫЕ ключи кеша, и жалоба владельца была про второй.
    [Theory]
    [InlineData("?sort=now_playing&email=")]
    [InlineData("top/hundred/movie")]
    [InlineData("")]
    public void Без_параметра_page_ждём_первую(string uri)
        => Assert.Equal(1, PageGuard.RequestedPage(uri));

    [Theory]
    [InlineData("?page=abc")]
    [InlineData("?page=")]
    [InlineData("?page=0")]
    [InlineData("?page=-3")]
    public void Нечитаемая_страница_проверке_не_подлежит(string uri)
    {
        Assert.Null(PageGuard.RequestedPage(uri));
        Assert.Equal(PageGuard.Verdict.Skip, PageGuard.Check(uri, Row(1)));
    }

    // ── сам вердикт ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Страница_совпала_вердикт_match()
        => Assert.Equal(PageGuard.Verdict.Match, PageGuard.Check("?sort=now_playing&page=3&email=", Row(3)));

    [Fact]
    public void Без_параметра_page_совпадение_с_первой()
        => Assert.Equal(PageGuard.Verdict.Match, PageGuard.Check("?sort=now_playing&email=", Row(1)));

    // Боевые случаи дословно: 04.09 ключ ряда держал тело page 11 на запрос первой страницы;
    // 05.09 перебор page=1..40 дал page=21 → 2 и page=31 → 3 (оба на x-cache-status: HIT).
    [Theory]
    [InlineData("?sort=now_playing&page=1&email=", 11)]
    [InlineData("?sort=now_playing&email=", 11)]
    [InlineData("?sort=now_playing&page=21&email=", 2)]
    [InlineData("?sort=now_playing&page=31&email=", 3)]
    public void Тело_чужой_страницы_ловится(string uri, int got)
        => Assert.Equal(PageGuard.Verdict.Mismatch, PageGuard.Check(uri, Row(got)));

    // ⚠️ За последней страницей апстрим вправе клампить — это НЕ отравление. Без этого правила
    // сторож делал бы повтор на каждом заходе за край ленты.
    [Fact]
    public void Запрос_за_пределами_total_pages_не_расхождение()
        => Assert.Equal(PageGuard.Verdict.Skip, PageGuard.Check("?page=99999", Row(1, totalPages: 427)));

    [Fact]
    public void Ровно_на_последней_странице_проверяем()
        => Assert.Equal(PageGuard.Verdict.Mismatch, PageGuard.Check("?page=427", Row(3, totalPages: 427)));

    [Fact]
    public void Без_total_pages_проверяем_как_обычно()
    {
        string body = "{\"page\":11,\"results\":[{\"id\":1}]}";
        Assert.Equal(PageGuard.Verdict.Mismatch, PageGuard.Check("?page=1", body));
    }

    // ── чужая форма тела ────────────────────────────────────────────────────────────────────

    // /blocked отдаёт МАССИВ, а не объект — сторож обязан быть на нём строго no-op.
    [Theory]
    [InlineData("[{\"id\":0,\"kpid\":5303}]")]
    [InlineData("{}")]
    [InlineData("{\"page\":1}")]                                  // нет results — не наша форма
    [InlineData("{\"results\":[]}")]                              // нет page
    [InlineData("{\"page\":null,\"results\":[]}")]
    [InlineData("{\"page\":\"хрень\",\"results\":[]}")]
    [InlineData("не json вовсе")]
    [InlineData("")]
    [InlineData(null)]
    public void Чужая_форма_тела_проверке_не_подлежит(string body)
        => Assert.Equal(PageGuard.Verdict.Skip, PageGuard.Check("?page=1", body));

    [Fact]
    public void Строковый_номер_страницы_понимается()
        => Assert.Equal(PageGuard.Verdict.Match, PageGuard.Check("?page=2", "{\"page\":\"2\",\"results\":[]}"));

    // ── кеш-бастер ──────────────────────────────────────────────────────────────────────────

    // 🔴 Повтор — ровно тот же адрес плюс уникальный параметр. Ни page±1, ни склейки соседних
    // страниц: именно добор давал дубли в «Ещё» и отменён в 2.94 (§DA).
    [Theory]
    [InlineData("https://tmdb.cub.best/?sort=now_playing&page=1&email=")]
    [InlineData("https://tmdb.cub.best/top/hundred/movie")]
    public void Кеш_бастер_не_меняет_запрошенную_страницу(string requri)
    {
        string bust = PageGuard.BustUrl(requri, "abc123");

        Assert.Contains(PageGuard.BustKey + "=abc123", bust);
        Assert.StartsWith(requri, bust);

        // номер страницы в адресе обязан остаться прежним
        int? before = PageGuard.RequestedPage(requri.Substring(requri.IndexOf('/', 8) + 1));
        int? after = PageGuard.RequestedPage(bust.Substring(bust.IndexOf('/', 8) + 1));
        Assert.Equal(before, after);
    }

    [Fact]
    public void Бастер_не_ломает_адрес_без_вопроса()
        => Assert.Equal("https://x/y?d1v=n", PageGuard.BustUrl("https://x/y", "n"));

    // ── ключ хранилища копий ────────────────────────────────────────────────────────────────

    // Один и тот же ряд просят с трёх входов; ключ обязан сводить их в одну копию.
    [Fact]
    public void Летучие_параметры_из_ключа_выброшены()
    {
        string a = PageGuard.StoreKey("?sort=now_playing&page=1&email=");
        string b = PageGuard.StoreKey("?sort=now_playing&page=1&email=me@example.com&uid=42&d1v=zz");

        Assert.Equal(a, b);
        Assert.Contains("page=1", a);
        Assert.Contains("sort=now_playing", a);
    }

    [Fact]
    public void Порядок_параметров_на_ключ_не_влияет()
        => Assert.Equal(PageGuard.StoreKey("?sort=latest&page=2"), PageGuard.StoreKey("?page=2&sort=latest"));

    [Fact]
    public void Разные_страницы_это_разные_ключи()
        => Assert.NotEqual(PageGuard.StoreKey("?sort=latest&page=1"), PageGuard.StoreKey("?sort=latest&page=2"));

    // ── годность копии для подстановки ──────────────────────────────────────────────────────

    [Fact]
    public void Подставляем_только_свежую_копию_той_же_страницы()
    {
        var now = new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);

        Assert.True(PageGuard.Usable(1, now.AddMinutes(-10), wanted: 1, now, keepMinutes: 1440));
        Assert.False(PageGuard.Usable(1, now.AddMinutes(-2000), wanted: 1, now, keepMinutes: 1440));  // протухла
        Assert.False(PageGuard.Usable(2, now.AddMinutes(-10), wanted: 1, now, keepMinutes: 1440));    // чужая страница
        Assert.False(PageGuard.Usable(null, now, wanted: 1, now, keepMinutes: 1440));                 // копии нет
        Assert.False(PageGuard.Usable(1, now, wanted: 1, now, keepMinutes: 0));                       // выключено
    }

    // ── предохранитель ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Предохранитель_открывается_на_потоке_расхождений()
    {
        var f = default(PageGuard.Fuse);
        long slot = PageGuard.SlotOf(DateTime.UtcNow);

        for (int i = 0; i < 3; i++)
            f = PageGuard.Note(f, slot, retried: true, confirmed: true, retryCap: 60, openAfter: 3);

        Assert.True(f.open);
        Assert.False(PageGuard.MayRetry(f, slot, retryCap: 60));

        // ⚠️ негативный контроль: без порога тот же поток предохранитель НЕ открывает
        var g = default(PageGuard.Fuse);
        for (int i = 0; i < 3; i++)
            g = PageGuard.Note(g, slot, retried: true, confirmed: true, retryCap: 60, openAfter: 0);

        Assert.False(g.open);
    }

    [Fact]
    public void Предохранитель_не_залипает_между_окнами()
    {
        var f = default(PageGuard.Fuse);
        long slot = PageGuard.SlotOf(DateTime.UtcNow);

        for (int i = 0; i < 5; i++)
            f = PageGuard.Note(f, slot, retried: true, confirmed: true, retryCap: 60, openAfter: 3);

        Assert.True(f.open);
        Assert.True(PageGuard.MayRetry(f, slot + 1, retryCap: 60));       // новое окно — можно

        f = PageGuard.Note(f, slot + 1, retried: true, confirmed: false, retryCap: 60, openAfter: 3);
        Assert.False(f.open);
        Assert.Equal(1, f.retries);
        Assert.Equal(0, f.confirmed);
    }

    [Fact]
    public void Повторы_ограничены_потолком_за_окно()
    {
        var f = default(PageGuard.Fuse);
        long slot = PageGuard.SlotOf(DateTime.UtcNow);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(PageGuard.MayRetry(f, slot, retryCap: 4));
            f = PageGuard.Note(f, slot, retried: true, confirmed: false, retryCap: 4, openAfter: 0);
        }

        Assert.False(PageGuard.MayRetry(f, slot, retryCap: 4));
    }

    [Fact]
    public void Окно_предохранителя_десять_минут()
    {
        var t = new DateTime(2026, 9, 5, 22, 3, 0, DateTimeKind.Utc);

        Assert.Equal(PageGuard.SlotOf(t), PageGuard.SlotOf(t.AddMinutes(6)));
        Assert.NotEqual(PageGuard.SlotOf(t), PageGuard.SlotOf(t.AddMinutes(11)));
    }
}
