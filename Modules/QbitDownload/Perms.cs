using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared.Models.Base;
using Shared.Models.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// ПРАВА НА СКРЫТЫЕ РАЗДЕЛЫ (qdl 2.54) — «D1versy Live» (эфир) и «D1versy Rec» (записи).
//
// До этого разделы видели ВСЕ: пункты меню рисовал qdl.js без единого гейта, а 13 роутов
// qdl/live/* были [AllowAnonymous]. Единственным «замком» был CSS-лок в телефонной Android-сборке
// (PhoneLock.kt) — сокрытие, а не запрет. Теперь решение принимает СЕРВЕР, по айди устройства.
//
// Айди — тот же канонический lampac_unic_id (он же qdl_device_uid в нативном KV), с которым уже
// ходят Sync/TimeCode/Bookmark/история jut.su. Своё второе понятие «устройства» немедленно
// разъехалось бы с прогрессом просмотра. Клиент шлёт его параметром uid=, сервер читает готовым
// requestInfo.user_uid (разбирает RequestInfo.getuid).
//
// 🔴 ЧЕСТНАЯ ГРАНИЦА. uid приходит от клиента и ничем не подписан (BaseModule.ValidateIdentity по
// умолчанию ВЫКЛЮЧЕН — RequestInfo отдаёт значение query как есть). Кто знает чужой айди, тем и
// представится. Это защита «от нечаянно и от гостей», а не криптография; снаружи поверх неё стоит
// периметр D1Vision с ключом платформы. Но это строго сильнее прежнего состояния, где роуты были
// открыты вообще всем. Криптопривязка (подписанный сервером токен) — отдельная задача.
//
// Хранилище — один файл access.json в cachePath через JsonStore (РАМ источник истины, диск фоном,
// зеркало на E:). Реестр устройств и гранты ВМЕСТЕ, потому что:
//   • грант должен уметь существовать до первого появления устройства;
//   • админке нужна одна строка на устройство, а не джойн двух списков.
// Не таблица в qdl.db сознательно: EnsureCreated() не добавляет таблицы в уже существующую базу,
// пришлось бы писать CREATE TABLE-миграцию руками ради десятков записей.
//
// Формат: { "ver":1, "devices": { "<uid>": { name, platform, client, ua, ip, first, last, grants:[] } } }
// ─────────────────────────────────────────────────────────────────────────────
public static class Perms
{
    // Ключи фич — строки, чтобы новая скрытая вкладка не требовала правки схемы.
    public const string FeatureLive = "live";   // эфир камер (D1versy Live)
    public const string FeatureRec = "rec";     // записи регистратора (D1versy Rec)

    public static readonly string[] Features = { FeatureLive, FeatureRec };

    const int UidMaxLen = 48;
    const int DeviceCap = 200;          // кап реестра; устройства С ГРАНТАМИ не вытесняются никогда
    const int TouchThrottleSec = 60;    // не чаще раза в минуту на uid обновляем last/ip

