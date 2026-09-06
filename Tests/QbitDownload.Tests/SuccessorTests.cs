using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Преемник раздачи (Successor.cs, qdl 2.115): перекачка без пропажи уже скачанных серий.
///
/// Боевой повод — «Фонари» 06.09.2026: перевыложенный топик с теми же именами файлов, но другими
/// байтами качался поверх старых файлов, а старый торрент был снят сразу; «Укрытие» в тот же час —
/// та же папка, те же байты, окно перепроверки 44 ГБ с запертыми сериями. Здесь закреплено:
/// новая раздача добавляется в СВОЮ подпапку, режим выбирается по спискам файлов, старая живёт
/// до подтверждённого покрытия, файлы старой удаляются только в aside и только по safe-пути.
/// </summary>
[Collection("qbit-fake")]
public class SuccessorTests
{
    const string OldHash = "1e83a973528e0badea0543e1bcd68659b6b956ef";
    const string NewHash = "0a458f5a90f571ca98320f03301c9235affd10cf";
    const string Third = "cccccccccccccccccccccccccccccccccccccccc";
    const string Magnet = "magnet:?xt=urn:btih:" + NewHash + "&dn=Lanterns";
    const string Link = "http://127.0.0.1:9118/kinozal/parsemagnet?id=2150599&apikey=1";

    public SuccessorTests()
    {
        TestEnv.FreshCache();
        ModInit.conf.category = "lampa";
        ModInit.conf.donorCategory = "";
        ModInit.conf.downloadsPath = "/downloads";
        ModInit.conf.successorEnabled = true;
        ModInit.conf.successorMetaWaitSec = 0;      // одна проба метаданных, без ожидания
        ModInit.conf.successorMaxDays = 7;
        ModInit.conf.successorDeleteOldFiles = true;
        ModInit.conf.successorPlayedGraceMinutes = 30;
    }

    static JToken F(int index, string name, double progress, long size)
        => new JObject { ["index"] = index, ["name"] = name, ["progress"] = progress, ["size"] = size };

    static JArray OldFiles(double p3 = 1) => new JArray(
        F(0, "Lanterns.S01/Lanterns.S01E01.mkv", 1, 10_254_031_737),
        F(1, "Lanterns.S01/Lanterns.S01E02.mkv", 1, 9_218_196_941),
        F(2, "Lanterns.S01/Lanterns.S01E03.mkv", p3, 9_214_986_706));

    static JArray NewFilesSameNamesOtherSizes(double p = 0) => new JArray(
        F(0, "Lanterns.S01/Lanterns.S01E01.mkv", p, 10_388_857_953),
        F(1, "Lanterns.S01/Lanterns.S01E02.mkv", p, 9_465_398_930),
        F(2, "Lanterns.S01/Lanterns.S01E03.mkv", p, 9_318_080_854),
        F(3, "Lanterns.S01/Lanterns.S01E04.mkv", p, 9_000_000_000));

    static JArray NewFilesSameNamesSameSizes(double p = 0) => new JArray(
        F(0, "Lanterns.S01/Lanterns.S01E01.mkv", p, 10_254_031_737),
        F(1, "Lanterns.S01/Lanterns.S01E02.mkv", p, 9_218_196_941),
        F(2, "Lanterns.S01/Lanterns.S01E03.mkv", p, 9_214_986_706),
        F(3, "Lanterns.S01/Lanterns.S01E04.mkv", p, 9_000_000_000));

    static JObject Rec(JObject next = null)
    {
        var m = new JObject { ["hash"] = OldHash, ["link"] = Link, ["id"] = 95350, ["title"] = "Фонари", ["ctx"] = new JObject { ["title"] = "Фонари", ["season"] = 1 } };
        if (next != null) m["next"] = next;
        return m;
    }

