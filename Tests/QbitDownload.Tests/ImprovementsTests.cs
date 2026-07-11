using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// Тесты улучшений §Z: CodecFromTitle (бейдж HEVC в поиске), PurgeCache (чистка сирот при удалении),
// очередь транскода (EnqueueTranscode/QueuePosition/воркер), CleanupTranscodeParts (уборка .part).

public class CodecFromTitleTests
{
    [Theory]
    [InlineData("Dune.2021.BDRip-HEVC.1080p", "hevc")]
    [InlineData("Фильм x265 10bit", "hevc")]
    [InlineData("Movie.H.265.2160p", "hevc")]
    [InlineData("Movie h265", "hevc")]
    [InlineData("Кино [AV1 2160p]", "av1")]
    [InlineData("Movie.x264.1080p", "h264")]
    [InlineData("Movie.H.264-GROUP", "h264")]
    [InlineData("Movie AVC 1080p", "h264")]
    public void Detects(string title, string codec) => Assert.Equal(codec, Access.CodecFromTitle(title));

    [Theory]
    [InlineData("Фильм 265 серия")]      // голое число — не кодек
    [InlineData("flav1our of life")]     // av1 внутри слова
    [InlineData("AV12 special")]         // av1 с цифрой следом
    [InlineData("Просто название 1080p")]
    [InlineData("")]
    [InlineData(null)]
    public void NullForNonCodec(string title) => Assert.Null(Access.CodecFromTitle(title));

    [Fact]
    public void HevcWinsWhenBothPresent() =>
        Assert.Equal("hevc", Access.CodecFromTitle("Movie x264 + x265 versions"));
}

public class PurgeCacheTests
{
    // посадить файлы кэша раздачи
    static void Seed(string cache, string hash, int? metaId, string link)
    {
        Directory.CreateDirectory(Path.Combine(cache, "meta"));
        Directory.CreateDirectory(Path.Combine(cache, "img"));
        Directory.CreateDirectory(Path.Combine(cache, "links"));
        Directory.CreateDirectory(Path.Combine(cache, "local"));
        if (metaId != null) File.WriteAllText(Path.Combine(cache, "meta", hash + ".json"), new JObject { ["id"] = metaId.Value, ["title"] = "T" }.ToString());
        File.WriteAllBytes(Path.Combine(cache, "img", hash + ".jpg"), new byte[] { 1, 2, 3 });
        if (link != null) File.WriteAllText(Path.Combine(cache, "links", hash + ".json"), new JObject { ["link"] = link, ["query"] = "q" }.ToString());
        File.WriteAllText(Path.Combine(cache, "local", hash + ".json"), new JObject { ["name"] = "n.mp4", ["path"] = "/x/n.mp4" }.ToString());
    }

    static void SeedWatch(string cache, params (string hash, int id, string link)[] rows)
    {
        var a = new JArray();
        foreach (var r in rows) a.Add(new JObject { ["hash"] = r.hash, ["link"] = r.link, ["query"] = "q", ["id"] = r.id, ["title"] = "T" });
        File.WriteAllText(Path.Combine(cache, "watch.json"), a.ToString());
    }

    static string H(char c) => new string(c, 40);

    [Fact]
    public void FullPurge_SingleDownload()
    {
        string cache = TestEnv.FreshCache();
        string h = H('a');
        Seed(cache, h, 123, "http://t/1");
        SeedWatch(cache, (h, 123, "http://t/1"));
        using (var db = new SqlContext())
        {
            db.Database.EnsureCreated();
            db.seen.Add(new SeenModel { seriesKey = "t123", epkey = "s1e1" });
            db.noti.Add(new NotiModel { seriesKey = "t123", hash = h, epkey = "s1e1", created = DateTime.UtcNow });
            db.SaveChanges();
        }

        Access.PurgeCache(h);

        Assert.False(File.Exists(Path.Combine(cache, "meta", h + ".json")));
        Assert.False(File.Exists(Path.Combine(cache, "img", h + ".jpg")));
        Assert.False(File.Exists(Path.Combine(cache, "links", h + ".json")));
        Assert.False(File.Exists(Path.Combine(cache, "local", h + ".json")));
        Assert.Empty(JArray.Parse(File.ReadAllText(Path.Combine(cache, "watch.json"))));
        using (var db = new SqlContext())
        {
            Assert.Empty(db.seen.ToList());
            Assert.Empty(db.noti.ToList());
        }
    }

