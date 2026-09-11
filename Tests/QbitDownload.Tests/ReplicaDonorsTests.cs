using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Доноры охоты на реплике (ReplicaDonors.cs, qdl 2.116.1).
///
/// Боевой повод 11.09.2026: серия 11 «Изгнанного… тяжёлого рыцаря» пришла дома донором охоты в
/// 11:01, дом дважды лёг после обеда, клиенты ушли на tv2 — а там у сериала 10 серий, потому что
/// манифест доноров не отдавал вовсе, и при этом лента (зеркало дома) показывала «вышла серия 11».
///
/// Что закреплено: разбор поля donors (старый дом — пусто), бюджет считает донора вместе с
/// основной, снятие — только когда дом донора больше не держит, показ не опережает закачку
/// (донорская строка появляется лишь при живом торренте в своём qBit), форма добавления —
/// донорская категория + тег + ОСТАНОВЛЕННЫМ, приоритеты как у дома, и стартовая уборка
/// ReconcileDonors на реплике не работает (иначе сносила бы доноров с файлами на каждом старте).
/// </summary>
[Collection("qbit-fake")]
public class ReplicaDonorsTests
{
    const long GiB = 1024L * 1024 * 1024;
    const string MainHash = "dbbc5203872dabaac99423fdc805cdcf33397fe5";
    const string DonorHash = "d776c88bbb9f18640f3ac268be065b63af206aae";
    const string D2 = "2222222222222222222222222222222222222222";
    const string D3 = "3333333333333333333333333333333333333333";
    const string D4 = "4444444444444444444444444444444444444444";

    readonly string _cache;

    public ReplicaDonorsTests()
    {
        _cache = TestEnv.FreshCache();
        ModInit.conf.category = "lampa";
        ModInit.conf.donorCategory = "";
        ModInit.conf.downloadsPath = "/downloads";
        ModInit.conf.replicaRole = "";
        ModInit.conf.replicaDonors = true;
    }

    // ── обвязка ───────────────────────────────────────────────────────────

    static JObject Ep(string epkey, int season, int ep, int fileIndex, string status = "hunted")
        => new JObject { ["epkey"] = epkey, ["season"] = season, ["ep"] = ep, ["fileIndex"] = fileIndex, ["status"] = status };

    static JObject DonorRec(string hash, params JObject[] eps)
        => new JObject { ["hash"] = hash, ["score"] = 84.8, ["quality"] = 1080, ["eps"] = new JArray(eps.Cast<object>().ToArray()) };

    /// <summary>Пункт torrents[] манифеста так, как его собирает дом (ReplicaDonorsSnapshot).</summary>
    static JObject ManifestItem(string hash, long sizeGb, params JObject[] donors)
    {
        var arr = new JArray();
        foreach (var d in donors)
        {
            var x = (JObject)d.DeepClone();
            x["name"] = "донор " + x.Value<string>("hash")?.Substring(0, 4);
            x["size"] = 1_535_615_888L;
            x["private"] = false;
            x["numComplete"] = 24;
            arr.Add(x);
        }
        var it = new JObject { ["hash"] = hash, ["name"] = "сериал", ["size"] = sizeGb * GiB, ["progress"] = 1.0, ["added"] = 1, ["activity"] = 1, ["private"] = false, ["numComplete"] = 5 };
        if (arr.Count > 0) it["donors"] = arr;
        return it;
    }

    static JToken F(int index, string name, double progress, long size, int priority = 1)
        => new JObject { ["index"] = index, ["name"] = name, ["progress"] = progress, ["size"] = size, ["priority"] = priority };

    static JArray MainFiles()
    {
        var a = new JArray();
        for (int i = 1; i <= 10; i++) a.Add(F(i - 1, $"Show/Show S01E{i:00}.mkv", 1.0, 1_500_000_000));
        return a;
    }

