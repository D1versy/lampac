using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using JacRed.Engine;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Фолбэк «прокси не ответил → один прямой запрос» (Modules/JacRed/Engine/ProxyFallback.cs).
/// Сети не нужно: Run() принимает транспорт и часы делегатами.
/// ⚠️ _state и Now — процессные статики. Параллелизм в сборке уже отключён (TestEnv.cs), но
/// состояние всё равно обязано сбрасываться на каждый тест — иначе вердикт течёт между ними.
/// </summary>
public class JacRedProxyFallbackTests : IDisposable
{
    static readonly WebProxy P = new WebProxy("http://10.0.0.9:3128");

    const string GOOD = "<html><div id=\"logo\">tracker</div></html>";
    const string CAPTIVE = "<html>ISP block page, 200 OK</html>";   // 200, но не тот сайт

    readonly List<WebProxy> _sent = new();
    readonly List<(string plugin, string reason)> _log = new();
    DateTime _now = new DateTime(2026, 1, 1, 12, 0, 0);

    public JacRedProxyFallbackTests()
    {
        ProxyFallback.Reset();
        ProxyFallback.Now = () => _now;
    }

    public void Dispose()
    {
        ProxyFallback.Reset();
        ProxyFallback.Now = () => DateTime.Now;
    }

    Task<string> Run(Func<WebProxy> getProxy, Func<WebProxy, string> reply,
                     bool enabled = true, int cooldown = 300, string plugin = "rutor")
        => ProxyFallback.Run(plugin, getProxy,
                             p => { _sent.Add(p); return Task.FromResult(reply(p)); },
                             h => h != null && h.Contains("id=\"logo\""),
                             enabled, cooldown,
                             (pl, r) => _log.Add((pl, r)));

    static Func<WebProxy> Proxy() => () => P;
    static Func<WebProxy> NoProxy() => () => null;

    // Инвариант: выключатель из init.conf отключает ветку целиком.
    [Fact]
    public async Task Disabled_sends_once_through_proxy()
    {
        string r = await Run(Proxy(), _ => null, enabled: false);

        Assert.Null(r);
        Assert.Equal(new WebProxy[] { P }, _sent);
        Assert.Empty(_log);
    }

    // Инвариант 2 (главный): при useproxy:false ретрай физически недостижим — второй запрос
    // был бы побайтовой копией первого. Это гарантия нулевого изменения поведения.
    [Fact]
    public async Task No_proxy_makes_retry_unreachable_and_writes_no_state()
    {
        await Run(NoProxy(), _ => null);
        await Run(NoProxy(), _ => null);

        Assert.Equal(2, _sent.Count);           // по одному на вызов, не по два
        Assert.All(_sent, Assert.Null);
        Assert.Empty(_log);
    }

    // Форма отказа (а): прокси вернул null.
    [Fact]
    public async Task Proxy_null_then_direct_ok_exactly_one_retry()
    {
        string r = await Run(Proxy(), p => p != null ? null : GOOD);

        Assert.Equal(GOOD, r);
        Assert.Equal(new WebProxy[] { P, null }, _sent);
        Assert.Single(_log);
        Assert.Equal("rutor", _log[0].plugin);
        Assert.Contains("прямой путь ok", _log[0].reason);
    }

    // Форма отказа (б): 200 OK, но это каптив-портал, а не трекер. Доказывает, что работает
    // не только null-ветка — иначе ISP-заглушка молча уехала бы в парсер.
    [Fact]
    public async Task Captive_portal_page_triggers_retry()
    {
        string r = await Run(Proxy(), p => p != null ? CAPTIVE : GOOD);

        Assert.Equal(GOOD, r);
        Assert.Equal(new WebProxy[] { P, null }, _sent);
        Assert.Single(_log);
    }

    // Инвариант 4: удачный прямой путь запоминается — следующий поиск идёт сразу мимо прокси.
    [Fact]
    public async Task After_direct_ok_next_call_goes_direct_first()
    {
        await Run(Proxy(), p => p != null ? null : GOOD);
        string r = await Run(Proxy(), p => p != null ? null : GOOD);

        Assert.Equal(GOOD, r);
        Assert.Equal(3, _sent.Count);           // 2 в первом вызове + 1 во втором
        Assert.Null(_sent[2]);
        Assert.Single(_log);                    // второй раз молчим
    }

    [Fact]
    public async Task Sticky_verdict_expires_and_proxy_is_probed_again()
    {
        await Run(Proxy(), p => p != null ? null : GOOD);
        _now = _now.AddSeconds(301);

        await Run(Proxy(), p => p != null ? null : GOOD);

        Assert.Equal(P, _sent[2]);              // снова начали с прокси
    }

    // Инвариант 3: мертвы оба — лежит трекер, ретрай глушим (иначе двойной таймаут на каждый поиск).
    [Fact]
    public async Task Both_dead_suppresses_retry_for_cooldown()
    {
        await Run(Proxy(), _ => null);
        Assert.Equal(new WebProxy[] { P, null }, _sent);
        Assert.Contains("ретрай выключен", _log[0].reason);

        _sent.Clear();
        await Run(Proxy(), _ => null);

        Assert.Equal(new WebProxy[] { P }, _sent);   // ровно один запрос
        Assert.Single(_log);
    }

    [Fact]
    public async Task Sticky_direct_that_dies_resets_to_proxy_first()
    {
        await Run(Proxy(), p => p != null ? null : GOOD);   // вошли в Direct

        _sent.Clear();
        await Run(Proxy(), _ => null);                      // и прямой сдох
        Assert.Equal(new WebProxy[] { null }, _sent);

        _sent.Clear();
        await Run(Proxy(), p => p != null ? GOOD : null);
        Assert.Equal(new WebProxy[] { P }, _sent);          // снова через прокси
    }

    // Инвариант 5: cooldown:0 превратил бы каждый поиск в двойной.
    [Fact]
    public async Task Cooldown_is_clamped_to_thirty_seconds()
    {
        await Run(Proxy(), p => p != null ? null : GOOD, cooldown: 0);

        _now = _now.AddSeconds(29);
        _sent.Clear();
        await Run(Proxy(), p => p != null ? null : GOOD, cooldown: 0);

        Assert.Equal(new WebProxy[] { null }, _sent);       // вердикт ещё жив
    }

    [Fact]
    public async Task Disabling_on_the_fly_clears_sticky_state()
    {
        await Run(Proxy(), p => p != null ? null : GOOD);   // вошли в Direct

        _sent.Clear();
        await Run(Proxy(), _ => GOOD, enabled: false);
        Assert.Equal(new WebProxy[] { P }, _sent);          // выключатель вернул прокси

        _sent.Clear();
        await Run(Proxy(), p => p != null ? GOOD : null);
        Assert.Equal(new WebProxy[] { P }, _sent);          // и состояние действительно стёрлось
    }

    // Вердикты живут по трекерам независимо: дохлый прокси у одного не тащит за собой другого.
    [Fact]
    public async Task Verdict_is_per_plugin()
    {
        await Run(Proxy(), p => p != null ? null : GOOD, plugin: "rutor");

        _sent.Clear();
        await Run(Proxy(), p => p != null ? GOOD : null, plugin: "kinozal");

        Assert.Equal(new WebProxy[] { P }, _sent);
    }
}
