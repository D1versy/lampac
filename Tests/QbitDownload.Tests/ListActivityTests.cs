using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// e2e экшена /qdl/list: порядок карточек по activity (штамп охоты/докачки поднимает карточку),
/// completion_on с гардом от мусора qBit, транскод-маркер позицию не меняет (§AG),
/// отсев доноров и прунинг сирот activity.json. qBit — фейковый стек под production Qbit().
/// </summary>
public class ListActivityTests
{
    const string HA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string HB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string HD = "dddddddddddddddddddddddddddddddddddddddd";

    static string Torrent(string hash, long addedOn, double progress = 0.5, long completionOn = 0)
        => $"{{\"hash\":\"{hash}\",\"name\":\"t-{hash[..4]}\",\"progress\":{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
         + $"\"state\":\"downloading\",\"size\":100,\"save_path\":\"/downloads\",\"content_path\":\"/downloads/t\","
         + $"\"added_on\":{addedOn},\"completion_on\":{completionOn}}}";

    static async Task<JArray> RunList(string torrentsJson)
    {
        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", torrentsJson).BuildHandler());
        try
        {
            var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var res = await ctrl.List();
            var cr = Assert.IsType<ContentResult>(res);   // ContentTo без StaticacheFeature → обычный Content
            return JArray.Parse(cr.Content);
        }
        finally { Access.ResetQbitFake(); }
    }

    [Fact]
    public async Task List_sorts_by_activity_touch_lifts_old_card()
    {
        TestEnv.FreshCache();
        Access.ActivityTouch(HB, 500);   // старой карточке прилетела серия от охоты

        var list = await RunList("[" + Torrent(HA, 300) + "," + Torrent(HB, 100) + "]");

        Assert.Equal(new[] { HB, HA }, list.Select(x => x.Value<string>("hash")));
        var b = (JObject)list[0];
        Assert.Equal(100, b.Value<long?>("added"));      // added не переписан (совместимость)
        Assert.Equal(500, b.Value<long?>("activity"));
        Assert.Equal(300, list[1].Value<long?>("activity"));   // без событий activity == added
    }

    [Fact]
    public async Task List_completion_lifts_finished_and_ignores_u32_garbage()
    {
        TestEnv.FreshCache();
        var list = await RunList("["
            + Torrent(HA, 300, progress: 0.5, completionOn: 4294967295L) + ","   // качается, completion — мусор
            + Torrent(HB, 100, progress: 1.0, completionOn: 400) + "]");         // докачалась в 400

        Assert.Equal(new[] { HB, HA }, list.Select(x => x.Value<string>("hash")));
        Assert.Equal(400, list[0].Value<long?>("activity"));
        Assert.Equal(300, list[1].Value<long?>("activity"));
    }

    [Fact]
    public async Task List_transcode_marker_without_touch_keeps_position()   // регрессия §AG
    {
        string cache = TestEnv.FreshCache();
        string mp4 = Path.Combine(cache, "Movie.mp4");
        File.WriteAllText(mp4, "x");
        Directory.CreateDirectory(Path.Combine(cache, "local"));
        File.WriteAllText(Path.Combine(cache, "local", HB + ".json"),
            new JObject { ["name"] = "Movie.mp4", ["path"] = mp4, ["size"] = 1, ["added"] = 100 }.ToString());

        var list = await RunList("[" + Torrent(HA, 200) + "]");

        Assert.Equal(new[] { HA, HB }, list.Select(x => x.Value<string>("hash")));
        Assert.Equal(100, list[1].Value<long?>("activity"));   // activity == added → транскод не двигает
    }

    [Fact]
    public async Task List_donor_hashes_are_filtered_out()
    {
        TestEnv.FreshCache();
        Access.SaveWatch(new JArray { new JObject
        {
            ["hash"] = HA, ["id"] = 42, ["title"] = "Сериал", ["link"] = "magnet:x",
            ["donors"] = new JArray { new JObject { ["hash"] = HD } }
        } });

        var list = await RunList("[" + Torrent(HA, 300) + "," + Torrent(HD, 999) + "]");

        Assert.Equal(new[] { HA }, list.Select(x => x.Value<string>("hash")));
    }

    [Fact]
    public async Task List_prunes_stale_orphans_keeps_fresh()
    {
        TestEnv.FreshCache();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Access.ActivityTouch(HB, now - 8 * 86400);   // сирота старше грейса → уйдёт
        Access.ActivityTouch(HD, now - 3600);        // сирота, но свежая → останется

        await RunList("[" + Torrent(HA, 300) + "]");

        var a = Access.ActivityLoad();
        Assert.Null(a[HB]);
        Assert.NotNull(a[HD]);
    }
}
