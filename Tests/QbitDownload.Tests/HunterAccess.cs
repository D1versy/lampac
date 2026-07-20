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

    public static object MakeHuntCtx(string mainHash, int season, IEnumerable<string> known, IEnumerable<string> blacklist,
                                     int minSeeds, int minQuality, int minMb, int maxGb)
    {
        var h = Activator.CreateInstance(HuntCtxT);
        HuntCtxT.GetField("mainHash").SetValue(h, mainHash);
        HuntCtxT.GetField("season").SetValue(h, season);
        HuntCtxT.GetField("knownHashes").SetValue(h, new HashSet<string>(known ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));
        HuntCtxT.GetField("blacklistKeys").SetValue(h, new HashSet<string>(blacklist ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));
        HuntCtxT.GetField("minSeeds").SetValue(h, minSeeds);
        HuntCtxT.GetField("minQuality").SetValue(h, minQuality);
        HuntCtxT.GetField("minMb").SetValue(h, minMb);
        HuntCtxT.GetField("maxGb").SetValue(h, maxGb);
        return h;
    }

    public static List<JObject> FilterDonorCandidates(JArray scored, object huntCtx)
        => (List<JObject>)Access.Call("FilterDonorCandidates", scored, huntCtx);

    public static List<JObject> OrderByCover(List<JObject> eligible, int season, List<int> wanted)
        => (List<JObject>)Access.Call("OrderByCover", eligible, season, wanted);

    public static List<(int index, int ep, int season, string epkey, long size)> FindEpFiles(JArray files, int season, List<int> wanted, string candidateTitle)
    {
        object titleEp = candidateTitle == null ? null : Access.Call("ParseEp", candidateTitle);
        var raw = (IEnumerable)Access.Call("FindEpFiles", files, season, wanted, titleEp);
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
    public static HashSet<string> BlacklistKeys(JObject item) => (HashSet<string>)Access.Call("BlacklistKeys", item);

    // ── qBit-хелперы (инъецируемый HttpClient) ────────────────────────────
    public static Task<bool> QbitAddMagnetEx(HttpClient c, string magnet, string category, string tags = null, bool stopAfterMeta = false)
        => (Task<bool>)Access.Call("QbitAddMagnetEx", c, magnet, category, tags, stopAfterMeta);
    public static Task<JArray> QbitFiles(HttpClient c, string hash) => (Task<JArray>)Access.Call("QbitFiles", c, hash);
    public static Task<JObject> QbitTorrentInfo(HttpClient c, string hash) => (Task<JObject>)Access.Call("QbitTorrentInfo", c, hash);
    public static Task<JArray> QbitWaitFiles(HttpClient c, string hash, int timeoutSec) => (Task<JArray>)Access.Call("QbitWaitFiles", c, hash, timeoutSec);
    public static Task<bool> QbitFilePrio(HttpClient c, string hash, IEnumerable<int> ids, int prio)
        => (Task<bool>)Access.Call("QbitFilePrio", c, hash, ids, prio);
    public static Task QbitStartTorrent(HttpClient c, string hash) => (Task)Access.Call("QbitStartTorrent", c, hash);
    public static Task QbitDelete(HttpClient c, string hash, bool deleteFiles) => (Task)Access.Call("QbitDelete", c, hash, deleteFiles);
    public static Task QbitDeleteDonorSafe(HttpClient c, string hash) => (Task)Access.Call("QbitDeleteDonorSafe", c, hash);
    public static Task<string> QbitCategory(HttpClient c, string hash) => (Task<string>)Access.Call("QbitCategory", c, hash);

    // ── реконсиляция watch.json ───────────────────────────────────────────
    public static HashSet<string> WatchHashes(JArray a) => (HashSet<string>)Access.Call("WatchHashes", a);
    public static void SaveWatchReconciled(JArray working, HashSet<string> originalHashes)
        => Access.Call("SaveWatchReconciled", working, originalHashes);
    public static JArray LoadWatch() => (JArray)Access.Call("LoadWatch");
    public static void SaveWatch(JArray a) => Access.Call("SaveWatch", a);
}