    [Fact]
    public void RegrabDuplicate_KeepsSeenAndOtherNoti()
    {
        string cache = TestEnv.FreshCache();
        string h1 = H('b'), h2 = H('c');
        Seed(cache, h1, 555, "http://t/old");
        SeedWatch(cache, (h1, 555, "http://t/old"), (h2, 555, "http://t/new"));
        using (var db = new SqlContext())
        {
            db.Database.EnsureCreated();
            db.seen.Add(new SeenModel { seriesKey = "t555", epkey = "s1e1" });
            db.noti.Add(new NotiModel { seriesKey = "t555", hash = h1, epkey = "s1e1", created = DateTime.UtcNow });
            db.noti.Add(new NotiModel { seriesKey = "t555", hash = h2, epkey = "s1e2", created = DateTime.UtcNow });
            db.SaveChanges();
        }

        Access.PurgeCache(h1);

        var watch = JArray.Parse(File.ReadAllText(Path.Combine(cache, "watch.json")));
        Assert.Single(watch);
        Assert.Equal(h2, watch[0].Value<string>("hash"));
        using (var db = new SqlContext())
        {
            Assert.Single(db.seen.ToList());                       // сериал жив у h2 → seen не тронут
            var noti = db.noti.ToList();
            Assert.Single(noti);                                    // noti h1 удалён точечно
            Assert.Equal(h2, noti[0].hash);
        }
    }

    [Fact]
    public void LinkOnly_NoMeta_CleansByLinkKey()
    {
        string cache = TestEnv.FreshCache();
        string h = H('d');
        Seed(cache, h, null, "http://t/linkonly");   // меты нет
        string sk = Access.SeriesKey(0, "http://t/linkonly");
        using (var db = new SqlContext())
        {
            db.Database.EnsureCreated();
            db.seen.Add(new SeenModel { seriesKey = sk, epkey = "e1" });
            db.SaveChanges();
        }

        Access.PurgeCache(h);

        using (var db = new SqlContext())
            Assert.Empty(db.seen.ToList());
    }

    [Fact]
    public void Degenerate_NoMetaNoLink_DoesNotTouchForeignRows()
    {
        string cache = TestEnv.FreshCache();
        string h = H('e');   // никаких файлов/watch — совсем пустая раздача
        string degenerate = Access.SeriesKey(0, null);   // "l" + FNV("")
        using (var db = new SqlContext())
        {
            db.Database.EnsureCreated();
            db.seen.Add(new SeenModel { seriesKey = degenerate, epkey = "e1" });   // «чужая» запись с вырожденным ключом
            db.SaveChanges();
        }

        Access.PurgeCache(h);   // не должен упасть и не должен удалить чужое

        using (var db = new SqlContext())
            Assert.Single(db.seen.ToList());
    }
}

public class TranscodeQueueTests
{
    static string UniqueHash()
    {
        var g = Guid.NewGuid().ToString("N");
        return (g + g).Substring(0, 40);
    }

