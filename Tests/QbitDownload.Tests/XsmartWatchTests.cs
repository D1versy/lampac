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
// Слежение за новыми сериями XSMART: ДВА РЕЖИМА подписки.
//   "notify" (autoGrab:false) — с карточки тайтла: уведомляем, НЕ качаем.
//   "grab"   (autoGrab:true)  — из «Загрузок»: уведомляем И качаем.
//
// Тик прогоняется БЕЗ СЕТИ через параметры-сеймы XsmartWatchTick(loadSeasons, loadEpisodes):
// у XsmartNet своя фабрика HttpClient без места под HttpMessageHandler.
//
// ⚠️ Воркер скачивания пиним (_xsWorker = 1), иначе постановка в очередь утащит тест
// в реальную сеть с ретраями 5/15/60 сек.
// ─────────────────────────────────────────────────────────────────────────────

static class XsAccess
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    static FieldInfo F(string name) =>
        C.GetField(name, SF) ?? throw new MissingFieldException("QbitController." + name);

    public sealed class QItem { public string sref, epkey; }

    public static List<QItem> Queue()
    {
        var list = new List<QItem>();
        foreach (var it in (IEnumerable)F("_xsQueue").GetValue(null))
        {
            var t = it.GetType();
            var ep = t.GetField("ep").GetValue(it);
            list.Add(new QItem
            {
                sref = (string)t.GetField("sref").GetValue(it),
                epkey = (string)ep.GetType().GetProperty("epkey").GetValue(ep)
            });
        }
        return list;
    }

    public static void ClearQueues()
    {
        var queue = F("_xsQueue").GetValue(null);
        var tryDequeue = queue.GetType().GetMethod("TryDequeue");
        object[] args = { null };
        while ((bool)tryDequeue.Invoke(queue, args)) args[0] = null;

        var queued = F("_xsQueued").GetValue(null);
        queued.GetType().GetMethod("Clear").Invoke(queued, null);
        // Журнал намерений — тоже статика, и без сброса долг одного кейса воскресает в другом.
        DownloadWants.Xsmart.Reset(flush: false);
        // Job'ы — тоже статика: с qdl 2.114 их читают /qdl/list и /qdl/progress (карточка «в полёте»),
        // и running-job прошлого кейса всплыл бы карточкой в чужом тесте.
        JobClear();
    }

    /// <summary>
    /// Job тайтла напрямую — как её видит воркер посреди закачки (qdl 2.114: карточка «в полёте»).
    /// Сам воркер в тестах запинен, поэтому состояние выставляем руками.
    /// </summary>
    public static void JobSet(string sref, string state, long done = 0, long total = 0,
                              int seg = 0, int segTotal = 0, int fileDone = 0, int filesTotal = 1)
    {
        var jobs = F("_xsJobs").GetValue(null);
        var jobType = C.GetNestedType("XsmartGrabJob", BindingFlags.NonPublic | BindingFlags.Public)
                      ?? throw new MissingMemberException("QbitController.XsmartGrabJob");
        const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        object job = Activator.CreateInstance(jobType);
        void Set(string f, object v) => (jobType.GetField(f, IF) ?? throw new MissingFieldException(f)).SetValue(job, v);
        Set("state", state); Set("done", done); Set("total", total);
        Set("seg", seg); Set("segTotal", segTotal); Set("fileDone", fileDone); Set("filesTotal", filesTotal);
        jobs.GetType().GetProperty("Item").SetValue(jobs, job, new object[] { sref });
    }

    public static void JobClear()
    {
        var jobs = F("_xsJobs").GetValue(null);
        jobs.GetType().GetMethod("Clear").Invoke(jobs, null);
    }

    /// <summary>Воркер «занят»: постановка в очередь не поднимет реальную качалку.</summary>
    public static IDisposable PinWorker()
    {
        F("_xsWorker").SetValue(null, 1);
        return new Unpin();
    }

    sealed class Unpin : IDisposable
    {
        public void Dispose()
        {
            ClearQueues();
            F("_xsWorker").SetValue(null, 0);
        }
    }

    public static (string cache, string downloads) Env()
    {
        string cache = TestEnv.FreshCache();
        string downloads = Path.Combine(cache, "downloads-xsmart");
        Directory.CreateDirectory(downloads);
        ModInit.conf.xsmartEnable = true;
        ModInit.conf.xsmartDownloadsPath = downloads;
        ModInit.conf.xsmartWatchAutoGrab = true;
        ModInit.conf.xsmartWatchSeasonSwitch = true;
        ModInit.conf.xsmartMinFreeGb = 1;
        using var db = new SqlContext();
        db.Database.EnsureCreated();
        return (cache, downloads);
    }

    public static XsmartTitle Title(int cat, string id, params (string sid, int sno, int epno)[] eps)
    {
        var t = new XsmartTitle { cat = cat, id = id, title = "Тайтл " + id, series = true, source = "3" };
        foreach (var (sid, sno, epno) in eps)
            t.items.Add(new XsmartEp
            {
                kind = XsmartKind.Episode, seasonId = sid, seasonNo = sno,
                epNo = epno, epId = sid + "-" + epno
            });
        return t;
    }

    public static List<XsmartEp> Eps(string sid, int sno, int from, int to, bool playable = true)
    {
        var list = new List<XsmartEp>();
        for (int i = from; i <= to; i++)
            list.Add(new XsmartEp
            {
                kind = XsmartKind.Episode, seasonId = sid, seasonNo = sno,
                epNo = i, epId = sid + "-" + i, playable = playable
            });
        return list;
    }

    public static Func<int, string, string, Task<List<(string id, int number)>>> Seasons(
        params (string id, int number)[] items)
        => (c, i, s) => Task.FromResult(items.ToList());

    public static Func<int, string, string, int, string, Task<List<XsmartEp>>> Episodes(List<XsmartEp> eps)
        => (c, i, sid, sno, src) => Task.FromResult(eps.Where(e => e.seasonId == sid).ToList());

    public static JObject Rec(string sref) => QbitController.XsmartLoadWatch().OfType<JObject>()
        .FirstOrDefault(x => x.Value<string>("ref") == sref);
}

