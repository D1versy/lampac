using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JacRed.Engine;
using JacRed.Models.AppConf;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Сессии FlareSolverr (Modules/JacRed/Engine/FlareSolverr.cs). Сети не нужно: транспорт и часы
/// подменяются швами Transport/Now.
///
/// Зачем эти тесты вообще появились — боевой случай §BW: за сутки 153 sessions.create против
/// 0 sessions.destroy, 37 живых Chrome'ов, упор в mem_limit и 108 кулдаунов по 300 с. Причина
/// была в уникальном имени сессии на каждый заход плюс обнуление _session без destroy.
///
/// ⚠️ Состояние процессное. Параллелизм в сборке отключён (TestEnv.cs), но Reset() на каждый
/// тест обязателен — иначе сессия и кулдаун текут между тестами.
/// </summary>
public class JacRedFlareSolverrTests : IDisposable
{
    const string SOLUTION = "{\"status\":\"ok\",\"solution\":{\"status\":200," +
                            "\"response\":\"<html>tracker</html>\",\"userAgent\":\"UA\"}}";
    const string OK = "{\"status\":\"ok\"}";
    const string NO_SESSION = "{\"status\":\"error\",\"message\":\"The session doesn't exist.\"}";

    readonly List<(string cmd, string session, JObject body)> _calls = new();
    DateTime _now = new DateTime(2026, 1, 1, 12, 0, 0);

    // Что отвечает поддельный солвер. null = транспорт не дошёл (именно так ведёт себя Http.Post).
    string _createReply = OK;
    string _destroyReply = OK;
    string _solveReply = SOLUTION;

    static FlareSolverrConf Conf() => new FlareSolverrConf
    {
        enable = true,
        url = "http://flaresolverr:8191",
        cooldownSeconds = 300,
        sessionTtlMinutes = 120,
        sessionName = "jacred"
    };

    public JacRedFlareSolverrTests()
    {
        FlareSolverr.Reset();
        FlareSolverr.Now = () => _now;
        FlareSolverr.Transport = (url, json, timeout) =>
        {
            var body = JObject.Parse(json);
            string cmd = body.Value<string>("cmd");
            _calls.Add((cmd, body.Value<string>("session"), body));

            return Task.FromResult(cmd switch
            {
                "sessions.create" => _createReply,
                "sessions.destroy" => _destroyReply,
                _ => _solveReply
            });
        };
    }

    public void Dispose()
    {
        FlareSolverr.Reset();
        FlareSolverr.Now = () => DateTime.Now;
        FlareSolverr.Transport = null;   // боевой транспорт восстанавливать незачем: тесты сети не трогают
    }

    int Count(string cmd) => _calls.Count(c => c.cmd == cmd);

    /// <summary>Промотать кулдаун: без этого Available() закрыт и следующий вызов молча вернёт null.</summary>
    void SkipCooldown() => _now = _now.AddSeconds(301);

    // ─────────────────────────────────────────────────────────────────────────────
    // Главный инвариант: сессии НЕ текут
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Прямая формализация диагноза §BW. Раньше здесь было 5 create и 0 destroy.
    /// </summary>
    [Fact]
    public async Task Failures_do_not_leak_sessions()
    {
        var c = Conf();
        _solveReply = null;                        // solve валится → MarkDown на каждом заходе

        for (int i = 0; i < 5; i++)
        {
            await FlareSolverr.Get(c, "https://rutracker.org/forum/tracker.php");
            SkipCooldown();
        }

        Assert.Equal(5, Count("sessions.create"));
        Assert.True(Count("sessions.create") - Count("sessions.destroy") <= 1,
                    $"утечка: create={Count("sessions.create")}, destroy={Count("sessions.destroy")}");
    }

    /// <summary>
    /// Имя сессии стабильно. Это и есть структурная защита: sessions.create в образе идемпотентен
    /// по имени, значит второй браузер не заведётся, даже если весь остальной код сломается.
    /// </summary>
    [Fact]
    public async Task Session_name_is_stable_across_failures()
    {
        var c = Conf();
        _solveReply = null;

        for (int i = 0; i < 4; i++)
        {
            await FlareSolverr.Get(c, "https://rutracker.org/");
            SkipCooldown();
        }

        var names = _calls.Where(x => x.cmd == "sessions.create").Select(x => x.session).Distinct().ToArray();
        Assert.Equal(new[] { "jacred" }, names);
    }

    /// <summary>Сбой → следующий заход обязан снести браузер, а не переиспользовать зависший.</summary>
    [Fact]
    public async Task Failure_forces_fresh_session()
    {
        var c = Conf();

        _solveReply = null;
        await FlareSolverr.Get(c, "https://rutracker.org/");
        Assert.Equal(0, Count("sessions.destroy"));   // первый заход сносить ещё нечего

        SkipCooldown();
        _solveReply = SOLUTION;
        var sol = await FlareSolverr.Get(c, "https://rutracker.org/");

        Assert.NotNull(sol);
        Assert.Equal(1, Count("sessions.destroy"));
        Assert.Equal(2, Count("sessions.create"));
    }

