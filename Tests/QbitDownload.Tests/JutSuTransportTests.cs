using QbitDownload;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace QbitDownload.Tests;

// Транспорт jut.su без сети: самоописывающий токен, политика прокси-фолбэка, идентичность.
// Живые проверки (реальные запросы к сайту) — через /qdl/jut/diag, см. claude/jut/03-runbook.md
public class JutSuTransportTests
{
    #region самоописывающий токен

    // 🔥 Смысл самоописывающего токена: пережить рестарт контейнера. Раньше маппинг
    // token→(slug,ep,quality) жил только в in-proc словаре, и рестарт (частый и дорогой из-за
    // Roslyn-компиляции) убивал активный двухчасовой просмотр — плеер слал Range по мёртвому
    // токену и получал 404 без единого шанса на восстановление.

    [Fact]
    public void Токен_восстанавливается_обратно()
    {
        string t = JutNet.MakeToken("spy-family", 2, 7, "episode", 1080);
        var back = JutNet.ParseToken(t);

        Assert.NotNull(back);
        Assert.Equal("spy-family", back.slug);
        Assert.Equal(2, back.season);
        Assert.Equal(7, back.ep);
        Assert.Equal("episode", back.kind);
        Assert.Equal(1080, back.quality);
    }

    [Fact]
    public void Токен_стабилен_для_одних_и_тех_же_параметров()
    {
        // Клиентский URL /qdl/jut/stream?t=... не должен меняться при перевыпуске ссылки:
        // нативный плеер URL на лету не переоткрывает.
        Assert.Equal(JutNet.MakeToken("x", 1, 1, "episode", 0),
                     JutNet.MakeToken("x", 1, 1, "episode", 0));
        Assert.NotEqual(JutNet.MakeToken("x", 1, 1, "episode", 0),
                        JutNet.MakeToken("x", 1, 2, "episode", 0));
    }

    [Fact]
    public void Подделанный_токен_отбивается()
    {
        // Без подписи токен превращался бы в открытый прокси на произвольный внешний URL
        string t = JutNet.MakeToken("spy-family", 1, 1, "episode", 0);
        string[] parts = t.Split('.');

        Assert.Null(JutNet.ParseToken(parts[0] + "." + parts[1] + ".AAAAAAAAAAAA"));  // чужая подпись
        Assert.Null(JutNet.ParseToken("v1.ZXZpbA.AAAAAAAAAAAA"));                     // чужой payload
        Assert.Null(JutNet.ParseToken(""));
        Assert.Null(JutNet.ParseToken(null));
        Assert.Null(JutNet.ParseToken("../../etc/passwd"));
    }

    [Fact]
    public void Токен_с_traversal_в_слаге_отбивается()
    {
        // Даже с валидной подписью slug обязан пройти IsValidSlug: он идёт в пути на диске
        Assert.Null(JutNet.ParseToken(JutNet.MakeToken("../../etc", 1, 1, "episode", 0)));
    }

    [Theory]
    [InlineData("film")]
    [InlineData("ova")]
    [InlineData("game-ova")]
    public void Токен_переживает_фильмы_и_ova(string kind)
    {
        var back = JutNet.ParseToken(JutNet.MakeToken("naruuto", 1, 3, kind, 0));
        Assert.NotNull(back);
        Assert.Equal(kind, back.kind);
        Assert.Equal(3, back.ep);
    }

    #endregion

    #region политика прокси-фолбэка

    // Шесть инвариантов скопированы из JacRed/ProxyFallback дословно; копия, а не переиспользование,
    // потому что CSharpEval не показывает модулю типы других модулей.

    static WebProxy P => new WebProxy("http://127.0.0.1:1080");
    sealed class R { public bool good; }
    static Task<R> Ret(bool good) => Task.FromResult(new R { good = good });

    static async Task<R> Run(string key, Func<WebProxy> getProxy, Func<WebProxy, Task<R>> send,
                             bool enabled = true, int cd = 300)
        => await JutProxyFallback.Run(key, getProxy, send, x => x != null && x.good, enabled, cd);

