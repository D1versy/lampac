using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Очередь скачивания, пережившая рестарт (DownloadWants.cs, qdl 2.77).
//
// 🔥 Цена вопроса — боевой случай 28.08.2026: из восьми поставленных серий XSMART рестарт
// контейнера пережила ОДНА, та, у которой на диске остался хвост .parts. Реконсиляция
// подбирает только .part/.parts, а элемент, не дошедший до первого байта, следов на диске
// не оставляет вообще; статус при этом честно показывал «done».
//
// ⚠️ Воркеры пиним, иначе постановка в очередь утащит тест в реальную сеть.
// ─────────────────────────────────────────────────────────────────────────────

static class WantsAccess
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    static FieldInfo F(string name) =>
        C.GetField(name, SF) ?? throw new MissingFieldException("QbitController." + name);

    public static string XsPath() => Path.Combine(XsmartNet.DataDir(), "queue.json");
    public static string JutPath() => Path.Combine(JutNet.JutDataDir(), "queue.json");

    /// <summary>
    /// Честная симуляция рестарта процесса: РАМ-состояние очереди и журнала стирается,
    /// файл на диске остаётся. Ключ JsonStore тоже забываем — иначе Load поднял бы
    /// значение из горячего слоя и тест не проверил бы диск вовсе.
    /// </summary>
    public static void RestartXsmart()
    {
        var queue = F("_xsQueue").GetValue(null);
        var tryDequeue = queue.GetType().GetMethod("TryDequeue");
        object[] args = { null };
        while ((bool)tryDequeue.Invoke(queue, args)) args[0] = null;

        ((HashSet<string>)F("_xsQueued").GetValue(null)).Clear();
        var jobs = F("_xsJobs").GetValue(null);
        jobs.GetType().GetMethod("Clear").Invoke(jobs, null);
        var gens = F("_xsGen").GetValue(null);
        gens.GetType().GetMethod("Clear").Invoke(gens, null);

        QbitController.XsmartDropAllDiskKeys();
        JsonStore.Flush();               // грязное из write-behind обязано доехать до диска
        JsonStore.Forget(XsPath());
        DownloadWants.Xsmart.Reset(flush: false);
    }

    public static void RestartJut()
    {
        var queue = F("_jutQueue").GetValue(null);
        queue.GetType().GetMethod("Clear").Invoke(queue, null);
        ((HashSet<string>)F("_jutQueued").GetValue(null)).Clear();
        var jobs = F("_jutJobs").GetValue(null);
        jobs.GetType().GetMethod("Clear").Invoke(jobs, null);
        var gens = F("_jutGen").GetValue(null);
        gens.GetType().GetMethod("Clear").Invoke(gens, null);

        QbitController.JutDropAllDiskKeys();
        JsonStore.Flush();
        JsonStore.Forget(JutPath());
        DownloadWants.Jut.Reset(flush: false);
    }

    /// <summary>Файл журнала прямо с диска, мимо горячего слоя.</summary>
    public static JObject OnDisk(string path)
        => File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;

    public static List<string> Keys(JObject store)
        => (store?["items"] as JObject)?.Properties().Select(p => p.Name).ToList() ?? new List<string>();

    /// <summary>Фаза 1 напрямую — то же, что делают все шесть точек постановки.</summary>
    public static void CommitXs(string sref, int cat, string id, params XsmartEp[] eps)
        => QbitController.XsmartWantsCommit(sref, cat, id, "3", "Тайтл " + id, eps, "manual");

    public static XsmartEp Ep(string sid, int sno, int epno)
        => new XsmartEp { kind = XsmartKind.Episode, seasonId = sid, seasonNo = sno, epNo = epno, epId = sid + "-" + epno };

    /// <summary>Контроллер с пустым HttpContext: экшены трогают заголовки ответа.</summary>
    public static QbitController Ctrl()
        => new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    public static string Body(ActionResult r) => ((ContentResult)r).Content;

    /// <summary>Устарел ли первый элемент очереди относительно ТЕКУЩЕГО поколения тайтла.</summary>
    public static bool FirstStale()
    {
        var queue = F("_xsQueue").GetValue(null);
        object first = ((IEnumerable)queue).Cast<object>().First();
        return (bool)Access.Call("XsmartStale", first);
    }

    public static string JutQueueTitleRu(int idx)
    {
        var queue = F("_jutQueue").GetValue(null);
        object it = ((IEnumerable)queue).Cast<object>().ElementAt(idx);
        return (string)it.GetType()
            .GetField("titleRu", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .GetValue(it);
    }

    public static void Touch(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "x");
    }
}