    static JObject Next(string mode, string reason = "regrab", int daysAgo = 1, int deadlineInDays = 6) => new JObject
    {
        ["hash"] = NewHash, ["reason"] = reason, ["link"] = Link, ["mode"] = mode,
        ["savepath"] = mode == "flat" ? "/downloads" : "/downloads/.next/" + NewHash.Substring(0, 8),
        ["since"] = DateTime.UtcNow.AddDays(-daysAgo).ToString("o"),
        ["deadline"] = DateTime.UtcNow.AddDays(deadlineInDays).ToString("o")
    };

    static string Info(string hash, double progress, string contentPath, string savePath = "/downloads", string state = "downloading", string tags = "", string category = "lampa")
        => new JObject { ["hash"] = hash, ["progress"] = progress, ["content_path"] = contentPath, ["save_path"] = savePath, ["state"] = state,
                         ["tags"] = tags, ["category"] = category, ["completed"] = 0, ["downloaded"] = 0 }.ToString(Newtonsoft.Json.Formatting.None);

    static string Body(HttpRequestMessage r) => r.Content == null ? "" : r.Content.ReadAsStringAsync().Result;
    static IEnumerable<HttpRequestMessage> Calls(FakeQbit f, string needle) => f.Requests.Where(r => (r.RequestUri?.ToString() ?? "").Contains(needle));

    static async Task<T> Invoke<T>(string name, params object[] args)
    {
        var t = (Task)Access.Call(name, args);
        await t;
        return (T)t.GetType().GetProperty("Result")!.GetValue(t)!;
    }
    static async Task InvokeVoid(string name, params object[] args) => await (Task)Access.Call(name, args);

    // m["next"] = null у Newtonsoft — это JValue(Null), а не отсутствие ключа; продакшен читает через NextOf (as JObject)
    static void AssertNoNext(JToken m) => Assert.Null((m as JObject)?["next"] as JObject);

    // ── чистая логика ─────────────────────────────────────────────────────
    [Fact]
    public void DecideMode_NoOverlap_Flat()
        => Assert.Equal("flat", QbitController.DecideSuccessorMode(OldFiles(), new JArray(F(0, "Other.Release/e01.mkv", 0, 5))));

    [Fact]
    public void DecideMode_SameNamesSameSizes_Flat()
        => Assert.Equal("flat", QbitController.DecideSuccessorMode(OldFiles(), NewFilesSameNamesSameSizes()));

    [Fact]
    public void DecideMode_SameNameOtherSize_Aside()   // «Фонари»: те же имена, другие байты
        => Assert.Equal("aside", QbitController.DecideSuccessorMode(OldFiles(), NewFilesSameNamesOtherSizes()));

    [Fact]
    public void DecideMode_NoMetadata_Meta()
        => Assert.Equal("meta", QbitController.DecideSuccessorMode(OldFiles(), new JArray()));

    [Fact]
    public void Covers_AllOldEpisodesDoneInNew_True()
        => Assert.True(QbitController.SuccessorCovers(OldFiles(), NewFilesSameNamesOtherSizes(1), 1));

    [Fact]
    public void Covers_MissingEpisode_False()
    {
        var nf = NewFilesSameNamesOtherSizes(1);
        ((JObject)nf[2])["progress"] = 0.7;   // E03 ещё качается
        Assert.False(QbitController.SuccessorCovers(OldFiles(), nf, 1));
    }

    [Fact]
    public void Covers_OldIncompleteEpisodeNotRequired()
    {
        var nf = NewFilesSameNamesOtherSizes(1);
        ((JObject)nf[2])["progress"] = 0.1;
        Assert.True(QbitController.SuccessorCovers(OldFiles(p3: 0.4), nf, 1));   // E03 у старой не докачана — не требуется
    }

    [Fact]
    public void Covers_ExtrasIgnored_ButNoParsedEpisodes_False()
    {
        var old = new JArray(F(0, "Show/Extras/Making.Of.mkv", 1, 100));
        Assert.False(QbitController.SuccessorCovers(old, new JArray(F(0, "Show/Show.S01E01.mkv", 1, 100)), 1));
    }

