using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Слежение за сериями jut.su: ДВА РЕЖИМА подписки.
//   "notify" (autoGrab:false) — включается только с карточки тайтла: уведомляем, НЕ качаем.
//   "grab"   (autoGrab:true)  — включается только из «Загрузок»: уведомляем И качаем.
//
// Тик прогоняется БЕЗ СЕТИ через параметры-сеймы JutWatchTick(loadOngoing, loadTitle):
// у JutNet своя фабрика HttpClient без места под HttpMessageHandler.
//
// ⚠️ Воркер скачивания пиним (_jutWorker = 1), иначе постановка в очередь утащит тест
// в реальную сеть с ретраями 5/15/60 сек.
// Канон: E:\Media-server\claude\jut\02-architecture.md §9
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Доступ к private-static кухне слежения + подготовка изолированного окружения.</summary>
static class JutWatchAccess
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    static FieldInfo F(string name) =>
        C.GetField(name, SF) ?? throw new MissingFieldException("QbitController." + name);

    public static string WatchPath() => (string)Access.Call("JutWatchPath");
    public static JArray LoadWatch() => (JArray)Access.Call("JutLoadWatch");
    public static void SaveWatch(JArray arr) => Access.Call("JutSaveWatch", arr);

    public static string MetaPath(string hash) => (string)Access.Call("MetaPath", hash);
    public static string LocalPath(string hash) => (string)Access.Call("LocalPath", hash);
    public static string PosterPath(string hash) => (string)Access.Call("PosterPath", hash);
    public static string LinkPath(string hash) => (string)Access.Call("LinkPath", hash);

    public static JObject Find(string slug) => LoadWatch().OfType<JObject>()
        .FirstOrDefault(x => string.Equals(x.Value<string>("slug"), slug, StringComparison.OrdinalIgnoreCase));

    // ── очередь скачивания ────────────────────────────────────────────────
    public sealed class QItem
    {
        public string slug, epkey, kind;
        public int season, ep;
        public bool cancel;
    }

    public static List<QItem> Queue()
    {
        var list = new List<QItem>();
        foreach (var it in (IEnumerable)F("_jutQueue").GetValue(null))
        {
            var t = it.GetType();
            string G(string n) => (string)t.GetField(n, IF).GetValue(it);
            int I(string n) => (int)t.GetField(n, IF).GetValue(it);
            list.Add(new QItem
            {
                slug = G("slug"), epkey = G("epkey"), kind = G("kind"),
                season = I("season"), ep = I("ep"),
                cancel = (bool)t.GetField("cancel", IF).GetValue(it)
            });
        }
        return list;
    }

    public static HashSet<string> Queued() => (HashSet<string>)F("_jutQueued").GetValue(null);

    public static void SeedQueued(string slug, string epkey)
    {
        var set = Queued();
        lock (F("_jutEnqLock").GetValue(null)) set.Add(slug + ":" + epkey);
    }

    static void ClearQueues()
    {
        var q = F("_jutQueue").GetValue(null);
        q.GetType().GetMethod("Clear").Invoke(q, null);
        Queued().Clear();
        var jobs = F("_jutJobs").GetValue(null);
        jobs.GetType().GetMethod("Clear").Invoke(jobs, null);
        DownloadWants.Jut.Reset(flush: false);
    }

    /// <summary>
    /// Пин воркера: JutKickWorker делает CompareExchange(_jutWorker, 1, 0) и при 1 выходит.
    /// ⚠️ В Dispose очередь осушается ДО снятия пина — иначе настоящий воркер соседнего
    /// теста подберёт наши элементы и уйдёт качать их из интернета.
    /// </summary>
    public static IDisposable PinWorker()
    {
        ClearQueues();
        F("_jutWorker").SetValue(null, 1);
        return new Pin();
    }

    sealed class Pin : IDisposable
    {
        public void Dispose()
        {
            ClearQueues();
            F("_jutWorker").SetValue(null, 0);
        }
    }

    // ── окружение ─────────────────────────────────────────────────────────
    /// <summary>
    /// Свежий cachePath (от него производны jut/watch.json, meta/, local/, img/, qdl.db)
    /// + свой каталог скачивания (jutDownloadsPath от FreshCache НЕ производен)
    /// + выключенный апгрейд постеров (иначе JutEnsureMeta стартует сетевой воркер).
    /// </summary>
    public static (string cache, string downloads) Env()
    {
        string cache = TestEnv.FreshCache();
        string downloads = Path.Combine(cache, "downloads");
        Directory.CreateDirectory(downloads);
        ModInit.conf.jutEnable = true;
        ModInit.conf.jutDownloadsPath = downloads;
        ModInit.conf.jutPosterUpgrade = false;
        ModInit.conf.jutWatchAutoGrab = true;
        ModInit.conf.jutWatchSeasonSwitch = true;
        ModInit.conf.jutWatchTitlesPerTick = 30;
        ModInit.conf.jutMinFreeGb = 1;
        using var db = new SqlContext();
        db.Database.EnsureCreated();
        return (cache, downloads);
    }

    /// <summary>Тайтл без постера: с ним JutEnsureMeta не полезет качать картинку.</summary>
    public static JutTitle Title(string slug, bool ongoing, params (int season, int num)[] eps)
    {
        var t = new JutTitle { slug = slug, titleRu = "Тайтл " + slug, ongoing = ongoing, poster = null };
        foreach (var (s, n) in eps)
            t.items.Add(new JutEp { kind = JutEpKind.Episode, season = s, num = n, url = $"/{slug}/season-{s}/episode-{n}.html" });
        return t;
    }

    public static void AddSpecial(JutTitle t, JutEpKind kind, int num)
        => t.items.Add(new JutEp { kind = kind, num = num, url = $"/{t.slug}/{kind}-{num}.html" });

    /// <summary>Подписка на диск без сети: то же, что делает роут /qdl/jut/watch.</summary>
    public static QbitController.JutWatchUpsertResult Subscribe(string slug, JutTitle t, int season, int autoGrab)
        => QbitController.JutWatchUpsert(slug, t, season, autoGrab);

    /// <summary>Отмотать baseline на N последних серий — имитация «вышли новые серии».</summary>
    public static void RollbackKnown(string slug, int drop)
    {
        var arr = LoadWatch();
        var rec = arr.OfType<JObject>().First(x => x.Value<string>("slug") == slug);
        var keys = (JArray)rec["known"]["keys"];
        for (int i = 0; i < drop && keys.Count > 0; i++) keys.RemoveAt(keys.Count - 1);
        rec["known"]["count"] = keys.Count;
        SaveWatch(arr);
    }

    public static Func<Task<(bool, Dictionary<string, int>)>> Ongoing(bool ok, params (string slug, int count)[] items)
        => () => Task.FromResult((ok, items.ToDictionary(x => x.slug, x => x.count, StringComparer.OrdinalIgnoreCase)));

    public static Task<JObject> Tick(Func<Task<(bool, Dictionary<string, int>)>> ongoing,
                                     Func<string, Task<(JutTitle, string)>> title)
        => QbitController.JutWatchTick(true, ongoing, title);

    /// <summary>Один тайтл на все запросы страницы — обычный случай в тестах.</summary>
    public static Func<string, Task<(JutTitle, string)>> One(JutTitle t)
        => _ => Task.FromResult((t, (string)null));

    public static void NotifyNew(string slug, string title, JutEp e, bool auto)
        => Access.Call("JutNotifyNewEpisode", slug, title, e, auto);

    public static List<NotiModel> Noti(string slug)
    {
        using var db = new SqlContext();
        string sk = "j" + slug;
        return db.noti.Where(x => x.seriesKey == sk).OrderBy(x => x.Id).ToList();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
public class JutSuWatchModeTests
{
    [Fact]
    public void Режим_читается_из_autoGrab()
    {
        Assert.Equal("grab", QbitController.JutModeOf(new JObject { ["autoGrab"] = true }));
        Assert.Equal("notify", QbitController.JutModeOf(new JObject { ["autoGrab"] = false }));
    }

    [Fact]
    public void Запись_без_autoGrab_это_режим_качаю()
    {
        // Подписки, созданные до раздвоения режимов, обязаны продолжать качать: трактовать
        // их как «только уведомления» = молча выключить скачивание живым подпискам.
        TestEnv.EnsureConf();
        ModInit.conf.jutWatchAutoGrab = false;   // конфиг НЕ должен переопределять запись
        Assert.Equal("grab", QbitController.JutModeOf(new JObject { ["slug"] = "x" }));
        ModInit.conf.jutWatchAutoGrab = true;
    }

    [Fact]
    public void Явный_параметр_режима_сильнее_сохранённого()
    {
        TestEnv.EnsureConf();
        Assert.True(QbitController.JutAutoGrabFor(false, 1));
        Assert.False(QbitController.JutAutoGrabFor(true, 0));
    }

    [Fact]
    public void Без_параметра_режим_сохраняется_а_не_берётся_из_конфига()
    {
        // 🔥 Регресс: раньше любой повторный /qdl/jut/watch без autoGrab затирал режим
        // дефолтом конфига, то есть молча включал автоскачивание.
        TestEnv.EnsureConf();
        ModInit.conf.jutWatchAutoGrab = true;
        Assert.False(QbitController.JutAutoGrabFor(false, -1));
        Assert.True(QbitController.JutAutoGrabFor(true, -1));
    }

    [Fact]
    public void Новая_запись_без_параметра_берёт_дефолт_конфига()
    {
        TestEnv.EnsureConf();
        ModInit.conf.jutWatchAutoGrab = false;
        Assert.False(QbitController.JutAutoGrabFor(null, -1));
        ModInit.conf.jutWatchAutoGrab = true;
        Assert.True(QbitController.JutAutoGrabFor(null, -1));
    }

    [Fact]
    public void Карта_режимов_читает_файл_подписок()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("a-slug", JutWatchAccess.Title("a-slug", true, (1, 1)), 0, 1);
        JutWatchAccess.Subscribe("b-slug", JutWatchAccess.Title("b-slug", true, (1, 1)), 0, 0);

        var map = QbitController.JutWatchModes();
        Assert.Equal("grab", map["a-slug"]);
        Assert.Equal("notify", map["b-slug"]);
        Assert.Equal("grab", map["A-SLUG"]);            // регистронезависимо, как JutFindWatch
        Assert.False(map.ContainsKey("c-slug"));
    }

    [Fact]
    public void Битый_файл_подписок_даёт_пустую_карту_без_исключения()
    {
        JutWatchAccess.Env();
        string p = JutWatchAccess.WatchPath();
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, "{{{ не json");
        Assert.Empty(QbitController.JutWatchModes());
    }

    [Fact]
    public void Смена_режима_не_двигает_baseline_и_сезон()
    {
        // Апгрейд notify→grab обязан оставить known как есть: иначе серия, вышедшая между
        // тиком и нажатием, уходит в baseline и не скачается никогда.
        JutWatchAccess.Env();
        var t = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));
        JutWatchAccess.Subscribe("liar", t, 0, 0);
        string keysBefore = JutWatchAccess.Find("liar")["known"]["keys"].ToString();

        Assert.True(QbitController.JutWatchSetModeOnDisk("liar", true, out string mode, out int season));
        Assert.Equal("grab", mode);
        Assert.Equal(1, season);

        var rec = JutWatchAccess.Find("liar");
        Assert.True(rec.Value<bool>("autoGrab"));
        Assert.Equal(keysBefore, rec["known"]["keys"].ToString());
        Assert.Equal(3, rec["known"].Value<int>("count"));
        Assert.Equal(1, rec.Value<int>("season"));
    }

    [Fact]
    public void Смена_режима_без_подписки_не_создаёт_её()
    {
        JutWatchAccess.Env();
        Assert.False(QbitController.JutWatchSetModeOnDisk("nope", true, out _, out _));
        Assert.Null(JutWatchAccess.Find("nope"));
    }

    [Fact]
    public void Подписка_с_нулевым_сезоном_берёт_последний_вышедший()
    {
        // Регресс: карточка тайтла слала сезон ПЕРВОЙ серии списка и у многосезонного
        // тайтла подписывала сезон 1, где новых серий не будет никогда.
        JutWatchAccess.Env();
        var t = JutWatchAccess.Title("multi", true, (1, 1), (1, 2), (2, 1), (3, 1), (3, 2), (3, 3));
        var r = JutWatchAccess.Subscribe("multi", t, 0, 0);
        Assert.Equal(3, r.season);
        Assert.Equal(3, r.baseline);
        Assert.Equal("notify", r.mode);
        Assert.True(r.created);
    }

    [Fact]
    public void Подписка_с_явным_сезоном_уважает_его()
    {
        JutWatchAccess.Env();
        var t = JutWatchAccess.Title("multi", true, (1, 1), (1, 2), (3, 1));
        var r = JutWatchAccess.Subscribe("multi", t, 1, 1);
        Assert.Equal(1, r.season);
        Assert.Equal(2, r.baseline);
    }

    [Fact]
    public void Подписка_на_тайтл_без_серий_не_падает()
    {
        JutWatchAccess.Env();
        var r = JutWatchAccess.Subscribe("empty", JutWatchAccess.Title("empty", false), 0, 0);
        Assert.Equal(1, r.season);
        Assert.Equal(0, r.baseline);
        Assert.NotNull(JutWatchAccess.Find("empty"));
    }

    [Fact]
    public void Повторная_подписка_помечает_режим_неизменным_и_не_создаёт_дубль()
    {
        JutWatchAccess.Env();
        var t = JutWatchAccess.Title("dup", true, (1, 1));
        JutWatchAccess.Subscribe("dup", t, 0, 0);
        var r = JutWatchAccess.Subscribe("dup", t, 0, -1);
        Assert.False(r.created);
        Assert.Equal("notify", r.mode);                       // режим не затёрт конфигом
        Assert.Single(JutWatchAccess.LoadWatch().OfType<JObject>());
    }

    [Theory]
    [InlineData("grab", true)]
    [InlineData("notify", true)]
    [InlineData("off", false)]
    public void Список_загрузок_отдаёт_режим_и_bool_watched(string mode, bool watched)
    {
        var modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mode != "off") modes["liar"] = mode;

        var item = new JObject();
        QbitController.JutDecorateListItem(item, new JObject { ["jut"] = new JObject { ["slug"] = "liar" } }, modes);

        Assert.Equal("liar", item["jut"].Value<string>("slug"));
        Assert.Equal(mode, item["jut"].Value<string>("watch"));
        // watched обязан остаться bool: на нём держатся отметка в гриде и старый клиент
        Assert.Equal(JTokenType.Boolean, item["watched"].Type);
        Assert.Equal(watched, item.Value<bool>("watched"));
    }

    [Fact]
    public void Карточка_без_jut_полей_режима_не_получает()
    {
        var item = new JObject { ["watched"] = false };
        QbitController.JutDecorateListItem(item, new JObject { ["name"] = "Sintel.mp4" }, new Dictionary<string, string>());
        Assert.Null(item["jut"]);
        Assert.False(item.Value<bool>("watched"));
    }

    [Theory]
    [InlineData("jspy-family", "spy-family")]
    [InlineData("jliar-game", "liar-game")]
    [InlineData("t123", null)]
    [InlineData("l7f3a1b2", null)]
    [InlineData("j", null)]
    [InlineData("j../etc/passwd", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Slug_восстанавливается_только_из_jut_ключа(string seriesKey, string expect)
    {
        // Торрентные ключи наружу не отдаём, мусор и traversal режет IsValidSlug
        Assert.Equal(expect, QbitController.JutSlugFromSeriesKey(seriesKey));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
public class JutSuWatchTickNotifyTests
{
    [Fact]
    async public Task Режим_уведомлений_не_ставит_в_очередь_и_не_создаёт_следов_на_диске()
    {
        var (cache, downloads) = JutWatchAccess.Env();
        var before = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));
        JutWatchAccess.Subscribe("liar", before, 0, 0);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));

        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 3)), JutWatchAccess.One(after));
            Assert.Equal(1, res.Value<int>("changed"));
            Assert.Equal(0, res.Value<int>("queued"));
            Assert.Empty(JutWatchAccess.Queue());
            Assert.Empty(JutWatchAccess.Queued());
        }

        string hash = JutNet.Hash("liar");
        Assert.False(File.Exists(JutWatchAccess.MetaPath(hash)), "мета создана — карточка появится в «Загрузках»");
        Assert.False(File.Exists(JutWatchAccess.LocalPath(hash)), "маркер создан");
        Assert.False(File.Exists(JutWatchAccess.PosterPath(hash)), "постер создан");
        Assert.False(Directory.Exists(Path.Combine(downloads, "liar")), "каталог скачивания создан");
        // ПОЯС 1: links/<hash>.json для jut не создаётся никогда
        Assert.False(File.Exists(JutWatchAccess.LinkPath(hash)));
    }

    [Fact]
    async public Task Режим_уведомлений_шлёт_ОДНУ_строку_на_волну()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 0);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));

        using (JutWatchAccess.PinWorker())
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 3)), JutWatchAccess.One(after));

        // qdl 2.111: две серии за тик = ОДНА строка. Раньше здесь стоял цикл по fresh, и
        // за сутки простоя сайта лента получала по строке на каждую вышедшую серию.
        var noti = JutWatchAccess.Noti("liar");
        var n = Assert.Single(noti);
        Assert.Equal("NEW", n.kind);                                 // не null: иначе клиент печатает «скачана»
        Assert.Equal("newwave-s1e3", n.epkey);                       // ключ по МАКСИМАЛЬНОЙ серии волны
        Assert.Equal(JutNet.Hash("liar"), n.hash);
        Assert.Equal(1, n.season);
        Assert.Equal("Вышли новые серии 2–3", n.label);
        Assert.DoesNotContain("качаю", n.label);                     // в notify-режиме не обещаем скачивание
        Assert.DoesNotContain("jut.su", n.label);                    // откуда приехало — не забота зрителя
    }

    [Fact]
    async public Task Уведомление_notify_режима_несёт_ПОКАЗУЕМЫЙ_постер()
    {
        // 🔥 Сквозной тест: связывает ПРОДЮСЕРА уведомления с тем, что увидит лента.
        // До qdl 2.46 продюсер писал псевдо-hash, клиент шёл в /qdl/poster?hash= и получал 404 —
        // на каждой отслеживаемой, но не скачанной серии висел img_broken.svg (жалоба владельца).
        // Каждый кусок по отдельности был «правильным», ломался только стык — тут он и закрыт.
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 0);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));

        using (JutWatchAccess.PinWorker())
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));

        var n = JutWatchAccess.Noti("liar")[0];
        string slug = QbitController.JutSlugFromSeriesKey(n.seriesKey);
        Assert.Equal("liar", slug);

        string url = QbitController.NotiPosterUrl(n.hash, slug);
        Assert.NotNull(url);
        Assert.StartsWith("/qdl/jut/poster?slug=liar", url);
        Assert.DoesNotContain("/qdl/poster?hash=", url);   // файла нет и не будет — это был бы гарантированный 404

        // 🔴 И при этом «фейковый постер» не появился: meta и постер обязаны ездить парой.
        Assert.False(File.Exists(JutWatchAccess.PosterPath(n.hash)), "постер создан");
        Assert.False(File.Exists(JutWatchAccess.MetaPath(n.hash)), "мета создана");
    }

    [Fact]
    async public Task Режим_уведомлений_продвигает_baseline_и_на_втором_тике_молчит()
    {
        // Иначе одно и то же «вышла новая серия» приходило бы каждые сутки.
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 0);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));

        using (JutWatchAccess.PinWorker())
        {
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
            Assert.Equal(2, JutWatchAccess.Find("liar")["known"].Value<int>("count"));
            Assert.Single(JutWatchAccess.Noti("liar"));

            var res2 = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
            Assert.Equal(0, res2.Value<int>("changed"));
            Assert.Single(JutWatchAccess.Noti("liar"));
        }
    }

    [Fact]
    async public Task Режим_уведомлений_не_пишет_в_seen()
    {
        // seen — «уже скачано»; в notify мы ничего не качаем, и после переподписки
        // серии не должны считаться виденными.
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 0);

        using (JutWatchAccess.PinWorker())
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)),
                                      JutWatchAccess.One(JutWatchAccess.Title("liar", true, (1, 1), (1, 2))));

        using var db = new SqlContext();
        // qdl 2.111: ключи дедупа волны («newwave-…») в seen лежать ОБЯЗАНЫ — иначе после
        // ретенции ленты волна пришла бы заново. Инвариант же в другом: ЭПИЗОДНОГО ключа
        // («уже скачано») в notify-режиме не появляется, и переподписка не считает серии
        // виденными. Плюс сама переподписка чистит seen тайтла целиком (JutWatchRemove).
        var keys = db.seen.Where(x => x.seriesKey == "jliar").Select(x => x.epkey).ToList();
        Assert.All(keys, k => Assert.StartsWith("newwave-", k));
        Assert.DoesNotContain("s1e2", keys);
    }

    [Fact]
    async public Task Фильмы_и_ova_в_diff_сезона_не_попадают()
    {
        // Единица слежения — (тайтл, СЕЗОН) по эпизодам: known/fresh считаются только по
        // JutEpKind.Episode. Появившийся на странице фильм подписку не будит.
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("naruuto", JutWatchAccess.Title("naruuto", false, (1, 1)), 0, 0);

        var after = JutWatchAccess.Title("naruuto", false, (1, 1));
        JutWatchAccess.AddSpecial(after, JutEpKind.Film, 3);
        JutWatchAccess.AddSpecial(after, JutEpKind.Ova, 1);

        using (JutWatchAccess.PinWorker())
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false), JutWatchAccess.One(after));

        Assert.Empty(JutWatchAccess.Noti("naruuto"));
        Assert.Empty(JutWatchAccess.Queue());
    }

    [Theory]
    [InlineData(JutEpKind.Film, "film")]
    [InlineData(JutEpKind.Ova, "ova")]
    [InlineData(JutEpKind.Special, "sp")]
    public void Уведомление_о_фильме_и_ova_тоже_NEW(JutEpKind kind, string epkeyPrefix)
    {
        // FILM/OVA/SPECIAL раньше уезжали в клиентскую корзину «скачана» вместе с обычными
        // сериями: вид обязан остаться в label, а kind — быть NEW.
        JutWatchAccess.Env();
        JutWatchAccess.NotifyNew("naruuto", "Наруто", new JutEp { kind = kind, num = 3 }, false);

        var n = Assert.Single(JutWatchAccess.Noti("naruuto"));
        Assert.Equal("NEW", n.kind);
        Assert.Equal("new-" + epkeyPrefix + "3", n.epkey);
        Assert.Equal(-1, n.season);
        Assert.Contains("вышла", n.label);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
public class JutSuWatchTickGrabTests
{
    [Fact]
    async public Task Режим_скачивания_ставит_новые_серии_в_очередь_и_пишет_мету()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));

        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 3)), JutWatchAccess.One(after));
            Assert.Equal(2, res.Value<int>("queued"));

            var q = JutWatchAccess.Queue();
            Assert.Equal(new[] { "s1e2", "s1e3" }, q.Select(x => x.epkey).ToArray());
            Assert.All(q, x => Assert.Equal("liar", x.slug));
            Assert.All(q, x => Assert.Equal("episode", x.kind));

            string hash = JutNet.Hash("liar");
            Assert.True(File.Exists(JutWatchAccess.MetaPath(hash)));
            Assert.Contains("\"jutsu\"", File.ReadAllText(JutWatchAccess.MetaPath(hash)));   // ПОЯС 2
            // маркер «Загрузок» пишет только ЗАВЕРШЁННЫЙ файл
            Assert.False(File.Exists(JutWatchAccess.LocalPath(hash)));
            Assert.False(File.Exists(JutWatchAccess.LinkPath(hash)));                        // ПОЯС 1
        }
    }

    [Fact]
    async public Task Уведомление_в_режиме_скачивания_говорит_что_качает()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);

        using (JutWatchAccess.PinWorker())
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)),
                                      JutWatchAccess.One(JutWatchAccess.Title("liar", true, (1, 1), (1, 2))));

        // qdl 2.111: на автокачке «вышла» зритель не видит — он узнает о серии, когда её
        // можно смотреть. Событие уходит в журнал владельца.
        Assert.Empty(JutWatchAccess.Noti("liar"));
        var e = Assert.Single(Access.Events(QdlEvents.CatWatch));
        Assert.Contains("ставлю в очередь", e.Value<string>("text"));
        Assert.Contains("Вышла новая серия 2", e.Value<string>("text"));
    }

    [Fact]
    async public Task Лежащее_на_диске_не_ставится_в_очередь_но_уведомление_приходит()
    {
        // «Что качать» = diff(сайт, ДИСК), а «о чём уведомить» = diff(сайт, known)
        var (_, downloads) = JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);

        string dir = Path.Combine(downloads, "liar");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "liar.s01e02.1080p.mp4"), "x");

        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));
        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 3)), JutWatchAccess.One(after));
            Assert.Equal(1, res.Value<int>("queued"));
            Assert.Equal(new[] { "s1e3" }, JutWatchAccess.Queue().Select(x => x.epkey).ToArray());
        }
        // qdl 2.111: зрителю на автокачке — ничего; владельцу — ОДНА запись про обе серии
        Assert.Empty(JutWatchAccess.Noti("liar"));
        var e = Assert.Single(Access.Events(QdlEvents.CatWatch));
        Assert.Contains("Вышли новые серии 2–3", e.Value<string>("text"));
    }

    [Fact]
    async public Task Уже_стоящее_в_очереди_не_дублируется()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));

        using (JutWatchAccess.PinWorker())
        {
            JutWatchAccess.SeedQueued("liar", "s1e2");
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 3)), JutWatchAccess.One(after));
            Assert.Equal(1, res.Value<int>("queued"));
            Assert.Equal(new[] { "s1e3" }, JutWatchAccess.Queue().Select(x => x.epkey).ToArray());
        }
    }

    [Fact]
    async public Task Соседний_тайтл_без_новых_серий_меты_не_получает()
    {
        // Регресс: мета писалась по ОБЩЕМУ счётчику queued, то есть соседнему тайтлу,
        // который в очередь ничего не поставил → фантомная карточка в «Загрузках».
        JutWatchAccess.Env();
        var a = JutWatchAccess.Title("aaa", true, (1, 1));
        var b = JutWatchAccess.Title("bbb", true, (1, 1));
        JutWatchAccess.Subscribe("aaa", a, 0, 1);
        JutWatchAccess.Subscribe("bbb", b, 0, 1);

        var aNew = JutWatchAccess.Title("aaa", true, (1, 1), (1, 2));
        Func<string, Task<(JutTitle, string)>> loader = slug =>
            Task.FromResult((slug == "aaa" ? aNew : b, (string)null));

        using (JutWatchAccess.PinWorker())
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false), loader);

        Assert.True(File.Exists(JutWatchAccess.MetaPath(JutNet.Hash("aaa"))));
        Assert.False(File.Exists(JutWatchAccess.MetaPath(JutNet.Hash("bbb"))));
    }

    [Fact]
    async public Task Отказ_по_месту_уведомляет_и_не_двигает_baseline()
    {
        // 🔥 Без этого серия вычёркивалась из скачивания навсегда: baseline уезжал вперёд,
        // а в очередь она не попала. Плюс молчаливый отказ (одна строка в лог) недопустим.
        JutWatchAccess.Env();
        ModInit.conf.jutMinFreeGb = 100_000_000;                 // заведомо больше любого диска
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));

        try
        {
            using (JutWatchAccess.PinWorker())
            {
                var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
                Assert.Equal(0, res.Value<int>("queued"));
                Assert.Empty(JutWatchAccess.Queue());
            }

            Assert.Equal(1, JutWatchAccess.Find("liar")["known"].Value<int>("count"));   // baseline на месте
            Assert.False(File.Exists(JutWatchAccess.MetaPath(JutNet.Hash("liar"))));

            // qdl 2.111: и «вышла», и «нет места» — события владельца (его выбор), лента чиста
            Assert.Empty(JutWatchAccess.Noti("liar"));
            Assert.Single(Access.Events(QdlEvents.CatWatch));
            var sp = Assert.Single(Access.Events(QdlEvents.CatSpace));
            Assert.Contains("не скачаны", sp.Value<string>("text"));
        }
        finally { ModInit.conf.jutMinFreeGb = 1; }
    }

    [Fact]
    async public Task Тревога_о_месте_не_дублируется_за_сутки()
    {
        JutWatchAccess.Env();
        ModInit.conf.jutMinFreeGb = 100_000_000;
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));

        try
        {
            using (JutWatchAccess.PinWorker())
            {
                await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
                await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
            }
            Assert.Single(Access.Events(QdlEvents.CatSpace));   // дедуп по дню — теперь в seen
        }
        finally { ModInit.conf.jutMinFreeGb = 1; }
    }

    [Fact]
    async public Task Апгрейд_notify_в_grab_не_выкачивает_бэклог()
    {
        JutWatchAccess.Env();
        var t = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));
        JutWatchAccess.Subscribe("liar", t, 0, 0);
        QbitController.JutWatchSetModeOnDisk("liar", true, out _, out _);

        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 3)), JutWatchAccess.One(t));
            Assert.Equal(0, res.Value<int>("queued"));
            Assert.Empty(JutWatchAccess.Queue());
        }
        Assert.Empty(JutWatchAccess.Noti("liar"));
    }

    [Fact]
    async public Task После_апгрейда_следующая_новая_серия_качается()
    {
        JutWatchAccess.Env();
        var t = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));
        JutWatchAccess.Subscribe("liar", t, 0, 0);
        QbitController.JutWatchSetModeOnDisk("liar", true, out _, out _);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3));

        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 3)), JutWatchAccess.One(after));
            Assert.Equal(1, res.Value<int>("queued"));
            Assert.Equal(new[] { "s1e3" }, JutWatchAccess.Queue().Select(x => x.epkey).ToArray());
        }
    }

    [Fact]
    async public Task Понижение_grab_в_notify_останавливает_скачивание()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        QbitController.JutWatchSetModeOnDisk("liar", false, out _, out _);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));

        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
            Assert.Equal(0, res.Value<int>("queued"));
            Assert.Empty(JutWatchAccess.Queue());
        }
        Assert.Single(JutWatchAccess.Noti("liar"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    async public Task Старая_запись_без_autoGrab_продолжает_качать_при_любом_конфиге(bool confAuto)
    {
        // 🔴 Главная гарантия совместимости: живая подписка без поля autoGrab (создана до
        // раздвоения режимов) остаётся «качаю» ВСЕГДА. Правка jutWatchAutoGrab в init.conf
        // не имеет права молча выключить скачивание у уже отслеживаемого аниме.
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        var arr = JutWatchAccess.LoadWatch();
        ((JObject)arr[0]).Remove("autoGrab");
        JutWatchAccess.SaveWatch(arr);
        ModInit.conf.jutWatchAutoGrab = confAuto;

        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));
        try
        {
            using (JutWatchAccess.PinWorker())
            {
                var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
                // поля нет → "grab" при ЛЮБОМ значении конфига
                Assert.Equal(1, res.Value<int>("queued"));
                Assert.Equal(new[] { "s1e2" }, JutWatchAccess.Queue().Select(x => x.epkey).ToArray());
            }
            Assert.Empty(JutWatchAccess.Noti("liar"));                  // режим «качаю» → в журнал
            Assert.Single(Access.Events(QdlEvents.CatWatch));
        }
        finally { ModInit.conf.jutWatchAutoGrab = true; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
public class JutSuWatchSeasonTests
{
    [Theory]
    [InlineData(1)]   // grab
    [InlineData(0)]   // notify
    async public Task Переключение_сезона_не_выкачивает_бэклог(int autoGrab)
    {
        // Боевая находка 10.08.2026: пустой baseline нового сезона отправил 13 серий ≈ 6 ГБ
        // в очередь одним тиком. Инвариант обязан жить в ОБОИХ режимах.
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("multi", JutWatchAccess.Title("multi", true, (1, 1), (1, 2)), 1, autoGrab);

        var after = JutWatchAccess.Title("multi", true, (1, 1), (1, 2), (3, 1), (3, 2), (3, 3));
        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false), JutWatchAccess.One(after));
            Assert.Equal(0, res.Value<int>("queued"));
            Assert.Empty(JutWatchAccess.Queue());
        }

        var rec = JutWatchAccess.Find("multi");
        Assert.Equal(3, rec.Value<int>("season"));
        Assert.Equal(3, rec["known"].Value<int>("count"));
        Assert.Equal(new[] { "s3e1", "s3e2", "s3e3" },
                     ((JArray)rec["known"]["keys"]).Select(x => x.Value<string>()).ToArray());

        var noti = JutWatchAccess.Noti("multi");
        var season = Assert.Single(noti);
        Assert.Equal("SEASON", season.kind);
        Assert.Equal("season-3", season.epkey);
        Assert.Empty(noti.Where(x => x.kind == "NEW"));
    }

    [Fact]
    async public Task Уведомление_о_сезоне_не_дублируется()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("multi", JutWatchAccess.Title("multi", true, (1, 1)), 1, 0);
        var after = JutWatchAccess.Title("multi", true, (1, 1), (3, 1));

        using (JutWatchAccess.PinWorker())
        {
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false), JutWatchAccess.One(after));
            // откатить сезон, чтобы переключение случилось второй раз
            var arr = JutWatchAccess.LoadWatch();
            ((JObject)arr[0])["season"] = 1;
            JutWatchAccess.SaveWatch(arr);
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false), JutWatchAccess.One(after));
        }

        Assert.Single(JutWatchAccess.Noti("multi").Where(x => x.kind == "SEASON"));
    }

    [Fact]
    async public Task Переключение_сезона_выключается_настройкой_а_новая_серия_всё_равно_ловится()
    {
        JutWatchAccess.Env();
        ModInit.conf.jutWatchSeasonSwitch = false;
        JutWatchAccess.Subscribe("multi", JutWatchAccess.Title("multi", true, (1, 1)), 1, 1);
        var after = JutWatchAccess.Title("multi", true, (1, 1), (1, 2), (3, 1));

        try
        {
            using (JutWatchAccess.PinWorker())
            {
                var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false), JutWatchAccess.One(after));
                Assert.Equal(1, res.Value<int>("queued"));
                Assert.Equal(new[] { "s1e2" }, JutWatchAccess.Queue().Select(x => x.epkey).ToArray());
            }
            Assert.Equal(1, JutWatchAccess.Find("multi").Value<int>("season"));
            // qdl 2.111: подписка в режиме «качаю» → «вышла» видит только владелец
            Assert.Empty(JutWatchAccess.Noti("multi"));
            Assert.Single(Access.Events(QdlEvents.CatWatch));
        }
        finally { ModInit.conf.jutWatchSeasonSwitch = true; }
    }

    [Fact]
    async public Task После_переключения_новая_серия_нового_сезона_ловится()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("multi", JutWatchAccess.Title("multi", true, (1, 1)), 1, 1);

        using (JutWatchAccess.PinWorker())
        {
            await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false),
                JutWatchAccess.One(JutWatchAccess.Title("multi", true, (1, 1), (2, 1), (2, 2))));
            Assert.Empty(JutWatchAccess.Queue());

            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false),
                JutWatchAccess.One(JutWatchAccess.Title("multi", true, (1, 1), (2, 1), (2, 2), (2, 3))));
            Assert.Equal(1, res.Value<int>("queued"));
            Assert.Equal(new[] { "s2e3" }, JutWatchAccess.Queue().Select(x => x.epkey).ToArray());
        }
    }

    [Fact]
    async public Task Снятая_с_сайта_серия_выравнивает_счётчик_без_уведомлений()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1), (1, 2), (1, 3)), 0, 1);
        var after = JutWatchAccess.Title("liar", true, (1, 1), (1, 2));

        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)), JutWatchAccess.One(after));
            Assert.Equal(0, res.Value<int>("changed"));
            Assert.Empty(JutWatchAccess.Queue());
        }
        Assert.Equal(2, JutWatchAccess.Find("liar")["known"].Value<int>("count"));
        Assert.Empty(JutWatchAccess.Noti("liar"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
public class JutSuWatchTickResilienceTests
{
    [Fact]
    async public Task Выключенный_раздел_никуда_не_ходит()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        ModInit.conf.jutEnable = false;

        int ongoingCalls = 0, titleCalls = 0;
        try
        {
            var res = await JutWatchAccess.Tick(
                () => { ongoingCalls++; return Task.FromResult((true, new Dictionary<string, int>())); },
                _ => { titleCalls++; return Task.FromResult((JutWatchAccess.Title("liar", true), (string)null)); });
            Assert.Equal("disabled", res.Value<string>("skipped"));
        }
        finally { ModInit.conf.jutEnable = true; }

        Assert.Equal(0, ongoingCalls);
        Assert.Equal(0, titleCalls);
    }

    [Fact]
    async public Task Пустой_файл_подписок_не_зовёт_список_онгоингов()
    {
        JutWatchAccess.Env();
        int calls = 0;
        var res = await JutWatchAccess.Tick(
            () => { calls++; return Task.FromResult((true, new Dictionary<string, int>())); },
            JutWatchAccess.One(JutWatchAccess.Title("x", true)));

        Assert.Equal(0, res.Value<int>("watched"));
        Assert.Equal(0, calls);
    }

    [Fact]
    async public Task Совпадение_счётчика_экономит_запрос_страницы_тайтла()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1), (1, 2)), 0, 1);

        int titleCalls = 0;
        var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("liar", 2)),
            _ => { titleCalls++; return Task.FromResult((JutWatchAccess.Title("liar", true), (string)null)); });

        Assert.Equal(0, res.Value<int>("probed"));
        Assert.Equal(0, titleCalls);
    }

    [Fact]
    async public Task Отказ_списка_онгоингов_переводит_в_полный_опрос()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("aaa", JutWatchAccess.Title("aaa", true, (1, 1)), 0, 0);
        JutWatchAccess.Subscribe("bbb", JutWatchAccess.Title("bbb", true, (1, 1)), 0, 0);

        var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false),
            slug => Task.FromResult((JutWatchAccess.Title(slug, true, (1, 1)), (string)null)));

        Assert.Equal(2, res.Value<int>("probed"));
        Assert.False(res.Value<bool>("ongoingList"));
    }

    [Fact]
    async public Task Бюджет_опросов_за_тик_соблюдается()
    {
        JutWatchAccess.Env();
        ModInit.conf.jutWatchTitlesPerTick = 1;
        JutWatchAccess.Subscribe("aaa", JutWatchAccess.Title("aaa", true, (1, 1)), 0, 0);
        JutWatchAccess.Subscribe("bbb", JutWatchAccess.Title("bbb", true, (1, 1)), 0, 0);

        try
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false),
                slug => Task.FromResult((JutWatchAccess.Title(slug, true, (1, 1)), (string)null)));
            Assert.Equal(1, res.Value<int>("probed"));
        }
        finally { ModInit.conf.jutWatchTitlesPerTick = 30; }
    }

    [Fact]
    async public Task Ошибка_страницы_растит_fails_и_не_ломает_остальных()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("aaa", JutWatchAccess.Title("aaa", true, (1, 1)), 0, 0);
        JutWatchAccess.Subscribe("bbb", JutWatchAccess.Title("bbb", true, (1, 1)), 0, 0);

        using (JutWatchAccess.PinWorker())
        {
            var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false),
                slug => slug == "aaa"
                    ? Task.FromResult(((JutTitle)null, "SITE_DOWN"))
                    : Task.FromResult((JutWatchAccess.Title("bbb", true, (1, 1), (1, 2)), (string)null)));

            Assert.Equal(1, res.Value<int>("failed"));
            Assert.Equal(1, res.Value<int>("changed"));
        }

        Assert.Equal(1, JutWatchAccess.Find("aaa").Value<int>("fails"));
        Assert.Equal(0, JutWatchAccess.Find("bbb").Value<int>("fails"));
        Assert.Single(JutWatchAccess.Noti("bbb"));
    }

    [Fact]
    async public Task Удачный_опрос_обнуляет_fails()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 0);
        var arr = JutWatchAccess.LoadWatch();
        ((JObject)arr[0])["fails"] = 7;
        JutWatchAccess.SaveWatch(arr);

        await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false),
            JutWatchAccess.One(JutWatchAccess.Title("liar", true, (1, 1))));

        Assert.Equal(0, JutWatchAccess.Find("liar").Value<int>("fails"));
    }

    [Fact]
    async public Task lastRun_ставится_только_на_удачном_проходе()
    {
        // Урок §AV: безусловный штамп превращал пустую выдачу в «новых серий нет».
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 0);
        var arr = JutWatchAccess.LoadWatch();
        ((JObject)arr[0]).Remove("lastRun");
        ((JObject)arr[0])["slug"] = "../evil";        // ещё и гейт IsValidSlug внутри цикла
        JutWatchAccess.SaveWatch(arr);

        var res = await JutWatchAccess.Tick(JutWatchAccess.Ongoing(false),
            _ => throw new InvalidOperationException("страницу тайтла с мусорным слагом открывать нельзя"));

        Assert.Equal(0, res.Value<int>("probed"));
        Assert.Null(JutWatchAccess.LoadWatch().OfType<JObject>().First()["lastRun"]);
    }

    [Fact]
    public void Догон_срабатывает_после_полутора_периодов()
    {
        JutWatchAccess.Env();
        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 0);

        void SetLastRun(DateTime v)
        {
            var arr = JutWatchAccess.LoadWatch();
            ((JObject)arr[0])["lastRun"] = v;
            JutWatchAccess.SaveWatch(arr);
        }

        SetLastRun(DateTime.UtcNow.AddHours(-30));
        Assert.False(QbitController.JutWatchOverdue(TimeSpan.FromHours(24), out _));

        SetLastRun(DateTime.UtcNow.AddHours(-40));
        Assert.True(QbitController.JutWatchOverdue(TimeSpan.FromHours(24), out var since));
        Assert.True(since > TimeSpan.FromHours(24));
    }

    [Fact]
    public void Догон_без_lastRun_и_на_битом_файле_не_срабатывает()
    {
        JutWatchAccess.Env();
        Assert.False(QbitController.JutWatchOverdue(TimeSpan.FromHours(24), out _));

        Directory.CreateDirectory(Path.GetDirectoryName(JutWatchAccess.WatchPath()));
        File.WriteAllText(JutWatchAccess.WatchPath(), "не json");
        Assert.False(QbitController.JutWatchOverdue(TimeSpan.FromHours(24), out _));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
public class JutSuForgetOnDeleteTests
{
    [Fact]
    public void Удаление_карточки_снимает_подписку_любого_режима()
    {
        // Решение владельца: удалил карточку — слежение выключено, без исключений для notify.
        JutWatchAccess.Env();
        foreach (int mode in new[] { 0, 1 })
        {
            JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, mode);
            QbitController.JutForgetOnDelete("liar");
            Assert.Null(JutWatchAccess.Find("liar"));
        }
    }

    [Fact]
    public void Удаление_карточки_чистит_очередь_и_seen_только_своего_тайтла()
    {
        JutWatchAccess.Env();
        using (JutWatchAccess.PinWorker())
        {
            JutWatchAccess.Subscribe("naruuto", JutWatchAccess.Title("naruuto", true, (1, 1)), 0, 1);
            JutWatchAccess.Subscribe("naruuto-2", JutWatchAccess.Title("naruuto-2", true, (1, 1)), 0, 1);
            JutWatchAccess.SeedQueued("naruuto", "s1e2");
            JutWatchAccess.SeedQueued("naruuto-2", "s1e2");

            using (var db = new SqlContext())
            {
                db.seen.Add(new SeenModel { seriesKey = "jnaruuto", epkey = "s1e1" });
                db.seen.Add(new SeenModel { seriesKey = "jnaruuto-2", epkey = "s1e1" });
                db.seen.Add(new SeenModel { seriesKey = "t123", epkey = "s1e1" });
                db.SaveChanges();
            }

            QbitController.JutForgetOnDelete("naruuto");

            // разделитель ":" в ключе очереди защищает naruuto-2 от чистки по префиксу
            Assert.DoesNotContain("naruuto:s1e2", JutWatchAccess.Queued());
            Assert.Contains("naruuto-2:s1e2", JutWatchAccess.Queued());
            Assert.NotNull(JutWatchAccess.Find("naruuto-2"));

            using var db2 = new SqlContext();
            Assert.Empty(db2.seen.Where(x => x.seriesKey == "jnaruuto").ToList());
            Assert.Single(db2.seen.Where(x => x.seriesKey == "jnaruuto-2").ToList());
            Assert.Single(db2.seen.Where(x => x.seriesKey == "t123").ToList());
        }
    }

    [Fact]
    public void Удаление_карточки_не_трогает_торрентный_watch_json()
    {
        // ПОЯС 1: jut-контур не читает и не пишет общий watch.json
        var (cache, _) = JutWatchAccess.Env();
        string torrent = Path.Combine(cache, "watch.json");
        string body = "[{\"hash\":\"" + new string('a', 40) + "\",\"link\":\"magnet:?x\"}]";
        File.WriteAllText(torrent, body);

        JutWatchAccess.Subscribe("liar", JutWatchAccess.Title("liar", true, (1, 1)), 0, 1);
        QbitController.JutForgetOnDelete("liar");

        Assert.Equal(body, File.ReadAllText(torrent));
    }
}