public class QueuePersistTests
{
    // ── ядро: боевой случай ───────────────────────────────────────────────

    [Fact]
    public void Восемь_серий_переживают_рестарт_без_единого_part()
    {
        // 🔥 Дословно инцидент 28.08.2026. Ни одного .part на диске: реконсиляции опереться
        // не на что, и до персистентного журнала все восемь исчезали молча.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var eps = Enumerable.Range(9, 8).Select(i => WantsAccess.Ep("28260", 2, i)).ToArray();
        WantsAccess.CommitXs("3-5059049", 3, "5059049", eps);

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        var q = XsAccess.Queue();
        Assert.Equal(8, q.Count);
        Assert.All(q, x => Assert.Equal("3-5059049", x.sref));
        // Порядок восстановления = порядок постановки: иначе владелец увидит s2e16 раньше s2e9.
        Assert.Equal(Enumerable.Range(9, 8).Select(i => "s2e" + i), q.Select(x => x.epkey));
    }

    [Fact]
    public void Намерение_ложится_на_диск_до_первого_байта()
    {
        // Постановка write-through: падение сразу после неё восстановимо, потому что окно
        // дебаунса (200 мс) тут недопустимо — на хук остановки полагаться нельзя.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        WantsAccess.CommitXs("3-777", 3, "777", WantsAccess.Ep("s1", 1, 4));

        var onDisk = WantsAccess.OnDisk(WantsAccess.XsPath());
        Assert.NotNull(onDisk);
        Assert.Contains("3-777:s1e4", WantsAccess.Keys(onDisk));
    }

    [Fact]
    public void Ошибка_после_ретраев_НЕ_теряет_серию()
    {
        // 🔥 Тест, отличающий журнал намерений от снимка очереди. Резолв ретраев не делает
        // вовсе: UPSTREAM_DOWN убивает элемент с первой попытки, и снимок аккуратно снял бы
        // все восемь через три секунды после рестарта — потеря та же, только позже.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var ep = WantsAccess.Ep("s1", 1, 5);
        WantsAccess.CommitXs("3-778", 3, "778", ep);

        // элемент отработал неудачно — ровно то, что делает finally воркера
        var item = Activator.CreateInstance(typeof(QbitController)
            .GetNestedType("XsmartGrabItem", BindingFlags.NonPublic));
        var it = item.GetType();
        const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        it.GetField("cat", IF).SetValue(item, 3);
        it.GetField("id", IF).SetValue(item, "778");
        it.GetField("sref", IF).SetValue(item, "3-778");
        it.GetField("ep", IF).SetValue(item, ep);
        Access.Call("XsmartDoneWith", item);

        var stat = DownloadWants.Xsmart.Stat("3-778");
        Assert.Equal(1, stat.owed + stat.parked);           // запись жива, а не снята

        // и переживает рестарт: снимок очереди на этом месте отдал бы пустоту
        WantsAccess.RestartXsmart();
        DownloadWants.Xsmart.Load();
        Assert.True(DownloadWants.Xsmart.Has("3-778", "s1e5"));

        // сразу в очередь она НЕ идёт — работает бэкофф (первая неудача = +5 минут),
        // и это ровно то, чем журнал отличается от бесконечного перебора
        QbitController.XsmartWantsRestore();
        Assert.Empty(XsAccess.Queue());
    }