    // ── старт замены ──────────────────────────────────────────────────────
    static FakeQbit StartFake(JArray oldFiles, JArray newFiles, string addBody = "Ok.", string oldInfo = null)
    {
        string oi = oldInfo ?? Info(OldHash, 1, "/downloads/Lanterns.S01");
        return new FakeQbit()
            .Json("/torrents/info?hashes=" + OldHash, "[" + oi + "]")
            .Json("/torrents/info?hashes=" + NewHash, "[" + Info(NewHash, 0, "/downloads/.next/" + NewHash.Substring(0, 8) + "/Lanterns.S01", "/downloads/.next/" + NewHash.Substring(0, 8), "stoppedDL", "qdl-next") + "]")
            .Json("/torrents/files?hash=" + OldHash, oldFiles.ToString(Newtonsoft.Json.Formatting.None))
            .Json("/torrents/files?hash=" + NewHash, newFiles.ToString(Newtonsoft.Json.Formatting.None))
            .Text("/torrents/add", addBody)
            .Text("/torrents/stop", "")
            .Text("/torrents/setLocation", "")
            .Text("/torrents/start", "")
            .Text("/torrents/delete", "");
    }

    [Fact]
    public async Task Start_Regrab_SizeMismatch_AsideKeepsOldStoppedNotDeleted()
    {
        var fake = StartFake(OldFiles(), NewFilesSameNamesOtherSizes());
        var m = Rec();
        var st = await Invoke<QbitController.SuccessorStart>("StartSuccessor", fake.Build(), m, Magnet, NewHash, "regrab", Link, null, new[] { m });

        Assert.Equal(QbitController.SuccessorStart.Started, st);
        Assert.DoesNotContain(fake.Requests, r => r.RequestUri!.ToString().Contains("/torrents/delete"));
        var add = Assert.Single(Calls(fake, "/torrents/add"));
        string body = Body(add);
        Assert.Contains("/downloads/.next/" + NewHash.Substring(0, 8), body);   // своя подпапка — по построению не поверх старых файлов
        Assert.Contains("qdl-next", body);
        Assert.Contains("MetadataReceived", body);
        Assert.Single(Calls(fake, "/torrents/stop"));
        Assert.Empty(Calls(fake, "/torrents/setLocation"));                       // aside: остаётся рядом
        Assert.Single(Calls(fake, "/torrents/start"));                            // преемник запущен

        var next = m["next"] as JObject;
        Assert.NotNull(next);
        Assert.Equal(NewHash, next.Value<string>("hash"));
        Assert.Equal("aside", next.Value<string>("mode"));
        Assert.Equal("regrab", next.Value<string>("reason"));
        Assert.Equal(OldHash, m.Value<string>("hash"));                           // основная — по-прежнему старая
    }

    [Fact]
    public async Task Start_Regrab_SameSizes_FlatMovesToRootAndStarts()
    {
        var fake = StartFake(OldFiles(), NewFilesSameNamesSameSizes());
        var m = Rec();
        var st = await Invoke<QbitController.SuccessorStart>("StartSuccessor", fake.Build(), m, Magnet, NewHash, "regrab", Link, null, new[] { m });

        Assert.Equal(QbitController.SuccessorStart.Started, st);
        var loc = Assert.Single(Calls(fake, "/torrents/setLocation"));
        Assert.Contains("location=%2Fdownloads", Body(loc));
        Assert.Equal("flat", (m["next"] as JObject)!.Value<string>("mode"));
        Assert.Equal("/downloads", (m["next"] as JObject)!.Value<string>("savepath"));
        Assert.Single(Calls(fake, "/torrents/start"));
    }

    [Fact]
    public async Task Start_OldHasNoCompleteEpisodes_Immediate_NoAdd()
    {
        var fake = StartFake(new JArray(F(0, "Lanterns.S01/Lanterns.S01E01.mkv", 0.3, 10)), NewFilesSameNamesOtherSizes());
        var m = Rec();
        var st = await Invoke<QbitController.SuccessorStart>("StartSuccessor", fake.Build(), m, Magnet, NewHash, "regrab", Link, null, new[] { m });
        Assert.Equal(QbitController.SuccessorStart.Immediate, st);
        Assert.Empty(Calls(fake, "/torrents/add"));
        AssertNoNext(m);
    }