    static JArray DonorFiles(int wantedPrio = 1, int extraIndex = -1)
    {
        var a = new JArray();
        for (int i = 1; i <= 11; i++)
            a.Add(F(i - 1, $"Other/Jukishi - {i:00} (1080p).mkv", i == 11 ? 1.0 : 0.0, 1_535_615_888,
                    i == 11 ? wantedPrio : (i - 1 == extraIndex ? 1 : 0)));
        return a;
    }

    static string Body(HttpRequestMessage r) => r.Content == null ? "" : r.Content.ReadAsStringAsync().Result;
    static IEnumerable<HttpRequestMessage> Calls(FakeQbit f, string needle) => f.Requests.Where(r => (r.RequestUri?.ToString() ?? "").Contains(needle));

    static async Task<T> Invoke<T>(string name, params object[] args)
    {
        var t = (Task)Access.Call(name, args);
        await t;
        return (T)t.GetType().GetProperty("Result")!.GetValue(t)!;
    }

    void Snapshot(params JObject[] donors)
        => JsonStore.WriteNow(Path.Combine(_cache, "replica-donors.json"), new JObject { [MainHash] = new JArray(donors.Cast<object>().ToArray()) });

    void Meta(int id)
    {
        Directory.CreateDirectory(Path.Combine(_cache, "meta"));
        File.WriteAllText(Path.Combine(_cache, "meta", MainHash + ".json"), new JObject { ["id"] = id, ["title"] = "сериал" }.ToString());
    }

    // ── разбор манифеста ──────────────────────────────────────────────────

    [Fact]
    public void Старый_дом_без_поля_donors_читается_как_пусто()
    {
        Assert.Empty(QbitController.ReplicaParseDonors(ManifestItem(MainHash, 15)));
        Assert.Empty(QbitController.ReplicaParseDonors(null));
    }

    [Fact]
    public void Разбор_берёт_донора_и_выбрасывает_замещённые_серии()
    {
        var item = ManifestItem(MainHash, 15,
            DonorRec(DonorHash, Ep("s1e11", 1, 11, 10), Ep("s1e9", 1, 9, 8, status: "replaced")),
            DonorRec(D2, Ep("s1e8", 1, 8, 7, status: "replaced")),   // всё замещено — качать нечего
            DonorRec("не-хеш", Ep("s1e1", 1, 1, 0)));

        var donors = QbitController.ReplicaParseDonors(item);

        var d = Assert.Single(donors);
        Assert.Equal(DonorHash, d.hash);
        Assert.False(d.priv);
        Assert.Equal(24, d.numComplete);
        Assert.Equal(1_535_615_888L, d.size);
        Assert.Equal(new[] { 10 }, d.Wanted);
        Assert.Equal("s1e11", d.EpKeys);
        Assert.Equal(1080, d.rec.Value<int>("quality"));   // MergeEpisodeFiles выбирает копию по этим полям
    }

    // ── бюджет ────────────────────────────────────────────────────────────

    [Fact]
    public void План_считает_донора_вместе_с_основной()
    {
        // 🎯 Без этого донорские файлы съедали бы место мимо ватерлиний: бюджет 100 ГБ, нижняя 85%.
        // a = 50 ГБ + донор 40 ГБ = 90 > 85 → не влезает; b = 30 → влезает.
        var a = new QbitController.ReplicaItem { hash = "a", name = "a", size = 50 * GiB, activity = 300, added = 300, donorBytes = 40 * GiB };
        var b = new QbitController.ReplicaItem { hash = "b", name = "b", size = 30 * GiB, activity = 200, added = 200 };

        var plan = QbitController.ReplicaPlan(new[] { a, b }, 100 * GiB, 85, 100);

        Assert.Equal(new[] { "b" }, plan.Select(x => x.hash));
        Assert.Equal(90 * GiB, a.planSize);
    }

    // ── снятие ────────────────────────────────────────────────────────────