    static void WaitUntil(Func<bool> cond, int ms = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met in " + ms + "ms");
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void MissingSource_WorkerSetsError_AndSurvives()
    {
        TestEnv.EnsureConf();
        string h1 = UniqueHash(), h2 = UniqueHash();
        string missing = Path.Combine(Path.GetTempPath(), "qdl-tests", Guid.NewGuid().ToString("N") + ".mkv");

        Access.EnqueueTranscode(h1, missing, missing + ".part", missing + ".mp4", 0);
        WaitUntil(() => Access.TcJob(h1)?.state == "error");
        Assert.Contains("не найден", Access.TcJob(h1).error);

        // существующий src, но ffmpeg-бинарь битый → Process.Start кидает → воркер выживает и обрабатывает второй элемент
        string src = Path.Combine(Path.GetTempPath(), "qdl-tests", Guid.NewGuid().ToString("N") + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(src));
        File.WriteAllBytes(src, new byte[] { 0 });
        string prevFfmpeg = ModInit.conf.ffmpeg, prevFfprobe = ModInit.conf.ffprobe;
        ModInit.conf.ffmpeg = Path.Combine(Path.GetTempPath(), "no-such-ffmpeg.exe");
        ModInit.conf.ffprobe = Path.Combine(Path.GetTempPath(), "no-such-ffprobe.exe");
        try
        {
            Access.EnqueueTranscode(h2, src, src + ".part", src + ".mp4", 0);
            WaitUntil(() => Access.TcJob(h2)?.state == "error");
            WaitUntil(Access.TcQueueIdle);
        }
        finally { ModInit.conf.ffmpeg = prevFfmpeg; ModInit.conf.ffprobe = prevFfprobe; }
    }

    [Fact]
    public void Dedup_QueuedOrRunningNotEnqueuedTwice()
    {
        TestEnv.EnsureConf();
        WaitUntil(Access.TcQueueIdle);

        string h = UniqueHash();
        Access.TcJobSet(h, "queued");
        int pos = Access.EnqueueTranscode(h, "irrelevant", "p", "f", 0);
        Assert.Equal(0, pos);                       // в реальную очередь не попал (дедуп по _tcJobs)
        Assert.Equal("queued", Access.TcJob(h).state);   // и job не перезаписан
        WaitUntil(Access.TcQueueIdle);

        string h2 = UniqueHash();
        Access.TcJobSet(h2, "running");
        Access.EnqueueTranscode(h2, "irrelevant", "p", "f", 0);
        Assert.Equal("running", Access.TcJob(h2).state);
        WaitUntil(Access.TcQueueIdle);
    }

    [Fact]
    public void QueuePosition_OrderAndMisses()
    {
        TestEnv.EnsureConf();
        WaitUntil(Access.TcQueueIdle);

        string q1 = UniqueHash(), q2 = UniqueHash(), q3 = UniqueHash();
        Access.TcEnqueueRaw(q1);   // без KickWorker — позиции стабильны
        Access.TcEnqueueRaw(q2);
        Access.TcEnqueueRaw(q3);
        Assert.Equal(1, Access.QueuePosition(q1));
        Assert.Equal(2, Access.QueuePosition(q2));
        Assert.Equal(3, Access.QueuePosition(q3));
        Assert.Equal(0, Access.QueuePosition(UniqueHash()));   // не в очереди

        // дренаж: воркер молча скипнет элементы без job'ов
        Access.KickWorker();
        WaitUntil(Access.TcQueueIdle);
    }
}

public class CleanupTranscodePartsTests
{
    [Fact]
    public void RemovesPartsKeepsFinals()
    {
        TestEnv.EnsureConf();
        string prev = ModInit.conf.downloadsPath;
        string dl = Path.Combine(Path.GetTempPath(), "qdl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dl, "transcoded"));
        File.WriteAllText(Path.Combine(dl, "transcoded", "a.mp4.part"), "x");
        File.WriteAllText(Path.Combine(dl, "transcoded", "b.mp4"), "x");
        ModInit.conf.downloadsPath = dl;
        try
        {
            Access.CleanupTranscodeParts();
            Assert.False(File.Exists(Path.Combine(dl, "transcoded", "a.mp4.part")));
            Assert.True(File.Exists(Path.Combine(dl, "transcoded", "b.mp4")));
        }
        finally { ModInit.conf.downloadsPath = prev; }
    }

    [Fact]
    public void MissingDir_NoThrow()
    {
        TestEnv.EnsureConf();
        string prev = ModInit.conf.downloadsPath;
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-tests", "no-such-" + Guid.NewGuid().ToString("N"));
        try { Access.CleanupTranscodeParts(); }   // просто не должен упасть
        finally { ModInit.conf.downloadsPath = prev; }
    }
}