    [Fact]
    public async Task Start_Disabled_Immediate()
    {
        ModInit.conf.successorEnabled = false;
        var fake = StartFake(OldFiles(), NewFilesSameNamesOtherSizes());
        var m = Rec();
        var st = await Invoke<QbitController.SuccessorStart>("StartSuccessor", fake.Build(), m, Magnet, NewHash, "regrab", Link, null, new[] { m });
        Assert.Equal(QbitController.SuccessorStart.Immediate, st);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Start_AddFailed_Failed_OldUntouched()
    {
        var fake = StartFake(OldFiles(), NewFilesSameNamesOtherSizes(), addBody: "Fails.");
        var m = Rec();
        var st = await Invoke<QbitController.SuccessorStart>("StartSuccessor", fake.Build(), m, Magnet, NewHash, "regrab", Link, null, new[] { m });
        Assert.Equal(QbitController.SuccessorStart.Failed, st);
        Assert.Empty(Calls(fake, "/torrents/stop"));
        AssertNoNext(m);
    }

    [Fact]
    public async Task Start_DuplicateVisibleDownload_Immediate()
    {
        // кто-то уже нажал «Скачать» на этой раздаче: она сидит в lampa без нашего тега
        var fake = new FakeQbit()
            .Json("/torrents/info?hashes=" + OldHash, "[" + Info(OldHash, 1, "/downloads/Lanterns.S01") + "]")
            .Json("/torrents/info?hashes=" + NewHash, "[" + Info(NewHash, 0.2, "/downloads/Lanterns.S01") + "]")
            .Json("/torrents/files?hash=" + OldHash, OldFiles().ToString(Newtonsoft.Json.Formatting.None))
            .Text("/torrents/add", "Conflict", HttpStatusCode.Conflict);
        var m = Rec();
        var st = await Invoke<QbitController.SuccessorStart>("StartSuccessor", fake.Build(), m, Magnet, NewHash, "regrab", Link, null, new[] { m });
        Assert.Equal(QbitController.SuccessorStart.Immediate, st);
        AssertNoNext(m);
    }

    // ── жнец ──────────────────────────────────────────────────────────────
    static FakeQbit ReapFake(string mode, JArray newFiles, double newProgress, JArray oldFiles = null, bool newInCategory = true, bool oldInCategory = true,
                             string otherSharingOld = null)
    {
        string nextDir = "/downloads/.next/" + NewHash.Substring(0, 8);
        string newContent = mode == "aside" ? nextDir + "/Lanterns.S01" : "/downloads/Lanterns.S01";
        string newSave = mode == "aside" ? nextDir : "/downloads";
        string oi = Info(OldHash, 1, "/downloads/Lanterns.S01", state: "stoppedUP");
        string ni = Info(NewHash, newProgress, newContent, newSave, tags: "qdl-next");
        var cat = new List<string>();
        if (oldInCategory) cat.Add(oi);
        if (newInCategory) cat.Add(ni);
        if (otherSharingOld != null) cat.Add(Info(Third, 1, otherSharingOld));
        return new FakeQbit()
            .Json("/torrents/info?category=lampa", "[" + string.Join(",", cat) + "]")
            .Json("/torrents/info?category=lampa-donor", "[]")
            .Json("/torrents/info?hashes=" + OldHash, oldInCategory ? "[" + oi + "]" : "[]")
            .Json("/torrents/info?hashes=" + NewHash, newInCategory ? "[" + ni + "]" : "[]")
            .Json("/torrents/files?hash=" + OldHash, (oldFiles ?? OldFiles()).ToString(Newtonsoft.Json.Formatting.None))
            .Json("/torrents/files?hash=" + NewHash, newFiles.ToString(Newtonsoft.Json.Formatting.None))
            .Text("/torrents/delete", "").Text("/torrents/removeTags", "").Text("/torrents/setLocation", "")
            .Text("/torrents/start", "").Text("/torrents/stop", "");
    }

    static async Task<JArray> Reap(FakeQbit fake, JObject rec)
    {
        var list = new JArray(rec);
        var orig = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OldHash };
        await InvokeVoid("ScanSuccessors", fake.Build(), list, orig);
        return list;
    }

