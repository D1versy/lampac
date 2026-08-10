using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// e2e ScanEpisodeNotifications × activity: докачанная серия (основной или донорской раздачи)
/// бампает штамп ОСНОВНОГО hash в activity.json; baseline-сидирование и недокачанные серии — нет.
/// qBit — фейковый стек под production Qbit(); AutoTranscodeOverlay без local-маркера — no-op,
/// ScanReplacements на 404-ответах фейка — no-op (оба и так в try/catch).
/// </summary>
public class ScanActivityTests
{
    const string MAIN  = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string DONOR = "dddddddddddddddddddddddddddddddddddddddd";

    static string File(string name, double progress, int index = 0)
        => $"{{\"index\":{index},\"name\":\"{name}\",\"size\":100,\"progress\":{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    static void Seed(JArray watch)
    {
        Access.SaveWatch(watch);
        using var db = new SqlContext();
        db.Database.EnsureCreated();
    }

    static JObject Monitor(bool withDonor = false)
    {
        var m = new JObject { ["hash"] = MAIN, ["id"] = 42, ["title"] = "Сериал", ["link"] = "magnet:x" };
        if (withDonor) m["donors"] = new JArray { new JObject { ["hash"] = DONOR, ["eps"] = new JArray() } };
        return m;
    }

    static async Task<int> RunScan(string mainFiles, string donorFiles = null)
    {
        var fake = new FakeQbit().Json("files?hash=" + MAIN, mainFiles);
        if (donorFiles != null) fake.Json("files?hash=" + DONOR, donorFiles);
        Access.SeedQbitFake(fake.BuildHandler());
        try { return await QbitController.ScanEpisodeNotifications(); }
        finally { Access.ResetQbitFake(); }
    }

    [Fact]
    public async Task Baseline_seeds_without_noti_and_without_bump()
    {
        TestEnv.FreshCache();
        Seed(new JArray { Monitor() });

        int created = await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "," + File("Show.S01E02.mkv", 1.0, 1) + "]");

        Assert.Equal(0, created);                       // первый проход — только база
        Assert.Empty(Access.ActivityLoad());            // и никакого бампа

        // повторный проход с теми же сериями — всё уже в seen
        Assert.Equal(0, await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "," + File("Show.S01E02.mkv", 1.0, 1) + "]"));
        Assert.Empty(Access.ActivityLoad());
    }

    [Fact]
    public async Task Finished_episode_bumps_main_hash()   // регрессия: «докачка двигает карточку»
    {
        TestEnv.FreshCache();
        Seed(new JArray { Monitor() });
        await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "]");                 // baseline

        int created = await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "," + File("Show.S01E02.mkv", 1.0, 1) + "]");

        Assert.Equal(1, created);
        Assert.True((Access.ActivityLoad().Value<long?>(MAIN) ?? 0) > 0);
    }

    [Fact]
    public async Task Donor_episode_bumps_MAIN_hash()      // регрессия: «серия временно с другой раздачи двигает»
    {
        TestEnv.FreshCache();
        Seed(new JArray { Monitor(withDonor: true) });
        // baseline: доноры для noti пропускаются, но роут файлов донора обязан отвечать —
        // 404 ScanReplacements честно трактует как «донора удалили извне» и забывает запись
        await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "]",
                      "[" + File("Show.S01E03.mkv", 0.5) + "]");

        int created = await RunScan(
            "[" + File("Show.S01E01.mkv", 1.0) + "]",
            "[" + File("Show.S01E03.mkv", 1.0) + "]");   // серия докачалась у ДОНОРА

        Assert.Equal(1, created);
        var a = Access.ActivityLoad();
        Assert.True((a.Value<long?>(MAIN) ?? 0) > 0);    // бамп на карточке сериала...
        Assert.Null(a[DONOR]);                            // ...а не на невидимом доноре
    }

    [Fact]
    public async Task Unfinished_episode_does_not_bump()
    {
        TestEnv.FreshCache();
        Seed(new JArray { Monitor() });
        await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "]");                 // baseline

        int created = await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "," + File("Show.S01E02.mkv", 0.5, 1) + "]");

        Assert.Equal(0, created);                        // серия ещё качается
        Assert.Empty(Access.ActivityLoad());
    }
}
