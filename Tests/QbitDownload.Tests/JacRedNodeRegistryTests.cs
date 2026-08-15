using System;
using System.Linq;
using System.Net;
using JacRed.Engine;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Реестр узлов-помощников (Modules/JacRed/Engine/NodeRegistry.cs).
/// Сети не нужно: часы подменяются делегатом, адрес узла передаётся строкой.
/// ⚠️ _nodes и Now — процессные статики: сбрасываем на каждый тест, иначе состояние течёт.
/// </summary>
public class JacRedNodeRegistryTests : IDisposable
{
    const int TTL = 90;
    const int MAX = 32;

    DateTime _now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public JacRedNodeRegistryTests()
    {
        NodeRegistry.Reset();
        NodeRegistry.Now = () => _now;
    }

    public void Dispose() => NodeRegistry.Reset();

    // ── Главный инвариант ─────────────────────────────────────────────────────

    [Fact]
    public void Пустой_реестр_не_даёт_прокси__дом_ходит_как_раньше()
    {
        // Именно null, а не «какой-нибудь» прокси: вызывающий обязан упасть на штатный путь.
        Assert.Null(NodeRegistry.ProxyOrNull(TTL));
    }

    [Fact]
    public void Узел_замолчал__выпадает_по_TTL()
    {
        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);
        Assert.NotNull(NodeRegistry.ProxyOrNull(TTL));

        _now = _now.AddSeconds(TTL + 1);