public class XsmartWatchTests
{
    [Fact]
    public void Подписка_берёт_последний_сезон_и_ставит_baseline_по_текущему()
    {
        XsAccess.Env();
        var t = XsAccess.Title(3, "100",
            ("s1", 1, 1), ("s1", 1, 2),
            ("s2", 2, 1), ("s2", 2, 2), ("s2", 2, 3));

        var r = QbitController.XsmartWatchUpsert(t, null, autoGrab: 1);

        // Сезон не назвали — берём ПОСЛЕДНИЙ вышедший: новые серии бывают только там.
        Assert.Equal("s2", r.seasonId);
        Assert.Equal(2, r.seasonNo);
        // «Следить» качает только БУДУЩИЕ серии — уже вышедшие уходят в baseline.
        Assert.Equal(3, r.baseline);
        Assert.Equal("grab", r.mode);
    }

    [Fact]
    public void Режим_notify_уведомляет_но_НЕ_ставит_в_очередь()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "101", ("s1", 1, 1), ("s1", 1, 2));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 0);

        // вышла третья серия
        var res = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 3))).Result;

        Assert.Equal(1, res.Value<int?>("changed"));
        Assert.Equal(0, res.Value<int?>("queued"));
        Assert.Empty(XsAccess.Queue());
    }

    [Fact]
    public void Режим_grab_ставит_новую_серию_в_очередь()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "102", ("s1", 1, 1), ("s1", 1, 2));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        var res = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 3))).Result;

        Assert.Equal(1, res.Value<int?>("queued"));
        var q = XsAccess.Queue();
        Assert.Single(q);
        Assert.Equal("3-102", q[0].sref);
        Assert.Equal("s1e3", q[0].epkey);
    }

    [Fact]
    public void Переключение_сезона_НЕ_выкачивает_бэклог()
    {
        // 🔥 Боевая находка на jut (2026-08-10): подписка на сезон 1 переключилась на сезон 3
        // и поставила в очередь 13 серий ≈ 6 ГБ, потому что baseline нового сезона обнулялся.
        // Политика «Следить качает только БУДУЩИЕ серии» обязана переживать переключение.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "103", ("s1", 1, 1), ("s1", 1, 2));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        // вышел целый второй сезон из 10 серий
        var res = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1), ("s2", 2)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s2", 2, 1, 10))).Result;

        Assert.Equal(0, res.Value<int?>("queued"));
        Assert.Empty(XsAccess.Queue());

        var rec = XsAccess.Rec("3-103");
        Assert.Equal("s2", rec.Value<string>("seasonId"));
        Assert.Equal(10, rec.Value<JObject>("known").Value<int?>("count"));   // baseline = текущее, а не пусто
    }

    [Fact]
    public void После_переключения_следующая_серия_нового_сезона_уже_качается()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "104", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1), ("s2", 2)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s2", 2, 1, 10))).Wait();

        var res = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1), ("s2", 2)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s2", 2, 1, 11))).Result;

        Assert.Equal(1, res.Value<int?>("queued"));
        Assert.Equal("s2e11", XsAccess.Queue().Single().epkey);
    }

    [Fact]
    public void Baseline_двигается_и_в_notify_иначе_уведомляем_каждые_сутки()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "105", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 0);

        var eps = XsAccess.Eps("s1", 1, 1, 2);
        var first = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)), loadEpisodes: XsAccess.Episodes(eps)).Result;
        var second = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)), loadEpisodes: XsAccess.Episodes(eps)).Result;

        Assert.Equal(1, first.Value<int?>("changed"));
        Assert.Equal(0, second.Value<int?>("changed"));   // второй проход молчит
    }

    [Fact]
    public void Смена_режима_не_трогает_baseline()
    {
        // ⚠️ Повторной подпиской режим менять нельзя: она сбрасывает baseline на текущее
        // состояние, и серия, вышедшая между тиком и нажатием, была бы проглочена навсегда.
        XsAccess.Env();
        var t = XsAccess.Title(3, "106", ("s1", 1, 1), ("s1", 1, 2));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 0);
        int before = XsAccess.Rec("3-106").Value<JObject>("known").Value<int>("count");

        Assert.True(QbitController.XsmartWatchSetModeOnDisk("3-106", true, out string mode, out _));

        Assert.Equal("grab", mode);
        Assert.Equal(before, XsAccess.Rec("3-106").Value<JObject>("known").Value<int>("count"));
    }

    [Fact]
    public void Режим_без_явного_параметра_сохраняется_а_не_берётся_из_конфига()
    {
        XsAccess.Env();
        ModInit.conf.xsmartWatchAutoGrab = true;

        // повторный вызов БЕЗ параметра не должен молча включить автоскачивание
        Assert.False(QbitController.XsmartAutoGrabFor(prev: false, autoGrab: -1));
        Assert.True(QbitController.XsmartAutoGrabFor(prev: true, autoGrab: -1));
        // явный параметр UI побеждает всё
        Assert.False(QbitController.XsmartAutoGrabFor(prev: true, autoGrab: 0));
        // записи ещё нет — берём дефолт конфига
        Assert.True(QbitController.XsmartAutoGrabFor(prev: null, autoGrab: -1));
    }

    [Fact]
    public void Неиграбельные_серии_в_очередь_не_ставятся()
    {
        // playable:false — узел ветки VCDN, который на нашей подписке не резолвится вовсе.
        // Поставить такое значит гарантированно получить NO_STREAM и «ошибку» на пустом месте.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "107", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        var res = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 3, playable: false))).Result;

        Assert.Equal(1, res.Value<int?>("changed"));     // уведомить — уведомили
        Assert.Equal(0, res.Value<int?>("queued"));      // а качать нечего
    }

    [Fact]
    public void Уже_лежащее_на_диске_повторно_не_качается()
    {
        // 🔥 Что КАЧАТЬ — это diff(источник, ДИСК), а не diff с baseline: рестарт между
        // постановкой в очередь и завершением файла иначе терял бы серию навсегда.
        var (_, downloads) = XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        string dir = Path.Combine(downloads, "3-108");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "3-108.s01e03.1080p.mp4"), "x");
        QbitController.XsmartDropAllDiskKeys();

        var t = XsAccess.Title(3, "108", ("s1", 1, 1), ("s1", 1, 2));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        var res = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 3))).Result;

        Assert.Equal(0, res.Value<int?>("queued"));
        Assert.Empty(XsAccess.Queue());
    }

    [Fact]
    public void Источник_молчит_подписка_не_ломается_а_считает_отказы()
    {
        XsAccess.Env();
        var t = XsAccess.Title(3, "109", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        var res = QbitController.XsmartWatchTick(
            loadSeasons: (c, i, s) => Task.FromResult<List<(string id, int number)>>(null),
            loadEpisodes: XsAccess.Episodes(new List<XsmartEp>())).Result;

        Assert.Equal(1, res.Value<int?>("failed"));
        Assert.Equal(1, XsAccess.Rec("3-109").Value<int?>("fails"));
    }

    [Fact]
    public void Удаление_загрузки_снимает_подписку()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "110", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);
        Assert.NotNull(XsAccess.Rec("3-110"));

        QbitController.XsmartForgetOnDelete("3-110");

        Assert.Null(XsAccess.Rec("3-110"));
    }

    // ── ref из ключа серии: тап по уведомлению должен открывать раздел XSMART ──
    // В режиме "notify" карточки в «Загрузках» НЕТ вовсе, а hash необратим — без ref
    // тап уходил в торрентную ветку и открывал плеер по мёртвому URL (та же болезнь,
    // которую у jut.su лечили полем slug).

    [Fact]
    public void Ref_из_ключа_серии_разбирается_обратно()
    {
        Assert.Equal("3-109", QbitController.XsmartRefFromSeriesKey("x3-109"));
    }

    [Theory]
    [InlineData("t603692")]        // торрентный по tmdb id
    [InlineData("l4f3a2b1c")]      // торрентный по fnv(link)
    [InlineData("jliar-game")]     // jut.su по slug
    [InlineData("x")]
    [InlineData("x3")]             // без id
    [InlineData("x3-109-2")]       // лишний дефис: ref строго cat-id
    [InlineData("xабв-109")]       // cat не число
    [InlineData("")]
    [InlineData(null)]
    public void Чужие_и_битые_ключи_наружу_не_идут(string key)
    {
        // Гейт нужен именно здесь: поле уходит в /qdl/notifications ВСЕМ клиентам,
        // и торрентный ключ, просочившийся как xsmart-ref, увёл бы тап не туда.
        Assert.Null(QbitController.XsmartRefFromSeriesKey(key));
    }
}
