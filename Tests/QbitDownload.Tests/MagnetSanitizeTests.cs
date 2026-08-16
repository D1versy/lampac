using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Санитайз магнетов (QbitController.SanitizeMagnet).
///
/// Зачем: магнет — это не только btih. Дописанный `&amp;tr=` заставит qBittorrent объявлять внешний
/// IP дома, порт и infohash постороннему серверу на каждом анонсе, а `&amp;ws=`/`&amp;as=`/`&amp;xs=`/`&amp;x.pe=`
/// — ходить по чужому адресу прямо из домашней сети. При этом БОЛЬШИНСТВО магнетов приходит не от
/// нашего парсинга, а с чужого сервера (JacRed typesearch: "webapi").
///
/// ⚠️ ModInit.conf — процессный статик, и sanitizeMagnetTrackers читается из него. Ставим его в
/// каждом тесте явно, иначе вердикт течёт между тестами и порядком запуска.
/// </summary>
public class MagnetSanitizeTests
{
    const string HASH = "0123456789abcdef0123456789abcdef01234567";
    const string XT = "xt=urn:btih:" + HASH;

    static void Conf(bool dropTrackers)
        => ModInit.conf = new ModuleConf { sanitizeMagnetTrackers = dropTrackers };

    // ── Веб-сиды и адреса пиров режутся ВСЕГДА ────────────────────────────

    [Theory]
    [InlineData("ws=http://attacker/seed")]
    [InlineData("as=http://attacker/seed")]
    [InlineData("xs=http://attacker/meta")]
    [InlineData("x.pe=203.0.113.7:6881")]
    [InlineData("mt=http://attacker/manifest")]
    public void Dangerous_params_are_dropped_regardless_of_flag(string param)
    {
        foreach (bool drop in new[] { false, true })
        {
            Conf(drop);
            string clean = QbitController.SanitizeMagnet($"magnet:?{XT}&dn=Film&{param}");

            Assert.DoesNotContain(param.Split('=')[0] + "=", clean);
            Assert.Contains(XT, clean);
            Assert.Contains("dn=Film", clean);
        }
    }

    // ── tr — только по флагу ──────────────────────────────────────────────

    [Fact]
    public void Trackers_are_kept_by_default()
    {
        Conf(false);
        string src = $"magnet:?{XT}&dn=Film&tr=udp%3A%2F%2Fopentor.org%3A2710";

        // Приватные раздачи без tr не скачают даже метаданные — DHT у них отключён флагом
        // внутри торрента. Поэтому по умолчанию анонсы остаются.
        Assert.Equal(src, QbitController.SanitizeMagnet(src));
    }

    [Fact]
    public void Trackers_are_dropped_when_enabled()
    {
        Conf(true);
        string clean = QbitController.SanitizeMagnet(
            $"magnet:?{XT}&dn=Film&tr=udp%3A%2F%2Fopentor.org%3A2710&tr.1=http%3A%2F%2Fzlo%2Fannounce");

        Assert.DoesNotContain("tr=", clean);
        Assert.DoesNotContain("tr.1=", clean);
        Assert.Contains(XT, clean);
    }

    // ── Что обязано выжить ────────────────────────────────────────────────

    [Fact]
    public void Hash_and_name_survive()
    {
        Conf(true);
        string clean = QbitController.SanitizeMagnet($"magnet:?{XT}&dn=%D0%94%D1%8E%D0%BD%D0%B0&ws=http://zlo/");

        Assert.Contains(XT, clean);
        Assert.Contains("dn=%D0%94%D1%8E%D0%BD%D0%B0", clean);
    }

    /// <summary>Множественный хеш (xt.1/xt.2) — законная форма, резать нельзя.</summary>
    [Fact]
    public void Multiple_xt_survive()
    {
        Conf(true);
        string src = $"magnet:?xt.1=urn:btih:{HASH}&xt.2=urn:ed2k:abc&dn=Film";

        Assert.Equal(src, QbitController.SanitizeMagnet(src));
    }

    /// <summary>
    /// Ключевой инвариант совместимости: btih не меняется, значит дедуп по MagnetHash,
    /// dedupe_key в LocalIndex и все гейты донора продолжают сходиться со старыми записями.
    /// </summary>
    [Fact]
    public void Hash_is_unchanged_so_dedupe_still_matches()
    {
        Conf(true);
        string dirty = $"magnet:?{XT}&dn=Film&tr=http%3A%2F%2Fzlo%2Fannounce&ws=http://zlo/";

        Assert.Equal(Access.MagnetHash(dirty), Access.MagnetHash(QbitController.SanitizeMagnet(dirty)));
    }

    // ── Не трогаем то, что трогать нельзя ─────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.org/file.torrent")]   // не магнет — путь через .torrent
    [InlineData("magnet:")]                            // без query
    public void Non_magnets_pass_through(string input)
    {
        Conf(true);
        Assert.Equal(input, QbitController.SanitizeMagnet(input));
    }

    /// <summary>Чистый магнет пересобирать незачем — возвращаем ту же строку.</summary>
    [Fact]
    public void Already_clean_magnet_is_untouched()
    {
        Conf(true);
        string src = $"magnet:?{XT}&dn=Film";

        Assert.Equal(src, QbitController.SanitizeMagnet(src));
    }

    /// <summary>
    /// Если после чистки не осталось ничего, отдаём исходную строку: пусть её отбракует
    /// MagnetHash или сам qBittorrent, а глотать молча нельзя.
    /// </summary>
    [Fact]
    public void Magnet_without_safe_params_is_returned_as_is()
    {
        Conf(true);
        string src = "magnet:?ws=http://zlo/&as=http://zlo/";

        Assert.Equal(src, QbitController.SanitizeMagnet(src));
    }
}
