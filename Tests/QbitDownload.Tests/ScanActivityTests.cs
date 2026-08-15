using System.Linq;
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
        // Потолок эфира ходит в TMDB через свой же loopback — в тестах это лишний сетевой шум
        // (он и так fail-open). Само правило проверяется чистым AboveAired в DonorNotiPlanTests.
        ModInit.conf.notifyAiredCap = false;
        Access.SaveWatch(watch);
        using var db = new SqlContext();
        db.Database.EnsureCreated();
    }

    // Запись донора в том виде, в каком её пишет охота: серия, которую она реально заказала (§BS).
    static JObject Eps(string epkey, int season, int ep, int fileIndex, string status = "hunted")
        => new JObject
        {
            ["epkey"] = epkey, ["season"] = season, ["ep"] = ep, ["fileIndex"] = fileIndex,
            ["status"] = status, ["grabbedAt"] = "2026-08-15T01:50:38Z", ["replacedAt"] = null
        };

    static JObject Monitor(bool withDonor = false, JObject donorEp = null)
    {
        var m = new JObject { ["hash"] = MAIN, ["id"] = 42, ["title"] = "Сериал", ["link"] = "magnet:x" };
        if (withDonor)
            m["donors"] = new JArray {
                new JObject {
                    ["hash"] = DONOR,
                    // ⚠️ Пустой eps здесь означал бы «донора пропускаем целиком»: с §BS уведомление
                    // рождается только из заказанных охотой серий, а не из любого докачанного файла.
                    ["eps"] = new JArray(donorEp ?? Eps("s1e3", 1, 3, 0))
                }
            };
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
    public async Task Donor_file_not_requested_does_not_notify()   // §BS: чужой докачанный файл донора — не наша серия
    {
        TestEnv.FreshCache();
        Seed(new JArray { Monitor(withDonor: true, donorEp: Eps("s1e3", 1, 3, 5)) });   // заказан индекс 5, а не 0
        await RunScan("[" + File("Show.S01E01.mkv", 1.0) + "]",
                      "[" + File("Show.S01E03.mkv", 0.5) + "]");                        // baseline

        int created = await RunScan(
            "[" + File("Show.S01E01.mkv", 1.0) + "]",
            "[" + File("Show.S01E03.mkv", 1.0) + "]");   // файл докачан, но охота его не заказывала

        Assert.Equal(0, created);
        Assert.Empty(Access.ActivityLoad());

        using var db = new SqlContext();
        Assert.DoesNotContain(db.seen.ToList(), x => x.epkey == "s1e3");   // и в seen не осело — серия не заглушена
    }

    [Fact]
    public async Task Donor_multiseason_pack_notifies_only_grabbed_episode()   // регрессия «Укрытия» 2026-08-09
    {
        TestEnv.FreshCache();
        Seed(new JArray { Monitor(withDonor: true, donorEp: Eps("s3e7", 3, 7, 2)) });

        string main = "[" + File("Silo.S03E01.1080p.WEB-DL.mkv", 1.0) + "," + File("Silo.S03E06.1080p.WEB-DL.mkv", 1.0, 1) + "]";
        string pack = "[" + File("Укрытие.S02.WEB-DLRip/Silo.S02.E07.Rus.avi", 1.0)
                    + "," + File("Укрытие.S02.WEB-DLRip/Silo.S02.E08.Rus.avi", 1.0, 1)
                    + "," + File("Silo (Season 3)/Silo.S03E07.1080p.WEB-DL.mkv", 1.0, 2) + "]";

        await RunScan(main, pack);                      // baseline: доноров не трогаем
        int created = await RunScan(main, pack);

        Assert.Equal(1, created);                       // ровно одна — заказанная S03E07

        using var db = new SqlContext();
        var n = Assert.Single(db.noti.ToList());
        Assert.Equal("s3e7", n.epkey);
        Assert.EndsWith("· временно с другой раздачи", n.label);
        var keys = db.seen.Select(x => x.epkey).ToList();
        Assert.Contains("s3e7", keys);
        Assert.DoesNotContain("s2e7", keys);            // чужой сезон в seen не оседает
        Assert.DoesNotContain("s2e8", keys);
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