    [Fact]
    public async Task Выключенный_фолбэк_шлёт_один_раз()
    {
        JutProxyFallback.Reset();
        int calls = 0;
        await Run("k1", () => P, _ => { calls++; return Ret(false); }, enabled: false);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Без_прокси_ретрай_недостижим()
    {
        // ⚠️ Инвариант 2: второй запрос был бы побайтовой копией первого
        JutProxyFallback.Reset();
        int calls = 0;
        await Run("k2", () => null, _ => { calls++; return Ret(false); });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Прокси_мёртв_прямой_жив_ровно_один_ретрай()
    {
        JutProxyFallback.Reset();
        int calls = 0;
        var r = await Run("k3", () => P, p => { calls++; return Ret(p == null); });
        Assert.Equal(2, calls);          // ровно один ретрай, циклов нет
        Assert.True(r.good);
    }

    [Fact]
    public async Task После_удачного_direct_следующий_запрос_идёт_мимо_прокси()
    {
        JutProxyFallback.Reset();
        await Run("k4", () => P, p => Ret(p == null));

        bool usedProxy = false;
        await Run("k4", () => P, p => { if (p != null) usedProxy = true; return Ret(p == null); });
        Assert.False(usedProxy);
    }

    [Fact]
    public async Task Оба_мертвы_значит_лежит_сайт_ретрай_глушится()
    {
        // ⚠️ Инвариант 3: иначе каждый запрос платил бы двойным таймаутом за лежащий сайт
        JutProxyFallback.Reset();
        await Run("k5", () => P, _ => Ret(false));

        int calls = 0;
        await Run("k5", () => P, _ => { calls++; return Ret(false); });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Кулдаун_клампится_снизу_до_30_секунд()
    {
        var fixedNow = new DateTime(2026, 8, 10, 12, 0, 0);
        JutProxyFallback.Reset();
        JutProxyFallback.Now = () => fixedNow;
        try
        {
            await Run("k6", () => P, p => Ret(p == null), cd: 0);   // 0 удвоил бы каждый запрос
            var (mode, until) = JutProxyFallback.State("k6");
            Assert.Equal("Direct", mode);
            Assert.True(until >= fixedNow.AddSeconds(30));
        }
        finally { JutProxyFallback.Now = () => DateTime.Now; }
    }

    [Fact]
    public async Task Липкий_вердикт_истекает_и_прокси_пробуется_снова()
    {
        var t = new DateTime(2026, 8, 10, 12, 0, 0);
        JutProxyFallback.Reset();
        JutProxyFallback.Now = () => t;
        try
        {
            await Run("k7", () => P, p => Ret(p == null), cd: 60);
            Assert.Equal("Direct", JutProxyFallback.State("k7").mode);

            t = t.AddSeconds(61);
            bool triedProxy = false;
            await Run("k7", () => P, p => { if (p != null) triedProxy = true; return Ret(true); });
            Assert.True(triedProxy);
        }
        finally { JutProxyFallback.Now = () => DateTime.Now; }
    }

    [Fact]
    public async Task Выключение_на_лету_снимает_липкий_вердикт()
    {
        JutProxyFallback.Reset();
        await Run("k8", () => P, p => Ret(p == null));
        Assert.Equal("Direct", JutProxyFallback.State("k8").mode);

        await Run("k8", () => P, _ => Ret(true), enabled: false);
        Assert.Equal("none", JutProxyFallback.State("k8").mode);
    }

    [Fact]
    public async Task Заглушка_не_считается_успехом()
    {
        // §BB.3: сайт/провайдер могут отдать 200 с капчей — предикат ok обязан это ловить,
        // иначе мусор попадёт в выдачу как валидный ответ
        JutProxyFallback.Reset();
        int calls = 0;
        await JutProxyFallback.Run<string>("k9", () => P,
            _ => { calls++; return Task.FromResult("<html>captcha</html>"); },
            JutSuParse.ReachedCatalog, true, 300);
        Assert.Equal(2, calls);   // не дошли → был ретрай
    }

    #endregion

    #region идентичность и изоляция

    [Fact]
    public void Псевдо_хеш_детерминирован_и_проходит_ValidHash()
    {
        string h = JutNet.Hash("spy-family");
        Assert.Equal(40, h.Length);
        Assert.Matches("^[0-9a-f]{40}$", h);
        Assert.Equal(h, JutNet.Hash("spy-family"));
        Assert.NotEqual(h, JutNet.Hash("oneepiece"));
    }

    [Fact]
    public void Псевдо_хеш_принимается_разделом_Загрузки()
    {
        // /qdl/list строит карточку из local/<hash>.json при единственном условии ValidHash —
        // на этом держится «скачанное аниме = обычная локальная карточка».
        var m = typeof(QbitController).GetMethod("ValidHash",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
        Assert.True((bool)m.Invoke(null, new object[] { JutNet.Hash("spy-family") }));
    }

    [Fact]
    public void UA_по_умолчанию_непустой()
    {
        // Рассинхрон UA между страницей и CDN = 403 на всём видео, поэтому константа обязана быть
        Assert.False(string.IsNullOrWhiteSpace(JutNet.Ua));
        Assert.Contains("Mozilla/5.0", JutNet.Ua);
    }

    #endregion
}
