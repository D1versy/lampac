using CubProxy;
using System;
using Xunit;

namespace QbitDownload.Tests;

// Сторож номера страницы в рядах каталога CUB (qdl 2.112) — Modules/Proxy/CubProxy/PageGuard.cs.
// Дефект и замеры — шапка PageGuard.cs и §DI/§DO. Что здесь под защитой:
//  • 🔴 адрес БЕЗ параметра page означает первую страницу — так ходит ряд ГЛАВНОЙ, и именно он
//    был пострадавшим. Проверять только явный page= значит не закрыть главный симптом;
//  • кламп за краем ленты признаётся, только если тело на него ПОХОЖЕ (пришла первая или
//    последняя страница): total_pages берётся из подозрительного тела и у одного ряда нестабилен;
//  • числа читаются строго (integer/string, не 1.0) — единое правило с копией в CatalogWarmup;
//  • чужая форма тела (у /blocked это МАССИВ) — строго no-op, как и у RowFilter;
//  • 🔴 повтор не смеет менять page: добор соседних страниц отменён в 2.94 (§DA) — равенство дословно;
//  • ключ копии одинаков для ряда главной и «Ещё» стр. 1 — это одна апстримная страница;
//  • предохранитель: открывается ТОЛЬКО от подтверждённых расхождений, повторы ограничены
//    потолком, окно сбрасывается вперёд и не откатывается назад, ноль везде значит «выключено»;
//  • решение по ответу (Decide) — чистое: ветка «предохранитель открыт → только наблюдать»
//    иначе жила бы только в контроллере, недостижимом для тестов.
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

    // ── кандидаты (общее правило с фильтром по году — RowFilter.IsCatalogApi) ───────────────

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
    {
        Assert.Equal(expected, PageGuard.IsCandidate(subdomain, uri));
        Assert.Equal(expected, RowFilter.IsCatalogApi(subdomain, uri));   // одно правило — один источник
    }

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
    [InlineData("?PAGE=5", 5)]                  // регистр не важен
    [InlineData("?page=2&page=2", 2)]           // дубль с тем же значением — не спор
    public void Страница_читается_из_запроса(string uri, int expected)
        => Assert.Equal(expected, PageGuard.RequestedPage(uri));

    // 🔴 Ряд ГЛАВНОЙ ходит без page (§DI): «Ещё» — на ?sort=now_playing&page=1&email=, а сам ряд —
    // на ?sort=now_playing&email=. Это РАЗНЫЕ ключи кеша, и жалоба владельца была про второй.
    [Theory]
    [InlineData("?sort=now_playing&email=")]
    [InlineData("top/hundred/movie")]
    [InlineData("")]
    [InlineData("?page")]                       // нет «=» — считаем, что параметра нет
    public void Без_параметра_page_ждём_первую(string uri)
        => Assert.Equal(1, PageGuard.RequestedPage(uri));

    [Theory]
    [InlineData("?page=abc")]
    [InlineData("?page=")]
    [InlineData("?page=0")]
    [InlineData("?page=-3")]
    [InlineData("?page=1.0")]
    [InlineData("?page=2&page=9")]              // дубль с РАЗНЫМИ значениями — у фреймворков выигрывает то первый, то последний
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

    [Fact]
    public void Judge_отдаёт_цифры_для_контроллера_и_журнала()
    {
        var j = PageGuard.Judge("?sort=now_playing&page=1&email=", Row(11, cards: 5));
        Assert.Equal(PageGuard.Verdict.Mismatch, j.verdict);
        Assert.Equal(1, j.wanted);
        Assert.Equal(11, j.got);
        Assert.Equal(5, j.results);
    }

    // ── кламп за краем ленты ────────────────────────────────────────────────────────────────

    // ⚠️ За последней страницей апстрим вправе клампить — это НЕ отравление. Без этого правила
    // сторож делал бы повтор на каждом заходе за край ленты.
    [Theory]
    [InlineData(1)]      // откатил на первую
    [InlineData(427)]    // или на последнюю
    public void Кламп_за_последней_страницей_не_расхождение(int got)
        => Assert.Equal(PageGuard.Verdict.Skip, PageGuard.Check("?page=99999", Row(got, totalPages: 427)));

    // 🔴 total_pages приходит из ПОДОЗРИТЕЛЬНОГО тела и у одного ряда нестабилен (15 на page=1,
    // 21 на page=2 — §CW). Чужая страница 2 с total_pages=15 на запрос page=18 — это отравление,
    // а не кламп: на кламп тело не похоже.
    [Fact]
    public void Ложный_кламп_из_чужого_тела_ловится()
        => Assert.Equal(PageGuard.Verdict.Mismatch, PageGuard.Check("?page=18", Row(2, totalPages: 15)));

    [Fact]
    public void Ровно_на_последней_странице_проверяем()
        => Assert.Equal(PageGuard.Verdict.Mismatch, PageGuard.Check("?page=427", Row(3, totalPages: 427)));

    [Fact]
    public void Отрицательный_total_pages_игнорируется_а_страница_судится()
        => Assert.Equal(PageGuard.Verdict.Mismatch, PageGuard.Check("?page=1", Row(11, totalPages: -1)));

    [Fact]
    public void Пустая_лента_проверке_не_подлежит()
        => Assert.Equal(PageGuard.Verdict.Skip, PageGuard.Check("?page=1", Row(1, totalPages: 0, cards: 0)));

    [Fact]
    public void Без_total_pages_проверяем_как_обычно()
        => Assert.Equal(PageGuard.Verdict.Mismatch, PageGuard.Check("?page=1", "{\"page\":11,\"results\":[{\"id\":1}]}"));

    // ── форма тела ──────────────────────────────────────────────────────────────────────────

    // /blocked отдаёт МАССИВ, а не объект — сторож обязан быть на нём строго no-op.
    [Theory]
    [InlineData("[{\"id\":0,\"kpid\":5303}]")]
    [InlineData("{}")]
    [InlineData("{\"page\":1}")]                                  // нет results — не наша форма
    [InlineData("{\"results\":[]}")]                              // нет page
    [InlineData("{\"page\":null,\"results\":[]}")]
    [InlineData("{\"page\":\"хрень\",\"results\":[]}")]
    [InlineData("{\"page\":1,\"results\":{}}")]                    // results не массив
    [InlineData("не json вовсе")]
    [InlineData("")]
    [InlineData(null)]
    public void Чужая_форма_тела_проверке_не_подлежит(string body)
        => Assert.Equal(PageGuard.Verdict.Skip, PageGuard.Check("?page=1", body));

    [Fact]
    public void Строковый_номер_страницы_понимается()
        => Assert.Equal(PageGuard.Verdict.Match, PageGuard.Check("?page=2", "{\"page\":\"2\",\"results\":[]}"));

    // Числа — строго целые: 1.0 и 1e0 не читаются. Это ЕДИНОЕ правило с копией в CatalogWarmup:
    // Newtonsoft печатал 1.0 как "1", а System.Text.Json на 1.0 падал — копии расходились.
    [Theory]
    [InlineData("{\"page\":1.0,\"results\":[]}")]
    [InlineData("{\"page\":11.0,\"results\":[]}")]
    [InlineData("{\"page\":1e0,\"results\":[]}")]
    [InlineData("{\"page\":99999999999,\"results\":[]}")]        // не влезает в int
    public void Дробные_и_огромные_числа_не_читаются(string body)
    {
        Assert.Null(PageGuard.Shape(body).page);
        Assert.Equal(PageGuard.Verdict.Skip, PageGuard.Check("?page=1", body));
    }

    [Fact]
    public void Shape_считает_карточки()
    {
        Assert.Equal(3, PageGuard.Shape(Row(1)).results);
        Assert.Equal(0, PageGuard.Shape(Row(1, cards: 0)).results);
        Assert.Equal(-1, PageGuard.Shape("[1,2]").results);
    }

    // ── кеш-бастер ──────────────────────────────────────────────────────────────────────────

    // 🔴 Повтор — ровно тот же адрес плюс уникальный параметр. Ни page±1, ни склейки соседних
    // страниц: именно добор давал дубли в «Ещё» и отменён в 2.94 (§DA). Равенство ДОСЛОВНО —
    // иначе бастер, дописывающий «&page=2&d1v=…», прошёл бы проверку по StartsWith.
    [Theory]
    [InlineData("https://tmdb.cub.best/?sort=now_playing&page=1&email=", "&")]
    [InlineData("http://tmdb.cub.red/top/hundred/movie", "?")]
    public void Кеш_бастер_только_дописывает_параметр(string requri, string sep)
        => Assert.Equal(requri + sep + PageGuard.BustKey + "=abc123", PageGuard.BustUrl(requri, "abc123"));

    [Fact]
    public void Бастер_поверх_бастера_просто_дописывается()
        => Assert.Equal("https://x/y?d1v=old&d1v=new", PageGuard.BustUrl("https://x/y?d1v=old", "new"));

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

    // Ряд главной (без page) и «Ещё» стр. 1 — одна апстримная страница: копия с любого из них
    // годится обоим, а главный пострадавший как раз ряд главной.
    [Fact]
    public void Ряд_главной_и_первая_страница_Ещё_это_одна_копия()
        => Assert.Equal(PageGuard.StoreKey("?sort=now_playing&email="), PageGuard.StoreKey("?sort=now_playing&page=1&email="));

    [Fact]
    public void Адрес_без_параметров_и_с_одними_летучими_это_один_ключ()
    {
        Assert.Equal(PageGuard.StoreKey("top/hundred/movie"), PageGuard.StoreKey("top/hundred/movie?email="));
        Assert.DoesNotContain("?email", PageGuard.StoreKey("top/hundred/movie?email="));
    }

    [Fact]
    public void Порядок_параметров_на_ключ_не_влияет()
        => Assert.Equal(PageGuard.StoreKey("?sort=latest&page=2"), PageGuard.StoreKey("?page=2&sort=latest"));

    [Theory]
    [InlineData("?sort=latest&page=1", "?sort=latest&page=2")]
    [InlineData("?sort=latest&page=1", "?sort=latest&page=10")]   // «page=1» не префикс «page=10»
    [InlineData("?sort=latest&page=1", "?sort=now_playing&page=1")]
    public void Разные_страницы_и_ряды_это_разные_ключи(string a, string b)
        => Assert.NotEqual(PageGuard.StoreKey(a), PageGuard.StoreKey(b));

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

    [Fact]
    public void Годность_копии_границы()
    {
        var now = new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);

        Assert.True(PageGuard.Usable(1, now.AddMinutes(-1440), 1, now, 1440));                  // ровно на границе — годна
        Assert.False(PageGuard.Usable(1, now.AddMinutes(-1440).AddSeconds(-1), 1, now, 1440));
        Assert.False(PageGuard.Usable(1, default, 1, now, 1440));                               // нет mtime
        Assert.True(PageGuard.Usable(1, now.AddDays(3), 1, now, 1440));                         // часы уехали вперёд — не выбрасываем
    }

    // ── решение по ответу ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Skip", false, false, false, "PassThrough")]
    [InlineData("Match", false, false, true, "PassThrough")]
    [InlineData("Mismatch", false, true, true, "Healed")]
    [InlineData("Mismatch", true, true, true, "Healed")]          // вылечили — предохранитель ни при чём
    [InlineData("Mismatch", true, false, true, "Fuse")]           // открыт: только наблюдаем, копию НЕ подставляем
    [InlineData("Mismatch", false, false, true, "Restored")]
    [InlineData("Mismatch", false, false, false, "MismatchNoCache")]
    public void Решение_по_ответу(string verdict, bool fuseOpen, bool healed, bool hasCopy, string expected)
        => Assert.Equal(expected, PageGuard.Decide(Enum.Parse<PageGuard.Verdict>(verdict), fuseOpen, healed, hasCopy).ToString());

    // ── предохранитель ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Предохранитель_открывается_на_потоке_подтверждённых_расхождений()
    {
        var f = default(PageGuard.Fuse);
        const long slot = 100;

        for (int i = 0; i < 3; i++)
            f = PageGuard.Note(f, slot, retried: true, confirmed: true);

        Assert.True(PageGuard.Open(f, slot, openAfter: 3));
        Assert.False(PageGuard.MayRetry(f, slot, retryCap: 60, openAfter: 3));

        // ⚠️ негативный контроль: с порогом «никогда» тот же поток предохранитель НЕ открывает
        Assert.False(PageGuard.Open(f, slot, openAfter: 0));
        Assert.True(PageGuard.MayRetry(f, slot, retryCap: 60, openAfter: 0));
    }

    [Fact]
    public void Предохранитель_не_открывается_раньше_порога()
    {
        var f = default(PageGuard.Fuse);
        for (int i = 0; i < 2; i++) f = PageGuard.Note(f, 100, retried: true, confirmed: true);
        Assert.False(PageGuard.Open(f, 100, openAfter: 3));
    }

    [Fact]
    public void Предохранитель_не_залипает_между_окнами()
    {
        var f = default(PageGuard.Fuse);
        for (int i = 0; i < 5; i++) f = PageGuard.Note(f, 100, retried: true, confirmed: true);

        Assert.True(PageGuard.Open(f, 100, openAfter: 3));
        Assert.False(PageGuard.Open(f, 101, openAfter: 3));                       // новое окно — закрыт
        Assert.True(PageGuard.MayRetry(f, 101, retryCap: 60, openAfter: 3));      // и повторы снова можно

        f = PageGuard.Note(f, 101, retried: true, confirmed: false);
        Assert.Equal((101L, 1, 0), (f.slot, f.retries, f.confirmed));
    }

    [Fact]
    public void Окно_не_откатывается_назад()
    {
        // Запрос A начал повтор в старом окне, B уже перевёл счётчики в новое; Note от A не должен
        // обнулить новое окно и вернуть старое.
        var f = PageGuard.Note(default, 101, retried: true, confirmed: false);
        f = PageGuard.Note(f, 100, retried: false, confirmed: true);
        Assert.Equal((101L, 1, 1), (f.slot, f.retries, f.confirmed));
    }

    [Fact]
    public void Повторы_ограничены_потолком_за_окно()
    {
        var f = default(PageGuard.Fuse);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(PageGuard.MayRetry(f, 100, retryCap: 4, openAfter: 20));
            f = PageGuard.Note(f, 100, retried: true, confirmed: false);
        }

        Assert.False(PageGuard.MayRetry(f, 100, retryCap: 4, openAfter: 20));
        Assert.False(PageGuard.Open(f, 100, openAfter: 20));   // потолок повторов — не предохранитель
    }

    [Fact]
    public void Ноль_повторов_значит_повторов_нет()
    {
        // 0 везде в секции значит «выключено» (keepMinutes, suspectMinutes) — и здесь тоже,
        // иначе владелец, выключая походы наружу через 0, получил бы безлимит.
        Assert.False(PageGuard.MayRetry(default, 100, retryCap: 0, openAfter: 20));
        Assert.False(PageGuard.MayRetry(default, 100, retryCap: -1, openAfter: 20));
    }

    [Fact]
    public void Без_повторов_предохранитель_не_взводится()
    {
        // pageGuardRetry=false: подтвердить, что врёт сам ответ, нечем — confirmed не растёт
        var f = default(PageGuard.Fuse);
        for (int i = 0; i < 30; i++) f = PageGuard.Note(f, 100, retried: false, confirmed: false);
        Assert.Equal(0, f.confirmed);
        Assert.False(PageGuard.Open(f, 100, openAfter: 20));
    }

    [Fact]
    public void Окно_предохранителя_десять_минут()
    {
        var t = new DateTime(2026, 9, 5, 22, 3, 0, DateTimeKind.Utc);

        Assert.Equal(PageGuard.SlotOf(t), PageGuard.SlotOf(t.AddMinutes(6)));
        Assert.NotEqual(PageGuard.SlotOf(t), PageGuard.SlotOf(t.AddMinutes(11)));
    }
}
