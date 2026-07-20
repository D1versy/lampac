using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>Реконсиляция watch.json (SaveWatchReconciled) и безопасное удаление доноров по категории.</summary>
public class WatchReconcileTests
{
    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string C = "cccccccccccccccccccccccccccccccccccccccc";

    static JObject Entry(string hash, int stale = 0) => new JObject { ["hash"] = hash, ["title"] = "t", ["stale"] = stale };

    [Fact]
    public void Reconciled_PreservesInteractiveAdd_DropsInteractiveRemove()
    {
        TestEnv.FreshCache();
        // старт прохода: на диске {A, B}
        HunterAccess.SaveWatch(new JArray(Entry(A), Entry(B)));
        var orig = HunterAccess.WatchHashes(HunterAccess.LoadWatch());

        // пока шёл проход: интерактивно добавили C (WatchAdd) и убрали B (WatchRemove)
        HunterAccess.SaveWatch(new JArray(Entry(A), Entry(C)));

        // наш проход мутировал свой снимок (A.stale=5) и сохраняет реконсиляцией
        var working = new JArray(Entry(A, stale: 5), Entry(B));
        HunterAccess.SaveWatchReconciled(working, orig);

        var final = HunterAccess.LoadWatch();
        var hashes = final.Select(x => x.Value<string>("hash")).OrderBy(x => x).ToList();
        Assert.Equal(new[] { A, C }, hashes);                                   // C сохранён, B убран
        Assert.Equal(5, final.First(x => x.Value<string>("hash") == A).Value<int>("stale"));   // наша правка A цела
    }

    [Fact]
    public void Reconciled_ReGrabHashChange_NoResurrection()
    {
        TestEnv.FreshCache();
        // старт: {A}
        HunterAccess.SaveWatch(new JArray(Entry(A)));
        var orig = HunterAccess.WatchHashes(HunterAccess.LoadWatch());   // {A}

        // на диске всё ещё {A} (никто не трогал); наш проход сделал re-grab A→C (hash сменился)
        var working = new JArray(Entry(C));
        HunterAccess.SaveWatchReconciled(working, orig);

        var final = HunterAccess.LoadWatch();
        // старый A НЕ воскрешён (его hash был в orig → не считается интерактивным add)
        Assert.Equal(new[] { C }, final.Select(x => x.Value<string>("hash")).ToArray());
    }

    [Fact]
    public async Task QbitDeleteDonorSafe_OnlyDonorCategory()
    {
        TestEnv.EnsureConf();
        ModInit.conf.category = "lampa";
        ModInit.conf.donorCategory = "";   // → "lampa-donor"

        // донорская категория → удаляем с файлами
        var donor = new FakeQbit()
            .Json("/torrents/info", "[{\"hash\":\"" + B + "\",\"category\":\"lampa-donor\"}]")
            .Text("/torrents/delete", "");
        var dc = donor.Build();
        await HunterAccess.QbitDeleteDonorSafe(dc, B);
        Assert.Contains(donor.Requests, r => r.RequestUri.ToString().Contains("/torrents/delete"));

        // пользовательская категория lampa → НЕ удаляем (защита от сноса чужих файлов)
        var user = new FakeQbit()
            .Json("/torrents/info", "[{\"hash\":\"" + B + "\",\"category\":\"lampa\"}]")
            .Text("/torrents/delete", "");
        var uc = user.Build();
        await HunterAccess.QbitDeleteDonorSafe(uc, B);
        Assert.DoesNotContain(user.Requests, r => r.RequestUri.ToString().Contains("/torrents/delete"));
    }

    [Fact]
    public async Task QbitDeleteDonorSafe_TorrentGone_NoDelete()
    {
        TestEnv.EnsureConf();
        var fake = new FakeQbit().Json("/torrents/info", "[]").Text("/torrents/delete", "");
        var c = fake.Build();
        await HunterAccess.QbitDeleteDonorSafe(c, B);
        Assert.DoesNotContain(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/delete"));
    }
}
