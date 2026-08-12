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

    // ── снимок диска: точность и цена ─────────────────────────────────────

    [Theory]
    [InlineData(10, 100)]      // s01e100 начинается с s01e10
    [InlineData(1, 10)]        // s01e10  начинается с s01e01? нет — но проверим симметрию
    [InlineData(20, 200)]
    public void Серия_не_считается_скачанной_из_за_трёхзначной(int want, int onDisk)
    {
        // 🔥 Имя строится как s{season:00}e{num:00}, а прежняя проверка шла по StartsWith:
        // «spy.s01e100».StartsWith("spy.s01e10") == true → серия 10 считалась скачанной,
        // молча не вставала в очередь и не скачивалась НИКОГДА. Находка 12.08.2026.
        var (_, downloads) = JutWatchAccess.Env();
        QbitController.JutDropAllDiskKeys();

        string slug = "spy-family";
        string dir = Path.Combine(downloads, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{slug}.s01e{onDisk:00}.1080p.mp4"), "x");

        Assert.True(HaveFile(slug, 1, onDisk), $"серия {onDisk} лежит на диске — обязана считаться скачанной");
        if (want != onDisk)
            Assert.False(HaveFile(slug, 1, want), $"серия {want} НЕ скачана, а файл есть только у {onDisk}");
    }

    [Fact]
    public void Film1_не_путается_с_Film10()
    {
        var (_, downloads) = JutWatchAccess.Env();
        QbitController.JutDropAllDiskKeys();

        string slug = "oneepiece";
        string dir = Path.Combine(downloads, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{slug}.film10.1080p.mp4"), "x");

        Assert.True(HaveKind(slug, "film", 10));
        Assert.False(HaveKind(slug, "film", 1), "фильм 1 не скачан — на диске только фильм 10");
    }

    [Fact]
    public void Качество_в_имени_не_мешает_опознать_серию()
    {
        // Одна серия могла лечь как 1080p, другая как 720p — ключ обязан быть без качества.
        var (_, downloads) = JutWatchAccess.Env();
        QbitController.JutDropAllDiskKeys();

        string slug = "naruuto";
        string dir = Path.Combine(downloads, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{slug}.s01e07.720p.mp4"), "x");

        Assert.True(HaveFile(slug, 1, 7));
    }

    [Fact]
    public void Снимок_каталога_не_перечисляет_ФС_на_каждую_серию()
    {
        // Было Directory.EnumerateFiles НА КАЖДУЮ серию: «весь тайтл» на 500 серий при
        // 500 файлах = четверть миллиона syscall'ов внутри HTTP-запроса, на drvfs.
        var (_, downloads) = JutWatchAccess.Env();
        QbitController.JutDropAllDiskKeys();

        string slug = "bleach";
        string dir = Path.Combine(downloads, slug);
        Directory.CreateDirectory(dir);
        for (int i = 1; i <= 50; i++)
            File.WriteAllText(Path.Combine(dir, $"{slug}.s01e{i:00}.1080p.mp4"), "x");

        var keys = (System.Collections.Generic.HashSet<string>)Access.Call("JutDiskKeys", slug);
        Assert.Equal(50, keys.Count);
        // повторный вызов отдаёт ТОТ ЖЕ экземпляр — значит ФС не перечитывалась
        var again = (System.Collections.Generic.HashSet<string>)Access.Call("JutDiskKeys", slug);
        Assert.Same(keys, again);
    }

    [Fact]
    public void Снимок_диска_имеет_TTL_а_не_бессрочен()
    {
        // ⚠️ Файлы появляются не только через JutFinishFile (реконсиляция, docker cp).
        // Бессрочный снимок залипал бы на «ничего не скачано» и качал уже лежащее.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        Assert.Contains("_jutDisk.TryGetValue", src);
        Assert.Contains("TotalSeconds < 10", src);
    }

    static bool HaveFile(string slug, int season, int ep)
    {
        var e = new JutEp { kind = JutEpKind.Episode, season = season, num = ep };
        return (bool)Access.Call("JutHaveFile", slug, e);
    }

    static bool HaveKind(string slug, string kind, int num)
    {
        var e = new JutEp
        {
            kind = kind == "film" ? JutEpKind.Film : JutEpKind.Ova,
            season = 1, num = num
        };
        return (bool)Access.Call("JutHaveFile", slug, e);
    }

    // ── гард свободного места ─────────────────────────────────────────────

    [Fact]
    public void Место_считается_после_отсева_уже_скачанного()
    {
        // 🔥 Было: JutCheckSpace(want.Count) — 500 серий требовали 250 ГБ + резерв, даже если
        // 499 уже на диске, и запрос отбивался целиком, не поставив ничего.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int gate = src.IndexOf("JutCheckSpace(toGrab.Count", StringComparison.Ordinal);
        int filter = src.IndexOf("disk.Contains(JutEpKey(slug, e))", StringComparison.Ordinal);
        Assert.True(filter > 0, "отсев уже скачанного не найден");
        Assert.True(gate > filter, "гард места обязан считаться ПОСЛЕ отсева");
        // и очередь по другим тайтлам учитывается
        Assert.Contains("JutCheckSpace(toGrab.Count + _jutQueue.Count", src);
    }

    [Fact]
    public void Пустой_список_не_трогает_гард_места()
    {
        Assert.Null(Access.Call("JutCheckSpace", 0, null));
    }

    // ── дебаунс пуша уведомлений ──────────────────────────────────────────

    [Fact]
    public void Строки_noti_создаются_на_каждую_серию_несмотря_на_дебаунс()
    {
        // 🔴 Дебаунс касается ТОЛЬКО WS-сигнала. Строки в noti/seen обязаны писаться на каждую
        // серию: на них держится дедуп (epkey + UNIQUE noti(seriesKey,epkey)), бейдж и центр
        // уведомлений. Тронешь — центр начнёт врать, а §BH/§AZ-инварианты посыплются.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int i = src.IndexOf("static void JutNotifyDone", StringComparison.Ordinal);
        Assert.True(i > 0, "JutNotifyDone не найдена");
        string block = src.Substring(i, Math.Min(1600, src.Length - i));

        Assert.Contains("db.seen.Add", block);
        Assert.Contains("db.noti.Add", block);
        Assert.Contains("db.SaveChanges()", block);
        // и сигнал уходит через коалесер, а не напрямую
        Assert.Contains("JutPushDone(it.slug)", block);
        Assert.DoesNotContain("PushNotiSignal(1);", block);
    }

    [Fact]
    public void Последняя_серия_пачки_пушит_немедленно()
    {
        // Владелец должен сразу увидеть «готово». «Пачка кончилась» — по очереди ЭТОГО тайтла,
        // а не по глобальной: иначе последняя серия первого тайтла молчала бы, пока качается второй.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int i = src.IndexOf("static void JutPushDone", StringComparison.Ordinal);
        Assert.True(i > 0, "коалесер не найден");
        string block = src.Substring(i, Math.Min(900, src.Length - i));

        Assert.Contains("JutPendingFor(slug) <= 1", block);
        Assert.Contains("coalesce == 0 || last", block);
        Assert.DoesNotContain("_jutQueue.IsEmpty", block);
    }

    [Fact]
    public void Коалесинг_выключается_нулём()
    {
        // Киллсвитч на лету: правка init.conf вместо пересборки образа.
        Assert.NotNull(typeof(ModuleConf).GetProperty("jutNotifyCoalesceSec"));
        Assert.Equal(300, new ModuleConf().jutNotifyCoalesceSec);
    }

    // ── фоновый гейт и idle-таймаут ───────────────────────────────────────

    [Fact]
    public void Фоновая_работа_не_занимает_интерактивный_гейт()
    {
        // 🔥 Раньше один SemaphoreSlim(jutMaxConcurrent=3) обслуживал ВСЁ: каталог, карточку,
        // resolve плеера, воркер скачивания, тик слежения, постеры. Качалка отбирала слоты,
        // и открытие карточки вставало в очередь за скачиванием (один запрос к сайту ≈ 1.1 с) —
        // ровно жалоба «во время скачивания приложение подлагивает».
        string src = Strip(File.ReadAllText(ModuleFile("JutSuHttp.cs")));
        Assert.Contains("static SemaphoreSlim _bgGate;", src);
        Assert.Contains("jutBgConcurrent", src);
        Assert.Contains("if (_bg.Value)", src);

        // и все три фоновых контура помечены
        Assert.Contains("JutNet.BackgroundScope()", Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs"))));
        Assert.Contains("JutNet.BackgroundScope()", Strip(File.ReadAllText(ModuleFile("JutSuWatch.cs"))));
        Assert.Contains("JutNet.BackgroundScope()", Strip(File.ReadAllText(ModuleFile("JutSuPoster.cs"))));
    }

    [Fact]
    public void Область_фона_восстанавливает_предыдущее_значение()
    {
        // AsyncLocal-флаг обязан быть вложенным-безопасным, иначе вложенный scope
        // при выходе «разфонит» внешний.
        const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        var gate = typeof(JutNet).GetMethod("Gate", SF);
        Assert.NotNull(gate);
        object Current() => gate.Invoke(null, null);

        object interactive = Current();

        using (JutNet.BackgroundScope())
        {
            object bg = Current();
            Assert.NotSame(interactive, bg);           // фоновый гейт — другой семафор

            using (JutNet.BackgroundScope()) { Assert.Same(bg, Current()); }

            // 🔥 После выхода из ВЛОЖЕННОГО scope внешний обязан остаться фоновым:
            // иначе вложенный вызов «разфонил» бы внешний, и остаток фоновой работы
            // поехал бы по интерактивному гейту, снова отбирая слоты у карточек.
            Assert.Same(bg, Current());
        }

        Assert.Same(interactive, Current());           // и по выходу вернулись в интерактив
    }

    [Fact]
    public void Idle_таймаут_перезаводится_на_каждом_чанке()
    {
        // ⚠️ Именно idle, а не общий: у медиа-клиента Timeout.InfiniteTimeSpan обязателен
        // (§AL грабля 4 — иначе 489-МиБ файл рвётся на 100-й секунде). Без сдвига таймера
        // на каждом чанке это превратилось бы в общий таймаут и рубило здоровые закачки.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int i = src.IndexOf("static async Task JutWriteStream", StringComparison.Ordinal);
        Assert.True(i > 0);
        string block = src.Substring(i, Math.Min(1600, src.Length - i));
        Assert.Contains("idle.CancelAfter(TimeSpan.FromSeconds(idleSec))", block);
        // токен доходит до чтения тела
        Assert.Contains("src.ReadAsync(buf, 0, buf.Length, ct)", block);
    }

    [Fact]
    public void Медиа_клиент_остаётся_с_бесконечным_таймаутом()
    {
        // Регресс инварианта §AL: общий таймаут HttpClient режет чтение ТЕЛА,
        // ResponseHeadersRead от этого не спасает.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuHttp.cs")));
        int i = src.IndexOf("public static HttpClient Media", StringComparison.Ordinal);
        Assert.True(i > 0, "медиа-клиент не найден");
        string block = src.Substring(i, Math.Min(400, src.Length - i));
        Assert.Contains("total: null", block);
    }

    [Fact]
    public void Idle_таймаут_отличается_от_отмены()
    {
        // Оба прилетают как OperationCanceledException. Отмена окончательна;
        // idle-таймаут — штатный обрыв, .part докачивается бэкоффом.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        Assert.Contains("bool idleTimeout = ex is OperationCanceledException;", src);
        int stale = src.IndexOf("if (JutStale(it) || !JutOn) { JutSetState(job, \"canceled\"); return; }",
                                StringComparison.Ordinal);
        int idle = src.IndexOf("bool idleTimeout", StringComparison.Ordinal);
        Assert.True(stale > 0 && idle > stale, "проверка отмены обязана идти ПЕРЕД разбором idle");
    }

    // ── джоба прогрева кеша тайтлов ───────────────────────────────────────

    [Fact]
    public void Прогрев_греет_скачанное_подписки_и_недавно_открытое()
    {
        // Решение владельца: «джоба, пусть обновляется 2 раза в сутки», греем
        // «скачанное + подписки + недавно открытое».
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int i = src.IndexOf("internal static async Task JutWarmTitles", StringComparison.Ordinal);
        Assert.True(i > 0, "джоба прогрева не найдена");
        string block = src.Substring(i, Math.Min(2600, src.Length - i));

        Assert.Contains("JutDownloadRoot()", block);       // скачанное
        Assert.Contains("JutLoadWatch()", block);          // подписки
        Assert.Contains("_jutSeen", block);                // недавно открытое
        Assert.Contains("JutCacheWrite(\"title\"", block); // результат ложится в кеш
    }

    [Fact]
    public void Прогрев_идёт_под_фоновым_гейтом_и_с_темпом()
    {
        // ⚠️ Прогрев не имеет права отбирать слоты у карточек, которые пользователь
        // открывает прямо сейчас, и обязан щадить конечный бюджет прокси-выходов (§BB.5).
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int i = src.IndexOf("internal static async Task JutWarmTitles", StringComparison.Ordinal);
        string block = src.Substring(i, Math.Min(2600, src.Length - i));

        Assert.Contains("JutNet.BackgroundScope()", block);
        Assert.Contains("jutWarmPaceMs", block);
        Assert.Contains("jutWarmTitlesPerRun", block);
        // выключатель проверяется в начале и в цикле
        Assert.Contains("if (!JutOn) return;", block);
        Assert.Contains("if (!JutOn) break;", block);
    }

    [Fact]
    public void Прогрев_выключается_нулём_часов()
    {
        // Киллсвитч правкой init.conf, без пересборки образа.
        Assert.Equal(12, new ModuleConf().jutWarmIntervalHours);
        string src = Strip(File.ReadAllText(ModuleFile("ModInit.cs")));
        Assert.Contains("if (warmHours > 0)", src);
    }

    [Fact]
    public void Выгрузка_модуля_доводит_кеш_до_диска()
    {
        // Окно дебаунса writer'а — 200 мс. Без Flush в Dispose выгрузка модуля теряла бы
        // ещё не записанные маркер и activity.
        string src = Strip(File.ReadAllText(ModuleFile("ModInit.cs")));
        int i = src.IndexOf("public void Dispose", StringComparison.Ordinal);
        Assert.True(i > 0);
        Assert.Contains("JsonStore.Flush()", src.Substring(i));
    }

    [Fact]
    public void Пустое_добавление_не_создаёт_фантомный_job()
    {
        // 🔥 Найдено при проверке на боевом: GetOrAdd до цикла оставлял у тайтла,
        // где всё уже скачано, запись state="queued" с filesTotal=0 — статус врал
        // про работу, которой нет.
        string src = Strip(File.ReadAllText(ModuleFile("JutSuGrab.cs")));
        int gate = src.IndexOf("if (queued > 0)", StringComparison.Ordinal);
        int add = src.IndexOf("_jutJobs.GetOrAdd(slug", StringComparison.Ordinal);
        Assert.True(gate > 0 && add > gate, "job обязан создаваться ВНУТРИ ветки queued > 0");
        Assert.Contains("JutGrabJob job = null;", src);
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
