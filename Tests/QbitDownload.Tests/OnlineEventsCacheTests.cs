using Online;
using System.Collections.Generic;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.45: L2-снимок набора балансеров («Онлайн»).
///
/// Тут два инварианта, нарушение которых заметно испортит клиенту жизнь, причём надолго —
/// на весь TTL (сутки), — поэтому они и вынесены в чистые функции под тесты:
///  • неполный набор нельзя сохранять: LifeEvents считает ready по совпадению длин, и клиент
///    навсегда получил бы «готово» на обрезанном списке кнопок;
///  • массовый сбой нельзя выдавать за «источников нет»: упавший flaresolverr/VPN закрепил бы
///    пустые кнопки на сутки.
/// </summary>
public class OnlineEventsCacheTests
{
    static OnlineEventsCache.Item Ok(int i) => new() { code = "<div>" + i + "</div>", index = i, work = true };
    static OnlineEventsCache.Item Dead(int i) => new() { code = "<div>" + i + "</div>", index = i, work = false };

    static List<OnlineEventsCache.Item> Set(int total, int working)
    {
        var list = new List<OnlineEventsCache.Item>();
        for (int i = 0; i < total; i++)
            list.Add(i < working ? Ok(i) : Dead(i));
        return list;
    }

    [Fact]
    public void Полный_набор_признаётся_полным()
        => Assert.True(OnlineEventsCache.IsComplete(Set(23, 23), 23));

    [Fact]
    public void Недобранный_набор_неполон()
        => Assert.False(OnlineEventsCache.IsComplete(Set(20, 20), 23));

    [Fact]
    public void Набор_с_дыркой_неполон()
    {
        // именно так выглядит снимок, снятый до завершения всех проб: элемент есть, code == null
        var items = Set(23, 23);
        items[7] = null;
        Assert.False(OnlineEventsCache.IsComplete(items, 23));
    }

    [Fact]
    public void Элемент_без_кода_делает_набор_неполным()
    {
        var items = Set(23, 23);
        items[3] = new OnlineEventsCache.Item { code = null, index = 3, work = false };
        Assert.False(OnlineEventsCache.IsComplete(items, 23));
    }

    [Theory]
    [InlineData(null)]
    public void Null_не_полон(List<OnlineEventsCache.Item> items)
        => Assert.False(OnlineEventsCache.IsComplete(items, 23));

    [Fact]
    public void Нулевой_ожидаемый_счётчик_не_полон()
        => Assert.False(OnlineEventsCache.IsComplete(Set(0, 0), 0));

    // ── распознавание массового сбоя ──

    [Fact]
    public void Все_рабочие_это_не_авария()
        => Assert.False(OnlineEventsCache.LooksLikeOutage(Set(23, 23)));

    [Fact]
    public void Половина_рабочих_это_не_авария()
        => Assert.False(OnlineEventsCache.LooksLikeOutage(Set(23, 12)));

    [Fact]
    public void Ни_одного_рабочего_это_авария()
        => Assert.True(OnlineEventsCache.LooksLikeOutage(Set(23, 0)));

    [Fact]
    public void Два_рабочих_из_двадцати_трёх_это_авария()
        // ~9% при пороге 30% — так выглядит упавший flaresolverr или отвалившийся VPN
        => Assert.True(OnlineEventsCache.LooksLikeOutage(Set(23, 2)));

    [Fact]
    public void Ровно_на_пороге_не_авария()
    {
        int total = 20, work = (int)(total * OnlineEventsCache.MinWorkShare);   // 6
        Assert.False(OnlineEventsCache.LooksLikeOutage(Set(total, work)));
    }

    [Fact]
    public void Пустой_список_это_авария()
        => Assert.True(OnlineEventsCache.LooksLikeOutage(new List<OnlineEventsCache.Item>()));

    [Fact]
    public void Null_это_авария()
        => Assert.True(OnlineEventsCache.LooksLikeOutage(null));
}
