using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Зеркало ленты уведомлений и памяти экрана jut.su дом → реплика (ReplicaNoti.cs).
///
/// Почему под тестом именно это. Своих уведомлений у реплики не бывает, значит лента там —
/// целиком чужая копия, и ошибиться можно ровно тремя способами, каждый из которых виден только
/// на боевой машине: (1) разъехались Id — у клиента, переехавшего с дома, залп повторных тостов
/// либо немая лента; (2) домашний снапшот затирает местное «прочитано» — бейдж воскресает после
/// каждого тика; (3) авария дома читается как «всё почистили» — зеркало стирается по сбою.
/// </summary>
public class ReplicaNotiTests
{
    // ── обвязка ───────────────────────────────────────────────────────────────

    static JObject Row(long id, string sk, string ep, bool read = false, string hash = null, long posterAt = 0)
        => new JObject
        {
            ["id"] = id,
            ["seriesKey"] = sk,
            ["epkey"] = ep,
            ["seriesId"] = 0,
            ["hash"] = hash,
            ["title"] = "тайтл " + sk,
            ["season"] = 1,
            ["episode"] = 7,
            ["kind"] = (string)null,
            ["label"] = "Сезон 1 · серия 7",
            ["created"] = new DateTime(2026, 8, 26, 21, 4, 11, DateTimeKind.Utc).ToString("o"),
            ["read"] = read,
            ["posterAt"] = posterAt
        };

    static string H(char c) => new string(c, 40);

    static void SeedDb(params NotiModel[] rows)
    {
        using var db = new SqlContext();
        db.Database.EnsureCreated();
        foreach (var r in rows) db.noti.Add(r);
        db.SaveChanges();
    }

    static List<NotiModel> Feed()
    {
        using var db = new SqlContext();
        return db.noti.OrderByDescending(x => x.Id).ToList();
    }

    // ══ сигнатура и решение «идти ли за снапшотом» ═════════════════════════════

    [Fact]
    public void Сигнатура_ленты_ловит_все_четыре_события()
    {
        // С таблицей noti случается ровно четыре вещи, и каждая обязана дать другую сигнатуру:
        // новая строка (maxId), ретенция (total), очистка (и то и другое), «прочитано» (unread).
        string basic = QbitController.NotiSig(311, 187, 3, "7/1000");

        Assert.NotEqual(basic, QbitController.NotiSig(312, 188, 4, "7/1000"));   // новая строка
        Assert.NotEqual(basic, QbitController.NotiSig(311, 120, 3, "7/1000"));   // прун
        Assert.NotEqual(basic, QbitController.NotiSig(0, 0, 0, "7/1000"));       // очистка
        Assert.NotEqual(basic, QbitController.NotiSig(311, 187, 0, "7/1000"));   // прочитали дома
        Assert.NotEqual(basic, QbitController.NotiSig(311, 187, 3, "8/2000"));   // тронули jut-память

        Assert.Equal(basic, QbitController.NotiSig(311, 187, 3, "7/1000"));      // ничего не менялось
    }

    [Fact]
    public void За_снапшотом_не_ходим_только_когда_сошлось_всё()
    {
        // сигнатуры нет (дом старой версии или не смог посчитать) — это «не знаю», а не «не менялось»
        Assert.True(QbitController.NotiPullNeeded(null, "a", 10, 10));
        Assert.True(QbitController.NotiPullNeeded("", "a", 10, 10));

        Assert.True(QbitController.NotiPullNeeded("b", "a", 10, 10));    // дом изменился
        Assert.True(QbitController.NotiPullNeeded("a", "a", 0, 10));     // 🎯 том пересоздали: лента пуста
        Assert.False(QbitController.NotiPullNeeded("a", "a", 10, 10));   // всё сошлось — тик бесплатный

        // счётчик не удалось посчитать — поводом для перезаливки это не служит
        Assert.False(QbitController.NotiPullNeeded("a", "a", -1, 10));
        Assert.False(QbitController.NotiPullNeeded("a", "a", 10, -1));
    }

    [Fact]
    public void Прочитанное_на_реплике_не_воскресает()
    {
        Assert.True(QbitController.NotiReadMerge(false, true));    // прочитали здесь — домашний ноль не воскрешает
        Assert.True(QbitController.NotiReadMerge(true, false));    // прочитали дома — доезжает сюда
        Assert.True(QbitController.NotiReadMerge(true, true));
        Assert.False(QbitController.NotiReadMerge(false, false));
    }

    // ══ разбор снапшота ═══════════════════════════════════════════════════════

    [Fact]
    public void Снапшот_чистит_мусор_и_держит_порядок_ленты()
    {
        var arr = new JArray
        {
            Row(5, "t1", "s1e5"),
            Row(9, "t1", "s1e9"),
            new JObject { ["title"] = "без айди" },   // без Id не собрать ни порядок, ни отсечку тостов
            Row(9, "t1", "s1e9"),                     // дубль
            Row(7, "t2", "s1e7")
        };

        var rows = QbitController.NotiSnapshotRows(arr);

        Assert.Equal(new long[] { 9, 7, 5 }, rows.Select(r => r.Value<long>("id")));
    }