    [Fact]
    public void Восстановление_нормализует_поколение()
    {
        // 🔴 _xsGen после рестарта пуст. Сохранённое поколение > 0 сделало бы ВСЕ восстановленные
        // элементы XsmartStale, и воркер молча выкинул бы их — потеря выглядела бы как успех.
        // Поля gen в JSON нет физически; проверяем, что подсунуть его невозможно.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        WantsAccess.CommitXs("3-779", 3, "779", WantsAccess.Ep("s1", 1, 1));
        JsonStore.Flush();

        var raw = WantsAccess.OnDisk(WantsAccess.XsPath());
        var rec = (JObject)raw["items"]["3-779:s1e1"];
        Assert.Null(rec["gen"]);                                  // не сохраняем
        rec["gen"] = 7;                                           // и даже подсунутое игнорируем
        File.WriteAllText(WantsAccess.XsPath(), raw.ToString());

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        var q = XsAccess.Queue();
        Assert.Single(q);
        Assert.False(WantsAccess.FirstStale(), "элемент обязан быть актуальным для текущего поколения");
    }

    [Fact]
    public void Готовый_файл_снимает_намерение_даже_другого_качества()
    {
        // Ключ диска качества не различает (это сознательно — s1e5 в 720p и 1080p одна серия).
        // Значит лежащий 360p закрывает обычное намерение: качать второй раз нечего.
        var (_, downloads) = XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        WantsAccess.CommitXs("3-780", 3, "780", WantsAccess.Ep("s1", 1, 3));
        WantsAccess.Touch(Path.Combine(downloads, "3-780"), "3-780.s01e03.360p.mp4");

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        Assert.Empty(XsAccess.Queue());
        Assert.False(DownloadWants.Xsmart.HasTitle("3-780"));
    }

    [Fact]
    public void Отмена_снимает_намерение_и_рестарт_не_воскрешает()
    {
        // 🔴 Самый дорогой класс ошибок — «удалил, а оно вернулось». Снятие обязано жить
        // ровно там, где двигается поколение, и под тем же локом.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var eps = Enumerable.Range(1, 4).Select(i => WantsAccess.Ep("s1", 1, i)).ToArray();
        WantsAccess.CommitXs("3-781", 3, "781", eps);
        WantsAccess.Ctrl().XsmartDownloadCancel(3, "781");

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        Assert.Empty(XsAccess.Queue());
    }

