using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Локальный маркер транскода (cachePath/local/&lt;hash&gt;.json): нормализация LocalFiles
/// обязана читать И старый плоский формат {name,path,size,added} (существующие фильмы),
/// И новый мульти-файловый {files:[...]} (сериалы), плюс флаг overlay.
/// </summary>
public class LocalMarkerTests
{
    [Fact]
    public void Old_flat_marker_reads_as_single_file()
    {
        var loc = JObject.Parse("{\"name\":\"Movie.mp4\",\"path\":\"/downloads/transcoded/Movie.mp4\",\"size\":123456,\"added\":1760000000}");
        var files = Access.LocalFilesOf(loc);
        Assert.Single(files);
        Assert.Equal(0, files[0].index);
        Assert.Equal("Movie.mp4", files[0].name);
        Assert.Equal("/downloads/transcoded/Movie.mp4", files[0].path);
        Assert.Equal(123456, files[0].size);
        Assert.False(Access.LocalIsOverlay(loc));
    }

    [Fact]
    public void New_multi_marker_reads_all_files_in_order()
    {
        var loc = JObject.Parse(@"{
            ""name"":""Series"", ""dir"":""/downloads/transcoded/Series.abcd1234"", ""size"":300, ""overlay"":false,
            ""files"":[
                {""index"":0,""name"":""Ep01.mp4"",""path"":""/downloads/transcoded/Series.abcd1234/Ep01.mp4"",""size"":100},
                {""index"":1,""name"":""Ep02.mp4"",""path"":""/downloads/transcoded/Series.abcd1234/Ep02.mp4"",""size"":200}
            ]}");
        var files = Access.LocalFilesOf(loc);
        Assert.Equal(2, files.Count);
        Assert.Equal((0, "Ep01.mp4"), (files[0].index, files[0].name));
        Assert.Equal((1, "Ep02.mp4"), (files[1].index, files[1].name));
        Assert.False(Access.LocalIsOverlay(loc));
    }

    [Fact]
    public void Overlay_flag_is_detected()
    {
        var loc = JObject.Parse("{\"overlay\":true,\"files\":[{\"index\":0,\"name\":\"a.mp4\",\"path\":\"/x/a.mp4\",\"size\":1}]}");
        Assert.True(Access.LocalIsOverlay(loc));
        Assert.Null(Access.Call("LoadLocal", "0000000000000000000000000000000000000000") as object);   // несуществующий hash → null
        Assert.False(Access.LocalIsOverlay(null));
    }

    [Fact]
    public void Null_and_empty_markers_are_safe()
    {
        Assert.Empty(Access.LocalFilesOf(null));
        Assert.Empty(Access.LocalFilesOf(new JObject()));
        Assert.Empty(Access.LocalFilesOf(JObject.Parse("{\"files\":[]}")));
        // файл без path — пропускается
        Assert.Empty(Access.LocalFilesOf(JObject.Parse("{\"files\":[{\"name\":\"x.mp4\",\"size\":1}]}")));
    }

    // ── SafeFileBase: стабильный ключ соответствия «торрент-файл ↔ mp4-копия» ──

    [Theory]
    [InlineData("Season 1/Ep01.mkv", "Ep01")]
    [InlineData("Ep01.mkv", "Ep01")]
    [InlineData("Ep01.mp4", "Ep01")]                    // после транскода — тот же ключ
    [InlineData("Show - 01 [1080p].avi", "Show - 01 [1080p]")]
    [InlineData("weird.name.S01E02.mkv", "weird.name.S01E02")]
    public void SafeFileBase_strips_dirs_and_video_ext(string input, string expected)
        => Assert.Equal(expected, Access.SafeFileBase(input));

    [Fact]
    public void SafeFileBase_is_stable_across_transcode()
    {
        // ключевое свойство: имя серии в торренте (mkv) и имя её mp4-копии дают ОДИН ключ
        string torrent = "Dune.Prophecy.S01E01.The.Hidden.Hand.1080p.mkv";
        string local = Access.SafeFileBase(torrent) + ".mp4";
        Assert.Equal(Access.SafeFileBase(torrent), Access.SafeFileBase(local));
    }
}
