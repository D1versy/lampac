using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>Формы qBit-запросов охоты (донорские хелперы) — через FakeQbit, без сети.</summary>
public class QbitDonorClientTests
{
    const string Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public QbitDonorClientTests() => TestEnv.EnsureConf();

    [Fact]
    public async Task AddMagnetEx_SendsCategoryTagsStopCondition()
    {
        string body = null;
        var c = FakeHttpMessageHandler.Client(req =>
        {
            body = req.Content.ReadAsStringAsync().Result;
            return FakeHttpMessageHandler.Text("Ok.");
        });
        bool ok = await HunterAccess.QbitAddMagnetEx(c, "magnet:?xt=urn:btih:" + Hash, "lampa-donor", "qdl-donor", stopAfterMeta: true);
        Assert.True(ok);
        Assert.Contains("lampa-donor", body);
        Assert.Contains("qdl-donor", body);
        Assert.Contains("MetadataReceived", body);
    }

    [Fact]
    public async Task AddMagnetEx_DefaultForm_NoDonorFields()
    {
        string body = null;
        var c = FakeHttpMessageHandler.Client(req =>
        {
            body = req.Content.ReadAsStringAsync().Result;
            return FakeHttpMessageHandler.Text("Ok.");
        });
        bool ok = await HunterAccess.QbitAddMagnetEx(c, "magnet:?xt=urn:btih:" + Hash, "lampa");
        Assert.True(ok);
        Assert.DoesNotContain("stopCondition", body);
        Assert.DoesNotContain("tags", body);
    }

    [Fact]
    public async Task AddMagnetEx_Duplicate409_IsSuccess()
    {
        var c = FakeHttpMessageHandler.Client(_ => FakeHttpMessageHandler.Text("Conflict", HttpStatusCode.Conflict));
        Assert.True(await HunterAccess.QbitAddMagnetEx(c, "magnet:?xt=urn:btih:" + Hash, "lampa-donor", "qdl-donor", true));
    }

    [Fact]
    public async Task FilePrio_PipeSeparatedIds()
    {
        string body = null;
        var c = FakeHttpMessageHandler.Client(req =>
        {
            body = req.Content.ReadAsStringAsync().Result;
            return FakeHttpMessageHandler.Text("");
        });
        Assert.True(await HunterAccess.QbitFilePrio(c, Hash, new[] { 0, 2, 5 }, 0));
        Assert.Contains("id=0%7C2%7C5", body);   // 0|2|5 url-encoded
        Assert.Contains("priority=0", body);
    }

    [Fact]
    public async Task FilePrio_EmptyIds_NoRequest()
    {
        var fake = new FakeQbit();
        var c = fake.Build();
        Assert.True(await HunterAccess.QbitFilePrio(c, Hash, new int[0], 1));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task StartTorrent_FallsBackToResume_OnV4()
    {
        var fake = new FakeQbit()
            .Text("/torrents/start", "", HttpStatusCode.NotFound)   // qBit v4: /start нет
            .Text("/torrents/resume", "");
        var c = fake.Build();
        await HunterAccess.QbitStartTorrent(c, Hash);
        Assert.Contains(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/start"));
        Assert.Contains(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/resume"));
    }

    [Fact]
    public async Task StartTorrent_V5_NoResume()
    {
        var fake = new FakeQbit().Text("/torrents/start", "");
        var c = fake.Build();
        await HunterAccess.QbitStartTorrent(c, Hash);
        Assert.DoesNotContain(fake.Requests, r => r.RequestUri.ToString().Contains("/torrents/resume"));
    }

    [Fact]
    public async Task Delete_SendsDeleteFilesFlag()
    {
        string body = null;
        var c = FakeHttpMessageHandler.Client(req =>
        {
            body = req.Content.ReadAsStringAsync().Result;
            return FakeHttpMessageHandler.Text("");
        });
        await HunterAccess.QbitDelete(c, Hash, deleteFiles: true);
        Assert.Contains("deleteFiles=true", body);
        Assert.Contains(Hash, body);
    }

    [Fact]
    public async Task WaitFiles_ReturnsOnceMetadataArrives()
    {
        int calls = 0;
        var c = FakeHttpMessageHandler.Client(req =>
        {
            string url = req.RequestUri.ToString();
            if (url.Contains("/torrents/files"))
            {
                calls++;
                return FakeHttpMessageHandler.Json(calls < 2 ? "[]" : "[{\"index\":0,\"name\":\"a.mkv\",\"size\":100}]");
            }
            return FakeHttpMessageHandler.Json("[]");
        });
        var files = await HunterAccess.QbitWaitFiles(c, Hash, 30);
        Assert.NotNull(files);
        Assert.Single(files);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task TorrentInfo_FirstObject()
    {
        var c = FakeHttpMessageHandler.Client(_ => FakeHttpMessageHandler.Json("[{\"state\":\"stoppedDL\",\"save_path\":\"/downloads\"}]"));
        var info = await HunterAccess.QbitTorrentInfo(c, Hash);
        Assert.Equal("stoppedDL", info.Value<string>("state"));
    }
}
