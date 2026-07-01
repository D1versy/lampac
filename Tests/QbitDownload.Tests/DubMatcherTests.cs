using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Characterization tests for the dub-matcher helpers of <c>QbitController</c>:
/// <c>BaseNoExt</c>, <c>NaturalCompare</c>, <c>FindVideo</c> and <c>DubsForVideo</c>.
/// All assertions capture the ACTUAL current behavior of the runtime-compiled module.
/// </summary>
public class DubMatcherTests
{
    // ── helpers ───────────────────────────────────────────────────────────
    static JObject File(string name, int index, long size = 0, double progress = 1.0)
        => new JObject { ["name"] = name, ["index"] = index, ["size"] = size, ["progress"] = progress };

    static int Sign(int v) => v < 0 ? -1 : v > 0 ? 1 : 0;

    // ════════════════════════════════════════════════════════════════════
    //  BaseNoExt — strip path + extension from f["name"]
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Show S01E05.mkv", "Show S01E05")]
    [InlineData("Show.mka", "Show")]
    [InlineData("folder/sub/Show S01E05.mkv", "Show S01E05")]      // forward slash path
    [InlineData(@"folder\sub\Show S01E05.mkv", "Show S01E05")]      // backslash path normalised
    [InlineData(@"C:\downloads\Movie [Studio].mka", "Movie [Studio]")]
    [InlineData("NoExtension", "NoExtension")]                      // no dot at all
    [InlineData("Archive.tar.gz", "Archive.tar")]                  // only last ext stripped
    [InlineData("dir/", "")]                                       // trailing slash → segment after '/' is empty
    [InlineData(".hidden", "")]                                     // leading-dot file → GetFileNameWithoutExtension = ""
    public void BaseNoExt_strips_path_and_extension(string name, string expected)
    {
        var f = new JObject { ["name"] = name };
        Assert.Equal(expected, Access.BaseNoExt(f));
    }

    [Fact]
    public void BaseNoExt_missing_name_yields_empty()
    {
        var f = new JObject { ["index"] = 3 };   // no "name"
        Assert.Equal("", Access.BaseNoExt(f));
    }

    [Fact]
    public void BaseNoExt_null_name_yields_empty()
    {
        var f = new JObject { ["name"] = JValue.CreateNull() };
        Assert.Equal("", Access.BaseNoExt(f));
    }

    // ════════════════════════════════════════════════════════════════════
    //  NaturalCompare — numeric-aware, leading-zero-insensitive, case-insensitive
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    // numeric-aware ordering: ep2 < ep10 (2 < 10 despite lexicographic '1' < '2')
    [InlineData("ep2", "ep10", -1)]
    [InlineData("ep10", "ep2", 1)]
    [InlineData("ep2", "ep2", 0)]
    // leading zeros equal
    [InlineData("ep05", "ep5", 0)]
    [InlineData("ep005", "ep5", 0)]
    [InlineData("05", "5", 0)]
    // multi-digit magnitude via trimmed-zero length compare
    [InlineData("100", "99", 1)]
    [InlineData("099", "100", -1)]
    // case-insensitive letters
    [InlineData("Show", "show", 0)]
    [InlineData("ABC", "abc", 0)]
    // pure lexical, differing letters
    [InlineData("apple", "banana", -1)]
    [InlineData("banana", "apple", 1)]
    // equal strings
    [InlineData("", "", 0)]
    [InlineData("same", "same", 0)]
    // prefix: shorter is smaller when the rest is longer
    [InlineData("ep", "ep1", -1)]
    [InlineData("ep1", "ep", 1)]
    // mixed: digit block vs letter block at same position → compares chars
    [InlineData("1", "a", -1)]       // '1'(0x31) < 'a'(0x61)
    [InlineData("a", "1", 1)]
    // two numeric runs, first decides
    [InlineData("s1e2", "s1e10", -1)]
    [InlineData("s2e1", "s10e1", -1)]
    public void NaturalCompare_returns_expected_sign(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, Sign(Access.NaturalCompare(a, b)));

