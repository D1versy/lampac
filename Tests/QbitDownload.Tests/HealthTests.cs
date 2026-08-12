using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты чистых частей хелс-чеков (Health.cs, <c>GET /qdl/health</c>): форма записи о сервисе
/// и раздел «Поиск раздач», который собирается БЕЗ сети — из состояния канареек SearchMonitor.
/// Живые пробы (HTTP/Postgres/qBittorrent) здесь не гоняем: их проверка — негативный прогон
/// на живом сервере (остановить сервис → в отчёте fail только у него).
/// </summary>
public class HealthTests
{
    static JObject State(params (string id, string state)[] checks)
    {
        var o = new JObject();
        foreach (var (id, state) in checks)
            o[id] = new JObject { ["state"] = state, ["streak"] = 0, ["incident"] = 0 };
        return new JObject { ["runs"] = new JArray(), ["checks"] = o };
    }

    static JObject Find(JArray arr, string id) => arr.OfType<JObject>().FirstOrDefault(x => x.Value<string>("id") == id);

    [Fact]
    public void Svc_HasStableShape()
    {
        var s = QbitController.Svc("qbit", "qBittorrent", "Инфраструктура", "ok", 12, "v5.0.0");

        Assert.Equal("qbit", s.Value<string>("id"));
        Assert.Equal("qBittorrent", s.Value<string>("name"));
        Assert.Equal("Инфраструктура", s.Value<string>("group"));
        Assert.Equal("ok", s.Value<string>("status"));
        Assert.Equal(12, s.Value<long>("ms"));
        Assert.Equal("v5.0.0", s.Value<string>("detail"));
    }

    [Fact]
    public void AddSearchChecks_MonitorDisabled_IsOffNotFail()
    {
        TestEnv.EnsureConf();
        int prev = ModInit.conf.searchMonitorIntervalMinutes;
        try
        {
            ModInit.conf.searchMonitorIntervalMinutes = 0;
            var arr = new JArray();
            QbitController.AddSearchChecks(arr, State(("indexer", "fail")), DateTime.UtcNow);

            // выключено ≠ отвалилось: даже с провальным состоянием на диске статус off
            Assert.Equal("off", Find(arr, "indexer").Value<string>("status"));
            Assert.Single(arr);
        }
        finally { ModInit.conf.searchMonitorIntervalMinutes = prev; }
    }

    [Fact]
    public void AddSearchChecks_NoRunsYet_IsOff()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchMonitorIntervalMinutes = 180;

        var arr = new JArray();
        QbitController.AddSearchChecks(arr, new JObject { ["runs"] = new JArray(), ["checks"] = new JObject() }, DateTime.UtcNow);

        var indexer = Find(arr, "indexer");
        Assert.Equal("off", indexer.Value<string>("status"));
        Assert.Contains("прогонов ещё не было", indexer.Value<string>("detail"));
    }

    [Fact]
    public void AddSearchChecks_MapsStatesAndTrackers()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchMonitorIntervalMinutes = 180;

        var st = State(("indexer", "ok"), ("tracker:rutor", "ok"), ("tracker:kinozal", "fail"), ("stars", "fail"));
        var now = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
        (st["runs"] as JArray).Add(new JObject { ["at"] = now.AddMinutes(-25), ["ok"] = true });

        var arr = new JArray();
        QbitController.AddSearchChecks(arr, st, now);

        Assert.Equal("ok", Find(arr, "indexer").Value<string>("status"));
        Assert.Equal("ok", Find(arr, "tracker:rutor").Value<string>("status"));
        Assert.Equal("fail", Find(arr, "tracker:kinozal").Value<string>("status"));
        Assert.Equal("fail", Find(arr, "stars").Value<string>("status"));

        // имя трекера без служебного префикса + возраст последнего прогона
        Assert.Equal("Трекер kinozal", Find(arr, "tracker:kinozal").Value<string>("name"));
        Assert.Contains("25 мин назад", Find(arr, "indexer").Value<string>("detail"));

        // канарейки (canary:*) — внутренняя кухня монитора, в отчёт не тащим
        Assert.All(arr.OfType<JObject>(), x => Assert.DoesNotContain("canary:", x.Value<string>("id")));
    }

    [Fact]
    public void AddSearchChecks_AllRowsAreInSearchGroup()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchMonitorIntervalMinutes = 180;

        var arr = new JArray();
        QbitController.AddSearchChecks(arr, State(("indexer", "ok"), ("tracker:rutor", "ok")), DateTime.UtcNow);

        Assert.All(arr.OfType<JObject>(), x => Assert.Equal("Поиск раздач", x.Value<string>("group")));
    }
}
