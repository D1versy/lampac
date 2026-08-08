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
/// Reflection gateway to <c>QbitController</c>'s private static logic.
///
/// The QbitDownload module is compiled by the host at runtime (Roslyn) and its logic lives in
/// <c>private static</c> members, so tests reach them via reflection — production code stays 100% untouched.
/// Each method below mirrors a real production method 1:1; add a wrapper here when you need a new one.
/// </summary>
public static class Access
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    internal static readonly Type EpT = C.GetNestedType("Ep", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+Ep not found");

    static MethodInfo M(string name, int argc)
    {
        var ms = C.GetMethods(SF).Where(m => m.Name == name).ToArray();
        if (ms.Length == 0) throw new MissingMethodException("QbitController." + name);
        return ms.Length == 1 ? ms[0] : ms.First(m => m.GetParameters().Length == argc);
    }

    /// <summary>Invoke a private static method by name, unwrapping reflection exceptions.</summary>
    public static object Call(string name, params object[] args)
    {
        try { return M(name, args?.Length ?? 0).Invoke(null, args); }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
    }

    static FieldInfo F(string name) =>
        C.GetField(name, SF) ?? throw new MissingFieldException("QbitController." + name);

    // ── simple pure helpers ───────────────────────────────────────────────
    public static string HumanSize(long b) => (string)Call("HumanSize", b);
    public static int QualityFromTitle(string t) => (int)Call("QualityFromTitle", t);
    public static string CodecFromTitle(string t) => (string)Call("CodecFromTitle", t);
    public static bool ValidHash(string h) => (bool)Call("ValidHash", h);
    public static string ConfinedCombine(string baseDir, string rel) => (string)Call("ConfinedCombine", baseDir, rel);
    public static string MimeType(string p) => (string)Call("MimeType", p);
    public static string LangName(string l) => (string)Call("LangName", l);
    public static string LangCode(string raw, string label) => (string)Call("LangCode", raw, label);
    public static string StripNoise(string s) => (string)Call("StripNoise", s);
    public static bool IsGenericFolder(string s) => (bool)Call("IsGenericFolder", s);
    public static string CleanStudio(string s) => (string)Call("CleanStudio", s);
    public static string StudioId(string s) => (string)Call("StudioId", s);
    public static string StudioOf(string fullPath, string videoBase) => (string)Call("StudioOf", fullPath, videoBase);
    public static bool NormStarts(string a, string b) => (bool)Call("NormStarts", a, b);
    public static int NaturalCompare(string a, string b) => (int)Call("NaturalCompare", a, b);
    public static string MagnetHash(string magnet) => (string)Call("MagnetHash", magnet);
    public static bool LooksLikeTorrent(byte[] data) => (bool)Call("LooksLikeTorrent", data);
    public static string SeriesKey(int seriesId, string link) => (string)Call("SeriesKey", seriesId, link);
    public static bool IsPrivateHost(Uri u) => (bool)Call("IsPrivateHost", u);
    public static bool IsLoopbackSelf(Uri u) => (bool)Call("IsLoopbackSelf", u);
    public static string TmdbPosterPath(string posterUrl, string hash) => (string)Call("TmdbPosterPath", posterUrl, hash);

    // ── JToken-based helpers ──────────────────────────────────────────────
    public static string BaseNoExt(JToken f) => (string)Call("BaseNoExt", f);
    public static JToken FindVideo(JArray files, int index) => (JToken)Call("FindVideo", files, index);

    // ── qBit helpers that accept an injectable HttpClient (async) ─────────
    public static Task<bool> QbitAddMagnet(HttpClient c, string magnet) => (Task<bool>)Call("QbitAddMagnet", c, magnet);
    public static Task<string> ResolveFile(HttpClient c, string hash, int index) => (Task<string>)Call("ResolveFile", c, hash, index);
    public static Task<string> ResolveDubFile(HttpClient c, string hash, int videoIndex, string dubId) => (Task<string>)Call("ResolveDubFile", c, hash, videoIndex, dubId);

    // ── episode model (Ep) ────────────────────────────────────────────────
    public static EpView ParseEp(string baseName) => new EpView(Call("ParseEp", baseName));
    public static string EpKey(EpView e) => (string)Call("EpKey", e.Raw);
    public static string EpLabel(EpView e) => (string)Call("EpLabel", e.Raw);
    public static bool IsEpisodeLike(EpView e) => (bool)Call("IsEpisodeLike", e.Raw);
    public static bool EpEqual(EpView a, EpView b) => (bool)Call("EpEqual", a.Raw, b.Raw);

    // ── dub matcher ───────────────────────────────────────────────────────
    public static List<(string id, string label, int idx)> DubsForVideo(JArray files, JToken video)
    {
        var res = new List<(string, string, int)>();
        var raw = (IEnumerable)Call("DubsForVideo", files, video);
        foreach (var item in raw)
            res.Add(((string, string, int))item);   // ValueTuple is a shared framework type — direct unbox
        return res;
    }

    // ── collections (/qdl/collections) ────────────────────────────────────
    public static bool ValidColId(string id) => (bool)Call("ValidColId", id);
    public static JObject ColCreate(string title, string[] hashes) => (JObject)Call("ColCreate", title, hashes);
    public static bool ColAdd(string id, string hash) => (bool)Call("ColAdd", id, hash);
    public static (bool ok, bool deleted) ColRemove(string id, string hash) => ((bool, bool))Call("ColRemove", id, hash);
    public static bool ColUpdate(string id, string title, string cover) => (bool)Call("ColUpdate", id, title, cover);
    public static bool ColDissolve(string id) => (bool)Call("ColDissolve", id);
    public static JArray LoadCollections() => (JArray)Call("LoadCollections");
    public static void CollectionsRemoveHash(string hash) => Call("CollectionsRemoveHash", hash);
    public static void CollectionsMigrateHash(string oldH, string newH) => Call("CollectionsMigrateHash", oldH, newH);

    // ── purge / transcode queue (§Z) ──────────────────────────────────────
    public static void PurgeCache(string hash) => Call("PurgeCache", hash);
    public static void MigrateCache(string oldH, string newH) => Call("MigrateCache", oldH, newH);
    public static bool HlsCopyVideo(string codec) => (bool)Call("HlsCopyVideo", codec);
    public static List<string> HlsArgs(string dir, string videoPath, string extAudio, string audioMap, bool copyVideo, int startSeg = -1, bool nvenc = false, QbitController.HlsMobileOpts mobile = null)
        => (List<string>)Call("HlsArgs", dir, videoPath, extAudio, audioMap, copyVideo, startSeg, nvenc, mobile);
    public static bool IsHdrTransfer(string t) => (bool)Call("IsHdrTransfer", t);
    public static string SignHlsPlaylist(string m3u8, string d1v) => (string)Call("SignHlsPlaylist", m3u8, d1v);
    public static bool ProbeHdrCached(string path) => (bool)Call("ProbeHdrCached", path);
    public static bool HlsHdrCacheHas(string path) => ((IDictionary)F("_hlsHdrByPath").GetValue(null)).Contains(path);

    // ── idle-kill HLS-сессий (зритель ушёл → глушим ffmpeg) ───────────────
    internal static readonly Type HlsSessT = C.GetNestedType("HlsSession", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+HlsSession not found");

    /// <summary>Посадить фиктивную HLS-сессию (без реального ffmpeg) с меткой активности N секунд назад.</summary>
    public static object HlsSessionSeed(string key, int startSeg, int touchAgeSec)
    {
        var s = Activator.CreateInstance(HlsSessT);
        HlsSessT.GetField("startSeg").SetValue(s, startSeg);
        ((IDictionary)F("_hlsRunning").GetValue(null))[key] = s;
        ((IDictionary)F("_hlsTouch").GetValue(null))[key] = DateTime.UtcNow.AddSeconds(-touchAgeSec);
        return s;
    }
    public static bool HlsRunningHas(string key) => ((IDictionary)F("_hlsRunning").GetValue(null)).Contains(key);
    public static void HlsRunningRemove(string key) => ((IDictionary)F("_hlsRunning").GetValue(null)).Remove(key);
    public static bool HlsSessionKilled(object sess) => (bool)HlsSessT.GetField("killed").GetValue(sess);
    public static bool HlsFailedHas(string key) => ((IDictionary)F("_hlsFailed").GetValue(null)).Contains(key);
    public static void KillIdleHls() => Call("KillIdleHls");
    public static List<string> Mp4Args(string src, string part, bool copyVideo = false, bool nvenc = false)
        => (List<string>)Call("Mp4Args", src, part, copyVideo, nvenc);
    public static double TcOverallProgress(long doneBytes, long totalBytes, long curSize, double curFileProgress)
        => (double)Call("TcOverallProgress", doneBytes, totalBytes, curSize, curFileProgress);
    public static string SafeFileBase(string fileName) => (string)Call("SafeFileBase", fileName);

    // нормализатор локального маркера (LocalFiles возвращает приватный LocalFile → раскладываем рефлексией)
    public static List<(int index, string name, string path, long size)> LocalFilesOf(JObject loc)
    {
        var raw = (IEnumerable)Call("LocalFiles", loc);
        var t = typeof(QbitController).GetNestedType("LocalFile", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QbitController+LocalFile not found");
        var res = new List<(int, string, string, long)>();
        foreach (var item in raw)
            res.Add(((int)t.GetField("index").GetValue(item), (string)t.GetField("name").GetValue(item),
                     (string)t.GetField("path").GetValue(item), (long)t.GetField("size").GetValue(item)));
        return res;
    }
    public static bool LocalIsOverlay(JObject loc) => (bool)Call("LocalIsOverlay", loc);
    public static string BuildVodPlaylist(double duration) => (string)Call("BuildVodPlaylist", duration);
    public static bool SegReady(string dir, int n) => (bool)Call("SegReady", dir, n);
    public static JArray LoadWatch() => (JArray)Call("LoadWatch");
    public static void SaveWatch(JArray a) => Call("SaveWatch", a);
    public static void CleanupTranscodeParts() => QbitController.CleanupTranscodeParts();   // public — напрямую

    // у EnqueueTranscode ДВЕ перегрузки по 5 параметров (фильм/сериал) — выбираем по типу 2-го
    static MethodInfo EnqM(Type second) => C.GetMethods(SF)
        .First(m => m.Name == "EnqueueTranscode" && m.GetParameters().Length == 5 && m.GetParameters()[1].ParameterType == second);
    public static int EnqueueTranscode(string hash, string src, string part, string final, double duration)
    {
        try { return (int)EnqM(typeof(string)).Invoke(null, new object[] { hash, src, part, final, duration }); }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
    }
    public static int EnqueueTranscodeSeries(string hash, bool finalize, string name, string dir, IList files)
    {
        try { return (int)EnqM(typeof(bool)).Invoke(null, new object[] { hash, finalize, name, dir, files }); }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
    }
    public static int QueuePosition(string hash) => (int)Call("QueuePosition", hash);
    public static void KickWorker() => Call("KickWorker");

    // ── сериальная очередь (TcFile/TcQueueItem — приватные вложенные типы) ──
    internal static readonly Type TcFileT = C.GetNestedType("TcFile", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+TcFile not found");

    public static object MakeTcFile(int index, string src, string part, string final, long size)
    {
        var f = Activator.CreateInstance(TcFileT);
        TcFileT.GetField("index").SetValue(f, index);
        TcFileT.GetField("src").SetValue(f, src);
        TcFileT.GetField("part").SetValue(f, part);
        TcFileT.GetField("final").SetValue(f, final);
        TcFileT.GetField("size").SetValue(f, size);
        return f;
    }

    public static IList MakeTcFileList(params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(TcFileT));
        foreach (var it in items) list.Add(it);
        return list;
    }

    /// <summary>Заблокировать/разблокировать воркер очереди (детерминированные тесты без запуска ffmpeg).</summary>
    public static void TcWorkerSet(int v) => F("_tcWorker").SetValue(null, v);

    public static void TcQueueClear()
    {
        var q = F("_tcQueue").GetValue(null);
        var tryDeq = q.GetType().GetMethod("TryDequeue");
        var args = new object[1];
        while ((bool)tryDeq.Invoke(q, args)) { }
        F("_tcCurrent").SetValue(null, null);
    }

    /// <summary>Снимок очереди: (hash, finalize, индексы файлов) по каждому элементу.</summary>
    public static List<(string hash, bool finalize, List<int> indexes)> TcQueueSnapshot()
    {
        var q = F("_tcQueue").GetValue(null);
        var arr = (Array)q.GetType().GetMethod("ToArray").Invoke(q, null);
        var itemT = C.GetNestedType("TcQueueItem", BindingFlags.NonPublic);
        var res = new List<(string, bool, List<int>)>();
        foreach (var it in arr)
        {
            var files = (IList)itemT.GetField("files").GetValue(it);
            var idx = new List<int>();
            if (files != null) foreach (var f in files) idx.Add((int)TcFileT.GetField("index").GetValue(f));
            res.Add(((string)itemT.GetField("hash").GetValue(it), (bool)itemT.GetField("finalize").GetValue(it), idx));
        }
        return res;
    }

    /// <summary>Job из приватного _tcJobs (null если нет).</summary>
    public static TcJobView TcJob(string hash)
    {
        var dict = (IDictionary)F("_tcJobs").GetValue(null);
        var raw = dict[hash];
        return raw == null ? null : new TcJobView(raw);
    }

    /// <summary>Посадить job с нужным state в _tcJobs (для детерминированных дедуп-тестов).</summary>
    public static void TcJobSet(string hash, string state)
    {
        var t = TcJobView.T;
        var job = Activator.CreateInstance(t);
        t.GetField("state").SetValue(job, state);
        ((IDictionary)F("_tcJobs").GetValue(null))[hash] = job;
    }

    /// <summary>Положить элемент прямо в _tcQueue БЕЗ KickWorker (для тестов QueuePosition).</summary>
    public static void TcEnqueueRaw(string hash)
    {
        var t = C.GetNestedType("TcQueueItem", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QbitController+TcQueueItem not found");
        var it = Activator.CreateInstance(t);
        t.GetField("hash").SetValue(it, hash);
        var q = F("_tcQueue").GetValue(null);
        q.GetType().GetMethod("Enqueue").Invoke(q, new[] { it });
    }

    /// <summary>Очередь пуста и воркер не крутится.</summary>
    public static bool TcQueueIdle()
    {
        var q = F("_tcQueue").GetValue(null);
        bool empty = (bool)q.GetType().GetProperty("IsEmpty").GetValue(q);
        int worker = (int)F("_tcWorker").GetValue(null);
        return empty && worker == 0;
    }

    // ── HLS cleanup throttle / START-уведомления / фильтр OTA-хостов ──────
    public static void CleanupHlsThrottled(int minIntervalSec) => Call("CleanupHlsThrottled", minIntervalSec);

    /// <summary>Метка «когда последний раз чистили» (Ticks; 0 — ещё ни разу).</summary>
    public static long HlsCleanupAt
    {
        get => (long)F("_hlsCleanupAt").GetValue(null);
        set => F("_hlsCleanupAt").SetValue(null, value);
    }

    public static string StartKey(string hash, string ep) => (string)Call("StartKey", hash, ep);
    public static bool IsOurClientHost(string url) => (bool)Call("IsOurClientHost", url);
    public static bool AddStartNotification(int seriesId, string link, string hash, string title, string ep)
        => (bool)Call("AddStartNotification", seriesId, link, hash, title, ep);
    public static void PushNotiSignal(int count) => Call("PushNotiSignal", count);
}

/// <summary>Reflection view над приватным QbitController+TcJob (state/progress/error).</summary>
public sealed class TcJobView
{
    internal static readonly Type T = typeof(QbitController).GetNestedType("TcJob", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+TcJob not found");
    public readonly object Raw;
    public TcJobView(object raw) { Raw = raw; }

    public string state => (string)T.GetField("state").GetValue(Raw);
    public double progress => (double)T.GetField("progress").GetValue(Raw);
    public string error => (string)T.GetField("error").GetValue(Raw);
    public string file => (string)T.GetField("file").GetValue(Raw);
    public int fileDone => (int)T.GetField("fileDone").GetValue(Raw);
    public int filesTotal => (int)T.GetField("filesTotal").GetValue(Raw);
}

/// <summary>Reflection view over the private nested <c>QbitController+Ep</c> struct-like class.</summary>
public sealed class EpView
{
    static readonly Type T = Access.EpT;
    public readonly object Raw;
    public EpView(object raw) { Raw = raw; }

    public string kind => (string)T.GetField("kind").GetValue(Raw);
    public int season => (int)T.GetField("season").GetValue(Raw);
    public int ep => (int)T.GetField("ep").GetValue(Raw);
    public int ep2 => (int)T.GetField("ep2").GetValue(Raw);
    public bool any => (bool)T.GetProperty("any").GetValue(Raw);

    public override string ToString() => $"Ep(kind={kind ?? "null"}, s={season}, e={ep}, e2={ep2}, any={any})";
}