    [Fact]
    public void Удаление_карточки_снимает_намерение()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        WantsAccess.CommitXs("3-782", 3, "782", WantsAccess.Ep("s1", 1, 1));
        QbitController.XsmartForgetOnDelete("3-782");

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        Assert.Empty(XsAccess.Queue());
    }

    [Fact]
    public void Припаркованная_серия_не_крутит_воркер()
    {
        // Без порога снятая с портала серия переставлялась бы каждым свипом вечно.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();
        ModInit.conf.xsmartWantMaxTries = 3;

        WantsAccess.CommitXs("3-783", 3, "783", WantsAccess.Ep("s1", 1, 1));
        for (int i = 0; i < 3; i++) DownloadWants.Xsmart.Fail("3-783", "s1e1", "NO_STREAM");

        Assert.Empty(DownloadWants.Xsmart.Owed("3-783"));
        Assert.Equal(1, DownloadWants.Xsmart.Stat("3-783").parked);

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();
        Assert.Empty(XsAccess.Queue());
    }

    [Fact]
    public void Бэкофф_переживает_рестарт()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();
        ModInit.conf.xsmartWantMaxTries = 12;

        WantsAccess.CommitXs("3-784", 3, "784", WantsAccess.Ep("s1", 1, 1));
        DownloadWants.Xsmart.Fail("3-784", "s1e1", "UPSTREAM_DOWN");   // +5 минут

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        Assert.Empty(XsAccess.Queue());                                // время ещё не подошло
        Assert.Empty(DownloadWants.Xsmart.Owed("3-784"));              // Owed уважает nextAt
        // ⚠️ Stat при этом честно считает серию долгом: для владельца «ещё качается»,
        // а не «пусто». Расхождение с Owed намеренное — разные вопросы.
        Assert.Equal(1, DownloadWants.Xsmart.Stat("3-784").owed);
        Assert.True(DownloadWants.Xsmart.HasTitle("3-784"));
    }

    [Fact]
    public void Свип_не_ходит_в_сеть_и_обходится_без_кеша_карточки()
    {
        // Именно на отсутствии кеша карточки спотыкается XsmartReconcile (t == null → continue).
        // Журнал несёт sid/eid, поэтому резолв соберётся и без него.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        WantsAccess.CommitXs("3-785", 3, "785", WantsAccess.Ep("28260", 2, 11));
        WantsAccess.RestartXsmart();

        Assert.Equal(1, QbitController.XsmartWantsSweep());
        Assert.Single(XsAccess.Queue());
    }

    [Fact]
    public void Реконсиляция_part_не_дублирует_восстановленную_серию()
    {
        // Дедуп общим ключом (sref + ":" + epkey) обязан развести два источника восстановления.
        var (_, downloads) = XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        WantsAccess.CommitXs("3-786", 3, "786", WantsAccess.Ep("s1", 1, 2));
        WantsAccess.Touch(Path.Combine(downloads, "3-786"), "3-786.s01e02.720p.mp4.part");

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();
        QbitController.XsmartReconcile().Wait();

        Assert.Single(XsAccess.Queue());
    }

    [Fact]
    public void Статус_после_рестарта_не_idle()
    {
        // До 2.77 /qdl/xsmart/download/status отвечал «done»/«idle» на тайтле, у которого
        // семь серий тихо пропали. Это и было главным «выглядит исправным».
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var eps = Enumerable.Range(1, 3).Select(i => WantsAccess.Ep("s1", 1, i)).ToArray();
        WantsAccess.CommitXs("3-787", 3, "787", eps);
        WantsAccess.RestartXsmart();
        DownloadWants.Xsmart.Load();

        var res = WantsAccess.Ctrl().XsmartDownloadStatus(3, "787");
        var jo = JObject.Parse(WantsAccess.Body(res));
        Assert.Equal("queued", jo.Value<string>("state"));
        Assert.Equal(3, jo.Value<int?>("pending"));
        Assert.True(jo.Value<bool?>("restored"));
    }

    [Fact]
    public void Битая_запись_удаляется_а_не_паркуется()
    {
        // Серия без sid/eid не резолвится НИКОГДА — двенадцать бесполезных попыток
        // это не политика, а мусор в очереди на всё время жизни файла.
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        WantsAccess.CommitXs("3-788", 3, "788", WantsAccess.Ep("s1", 1, 1));
        JsonStore.Flush();

        var raw = WantsAccess.OnDisk(WantsAccess.XsPath());
        ((JObject)raw["items"]["3-788:s1e1"]["p"]).Remove("eid");
        File.WriteAllText(WantsAccess.XsPath(), raw.ToString());

        WantsAccess.RestartXsmart();
        QbitController.XsmartWantsRestore();

        Assert.Empty(XsAccess.Queue());
        Assert.False(DownloadWants.Xsmart.HasTitle("3-788"));   // выброшена, а не припаркована
    }

    [Fact]
    public void Выключатель_persist_гасит_и_запись_и_восстановление()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();
        ModInit.conf.xsmartQueuePersist = false;
        try
        {
            DownloadWants.Xsmart.Reset(flush: false);
            WantsAccess.CommitXs("3-789", 3, "789", WantsAccess.Ep("s1", 1, 1));
            Assert.False(File.Exists(WantsAccess.XsPath()));

            WantsAccess.RestartXsmart();
            QbitController.XsmartWantsRestore();
            Assert.Empty(XsAccess.Queue());
        }
        finally { ModInit.conf.xsmartQueuePersist = true; }
    }

    // ── jut.su ────────────────────────────────────────────────────────────

    [Fact]
    public void Jut_восстанавливается_offline_с_настоящим_titleRu()
    {
        // У .part-реконсиляции titleRu вырождался в slug и уезжал в маркер карточки.
        JutWatchAccess.Env();
        using var pin = JutWatchAccess.PinWorker();

        var eps = new[] { new JutEp { kind = JutEpKind.Episode, season = 1, num = 7 } };
        QbitController.JutWantsCommit("one-piece", "Ван-Пис", eps, "manual");

        WantsAccess.RestartJut();
        QbitController.JutWantsRestore();

        var q = JutWatchAccess.Queue();
        Assert.Single(q);
        Assert.Equal("s1e7", q[0].epkey);
        Assert.Equal("Ван-Пис", WantsAccess.JutQueueTitleRu(0));
    }

    [Fact]
    public void Jut_ключи_очереди_и_диска_не_смешиваются()
    {
        // 🔴 В журнале и в _jutQueued epkey БЕЗ паддинга (s1e5), а на диске ключ — имя файла
        // С паддингом (slug.s01e05.mp4). Сравнение строк напрямую объявило бы серию
        // «уже лежащей» и молча её потеряло.
        var (_, downloads) = JutWatchAccess.Env();
        using var pin = JutWatchAccess.PinWorker();

        var eps = new[] { new JutEp { kind = JutEpKind.Episode, season = 1, num = 5 } };
        QbitController.JutWantsCommit("liar-game", "Лжец", eps, "manual");

        WantsAccess.RestartJut();
        QbitController.JutWantsRestore();
        Assert.Single(JutWatchAccess.Queue());          // файла нет → долг стоит

        // теперь кладём файл с ПАДДИНГОМ и повторяем — долг обязан закрыться
        WantsAccess.RestartJut();
        WantsAccess.Touch(Path.Combine(downloads, "liar-game"), "liar-game.s01e05.1080p.mp4");
        QbitController.JutWantsRestore();

        Assert.Empty(JutWatchAccess.Queue());
        Assert.False(DownloadWants.Jut.HasTitle("liar-game"));
    }

    [Fact]
    public void Jut_отмена_снимает_намерение()
    {
        JutWatchAccess.Env();
        using var pin = JutWatchAccess.PinWorker();

        var eps = new[] { new JutEp { kind = JutEpKind.Episode, season = 1, num = 3 } };
        QbitController.JutWantsCommit("solo-leveling", "Соло", eps, "manual");
        WantsAccess.Ctrl().JutDownloadCancel("solo-leveling");

        WantsAccess.RestartJut();
        QbitController.JutWantsRestore();
        Assert.Empty(JutWatchAccess.Queue());
    }

    // ── прополка ──────────────────────────────────────────────────────────

    [Fact]
    public void Кап_на_тайтл_подрезает_журнал()
    {
        XsAccess.Env();
        using var pin = XsAccess.PinWorker();

        var eps = Enumerable.Range(1, 40).Select(i => WantsAccess.Ep("s1", 1, i)).ToArray();
        WantsAccess.CommitXs("3-790", 3, "790", eps);
        Assert.Equal(40, DownloadWants.Xsmart.CountFor("3-790"));

        DownloadWants.Xsmart.Prune(keepDays: 30, maxPerTitle: 10);
        Assert.Equal(10, DownloadWants.Xsmart.CountFor("3-790"));
        // Режем самые старые: свежие намерения нужнее.
        Assert.True(DownloadWants.Xsmart.Has("3-790", "s1e40"));
        Assert.False(DownloadWants.Xsmart.Has("3-790", "s1e1"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Текстовые инварианты исходника. Приём уже применяется в JutSuGrabQueueTests: часть
// правил невозможно проверить поведением дёшево, но очень легко нарушить правкой.
// ─────────────────────────────────────────────────────────────────────────────
public class QueueWiringTests
{
    /// <summary>
    /// Снять комментарии: иначе закомментированный вызов считается за живой, и негативный
    /// прогон «убрал постановку намерения» остаётся зелёным (проверено — так и было).
    /// </summary>
    static string NoComments(string src)
        => System.Text.RegularExpressions.Regex.Replace(
               System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline),
               @"//[^
]*", "");

    static string Src(string name)
    {
        string[] probe =
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Modules", "QbitDownload", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Modules", "QbitDownload", name)
        };
        foreach (string p in probe) if (File.Exists(p)) return File.ReadAllText(p);
        throw new FileNotFoundException(name);
    }

    static string Region(string src, string anchor, int len = 1400)
    {
        int i = src.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(i > 0, anchor + " не найдено");
        return src.Substring(i, Math.Min(len, src.Length - i));
    }

    [Fact]
    public void Все_точки_постановки_коммитят_намерение()
    {
        // Пропущенная точка = серия, которая не переживёт рестарт. Их шесть: маршрут,
        // сторож и реконсиляция — на каждый из двух контуров.
        Assert.Equal(2, Count(NoComments(Src("XsmartGrab.cs")), "XsmartWantsCommit("));   // маршрут + реконсиляция
        Assert.Equal(1, Count(NoComments(Src("XsmartWatch.cs")), "XsmartWantsCommit("));  // сторож
        Assert.Equal(2, Count(NoComments(Src("JutSuGrab.cs")), "JutWantsCommit("));
        Assert.Equal(1, Count(NoComments(Src("JutSuWatch.cs")), "JutWantsCommit("));
    }

    [Fact]
    public void Намерение_снимается_только_в_FinishFile()
    {
        // 🔴 Инвариант, отличающий журнал намерений от снимка очереди. Перенести снятие
        // в *Forget — значит вернуть все потери, ради которых слой и написан.
        string xs = Src("XsmartGrab.cs");
        Assert.Contains("XsmartWantsDone", Region(xs, "static async Task XsmartFinishFile", 5000));
        Assert.DoesNotContain("XsmartWantsDone", Region(xs, "static void XsmartForget(", 500));

        string ju = Src("JutSuGrab.cs");
        Assert.Contains("JutWantsDone", Region(ju, "static async Task JutFinishFile", 5000));
        Assert.DoesNotContain("JutWantsDone", Region(ju, "static void JutForget(", 500));
    }

    [Fact]
    public void Снятие_намерений_стоит_везде_где_двигается_поколение()
    {
        // Отмена, удаление карточки и уборка хвостов. Пропуск любой = «удалил, а оно вернулось».
        Assert.Contains("XsmartWantsDropTitle", Region(Src("XsmartGrab.cs"), "ActionResult XsmartDownloadCancel(int cat, string id)", 1500));
        Assert.Contains("XsmartWantsDropTitle", Region(Src("XsmartWatch.cs"), "XsmartForgetOnDelete", 2400));
        Assert.Contains("JutWantsDropTitle", Region(Src("JutSuGrab.cs"), "ActionResult JutDownloadCancel(string slug)", 1800));
        Assert.Contains("JutWantsDropTitle", Region(Src("JutSuWatch.cs"), "JutForgetOnDelete", 2400));
        Assert.Contains("JutWantsDropTitle", Region(Src("JutSuWatch.cs"), "JutPurgePartials", 1500));
    }

    [Fact]
    public void Восстановление_идёт_перед_реконсиляциями()
    {
        // Иначе .part-сканер отработает первым: продублирует единицу, затрёт filesTotal
        // и подставит slug вместо titleRu.
        string m = Src("ModInit.cs");
        int wants = m.IndexOf("JutWantsRestore()", StringComparison.Ordinal);
        int rec = m.IndexOf("JutReconcile()", StringComparison.Ordinal);
        Assert.True(wants > 0 && rec > wants, "JutWantsRestore обязан идти до JutReconcile");

        int xw = m.IndexOf("XsmartWantsRestore()", StringComparison.Ordinal);
        int xr = m.IndexOf("XsmartReconcile()", StringComparison.Ordinal);
        Assert.True(xw > 0 && xr > xw, "XsmartWantsRestore обязан идти до XsmartReconcile");
    }

    [Fact]
    public void Флаш_журнала_идёт_перед_флашем_горячего_слоя()
    {
        // Журнал пишется ЧЕРЕЗ JsonStore — значит горячий слой обязан флашиться последним.
        string m = Src("ModInit.cs");
        // Якоря с try — иначе IndexOf зацепится за упоминание в комментарии выше.
        int w = m.IndexOf("try { DownloadWants.Flush(); }", StringComparison.Ordinal);
        int j = m.IndexOf("try { JsonStore.Flush(); }", StringComparison.Ordinal);
        Assert.True(w > 0 && j > w, "DownloadWants.Flush обязан идти до JsonStore.Flush");
    }

    [Fact]
    public void Поколение_в_журнал_не_пишется()
    {
        // 🔴 _xsGen после рестарта пуст. Сохранённое поколение сделало бы все восстановленные
        // элементы stale, и воркер выкинул бы их молча — потеря выглядела бы как успех.
        string w = Src("DownloadWants.cs");
        Assert.DoesNotContain("[\"gen\"]", w);
        Assert.Contains("gen = XsmartGenOf(sref)", w);
        Assert.Contains("gen = JutGenOf(slug)", w);
    }

    static int Count(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