        Assert.Null(NodeRegistry.ProxyOrNull(TTL));
    }

    [Fact]
    public void Узел_снова_застучал__возвращается_в_строй()
    {
        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);
        _now = _now.AddSeconds(TTL + 1);
        Assert.Null(NodeRegistry.ProxyOrNull(TTL));

        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);

        Assert.NotNull(NodeRegistry.ProxyOrNull(TTL));
    }

    [Fact]
    public void TTL_меньше_периода_удара_клампится__живой_узел_не_выкидывается()
    {
        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);

        _now = _now.AddSeconds(25);

        // ttlSeconds:1 клампится снизу до 30 — иначе узел выпадал бы между ударами агента (30с)
        Assert.NotNull(NodeRegistry.ProxyOrNull(1));
    }

    // ── Адрес выхода ──────────────────────────────────────────────────────────

    [Fact]
    public void Прокси_собирается_из_адреса_источника_и_объявленного_порта()
    {
        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);

        var proxy = NodeRegistry.ProxyOrNull(TTL)();

        Assert.Equal("http://192.168.87.50:9121/", proxy.Address.ToString());
    }

    [Fact]
    public void Узел_только_с_солвером_выходом_не_считается()
    {
        NodeRegistry.Hello("solveronly", "192.168.87.51", 0, 9120, MAX);

        Assert.Null(NodeRegistry.ProxyOrNull(TTL));
        Assert.Equal(new[] { "http://192.168.87.51:9120" }, NodeRegistry.Solvers(TTL));
    }

    [Fact]
    public void Мёртвый_узел_не_попадает_в_список_солверов()
    {
        NodeRegistry.Hello("s", "192.168.87.51", 0, 9120, MAX);
        _now = _now.AddSeconds(TTL + 1);

        Assert.Empty(NodeRegistry.Solvers(TTL));
    }

    [Fact]
    public void Несколько_выходов__выбор_по_живым()
    {
        NodeRegistry.Hello("a", "192.168.87.50", 9121, 0, MAX);
        NodeRegistry.Hello("b", "192.168.87.51", 9121, 0, MAX);

        var pick = NodeRegistry.ProxyOrNull(TTL);
        var seen = Enumerable.Range(0, 50).Select(_ => pick().Address.Host).ToHashSet();

        Assert.Subset(new[] { "192.168.87.50", "192.168.87.51" }.ToHashSet(), seen);
    }

    // ── Приём объявления ──────────────────────────────────────────────────────

    [Fact]
    public void Повторное_объявление_обновляет_узел__а_не_плодит_записи()
    {
        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);
        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);

        Assert.Single(NodeRegistry.Snapshot(TTL, maskIp: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("плохое имя с пробелами")]
    [InlineData("../../etc/passwd")]
    public void Мусор_в_имени_отвергается(string name)
    {
        Assert.False(NodeRegistry.Hello(name, "192.168.87.50", 9121, 0, MAX));
    }

    [Fact]
    public void Узел_без_единой_роли_отвергается()
    {
        Assert.False(NodeRegistry.Hello("node1", "192.168.87.50", 0, 0, MAX));
    }

    [Fact]
    public void Реестр_не_растёт_без_границ__но_свои_узлы_обновляются()
    {
        for (int i = 0; i < 3; i++)
            Assert.True(NodeRegistry.Hello("n" + i, "192.168.87.5" + i, 9121, 0, maxNodes: 3));

        // новый сверх капа — отказ
        Assert.False(NodeRegistry.Hello("n99", "192.168.87.99", 9121, 0, maxNodes: 3));

        // уже известный — проходит, иначе живой узел «выпал бы» из-за переполнения
        Assert.True(NodeRegistry.Hello("n1", "192.168.87.51", 9121, 0, maxNodes: 3));
    }

    [Fact]
    public void Снятый_с_эксплуатации_узел_освобождает_место_в_капе()
    {
        Assert.True(NodeRegistry.Hello("retired", "192.168.87.50", 9121, 0, maxNodes: 1));

        // ещё «мёртвый, но не забытый» — место занято, диагностика его показывает
        _now = _now.AddSeconds(TTL + 1);
        Assert.False(NodeRegistry.Hello("fresh", "192.168.87.51", 9121, 0, maxNodes: 1));
        Assert.Single(NodeRegistry.Snapshot(TTL, maskIp: false));

        // час молчания — забыт совсем, место освободилось
        _now = _now.AddSeconds(NodeRegistry.ForgetAfterSeconds);
        Assert.True(NodeRegistry.Hello("fresh", "192.168.87.51", 9121, 0, maxNodes: 1));

        string json = System.Text.Json.JsonSerializer.Serialize(NodeRegistry.Snapshot(TTL, maskIp: false));
        Assert.Single(NodeRegistry.Snapshot(TTL, maskIp: false));
        Assert.Contains("fresh", json);
        Assert.DoesNotContain("retired", json);
    }

    [Fact]
    public void Узел_убирается_из_реестра_руками()
    {
        NodeRegistry.Hello("keep", "192.168.87.50", 9121, 0, MAX);
        NodeRegistry.Hello("drop", "192.168.87.51", 9121, 0, MAX);

        Assert.Equal(1, NodeRegistry.Forget("drop"));
        Assert.Equal(0, NodeRegistry.Forget("drop"));      // повторно — нечего убирать
        Assert.Equal(0, NodeRegistry.Forget(""));

        string json = System.Text.Json.JsonSerializer.Serialize(NodeRegistry.Snapshot(TTL, maskIp: false));
        Assert.Contains("keep", json);
        Assert.DoesNotContain("drop", json);
    }

    [Fact]
    public void Неживые_записи_убираются_разом__живые_остаются()
    {
        NodeRegistry.Hello("old", "192.168.87.50", 9121, 0, MAX);
        _now = _now.AddSeconds(TTL + 1);
        NodeRegistry.Hello("fresh", "192.168.87.51", 9121, 0, MAX);

        Assert.Equal(1, NodeRegistry.ForgetDead(TTL));

        var items = NodeRegistry.Snapshot(TTL, maskIp: false);
        Assert.Single(items);
        Assert.Contains("fresh", System.Text.Json.JsonSerializer.Serialize(items));
    }

    // ── Кого пускаем ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.8.0.2")]
    [InlineData("172.16.0.9")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.87.50")]
    [InlineData("127.0.0.1")]
    public void Приватные_адреса_принимаются(string ip)
    {
        Assert.True(NodeRegistry.IsPrivate(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("185.226.112.34")]   // наш собственный внешний адрес — тоже «снаружи»
    [InlineData("172.32.0.1")]       // сразу за границей 172.16/12
    [InlineData("172.15.255.255")]
    public void Публичные_адреса_отвергаются(string ip)
    {
        Assert.False(NodeRegistry.IsPrivate(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IPv4_завёрнутый_в_IPv6_разворачивается()
    {
        // Kestrel часто отдаёт RemoteIpAddress именно так — без разворота свой узел
        // не прошёл бы проверку «приватный ли адрес».
        Assert.True(NodeRegistry.IsPrivate(IPAddress.Parse("::ffff:192.168.87.50")));
        Assert.False(NodeRegistry.IsPrivate(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    // ── Диагностика ───────────────────────────────────────────────────────────

    [Fact]
    public void Снаружи_адреса_узлов_маскируются()
    {
        NodeRegistry.Hello("node1", "192.168.87.50", 9121, 0, MAX);

        string open = System.Text.Json.JsonSerializer.Serialize(NodeRegistry.Snapshot(TTL, maskIp: false));
        string masked = System.Text.Json.JsonSerializer.Serialize(NodeRegistry.Snapshot(TTL, maskIp: true));

        Assert.Contains("192.168.87.50", open);
        Assert.DoesNotContain("192.168.87.50", masked);
        Assert.Contains("192.168.87.x", masked);
    }
}