    [Fact]
    public void Кандидат_на_снятие_только_тот_кого_дом_больше_не_держит()
    {
        // D1 — упомянут домом у основной, которая у нас есть → остаётся;
        // D2 — дом снял → кандидат; D3 — стал у дома основной → повышение, не снятие; D4 — снят домом → кандидат.
        var referenced = new HashSet<string>(new[] { DonorHash }, StringComparer.OrdinalIgnoreCase);
        var mains = new HashSet<string>(new[] { MainHash, D3 }, StringComparer.OrdinalIgnoreCase);

        var cands = QbitController.ReplicaDonorOrphans(new[] { DonorHash, D2, D3, D4, "мусор" }, referenced, mains);

        Assert.Equal(new[] { D2, D4 }, cands);
    }

    // ── watch-запись: дом и реплика ───────────────────────────────────────

    [Fact]
    public void Дома_WatchItemFor_читает_watch_json()
    {
        File.WriteAllText(Path.Combine(_cache, "watch.json"),
            new JArray(new JObject { ["hash"] = MainHash, ["donors"] = new JArray(DonorRec(DonorHash, Ep("s1e11", 1, 11, 10))) }).ToString());

        var w = (JObject)Access.Call("WatchItemFor", MainHash, null);
        Assert.NotNull(w);
        Assert.Equal(DonorHash, w["donors"]![0]!.Value<string>("hash"));
        Assert.Null(Access.Call("WatchItemFor", D2, null));
    }

    [Fact]
    public void На_реплике_WatchItemFor_читает_привезённый_снимок()
    {
        Snapshot(DonorRec(DonorHash, Ep("s1e11", 1, 11, 10)));
        ModInit.conf.replicaRole = "replica";
        try
        {
            var w = (JObject)Access.Call("WatchItemFor", MainHash.ToUpperInvariant(), null);
            Assert.NotNull(w);
            Assert.Equal(DonorHash, w["donors"]![0]!.Value<string>("hash"));
            Assert.Null(w["next"] as JObject);   // преемников на реплике нет по построению
            Assert.Null(Access.Call("WatchItemFor", D2, null));
        }
        finally { ModInit.conf.replicaRole = ""; }
    }

    // ── читатели ──────────────────────────────────────────────────────────

    [Fact]
    public async Task На_реплике_серия_донора_видна_только_когда_торрент_уже_здесь()
    {
        // 🎯 Показ не опережает закачку: снимок из дома есть всегда, строка — только при живом
        // торренте в своём qBit (MergeEpisodeFiles: dfiles == null → пропуск).
        Snapshot(DonorRec(DonorHash, Ep("s1e11", 1, 11, 10)));
        Meta(270603);
        ModInit.conf.replicaRole = "replica";
        try
        {
            Access.SeedQbitFake(new FakeQbit()
                .Json("/torrents/files?hash=" + MainHash, MainFiles().ToString(Newtonsoft.Json.Formatting.None))
                .Json("/torrents/files?hash=" + DonorHash, DonorFiles().ToString(Newtonsoft.Json.Formatting.None))
                .BuildHandler());
            var rows = await Invoke<JArray>("EpisodesJson", MainHash);

            Assert.Equal(11, rows.Count);
            var e11 = rows.First(r => r.Value<int?>("episode") == 11);
            Assert.Equal("donor", e11.Value<string>("source"));
            Assert.Equal(DonorHash, e11.Value<string>("hash"));
            Assert.Equal(10, e11.Value<int>("index"));
            Assert.Equal("t270603:s1e11", e11.Value<string>("tl"));   // ключ таймлайна тот же, что дома

            // донора у нас ещё нет — 10 серий, без ошибки
            Access.SeedQbitFake(new FakeQbit()
                .Json("/torrents/files?hash=" + MainHash, MainFiles().ToString(Newtonsoft.Json.Formatting.None))
                .BuildHandler());
            rows = await Invoke<JArray>("EpisodesJson", MainHash);
            Assert.Equal(10, rows.Count);
            Assert.DoesNotContain(rows, r => r.Value<string>("source") == "donor");
        }
        finally { Access.ResetQbitFake(); ModInit.conf.replicaRole = ""; }
    }

