using Newtonsoft.Json.Linq;
using QbitDownload;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Горячий слой JSON-кеша (JsonStore.cs). Пять инвариантов слоя — здесь.
//
// Зачем слой: /qdl/list дёргался на каждое открытие карточки и пересобирался с нуля
// (~200-300 файловых операций + 62 JObject.Parse). Замер до: 0.58-1.03 с.
//
// ⚠️ Тесты идут последовательно (Collection): стор статический, параллельные классы
// затирали бы друг другу _mem и счётчики Stats().
// Канон: E:\Media-server\claude\jut\06-cache.md
// ─────────────────────────────────────────────────────────────────────────────
[Collection("JsonStore")]
public class JsonStoreTests : IDisposable
{
    readonly string _dir;

    public JsonStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jsonstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        JsonStore.ForgetAll();
        JsonStore.ForgetAllDirs();
    }

    public void Dispose()
    {
        JsonStore.ForgetAll();
        JsonStore.ForgetAllDirs();
        try { Directory.Delete(_dir, true); } catch { }
    }

    string P(string name) => Path.Combine(_dir, name);

    // ── инвариант 1: чтение прогретого ключа не идёт на диск ──────────────

    [Fact]
    public void Прогретое_чтение_не_трогает_диск()
    {
        string p = P("a.json");
        File.WriteAllText(p, "{\"x\":1}");

        var before = JsonStore.Stats();
        Assert.Equal(1, JsonStore.ReadObject(p).Value<int>("x"));   // промах: один поход на диск
        var afterMiss = JsonStore.Stats();
        Assert.Equal(before.diskReads + 1, afterMiss.diskReads);

        for (int i = 0; i < 50; i++) Assert.Equal(1, JsonStore.ReadObject(p).Value<int>("x"));
        Assert.Equal(afterMiss.diskReads, JsonStore.Stats().diskReads);   // ни одного нового
    }

    [Fact]
    public void Отсутствующий_файл_тоже_кешируется()
    {
        // Негативный кеш важен не меньше: /qdl/list звал File.Exists на каждый файл
        // каждого маркера, и большинство ответов были «нет».
        string p = P("нет.json");
        Assert.Null(JsonStore.ReadObject(p));
        var after = JsonStore.Stats();
        for (int i = 0; i < 20; i++) Assert.Null(JsonStore.ReadObject(p));
        Assert.Equal(after.diskReads, JsonStore.Stats().diskReads);
    }

    [Fact]
    public void Битый_json_не_роняет_чтение_и_не_перечитывается()
    {
        string p = P("битый.json");
        File.WriteAllText(p, "{это не json");
        Assert.Null(JsonStore.ReadObject(p));       // не бросает
        var after = JsonStore.Stats();
        Assert.Null(JsonStore.ReadObject(p));
        Assert.Equal(after.diskReads, JsonStore.Stats().diskReads);
    }

    // ── возврат клона: вызывающие правят результат ────────────────────────

    [Fact]
    public void Чтение_отдаёт_клон_а_не_живой_экземпляр()
    {
        // 🔥 /qdl/list вешает meta в дерево ответа (item["meta"] = …) и правит его.
        // Отдав живой экземпляр, мы бы порвали кеш и получили гонку на JToken.
        string p = P("m.json");
        JsonStore.Write(p, JObject.Parse("{\"title\":\"Наруто\"}"));

        var first = JsonStore.ReadObject(p);
        first["title"] = "испорчено";
        first["новое"] = 1;

        var second = JsonStore.ReadObject(p);
        Assert.Equal("Наруто", second.Value<string>("title"));
        Assert.Null(second["новое"]);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Запись_копирует_значение_а_не_держит_ссылку()
    {
        string p = P("w.json");
        var src = JObject.Parse("{\"n\":1}");
        JsonStore.Write(p, src);
        src["n"] = 999;                              // правим ПОСЛЕ записи
        Assert.Equal(1, JsonStore.ReadObject(p).Value<int>("n"));
    }

    // ── инвариант 2 + 4: запись асинхронная и коалесится ──────────────────

    [Fact]
    public void Запись_возвращается_сразу_и_видна_из_РАМ_до_диска()
    {
        string p = P("быстро.json");
        JsonStore.Write(p, JObject.Parse("{\"v\":7}"));
        // значение доступно немедленно, даже если writer ещё не проснулся
        Assert.Equal(7, JsonStore.ReadObject(p).Value<int>("v"));
    }

    [Fact]
    public void Пачка_правок_одного_ключа_даёт_одну_запись_на_диск()
    {
        // Инвариант 4. Ровно этот случай: маркер local/<hash>.json перезаписывался
        // после КАЖДОЙ докачанной серии — 60 серий давали 60 записей.
        string p = P("маркер.json");
        var before = JsonStore.Stats();

        for (int i = 0; i < 60; i++) JsonStore.Write(p, JObject.Parse("{\"files\":" + i + "}"));
        JsonStore.Flush();

        long written = JsonStore.Stats().diskWrites - before.diskWrites;
        Assert.True(written <= 2, "ожидали коалесинг, а записей на диск было " + written);
        Assert.Equal(59, JObject.Parse(File.ReadAllText(p)).Value<int>("files"));
    }

    [Fact]
    public void Flush_доводит_всё_до_диска()
    {
        string a = P("a.json"), b = P("b.json");
        JsonStore.Write(a, JObject.Parse("{\"k\":\"a\"}"));
        JsonStore.Write(b, JArray.Parse("[1,2,3]"));
        JsonStore.Flush();

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal("a", JObject.Parse(File.ReadAllText(a)).Value<string>("k"));
        Assert.Equal(3, JArray.Parse(File.ReadAllText(b)).Count);
    }

    [Fact]
    public void Запись_идёт_через_tmp_и_не_оставляет_обрезков()
    {
        // На drvfs (диск E: смонтирован биндом) обрыв посреди WriteAllText оставил бы
        // обрезанный JSON, и холодный старт прочитал бы мусор.
        string p = P("атомарно.json");
        JsonStore.Write(p, JObject.Parse("{\"big\":\"" + new string('x', 5000) + "\"}"));
        JsonStore.Flush();

        Assert.True(File.Exists(p));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(5000, JObject.Parse(File.ReadAllText(p)).Value<string>("big").Length);
    }

    [Fact]
    public void Удаление_убирает_и_из_РАМ_и_с_диска()
    {
        string p = P("del.json");
        JsonStore.Write(p, JObject.Parse("{\"v\":1}"));
        JsonStore.Flush();
        Assert.True(File.Exists(p));

        JsonStore.Remove(p);
        Assert.Null(JsonStore.ReadObject(p));        // из РАМ пропало сразу
        JsonStore.Flush();
        Assert.False(File.Exists(p));
    }

    // ── инвариант 3: диск недоступен — работаем дальше ────────────────────

    [Fact]
    public void Недоступный_диск_не_роняет_ни_чтение_ни_запись()
    {
        // Путь заведомо непригоден для записи. Значение обязано остаться доступным из РАМ:
        // потеря диска = потеря скорости холодного старта, а не корректности.
        string bad = Path.Combine(_dir, "нет-такого\0каталога", "x.json");
        var ex = Record.Exception(() =>
        {
            JsonStore.Write(bad, JObject.Parse("{\"v\":42}"));
            JsonStore.Flush();
        });
        Assert.Null(ex);
        Assert.Equal(42, JsonStore.ReadObject(bad).Value<int>("v"));
    }

    // ── листинг каталога ──────────────────────────────────────────────────

    [Fact]
    public void Листинг_кешируется_и_сбрасывается_явно()
    {
        File.WriteAllText(P("1.json"), "{}");
        Assert.Single(JsonStore.List(_dir, "*.json"));

        File.WriteAllText(P("2.json"), "{}");
        Assert.Single(JsonStore.List(_dir, "*.json"));        // из кеша, диск не перечитан

        JsonStore.ForgetDir(_dir);
        Assert.Equal(2, JsonStore.List(_dir, "*.json").Length);
    }

    // ── прогрев и сверка ──────────────────────────────────────────────────

    [Fact]
    public void Прогрев_поднимает_каталог_и_дальше_чтение_без_диска()
    {
        for (int i = 0; i < 10; i++) File.WriteAllText(P("f" + i + ".json"), "{\"i\":" + i + "}");

        Assert.Equal(10, JsonStore.Warm(_dir));
        var after = JsonStore.Stats();

        for (int i = 0; i < 10; i++)
            Assert.Equal(i, JsonStore.ReadObject(P("f" + i + ".json")).Value<int>("i"));

        Assert.Equal(after.diskReads, JsonStore.Stats().diskReads);
    }

    [Fact]
    public void Прогрев_не_тащит_tmp_файлы()
    {
        File.WriteAllText(P("ok.json"), "{}");
        File.WriteAllText(P("ok.json.tmp"), "{обрезок");
        JsonStore.Warm(_dir, "*.json*");
        Assert.Null(JsonStore.ReadObject(P("ok.json.tmp")));
    }

    [Fact]
    public void Сверка_подхватывает_появившийся_файл()
    {
        string p = P("поздний.json");
        Assert.Null(JsonStore.ReadObject(p));            // негативный кеш взведён

        File.WriteAllText(p, "{\"v\":5}");               // файл появился мимо нас
        Assert.Null(JsonStore.ReadObject(p));            // РАМ всё ещё помнит «нет»

        JsonStore.Reconcile(_dir);
        Assert.Equal(5, JsonStore.ReadObject(p).Value<int>("v"));
    }

    [Fact]
    public void Сверка_не_затирает_несохранённое()
    {
        // ⚠️ Грязный ключ свежее диска по определению. Если сверка его перечитает,
        // мы потеряем правку, ещё не доехавшую до диска.
        string p = P("грязный.json");
        File.WriteAllText(p, "{\"v\":1}");
        JsonStore.ReadObject(p);

        JsonStore.Write(p, JObject.Parse("{\"v\":2}"));  // в РАМ 2, на диске всё ещё 1
        JsonStore.Reconcile(_dir);
        Assert.Equal(2, JsonStore.ReadObject(p).Value<int>("v"));

        JsonStore.Flush();
        Assert.Equal(2, JObject.Parse(File.ReadAllText(p)).Value<int>("v"));
    }

    [Fact]
    public void Сверка_забывает_удалённый_с_диска_файл()
    {
        string p = P("исчез.json");
        File.WriteAllText(p, "{\"v\":1}");
        Assert.NotNull(JsonStore.ReadObject(p));

        File.Delete(p);
        JsonStore.Reconcile(_dir);
        Assert.Null(JsonStore.ReadObject(p));
    }

    // ── зеркало на диск E: ────────────────────────────────────────────────

    [Fact]
    public void Зеркало_дублирует_файл_но_не_является_источником_истины()
    {
        // Решение владельца: «на диск E пишется как место, откуда тянуть кеш».
        // 🔴 Зеркало именно зеркало: meta/local — единственная запись о том, что скачано,
        // и держать её authoritative на drvfs нельзя (файловые локи bind-маунта).
        string cache = TestEnv.FreshCache();
        string mirror = Path.Combine(_dir, "hot");
        string prev = ModInit.conf.hotMirrorPath;
        ModInit.conf.hotMirrorPath = mirror;
        try
        {
            string p = Path.Combine(cache, "meta", "abc.json");
            JsonStore.Write(p, JObject.Parse("{\"title\":\"Тайтл\"}"));
            JsonStore.Flush();

            Assert.True(File.Exists(p), "основная копия обязана лежать в томе");
            string m = Path.Combine(mirror, "meta", "abc.json");
            Assert.True(File.Exists(m), "зеркало обязано появиться на E:");
            Assert.Equal("Тайтл", JObject.Parse(File.ReadAllText(m)).Value<string>("title"));
        }
        finally { ModInit.conf.hotMirrorPath = prev; }
    }

    [Fact]
    public void Сломанное_зеркало_не_ломает_основную_запись()
    {
        // Инвариант: сбой drvfs не имеет права уронить запись на ext4-том.
        string cache = TestEnv.FreshCache();
        string prev = ModInit.conf.hotMirrorPath;
        ModInit.conf.hotMirrorPath = "нет\0такого";
        try
        {
            string p = Path.Combine(cache, "local", "def.json");
            var ex = Record.Exception(() =>
            {
                JsonStore.Write(p, JObject.Parse("{\"v\":1}"));
                JsonStore.Flush();
            });
            Assert.Null(ex);
            Assert.True(File.Exists(p));
            Assert.Equal(1, JsonStore.ReadObject(p).Value<int>("v"));
        }
        finally { ModInit.conf.hotMirrorPath = prev; }
    }

    [Fact]
    public void Пустой_hotMirrorPath_выключает_зеркало()
    {
        string cache = TestEnv.FreshCache();
        string prev = ModInit.conf.hotMirrorPath;
        ModInit.conf.hotMirrorPath = null;
        try
        {
            string p = Path.Combine(cache, "meta", "ghi.json");
            JsonStore.Write(p, JObject.Parse("{\"v\":1}"));
            JsonStore.Flush();
            Assert.True(File.Exists(p));
        }
        finally { ModInit.conf.hotMirrorPath = prev; }
    }

    // ── параллельная нагрузка ─────────────────────────────────────────────

    [Fact]
    public void Параллельные_чтения_и_записи_не_рвут_стор()
    {
        // /qdl/list открывается с нескольких устройств одновременно, а маркеры в это же
        // время переписывает воркер скачивания.
        string[] paths = Enumerable.Range(0, 8).Select(i => P("p" + i + ".json")).ToArray();
        foreach (string p in paths) JsonStore.Write(p, JObject.Parse("{\"n\":0}"));

        var ex = Record.Exception(() => Parallel.For(0, 200, i =>
        {
            string p = paths[i % paths.Length];
            if (i % 3 == 0) JsonStore.Write(p, JObject.Parse("{\"n\":" + i + "}"));
            else Assert.NotNull(JsonStore.ReadObject(p));
        }));

        Assert.Null(ex);
        JsonStore.Flush();
        foreach (string p in paths) Assert.NotNull(JsonStore.ReadObject(p));
    }
}
