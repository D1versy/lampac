using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QbitDownload;

namespace QbitDownload.Tests;

/// <summary>
/// Reflection gateway to the EpisodeHunter half of <c>QbitController</c> (partial class) —
/// same pattern as <see cref="Access"/>: private statics stay private, tests reach them here.
/// </summary>
public static class HunterAccess
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    internal static readonly Type HuntCtxT = C.GetNestedType("HuntCtx", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+HuntCtx not found");
    internal static readonly Type EpFileT = C.GetNestedType("EpFile", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+EpFile not found");
    internal static readonly Type ReplaceActionT = C.GetNestedType("ReplaceAction", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+ReplaceAction not found");

    // ── чистая логика охоты ───────────────────────────────────────────────
    public static DonorCover TitleCoversEp(string title, int season, int ep)
        => (DonorCover)Access.Call("TitleCoversEp", title, season, ep);

    public static long EstimateEpBytes(long sizeBytes, int haveCount)
        => (long)Access.Call("EstimateEpBytes", sizeBytes, haveCount);

    public static bool EpSizeOk(long estBytes, int minMb, int maxGb)
        => (bool)Access.Call("EpSizeOk", estBytes, minMb, maxGb);

    public static List<int> ComputeWanted(HashSet<int> inventory, int maxClaim)
        => (List<int>)Access.Call("ComputeWanted", inventory, maxClaim);

    public static int MaxClaim(List<JObject> eligible) => (int)Access.Call("MaxClaim", eligible);

    public static int DominantSeason(JArray files) => (int)Access.Call("DominantSeason", files);

    public static HashSet<int> InventoryEps(JArray mainFiles, JArray donors, int season)
        => (HashSet<int>)Access.Call("InventoryEps", mainFiles, donors, season);

    // qdl 2.107: новые гейты выключены по умолчанию (requireRussian=false, rejectUnknownQuality=false),
    // чтобы старые фикстуры (кириллические названия, quality задан явно) вели себя как раньше;
    // кейсы новых гейтов включают их явно.
    public static object MakeHuntCtx(string mainHash, int season, IEnumerable<string> known, IEnumerable<string> blacklist,
                                     int minSeeds, int minQuality, int minMb, int maxGb,
                                     string titleNorm = null, string originalNorm = null, string selfLink = null,
                                     bool requireRussian = false, bool rejectUnknownQuality = false, int targetQuality = 1080,
                                     IEnumerable<(string name, long size)> mainSig = null, string mainName = null,
                                     bool rejectLegacy = true, bool rejectScreener = true)
    {
        var h = Activator.CreateInstance(HuntCtxT);
        HuntCtxT.GetField("selfTopicKey").SetValue(h, selfLink == null ? null : TopicKey(selfLink));
        HuntCtxT.GetField("mainHash").SetValue(h, mainHash);
        HuntCtxT.GetField("season").SetValue(h, season);
        HuntCtxT.GetField("knownHashes").SetValue(h, new HashSet<string>(known ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));
        HuntCtxT.GetField("blacklistKeys").SetValue(h, new HashSet<string>(blacklist ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));
        HuntCtxT.GetField("minSeeds").SetValue(h, minSeeds);
        HuntCtxT.GetField("minQuality").SetValue(h, minQuality);
        HuntCtxT.GetField("minMb").SetValue(h, minMb);
        HuntCtxT.GetField("maxGb").SetValue(h, maxGb);
        HuntCtxT.GetField("titleNorm").SetValue(h, titleNorm);
        HuntCtxT.GetField("originalNorm").SetValue(h, originalNorm);
        HuntCtxT.GetField("requireRussian").SetValue(h, requireRussian);
        HuntCtxT.GetField("rejectUnknownQuality").SetValue(h, rejectUnknownQuality);
        HuntCtxT.GetField("rejectLegacy").SetValue(h, rejectLegacy);
        HuntCtxT.GetField("rejectScreener").SetValue(h, rejectScreener);
        HuntCtxT.GetField("targetQuality").SetValue(h, targetQuality);
        if (mainSig != null)
        {
            var sig = new HashSet<string>(StringComparer.Ordinal);
            int n = 0;
            foreach (var (name, size) in mainSig) { n++; var k = SigKey(name, size); if (k != null) sig.Add(k); }
            HuntCtxT.GetField("mainSig").SetValue(h, sig);
            HuntCtxT.GetField("mainVideoCount").SetValue(h, n);
        }
        if (mainName != null)
            HuntCtxT.GetField("mainNameNorm").SetValue(h, Shared.Services.Utilities.SearchNameTo.Convert(mainName));
        return h;
    }

    public static bool NameMatchesSeries(string title, string titleNorm, string originalNorm)
        => (bool)Access.Call("NameMatchesSeries", title, titleNorm, originalNorm);

    // ── qdl 2.107: bitmagnet в охоте ──────────────────────────────────────
    public static bool NameMatchesSeriesOrId(JObject cand, object huntCtx) => (bool)Access.Call("NameMatchesSeriesOrId", cand, huntCtx);
    public static string TitleHeadBeforeMarker(string title) => (string)Access.Call("TitleHeadBeforeMarker", title);
    public static DonorCover TitleCoversEpItem(JObject cand, int season, int ep) => (DonorCover)Access.Call("TitleCoversEpItem", cand, season, ep);
    public static bool SeasonOkItem(JObject cand, object huntCtx) => (bool)Access.Call("SeasonOkItem", cand, huntCtx);
    public static int QualityRank(int q, int target) => (int)Access.Call("QualityRank", q, target);
    public static int DominantQuality(IEnumerable<string> paths) => (int)Access.Call("DominantQuality", paths);
    public static int DonorTargetQuality(JArray mainFiles, ModuleConf conf) => (int)Access.Call("DonorTargetQuality", mainFiles, conf);
    public static int SizeBucket(long bytesPerEp) => (int)Access.Call("SizeBucket", bytesPerEp);
    public static string SigKey(string name, long size) => (string)Access.Call("SigKey", name, size);
    public static bool LooksLikeOwnRelease(JObject cand, object huntCtx) => (bool)Access.Call("LooksLikeOwnRelease", cand, huntCtx);
    public static string MainRootFolder(JArray mainFiles) => (string)Access.Call("MainRootFolder", mainFiles);
    public static bool LocalTickWaiting(JObject m, JArray mainFiles, JArray donors, int season) => (bool)Access.Call("LocalTickWaiting", m, mainFiles, donors, season);
    public static object BuildHuntPlan(JObject m, JArray mainFiles, string mainName, JArray donors, HashSet<string> donorSig,
                                       JArray scored, IEnumerable<string> lampaHashes, string ctitle, string titleOriginal,
                                       int season, int aired, DateTime now, ModuleConf conf, bool localOnly)
        => Access.Call("BuildHuntPlan", m, mainFiles, mainName, donors, donorSig, scored, lampaHashes, ctitle, titleOriginal, season, aired, now, conf, localOnly);
    public static T PlanField<T>(object plan, string field) => (T)plan.GetType().GetField(field).GetValue(plan);
    public static Task<JArray> HuntDry(string onlyHash, bool localOnly) => (Task<JArray>)Access.Call("HuntDry", onlyHash, localOnly);
    public static Task<int> HuntAll(string onlyHash, bool localOnly) => (Task<int>)Access.Call("HuntAll", onlyHash, localOnly);
    public static void SetLocalWanted(JObject m, List<int> wanted) => Access.Call("SetLocalWanted", m, wanted);
    /// <summary>Посеять/снять запись кеша эфира TMDB (_airedCache[id:season]); aired &lt;= 0 — удалить.</summary>
    public static void SeedAiredCache(int id, int season, int aired)
    {
        var dict = C.GetField("_airedCache", SF)!.GetValue(null)!;
        string key = id + ":" + season;
        var t = dict.GetType();
        if (aired <= 0) { t.GetMethod("TryRemove", new[] { typeof(string), t.GetGenericArguments()[1].MakeByRefType() })!.Invoke(dict, new object[] { key, null }); return; }
        t.GetProperty("Item")!.SetValue(dict, ValueTuple.Create(aired, DateTime.UtcNow), new object[] { key });
    }

    public static string TopicKey(string link) => (string)Access.Call("TopicKey", link);
    public static bool PathsOverlap(string a, string b) => (bool)Access.Call("PathsOverlap", a, b);
    public static int DropDonorRefs(IEnumerable<JObject> items, string hash) => (int)Access.Call("DropDonorRefs", items, hash);
    public static bool IsDonorRef(IEnumerable<JObject> items, string hash) => (bool)Access.Call("IsDonorRef", items, hash);

    public static List<JObject> FilterDonorCandidates(JArray scored, object huntCtx)
        => (List<JObject>)Access.Call("FilterDonorCandidates", scored, huntCtx);

    public static List<JObject> OrderByCover(List<JObject> eligible, int season, List<int> wanted, int targetQuality = 1080)
        => (List<JObject>)Access.Call("OrderByCover", eligible, season, wanted, targetQuality);

    public static List<(int index, int ep, int season, string epkey, long size)> FindEpFiles(JArray files, int season, List<int> wanted, string candidateTitle, int donorSeason = 0)
    {
        object titleEp = candidateTitle == null ? null : Access.Call("ParseEp", candidateTitle);
        var raw = (IEnumerable)Access.Call("FindEpFiles", files, season, wanted, titleEp, donorSeason);
        var res = new List<(int, int, int, string, long)>();
        foreach (var it in raw)
            res.Add(((int)EpFileT.GetField("index").GetValue(it), (int)EpFileT.GetField("ep").GetValue(it),
                     (int)EpFileT.GetField("season").GetValue(it), (string)EpFileT.GetField("epkey").GetValue(it),
                     (long)EpFileT.GetField("size").GetValue(it)));
        return res;
    }

    public static JArray MergeEpisodeFiles(string mainHash, JArray mainFiles, List<(JObject donor, JArray files)> donorData, string seriesKey, int season)
        => (JArray)Access.Call("MergeEpisodeFiles", mainHash, mainFiles, donorData, seriesKey, season);

    public static List<(string kind, string donorHash, int fileIndex)> PlanReplacements(JArray mainFiles, JObject item, Dictionary<string, JArray> donorFiles, DateTime now, int staleDays)
    {
        var raw = (IEnumerable)Access.Call("PlanReplacements", mainFiles, item, donorFiles, now, staleDays);
        var res = new List<(string, string, int)>();
        foreach (var it in raw)
            res.Add(((string)ReplaceActionT.GetField("kind").GetValue(it),
                     (string)ReplaceActionT.GetField("donorHash").GetValue(it),
                     (int)ReplaceActionT.GetField("fileIndex").GetValue(it)));
        return res;
    }

    // ── blacklist ─────────────────────────────────────────────────────────
    public static void BlacklistAdd(JObject item, string btih, string parselink, string reason, int ttlDays)
        => Access.Call("BlacklistAdd", item, btih, parselink, reason, ttlDays);
    public static void PruneBlacklist(JObject item, DateTime now) => Access.Call("PruneBlacklist", item, now);
    public static HashSet<string> BlacklistKeys(JObject item) => BlacklistKeys(item, DateTime.UtcNow);
    public static HashSet<string> BlacklistKeys(JObject item, DateTime now) => (HashSet<string>)Access.Call("BlacklistKeys", item, now);
    public static void BlacklistAddMinutes(JObject item, string btih, string parselink, string reason, int minutes, int attempt)
        => Access.Call("BlacklistAddMinutes", item, btih, parselink, reason, minutes, attempt);
    public static int TransientFailMinutes(int attempt) => (int)Access.Call("TransientFailMinutes", attempt);
    public static int BlacklistAttempts(JObject item, string key, string reason) => (int)Access.Call("BlacklistAttempts", item, key, reason);

    // ── сезон донора / учёт заявленных серий ──────────────────────────────
    public static int SeasonFromPath(string relName) => (int)Access.Call("SeasonFromPath", relName);
    public static HashSet<int> DonorSeasons(JArray files) => (HashSet<int>)Access.Call("DonorSeasons", files);
    public static int DonorSeason(JArray files, string title) => (int)Access.Call("DonorSeason", files, title);
    public static int ClaimOf(JObject t) => (int)Access.Call("ClaimOf", t);
    public static List<JObject> ClaimCandidates(JArray scored, object huntCtx) => (List<JObject>)Access.Call("ClaimCandidates", scored, huntCtx);
    public static int SelfTopicClaim(JArray scored, object huntCtx) => (int)Access.Call("SelfTopicClaim", scored, huntCtx);

    public static List<int> ComputeUpgrades(JArray donors, JArray scored, List<JObject> eligible, HashSet<int> mainEps, int season, int minScoreGain,
                                            int targetQuality = 1080, HashSet<string> donorSig = null)
        => (List<int>)Access.Call("ComputeUpgrades", donors, scored, eligible, mainEps, season, minScoreGain, null, targetQuality, donorSig);

    // ── qBit-хелперы (инъецируемый HttpClient) ────────────────────────────
    public static Task<bool> QbitAddMagnetEx(HttpClient c, string magnet, string category, string tags = null, bool stopAfterMeta = false)
        => (Task<bool>)Access.Call("QbitAddMagnetEx", c, magnet, category, tags, stopAfterMeta);
    public static Task<QbitAddStatus> QbitAddMagnetStatus(HttpClient c, string magnet, string category, string tags = null, bool stopAfterMeta = false)
        => (Task<QbitAddStatus>)Access.Call("QbitAddMagnetStatus", c, magnet, category, tags, stopAfterMeta);
    public static Task<bool> PromoteDonorToMain(HttpClient c, string hash) => (Task<bool>)Access.Call("PromoteDonorToMain", c, hash);
    public static Task<bool> PromoteIfDonor(HttpClient c, string newHash, IEnumerable<JObject> items, string title)
        => (Task<bool>)Access.Call("PromoteIfDonor", c, newHash, items, title);
    public static Task<JArray> QbitFiles(HttpClient c, string hash) => (Task<JArray>)Access.Call("QbitFiles", c, hash);
    public static Task<JObject> QbitTorrentInfo(HttpClient c, string hash) => (Task<JObject>)Access.Call("QbitTorrentInfo", c, hash);
    public static Task<JArray> QbitWaitFiles(HttpClient c, string hash, int timeoutSec) => (Task<JArray>)Access.Call("QbitWaitFiles", c, hash, timeoutSec);
    public static Task<bool> QbitFilePrio(HttpClient c, string hash, IEnumerable<int> ids, int prio)
        => (Task<bool>)Access.Call("QbitFilePrio", c, hash, ids, prio);
    public static Task QbitStartTorrent(HttpClient c, string hash) => (Task)Access.Call("QbitStartTorrent", c, hash);
    public static Task QbitDelete(HttpClient c, string hash, bool deleteFiles) => (Task)Access.Call("QbitDelete", c, hash, deleteFiles);
    public static Task QbitDeleteDonorSafe(HttpClient c, string hash, string mainHash = null, string mainContentPath = null)
        => (Task)Access.Call("QbitDeleteDonorSafe", c, hash, mainHash, mainContentPath);
    public static Task<string> QbitCategory(HttpClient c, string hash) => (Task<string>)Access.Call("QbitCategory", c, hash);

    // ── реконсиляция watch.json ───────────────────────────────────────────
    public static HashSet<string> WatchHashes(JArray a) => (HashSet<string>)Access.Call("WatchHashes", a);
    public static void SaveWatchReconciled(JArray working, HashSet<string> originalHashes)
        => Access.Call("SaveWatchReconciled", working, originalHashes);
    public static JArray LoadWatch() => (JArray)Access.Call("LoadWatch");
    public static void SaveWatch(JArray a) => Access.Call("SaveWatch", a);

    // ── надёжность и покрытие охоты (топ-N проб, пустая выдача, догон после рестарта) ──
    public static List<JObject> ProbeCandidates(List<JObject> eligible, int season, List<int> wanted, int probesPerRun, int targetQuality = 1080)
        => (List<JObject>)Access.Call("ProbeCandidates", eligible, season, wanted, probesPerRun, targetQuality);

    public static string DropReason(JObject cand, object huntCtx) => (string)Access.Call("DropReason", cand, huntCtx);

    public static bool ShouldRetryHunt(int searched, int barren, int retries)
        => (bool)Access.Call("ShouldRetryHunt", searched, barren, retries);

    public static void SetHuntStamp(JObject m, DateTime now, int maxClaim) => Access.Call("SetHuntStamp", m, now, maxClaim);
    public static void MarkHuntBarren(JObject m, DateTime now) => Access.Call("MarkHuntBarren", m, now);

    public static int HuntRetryMax => (int)C.GetField("HuntRetryMax", SF).GetValue(null);
}