    [Fact]
    public async Task На_реплике_живой_прогресс_знает_хеш_донора()
    {
        // Иначе строка донорской серии на экране серий залипла бы на снимке /qdl/episodes.
        Snapshot(DonorRec(DonorHash, Ep("s1e11", 1, 11, 10)));
        ModInit.conf.replicaRole = "replica";
        Access.SeedQbitFake(new FakeQbit()
            .Json("/torrents/files?hash=" + MainHash, MainFiles().ToString(Newtonsoft.Json.Formatting.None))
            .Json("/torrents/files?hash=" + DonorHash, DonorFiles().ToString(Newtonsoft.Json.Formatting.None))
            .BuildHandler());
        try
        {
            QbitController.DropProgressCache();
            var res = await Invoke<JObject>("ProgressFilesFor", MainHash);
            Assert.NotNull(res[MainHash]);
            Assert.NotNull(res[DonorHash]);
        }
        finally { Access.ResetQbitFake(); QbitController.DropProgressCache(); ModInit.conf.replicaRole = ""; }
    }

    // ── формы qBit ────────────────────────────────────────────────────────

    [Fact]
    public async Task Донор_заливается_остановленным_в_донорскую_категорию_с_тегом()
    {
        // 🔴 Остановленным: у свежедобавленной раздачи «нужны» все файлы, и без стопа реплика
        // успела бы начать качать весь сезон до того, как выставлены приоритеты.
        string body = null;
        var c = FakeHttpMessageHandler.Client(req => { body = req.Content!.ReadAsStringAsync().Result; return FakeHttpMessageHandler.Text("Ok."); });

        var (outcome, status) = await Invoke<(QbitAddStatus outcome, int status)>("ReplicaUploadTorrent", c, new byte[] { 1, 2, 3 }, DonorHash, "lampa-donor", "qdl-donor", true);

        Assert.Equal(QbitAddStatus.Added, outcome);
        Assert.Equal(200, status);
        Assert.Contains("lampa-donor", body);
        Assert.Contains("qdl-donor", body);
        Assert.Contains("stopped", body);
        Assert.Contains("paused", body);   // qBit v4 знает только paused
    }

    [Fact]
    public async Task Основная_заливается_как_раньше_без_стопа_и_тега()
    {
        string body = null;
        var c = FakeHttpMessageHandler.Client(req => { body = req.Content!.ReadAsStringAsync().Result; return FakeHttpMessageHandler.Text("Ok."); });

        await Invoke<(QbitAddStatus outcome, int status)>("ReplicaUploadTorrent", c, new byte[] { 1 }, MainHash, "lampa", null, false);

        Assert.Contains("lampa", body);
        Assert.DoesNotContain("stopped", body);
        Assert.DoesNotContain("qdl-donor", body);
        Assert.Contains("ratioLimit", body);
    }

    [Fact]
    public async Task Приоритеты_как_у_дома_всё_выключить_нужное_включить_и_старт()
    {
        var fake = new FakeQbit()
            .Json("/torrents/files", DonorFiles().ToString(Newtonsoft.Json.Formatting.None))
            .Text("/torrents/filePrio", "")
            .Text("/torrents/start", "");
        var c = fake.Build();

        Assert.True(await Invoke<bool>("ReplicaDonorInstall", c, DonorHash, new List<int> { 10 }));

        var prios = Calls(fake, "/torrents/filePrio").Select(Body).ToList();
        Assert.Equal(2, prios.Count);
        Assert.Contains("priority=0", prios[0]);
        Assert.Contains("id=0%7C1%7C2", prios[0]);      // все файлы
        Assert.Contains("id=10&priority=1", prios[1]);   // только нужная серия
        Assert.Single(Calls(fake, "/torrents/start"));
    }

