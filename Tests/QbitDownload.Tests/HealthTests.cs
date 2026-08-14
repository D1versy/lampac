using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты чистых частей хелс-чеков (Health.cs + HealthState.cs, <c>GET /qdl/health</c>).
///
/// С qdl 2.44 модель пассивная: внешние сервисы наблюдаются по исходу РЕАЛЬНЫХ обращений,
/// а не пробами ради экрана. Поэтому здесь покрыты вердикт (липкий сбой, флап, деградация),
/// раздел «Поиск раздач» (сырой streak вместо пост-машинного state) и персист реестра.
/// Живые пробы своих контейнеров (HTTP/Postgres/qBittorrent) не гоняем: их проверка —
/// негативный прогон на живом сервере (остановить сервис → в отчёте fail только у него).
/// </summary>
public class HealthTests
{
    static readonly DateTime T0 = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Состояние канареек. streak — сырой вердикт последнего прогона, state — машина уведомлений.</summary>
    static JObject State(params (string id, string state, int streak)[] checks)
    {
        var o = new JObject();
        foreach (var (id, state, streak) in checks)
            o[id] = new JObject { ["state"] = state, ["streak"] = streak, ["incident"] = 0 };
        return new JObject { ["runs"] = new JArray(), ["checks"] = o };
    }

    static JObject StateWithRun(DateTime at, params (string id, string state, int streak)[] checks)
    {
        var st = State(checks);
        (st["runs"] as JArray).Add(new JObject { ["at"] = at, ["ok"] = true });
        return st;
    }

    static JObject Find(JArray arr, string id) => arr.OfType<JObject>().FirstOrDefault(x => x.Value<string>("id") == id);

    static HealthState.Snap Snap(string id, DateTime now, int flap = 60) => HealthState.Get(id, now, flap);

