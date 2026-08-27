using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Сторож и непогашенный долг (qdl 2.77).
//
// Гейты «нет новых серий → выходим» существуют, чтобы БЭКЛОГ не поехал в очередь:
// у сериала, где владелец скачал 2 сезона из 5, сверка с диском потянула бы всё.
// Но серия, поставленная прошлым тиком и потерянная рестартом, уже сидит в baseline
// и в fresh не попадёт НИКОГДА — до 2.77 она пропадала молча.
//
// 🔴 Развязка: долг берётся ИСКЛЮЧИТЕЛЬНО из журнала намерений. Бэклога там нет
// по построению — записи создаёт только явное действие владельца или прошлый тик.
// Первые два теста стерегут именно это и обязаны быть зелёными ВСЕГДА.
// ─────────────────────────────────────────────────────────────────────────────
public class WatchDebtTests
{
    // ── бэклог не едет ────────────────────────────────────────────────────

    [Fact]
    public void Сторож_XSMART_не_качает_бэклог()
    {
        // Подписка в режиме «качаю», на сайте 10 серий, на диске НИ ОДНОЙ, долгов нет.
        // Правильный ответ — ноль: эти серии владелец не заказывал.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "300", Enumerable.Range(1, 10).Select(i => ("s1", 1, i)).ToArray());
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        var res = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 10))).Result;

        Assert.Equal(0, res.Value<int?>("queued"));
        Assert.Empty(XsAccess.Queue());
        Assert.False(DownloadWants.Xsmart.HasTitle("3-300"));
    }

    [Fact]
    public void Сторож_jut_не_качает_бэклог_при_снятом_knownKeys()
    {
        // 🔴 Главный тест правки JutSuWatch: из toGrab убрано `!knownKeys.Contains(...)`,
        // которое обезвреживало сверку с диском. Если бы вместо журнала намерений долг
        // строился как diff с диском, здесь встали бы все 10 серий.
        JutWatchAccess.Env();
        using var pin = JutWatchAccess.PinWorker();

        var t = JutWatchAccess.Title("backlog-anime", true,
            Enumerable.Range(1, 10).Select(i => (1, i)).ToArray());
        JutWatchAccess.Subscribe("backlog-anime", t, 1, autoGrab: 1);

        // ongoingOk=false → mustProbe true, страница тайтла реально разбирается
        var res = JutWatchAccess.Tick(JutWatchAccess.Ongoing(false), JutWatchAccess.One(t)).Result;

        Assert.Equal(0, res.Value<int?>("queued"));
        Assert.Empty(JutWatchAccess.Queue());
        Assert.False(DownloadWants.Jut.HasTitle("backlog-anime"));
    }

    // ── долг переживает рестарт ───────────────────────────────────────────

    [Fact]
    public void Долг_XSMART_переживает_рестарт_несмотря_на_сдвинутый_baseline()
    {
        // 🔥 Ровно тот канал потери, который не закрывает никакая реконсиляция: baseline
        // уехал вперёд СРАЗУ после постановки, .part на диске не появился, и на следующем
        // тике серия уже не fresh.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "301", ("s1", 1, 1), ("s1", 1, 2));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        var first = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 3))).Result;
        Assert.Equal(1, first.Value<int?>("queued"));

        // baseline действительно уехал — серия больше не «новая»
        var keys = (JArray)XsAccess.Rec("3-301")["known"]["keys"];
        Assert.Contains("s1e3", keys.Select(x => x.Value<string>()));

        WantsAccess.RestartXsmart();

        var second = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 3))).Result;

        // Долг поднимает СВИП в начале тика — он дешевле и не ждёт разбора источника;
        // до ветки postановки дело уже не доходит (ключ в _xsQueued), поэтому queued=0.
        Assert.Equal(1, second.Value<int?>("swept"));
        Assert.Contains(XsAccess.Queue(), x => x.epkey == "s1e3");
        // но «изменением источника» второй проход не считается — новых серий не выходило
        Assert.Equal(0, second.Value<int?>("changed"));
    }

    [Fact]
    public void Долг_jut_переживает_рестарт_несмотря_на_сдвинутый_baseline()
    {
        JutWatchAccess.Env();
        using var pin = JutWatchAccess.PinWorker();

        var t2 = JutWatchAccess.Title("debt-anime", true, (1, 1), (1, 2));
        JutWatchAccess.Subscribe("debt-anime", t2, 1, autoGrab: 1);

        var t3 = JutWatchAccess.Title("debt-anime", true, (1, 1), (1, 2), (1, 3));
        var first = JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("debt-anime", 3)),
                                        JutWatchAccess.One(t3)).Result;
        Assert.Equal(1, first.Value<int?>("queued"));

        WantsAccess.RestartJut();

        // счётчик онгоингов теперь СОВПАДАЕТ с baseline — mustProbe пропустил бы тайтл,
        // если бы не дизъюнкт «есть непогашенный долг»
        var second = JutWatchAccess.Tick(JutWatchAccess.Ongoing(true, ("debt-anime", 3)),
                                         JutWatchAccess.One(t3)).Result;

        Assert.Equal(1, second.Value<int?>("swept"));
        Assert.Contains(JutWatchAccess.Queue(), x => x.epkey == "s1e3");
    }

    [Fact]
    public void Долг_не_уведомляет_повторно()
    {
        // Висящий долг не должен писать строку в ленту каждые сутки, пока портал лежит.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "302", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        _ = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 2))).Result;

        int after1 = NotiCount("x3-302", "NEW");
        WantsAccess.RestartXsmart();

        _ = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 2))).Result;

        Assert.Equal(after1, NotiCount("x3-302", "NEW"));
    }

    // ── baselineHold у XSMART ─────────────────────────────────────────────

    [Fact]
    public void Нехватка_места_не_двигает_baseline_XSMART()
    {
        // 🔴 До 2.77 у XSMART baselineHold не было вовсе: уведомили о нехватке места
        // и провалились в сдвиг baseline. Серия исключалась из fresh навсегда, в очередь
        // не попадала, .part не оставляла — терялась насовсем. У jut это давно закрыто.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "303", ("s1", 1, 1), ("s1", 1, 2));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);
        ModInit.conf.xsmartMinFreeGb = 1_000_000;         // места не хватит гарантированно
        try
        {
            var res = QbitController.XsmartWatchTick(
                loadSeasons: XsAccess.Seasons(("s1", 1)),
                loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 3))).Result;

            Assert.Equal(0, res.Value<int?>("queued"));
            var keys = ((JArray)XsAccess.Rec("3-303")["known"]["keys"]).Select(x => x.Value<string>()).ToList();
            Assert.DoesNotContain("s1e3", keys);          // baseline держится
            Assert.Equal(1, NotiCount("x3-303", "NOSPACE"));
        }
        finally { ModInit.conf.xsmartMinFreeGb = 1; }
    }

    // ── режимы и намерения ────────────────────────────────────────────────

    [Fact]
    public void Режим_notify_не_создаёт_намерений()
    {
        // «Только уведомляю» не заказывает скачивание — значит и долга у него быть не может,
        // иначе свип начал бы качать за спиной у режима.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "304", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 0);

        _ = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 2))).Result;

        Assert.Empty(XsAccess.Queue());
        Assert.False(DownloadWants.Xsmart.HasTitle("3-304"));
    }

    // ── догон пропущенных тиков ───────────────────────────────────────────

    [Fact]
    public void Тик_XSMART_штампует_lastRun_только_на_удачном_проходе()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var t = XsAccess.Title(3, "305", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);
        Assert.Null(XsAccess.Rec("3-305")["lastRun"]);

        _ = QbitController.XsmartWatchTick(
            loadSeasons: XsAccess.Seasons(("s1", 1)),
            loadEpisodes: XsAccess.Episodes(XsAccess.Eps("s1", 1, 1, 1))).Result;

        Assert.NotNull(XsAccess.Rec("3-305")["lastRun"]);
    }

    [Fact]
    public void Догон_XSMART_считается_по_lastRun()
    {
        // Без догона каждый рестарт сдвигал бы суточную проверку на новые сутки, а рестарт
        // тут событие штатное. Порог — 1.5 периода, как у jut.
        XsAccess.Env();

        var t = XsAccess.Title(3, "306", ("s1", 1, 1));
        QbitController.XsmartWatchUpsert(t, "s1", autoGrab: 1);

        Assert.False(QbitController.XsmartWatchOverdue(TimeSpan.FromHours(24), out _),
                     "без штампа догона быть не может — иначе он срабатывал бы на каждом старте");

        var arr = QbitController.XsmartLoadWatch();
        foreach (var rec in arr.OfType<JObject>()) rec["lastRun"] = DateTime.UtcNow.AddHours(-40);
        QbitController.XsmartSaveWatch(arr);

        Assert.True(QbitController.XsmartWatchOverdue(TimeSpan.FromHours(24), out var since));
        Assert.InRange(since.TotalHours, 39, 41);
        Assert.False(QbitController.XsmartWatchOverdue(TimeSpan.FromHours(72), out _));
    }

    static int NotiCount(string seriesKey, string kind)
    {
        using var db = new SqlContext();
        return db.noti.Count(x => x.seriesKey == seriesKey && x.kind == kind);
    }
}
