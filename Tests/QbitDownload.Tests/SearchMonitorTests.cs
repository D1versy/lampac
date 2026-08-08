using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Антиспам-машина мониторинга поиска.
///
/// Это главное требование к контуру: ложная тревога хуже пропущенной — после пары ложных
/// срабатываний уведомления перестают читать. Поэтому переходы состояний покрыты детерминированно,
/// а не проверяются «на живую» ожиданием окна разогрева.
/// </summary>
public class SearchMonitorTests
{
    static JObject Checks() => new JObject();
    static readonly DateTime T0 = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    static string Eval(JObject checks, bool failed, DateTime now, int streak = 3, int cooldown = 12)
        => QbitController.EvalCheck(checks, "x", failed, streak, "СЛОМАЛОСЬ", "ПОЧИНИЛОСЬ", cooldown, now);

    [Fact]
    public void Молчит_пока_стрик_не_набран()
    {
        var c = Checks();
        Assert.Null(Eval(c, true, T0));                       // 1
        Assert.Null(Eval(c, true, T0.AddMinutes(10)));        // 2
        Assert.Equal("СЛОМАЛОСЬ", Eval(c, true, T0.AddMinutes(20)));   // 3 — только теперь
    }

    [Fact]
    public void Внутри_состояния_fail_больше_не_шумит()
    {
        var c = Checks();
        Eval(c, true, T0); Eval(c, true, T0.AddMinutes(10));
        Assert.Equal("СЛОМАЛОСЬ", Eval(c, true, T0.AddMinutes(20)));
        // дальше — тишина, сколько бы провалов ни было
        for (int i = 3; i < 10; i++)
            Assert.Null(Eval(c, true, T0.AddMinutes(20 + i * 10)));
    }

    [Fact]
    public void Восстановление_сообщается_ровно_один_раз()
    {
        var c = Checks();
        Eval(c, true, T0); Eval(c, true, T0.AddMinutes(10)); Eval(c, true, T0.AddMinutes(20));
        Assert.Equal("ПОЧИНИЛОСЬ", Eval(c, false, T0.AddMinutes(30)));
        Assert.Null(Eval(c, false, T0.AddMinutes(40)));
        Assert.Null(Eval(c, false, T0.AddMinutes(50)));
    }

    [Fact]
    public void Одиночный_провал_между_успехами_стрик_сбрасывает()
    {
        var c = Checks();
        Eval(c, true, T0);
        Eval(c, false, T0.AddMinutes(10));          // сброс
        Assert.Null(Eval(c, true, T0.AddMinutes(20)));
        Assert.Null(Eval(c, true, T0.AddMinutes(30)));
        // третий подряд после сброса — только теперь тревога
        Assert.Equal("СЛОМАЛОСЬ", Eval(c, true, T0.AddMinutes(40)));
    }

    [Fact]
    public void Кулдаун_гасит_повторную_тревогу_после_мигания()
    {
        var c = Checks();
        Eval(c, true, T0); Eval(c, true, T0.AddMinutes(10));
        Assert.Equal("СЛОМАЛОСЬ", Eval(c, true, T0.AddMinutes(20)));
        Assert.Equal("ПОЧИНИЛОСЬ", Eval(c, false, T0.AddMinutes(30)));
        // снова сломалось в пределах кулдауна (12 ч) → молчим
        Eval(c, true, T0.AddMinutes(40)); Eval(c, true, T0.AddMinutes(50));
        Assert.Null(Eval(c, true, T0.AddMinutes(60)));
        // а за пределами кулдауна — сообщаем
        Assert.Equal("СЛОМАЛОСЬ", Eval(c, true, T0.AddHours(13)));
    }

    [Fact]
    public void Номер_инцидента_растёт_для_дедупа_уведомлений()
    {
        var c = Checks();
        Eval(c, true, T0); Eval(c, true, T0.AddMinutes(10)); Eval(c, true, T0.AddMinutes(20));
        Assert.Equal(1, c["x"].Value<int>("incident"));
        Eval(c, false, T0.AddHours(20));
        Eval(c, true, T0.AddHours(21)); Eval(c, true, T0.AddHours(22));
        Eval(c, true, T0.AddHours(23));   // >12 ч от ПРОШЛОЙ поломки → новый инцидент
        Assert.Equal(2, c["x"].Value<int>("incident"));
    }

    // Уведомление «восстановилось» НЕ должно продлевать окно тишины: иначе после починки
    // система на cooldownHours ослепла бы к новой поломке.
    [Fact]
    public void Восстановление_не_продлевает_кулдаун()
    {
        var c = Checks();
        Eval(c, true, T0); Eval(c, true, T0.AddMinutes(10)); Eval(c, true, T0.AddMinutes(20));
        Assert.Equal("ПОЧИНИЛОСЬ", Eval(c, false, T0.AddHours(13)));   // recovery уже за кулдауном
        Eval(c, true, T0.AddHours(14)); Eval(c, true, T0.AddHours(15));
        // кулдаун считается от поломки в T0+20мин, с тех пор >12 ч → сообщаем
        Assert.Equal("СЛОМАЛОСЬ", Eval(c, true, T0.AddHours(16)));
    }

    // ── медиана: на холодном старте относительное правило обязано молчать ──
    [Theory]
    [InlineData(new int[] { }, 0)]
    [InlineData(new[] { 400 }, 0)]
    [InlineData(new[] { 400, 380 }, 0)]              // < 3 точек — базы нет
    [InlineData(new[] { 400, 380, 420 }, 400)]
    [InlineData(new[] { 10, 400, 380, 420, 390 }, 390)]
    public void Медиана_требует_минимум_трёх_точек(int[] vals, int expected)
        => Assert.Equal(expected, QbitController.MedianOrZero(new List<int>(vals)));
}