    [Fact]
    public async Task Reap_Covered_Aside_DeletesOldWithFiles_MigratesRecord()
    {
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "meta", OldHash + ".json").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), "{\"id\":95350,\"title\":\"Фонари\"}");
        var fake = ReapFake("aside", NewFilesSameNamesOtherSizes(1), 1);
        var rec = Rec(Next("aside"));
        await Reap(fake, rec);

        var del = Assert.Single(Calls(fake, "/torrents/delete"));
        Assert.Contains("hashes=" + OldHash, Body(del));
        Assert.Contains("deleteFiles=true", Body(del));                          // подтверждённая замена, своя папка
        Assert.Single(Calls(fake, "/torrents/removeTags"));
        Assert.Single(Calls(fake, "/torrents/setLocation"));                     // назад в общий корень: папки Lanterns.S01 нет
        Assert.Equal(NewHash, rec.Value<string>("hash"));
        AssertNoNext(rec);
        Assert.Equal(0, rec.Value<int>("stale"));
        Assert.True(File.Exists(Path.Combine(ModInit.conf.cachePath, "meta", NewHash + ".json")));   // MigrateCache
        Assert.False(File.Exists(Path.Combine(ModInit.conf.cachePath, "meta", OldHash + ".json")));
    }

    [Fact]
    public async Task Reap_Covered_Flat_DeletesOldWithoutFiles()
    {
        var fake = ReapFake("flat", NewFilesSameNamesSameSizes(1), 1);
        var rec = Rec(Next("flat"));
        await Reap(fake, rec);
        var del = Assert.Single(Calls(fake, "/torrents/delete"));
        Assert.Contains("deleteFiles=false", Body(del));                         // общая папка — файлы не трогаем
        Assert.Empty(Calls(fake, "/torrents/setLocation"));
        Assert.Equal(NewHash, rec.Value<string>("hash"));
    }

    [Fact]
    public async Task Reap_Covered_Aside_OtherDownloadSharesOldFolder_KeepsFiles()
    {
        var fake = ReapFake("aside", NewFilesSameNamesOtherSizes(1), 1, otherSharingOld: "/downloads/Lanterns.S01/extra.mkv");
        var rec = Rec(Next("aside"));
        await Reap(fake, rec);
        Assert.Contains("deleteFiles=false", Body(Assert.Single(Calls(fake, "/torrents/delete"))));
    }

    [Fact]
    public async Task Reap_NotCovered_BeforeDeadline_Waits()
    {
        var nf = NewFilesSameNamesOtherSizes(1);
        ((JObject)nf[1])["progress"] = 0.5;   // E02 ещё качается
        var fake = ReapFake("aside", nf, 0.8);
        var rec = Rec(Next("aside"));
        await Reap(fake, rec);
        Assert.Empty(Calls(fake, "/torrents/delete"));
        Assert.NotNull(rec["next"]);
        Assert.Equal(OldHash, rec.Value<string>("hash"));
    }

    [Fact]
    public async Task Reap_Overdue_ForcedCutOver_KeepsOldFiles()
    {
        var nf = NewFilesSameNamesOtherSizes(1);
        ((JObject)nf[1])["progress"] = 0.5;
        var fake = ReapFake("aside", nf, 0.8);
        var rec = Rec(Next("aside", daysAgo: 9, deadlineInDays: -1));
        await Reap(fake, rec);
        var del = Assert.Single(Calls(fake, "/torrents/delete"));
        Assert.Contains("hashes=" + OldHash, Body(del));
        Assert.Contains("deleteFiles=false", Body(del));                         // по сроку — файлы старой остаются
        Assert.Equal(NewHash, rec.Value<string>("hash"));
        AssertNoNext(rec);
    }

    [Fact]
    public async Task Reap_SuccessorGone_RestoresOld()
    {
        var fake = ReapFake("aside", NewFilesSameNamesOtherSizes(), 0, newInCategory: false);
        var rec = Rec(Next("aside"));
        await Reap(fake, rec);
        AssertNoNext(rec);
        Assert.Equal(OldHash, rec.Value<string>("hash"));
        Assert.Contains("hashes=" + OldHash, Body(Assert.Single(Calls(fake, "/torrents/start"))));
        Assert.Empty(Calls(fake, "/torrents/delete"));
    }

    [Fact]
    public async Task Reap_QbitDown_NoChanges()
    {
        var fake = new FakeQbit().Text("/torrents/info", "down", HttpStatusCode.InternalServerError).Text("/torrents/delete", "");
        var rec = Rec(Next("aside", daysAgo: 9, deadlineInDays: -1));
        await Reap(fake, rec);
        Assert.NotNull(rec["next"]);
        Assert.Empty(Calls(fake, "/torrents/delete"));
    }

    [Fact]
    public async Task Reap_PlayedRecently_Defers()
    {
        JsonStore.Write(Path.Combine(ModInit.conf.cachePath, "replica-played.json"),
                        new JObject { [OldHash] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60 });
        var fake = ReapFake("aside", NewFilesSameNamesOtherSizes(1), 1);
        var rec = Rec(Next("aside"));
        await Reap(fake, rec);
        Assert.Empty(Calls(fake, "/torrents/delete"));
        Assert.NotNull(rec["next"]);
    }

    [Fact]
    public async Task Reap_MetaMode_MetadataArrived_DecidesAndStarts()
    {
        var fake = ReapFake("meta", NewFilesSameNamesOtherSizes(), 0);
        var rec = Rec(Next("meta"));
        await Reap(fake, rec);
        Assert.Equal("aside", (rec["next"] as JObject)!.Value<string>("mode"));
        Assert.Single(Calls(fake, "/torrents/start"));
        Assert.Empty(Calls(fake, "/torrents/delete"));
    }

    // ── отмена ────────────────────────────────────────────────────────────
    [Fact]
    public async Task Abort_Aside_DeletesSuccessorWithFiles_ResumesOld()
    {
        var fake = ReapFake("aside", NewFilesSameNamesOtherSizes(), 0.3);
        var rec = Rec(Next("aside"));
        await InvokeVoid("AbortSuccessor", fake.Build(), rec, "тест", true);
        var del = Assert.Single(Calls(fake, "/torrents/delete"));
        Assert.Contains("hashes=" + NewHash, Body(del));
        Assert.Contains("deleteFiles=true", Body(del));                          // своя подпапка .next
        Assert.Contains("hashes=" + OldHash, Body(Assert.Single(Calls(fake, "/torrents/start"))));
        AssertNoNext(rec);
    }

    [Fact]
    public async Task Abort_Flat_DeletesSuccessorWithoutFiles()
    {
        var fake = ReapFake("flat", NewFilesSameNamesSameSizes(), 0.3);
        var rec = Rec(Next("flat"));
        await InvokeVoid("AbortSuccessor", fake.Build(), rec, "тест", false);
        Assert.Contains("deleteFiles=false", Body(Assert.Single(Calls(fake, "/torrents/delete"))));
        Assert.Empty(Calls(fake, "/torrents/start"));
    }

    // ── что видит зритель ─────────────────────────────────────────────────
    [Fact]
    public void Merge_NextRowsOnlyWhereOldLacksOrIncomplete()
    {
        var main = new JArray(F(0, "L/L.S01E01.mkv", 1, 10), F(1, "L/L.S01E02.mkv", 0.3, 10));
        var next = new JArray(F(0, "L/L.S01E01.mkv", 0.5, 11), F(1, "L/L.S01E02.mkv", 1, 11), F(2, "L/L.S01E03.mkv", 0.2, 11), F(3, "L/Extras/bonus.mkv", 1, 5));
        var rows = (JArray)Access.Call("MergeEpisodeFiles", OldHash, main, new List<(JObject donor, JArray files)>(), "t1", 1, NewHash, next, false);

        var e1 = rows.First(r => r.Value<int?>("episode") == 1);
        var e2 = rows.First(r => r.Value<int?>("episode") == 2);
        var e3 = rows.First(r => r.Value<int?>("episode") == 3);
        Assert.Equal("main", e1.Value<string>("source"));      // старая докачана — её и показываем
        Assert.Equal("next", e2.Value<string>("source"));      // у старой качается, у преемника готова
        Assert.Equal(NewHash, e2.Value<string>("hash"));
        Assert.Equal("next", e3.Value<string>("source"));      // у старой нет — качающаяся строка преемника
        Assert.Equal("t1:s1e3", e3.Value<string>("tl"));       // общий ключ таймлайна
        Assert.DoesNotContain(rows, r => (r.Value<string>("name") ?? "").Contains("bonus"));   // экстры преемника — не до жатвы
    }

    [Fact]
    public void Merge_SharedPathAfterCheck_TrustsNext()
    {
        var main = new JArray(F(0, "L/L.S01E01.mkv", 1, 10));
        var next = new JArray(F(0, "L/L.S01E01.mkv", 0.4, 10));
        var rows = (JArray)Access.Call("MergeEpisodeFiles", OldHash, main, new List<(JObject donor, JArray files)>(), "t1", 1, NewHash, next, true);
        Assert.Equal("next", Assert.Single(rows).Value<string>("source"));   // тот же файл на диске перезаписывается — истина у преемника
    }

    [Fact]
    public void Merge_DonorDoneNextIncomplete_KeepsDonor_BothDone_NextWins()
    {
        var main = new JArray();
        var donor = new JObject { ["hash"] = Third, ["score"] = 10, ["quality"] = 1080,
            ["eps"] = new JArray(new JObject { ["epkey"] = "s1e5", ["season"] = 1, ["ep"] = 5, ["fileIndex"] = 0, ["status"] = "hunted" }) };
        var donorData = new List<(JObject donor, JArray files)> { (donor, new JArray(F(0, "D/D.S01E05.mkv", 1, 10))) };

        var rows = (JArray)Access.Call("MergeEpisodeFiles", OldHash, main, donorData, "t1", 1, NewHash, new JArray(F(0, "L/L.S01E05.mkv", 0.5, 10)), false);
        Assert.Equal("donor", Assert.Single(rows).Value<string>("source"));

        rows = (JArray)Access.Call("MergeEpisodeFiles", OldHash, main, donorData, "t1", 1, NewHash, new JArray(F(0, "L/L.S01E05.mkv", 1, 10)), false);
        Assert.Equal("next", Assert.Single(rows).Value<string>("source"));
    }

    [Fact]
    public async Task List_HidesSuccessor()
    {
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "watch.json"), new JArray(Rec(Next("aside"))).ToString());
        string torrents = "[" + Info(OldHash, 1, "/downloads/Lanterns.S01", state: "stoppedUP") + "," + Info(NewHash, 0.3, "/downloads/.next/x/Lanterns.S01", tags: "qdl-next") + "]";
        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", torrents).BuildHandler());
        try
        {
            QbitController.DropListCache();
            var ctrl = new QbitController { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
            var res = await ctrl.List();
            var arr = JArray.Parse(Assert.IsType<Microsoft.AspNetCore.Mvc.ContentResult>(res).Content!);
            Assert.Contains(arr, x => x.Value<string>("hash") == OldHash);
            Assert.DoesNotContain(arr, x => x.Value<string>("hash") == NewHash);
        }
        finally { Access.ResetQbitFake(); QbitController.DropListCache(); }
    }

    [Fact]
    public async Task ProgressFiles_IncludesSuccessorHash()
    {
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "watch.json"), new JArray(Rec(Next("aside"))).ToString());
        Access.SeedQbitFake(new FakeQbit()
            .Json("/torrents/files?hash=" + OldHash, OldFiles().ToString(Newtonsoft.Json.Formatting.None))
            .Json("/torrents/files?hash=" + NewHash, NewFilesSameNamesOtherSizes(0.5).ToString(Newtonsoft.Json.Formatting.None))
            .BuildHandler());
        try
        {
            QbitController.DropProgressCache();
            var res = await Invoke<JObject>("ProgressFilesFor", OldHash);
            Assert.NotNull(res);
            Assert.NotNull(res[OldHash]);
            Assert.NotNull(res[NewHash]);
        }
        finally { Access.ResetQbitFake(); QbitController.DropProgressCache(); }
    }

    [Fact]
    public void PendingFor_MainAndSuccessor()
    {
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "watch.json"), new JArray(Rec(Next("aside"))).ToString());
        Assert.True((bool)Access.Call("SuccessorPendingFor", OldHash));
        Assert.True((bool)Access.Call("SuccessorPendingFor", NewHash));
        Assert.False((bool)Access.Call("SuccessorPendingFor", Third));
    }

    // ── фоновые контуры ───────────────────────────────────────────────────
    [Fact]
    public async Task HuntPrepare_SkipsWhilePending()
    {
        var fake = new FakeQbit().Json("/torrents/files", OldFiles().ToString(Newtonsoft.Json.Formatting.None));
        var m = Rec(Next("aside")); m["ctx"]!["is_serial"] = 2;
        var t = (Task)Access.Call("HuntPrepare", fake.Build(), m, false, true);
        await t;
        var prep = t.GetType().GetProperty("Result")!.GetValue(t)!;
        string skip = (string)prep.GetType().GetField("skip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(prep)!;
        Assert.Equal("successor-pending", skip);
    }

    [Fact]
    public async Task Reconcile_TagWithoutRef_RemovesTag_NoDelete()
    {
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "watch.json"), "[]");
        var fake = new FakeQbit()
            .Json("/torrents/info?category=lampa", "[" + Info(NewHash, 0.5, "/downloads/.next/x/L", "/downloads/.next/x", tags: "qdl-next") + "]")
            .Text("/torrents/removeTags", "").Text("/torrents/delete", "");
        Access.SeedQbitFake(fake.BuildHandler());
        try { await QbitController.ReconcileSuccessors(); }
        finally { Access.ResetQbitFake(); }
        Assert.Single(Calls(fake, "/torrents/removeTags"));
        Assert.Empty(Calls(fake, "/torrents/delete"));
    }

    [Fact]
    public async Task Reconcile_RefWithoutTorrent_RestoresOld()
    {
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "watch.json"), new JArray(Rec(Next("aside"))).ToString());
        var fake = new FakeQbit()
            .Json("/torrents/info?category=lampa", "[" + Info(OldHash, 1, "/downloads/L", state: "stoppedUP") + "]")
            .Json("/torrents/info?hashes=" + NewHash, "[]")
            .Text("/torrents/start", "");
        Access.SeedQbitFake(fake.BuildHandler());
        try { await QbitController.ReconcileSuccessors(); }
        finally { Access.ResetQbitFake(); }
        Assert.Single(Calls(fake, "/torrents/start"));
        var saved = JArray.Parse(File.ReadAllText(Path.Combine(ModInit.conf.cachePath, "watch.json")));
        AssertNoNext(saved[0]);
    }

    [Fact]
    public void HealthVerdict_Ok_Warn()
    {
        var now = DateTime.UtcNow;
        Assert.Equal("ok", QbitController.SuccessorHealthVerdict(new JArray(Rec()), now).status);
        Assert.Equal("ok", QbitController.SuccessorHealthVerdict(new JArray(Rec(Next("aside"))), now).status);
        Assert.Equal("warn", QbitController.SuccessorHealthVerdict(new JArray(Rec(Next("meta", daysAgo: 1))), now).status);
        Assert.Equal("warn", QbitController.SuccessorHealthVerdict(new JArray(Rec(Next("aside", daysAgo: 9, deadlineInDays: -1))), now).status);
    }
}

static class PathExt
{
    public static string Also(this string p, Action<string> a) { a(p); return p; }
}