    /// <summary>Успешные заходы подряд сессию не пересоздают — TTL ещё не вышел.</summary>
    [Fact]
    public async Task Success_reuses_session()
    {
        var c = Conf();

        for (int i = 0; i < 3; i++)
            Assert.NotNull(await FlareSolverr.Get(c, "https://rutracker.org/"));

        Assert.Equal(1, Count("sessions.create"));
        Assert.Equal(0, Count("sessions.destroy"));
    }

    /// <summary>Истёк наш TTL — сессия пересоздаётся, и старая при этом сносится.</summary>
    [Fact]
    public async Task Ttl_expiry_rotates_with_destroy()
    {
        var c = Conf();

        await FlareSolverr.Get(c, "https://rutracker.org/");
        _now = _now.AddMinutes(121);                  // sessionTtlMinutes = 120
        await FlareSolverr.Get(c, "https://rutracker.org/");

        Assert.Equal(2, Count("sessions.create"));
        Assert.Equal(1, Count("sessions.destroy"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Контракт с образом
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// session_ttl_minutes обязан уезжать солверу: это его собственная ротация с корректным
    /// driver.quit() и уборкой каталога — вторая линия обороны, если наша не сработает.
    /// </summary>
    [Fact]
    public async Task Ttl_is_sent_to_solver()
    {
        var c = Conf();

        await FlareSolverr.Get(c, "https://rutracker.org/");
        await FlareSolverr.PostForm(c, "https://rutracker.org/forum/login.php",
                                    new Dictionary<string, string> { ["login_username"] = "u" });

        foreach (string cmd in new[] { "request.get", "request.post" })
        {
            var body = _calls.First(x => x.cmd == cmd).body;
            Assert.Equal(120, body.Value<int?>("session_ttl_minutes"));
        }
    }

    /// <summary>
    /// ⚠️ destroy несуществующей сессии образ отдаёт 500 «The session doesn't exist» — это успех,
    /// браузера нет. Считать это отказом нельзя: уборка ставила бы себе кулдаун сама.
    /// </summary>
    [Fact]
    public async Task Destroy_of_missing_session_counts_as_success()
    {
        var c = Conf();
        _destroyReply = NO_SESSION;

        Assert.True(await FlareSolverr.DropSession(c));
    }

    /// <summary>А вот молчание транспорта — настоящий отказ: браузер мог остаться жив.</summary>
    [Fact]
    public async Task Destroy_without_transport_is_failure()
    {
        var c = Conf();
        _destroyReply = null;

        Assert.False(await FlareSolverr.DropSession(c));
    }

    /// <summary>
    /// DropSession обязан работать и когда локальной сессии нет: имя стабильное, значит сессию
    /// от прошлого процесса lampac всё ещё можно снести. Раньше ранний return по _session == null
    /// делал метод бесполезным ровно в этом случае.
    /// </summary>
    [Fact]
    public async Task DropSession_works_without_local_session()
    {
        var c = Conf();

        Assert.True(await FlareSolverr.DropSession(c));
        Assert.Equal(1, Count("sessions.destroy"));
        Assert.Equal("jacred", _calls.Single(x => x.cmd == "sessions.destroy").session);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Поколение сессии — сигнал потребителям, что кука логина уехала
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Epoch_advances_only_on_new_browser()
    {
        var c = Conf();

        await FlareSolverr.Get(c, "https://rutracker.org/");
        int first = FlareSolverr.SessionEpoch;

        await FlareSolverr.Get(c, "https://rutracker.org/");
        Assert.Equal(first, FlareSolverr.SessionEpoch);      // та же сессия — логин ещё жив

        _solveReply = null;
        await FlareSolverr.Get(c, "https://rutracker.org/");
        SkipCooldown();
        _solveReply = SOLUTION;
        await FlareSolverr.Get(c, "https://rutracker.org/");

        Assert.True(FlareSolverr.SessionEpoch > first);      // браузер новый — логина в нём нет
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Инварианты, которые нельзя сломать
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Кулдаун закрывает солвер целиком: во время паузы к нему не ходим вовсе.</summary>
    [Fact]
    public async Task Cooldown_blocks_calls()
    {
        var c = Conf();
        _solveReply = null;

        await FlareSolverr.Get(c, "https://rutracker.org/");
        int after = _calls.Count;

        Assert.Null(await FlareSolverr.Get(c, "https://rutracker.org/"));
        Assert.Equal(after, _calls.Count);                  // ни одного запроса не ушло

        SkipCooldown();
        await FlareSolverr.Get(c, "https://rutracker.org/");
        Assert.True(_calls.Count > after);
    }

    /// <summary>
    /// Нерешённый челлендж — это конкретная страница, а не «солвер лёг». Кулдаун ставить нельзя,
    /// иначе один упрямый URL выключал бы солвер целиком.
    /// </summary>
    [Fact]
    public async Task Unsolved_challenge_does_not_trigger_cooldown()
    {
        var c = Conf();
        _solveReply = "{\"status\":\"error\",\"message\":\"Error solving the challenge.\"}";

        Assert.Null(await FlareSolverr.Get(c, "https://rutracker.org/"));

        int after = _calls.Count;
        Assert.Null(await FlareSolverr.Get(c, "https://rutracker.org/"));
        Assert.True(_calls.Count > after, "кулдаун не должен был встать");
    }

    /// <summary>Выключенный солвер не трогаем вовсе.</summary>
    [Fact]
    public async Task Disabled_solver_is_never_called()
    {
        var c = Conf();
        c.enable = false;

        Assert.Null(await FlareSolverr.Get(c, "https://rutracker.org/"));
        Assert.Empty(_calls);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Кеш HTML: логин прежде запроса и выброс негодной страницы
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Боевой случай 24.08.2026: логин и запрос поиска — две независимые задачи за один семафор,
    /// и запрос выиграл гонку. Solve вернул гостевую страницу, она осела в кеше на час, rutracker
    /// весь этот час отдавал ноль раздач. warmup обязан отработать ДО обращения к солверу.
    /// </summary>
    [Fact]
    public async Task Warmup_runs_before_the_request()
    {
        var c = Conf();
        bool warmed = false;
        bool warmedFirst = false;

        FlareSolverr.Transport = (url, json, timeout) =>
        {
            var body = JObject.Parse(json);
            string cmd = body.Value<string>("cmd");
            _calls.Add((cmd, body.Value<string>("session"), body));

            if (cmd == "request.get")
                warmedFirst = warmed;

            return Task.FromResult(cmd switch
            {
                "sessions.create" => _createReply,
                "sessions.destroy" => _destroyReply,
                _ => _solveReply
            });
        };

        string html = await FlareSolverr.CachedHtml(c, "k1", "https://rutracker.org/forum/tracker.php", 30,
                                                    warmup: async () => { await Task.Yield(); warmed = true; });

        Assert.Equal("<html>tracker</html>", html);
        Assert.True(warmedFirst, "запрос ушёл раньше логина");
    }

    /// <summary>Упавший warmup не должен ронять сам запрос: логин мог не пройти, поиск всё равно нужен.</summary>
    [Fact]
    public async Task Warmup_failure_does_not_break_the_request()
    {
        var c = Conf();

        string html = await FlareSolverr.CachedHtml(c, "k2", "https://rutracker.org/forum/tracker.php", 30,
                                                    warmup: () => throw new InvalidOperationException("логин упал"));

        Assert.Equal("<html>tracker</html>", html);
    }

    /// <summary>Второй заход берёт HTML из кеша — солвер не трогаем.</summary>
    [Fact]
    public async Task CachedHtml_serves_second_call_from_cache()
    {
        var c = Conf();

        Assert.NotNull(await FlareSolverr.CachedHtml(c, "k3", "https://rutracker.org/", 30));
        int after = Count("request.get");

        Assert.NotNull(await FlareSolverr.CachedHtml(c, "k3", "https://rutracker.org/", 30));
        Assert.Equal(after, Count("request.get"));
    }

    /// <summary>
    /// Странице ТЕМЫ нужен короткий кеш: на ней живёт infohash, по смене которого слежение
    /// узнаёт о перерегистрации раздачи. Общий htmlCacheMinutes (час) спрятал бы её от
    /// CheckWatches, поэтому parseMagnet передаёт свой ttlMinutes.
    /// </summary>
    [Fact]
    public async Task Per_call_ttl_overrides_the_common_one()
    {
        var c = Conf();
        c.htmlCacheMinutes = 60;

        await FlareSolverr.CachedHtml(c, "k5", "https://rutracker.org/forum/viewtopic.php?t=1", 30, ttlMinutes: 5);
        int after = Count("request.get");

        _now = _now.AddMinutes(4);                       // ещё внутри пяти минут — берём из кеша
        await FlareSolverr.CachedHtml(c, "k5", "https://rutracker.org/forum/viewtopic.php?t=1", 30, ttlMinutes: 5);
        Assert.Equal(after, Count("request.get"));

        _now = _now.AddMinutes(2);                       // 6 минут — снимок протух, хотя общий TTL час
        await FlareSolverr.CachedHtml(c, "k5", "https://rutracker.org/forum/viewtopic.php?t=1", 30, ttlMinutes: 5);
        Assert.Equal(after + 1, Count("request.get"));
    }

    /// <summary>
    /// Forget возвращает ключ в работу: тот, кто по содержимому понял, что страница негодная
    /// (гостевая версия rutracker), обязан иметь возможность её выбросить.
    /// </summary>
    [Fact]
    public async Task Forget_makes_next_call_refetch()
    {
        var c = Conf();

        await FlareSolverr.CachedHtml(c, "k4", "https://rutracker.org/", 30);
        int after = Count("request.get");

        FlareSolverr.Forget("k4");

        await FlareSolverr.CachedHtml(c, "k4", "https://rutracker.org/", 30);
        Assert.Equal(after + 1, Count("request.get"));
    }
}
