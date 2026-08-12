using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Очередь скачивания jut.su: инварианты, за которые владелец платил «второе аниме
// не добавилось, и ничего не произошло».
//
// Все четыре механизма молчаливого отказа разбирались вживую 12.08.2026:
//   1. break по jutEnable=false терял ключ и крутил воркер вхолостую;
//   2. прогресс складывался с прошлым прогоном;
//   3. серия, уже качавшаяся в момент отмены, затирала "canceled" на "done";
//   4. статус смотрел на ГЛОБАЛЬНУЮ очередь → первый тайтл вечно "running".
//
// Канон: E:\Media-server\claude\jut\02-architecture.md §8
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Доступ к private-static кухне очереди (сами поля живут в QbitController).</summary>
static class JutGrabAccess
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    static FieldInfo F(string name) =>
        C.GetField(name, SF) ?? throw new MissingFieldException("QbitController." + name);

    internal static readonly Type ItemT = C.GetNestedType("JutGrabItem", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+JutGrabItem not found");
    internal static readonly Type JobT = C.GetNestedType("JutGrabJob", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+JutGrabJob not found");

    public static object NewItem(string slug, string epkey, int gen)
    {
        object it = Activator.CreateInstance(ItemT);
        ItemT.GetField("slug", IF).SetValue(it, slug);
        ItemT.GetField("epkey", IF).SetValue(it, epkey);
        ItemT.GetField("gen", IF).SetValue(it, gen);
        return it;
    }

    public static object NewJob() => Activator.CreateInstance(JobT);

    public static string JobState(object job) => (string)JobT.GetField("state", IF).GetValue(job);
    public static void JobCanceled(object job, bool v) => JobT.GetField("canceled", IF).SetValue(job, v);
    public static void JobTouched(object job, DateTime v) => JobT.GetField("touched", IF).SetValue(job, v);
    public static void JobState(object job, string v) => JobT.GetField("state", IF).SetValue(job, v);

    public static int Gen(string slug) => (int)Access.Call("JutGenOf", slug);
    public static bool Stale(object it) => (bool)Access.Call("JutStale", it);
    public static int PendingFor(string slug) => (int)Access.Call("JutPendingFor", slug);
    public static void SetState(object job, string s) => Access.Call("JutSetState", job, s);
    public static void Forget(object it) => Access.Call("JutForget", it);
    public static void PruneJobs() => Access.Call("JutPruneJobs");
    public static string Message(int queued, int already, int duplicate, int pending)
        => (string)Access.Call("JutQueueMessage", queued, already, duplicate, pending);

    public static System.Collections.Generic.HashSet<string> Queued()
        => (System.Collections.Generic.HashSet<string>)F("_jutQueued").GetValue(null);

    public static IDictionary Jobs() => (IDictionary)F("_jutJobs").GetValue(null);
    public static IDictionary Gens() => (IDictionary)F("_jutGen").GetValue(null);

    public static void BumpGen(string slug) => Gens()[slug] = Gen(slug) + 1;

    public static void Reset()
    {
        Queued().Clear();
        Jobs().Clear();
        Gens().Clear();
    }
}

public class JutSuGrabQueueTests
{
    static string ModuleFile(string name)
    {
        string[] probe =
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Modules", "QbitDownload", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Modules", "QbitDownload", name)
        };
        foreach (string p in probe) if (File.Exists(p)) return p;
        throw new FileNotFoundException("не найден " + name);
    }

    // ── 1. выключатель на лету ────────────────────────────────────────────

    [Fact]
    public void Выключение_на_лету_не_теряет_очередь_и_не_крутит_воркер()
    {
        // 🔥 Было: `while (_jutQueue.TryDequeue(...)) { if (!enable) break; ... }` —
        // элемент УЖЕ вынут, а JutForget в finally не звался. Ключ протекал навсегда,
        // и finally тут же перезапускал воркер: тот выхватывал следующий элемент и снова
        // ломался о break. Busy-loop молча сливал всю очередь, после чего те же серии
        // нельзя было поставить заново до рестарта контейнера.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));

        // флаг проверяется В УСЛОВИИ цикла, до извлечения элемента
        Assert.Contains("while (JutOn && _jutQueue.TryDequeue", src);
        // и перекик тоже под флагом — иначе это тот же busy-loop
        Assert.Contains("if (JutOn && !_jutQueue.IsEmpty) JutKickWorker();", src);
        // старой формы больше нет
        Assert.DoesNotContain("if (ModInit.conf?.jutEnable != true) break;", src);
    }

    [Fact]
    public void Реконсиляция_оживляет_очередь_после_обратного_включения()
    {
        // Воркер выходит по !JutOn и сам себя не перезапускает. JutReconcile зовётся
        // на перечит init.conf и остаётся ЕДИНСТВЕННОЙ точкой, где очередь оживает,
        // поэтому кик там обязан быть безусловным, а не только при added > 0.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int i = src.IndexOf("JutReconcile", StringComparison.Ordinal);
        Assert.True(i > 0, "JutReconcile не найдена");
        string block = src.Substring(i);
        Assert.Contains("if (!_jutQueue.IsEmpty) JutKickWorker();", block);
    }

    // ── 2. прогресс не врёт ───────────────────────────────────────────────

    [Fact]
    public void Второе_добавление_не_врёт_прогрессом()
    {
        // filesTotal только рос: два добавления подряд давали fileDone/filesTotal вроде 3/40,
        // где 40 — сумма двух пачек. Сброс возможен только когда очередь этого тайтла пуста.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        Assert.Contains("bool freshBatch = JutPendingFor(slug) == 0;", src);
        Assert.Contains("job.fileDone = 0; job.filesTotal = 0;", src);

        // filesTotal обязан прибавляться ВНУТРИ ветки queued > 0 и ПОСЛЕ сброса:
        // иначе пустое добавление (всё уже скачано) всё равно раздувало бы знаменатель.
        int gate = src.IndexOf("if (queued > 0)", StringComparison.Ordinal);
        int reset = src.IndexOf("job.fileDone = 0;", StringComparison.Ordinal);
        int add = src.IndexOf("job.filesTotal += queued;", StringComparison.Ordinal);
        Assert.True(gate > 0 && reset > gate, "сброс счётчиков обязан быть внутри ветки queued > 0");
        Assert.True(add > reset, "filesTotal обязан прибавляться после сброса, а не до");
    }

    // ── 3. отмена не перебивается ─────────────────────────────────────────

    [Fact]
    public void Отмена_не_перебивается_следующим_файлом()
    {
        // Серия, уже качавшаяся в момент отмены, из очереди вынута — пометить её обходом
        // _jutQueue невозможно. Она доигрывала и затирала "canceled" на "done".
        object job = JutGrabAccess.NewJob();
        JutGrabAccess.JobCanceled(job, true);
        JutGrabAccess.JobState(job, "canceled");

        JutGrabAccess.SetState(job, "running");
        Assert.Equal("canceled", JutGrabAccess.JobState(job));

        JutGrabAccess.SetState(job, "done");
        Assert.Equal("canceled", JutGrabAccess.JobState(job));

        // а новая пачка снимает флаг и статус снова живой
        JutGrabAccess.JobCanceled(job, false);
        JutGrabAccess.SetState(job, "running");
        Assert.Equal("running", JutGrabAccess.JobState(job));
    }

    // ── 4. два тайтла независимы ──────────────────────────────────────────

    [Fact]
    public void Два_тайтла_подряд_имеют_независимый_статус()
    {
        JutGrabAccess.Reset();
        var set = JutGrabAccess.Queued();
        set.Add("naruuto:s1e1");
        set.Add("naruuto:s1e2");
        set.Add("oneepiece:s1e1");

        Assert.Equal(2, JutGrabAccess.PendingFor("naruuto"));
        Assert.Equal(1, JutGrabAccess.PendingFor("oneepiece"));
        Assert.Equal(0, JutGrabAccess.PendingFor("spy-family"));

        // источник истины для "done" — очередь ЭТОГО тайтла, а не глобальная
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        Assert.Contains("JutPendingFor(it.slug) <= 1 ? \"done\" : \"running\"", src);
        Assert.DoesNotContain("_jutQueue.IsEmpty ? \"done\"", src);
        JutGrabAccess.Reset();
    }

    [Fact]
    public void Префикс_слага_не_ловит_чужой_тайтл()
    {
        // PendingFor считает по префиксу "slug:" — двоеточие обязательно, иначе
        // "naruuto" поймал бы ключи "naruuto-shippuden:*".
        JutGrabAccess.Reset();
        var set = JutGrabAccess.Queued();
        set.Add("naruuto-shippuden:s1e1");
        Assert.Equal(0, JutGrabAccess.PendingFor("naruuto"));
        Assert.Equal(1, JutGrabAccess.PendingFor("naruuto-shippuden"));
        JutGrabAccess.Reset();
    }

    // ── 5. отмена → повторное добавление ──────────────────────────────────

    [Fact]
    public void Отмена_и_повторное_добавление_не_теряют_ключ()
    {
        // 🔥 Гонка: отмена снимает ключи, но элементы остаются в ConcurrentQueue.
        // Пользователь сразу добавляет заново → ключ снова в _jutQueued. Дальше воркер
        // доходит до СТАРОГО элемента, и его JutForget снимает ключ, только что
        // поставленный новым запросом — серия молча не скачивается.
        JutGrabAccess.Reset();
        var set = JutGrabAccess.Queued();

        object stale = JutGrabAccess.NewItem("naruuto", "s1e1", gen: 0);
        JutGrabAccess.BumpGen("naruuto");                 // отмена
        object fresh = JutGrabAccess.NewItem("naruuto", "s1e1", gen: JutGrabAccess.Gen("naruuto"));
        set.Add("naruuto:s1e1");                          // ключ нового запроса

        Assert.True(JutGrabAccess.Stale(stale), "элемент до отмены обязан быть устаревшим");
        Assert.False(JutGrabAccess.Stale(fresh));

        JutGrabAccess.Forget(stale);                      // старый уходит из очереди
        Assert.Contains("naruuto:s1e1", set);             // но чужой ключ НЕ трогает

        JutGrabAccess.Forget(fresh);                      // а свой — снимает
        Assert.DoesNotContain("naruuto:s1e1", set);
        JutGrabAccess.Reset();
    }

    [Fact]
    public void Отмена_достаёт_серию_которая_уже_качается()
    {
        // Отмена не может пометить элемент, вынутый из очереди. Поколение может.
        JutGrabAccess.Reset();
        object inFlight = JutGrabAccess.NewItem("naruuto", "s1e5", gen: JutGrabAccess.Gen("naruuto"));
        Assert.False(JutGrabAccess.Stale(inFlight));

        JutGrabAccess.BumpGen("naruuto");
        Assert.True(JutGrabAccess.Stale(inFlight));

        // и цикл чтения тела проверяет именно JutStale, а не it.cancel
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        Assert.Contains("if (JutStale(it) || !JutOn) break;", src);
        JutGrabAccess.Reset();
    }

    [Fact]
    public void Снятие_подписки_тоже_двигает_поколение()
    {
        // JutForgetOnDelete отменяет очередь тем же способом, что и JutDownloadCancel —
        // иначе у него остаётся ровно та же гонка.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuWatch.cs")));
        Assert.Contains("_jutGen[slug] = JutGenOf(slug) + 1;", src);
    }

    // ── 6. мёртвый конфиг ─────────────────────────────────────────────────

    [Fact]
    public void Конфиг_не_содержит_мёртвого_jutDownloadConcurrency()
    {
        // Ключ существовал и никогда не читался: «один файл за раз» зашито флагом
        // _jutWorker. Ручка, которую крутят без эффекта, хуже её отсутствия.
        var f = typeof(ModuleConf).GetProperty("jutDownloadConcurrency");
        Assert.Null(f);
    }

    // ── 7. уборка job ─────────────────────────────────────────────────────

    [Fact]
    public void Терминальные_job_вычищаются_а_живые_остаются()
    {
        JutGrabAccess.Reset();
        var jobs = JutGrabAccess.Jobs();

        object old = JutGrabAccess.NewJob();
        JutGrabAccess.JobState(old, "done");
        JutGrabAccess.JobTouched(old, DateTime.UtcNow.AddHours(-12));
        jobs["старый"] = old;

        object running = JutGrabAccess.NewJob();
        JutGrabAccess.JobState(running, "running");
        JutGrabAccess.JobTouched(running, DateTime.UtcNow.AddHours(-12));
        jobs["качается"] = running;

        object recent = JutGrabAccess.NewJob();
        JutGrabAccess.JobState(recent, "done");
        JutGrabAccess.JobTouched(recent, DateTime.UtcNow);
        jobs["свежий"] = recent;

        JutGrabAccess.PruneJobs();

        Assert.False(jobs.Contains("старый"), "терминальный и протухший обязан уйти");
        Assert.True(jobs.Contains("качается"), "незавершённый нельзя выбрасывать");
        Assert.True(jobs.Contains("свежий"), "свежий терминальный ещё нужен клиенту");
        JutGrabAccess.Reset();
    }

    // ── 8. молчание «queued = 0» ──────────────────────────────────────────

    [Theory]
    [InlineData(37, 412, 5, "Поставлено в очередь: 37")]
    [InlineData(0, 0, 5, "Уже в очереди")]
    [InlineData(0, 412, 0, "Всё уже скачано")]
    [InlineData(0, 0, 0, "Нечего скачивать")]
    public void Сообщение_различает_исходы(int queued, int already, int duplicate, string expect)
    {
        // 🔥 Раньше все четыре случая давали клиенту «В очереди на скачивание: 0»,
        // и повторное добавление выглядело как «ничего не произошло».
        Assert.Contains(expect, JutGrabAccess.Message(queued, already, duplicate, 3));
    }

    [Fact]
    public void Ответ_несёт_поля_для_различения_исходов()
    {
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        foreach (string field in new[] { "\"queued\"", "\"already\"", "\"duplicate\"", "\"pending\"", "\"message\"" })
            Assert.Contains("[" + field + "]", src);
        // queued остаётся: его читает старый закешированный qdl.js
        Assert.Contains("[\"queued\"] = queued", src);
    }

    static string Strip(string src)
    {
        src = System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return string.Join("\n", src.Split('\n')
            .Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l.Substring(0, i) : l; }));
    }
}
