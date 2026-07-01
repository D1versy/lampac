using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for the qBittorrent-facing HTTP helpers of <c>QbitController</c>:
/// <c>QbitAddMagnet</c>, <c>ResolveFile</c>, and <c>ResolveDubFile</c>. All I/O is faked via
/// <see cref="FakeQbit"/> (no network) and real files are written to a temp dir so File.Exists is true.
/// </summary>
public sealed class QbitClientTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // QbitAddMagnet
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QbitAddMagnet_Ok_Text_ReturnsTrue()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        var c = new FakeQbit().Text("/torrents/add", "Ok.").Build();
        Assert.True(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_EmptyBody_ReturnsTrue()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        var c = new FakeQbit().Text("/torrents/add", "").Build();
        Assert.True(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_Conflict409_ReturnsTrue()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        // 409 status with the classic "Conflict" body — treated as already-added.
        var c = new FakeQbit().Text("/torrents/add", "Conflict", HttpStatusCode.Conflict).Build();
        Assert.True(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_ConflictBody_With200_ReturnsTrue()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        // Body says "Conflict" even at 200 → true (case-insensitive body match).
        var c = new FakeQbit().Text("/torrents/add", "conflict", HttpStatusCode.OK).Build();
        Assert.True(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_JsonSuccessCount1_ReturnsTrue()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        var c = new FakeQbit().Json("/torrents/add", "{\"success_count\":1}").Build();
        Assert.True(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_JsonPendingCount1_ReturnsTrue()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        var c = new FakeQbit().Json("/torrents/add", "{\"pending_count\":1}").Build();
        Assert.True(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_JsonSuccessCount0_ReturnsFalse()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        var c = new FakeQbit().Json("/torrents/add", "{\"success_count\":0}").Build();
        Assert.False(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_JsonEmptyObject_ReturnsFalse()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        // {} → neither success_count nor pending_count > 0 → false.
        var c = new FakeQbit().Json("/torrents/add", "{}").Build();
        Assert.False(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_MalformedJson_ReturnsFalse()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        // Starts with '{' but is not valid JSON → JObject.Parse throws → caught → false.
        var c = new FakeQbit().Text("/torrents/add", "{not json").Build();
        Assert.False(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public async Task QbitAddMagnet_UnknownTextBody_ReturnsFalse()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        // 200 but not "Ok.", not empty, not JSON → falls through to false.
        var c = new FakeQbit().Text("/torrents/add", "Fails.").Build();
        Assert.False(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task QbitAddMagnet_NonSuccessNon409_ReturnsFalse(HttpStatusCode code)
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = @"C:\downloads";
        ModInit.conf.category = "lampac";
        // Non-2xx, non-409, body not "Conflict" → false.
        var c = new FakeQbit().Text("/torrents/add", "Ok.", code).Build();
        Assert.False(await Access.QbitAddMagnet(c, "magnet:?xt=urn:btih:abc"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveFile
    // ─────────────────────────────────────────────────────────────────────────

    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qdl-resolve", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string InfoJson(string savePath, string contentPath)
        => "[{\"save_path\":" + Newtonsoft.Json.JsonConvert.ToString(savePath)
            + ",\"content_path\":" + Newtonsoft.Json.JsonConvert.ToString(contentPath) + "}]";

    [Fact]
    public async Task ResolveFile_ByIndex_ReturnsConfinedPath()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        File.WriteAllText(Path.Combine(dir, "a.mkv"), "a");
        File.WriteAllText(Path.Combine(dir, "b.mkv"), "bb");

        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files",
                "[{\"name\":\"a.mkv\",\"index\":0,\"size\":100},{\"name\":\"b.mkv\",\"index\":1,\"size\":50}]")
            .Build();

        string got = await Access.ResolveFile(c, "HASH", 0);
        Assert.Equal(Path.Combine(dir, "a.mkv"), got);
    }

    [Fact]
    public async Task ResolveFile_IndexNegative_ReturnsLargestFile()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        File.WriteAllText(Path.Combine(dir, "small.mkv"), "s");
        File.WriteAllText(Path.Combine(dir, "big.mkv"), "b");

        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files",
                "[{\"name\":\"small.mkv\",\"index\":0,\"size\":10},{\"name\":\"big.mkv\",\"index\":1,\"size\":9999}]")
            .Build();

        string got = await Access.ResolveFile(c, "HASH", -1);
        Assert.Equal(Path.Combine(dir, "big.mkv"), got);
    }

    [Fact]
    public async Task ResolveFile_IndexNotFound_FallsBackToLargest()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        File.WriteAllText(Path.Combine(dir, "small.mkv"), "s");
        File.WriteAllText(Path.Combine(dir, "big.mkv"), "b");

        // Requested index 99 doesn't exist → file==null → picks largest.
        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files",
                "[{\"name\":\"small.mkv\",\"index\":0,\"size\":10},{\"name\":\"big.mkv\",\"index\":1,\"size\":9999}]")
            .Build();

        string got = await Access.ResolveFile(c, "HASH", 99);
        Assert.Equal(Path.Combine(dir, "big.mkv"), got);
    }

    [Fact]
    public async Task ResolveFile_SingleFile_UsesContentPathBranch()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        // content_path points at a real on-disk file with a DIFFERENT name than "name",
        // proving the single-file content_path branch is taken (not ConfinedCombine(savePath, name)).
        string contentPath = Path.Combine(dir, "actual-content.mkv");
        File.WriteAllText(contentPath, "real");

        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, contentPath))
            .Json("/torrents/files", "[{\"name\":\"logical-name.mkv\",\"index\":0,\"size\":100}]")
            .Build();

        string got = await Access.ResolveFile(c, "HASH", 0);
        Assert.Equal(contentPath, got);
    }

    [Fact]
    public async Task ResolveFile_SingleFile_ContentPathMissing_FallsBackToConfined()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        // content_path doesn't exist on disk → branch skipped → ConfinedCombine(savePath, name),
        // which does exist.
        string realFile = Path.Combine(dir, "a.mkv");
        File.WriteAllText(realFile, "x");
        string ghost = Path.Combine(dir, "does-not-exist.mkv");

        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ghost))
            .Json("/torrents/files", "[{\"name\":\"a.mkv\",\"index\":0,\"size\":100}]")
            .Build();

        string got = await Access.ResolveFile(c, "HASH", 0);
        Assert.Equal(realFile, got);
    }

    [Fact]
    public async Task ResolveFile_FileMissingOnDisk_ReturnsNull()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        // No file written to disk → File.Exists false everywhere → null.
        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files", "[{\"name\":\"ghost.mkv\",\"index\":0,\"size\":100}]")
            .Build();

        Assert.Null(await Access.ResolveFile(c, "HASH", 0));
    }

    [Fact]
    public async Task ResolveFile_EmptyInfo_ReturnsNull()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = NewTempDir();
        var c = new FakeQbit()
            .Json("/torrents/info", "[]")
            .Json("/torrents/files", "[{\"name\":\"a.mkv\",\"index\":0,\"size\":100}]")
            .Build();

        Assert.Null(await Access.ResolveFile(c, "HASH", 0));
    }

    [Fact]
    public async Task ResolveFile_EmptyFiles_ReturnsNull()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;
        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files", "[]")
            .Build();

        Assert.Null(await Access.ResolveFile(c, "HASH", 0));
    }

    [Fact]
    public async Task ResolveFile_NullSavePath_FallsBackToDownloadsPath()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        File.WriteAllText(Path.Combine(dir, "a.mkv"), "x");

        // save_path is null in the info payload → savePath = downloadsPath.
        var c = new FakeQbit()
            .Json("/torrents/info", "[{\"content_path\":\"\"}]")
            .Json("/torrents/files", "[{\"name\":\"a.mkv\",\"index\":0,\"size\":100}]")
            .Build();

        string got = await Access.ResolveFile(c, "HASH", 0);
        Assert.Equal(Path.Combine(dir, "a.mkv"), got);
    }

    [Fact]
    public async Task ResolveFile_PathTraversalName_Rejected_ReturnsNull()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        // Plant a REAL file at the location a naive Path.Combine(dir, name) would escape to. ConfinedCombine
        // strips the "..", so the confined candidate is <dir>/evil.mkv (absent). If confinement regressed and
        // the traversal were followed, this planted file WOULD be found — so a null result proves the escape
        // was rejected, not merely that "the file was absent".
        string escaped = Path.GetFullPath(Path.Combine(dir, "..", "..", "evil.mkv"));
        File.WriteAllText(escaped, "x");
        try
        {
            var c = new FakeQbit()
                .Json("/torrents/info", InfoJson(dir, ""))
                .Json("/torrents/files",
                    "[{\"name\":\"..\\\\..\\\\evil.mkv\",\"index\":0,\"size\":100}]")
                .Build();

            Assert.Null(await Access.ResolveFile(c, "HASH", 0));   // traversal NOT followed despite planted file
        }
        finally { try { File.Delete(escaped); } catch { } }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveDubFile
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveDubFile_MatchingStudio_ResolvesAudioPath()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        // One video + one external audio track whose name = video base + studio suffix.
        // vbase="movie", audio base="movie.LostFilm" → StudioOf → "LostFilm".
        File.WriteAllText(Path.Combine(dir, "movie.mkv"), "video");
        string audioFile = Path.Combine(dir, "movie.LostFilm.mka");
        File.WriteAllText(audioFile, "audio");

        string filesJson =
            "[{\"name\":\"movie.mkv\",\"index\":0,\"size\":9999}," +
            "{\"name\":\"movie.LostFilm.mka\",\"index\":1,\"size\":100}]";

        // ResolveDubFile calls /files (to find dubs) then ResolveFile calls /info + /files again.
        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files", filesJson)
            .Build();

        string dubId = Access.StudioId("LostFilm");
        string got = await Access.ResolveDubFile(c, "HASH", 0, dubId);
        Assert.Equal(audioFile, got);
    }

    [Fact]
    public async Task ResolveDubFile_UnknownDubId_ReturnsNull()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        File.WriteAllText(Path.Combine(dir, "movie.mkv"), "video");
        File.WriteAllText(Path.Combine(dir, "movie.LostFilm.mka"), "audio");

        string filesJson =
            "[{\"name\":\"movie.mkv\",\"index\":0,\"size\":9999}," +
            "{\"name\":\"movie.LostFilm.mka\",\"index\":1,\"size\":100}]";

        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files", filesJson)
            .Build();

        // A dubId that no studio in the file list maps to → null.
        string got = await Access.ResolveDubFile(c, "HASH", 0, "dffffffff");
        Assert.Null(got);
    }

    [Fact]
    public async Task ResolveDubFile_NoVideoForIndex_ReturnsNull()
    {
        TestEnv.EnsureConf();
        string dir = NewTempDir();
        ModInit.conf.downloadsPath = dir;

        // Only audio in the list — FindVideo returns null → early null.
        var c = new FakeQbit()
            .Json("/torrents/info", InfoJson(dir, ""))
            .Json("/torrents/files", "[{\"name\":\"movie.LostFilm.mka\",\"index\":1,\"size\":100}]")
            .Build();

        string dubId = Access.StudioId("LostFilm");
        string got = await Access.ResolveDubFile(c, "HASH", 5, dubId);
        Assert.Null(got);
    }
}
