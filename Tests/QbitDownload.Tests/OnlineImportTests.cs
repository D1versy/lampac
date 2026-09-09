using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// «Сохранить в Загрузки» из раздела Online (OnlineImport.cs, qdl 2.116): контейнер копирует
// запись в общий бинд и зовёт POST /qdl/online/import — здесь та же кухня без HTTP
// (OnlineImportApply) плюс вердикт строки хелса (OnlineVerdict).
//
// Что стережётся: три файла карточки нужной формы (иначе «Загрузки» её не видят или клиент
// затирает мету TMDB-поиском), пояс изоляции от краулера, путь только под корнем, идемпотентность.
// ─────────────────────────────────────────────────────────────────────────────
public class OnlineImportTests
{
    const string ID = "2026-09-09_20-00-00";

    static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qdl-tests", "online-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static (string root, string video, string poster) Seed(string id = ID)
    {
        string root = NewRoot();
        string dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        string video = Path.Combine(dir, "video.mp4");
        string poster = Path.Combine(dir, "poster.jpg");
        File.WriteAllText(video, new string('v', 2048));
        File.WriteAllBytes(poster, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        return (root, video, poster);
    }

    static JObject Body(string id, string path, string poster = null) => new JObject
    {
        ["id"] = id,
        ["title"] = "Apple Event",
        ["started"] = "2026-09-09T17:00:00.000Z",
        ["durationSec"] = 5400,
        ["sourceUrl"] = "https://www.youtube.com/watch?v=abc",
        ["path"] = path,
        ["poster"] = poster,
        ["bytes"] = 2048
    };

    [Fact]
    public void Хеш_детерминирован_и_не_пересекается_с_xsmart_jut()
    {
        string h = OnlineNet.Hash(ID);
        Assert.Equal(40, h.Length);
        Assert.Equal(h, OnlineNet.Hash(ID));
        Assert.True(Access.ValidHash(h));
        Assert.NotEqual(h, XsmartNet.Hash(6, ID));
    }

    [Fact]
    public async Task Импорт_пишет_мету_постер_маркер_и_карточка_видна_в_Загрузках()
    {
        string cache = TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 0;
        var (root, video, poster) = Seed();
        string prev = ModInit.conf.onlineDownloadsPath;
        ModInit.conf.onlineDownloadsPath = root;
        try
        {
            var (status, error, hash) = QbitController.OnlineImportApply(Body(ID, video, poster));
            Assert.Equal(200, status);
            Assert.Null(error);
            Assert.Equal(OnlineNet.Hash(ID), hash);

            var meta = JObject.Parse(File.ReadAllText(Path.Combine(cache, "meta", hash + ".json")));
            Assert.Equal("online", meta.Value<string>("source"));      // пояс изоляции от краулера
            Assert.Equal(0, meta.Value<int>("id"));                      // клиент откроет qdl_card, не TMDB
            Assert.Equal("Apple Event", meta.Value<string>("title"));
            Assert.Equal("movie", meta.Value<string>("media_type"));
            Assert.Equal(90, meta.Value<int>("runtime"));                // минуты для qdl_card
            Assert.Equal(2026, meta.Value<int>("year"));
            Assert.True(QbitController.NonTorrentSource(meta.Value<string>("source")), "краулер обязан пропускать источник online");

            Assert.True(File.Exists(Path.Combine(cache, "img", hash + ".jpg")), "постер по хеш-пути");

            var marker = JObject.Parse(File.ReadAllText(Path.Combine(cache, "local", hash + ".json")));
            Assert.Equal(false, marker.Value<bool>("overlay"));
            var files = marker["files"] as JArray;
            Assert.NotNull(files);
            Assert.Single(files);
            Assert.Equal(Path.GetFullPath(video).Replace('\\', '/'), files[0].Value<string>("path"));
            Assert.Equal(2048, files[0].Value<long>("size"));
            Assert.Equal("Apple Event.mp4", files[0].Value<string>("name"));
            Assert.Equal(ID, marker["online"].Value<string>("id"));

            // Карточка в /qdl/list — сразу, без рестарта (§BM).
            Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", "[]").BuildHandler());
            try
            {
                var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
                var res = await ctrl.List();
                var arr = JArray.Parse(Assert.IsType<ContentResult>(res).Content);
                var card = arr.FirstOrDefault(x => x.Value<string>("hash") == hash);
                Assert.NotNull(card);
                Assert.Equal("local", card.Value<string>("state"));
                Assert.Equal("Apple Event", card.Value<string>("name"));
                Assert.True(card.Value<bool>("has_poster"));
                Assert.Equal("online", card["meta"].Value<string>("source"));
            }
            finally { Access.ResetQbitFake(); }
        }
        finally { ModInit.conf.onlineDownloadsPath = prev; }
    }

    [Fact]
    public void Повторный_импорт_того_же_id_идемпотентен()
    {
        string cache = TestEnv.FreshCache();
        var (root, video, poster) = Seed();
        string prev = ModInit.conf.onlineDownloadsPath;
        ModInit.conf.onlineDownloadsPath = root;
        try
        {
            var a = QbitController.OnlineImportApply(Body(ID, video, poster));
            var body2 = Body(ID, video, poster);
            body2["title"] = "Переименовано";
            var b = QbitController.OnlineImportApply(body2);
            Assert.Equal(a.hash, b.hash);
            Assert.Single(Directory.GetFiles(Path.Combine(cache, "local")));
            var meta = JObject.Parse(File.ReadAllText(Path.Combine(cache, "meta", a.hash + ".json")));
            Assert.Equal("Переименовано", meta.Value<string>("title"));
        }
        finally { ModInit.conf.onlineDownloadsPath = prev; }
    }

    [Fact]
    public void Путь_вне_корня_и_плохой_id_отклоняются_без_записи()
    {
        string cache = TestEnv.FreshCache();
        var (root, video, _) = Seed();
        string prev = ModInit.conf.onlineDownloadsPath;
        ModInit.conf.onlineDownloadsPath = root;
        try
        {
            // файл существует, но лежит вне корня
            string outside = Path.Combine(NewRoot(), "video.mp4");
            File.WriteAllText(outside, "x");
            Assert.Equal(400, QbitController.OnlineImportApply(Body(ID, outside)).status);
            // traversal через корень
            Assert.Equal(400, QbitController.OnlineImportApply(Body(ID, Path.Combine(root, "..", Path.GetFileName(Path.GetDirectoryName(outside)), "video.mp4"))).status);
            // плохой id
            Assert.Equal(400, QbitController.OnlineImportApply(Body("../etc", video)).status);
            Assert.Equal(400, QbitController.OnlineImportApply(Body("2026-09-09", video)).status);
            // файла нет
            Assert.Equal(404, QbitController.OnlineImportApply(Body(ID, Path.Combine(root, ID, "nope.mp4"))).status);
            // пустое тело
            Assert.Equal(400, QbitController.OnlineImportApply(new JObject()).status);
            Assert.Equal(400, QbitController.OnlineImportApply(null).status);

            Assert.False(Directory.Exists(Path.Combine(cache, "local")) && Directory.GetFiles(Path.Combine(cache, "local")).Length > 0, "ни одного маркера");
        }
        finally { ModInit.conf.onlineDownloadsPath = prev; }
    }

    [Fact]
    public void Постер_вне_корня_игнорируется_а_импорт_проходит()
    {
        string cache = TestEnv.FreshCache();
        var (root, video, _) = Seed();
        string prev = ModInit.conf.onlineDownloadsPath;
        ModInit.conf.onlineDownloadsPath = root;
        try
        {
            string badPoster = Path.Combine(NewRoot(), "poster.jpg");
            File.WriteAllText(badPoster, "x");
            var (status, _, hash) = QbitController.OnlineImportApply(Body(ID, video, badPoster));
            Assert.Equal(200, status);
            Assert.False(File.Exists(Path.Combine(cache, "img", hash + ".jpg")));
        }
        finally { ModInit.conf.onlineDownloadsPath = prev; }
    }

    [Fact]
    public void Заголовок_по_умолчанию_и_имя_файла_без_запрещённых_символов()
    {
        TestEnv.FreshCache();
        var (root, video, _) = Seed();
        string prev = ModInit.conf.onlineDownloadsPath;
        ModInit.conf.onlineDownloadsPath = root;
        try
        {
            var body = Body(ID, video);
            body["title"] = "  Матч: «Спартак» / ЦСКА?  ";
            var (status, _, hash) = QbitController.OnlineImportApply(body);
            Assert.Equal(200, status);
            var marker = JObject.Parse(File.ReadAllText(Path.Combine(ModInit.conf.cachePath, "local", hash + ".json")));
            Assert.Equal("Матч «Спартак» ЦСКА.mp4", marker["files"][0].Value<string>("name"));

            body["title"] = "";
            body["id"] = "2026-09-09_21-00-00";
            var (s2, _, h2) = QbitController.OnlineImportApply(body);
            Assert.Equal(200, s2);
            var meta = JObject.Parse(File.ReadAllText(Path.Combine(ModInit.conf.cachePath, "meta", h2 + ".json")));
            Assert.Equal("Эфир 2026-09-09_21-00-00", meta.Value<string>("title"));
        }
        finally { ModInit.conf.onlineDownloadsPath = prev; }
    }

    // ── строка хелса ─────────────────────────────────────────────────────────

    static JObject Health(Action<JObject> tweak = null)
    {
        var b = new JObject
        {
            ["ok"] = true,
            ["version"] = "1.0.0",
            ["state"] = "idle",
            ["live"] = null,
            ["lastResult"] = null,
            ["recordings"] = 3,
            ["ytdlp"] = new JObject { ["version"] = "2026.08.19" },
            ["export"] = new JObject { ["running"] = false, ["error"] = null }
        };
        tweak?.Invoke(b);
        return b;
    }

    [Fact]
    public void OnlineVerdict_Idle_IsOkWithRecordings()
    {
        var (status, detail) = QbitController.OnlineVerdict(Health());
        Assert.Equal("ok", status);
        Assert.Contains("нет эфира", detail);
        Assert.Contains("записей 3", detail);
        Assert.Contains("v1.0.0", detail);
    }

    [Fact]
    public void OnlineVerdict_Live_NamesTitleAndViewers()
    {
        var (status, detail) = QbitController.OnlineVerdict(Health(b =>
        {
            b["state"] = "live";
            b["live"] = new JObject { ["title"] = "Apple Event", ["viewers"] = 4, ["restarts"] = 1 };
        }));
        Assert.Equal("ok", status);
        Assert.Contains("Apple Event", detail);
        Assert.Contains("зрителей 4", detail);
        Assert.Contains("перезапусков 1", detail);
    }

    [Fact]
    public void OnlineVerdict_ManyRestarts_IsWarn()
    {
        var (status, _) = QbitController.OnlineVerdict(Health(b =>
        {
            b["state"] = "live";
            b["live"] = new JObject { ["title"] = "x", ["viewers"] = 0, ["restarts"] = 3 };
        }));
        Assert.Equal("warn", status);
    }

    [Fact]
    public void OnlineVerdict_LastErrorOrExportError_IsWarn()
    {
        var (s1, d1) = QbitController.OnlineVerdict(Health(b => b["lastResult"] = new JObject { ["error"] = "склейка не удалась" }));
        Assert.Equal("warn", s1);
        Assert.Contains("склейка", d1);
        var (s2, d2) = QbitController.OnlineVerdict(Health(b => b["export"] = new JObject { ["error"] = "нет диска" }));
        Assert.Equal("warn", s2);
        Assert.Contains("экспорт", d2);
    }

    [Fact]
    public void OnlineVerdict_NoYtdlp_IsWarn_NullBody_IsFail()
    {
        var (s, d) = QbitController.OnlineVerdict(Health(b => b["ytdlp"] = new JObject { ["version"] = null }));
        Assert.Equal("warn", s);
        Assert.Contains("yt-dlp", d);
        Assert.Equal("fail", QbitController.OnlineVerdict(null).status);
        Assert.Equal("fail", QbitController.OnlineVerdict(Health(b => b["ok"] = false)).status);
    }

    /// <summary>🔴 Остановленный контейнер — штатное состояние: ⏸, не ❌.</summary>
    [Fact]
    public void OnlineRefused_RecognisesConnectionRefusedAndDns()
    {
        var refused = new System.Net.Http.HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        Assert.True(QbitController.OnlineRefused(refused));
        var dns = new System.Net.Http.HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
        Assert.True(QbitController.OnlineRefused(dns));
        var timeout = new TaskCanceledException("timeout");
        Assert.False(QbitController.OnlineRefused(timeout));
    }
}
