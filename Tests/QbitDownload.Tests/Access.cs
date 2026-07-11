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

    // ── purge / transcode queue (§Z) ──────────────────────────────────────
    public static void PurgeCache(string hash) => Call("PurgeCache", hash);
    public static void CleanupTranscodeParts() => QbitController.CleanupTranscodeParts();   // public — напрямую
    public static int EnqueueTranscode(string hash, string src, string part, string final, double duration)
        => (int)Call("EnqueueTranscode", hash, src, part, final, duration);
    public static int QueuePosition(string hash) => (int)Call("QueuePosition", hash);
    public static void KickWorker() => Call("KickWorker");

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