    // ⚠️ Санация обязательна, а не гигиена: ValidateIdentity выключен, uid приходит из query без
    // единой проверки символов, а мы кладём его ключом в JSON и печатаем в админке.
    // Правила — те же, что у проверенного JutHistoryBucket (JutSuHistory.cs).
    static readonly Regex _uidRx = new Regex(@"[^a-z0-9\-_\.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex _platRx = new Regex(@"d1vision_(mac|ios|android|tizen|windows)/(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly object _lock = new object();
    static readonly ConcurrentDictionary<string, DateTime> _touched = new(StringComparer.OrdinalIgnoreCase);

    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "access.json");

    static bool Enabled => ModInit.conf?.permsEnabled != false;

    #region uid / платформа

    /// <summary>
    /// uid → ключ реестра. Пустой/мусорный → null, то есть «устройство неизвестно» = доступа нет.
    /// (У JutHistoryBucket на этом месте общий бакет _shared — здесь так нельзя: это были бы права
    /// «для всех безымянных».)
    /// </summary>
    public static string NormUid(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;

        string s = _uidRx.Replace(uid, "");
        if (s.Length > UidMaxLen) s = s.Substring(0, UidMaxLen);
        s = s.Trim('.');                       // «.»/«..» после чистки — это путь, а не имя
        if (s.Length == 0) return null;

        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Платформа по UA-токену d1vision_&lt;platform&gt;/&lt;версия&gt;. Логика повторяет клиентскую из
    /// lampainit-invc.js — иначе платформа в админке разошлась бы с window.d1vision_platform.
    /// Tizen токена не шлёт (сеет localStorage в loader.js виджета) — он опознается как web.
    /// </summary>
    public static (string platform, string client) PlatformOf(string ua)
    {
        if (!string.IsNullOrEmpty(ua))
        {
            var m = _platRx.Match(ua);
            if (m.Success)
                return (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);

            // старые бинари без токена слали только lampa_client — там это был android
            if (ua.IndexOf("lampa_client", StringComparison.OrdinalIgnoreCase) >= 0)
                return ("android", "");
        }

        return ("web", "");
    }

    #endregion

    #region чтение / запись реестра

    static JObject Load()
    {
        var root = JsonStore.ReadObject(StorePath);
        if (root == null)
            root = new JObject { ["ver"] = 1, ["devices"] = new JObject() };

        if (root["devices"] is not JObject)
            root["devices"] = new JObject();

        return root;
    }

    /// <summary>
    /// Единственная точка записи: read-modify-write под общим локом. 🔴 Иначе частое обновление
    /// last (реестр) и редкая правка grants (админка) гонялись бы за один и тот же файл, и
    /// последний писатель затирал бы чужое — а терять здесь можно именно выданные права.
    /// </summary>
    static T Mutate<T>(Func<JObject, T> mutator)
    {
        lock (_lock)
        {
            var root = Load();
            var res = mutator(root);
            JsonStore.Write(StorePath, root);
            return res;
        }
    }

    static JObject Device(JObject root, string uid)
    {
        var devices = (JObject)root["devices"];
        if (devices[uid] is JObject d) return d;

        var fresh = new JObject
        {
            ["name"] = "",
            ["platform"] = "",
            ["client"] = "",
            ["ua"] = "",
            ["ip"] = "",
            ["first"] = DateTime.UtcNow,
            ["last"] = DateTime.UtcNow,
            ["grants"] = new JArray()
        };
        devices[uid] = fresh;
        return fresh;
    }

    /// <summary>Вытеснение: только самые старые и только БЕЗ грантов. Устройство с правами вечно.</summary>
    static void Prune(JObject root)
    {
        var devices = (JObject)root["devices"];
        if (devices.Count <= DeviceCap) return;

        var victims = devices.Properties()
            .Where(p => ((p.Value["grants"] as JArray)?.Count ?? 0) == 0)
            .OrderBy(p => (DateTime?)p.Value["last"] ?? DateTime.MinValue)
            .Take(devices.Count - DeviceCap)
            .Select(p => p.Name)
            .ToList();

        foreach (string name in victims)
            devices.Remove(name);
    }

    #endregion

    #region публичное API

    /// <summary>Есть ли у устройства эта фича. Неизвестный/пустой uid → нет. Киллсвитч → да всем.</summary>
    public static bool Allowed(string uid, string feature)
    {
        if (!Enabled) return true;             // permsEnabled:false в init.conf — всё открыто как раньше

        string key = NormUid(uid);
        if (key == null) return false;

        try
        {
            if (Load()["devices"]?[key]?["grants"] is not JArray g) return false;

            foreach (var it in g)
            {
                if (string.Equals((string)it, feature, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] perms allowed: " + ex.Message); }

        return false;
    }

    /// <summary>Набор фич устройства — для ответа клиенту (/qdl/features).</summary>
    public static Dictionary<string, bool> FeaturesOf(string uid)
    {
        var map = new Dictionary<string, bool>();
        foreach (string f in Features)
            map[f] = Allowed(uid, f);
        return map;
    }

    /// <summary>
    /// Отметить устройство живым. force=true — синхронно, мимо троттлинга (заход на /qdl/features:
    /// устройство обязано появиться в админке с первого же запуска, а не через минуту).
    /// </summary>
    public static void Touch(RequestModel req, bool force = false)
    {
        if (req == null) return;

        string uid = NormUid(req.user_uid);
        if (uid == null) return;

        var now = DateTime.UtcNow;
        if (!force && _touched.TryGetValue(uid, out var seen) && (now - seen).TotalSeconds < TouchThrottleSec)
            return;

        _touched[uid] = now;

        var (platform, client) = PlatformOf(req.UserAgent);

        try
        {
            Mutate<object>(root =>
            {
                var d = Device(root, uid);
                d["last"] = now;
                if (!string.IsNullOrEmpty(req.IP)) d["ip"] = req.IP;
                if (!string.IsNullOrEmpty(req.UserAgent)) d["ua"] = req.UserAgent;

                // Платформу не затираем на «web», если раньше видели настоящую: тот же клиент ходит
                // и без UA-токена (нативный плеер VLC шлёт свой User-Agent), и потеря платформы
                // сделала бы список в админке нечитаемым ровно в момент просмотра.
                if (platform != "web" || string.IsNullOrEmpty((string)d["platform"]))
                    d["platform"] = platform;
                if (!string.IsNullOrEmpty(client)) d["client"] = client;

                Prune(root);
                return null;
            });
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] perms touch: " + ex.Message); }
    }

    /// <summary>Список устройств для админки (свежие сверху).</summary>
    public static JArray List()
    {
        var arr = new JArray();
        try
        {
            if (Load()["devices"] is not JObject devices) return arr;

            foreach (var p in devices.Properties().OrderByDescending(p => (DateTime?)p.Value["last"] ?? DateTime.MinValue))
            {
                var v = p.Value;
                arr.Add(new JObject
                {
                    ["uid"] = p.Name,
                    ["name"] = (string)v["name"] ?? "",
                    ["platform"] = (string)v["platform"] ?? "",
                    ["client"] = (string)v["client"] ?? "",
                    ["ip"] = (string)v["ip"] ?? "",
                    ["first"] = v["first"],
                    ["last"] = v["last"],
                    ["grants"] = (v["grants"] as JArray)?.DeepClone() ?? new JArray()
                });
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] perms list: " + ex.Message); }

        return arr;
    }

    /// <summary>Выдать/отобрать фичу. Неизвестный uid заводится — грант может опережать устройство.</summary>
    public static bool Grant(string uid, string feature, bool on)
    {
        string key = NormUid(uid);
        if (key == null) return false;
        if (!Features.Contains(feature, StringComparer.OrdinalIgnoreCase)) return false;

        return Mutate(root =>
        {
            var d = Device(root, key);
            var g = d["grants"] as JArray ?? new JArray();

            var kept = new JArray(g.Where(x => !string.Equals((string)x, feature, StringComparison.OrdinalIgnoreCase)));
            if (on) kept.Add(feature.ToLowerInvariant());

            d["grants"] = kept;
            return true;
        });
    }

    public static bool Rename(string uid, string name)
    {
        string key = NormUid(uid);
        if (key == null) return false;

        string clean = (name ?? "").Trim();
        if (clean.Length > 64) clean = clean.Substring(0, 64);

        return Mutate(root =>
        {
            Device(root, key)["name"] = clean;
            return true;
        });
    }

    public static bool Forget(string uid)
    {
        string key = NormUid(uid);
        if (key == null) return false;

        _touched.TryRemove(key, out _);

        return Mutate(root =>
        {
            ((JObject)root["devices"]).Remove(key);
            return true;
        });
    }

    /// <summary>Имя+платформа для футера «Уведомлений» (клиент показывает их пользователю).</summary>
    public static JObject Card(string uid)
    {
        string key = NormUid(uid);
        var o = new JObject { ["uid"] = key ?? "", ["name"] = "", ["platform"] = "", ["client"] = "" };
        if (key == null) return o;

        try
        {
            if (Load()["devices"]?[key] is JObject d)
            {
                o["name"] = (string)d["name"] ?? "";
                o["platform"] = (string)d["platform"] ?? "";
                o["client"] = (string)d["client"] ?? "";
            }
        }
        catch { }

        return o;
    }

    #endregion

    #region пассивный реестр (хук пайплайна)

    /// <summary>
    /// Забыть троттлинг «когда видели». Зовётся при смене cachePath: реестр там новый (пустой),
    /// а память о недавних uid относилась к прежнему хранилищу — без сброса устройство не
    /// зарегистрировалось бы в новом файле целую минуту.
    /// </summary>
    public static void ResetForConfigReload() => _touched.Clear();

    public static void Attach() => EventListener.Middleware += OnRequest;

    public static void Detach() => EventListener.Middleware -= OnRequest;

    // Наблюдатель: только учёт, всегда true (запрос не трогаем). Держать ДЁШЕВО — зовётся на всё.
    // ⚠️ Подписка стоит в пайплайне ПОСЛЕ UseStaticache, то есть HIT-ы сюда не доходят. Нам хватает:
    // /qdl/features, /qdl/list и /qdl/notifications не кэшируются, устройство засветится на них.
    public static bool OnRequest(bool first, EventMiddleware e)
    {
        try
        {
            if (!first) return true;

            var ctx = e.httpContext;
            if (ctx == null) return true;

            string path = ctx.Request.Path.Value;
            if (path == null || !path.StartsWith("/qdl", StringComparison.OrdinalIgnoreCase)) return true;

            var req = ctx.Features.Get<RequestModel>();
            if (req == null || req.IsLocalRequest) return true;   // межмодульные вызовы — не устройства

            Touch(req);
        }
        catch { }
        return true;
    }

    #endregion
}

public partial class QbitController
{
    /// <summary>
    /// Права текущего устройства. Клиент зовёт на старте и по ним рисует пункты меню.
    /// 🔴 Никакого [Staticache]: его ключ — Scheme+Host+Path+Query, без cookie и UA, и закэшированный
    /// ответ одного устройства уехал бы всем остальным.
    /// </summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/features")]
    public ActionResult QdlFeatures()
    {
        Perms.Touch(requestInfo, force: true);

        var card = Perms.Card(requestInfo?.user_uid);
        card["features"] = JObject.FromObject(Perms.FeaturesOf(requestInfo?.user_uid));

        SetHeadersNoCache();
        return ContentTo(card.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }
}