    #region форма записи
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
        Assert.Null(s["quiet"]);   // по умолчанию строка попадает в сводку «Проблемы»
    }

    [Fact]
    public void Svc_QuietFlag_IsEmittedOnlyWhenSet()
    {
        var q = QbitController.Svc("x", "X", "Инфраструктура", "warn", 0, "d", quiet: true);
        Assert.True(q.Value<bool>("quiet"));
    }
    #endregion

    #region вердикт — чистая функция (снапшот собираем явно: тест не должен зависеть от часов)
    [Fact]
    public void Verdict_NoObservations_IsOffNotOk()
    {
        var (status, detail) = HealthState.Verdict(new HealthState.Snap(), T0);

        Assert.Equal("off", status);
        Assert.Contains("нет данных", detail);
    }

    /// <summary>
    /// Ключевое правило владельца: сбой висит красным, пока сервис не отработает успешно.
    /// Возраст обязан быть в подписи — по нему видно, что сбой ночной, а не сейчас.
    /// </summary>
    [Fact]
    public void Verdict_FailIsSticky_WithAgeInDetail()
    {
        var s = new HealthState.Snap
        {
            known = true,
            lastFail = T0.AddHours(-6),
            lastFailText = "http 503",
            failStreak = 1
        };

        var (status, detail) = HealthState.Verdict(s, T0);
        Assert.Equal("fail", status);
        Assert.Contains("6 ч назад", detail);
        Assert.Contains("http 503", detail);
    }

    [Fact]
    public void Verdict_SuccessAfterFail_ClearsRed()
    {
        var s = new HealthState.Snap
        {
            known = true,
            lastFail = T0.AddHours(-6),
            lastFailText = "http 503",
            lastOk = T0.AddMinutes(-2)
        };

        Assert.Equal("ok", HealthState.Verdict(s, T0).status);
    }

    [Fact]
    public void Verdict_FailStreak_IsInDetailOnlyWhenRepeated()
    {
        var one = new HealthState.Snap { known = true, lastFail = T0, lastFailText = "x", failStreak = 1 };
        var many = new HealthState.Snap { known = true, lastFail = T0, lastFailText = "x", failStreak = 3 };

        Assert.DoesNotContain("подряд", HealthState.Verdict(one, T0).detail);
        Assert.Contains("3 ошибки подряд", HealthState.Verdict(many, T0).detail);
    }

    /// <summary>Работает сейчас, но в окне были ошибки — это ⚠️, а не ✅: сервис флапает.</summary>
    [Fact]
    public void Verdict_ErrorsInWindowButLastOk_IsWarn()
    {
        var s = new HealthState.Snap
        {
            known = true,
            lastOk = T0,
            lastFail = T0.AddMinutes(-12),
            lastFailText = "http 500",
            failsInWindow = 1
        };

        var (status, detail) = HealthState.Verdict(s, T0);
        Assert.Equal("warn", status);
        Assert.Contains("1 ошибка за час", detail);
        Assert.Contains("12 мин назад", detail);
    }

    [Fact]
    public void Verdict_CleanRun_IsOk()
    {
        var s = new HealthState.Snap { known = true, lastOk = T0.AddMinutes(-2) };

        var (status, detail) = HealthState.Verdict(s, T0);
        Assert.Equal("ok", status);
        Assert.Contains("без ошибок", detail);
    }

    [Fact]
    public void Verdict_Degraded_IsWarn()
    {
        var s = new HealthState.Snap
        {
            known = true,
            lastOk = T0,
            degradedAt = T0.AddMinutes(-1),
            degradedText = "через прокси-фолбэк"
        };

        var (status, detail) = HealthState.Verdict(s, T0);
        Assert.Equal("warn", status);
        Assert.Contains("прокси-фолбэк", detail);
    }

    [Fact]
    public void Verdict_FailBeatsDegraded()
    {
        var s = new HealthState.Snap
        {
            known = true,
            lastFail = T0,
            lastFailText = "лёг совсем",
            degradedAt = T0.AddMinutes(-5),
            degradedText = "запасной путь"
        };

        Assert.Equal("fail", HealthState.Verdict(s, T0).status);
    }
    #endregion

    #region реестр наблюдений (пишут боевые чокпоинты)
    [Fact]
    public void Registry_FailThenOk_FlipsVerdictAndKeepsFlapWindow()
    {
        HealthState.ResetForTests();
        var now = DateTime.UtcNow;

        HealthState.Fail("svc", "таймаут");
        Assert.Equal("fail", HealthState.Verdict(Snap("svc", now), now).status);

        HealthState.Ok("svc");
        var (status, detail) = HealthState.Verdict(Snap("svc", now), now);
        Assert.Equal("warn", status);                 // работает, но час назад падал
        Assert.Contains("1 ошибка", detail);
    }

    [Fact]
    public void Registry_Streak_ResetsOnSuccess()
    {
        HealthState.ResetForTests();
        var now = DateTime.UtcNow;

        HealthState.Fail("svc", "x"); HealthState.Fail("svc", "x"); HealthState.Fail("svc", "x");
        Assert.Equal(3, Snap("svc", now).failStreak);

        HealthState.Ok("svc");
        Assert.Equal(0, Snap("svc", now).failStreak);
    }

    [Fact]
    public void Registry_FlapWindow_ForgetsOldErrors()
    {
        HealthState.ResetForTests();
        HealthState.Fail("svc", "x");

        // окно 5 минут: час спустя ошибка в него уже не попадает (кольцо бакетов провернулось)
        var late = DateTime.UtcNow.AddHours(1);
        Assert.Equal(0, Snap("svc", late, 5).failsInWindow);
    }

    [Fact]
    public void Registry_Degraded_IsClearable()
    {
        HealthState.ResetForTests();
        var now = DateTime.UtcNow;

        HealthState.Ok("svc");
        HealthState.Degraded("svc", "через прокси-фолбэк");
        Assert.Equal("warn", HealthState.Verdict(Snap("svc", now), now).status);

        HealthState.ClearDegraded("svc");
        Assert.Equal("ok", HealthState.Verdict(Snap("svc", now), now).status);
    }

    /// <summary>OkDirect — успех основным путём: гасит и сбой, и деградацию.</summary>
    [Fact]
    public void Registry_OkDirect_ClearsDegradation()
    {
        HealthState.ResetForTests();
        var now = DateTime.UtcNow;

        HealthState.Degraded("svc", "запасной путь");
        HealthState.OkDirect("svc");

        Assert.Equal("ok", HealthState.Verdict(Snap("svc", now), now).status);
    }

    /// <summary>OkDirect — успех основным путём: гасит и сбой, и деградацию.</summary>
    [Fact]
    public void OkDirect_ClearsDegradation()
    {
        HealthState.ResetForTests();
        HealthState.Degraded("svc", "запасной путь");
        HealthState.OkDirect("svc");

        Assert.Equal("ok", HealthState.Verdict(Snap("svc", T0), T0).status);
    }

    [Fact]
    public void ShortErr_NeverLeaksExceptionMessage()
    {
        // message содержит хосты/порты и куски строк подключения — в отчёт уходит только тип
        var ex = new InvalidOperationException("host=192.168.87.24;password=secret");
        Assert.Equal("InvalidOperationException", HealthState.ShortErr(ex));
        Assert.Equal("таймаут", HealthState.ShortErr(new TaskCanceledException()));
    }

    [Fact]
    public void Ago_And_Plural_AreHumanReadable()
    {
        Assert.Equal("только что", HealthState.Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("5 мин назад", HealthState.Ago(TimeSpan.FromMinutes(5)));
        Assert.Equal("6 ч назад", HealthState.Ago(TimeSpan.FromHours(6)));
        Assert.Equal("2 дня назад", HealthState.Ago(TimeSpan.FromHours(50)));

        Assert.Equal("ошибка", HealthState.Plural(1, "ошибка", "ошибки", "ошибок"));
        Assert.Equal("ошибки", HealthState.Plural(3, "ошибка", "ошибки", "ошибок"));
        Assert.Equal("ошибок", HealthState.Plural(5, "ошибка", "ошибки", "ошибок"));
        Assert.Equal("ошибок", HealthState.Plural(11, "ошибка", "ошибки", "ошибок"));
        Assert.Equal("ошибка", HealthState.Plural(21, "ошибка", "ошибки", "ошибок"));
    }

    [Fact]
    public void Registry_ConcurrentWrites_AreNotLost()
    {
        HealthState.ResetForTests();
        var now = DateTime.UtcNow;
        Parallel.For(0, 8, _ => { for (int i = 0; i < 500; i++) HealthState.Fail("svc", "x"); });

        Assert.Equal(4000, Snap("svc", now).failTotal);
        Assert.Equal(4000, Snap("svc", now).failStreak);
    }
    #endregion

    #region персист (липкий сбой обязан пережить рестарт контейнера)
    [Fact]
    public void Persist_StickyFail_SurvivesRestart()
    {
        TestEnv.FreshCache();
        HealthState.ResetForTests();
        HealthState.Fail("svc", "куки протухли");
        HealthState.FlushIfDirty();

        HealthState.ResetForTests();                                  // «рестарт контейнера»
        Assert.Equal("off", HealthState.Verdict(Snap("svc", T0), T0).status);   // реестр пуст

        HealthState.Load();
        var (status, detail) = HealthState.Verdict(Snap("svc", T0), T0);
        Assert.Equal("fail", status);
        Assert.Contains("куки протухли", detail);
    }

    /// <summary>
    /// Окно флапа НЕ восстанавливается намеренно: после перерыва оно относилось бы
    /// к событиям, которых уже нет. Задокументированный размен.
    /// </summary>
    [Fact]
    public void Persist_DoesNotRestoreFlapWindow()
    {
        TestEnv.FreshCache();
        HealthState.ResetForTests();
        HealthState.Fail("svc", "x");
        HealthState.Ok("svc");
        HealthState.FlushIfDirty();

        HealthState.ResetForTests();
        HealthState.Load();

        Assert.Equal(0, Snap("svc", DateTime.UtcNow).failsInWindow);
        Assert.Equal("ok", HealthState.Verdict(Snap("svc", DateTime.UtcNow), DateTime.UtcNow).status);
    }

    [Fact]
    public void Persist_BrokenFile_DoesNotThrow()
    {
        string dir = TestEnv.FreshCache();
        File.WriteAllText(Path.Combine(dir, "health-state.json"), "{ это не json");

        HealthState.ResetForTests();
        var ex = Record.Exception(() => HealthState.Load());

        Assert.Null(ex);
        Assert.Equal("off", HealthState.Verdict(Snap("svc", T0), T0).status);
    }
    #endregion

    #region поиск раздач — сырой streak вместо пост-машинного state
    /// <summary>
    /// 🔥 Регресс ровно на баг, из-за которого экран переделывался: state держится «ok» до
    /// набора needStreak и внутри 12-часового кулдауна уведомлений, а прогон УЖЕ провалился.
    /// </summary>
    [Fact]
    public void SearchRowStatus_UsesRawStreak_NotNotificationState()
    {
        var c = new JObject { ["state"] = "ok", ["streak"] = 2, ["incident"] = 0 };
        Assert.Equal("fail", QbitController.SearchRowStatus(c).status);
    }

    /// <summary>Прежний StateOf был fail-open: отсутствующее поле трактовалось как «ok».</summary>
    [Fact]
    public void SearchRowStatus_MissingFields_IsNotFailOpen()
    {
        Assert.Equal("off", QbitController.SearchRowStatus(new JObject()).status);
        Assert.Equal("off", QbitController.SearchRowStatus(null).status);
    }

    [Fact]
    public void SearchRowStatus_ZeroStreak_IsOk()
    {
        var c = new JObject { ["state"] = "fail", ["streak"] = 0 };   // машина ещё не сняла тревогу
        Assert.Equal("ok", QbitController.SearchRowStatus(c).status);
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
            QbitController.AddSearchChecks(arr, State(("indexer", "fail", 3)), DateTime.UtcNow);

            // выключено ≠ отвалилось: даже с провальным состоянием на диске статус off
            Assert.Equal("off", Find(arr, "searchmon").Value<string>("status"));
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

        var mon = Find(arr, "searchmon");
        Assert.Equal("off", mon.Value<string>("status"));
        Assert.Contains("прогонов ещё не было", mon.Value<string>("detail"));
    }

    [Fact]
    public void AddSearchChecks_MapsStreaksAndTrackers()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchMonitorIntervalMinutes = 180;

        var st = StateWithRun(T0.AddMinutes(-25),
            ("indexer", "ok", 0), ("tracker:rutor", "ok", 0), ("tracker:kinozal", "ok", 1), ("stars", "fail", 2));

        var arr = new JArray();
        QbitController.AddSearchChecks(arr, st, T0);

        Assert.Equal("ok", Find(arr, "indexer").Value<string>("status"));
        Assert.Equal("ok", Find(arr, "tracker:rutor").Value<string>("status"));
        Assert.Equal("fail", Find(arr, "tracker:kinozal").Value<string>("status"));   // state=ok, но прогон провалился
        Assert.Equal("fail", Find(arr, "stars").Value<string>("status"));

        // имя трекера без служебного префикса + возраст прогона в строке мониторинга
        Assert.Equal("Трекер kinozal", Find(arr, "tracker:kinozal").Value<string>("name"));
        Assert.Equal("ok", Find(arr, "searchmon").Value<string>("status"));
        Assert.Contains("25 мин назад", Find(arr, "searchmon").Value<string>("detail"));

        // канарейки (canary:*) — внутренняя кухня монитора, в отчёт не тащим
        Assert.All(arr.OfType<JObject>(), x => Assert.DoesNotContain("canary:", x.Value<string>("id")));
    }

    /// <summary>
    /// Планировщик встал → вердикты канареек относятся к неизвестно какому прошлому.
    /// Не врём «ok», но и в «Проблемы» не тащим десяток одинаковых строк: причина одна.
    /// </summary>
    [Fact]
    public void AddSearchChecks_StaleMonitor_WarnsAndMarksDependentRowsQuiet()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchMonitorIntervalMinutes = 180;
        ModInit.conf.healthMonitorStalePercent = 250;

        var st = StateWithRun(T0.AddHours(-10), ("indexer", "ok", 0), ("tracker:rutor", "ok", 0));

        var arr = new JArray();
        QbitController.AddSearchChecks(arr, st, T0);

        var mon = Find(arr, "searchmon");
        Assert.Equal("warn", mon.Value<string>("status"));
        Assert.Contains("устарели", mon.Value<string>("detail"));
        Assert.Null(mon["quiet"]);   // сама причина в сводке нужна

        var row = Find(arr, "tracker:rutor");
        Assert.Equal("warn", row.Value<string>("status"));
        Assert.True(row.Value<bool>("quiet"));
    }

    /// <summary>Один законный пропуск тика (разогрев, занятый _watchGate) красить экран не должен.</summary>
    [Fact]
    public void AddSearchChecks_OneMissedTick_IsNotStale()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchMonitorIntervalMinutes = 180;
        ModInit.conf.healthMonitorStalePercent = 250;

        var st = StateWithRun(T0.AddHours(-5), ("indexer", "ok", 0));   // порог 7.5 ч

        var arr = new JArray();
        QbitController.AddSearchChecks(arr, st, T0);

        Assert.Equal("ok", Find(arr, "searchmon").Value<string>("status"));
        Assert.Equal("ok", Find(arr, "indexer").Value<string>("status"));
    }

    [Fact]
    public void AddSearchChecks_AllRowsAreInSearchGroup()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchMonitorIntervalMinutes = 180;

        var arr = new JArray();
        QbitController.AddSearchChecks(arr, StateWithRun(T0.AddMinutes(-5), ("indexer", "ok", 0), ("tracker:rutor", "ok", 0)), T0);

        Assert.All(arr.OfType<JObject>(), x => Assert.Equal("Поиск раздач", x.Value<string>("group")));
    }
    #endregion
}