    // ══ применение: главное ════════════════════════════════════════════════════

    [Fact]
    public void Зеркало_кладёт_домашние_айди_и_замещает_ленту_целиком()
    {
        // 🎯 Пинит явную вставку против РЕАЛЬНОЙ схемы EnsureCreated: разойдись имена или типы
        // колонок в сыром SQL — поймала бы только боевая реплика.
        TestEnv.FreshCache();
        SeedDb(new NotiModel { seriesKey = "местная", epkey = "s1e1", created = DateTime.UtcNow });

        var rows = QbitController.NotiSnapshotRows(new JArray { Row(311, "t1", "s1e1"), Row(305, "t2", "s2e4") });
        var res = QbitController.ApplyNoti(rows);

        Assert.Equal(2, res.rows);
        Assert.Equal(new long[] { 311, 305 }, Feed().Select(x => x.Id));    // домашние Id, домашний порядок
        Assert.DoesNotContain(Feed(), x => x.seriesKey == "местная");        // местная строка вытеснена
        Assert.Equal(2, res.unread);
    }

    [Fact]
    public void Отметка_прочитанного_на_реплике_переживает_следующий_тик()
    {
        TestEnv.FreshCache();
        SeedDb(new NotiModel { seriesKey = "t1", epkey = "s1e1", created = DateTime.UtcNow, read = true });

        // дом присылает ту же строку (Id 1) непрочитанной и одну новую
        var rows = QbitController.NotiSnapshotRows(new JArray { Row(2, "t1", "s1e2"), Row(1, "t1", "s1e1") });
        var res = QbitController.ApplyNoti(rows);

        var feed = Feed();
        Assert.True(feed.Single(x => x.Id == 1).read);      // 🎯 бейдж не воскресает
        Assert.False(feed.Single(x => x.Id == 2).read);
        Assert.Equal(1, res.unread);
        Assert.Equal(1, res.fresh);                         // новой считается только строка сверх прежнего максимума
        Assert.Equal(1, res.prevMaxId);
    }

    [Fact]
    public void Прочитанное_дома_доезжает_до_реплики()
    {
        TestEnv.FreshCache();
        SeedDb(new NotiModel { seriesKey = "t1", epkey = "s1e1", created = DateTime.UtcNow });

        QbitController.ApplyNoti(QbitController.NotiSnapshotRows(new JArray { Row(1, "t1", "s1e1", read: true) }));

        Assert.True(Feed().Single().read);
    }

    [Fact]
    public void Повторное_применение_идемпотентно()
    {
        // Уникальный индекс (seriesKey, epkey) — самая вероятная причина падения заливки.
        TestEnv.FreshCache();
        SeedDb();

        var rows = QbitController.NotiSnapshotRows(new JArray { Row(4, "t1", "s1e1"), Row(5, "t1", "s1e2") });

        Assert.Equal(2, QbitController.ApplyNoti(rows).rows);
        Assert.Equal(2, QbitController.ApplyNoti(rows).rows);
        Assert.Equal(2, Feed().Count);
    }

    [Fact]
    public void Битый_снапшот_не_оставляет_ленту_пустой()
    {
        // 🎯 Две строки с одной парой (seriesKey, epkey) роняют вставку на UNIQUE. Транзакция
        // обязана откатиться целиком: пустая лента при неизменной сигнатуре молчала бы сутками.
        TestEnv.FreshCache();
        SeedDb(new NotiModel { seriesKey = "живая", epkey = "s1e1", created = DateTime.UtcNow });

        var rows = QbitController.NotiSnapshotRows(new JArray { Row(9, "t1", "s1e1"), Row(8, "t1", "s1e1") });
        var res = QbitController.ApplyNoti(rows);

        Assert.Equal(0, res.rows);
        Assert.Equal("живая", Feed().Single().seriesKey);
    }

    [Fact]
    public void Время_создания_переживает_перенос()
    {
        // created уходит строкой ISO, а в базе формат задаёт EF: разъезд был бы виден только
        // сдвигом всей ленты на часовой пояс.
        TestEnv.FreshCache();
        SeedDb();

        QbitController.ApplyNoti(QbitController.NotiSnapshotRows(new JArray { Row(3, "t1", "s1e1") }));

        Assert.Equal(new DateTime(2026, 8, 26, 21, 4, 11, DateTimeKind.Utc),
                     DateTime.SpecifyKind(Feed().Single().created, DateTimeKind.Utc));
    }

    // ══ постеры ════════════════════════════════════════════════════════════════

