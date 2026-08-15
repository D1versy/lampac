using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.45: постепенный прогрев кнопок «Онлайн».
/// Требование владельца — «совсем не спеша, чтобы точно не заспамить и не попасть в лимиты»,
/// поэтому под тестом именно то, что ограничивает темп: порог обновления и адаптивный тормоз.
/// </summary>
public class OnlineWarmTests
{
    const int Ttl = 1440;          // минут (сутки)
    const long Now = 1_800_000_000;

    [Fact]
    public void Ни_разу_не_гретое_надо_греть()
        => Assert.True(OnlineWarm.NeedsRefresh(0, Now, Ttl));

    [Fact]
    public void Свежее_трогать_не_надо()
        => Assert.False(OnlineWarm.NeedsRefresh(Now - 3600, Now, Ttl));

    [Fact]
    public void До_двух_третей_TTL_не_трогаем()
        => Assert.False(OnlineWarm.NeedsRefresh(Now - (Ttl * 60L * 2 / 3 - 60), Now, Ttl));

    [Fact]
    public void После_двух_третей_TTL_обновляем()
        // раньше — лишние пробы, позже — клиент успеет поймать холодный набор
        => Assert.True(OnlineWarm.NeedsRefresh(Now - Ttl * 60L * 2 / 3, Now, Ttl));

    // ── распознавание пустого ответа /lite/events ──

    [Theory]
    [InlineData("[]")]
    [InlineData("  []  ")]
    [InlineData("")]
    [InlineData(null)]
    public void Пустая_выдача_считается_неудачей(string body)
        => Assert.True(OnlineWarm.LooksEmpty(body));

    [Fact]
    public void Непустая_выдача_считается_удачей()
        => Assert.False(OnlineWarm.LooksEmpty("[{\"name\":\"Filmix\"}]"));

    // ── адаптивный тормоз ──

    static readonly (int a, int b, int c) Max = (20, 10, 5);

    [Fact]
    public void Много_пустых_режет_капы_вдвое()
    {
        var got = OnlineWarm.NextCaps((20, 10, 5), Max, done: 10, empty: 8);
        Assert.Equal((10, 5, 2), got);
    }

    [Fact]
    public void Тормоз_не_опускает_капы_ниже_двух()
    {
        // иначе после серии аварий джоба замолчала бы навсегда
        var got = OnlineWarm.NextCaps((2, 2, 2), Max, done: 10, empty: 10);
        Assert.Equal((2, 2, 2), got);
    }

    [Fact]
    public void Удачный_прогон_возвращает_капы_по_единице()
    {
        var got = OnlineWarm.NextCaps((10, 5, 2), Max, done: 10, empty: 0);
        Assert.Equal((11, 6, 3), got);
    }

    [Fact]
    public void Восстановление_не_превышает_настроенный_максимум()
    {
        var got = OnlineWarm.NextCaps(Max, Max, done: 10, empty: 0);
        Assert.Equal(Max, got);
    }

    [Fact]
    public void Серая_зона_капы_не_трогает()
    {
        // 20–50% пустых — балансеры частично лежат, но это не повод ни тормозить, ни разгоняться
        var got = OnlineWarm.NextCaps((10, 5, 3), Max, done: 10, empty: 3);
        Assert.Equal((10, 5, 3), got);
    }

    [Fact]
    public void Пустой_прогон_капы_не_меняет()
        => Assert.Equal((10, 5, 3), OnlineWarm.NextCaps((10, 5, 3), Max, done: 0, empty: 0));

    // ── ключ карточки ──

    [Fact]
    public void Ключ_различает_фильм_и_сериал()
        => Assert.NotEqual(OnlineWarm.CardKey(550, tv: false), OnlineWarm.CardKey(550, tv: true));

    [Fact]
    public void Ключ_стабилен()
        => Assert.Equal("movie|550", OnlineWarm.CardKey(550, tv: false));
}
