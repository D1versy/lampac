using Shared.Models.Events;
using System.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Хосты, которым проксируемый поток уходит по HTTP/2 (<c>ProxyHttp2.cs</c>, qdl 2.106).
/// Cloudflare у pornhub отвечает 410 на HTTP/1.1, поэтому промах по списку = «Клубничка»
/// снова не играет; лишнее попадание, наоборот, меняет транспорт чужому источнику.
/// Сам подъём версии проверяется вживую после деплоя (curl по /proxy/ на phub).
/// </summary>
public class ProxyHttp2Tests
{
    [Theory]
    [InlineData("phncdn.com")]      // сам суффикс
    [InlineData("hv-h.phncdn.com")] // площадка с ключами h/e
    [InlineData("ev-h.phncdn.com")] // площадка с validfrom/validto — pornhub отдаёт то одну, то другую
    [InlineData("EV-H.PHNCDN.COM")] // хост из Uri.Host приходит как есть, регистр не наш
    [InlineData("di.phncdn.com")]   // превью и картинки
    public void Match_Phncdn_True(string host)
        => Assert.True(ProxyHttp2.Match(host, ProxyHttp2.defaultHosts));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("phncdn.com.evil.net")]  // суффикс в СЕРЕДИНЕ — не наш хост
    [InlineData("evilphncdn.com")]       // без точки перед суффиксом Contains дал бы ложное «да»
    [InlineData("xhcdn.com")]
    [InlineData("192.168.87.24")]
    public void Match_Others_False(string host)
        => Assert.False(ProxyHttp2.Match(host, ProxyHttp2.defaultHosts));

    [Fact]
    public void Match_EmptyList_False()
    {
        Assert.False(ProxyHttp2.Match("hv-h.phncdn.com", System.Array.Empty<string>()));
        Assert.False(ProxyHttp2.Match("hv-h.phncdn.com", null));
    }

    [Fact]
    public void Match_ListFromConf_TrimsAndIgnoresBlanks()
    {
        string[] hosts = ["", "  ", " .xhcdn.com "];
        Assert.True(ProxyHttp2.Match("video-nss.xhcdn.com", hosts));
        Assert.False(ProxyHttp2.Match("hv-h.phncdn.com", hosts));
    }
}

/// <summary>
/// Политика ретрая (<c>ProxyHttp2.OnRetry</c> через <c>EventListener.ProxyApiRetry</c>):
/// повторяем только «Cloudflare отшил» и только на своих хостах, конечное число раз.
/// </summary>
public class ProxyRetryPolicyTests
{
    static int? Ask(string url, int status, int attempt)
    {
        ProxyHttp2.Attach();
        try { return EventListener.ProxyApiRetry(new EventProxyApiRetry(new System.Uri(url), status, attempt)); }
        finally { ProxyHttp2.Detach(); }
    }

    const string phub = "https://hv-h.phncdn.com/hls/x/master.m3u8";

    [Theory]
    [InlineData(410)]   // основной ответ вызова
    [InlineData(412)]   // «request incorrect», приходил вперемешку с 410
    [InlineData(429)]
    public void Retry_CloudflareStatuses(int status)
        => Assert.NotNull(Ask(phub, status, 0));

    [Theory]
    [InlineData(200)]
    [InlineData(206)]   // сегменты идут range-запросами — повтор успеха недопустим
    [InlineData(302)]
    [InlineData(404)]   // «ссылки нет» повтором не лечится
    [InlineData(500)]
    public void NoRetry_OtherStatuses(int status)
        => Assert.Null(Ask(phub, status, 0));

    [Fact]
    public void NoRetry_ForeignHost()
        => Assert.Null(Ask("https://video-nss.xhcdn.com/a/master.m3u8", 410, 0));

    [Fact]
    public void Retry_StopsAfterBudget()
    {
        for (int i = 0; i < ProxyHttp2.retryDelaysMs.Length; i++)
            Assert.NotNull(Ask(phub, 410, i));

        // 🔴 Без этой границы ProxyRetryHandler крутил бы попытки, пока держится соединение
        // клиента — то есть вешал бы плеер вместо того, чтобы показать ошибку.
        Assert.Null(Ask(phub, 410, ProxyHttp2.retryDelaysMs.Length));
        Assert.Null(Ask(phub, 410, 99));
    }

    [Fact]
    public void Retry_DelaysAreShort()
    {
        // Паузы нужны не «чтобы отпустило», а чтобы соединение уже стояло: сумма всех задержек
        // добавляется к старту видео, и секунды здесь были бы хуже самой ошибки.
        Assert.All(ProxyHttp2.retryDelaysMs, d => Assert.InRange(d, 0, 500));
        Assert.True(ProxyHttp2.retryDelaysMs.Sum() < 1000);
    }
}
