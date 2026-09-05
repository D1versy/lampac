using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.111 — разделение потоков уведомлений.
///
/// Зритель видит только «вышла новая серия/сезон» и итог пачки; постановка в очередь, смена
/// раздачи, охота, качество и диагностика уезжают в журнал владельца (вкладка «Уведомления»
/// в /admin/d1v). Здесь закреплены три вещи, на которых это держится:
///   1. белый список видов (он же фильтр ленты на чтении);
///   2. формулировки — один словарь на все три контура (торренты, jut.su, XSMART);
///   3. СИММЕТРИЧНОЕ отсечение уже учтённых серий — то, из-за чего «Тяжёлый рыцарь» получал
///      две строки про одну десятую серию.
/// </summary>
public class NotiRouteTests
{
    // ── 1. маршрутизация ────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]        // обычная серия
    [InlineData("")]
    [InlineData("OVA")]
    [InlineData("RANGE")]
    [InlineData("FILM")]
    [InlineData("WAVE")]      // волна новых серий
    [InlineData("SEASON")]
    [InlineData("TITLE")]     // итог пачки
    [InlineData("NEW")]       // только от подписок в режиме «только уведомляю»
    public void Зритель_видит(string kind) => Assert.True(NotiRoute.UserKind(kind));

    [Theory]
    [InlineData("START")]     // «раздача обновилась, качаются новые серии»
    [InlineData("SWITCH")]    // «найдена более полная раздача»
    [InlineData("INFO")]      // «переключено», «раздачи нет»
    [InlineData("NOSPACE")]   // «нет места»
    [InlineData("DIAG")]      // диагностика поиска
    public void Зритель_не_видит(string kind) => Assert.False(NotiRoute.UserKind(kind));

    [Fact]
    public void Киллсвитч_возвращает_всё_зрителю()
    {
        TestEnv.EnsureConf();
        ModInit.conf.notiSplit = false;
        try { Assert.True(NotiRoute.UserKind("SWITCH")); }
        finally { ModInit.conf.notiSplit = true; }
    }

    // ── 2. формулировки ─────────────────────────────────────────────────
    [Fact]
    public void Одна_серия() => Assert.Equal("Вышла новая серия 10", NotiRoute.Episodes(1, new[] { 10 }));

    [Fact]
    public void Сезон_печатается_только_со_второго()
        => Assert.Equal("Вышла новая серия 10 · сезон 3", NotiRoute.Episodes(3, new[] { 10 }));

    [Fact]
    public void Подряд_идущие_серии_диапазоном()
        => Assert.Equal("Вышли новые серии 8–10", NotiRoute.Episodes(1, new[] { 9, 8, 10 }));

    [Fact]
    public void Вразнобой_только_счётчиком()
        => Assert.Equal("Вышло новых серий: 3", NotiRoute.Episodes(1, new[] { 2, 5, 9 }));

    [Fact]
    public void Дубли_серий_не_раздувают_счётчик()
        => Assert.Equal("Вышла новая серия 7", NotiRoute.Episodes(1, new[] { 7, 7, 7 }));

    [Fact]
    public void Пустая_волна_текста_не_даёт()
    {
        Assert.Null(NotiRoute.Episodes(1, new int[0]));
        Assert.Null(NotiRoute.Episodes(1, null));
    }

    [Fact]
    public void Сезон_и_итог_пачки()
    {
        Assert.Equal("Вышел сезон 3", NotiRoute.Season(3));
        Assert.Equal("Скачано серий: 12 из 26", NotiRoute.Batch(12, 26, 0));
        Assert.Equal("Скачано серий: 26", NotiRoute.Batch(26, 26, 0));
        Assert.Equal("Качество улучшено: 12 серий (до 1080p)", NotiRoute.Batch(12, 26, 1080));
    }

    [Fact]
    public void В_тексте_зрителя_нет_кухни()
    {
        foreach (var s in new[] { NotiRoute.Episodes(2, new[] { 4, 5 }), NotiRoute.Season(2),
                                  NotiRoute.Batch(3, 9, 0) })
        {
            Assert.DoesNotContain("раздач", s);
            Assert.DoesNotContain("сид", s);
            Assert.DoesNotContain("очеред", s);
            Assert.DoesNotContain("jut.su", s);
        }
    }

    // ── 3. ключ волны ───────────────────────────────────────────────────
    [Fact]
    public void Ключ_волны_по_максимальной_серии()
    {
        Assert.Equal("wave-s3e10", NotiRoute.WaveKey(3, new[] { 8, 10, 9 }));
        Assert.Equal("wave-e10", NotiRoute.WaveKey(-1, new[] { 10 }));
        Assert.Null(NotiRoute.WaveKey(1, new int[0]));
    }

    [Fact]
    public void Ключ_волны_не_пересекается_с_эпизодным()
    {
        // 🔴 UNIQUE noti(seriesKey, epkey) схлопнул бы волну со строкой самой серии
        Assert.NotEqual("s3e10", NotiRoute.WaveKey(3, new[] { 10 }));
        Assert.StartsWith("wave-", NotiRoute.WaveKey(3, new[] { 10 }));
    }
}

/// <summary>
/// Симметрия отсечения уже учтённых серий. 🔥 Ровно тут жил дубль «Изгнанного реинкарнированного
/// тяжёлого рыцаря»: донорский пак распознавался с сезоном и писал «s1e10», основная раздача
/// сезона в имени не несла и давала «e10» — прямое направление работало, обратное нет.
/// </summary>
public class SeenSymmetryTests
{
    [Fact]
    public void Точное_совпадение() => Assert.True(Access.SeenAlready(new[] { "s1e10" }, 1, 10, "s1e10"));

    [Fact]
    public void Прямое_направление_с_сезоном_глушится_бессезонным()
        => Assert.True(Access.SeenAlready(new[] { "e10" }, 1, 10, "s1e10"));

    [Fact]
    public void Обратное_направление_бессезонный_глушится_сезонным()   // ← регрессия 05.09.2026
        => Assert.True(Access.SeenAlready(new[] { "s1e10" }, -1, 10, "e10"));

    [Fact]
    public void Обратное_направление_работает_с_двузначным_сезоном()
        => Assert.True(Access.SeenAlready(new[] { "s12e7" }, -1, 7, "e7"));

    [Fact]
    public void Чужая_серия_не_глушится()
    {
        Assert.False(Access.SeenAlready(new[] { "s1e10" }, -1, 11, "e11"));
        Assert.False(Access.SeenAlready(new[] { "s1e110" }, -1, 10, "e10"));   // не «оканчивается на»
        Assert.False(Access.SeenAlready(new[] { "se10" }, -1, 10, "e10"));     // без цифр сезона — не ключ
    }

    [Fact]
    public void Спецвыпуски_эквивалентностью_не_пользуются()
        => Assert.False(Access.SeenAlready(new[] { "s1e1" }, -1, 1, "ova1", kind: "OVA"));
}