    [Fact]
    public void За_постерами_идём_только_туда_где_они_есть_у_дома()
    {
        var rows = QbitController.NotiSnapshotRows(new JArray
        {
            Row(10, "t1", "s1e1", hash: H('a'), posterAt: 500),     // есть у дома, у нас нет — берём
            Row(11, "t2", "s1e2", hash: H('b'), posterAt: 0),       // 🎯 нет и у дома (jut/нескачанное) — не бьёмся в 404
            Row(12, "t3", "s1e3", hash: H('c'), posterAt: 400),     // местный свежее — не трогаем
            Row(13, "t4", "s1e4", hash: "не-хеш", posterAt: 900),   // мусорный хеш
            Row(14, "t1", "s1e5", hash: H('a'), posterAt: 500),     // дубль тайтла
            Row(3,  "t5", "s1e6", hash: H('d'), posterAt: 900)      // старая строка, не новая
        });

        var local = new Dictionary<string, long> { [H('c')] = 400 };
        var want = QbitController.NotiPostersWanted(rows, 5, h => local.TryGetValue(h, out long t) ? t : 0, 10);

        Assert.Equal(new[] { H('a') }, want);
    }

    [Fact]
    public void Кап_постеров_за_тик_соблюдается()
    {
        var arr = new JArray();
        for (int i = 1; i <= 12; i++) arr.Add(Row(100 + i, "t" + i, "s1e1", hash: i.ToString("x").PadLeft(40, '0'), posterAt: 500));

        var want = QbitController.NotiPostersWanted(QbitController.NotiSnapshotRows(arr), 0, _ => 0, 10);

        Assert.Equal(10, want.Count);
    }

    // ══ память экрана jut.su ═══════════════════════════════════════════════════

    [Fact]
    public void Имя_бакета_из_сети_отвергается_если_не_равно_своей_санации()
    {
        // Имя идёт в имя файла на диске, поэтому правило — ОТКАЗ, а не тихая нормализация:
        // отвергнутое попадёт в лог, нормализованное молча создало бы чужой файл.
        Assert.False(QbitController.JutBucketAcceptable("../../init.conf"));
        Assert.False(QbitController.JutBucketAcceptable("..\\init"));
        Assert.False(QbitController.JutBucketAcceptable("Abc"));        // регистр уже приведён на доме
        Assert.False(QbitController.JutBucketAcceptable("con"));        // зарезервировано Win32 → d_con
        Assert.False(QbitController.JutBucketAcceptable(""));
        Assert.False(QbitController.JutBucketAcceptable(null));

        Assert.True(QbitController.JutBucketAcceptable("_shared"));
        Assert.True(QbitController.JutBucketAcceptable("dueq3shm"));
    }

    [Fact]
    public void Память_экрана_jut_приезжает_и_видна_без_рестарта()
    {
        string cache = TestEnv.FreshCache();

        int n = QbitController.ApplyJutHistory(new JArray
        {
            new JObject
            {
                ["bucket"] = "dueq3shm",
                ["at"] = "2026-08-26T18:02:11Z",
                ["data"] = new JObject { ["watched"] = new JObject { ["naruto"] = new JObject { ["at"] = "2026-08-26T18:02:11Z" } }, ["at"] = "2026-08-26T18:02:11Z" }
            }
        });

        Assert.Equal(1, n);
        string path = Path.Combine(cache, "jut", "history", "dueq3shm.json");
        Assert.True(File.Exists(path));
        // читаем ровно тем же способом, что и /qdl/jut/recent: мимо горячего слоя запись была бы
        // невидима до рестарта, и файл на диске это не показал бы
        Assert.NotNull(JsonStore.ReadObject(path)?["watched"]?["naruto"]);
    }

    [Fact]
    public void Старая_домашняя_память_не_затирает_свежую_местную()
    {
        string cache = TestEnv.FreshCache();
        string path = Path.Combine(cache, "jut", "history", "_shared.json");

        var newer = new JObject { ["watched"] = new JObject { ["свежее"] = new JObject() }, ["at"] = "2026-08-27T10:00:00Z" };
        JsonStore.WriteNow(path, newer);

        int n = QbitController.ApplyJutHistory(new JArray
        {
            new JObject
            {
                ["bucket"] = "_shared",
                ["at"] = "2026-08-20T10:00:00Z",
                ["data"] = new JObject { ["watched"] = new JObject { ["старое"] = new JObject() }, ["at"] = "2026-08-20T10:00:00Z" }
            }
        });

        Assert.Equal(0, n);
        Assert.NotNull(JsonStore.ReadObject(path)?["watched"]?["свежее"]);
    }

    [Fact]
    public void Метка_свежести_остаётся_домашней()
    {
        // 🔴 Со «своим» временем местная копия оказалась бы свежее дома, и следующее домашнее
        // обновление перестало бы доезжать вовсе — ровно та же грабля, что закрыта у закладок.
        string cache = TestEnv.FreshCache();

        QbitController.ApplyJutHistory(new JArray
        {
            new JObject
            {
                ["bucket"] = "_shared",
                ["at"] = "2026-08-20T10:00:00Z",
                ["data"] = new JObject { ["watched"] = new JObject(), ["at"] = "2026-08-20T10:00:00Z" }
            }
        });

        var jo = JsonStore.ReadObject(Path.Combine(cache, "jut", "history", "_shared.json"));
        Assert.Equal(new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), QbitController.JutAtUtc(jo));
    }
}