    [Fact]
    public async Task Нет_нужного_индекса_в_раздаче_торрент_остаётся_остановленным()
    {
        var fake = new FakeQbit()
            .Json("/torrents/files", DonorFiles().ToString(Newtonsoft.Json.Formatting.None))
            .Text("/torrents/filePrio", "").Text("/torrents/start", "");

        Assert.False(await Invoke<bool>("ReplicaDonorInstall", fake.Build(), DonorHash, new List<int> { 99 }));
        Assert.Empty(Calls(fake, "/torrents/filePrio"));
        Assert.Empty(Calls(fake, "/torrents/start"));
    }

    [Fact]
    public async Task Доводка_выключает_лишнее_включает_нужное_и_запускает()
    {
        // Добавили остановленным, до приоритетов не дошли (или дом захотел ещё одну серию):
        // файл 3 включён зря, нужная 10 выключена, торрент стоит.
        var fake = new FakeQbit()
            .Json("/torrents/files", DonorFiles(wantedPrio: 0, extraIndex: 3).ToString(Newtonsoft.Json.Formatting.None))
            .Text("/torrents/filePrio", "").Text("/torrents/start", "");
        var info = new JObject { ["state"] = "stoppedDL", ["progress"] = 0.0 };

        Assert.True(await Invoke<bool>("ReplicaDonorHeal", fake.Build(), DonorHash, info, new List<int> { 10 }));

        var prios = Calls(fake, "/torrents/filePrio").Select(Body).ToList();
        Assert.Contains(prios, p => p.Contains("id=3&priority=0"));
        Assert.Contains(prios, p => p.Contains("id=10&priority=1"));
        Assert.Single(Calls(fake, "/torrents/start"));
    }

    [Fact]
    public async Task Доводка_не_трогает_исправного_и_докачанного_донора()
    {
        var fake = new FakeQbit()
            .Json("/torrents/files", DonorFiles().ToString(Newtonsoft.Json.Formatting.None))
            .Text("/torrents/filePrio", "").Text("/torrents/start", "");

        // качается штатно
        Assert.False(await Invoke<bool>("ReplicaDonorHeal", fake.Build(), DonorHash, new JObject { ["state"] = "downloading", ["progress"] = 0.4 }, new List<int> { 10 }));
        // 🎯 докачан и остановлен qBit по ратио — не будить: иначе реплика сидировала бы вечно
        Assert.False(await Invoke<bool>("ReplicaDonorHeal", fake.Build(), DonorHash, new JObject { ["state"] = "stoppedUP", ["progress"] = 1.0 }, new List<int> { 10 }));

        Assert.Empty(Calls(fake, "/torrents/filePrio"));
        Assert.Empty(Calls(fake, "/torrents/start"));
    }

    // ── стартовая уборка ──────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileDonors_на_реплике_ничего_не_удаляет()
    {
        // 🔴 watch.json на реплике пуст по построению: домашняя уборка «на кого не ссылается
        // watch.json» снесла бы всех доноров реплики С ФАЙЛАМИ при каждом старте.
        File.WriteAllText(Path.Combine(_cache, "watch.json"), "[]");
        var fake = new FakeQbit()
            .Json("/torrents/info?category=lampa-donor", "[{\"hash\":\"" + DonorHash + "\",\"name\":\"d\",\"category\":\"lampa-donor\",\"content_path\":\"/downloads/Other\"}]")
            .Json("/torrents/info?category=lampa", "[]")
            .Text("/torrents/delete", "");
        Access.SeedQbitFake(fake.BuildHandler());
        try
        {
            ModInit.conf.replicaRole = "replica";
            await QbitController.ReconcileDonors();
            Assert.Empty(Calls(fake, "/torrents/delete"));

            // а дома тот же снимок — сирота, и её снимают (прежнее поведение не тронуто)
            ModInit.conf.replicaRole = "";
            await QbitController.ReconcileDonors();
            Assert.Single(Calls(fake, "/torrents/delete"));
        }
        finally { Access.ResetQbitFake(); ModInit.conf.replicaRole = ""; }
    }
}
