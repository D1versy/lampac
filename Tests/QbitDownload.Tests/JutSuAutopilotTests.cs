using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// «Автопилот» jut.su на серверной стороне: сегменты опенинга, выбор следующей серии
/// для прогрева и память экрана поиска (что смотрели / что искали).
/// </summary>
public class JutSuAutopilotTests
{
    static JObject TitleWithItems(string slug, params (string kind, int season, int ep)[] eps)
    {
        var items = new JArray(eps.Select(e => new JObject
        {
            ["kind"] = e.kind,
            ["season"] = e.season,
            ["ep"] = e.ep,
            ["key"] = "s" + e.season + "e" + e.ep,
            ["tok"] = JutNet.MakeToken(slug, e.season, e.ep,
                                       e.kind == "gameova" ? "game-ova" : e.kind, 0)
        }));

        return new JObject { ["ok"] = true, ["slug"] = slug, ["items"] = items };
    }

    static JutLink Cur(string slug, int season, int ep, string kind = "episode")
        => new JutLink { slug = slug, season = season, ep = ep, kind = kind };

    // ── следующая серия ───────────────────────────────────────────────────

    [Fact]
    public void Следующая_серия_того_же_сезона()
    {
        TestEnv.FreshCache();
        Access.Call("JutCacheWrite", "title", "spy-family",
                    TitleWithItems("spy-family", ("episode", 1, 1), ("episode", 1, 2), ("episode", 1, 3)));

        string tok = (string)Access.Call("JutNextToken", Cur("spy-family", 1, 1));
        var parsed = JutNet.ParseToken(tok);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.ep);
        Assert.Equal(1, parsed.season);
        Assert.Equal("episode", parsed.kind);
    }

    [Fact]
    public void Дыра_в_нумерации_не_ломает_выбор()
    {
        // Берём минимальный ep больше текущего, а не ep+1: у части тайтлов серии идут с пропусками.
        TestEnv.FreshCache();
        Access.Call("JutCacheWrite", "title", "gapped",
                    TitleWithItems("gapped", ("episode", 1, 1), ("episode", 1, 7), ("episode", 1, 9)));

        var parsed = JutNet.ParseToken((string)Access.Call("JutNextToken", Cur("gapped", 1, 1)));
        Assert.Equal(7, parsed.ep);
    }

    [Fact]
    public void Последняя_серия_сезона_не_даёт_следующей()
    {
        // Клиент строит плейлист в пределах сезона и kind — кросс-сезонного перехода нет,
        // поэтому и греть нечего.
        TestEnv.FreshCache();
        Access.Call("JutCacheWrite", "title", "two-seasons",
                    TitleWithItems("two-seasons", ("episode", 1, 1), ("episode", 1, 2), ("episode", 2, 1)));

        Assert.Null(Access.Call("JutNextToken", Cur("two-seasons", 1, 2)));
    }

    [Fact]
    public void Фильмы_и_серии_не_смешиваются()
    {
        TestEnv.FreshCache();
        Access.Call("JutCacheWrite", "title", "mixed",
                    TitleWithItems("mixed", ("episode", 1, 1), ("film", 1, 1), ("film", 1, 2)));

        var afterFilm = JutNet.ParseToken((string)Access.Call("JutNextToken", Cur("mixed", 1, 1, "film")));
        Assert.Equal("film", afterFilm.kind);
        Assert.Equal(2, afterFilm.ep);

        // У серий продолжения нет — фильм за неё не считается
        Assert.Null(Access.Call("JutNextToken", Cur("mixed", 1, 1)));
    }

    [Fact]
    public void Игровая_OVA_нормализуется_к_виду_токена()
    {
        // 🔥 В JSON тайтла kind пишется как gameova, в токене — game-ova.
        TestEnv.FreshCache();
        Access.Call("JutCacheWrite", "title", "gameova-title",
                    TitleWithItems("gameova-title", ("gameova", 1, 1), ("gameova", 1, 2)));

        var parsed = JutNet.ParseToken((string)Access.Call("JutNextToken", Cur("gameova-title", 1, 1, "game-ova")));
        Assert.NotNull(parsed);
        Assert.Equal("game-ova", parsed.kind);
        Assert.Equal(2, parsed.ep);
    }

    [Fact]
    public void Без_кеша_тайтла_прогрев_молча_пропускается()
    {
        TestEnv.FreshCache();
        Assert.Null(Access.Call("JutNextToken", Cur("never-opened", 1, 1)));
    }

    // ── сегменты ──────────────────────────────────────────────────────────

    [Fact]
    public void Сегмент_опенинга_собирается_для_плеера()
    {
        var link = new JutLink
        {
            slug = "spy-family", season = 1, ep = 3, kind = "episode",
            duration = 1450, introStart = 80, introEnd = 170, outro = 1350
        };

        var jo = (JObject)Access.Call("JutSegJson", link);
        var skip = (JArray)jo["segments"]["skip"];

        Assert.Single(skip);
        Assert.Equal(80, skip[0]["start"].Value<double>());
        Assert.Equal(170, skip[0]["end"].Value<double>());
        Assert.Equal(80, jo["intro_start"].Value<double>());
        Assert.Equal(170, jo["intro_end"].Value<double>());
        // 🔥 duration_ms не отдаём: web-бандл при известной длительности подгоняет метки
        // под свою эвристику, а наши секунды точны для этого самого файла.
        Assert.Null(jo["segments"]["duration_ms"]);
    }

    [Fact]
    public void Без_опенинга_список_скипов_пуст()
    {
        var link = new JutLink { slug = "x", season = 1, ep = 1, kind = "episode", duration = 1400, outro = 1300 };
        var jo = (JObject)Access.Call("JutSegJson", link);

        Assert.Empty((JArray)jo["segments"]["skip"]);
        Assert.Equal(JTokenType.Null, jo["intro_start"].Type);
        Assert.Equal(JTokenType.Null, jo["intro_end"].Type);
    }

    [Fact]
    public void Разметка_переживает_протухание_ссылки()
    {
        // Кеш ссылок живёт 240 с, разметка серии не меняется никогда: сегменты должны
        // отдаваться из своего кеша и через час после старта серии.
        TestEnv.FreshCache();
        var link = new JutLink
        {
            slug = "spy-family", season = 1, ep = 3, kind = "episode",
            duration = 1450, introStart = 80, introEnd = 170
        };
        Access.Call("JutSegStore", link);

        string path = Path.Combine(JutNet.JutDataDir(), "seg", "spy-family-s1e3-episode.json");
        Assert.True(File.Exists(path), "разметка обязана лечь на диск: она переживает рестарт");

        var cached = JObject.Parse(File.ReadAllText(path));
        Assert.Equal(170, cached["intro_end"].Value<double>());
        Assert.Equal(80, ((JArray)cached["segments"]["skip"])[0]["start"].Value<double>());
    }

    // ── память экрана поиска (раздельно по устройствам) ───────────────────
    // ⚠️ Дедуп-окно просмотров и метка миграции статические и переживают тест — сбрасываем
    // на каждом входе, иначе повторный slug молча не запишется и тест позеленеет на сломанной логике.

    const string DevA = "da11111";
    const string DevB = "db22222";

    static void FreshHistory()
    {
        TestEnv.FreshCache();
        QbitController.JutHistoryResetForTests();
    }

    static JArray Recent(string uid, int limit = 50)
        => (JArray)((JObject)Access.Call("JutRecentPayload", limit, uid))["items"];

    static void Search(string uid, params string[] slugs)
        => Access.Call("JutHistoryRecordSearch",
                       new JArray(slugs.Select(s => new JObject { ["slug"] = s })), uid, 12);

    [Fact]
    public void Просмотренное_идёт_впереди_искомого()
    {
        FreshHistory();

        Search(DevA, "found-a", "found-b");
        QbitController.JutHistoryTouchWatch("watched-one", DevA);

        var items = Recent(DevA);

        Assert.Equal("watched-one", items[0]["slug"].Value<string>());
        Assert.Equal(3, items.Count);
        Assert.Contains(items, x => x["slug"].Value<string>() == "found-a");
    }

    [Fact]
    public void Один_тайтл_не_дублируется_между_секциями()
    {
        FreshHistory();

        Search(DevA, "same-one");
        QbitController.JutHistoryTouchWatch("same-one", DevA);

        var items = Recent(DevA);

        Assert.Single(items);
        Assert.Equal("watch", items[0]["src"].Value<string>());
    }

    [Fact]
    public void Свежий_просмотр_поднимается_наверх()
    {
        FreshHistory();

        QbitController.JutHistoryTouchWatch("older", DevA);
        QbitController.JutHistoryTouchWatch("newer", DevA);

        var items = Recent(DevA);
        Assert.Equal("newer", items[0]["slug"].Value<string>());
        Assert.Equal("older", items[1]["slug"].Value<string>());
    }

    [Fact]
    public void Кап_ограничивает_выдачу()
    {
        FreshHistory();
        for (int i = 0; i < 60; i++) QbitController.JutHistoryTouchWatch("title-" + i, DevA);

        Assert.Equal(50, Recent(DevA).Count);
    }

    [Fact]
    public void Повторное_открытие_потока_не_плодит_записей()
    {
        // Плеер открывает поток многократно (seek, докачка) — счётчик просмотров
        // не должен считать это отдельными просмотрами.
        FreshHistory();

        QbitController.JutHistoryTouchWatch("seeky", DevA);
        QbitController.JutHistoryTouchWatch("seeky", DevA);
        QbitController.JutHistoryTouchWatch("seeky", DevA);

        string path = Path.Combine(JutNet.JutDataDir(), "history", DevA + ".json");
        var jo = JsonStore.ReadObject(path);
        Assert.Equal(1, jo["watched"]["seeky"]["count"].Value<int>());
    }

    [Fact]
    public void Устройства_не_видят_историю_друг_друга()
    {
        // 🔥 Суть требования владельца: у каждого клиента своя выдача.
        FreshHistory();

        QbitController.JutHistoryTouchWatch("only-on-a", DevA);
        QbitController.JutHistoryTouchWatch("only-on-b", DevB);

        var a = Recent(DevA).Select(x => x["slug"].Value<string>()).ToList();
        var b = Recent(DevB).Select(x => x["slug"].Value<string>()).ToList();

        Assert.Contains("only-on-a", a);
        Assert.DoesNotContain("only-on-b", a);
        Assert.Contains("only-on-b", b);
        Assert.DoesNotContain("only-on-a", b);
    }

    [Fact]
    public void Дедуп_одного_устройства_не_глушит_другое()
    {
        // Ключ дедупа обязан включать устройство: иначе просмотр на телефоне на 5 минут
        // заглушил бы запись того же тайтла на ТВ.
        FreshHistory();

        QbitController.JutHistoryTouchWatch("same-anime", DevA);
        QbitController.JutHistoryTouchWatch("same-anime", DevB);

        Assert.Contains(Recent(DevA), x => x["slug"].Value<string>() == "same-anime");
        Assert.Contains(Recent(DevB), x => x["slug"].Value<string>() == "same-anime");
    }

    [Fact]
    public void Общая_история_добирается_новому_устройству()
    {
        // Иначе после разделения истории новый клиент увидел бы пустой экран.
        FreshHistory();

        QbitController.JutHistoryTouchWatch("from-shared", null);   // клиент без uid → _shared
        QbitController.JutHistoryTouchWatch("own-one", DevA);

        var a = Recent(DevA).Select(x => x["slug"].Value<string>()).ToList();

        Assert.Equal("own-one", a[0]);                              // своё впереди
        Assert.Contains("from-shared", a);                          // общее — добором
    }

    [Fact]
    public void Мусорный_слаг_в_историю_не_попадает()
    {
        // slug идёт в путь на диске и в URL к сайту — гейт обязателен на каждом входе.
        FreshHistory();

        QbitController.JutHistoryTouchWatch("../../etc/passwd", DevA);
        Search(DevA, "../evil");

        Assert.Empty(Recent(DevA));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("C:\\Windows\\x")]
    [InlineData("!!!")]
    [InlineData("")]
    [InlineData(null)]
    public void Мусорный_uid_не_вылезает_из_каталога(string uid)
    {
        // uid идёт в ИМЯ ФАЙЛА, а RequestInfo отдаёт его из query без проверки символов.
        string bucket = QbitController.JutHistoryBucket(uid);

        Assert.DoesNotContain('/', bucket);
        Assert.DoesNotContain('\\', bucket);
        Assert.DoesNotContain(':', bucket);
        Assert.NotEqual("..", bucket);
        Assert.False(string.IsNullOrWhiteSpace(bucket));
        Assert.Equal(bucket, Path.GetFileName(bucket));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("com1")]
    public void Зарезервированные_имена_Windows_не_становятся_бакетом(string uid)
    {
        // con.json на Windows — это консоль, а не файл; тесты гоняются именно там.
        string bucket = QbitController.JutHistoryBucket(uid);
        Assert.NotEqual(uid.ToLowerInvariant(), bucket);
    }

    [Fact]
    public void Миграция_общей_истории_идемпотентна()
    {
        // Повторный проход не должен удваивать счётчики просмотров.
        FreshHistory();

        var legacy = new JObject
        {
            ["watched"] = new JObject { ["old-title"] = new JObject { ["at"] = DateTime.UtcNow, ["count"] = 3 } },
            ["searched"] = new JObject()
        };
        JsonStore.WriteNow(Path.Combine(JutNet.JutDataDir(), "history.json"), legacy);

        Assert.Contains(Recent(DevA), x => x["slug"].Value<string>() == "old-title");

        QbitController.JutHistoryResetForTests();       // как будто рестарт процесса
        Assert.Contains(Recent(DevA), x => x["slug"].Value<string>() == "old-title");

        var shared = JsonStore.ReadObject(Path.Combine(JutNet.JutDataDir(), "history", "_shared.json"));
        Assert.Equal(3, shared["watched"]["old-title"]["count"].Value<int>());
    }

    [Fact]
    public void Реплика_в_историю_дома_не_пишет()
    {
        FreshHistory();
        var prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";
            QbitController.JutHistoryTouchWatch("from-replica", DevA);

            Assert.Empty(Recent(DevA));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }
}