    [Fact]
    public void NaturalCompare_nulls_treated_as_empty()
    {
        Assert.Equal(0, Sign(Access.NaturalCompare(null, null)));
        Assert.Equal(0, Sign(Access.NaturalCompare(null, "")));
        Assert.True(Access.NaturalCompare("x", null) > 0);
        Assert.True(Access.NaturalCompare(null, "x") < 0);
    }

    [Fact]
    public void NaturalCompare_is_antisymmetric_in_sign()
    {
        Assert.Equal(-Sign(Access.NaturalCompare("ep10", "ep2")),
                      Sign(Access.NaturalCompare("ep2", "ep10")));
    }

    // ════════════════════════════════════════════════════════════════════
    //  FindVideo — by index (>=0) else largest video by size; null if none
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void FindVideo_by_index_returns_matching_file()
    {
        var files = new JArray
        {
            File("a.mkv", 0, 100),
            File("b.mkv", 1, 200),
            File("c.mka", 2, 300),
        };
        var v = Access.FindVideo(files, 1);
        Assert.NotNull(v);
        Assert.Equal("b.mkv", v.Value<string>("name"));
    }

    [Fact]
    public void FindVideo_by_index_matches_even_non_video()
    {
        // index lookup does NOT check the extension — it returns whatever has that index
        var files = new JArray { File("a.mkv", 0, 100), File("audio.mka", 5, 999) };
        var v = Access.FindVideo(files, 5);
        Assert.NotNull(v);
        Assert.Equal("audio.mka", v.Value<string>("name"));
    }

    [Fact]
    public void FindVideo_index_not_found_falls_through_to_largest()
    {
        // index >= 0 but absent: the `if (index>=0)` only guards the FIRST loop; there is no early
        // return afterwards, so execution falls through to the size-scan and returns the largest video.
        var files = new JArray { File("a.mkv", 0, 100), File("b.mkv", 1, 200) };
        var v = Access.FindVideo(files, 7);
        Assert.NotNull(v);
        Assert.Equal("b.mkv", v.Value<string>("name"));   // largest of the two videos
    }

    [Fact]
    public void FindVideo_negative_index_picks_largest_video()
    {
        var files = new JArray
        {
            File("small.mkv", 0, 100),
            File("big.mp4", 1, 5000),
            File("mid.avi", 2, 1000),
            File("huge.mka", 3, 999999),   // audio, ignored despite being biggest
        };
        var v = Access.FindVideo(files, -1);
        Assert.NotNull(v);
        Assert.Equal("big.mp4", v.Value<string>("name"));
    }

    [Fact]
    public void FindVideo_negative_index_ignores_non_video_extensions()
    {
        var files = new JArray { File("a.txt", 0, 900), File("b.nfo", 1, 800) };
        Assert.Null(Access.FindVideo(files, -1));
    }

    [Fact]
    public void FindVideo_empty_list_is_null()
        => Assert.Null(Access.FindVideo(new JArray(), -1));

    [Theory]
    [InlineData("clip.mkv")]
    [InlineData("clip.mp4")]
    [InlineData("clip.avi")]
    [InlineData("clip.ts")]
    [InlineData("clip.m2ts")]
    [InlineData("clip.webm")]
    [InlineData("clip.mov")]
    [InlineData("clip.m4v")]
    public void FindVideo_recognises_every_video_extension(string name)
    {
        var files = new JArray { File(name, 0, 10) };
        var v = Access.FindVideo(files, -1);
        Assert.NotNull(v);
        Assert.Equal(name, v.Value<string>("name"));
    }

    [Fact]
    public void FindVideo_negative_index_first_of_equal_max_wins()
    {
        // strict > means the FIRST file reaching the max size is kept
        var files = new JArray { File("first.mkv", 0, 500), File("second.mp4", 1, 500) };
        var v = Access.FindVideo(files, -1);
        Assert.Equal("first.mkv", v.Value<string>("name"));
    }

    // ════════════════════════════════════════════════════════════════════
    //  DubsForVideo — matcher ranks A / B / D / E / F
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void DubsForVideo_null_video_returns_empty()
        => Assert.Empty(Access.DubsForVideo(new JArray { File("x.mkv", 0, 1) }, null));

