using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Models.Base;

namespace QbitDownload.Tests;

/// <summary>
/// Reflection-шлюз к половине <c>QbitController</c>, живущей в Live.cs (D1versy Live/Rec) —
/// тот же приём, что в <see cref="Access"/> и <see cref="HunterAccess"/>.
///
/// ⚠️ Историческая справка: до 2026-08-23 Live.cs был ЕДИНСТВЕННЫМ файлом модуля вне сборки
/// тестов — csproj утверждал, что он «тянет за собой контроллер». Это оказалось неверно
/// (Controller.cs линкуется с самого первого тестового коммита), линковка проходит чисто.
/// </summary>
public static class LiveAccess
{
    static readonly Type C = typeof(QbitController);
    const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    // ── чистые статические хелперы ────────────────────────────────────────

    public static string LiveBase() => (string)Access.Call("LiveBase");
    public static TimeZoneInfo LiveTz() => (TimeZoneInfo)Access.Call("LiveTz");

    /// <summary>Наивный UTC регистратора → DateTime kind=Utc.</summary>
    public static bool TryLiveUtc(string s, out DateTime utc)
    {
        object[] args = { s, null };
        bool ok = (bool)Access.Call("TryLiveUtc", args);
        utc = (DateTime)args[1];
        return ok;
    }

    public static DateTime LiveToday(TimeZoneInfo tz) => (DateTime)Access.Call("LiveToday", tz);

    public static bool TryLiveDay(string s, TimeZoneInfo tz, out DateTime day)
    {
        object[] args = { s, tz, null };
        bool ok = (bool)Access.Call("TryLiveDay", args);
        day = (DateTime)args[2];
        return ok;
    }

    public static DateTime LiveToUtc(DateTime local, TimeZoneInfo tz)
        => (DateTime)Access.Call("LiveToUtc", local, tz);

    public static (DateTime from, DateTime to) LiveDayWindow(DateTime day, TimeZoneInfo tz)
        => ((DateTime, DateTime))Access.Call("LiveDayWindow", day, tz);

    public static string LiveTime(DateTime utc, TimeZoneInfo tz) => (string)Access.Call("LiveTime", utc, tz);
    public static string LiveDayLabel(DateTime day, DateTime today) => (string)Access.Call("LiveDayLabel", day, today);
    public static string LiveDayKey(DateTime day) => (string)Access.Call("LiveDayKey", day);
    public static string LiveSegName(string line) => (string)Access.Call("LiveSegName", line);

    // ── регулярки имён сегментов (анти-traversal) ─────────────────────────

    public static Regex SegRx => (Regex)C.GetField("_liveSegRx", SF).GetValue(null);
    public static Regex WatchSegRx => (Regex)C.GetField("_liveWatchSegRx", SF).GetValue(null);

    // ── запись регистратора ───────────────────────────────────────────────

    /// <summary>Вид на приватный вложенный LiveRec.</summary>
    public sealed class RecView
    {
        readonly object _raw;
        internal RecView(object raw) => _raw = raw;

        public bool IsNull => _raw == null;
        static readonly Type T = typeof(QbitController).GetNestedType("LiveRec", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QbitController+LiveRec not found");

        object F(string n) => T.GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(_raw);

        public int Id => (int)F("id");
        public int Camera => (int)F("camera");
        public DateTime StartUtc => (DateTime)F("startUtc");
        public int Seconds => (int)F("seconds");
        public long Size => (long)F("size");
        public string Trigger => (string)F("trigger");
    }

    public static RecView ParseLiveRec(JToken t) => new RecView(Access.Call("ParseLiveRec", t));

    public static RecView ParseLiveRec(string json) => ParseLiveRec(JToken.Parse(json));

    // ── D1versy Live → Detection (qdl 2.95) ───────────────────────────────

