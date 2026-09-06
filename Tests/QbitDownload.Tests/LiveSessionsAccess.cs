using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using QbitDownload;

namespace QbitDownload.Tests;

/// <summary>
/// Reflection-шлюз к части Live.cs, добавленной в qdl 2.113 (склеенные сессии upload-камер):
/// чистые хелперы окна/режима дня и приватные статические кэши, которые сбрасывает LiveForgetRec.
/// </summary>
public static class LiveSessions
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    static readonly Type CamT = C.GetNestedType("LiveCam", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+LiveCam not found");
    static readonly Type BuildT = C.GetNestedType("LiveDayBuild", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+LiveDayBuild not found");
    static readonly Type RecT = LiveAccess.RecView.RawType;
    static readonly Type RecListT = typeof(List<>).MakeGenericType(RecT);

    static object Field(string name) => (C.GetField(name, SF) ?? throw new MissingFieldException("QbitController." + name)).GetValue(null);

    // ── чистые хелперы ────────────────────────────────────────────────────

    public static LiveAccess.RecView Rec(int id, string startUtc, int seconds, string preset = "original", int camera = 6)
        => LiveAccess.ParseLiveRec(new JObject
        {
            ["id"] = id, ["camera_id"] = camera, ["start_time"] = startUtc,
            ["duration_seconds"] = seconds, ["compression_preset"] = preset, ["trigger_type"] = "continuous"
        });

    public static bool LiveInWindow(LiveAccess.RecView r, DateTime from, DateTime to, bool overlap)
        => (bool)Access.Call("LiveInWindow", r?.Raw, from, to, overlap);

    public static int LiveSecondsIn(LiveAccess.RecView r, DateTime from, DateTime to)
        => (int)Access.Call("LiveSecondsIn", r?.Raw, from, to);

    public static List<DateTime> LiveUtcDates(DateTime from, DateTime to)
        => (List<DateTime>)Access.Call("LiveUtcDates", from, to);

    public static JObject LiveRecJson(LiveAccess.RecView r, DateTime from, DateTime to, TimeZoneInfo tz)
        => (JObject)Access.Call("LiveRecJson", r.Raw, from, to, tz);

    static object Cam(int id, string protocol)
    {
        object cam = Activator.CreateInstance(CamT);
        CamT.GetField("id", IF).SetValue(cam, id);
        CamT.GetField("name", IF).SetValue(cam, "cam " + id);
        CamT.GetField("protocol", IF).SetValue(cam, protocol);
        return cam;
    }

    static object RecList(params LiveAccess.RecView[] recs)
    {
        var list = (IList)Activator.CreateInstance(RecListT);
        foreach (var r in recs ?? Array.Empty<LiveAccess.RecView>())
            list.Add(r.Raw);
        return list;
    }

    /// <summary>Режим дня: "sessions" | "day". protocol null — камера неизвестна (null LiveCam).</summary>
    public static string LiveDayMode(string protocol, params LiveAccess.RecView[] recs)
        => (string)Access.Call("LiveDayMode", protocol == null ? null : Cam(6, protocol), RecList(recs));

    // ── кэши и их сброс ───────────────────────────────────────────────────

    public static void LiveForgetRec(int rec) => Access.Call("LiveForgetRec", rec);

    static void Put(object dict, object key, object value)
        => dict.GetType().GetProperty("Item").SetValue(dict, value, new[] { key });

    static bool Has(object dict, object key)
        => (bool)dict.GetType().GetMethod("ContainsKey").Invoke(dict, new[] { key });

    static object Tuple2(Type t2, object a, object b)
        => Activator.CreateInstance(typeof(ValueTuple<,>).MakeGenericType(typeof(DateTime), t2), a, b);

    public static string ByDateKey(int camera, DateTime utcDate) => (string)Access.Call("LiveByDateKey", camera, utcDate);

    public static void SeedByDate(int camera, DateTime utcDate, params LiveAccess.RecView[] recs)
        => Put(Field("_liveByDateCache"), ByDateKey(camera, utcDate), Tuple2(RecListT, DateTime.UtcNow.AddMinutes(5), RecList(recs)));

    public static bool HasByDate(int camera, DateTime utcDate) => Has(Field("_liveByDateCache"), ByDateKey(camera, utcDate));

    public static void SeedDayBuild(int camera, string dayKey, params int[] recs)
    {
        object build = Activator.CreateInstance(BuildT);
        BuildT.GetField("recs", IF).SetValue(build, new HashSet<int>(recs));
        Put(Field("_liveDayCache"), camera + ":" + dayKey, Tuple2(BuildT, DateTime.UtcNow.AddMinutes(5), build));
    }

    public static bool HasDayBuild(int camera, string dayKey) => Has(Field("_liveDayCache"), camera + ":" + dayKey);

    public static void SeedPts(int rec, long pts) => ((ConcurrentDictionary<int, long?>)Field("_livePtsBase"))[rec] = pts;
    public static bool HasPts(int rec) => ((ConcurrentDictionary<int, long?>)Field("_livePtsBase")).ContainsKey(rec);

    public static void SeedRecCam(int rec, int camera) => ((ConcurrentDictionary<int, int>)Field("_liveRecCam"))[rec] = camera;

    public static void SeedFeed(string key)
        => Put(Field("_liveFeedCache"), key, Tuple2(typeof(JObject), DateTime.Now.AddMinutes(5), new JObject()));
    public static bool HasFeed(string key) => Has(Field("_liveFeedCache"), key);

    public static void ClearAll()
    {
        foreach (string f in new[] { "_liveByDateCache", "_liveDayCache", "_livePtsBase", "_liveRecCam", "_liveFeedCache" })
            Field(f).GetType().GetMethod("Clear").Invoke(Field(f), null);
    }
}