    [Fact]
    public void DubsForVideo_no_audio_returns_empty()
    {
        var files = new JArray { File("Show S01E05.mkv", 0, 100), File("Show S01E06.mkv", 1, 100) };
        var video = files[0];
        Assert.Empty(Access.DubsForVideo(files, video));
    }

    // ── Rank A: exact episode match ──────────────────────────────────────
    [Fact]
    public void DubsForVideo_rankA_exact_episode()
    {
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File("Show S01E05 [Studio].mka", 2, 50),
            File("Show S01E06 [Studio].mka", 3, 50),
        };
        var video = files[0]; // S01E05
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(Access.StudioId("Studio"), d.id);
        Assert.Equal("Studio", d.label);
        Assert.Equal(2, d.idx);   // the S01E05 audio, index 2
    }

    [Fact]
    public void DubsForVideo_rankA_picks_correct_episode_for_second_video()
    {
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File("Show S01E05 [Studio].mka", 2, 50),
            File("Show S01E06 [Studio].mka", 3, 50),
        };
        var video = files[1]; // S01E06
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(Access.StudioId("Studio"), d.id);
        Assert.Equal(3, d.idx);   // the S01E06 audio
    }

    [Fact]
    public void DubsForVideo_rankA_two_studios_two_dubs()
    {
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File("Show S01E05 [Alpha].mka", 2, 50),
            File("Show S01E06 [Alpha].mka", 3, 50),
            File("Show S01E05 [Beta].mka", 4, 60),
            File("Show S01E06 [Beta].mka", 5, 60),
        };
        var video = files[0]; // S01E05
        var dubs = Access.DubsForVideo(files, video);

        Assert.Equal(2, dubs.Count);
        var alpha = dubs.Single(x => x.id == Access.StudioId("Alpha"));
        var beta = dubs.Single(x => x.id == Access.StudioId("Beta"));
        Assert.Equal("Alpha", alpha.label);
        Assert.Equal(2, alpha.idx);   // Alpha S01E05
        Assert.Equal("Beta", beta.label);
        Assert.Equal(4, beta.idx);    // Beta S01E05
    }

    // ── Rank B: name-prefix (audio base starts with video base, no exact ep) ─
    [Fact]
    public void DubsForVideo_rankB_name_prefix()
    {
        // Audio base "Movie Title RusDub" starts with video base "Movie Title";
        // the video has no parsed episode, and it's not a single-video movie set,
        // so it falls to the prefix rule (rank 5).
        var files = new JArray
        {
            File("Movie Title.mkv", 0, 1000),
            File("Other Title.mkv", 1, 1000),          // keeps videos.Count > 1 (not movie)
            File("Movie Title RusDub.mka", 2, 40),
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        // StudioOf: fbase "Movie Title RusDub" starts with vbase "Movie Title" → suffix "RusDub"
        Assert.Equal("RusDub", d.label);
        Assert.Equal(Access.StudioId("RusDub"), d.id);
        Assert.Equal(2, d.idx);
    }

    // ── Rank D: movie (single video, video has no episode → any track matches) ─
    [Fact]
    public void DubsForVideo_rankD_movie_any_track()
    {
        // Exactly one video, video base has no episode → isMovie && !vEp.any → rank 3.
        // Audio name shares no prefix and has no episode, yet still matches.
        var files = new JArray
        {
            File("SomeMovie.mkv", 0, 5000),
            File("Studio Audio Track.mka", 1, 80),
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(1, d.idx);
        // label is whatever StudioOf derived from the audio name
        Assert.Equal(Access.StudioOf("Studio Audio Track.mka", "SomeMovie"), d.label);
        Assert.Equal(Access.StudioId(d.label), d.id);
    }

    [Fact]
    public void DubsForVideo_rankD_movie_multiple_studios_each_matches()
    {
        var files = new JArray
        {
            File("SomeMovie.mkv", 0, 5000),
            File("SomeMovie [Alpha].mka", 1, 80),
            File("SomeMovie [Beta].mka", 2, 90),
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        Assert.Equal(2, dubs.Count);
        Assert.Contains(dubs, x => x.id == Access.StudioId("Alpha") && x.idx == 1);
        Assert.Contains(dubs, x => x.id == Access.StudioId("Beta") && x.idx == 2);
    }

    // ── Rank E: season-pack (studio has exactly one audio file with no episode) ─
    [Fact]
    public void DubsForVideo_rankE_season_pack_single_studio_file()
    {
        // Video HAS an episode (S01E05), so rank A/D are out for this audio.
        // The audio has no parseable episode and is the sole file for its studio → rank 2.
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),          // >1 video → not a movie
            File("Alpha Full Season Dub.mka", 2, 300),
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(2, d.idx);
        Assert.Equal(Access.StudioOf("Alpha Full Season Dub.mka", "Show S01E05"), d.label);
        Assert.Equal(Access.StudioId(d.label), d.id);
    }

    [Fact]
    public void DubsForVideo_rankE_not_applied_when_studio_has_two_files()
    {
        // ONE studio (shared non-generic parent folder "Alpha") owns TWO episode-less audios that don't
        // prefix the video base. lst.Count == 2 disqualifies rank E (which needs a single file), so this
        // must NOT yield two separate rank-E dubs; with 2 videos the positional fallback (F) gives exactly one.
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File("dubs/Alpha/x.mka", 2, 300),
            File("dubs/Alpha/y.mka", 3, 300),
        };
        // grouping is unambiguous and NOT recomputed to pick a branch: the parent folder "Alpha" is the
        // studio for BOTH files, so they form one group of two.
        Assert.Equal("Alpha", Access.StudioOf("dubs/Alpha/x.mka", "Show S01E05"));
        Assert.Equal("Alpha", Access.StudioOf("dubs/Alpha/y.mka", "Show S01E05"));

        var dubs = Access.DubsForVideo(files, files[0]);   // S01E05 → vPos 0
        var d = Assert.Single(dubs);                        // ONE dub (rank F), not two rank-E dubs
        Assert.Equal(Access.StudioId("Alpha"), d.id);
        Assert.Equal(2, d.idx);                             // natural-sorted lst[0] = x.mka (index 2)
    }

    // ── Rank F: positional (equal counts, no episodes, best still null) ──────
    [Fact]
    public void DubsForVideo_rankF_positional_equal_counts()
    {
        // One studio (non-generic parent folder "StudioX") owns TWO audios whose base names carry NO
        // episode markers and do NOT prefix the video base → ranks A/B/D/E all miss → positional
        // fallback pairs audio↔video by natural-sorted position. NB: a trailing digit (e.g. "Zeta 1")
        // WOULD parse as an episode and defeat F's noeps guard, so bases here are purely alphabetic.
        var files = new JArray
        {
            File("AAA.mkv", 0, 1000),
            File("BBB.mkv", 1, 1000),
            File("dub/StudioX/alpha.mka", 2, 40),
            File("dub/StudioX/beta.mka", 3, 40),
        };
        // sanity: both audios resolve to the SAME studio → lst.Count(2) == videos.Count(2)
        Assert.Equal(Access.StudioId(Access.StudioOf("dub/StudioX/alpha.mka", "AAA")),
                     Access.StudioId(Access.StudioOf("dub/StudioX/beta.mka", "AAA")));
        // sanity: neither base parses as an episode (F requires noeps)
        Assert.False(Access.ParseEp("alpha").any);
        Assert.False(Access.ParseEp("beta").any);

        // videoA "AAA" → vPos 0 → natural-sorted lst[0] = alpha (index 2)
        var dubA = Assert.Single(Access.DubsForVideo(files, files[0]));
        Assert.Equal(2, dubA.idx);

        // videoB "BBB" → vPos 1 → lst[1] = beta (index 3)
        var dubB = Assert.Single(Access.DubsForVideo(files, files[1]));
        Assert.Equal(3, dubB.idx);
    }

    [Fact]
    public void DubsForVideo_no_match_when_counts_unequal_and_no_prefix()
    {
        // One studio owns TWO non-episode, non-prefixing audios (defeats rank E: lst.Count == 2), but
        // there are THREE videos, so the positional fallback (needs lst.Count == videos.Count) also fails.
        var files = new JArray
        {
            File("AAA.mkv", 0, 1000),
            File("BBB.mkv", 1, 1000),
            File("CCC.mkv", 2, 1000),
            File("dubs/Alpha/x.mka", 3, 40),
            File("dubs/Alpha/y.mka", 4, 40),
        };
        // unambiguous grouping (not recomputed to pick a branch): both audios belong to the SAME studio
        // "Alpha" — a group of 2, which is != 3 videos.
        Assert.Equal("Alpha", Access.StudioOf("dubs/Alpha/x.mka", "AAA"));
        Assert.Equal("Alpha", Access.StudioOf("dubs/Alpha/y.mka", "AAA"));

        // rank E defeated (group of 2), positional F defeated (2 != 3 videos), no prefix/episode → nothing
        Assert.Empty(Access.DubsForVideo(files, files[0]));
    }

    [Fact]
    public void DubsForVideo_rankA_beats_rankB_when_both_possible()
    {
        // "Show S01E05 extra" both prefixes the video base AND exact-episode-matches.
        // Rank A (6) must win over B (5): the idx should be the exact-episode audio.
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File("Show S01E05 [Studio].mka", 2, 50),   // exact ep + prefix
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(2, d.idx);
    }

    [Fact]
    public void DubsForVideo_index_reported_is_minus_one_when_missing()
    {
        // Movie match but the audio file has no "index" field → reported idx == -1.
        var files = new JArray
        {
            File("SomeMovie.mkv", 0, 5000),
            new JObject { ["name"] = "Studio Track.mka", ["size"] = 80 },  // no index
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(-1, d.idx);
    }

    [Fact]
    public void DubsForVideo_ignores_non_audio_non_video_files()
    {
        var files = new JArray
        {
            File("SomeMovie.mkv", 0, 5000),
            File("cover.jpg", 1, 10),
            File("readme.txt", 2, 5),
            File("SomeMovie [Studio].mka", 3, 80),
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(3, d.idx);
        Assert.Equal(Access.StudioId("Studio"), d.id);
    }

    [Theory]
    [InlineData("Show S01E05 [Studio].mka")]
    [InlineData("Show S01E05 [Studio].aac")]
    [InlineData("Show S01E05 [Studio].ac3")]
    [InlineData("Show S01E05 [Studio].eac3")]
    [InlineData("Show S01E05 [Studio].dts")]
    [InlineData("Show S01E05 [Studio].flac")]
    [InlineData("Show S01E05 [Studio].opus")]
    [InlineData("Show S01E05 [Studio].m4a")]
    [InlineData("Show S01E05 [Studio].wav")]
    [InlineData("Show S01E05 [Studio].mp3")]
    [InlineData("Show S01E05 [Studio].thd")]
    public void DubsForVideo_recognises_every_audio_extension(string audioName)
    {
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File(audioName, 2, 50),
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        var d = Assert.Single(dubs);
        Assert.Equal(2, d.idx);
        Assert.Equal(Access.StudioId("Studio"), d.id);
    }

    [Fact]
    public void DubsForVideo_studio_id_matches_StudioId_of_label()
    {
        // Invariant: for every returned dub, id == StudioId(label).
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File("Show S01E05 [Alpha].mka", 2, 50),
            File("Show S01E05 [Beta].mka", 3, 60),
        };
        var video = files[0];
        var dubs = Access.DubsForVideo(files, video);

        Assert.NotEmpty(dubs);
        foreach (var d in dubs)
            Assert.Equal(Access.StudioId(d.label), d.id);
    }

    [Fact]
    public void DubsForVideo_no_exact_match_leaves_studio_unmatched()
    {
        // Video S01E05 with an episode; the only audio for the studio is a DIFFERENT
        // episode (S01E06), has a prefix? "Show S01E06 [Studio]" does NOT start with
        // "Show S01E05". Not a movie. lst.Count == 1 with an episode (aEp.any) →
        // rank E requires !aEp.any, so E fails too. Result: no match for this video.
        var files = new JArray
        {
            File("Show S01E05.mkv", 0, 1000),
            File("Show S01E06.mkv", 1, 1000),
            File("Show S01E06 [Studio].mka", 2, 50),
        };
        var video = files[0]; // S01E05
        var dubs = Access.DubsForVideo(files, video);
        Assert.Empty(dubs);
    }
}