    static readonly Type CamT = typeof(QbitController).GetNestedType("LiveCam", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QbitController+LiveCam not found");

    /// <summary>Собрать приватный LiveCam — им проверяется правило видимости плитки.</summary>
    public static object Cam(int id, string name, string protocol, bool isLive)
    {
        object c = Activator.CreateInstance(CamT);
        CamT.GetField("id", IF).SetValue(c, id);
        CamT.GetField("name", IF).SetValue(c, name);
        CamT.GetField("protocol", IF).SetValue(c, protocol);
        CamT.GetField("isLive", IF).SetValue(c, isLive);
        return c;
    }

    public static bool LiveWatchVisible(object cam) => (bool)Access.Call("LiveWatchVisible", cam);

    /// <summary>Вид на приватный вложенный LiveEvt (срабатывание детектора).</summary>
    public sealed class EvtView
    {
        readonly object _raw;
        internal EvtView(object raw) => _raw = raw;

        public bool IsNull => _raw == null;
        static readonly Type T = typeof(QbitController).GetNestedType("LiveEvt", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QbitController+LiveEvt not found");

        object F(string n) => T.GetField(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(_raw);

        public int Id => (int)F("id");
        public int Camera => (int)F("camera");
        public string Kind => (string)F("type");
        public double Confidence => (double)F("confidence");
        public DateTime StartUtc => (DateTime)F("startUtc");
        public int Recording => (int)F("recording");
        public bool HasThumb => (bool)F("hasThumb");
    }

    public static EvtView ParseLiveEvt(JToken t) => new EvtView(Access.Call("ParseLiveEvt", t));

    public static EvtView ParseLiveEvt(string json) => ParseLiveEvt(JToken.Parse(json));

    /// <summary>Уменьшёнка кадра детекции. null = «отдай оригинал».</summary>
    public static byte[] LiveThumbResize(byte[] src, int w) => (byte[])Access.Call("LiveThumbResize", src, w);

    /// <summary>Курсор следующей страницы ленты Detection. kept — id уже отданных событий.</summary>
    public static int LiveDetectCursor(int[] kept, int rawMin)
    {
        Type evtT = typeof(QbitController).GetNestedType("LiveEvt", BindingFlags.NonPublic);
        Type listT = typeof(System.Collections.Generic.List<>).MakeGenericType(evtT);
        object list = Activator.CreateInstance(listT);
        var addM = listT.GetMethod("Add");

        foreach (int id in kept)
        {
            object e = Activator.CreateInstance(evtT);
            evtT.GetField("id", IF).SetValue(e, id);
            addM.Invoke(list, new[] { e });
        }

        return (int)Access.Call("LiveDetectCursor", list, rawMin);
    }

    // ── инстанс-методы: подпись сегментов и гейт прав ─────────────────────

    /// <summary>
    /// Контроллер с подставным HttpContext. Инстанс нужен потому, что подпись сегментных строк
    /// читает предъявленный ключ из Request и айди устройства из requestInfo.
    /// </summary>
    public static QbitController Controller(string query = null, string cookie = null, string uid = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/qdl/live/day/6/2026-08-21/stream.m3u8";
        if (!string.IsNullOrEmpty(query)) ctx.Request.QueryString = new QueryString(query);
        if (!string.IsNullOrEmpty(cookie)) ctx.Request.Headers["Cookie"] = cookie;
        ctx.Features.Set(new RequestModel { IP = "192.168.87.5", user_uid = uid });

        return new QbitController { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    static object CallOn(QbitController c, string name, params object[] args)
    {
        var m = C.GetMethod(name, IF) ?? throw new MissingMethodException("QbitController." + name);
        try { return m.Invoke(c, args); }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
    }

    public static string LiveD1vKey(QbitController c) => (string)CallOn(c, "LiveD1vKey");
    public static string LiveSegQuery(QbitController c) => (string)CallOn(c, "LiveSegQuery");
    public static string LiveSignDay(QbitController c, string playlist) => (string)CallOn(c, "LiveSignDay", playlist);
    public static bool LiveDenied(QbitController c, string feature) => (bool)CallOn(c, "LiveDenied", feature);

    // ── гейт «Управления» (qdl 2.67) ──────────────────────────────────────
    // ManageDenied/ManageCookie объявлены в Perms.cs, но это тот же partial QbitController и те же
    // приватные инстанс-методы — шлюз общий, отдельный заводить незачем.
    public static ActionResult ManageDenied(QbitController c) => (ActionResult)CallOn(c, "ManageDenied");
    // qdl 2.89: ManageCookie удалён вместе с кукой qdl_unlock=1 — ключ теперь только право.
}
