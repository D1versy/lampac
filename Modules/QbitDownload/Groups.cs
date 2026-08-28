using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Shared.Models.Base;
using Shared.Models.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// ГРУППЫ УСТРОЙСТВ — ОБЩАЯ ИСТОРИЯ ПРОСМОТРОВ (qdl 2.81).
//
// Задача владельца: связать несколько устройств так, чтобы у них была ОДНА история — что
// смотрели и где остановились, — и уметь развязать обратно. Управление только из админки
// /admin/d1v, клиентского интерфейса пока нет (задел на будущий UI — /qdl/features отдаёт группу).
//
// ПОЧЕМУ ПОДМЕНА КЛЮЧА, А НЕ РЕПЛИКАЦИЯ ЗАПИСЕЙ. Вся история уже лежит на сервере и ключуется
// ОДНОЙ строкой — requestInfo.user_uid (клиент шлёт её как uid=, это lampac_unic_id устройства):
//
//   database/Sync.sql      bookmarks(user, data)        «История просмотров» + все списки Избранного
//   database/TimeCode.sql  timecodes(user, card, item)  позиции просмотра и отметки «просмотрено»
//   <jutDataDir>/history/<uid>.json                     лента «недавнее» раздела jut.su
//
// Значит группа — это «подставить вместо айди устройства айди группы» на входе в контроллеры.
// Никаких вторых копий, никакой фоновой синхронизации, ни одной правки клиента: фича приезжает
// рестартом контейнера. Раскладка «каждому по копии + фоновый мерж» дала бы N-кратную запись,
// лаг, дрейф и те же самые конфликты — только позже и в трёх местах сразу.
//
// ТОЧКА ВРЕЗКИ — EventListener.MiddlewareAsync при first:false (Core/Startup.cs, UseModuleAsync).
// Это ПОСЛЕ UseAuthorization/UseAccsdb и ДО MapControllers: проверки доступа видят настоящее
// устройство, а контроллеры — группу. Тот же приём, что у Perms.Attach (там синхронный хук на
// first:true и только для /qdl*) — пересечения нет.
//
// 🔴 КРАСНЫЕ ЛИНИИ:
//
//  1. БЕЛЫЙ СПИСОК ПУТЕЙ, а не «подменяем везде». Список ниже (_syncPaths) — исчерпывающий.
//     Всё, что вне его, видит настоящий айди устройства.
//  2. ГРУППА НЕ ВЛИЯЕТ НА ПРАВА. /qdl/* под подмену не попадает никогда: реестр устройств
//     (Perms.Touch) и гранты live/rec/manage остаются на УСТРОЙСТВО. Иначе одна галочка
//     «объединить историю» тихо раздала бы эфир камер всей группе.
//  3. /storage/* ОСТАВЛЕН ЛИЧНЫМ. Блоб syncview пишется «весь документ целиком, последний
//     писатель затирает» — общий блоб был бы единственным местом, где три устройства реально
//     теряли бы данные друг друга. Замер на боевом (28.08.2026): 71 блоб, все по 114 байт и
//     без поля file_view, то есть хранилище фактически пустое. Цена решения названа вслух:
//     WS-событие sync от /storage/set уходит на айди устройства и до группы не доезжает.
//  4. ОДНО УСТРОЙСТВО — МАКСИМУМ ОДНА ГРУППА. Иначе резолв стал бы неоднозначным.
//  5. ТЕСТОВЫЕ АЙДИ (d1v-test-…) В ГРУППЫ НЕ ПРИНИМАЮТСЯ. Уборка песочницы (TestSandbox.cs)
//     УДАЛЯЕТ строки по айди; пусти мы стенд в группу — она получила бы право снести общую
//     историю владельца.
//
// Хранилище — groups.json в cachePath через JsonStore, ровно как access.json у Perms:
//   { "ver":1, "groups": { "g-a1b2c3d4": { name, created, members:[uid,…] } } }
// Единственная точка записи — Mutate() под общим локом.
// ─────────────────────────────────────────────────────────────────────────────
public static class Groups
{
    public const string GidPrefix = "g-";

    const int NameMaxLen = 64;
    const int GroupCap = 32;      // групп в реестре
    const int MemberCap = 24;     // устройств в одной группе

    // 🔴 Исчерпывающий белый список. Сравнение точное (не по префиксу): «/timecode/js/{token}»
    // и «/bookmark.js» — это отдача плагинов, им подмена не нужна и не должна доставаться.
    static readonly HashSet<string> _syncPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/reqinfo",
        "/bookmark/list", "/bookmark/set", "/bookmark/add", "/bookmark/added", "/bookmark/remove",
        "/timecode/all", "/timecode/add"
    };

    static readonly object _lock = new object();

    // Резолв зовётся на каждый запрос синка — держим готовый снимок uid → gid и меняем его
    // целиком при мутации. Volatile-поле вместо чтения JsonStore на горячем пути.
    static volatile Dictionary<string, string> _index;

    static string StorePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "groups.json");

    static bool Enabled => ModInit.conf?.groupsEnabled != false;

    #region хранилище и индекс

    static JObject Load()
    {
        var root = JsonStore.ReadObject(StorePath);
        if (root == null)
            root = new JObject { ["ver"] = 1, ["groups"] = new JObject() };

        if (root["groups"] is not JObject)
            root["groups"] = new JObject();

        return root;
    }

    /// <summary>
    /// Единственная точка записи: read-modify-write под общим локом плюс пересборка индекса.
    /// 🔴 Индекс обязан меняться В ТОМ ЖЕ локе: иначе между записью файла и пересборкой
    /// проскочил бы запрос, который резолвится по старой карте, и его история легла бы в
    /// чужой бакет — то есть ровно та потеря, ради предотвращения которой всё и делается.
    /// </summary>
    static T Mutate<T>(Func<JObject, T> mutator)
    {
        lock (_lock)
        {
            var root = Load();
            var res = mutator(root);
            JsonStore.Write(StorePath, root);
            _index = BuildIndex(root);
            return res;
        }
    }

    static Dictionary<string, string> BuildIndex(JObject root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root["groups"] is not JObject groups) return map;

        foreach (var p in groups.Properties())
        {
            if (p.Value?["members"] is not JArray members) continue;

            foreach (var m in members)
            {
                string uid = Perms.NormUid((string)m);
                // Первая победившая связка выигрывает: инвариант «одно устройство — одна группа»
                // держит Join, но битый вручную файл не должен ронять резолв.
                if (uid != null && !map.ContainsKey(uid))
                    map[uid] = p.Name;
            }
        }

        return map;
    }

    static Dictionary<string, string> Index()
    {
        var idx = _index;
        if (idx != null) return idx;

        lock (_lock)
        {
            if (_index == null)
            {
                try { _index = BuildIndex(Load()); }
                catch (Exception ex)
                {
                    Console.WriteLine("[QbitDownload] groups index: " + ex.Message);
                    _index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            return _index;
        }
    }

    /// <summary>Забыть индекс. Зовётся при смене cachePath: реестр там другой (пара к Perms.ResetForConfigReload).</summary>
    public static void ResetForConfigReload() => _index = null;

    #endregion

    #region резолв

    /// <summary>
    /// Айди устройства → айди его группы. Не в группе, киллсвитч, пустой или мусорный uid —
    /// возвращается ВХОД БЕЗ ИЗМЕНЕНИЙ (а не null): вызывающий должен уметь звать это на любом
    /// значении и получать «как было».
    /// </summary>
    public static string Resolve(string uid)
    {
        if (!Enabled) return uid;

        string key = Perms.NormUid(uid);
        if (key == null) return uid;
        if (key.StartsWith(GidPrefix, StringComparison.Ordinal)) return uid;   // уже группа

        try { return Index().TryGetValue(key, out string gid) ? gid : uid; }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups resolve: " + ex.Message); return uid; }
    }

    /// <summary>Айди группы устройства или null. Для админки и /qdl/features.</summary>
    public static string GroupOf(string uid)
    {
        string key = Perms.NormUid(uid);
        if (key == null || key.StartsWith(GidPrefix, StringComparison.Ordinal)) return null;

        try { return Index().TryGetValue(key, out string gid) ? gid : null; }
        catch { return null; }
    }

    public static bool IsGrouped(string uid) => GroupOf(uid) != null;

    /// <summary>Имя группы (для колонки в админке и футера уведомлений). Пусто — если не в группе.</summary>
    public static string NameOf(string gid)
    {
        if (string.IsNullOrEmpty(gid)) return "";
        try { return (string)Load()["groups"]?[gid]?["name"] ?? ""; }
        catch { return ""; }
    }

    public static List<string> MembersOf(string gid)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(gid)) return list;

        try
        {
            if (Load()["groups"]?[gid]?["members"] is JArray arr)
            {
                foreach (var m in arr)
                {
                    string uid = Perms.NormUid((string)m);
                    if (uid != null && !list.Contains(uid)) list.Add(uid);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups members: " + ex.Message); }

        return list;
    }

    #endregion

    #region админское API реестра

    /// <summary>Айди новой группы: g- + 8 hex. Префикс не может столкнуться с айди устройства —
    /// те генерятся как «d» + 7 символов (lampainit-invc.js) и дефиса не содержат.</summary>
    static string NewGid(JObject groups)
    {
        for (int i = 0; i < 32; i++)
        {
            string gid = GidPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (groups[gid] == null) return gid;
        }
        return null;
    }

    static string CleanName(string name)
    {
        string s = (name ?? "").Trim();
        if (s.Length > NameMaxLen) s = s.Substring(0, NameMaxLen);
        return s;
    }

    /// <summary>Создать пустую группу. Возвращает её айди или null.</summary>
    public static string Create(string name)
    {
        return Mutate(root =>
        {
            var groups = (JObject)root["groups"];
            if (groups.Count >= GroupCap) return null;

            string gid = NewGid(groups);
            if (gid == null) return null;

            groups[gid] = new JObject
            {
                ["name"] = CleanName(name),
                ["created"] = DateTime.UtcNow,
                ["members"] = new JArray()
            };

            return gid;
        });
    }

    public static bool Rename(string gid, string name)
    {
        if (string.IsNullOrEmpty(gid)) return false;

        return Mutate(root =>
        {
            if (root["groups"]?[gid] is not JObject g) return false;
            g["name"] = CleanName(name);
            return true;
        });
    }

    public static bool Exists(string gid)
    {
        if (string.IsNullOrEmpty(gid)) return false;
        try { return Load()["groups"]?[gid] is JObject; }
        catch { return false; }
    }

    /// <summary>
    /// Почему устройству нельзя в группу. null = можно. Отдельным методом, потому что причину
    /// показывает админка, и она же проверяется вторым замком внутри Join.
    /// </summary>
    public static string JoinDenied(string gid, string uid)
    {
        if (!Enabled) return "группы выключены (groupsEnabled: false)";

        string key = Perms.NormUid(uid);
        if (key == null) return "пустой или мусорный айди устройства";
        if (key.StartsWith(GidPrefix, StringComparison.Ordinal)) return "это айди группы, а не устройства";

        // 🔴 Красная линия 5: стенд e2e умеет УДАЛЯТЬ свои следы по айди (TestSandbox.cs).
        // В группе этот айди означал бы общую историю владельца.
        if (Perms.SandboxEnabled && Perms.IsTestUid(key)) return "тестовое устройство стенда в группы не принимается";

        if (!Exists(gid)) return "группы нет: " + gid;

        string cur = GroupOf(key);
        if (cur == gid) return "устройство уже в этой группе";
        if (cur != null) return "устройство уже в другой группе — сначала отвяжи";

        if (MembersOf(gid).Count >= MemberCap) return "в группе уже " + MemberCap + " устройств";

        return null;
    }

    /// <summary>
    /// Связать устройство с группой. Данные устройства СНАЧАЛА сливаются в группу и только
    /// потом появляется членство: иначе между флипом и переносом проскочил бы запрос, и
    /// устройство увидело бы пустую историю (а то и записало бы в неё нули).
    /// </summary>
    public static JObject Join(string gid, string uid, bool apply)
    {
        var report = new JObject { ["op"] = "join", ["apply"] = apply, ["gid"] = gid, ["uid"] = Perms.NormUid(uid) ?? "" };

        string deny = JoinDenied(gid, uid);
        if (deny != null) { report["error"] = deny; return report; }

        string key = Perms.NormUid(uid);

        var moved = QbitController.GroupsMergeHistory(key, gid, apply, report);
        if (report["error"] != null) return report;

        if (!apply) return report;

        bool ok = Mutate(root =>
        {
            if (root["groups"]?[gid] is not JObject g) return false;

            // Второй замок: список мог собираться раньше, реестр — измениться.
            foreach (var p in ((JObject)root["groups"]).Properties())
            {
                if (p.Value?["members"] is JArray other && other.Any(x => string.Equals((string)x, key, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            var members = g["members"] as JArray ?? new JArray();
            members.Add(key);
            g["members"] = members;
            return true;
        });

        report["linked"] = ok;
        if (!ok) report["error"] = "связать не вышло — устройство уже в группе";
        else Console.WriteLine("[QbitDownload] groups join: " + key + " → " + gid + " " + moved);

        return report;
    }

    #endregion

    #region разрыв связи

    /// <summary>
    /// Отвязать устройство. keepCopy=true (решение владельца по умолчанию) — общая история
    /// сначала КОПИРУЕТСЯ в личную строку устройства, и только потом снимается членство:
    /// иначе всё, что посмотрели за время в группе, для устройства просто исчезло бы.
    /// Личная строка при этом никогда не удалялась — она просто не читалась, пока шла группа.
    /// </summary>
    public static JObject Leave(string uid, bool keepCopy, bool apply)
    {
        string key = Perms.NormUid(uid);
        var report = new JObject { ["op"] = "leave", ["apply"] = apply, ["uid"] = key ?? "", ["keepCopy"] = keepCopy };

        if (key == null) { report["error"] = "пустой или мусорный айди устройства"; return report; }

        string gid = GroupOf(key);
        if (gid == null) { report["error"] = "устройство не в группе"; return report; }
        report["gid"] = gid;

        if (keepCopy)
        {
            QbitController.GroupsMergeHistory(gid, key, apply, report);
            if (report["error"] != null) return report;
        }

        if (!apply) return report;

        bool ok = Mutate(root =>
        {
            if (root["groups"]?[gid] is not JObject g) return false;
            if (g["members"] is not JArray members) return false;

            g["members"] = new JArray(members.Where(x => !string.Equals((string)x, key, StringComparison.OrdinalIgnoreCase)));
            return true;
        });

        report["unlinked"] = ok;
        Console.WriteLine("[QbitDownload] groups leave: " + key + " ← " + gid + (keepCopy ? " (с копией истории)" : ""));
        return report;
    }

    /// <summary>Расформировать группу: каждому участнику по копии (если просили) и снести запись.</summary>
    public static JObject Dissolve(string gid, bool keepCopy, bool apply)
    {
        var report = new JObject { ["op"] = "dissolve", ["apply"] = apply, ["gid"] = gid, ["keepCopy"] = keepCopy };

        // Группы уже нет — но её ДАННЫЕ могли остаться (расформировали без копии, а потом
        // передумали). Уборка тут единственно возможная работа, и она безопасна по построению:
        // GroupsPurge физически не принимает ничего, кроме ключа g-… несуществующей группы.
        if (!Exists(gid))
        {
            if (!gid.StartsWith(GidPrefix, StringComparison.Ordinal))
            {
                report["error"] = "это не айди группы: " + gid;
                return report;
            }

            report["note"] = "группы уже не было — убираем только её данные";
            report["members"] = 0;
            if (apply)
            {
                string tail = QbitController.GroupsPurge(gid);
                if (tail != null) report["purged"] = tail;
            }
            return report;
        }

        var members = MembersOf(gid);
        report["members"] = members.Count;

        var per = new JArray();
        bool everyoneGotACopy = keepCopy;
        foreach (string uid in members)
        {
            var one = Leave(uid, keepCopy, apply);
            if (one["error"] != null || (one["errors"] as JArray)?.Count > 0) everyoneGotACopy = false;
            per.Add(one);
        }
        report["devices"] = per;

        if (!apply) return report;

        Mutate(root => { ((JObject)root["groups"]).Remove(gid); return true; });
        Console.WriteLine("[QbitDownload] groups dissolve: " + gid);

        // 🔴 Убираем данные группы ТОЛЬКО когда копия уехала каждому участнику без ошибок —
        // иначе это была бы единственная копия истории. Правила и замки — GroupsHistory.cs.
        if (everyoneGotACopy)
        {
            string purged = QbitController.GroupsPurge(gid);
            if (purged != null) report["purged"] = purged;
        }

        return report;
    }

    /// <summary>
    /// Снять устройство со всех групп молча, без переноса истории. Зовётся из Perms.Forget:
    /// «забыли» устройство в админке — ссылка на него в groups.json обязана уйти вместе с ним,
    /// иначе в реестре осталась бы связка в никуда.
    /// </summary>
    public static void ForgetDevice(string uid)
    {
        string key = Perms.NormUid(uid);
        if (key == null) return;

        try
        {
            if (GroupOf(key) == null) return;

            Mutate<object>(root =>
            {
                foreach (var p in ((JObject)root["groups"]).Properties())
                {
                    if (p.Value?["members"] is JArray members)
                        p.Value["members"] = new JArray(members.Where(x => !string.Equals((string)x, key, StringComparison.OrdinalIgnoreCase)));
                }
                return null;
            });
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups forget: " + ex.Message); }
    }

    #endregion

    #region список для админки

    /// <summary>Группы со счётчиками и участниками. Имена устройств админка подставляет сама из Perms.List().</summary>
    public static JArray List()
    {
        var arr = new JArray();
        try
        {
            if (Load()["groups"] is not JObject groups) return arr;

            foreach (var p in groups.Properties().OrderBy(p => (string)p.Value["name"] ?? ""))
            {
                arr.Add(new JObject
                {
                    ["gid"] = p.Name,
                    ["name"] = (string)p.Value["name"] ?? "",
                    ["created"] = p.Value["created"],
                    ["members"] = new JArray(MembersOf(p.Name)),
                    ["stats"] = QbitController.GroupsStats(p.Name)
                });
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups list: " + ex.Message); }

        return arr;
    }

    #endregion

    #region перенос состава на реплику

    /// <summary>Снимок реестра групп — уезжает на реплику вместе с историей (ReplicaHistory).</summary>
    internal static JObject Snapshot()
    {
        try { return Load(); }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups snapshot: " + ex.Message); return null; }
    }

    /// <summary>
    /// Применить домашний снимок у себя. Без этого на реплике устройство группы резолвилось бы
    /// в себя и увидело бы пустую историю — при том, что СТРОКИ группы туда уже доезжают
    /// (ReplicaHistory переносит все user без фильтра).
    /// Поток односторонний: реплика свой состав не редактирует, поэтому замена целиком корректна.
    /// Возврат: true — состав изменился (для лога тика).
    /// </summary>
    internal static bool ApplySnapshot(JObject snapshot)
    {
        if (snapshot?["groups"] is not JObject) return false;

        try
        {
            lock (_lock)
            {
                var fresh = new JObject { ["ver"] = 1, ["groups"] = snapshot["groups"].DeepClone() };
                if (JToken.DeepEquals(fresh, Load())) return false;      // идемпотентность тика

                JsonStore.Write(StorePath, fresh);
                _index = BuildIndex(fresh);
                return true;
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups apply snapshot: " + ex.Message); return false; }
    }

    #endregion

    #region хук пайплайна (подмена айди) + замок обнуления

    public static void Attach() => EventListener.MiddlewareAsync += OnRequestAsync;

    public static void Detach() => EventListener.MiddlewareAsync -= OnRequestAsync;

    /// <summary>
    /// Подмена айди устройства на айди группы. Возврат false останавливает пайплайн
    /// (Core/Middlewares/ModuleAsync.cs) — этим пользуется только замок обнуления.
    /// Держать ДЁШЕВО: зовётся на каждый запрос сервера.
    /// </summary>
    public static async Task<bool> OnRequestAsync(bool first, EventMiddleware e)
    {
        try
        {
            // first:false — после UseAuthorization/UseAccsdb (они обязаны видеть устройство)
            // и до MapControllers.
            if (first) return true;

            var ctx = e?.httpContext;
            if (ctx == null) return true;

            string path = ctx.Request.Path.Value;
            if (path == null || !_syncPaths.Contains(path)) return true;

            var req = ctx.Features.Get<RequestModel>();
            if (req == null || req.IsLocalRequest) return true;   // межмодульные вызовы — не устройства

            string gid = Resolve(req.user_uid);
            if (gid == null || string.Equals(gid, req.user_uid, StringComparison.Ordinal)) return true;

            req.user_uid = gid;

            // Замок обнуления — только для группы и только на записи таймкода.
            if (path.Equals("/timecode/add", StringComparison.OrdinalIgnoreCase) && await DropZeroWriteAsync(ctx, gid))
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync("{\"success\": true, \"skipped\": \"zero\"}");
                }
                return false;
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups middleware: " + ex.Message); }

        return true;
    }

    /// <summary>
    /// 🔴 «Пустая запись не затирает прогресс». Плеер в момент ОТКРЫТИЯ карточки шлёт
    /// {"duration":0,"time":0,"percent":0}; у одного устройства это безобидно, а в группе третий,
    /// просто открывший тайтл, обнулял бы позицию первым двум.
    ///
    /// 🔥 РЕГРЕССИЯ 28.08.2026, из-за которой правило переписано. Первая версия считала пустой
    /// любую запись с time&lt;=0 или duration&lt;=0 — и выбрасывала прогресс НАТИВНЫХ плееров.
    /// Android/iOS (LampaKit) шлют road БЕЗ времени и длительности, но С процентом: в боевой базе
    /// лежат строки {"duration":0,"time":0,"percent":95}. То есть промотка на телефоне до сервера
    /// не доезжала вовсе, а второе устройство читало старую позицию и показывало «просмотрено».
    ///
    /// Правило теперь одно и оно про СОДЕРЖАНИЕ, а не про формат: пустая запись — это
    /// <c>time &lt;= 0 И percent &lt;= 0</c>, то есть road, в котором нет никакой информации.
    /// Такую нечего записывать по определению. duration не участвует вообще — у нативных
    /// плееров он ноль штатно.
    ///
    /// Цена: один точечный SELECT по уникальному индексу и ТОЛЬКО когда входящая запись пустая.
    /// В обычном потоке БД здесь не трогается вовсе.
    /// </summary>
    static async Task<bool> DropZeroWriteAsync(HttpContext ctx, string gid)
    {
        try
        {
            if (!ctx.Request.HasFormContentType) return false;

            // ReadFormAsync кэширует разбор в IFormFeature — модельбиндинг контроллера
            // ([FromForm] id/data) потом читает тот же кэш, тело второй раз не нужно.
            var form = await ctx.Request.ReadFormAsync();

            string item = form["id"];
            string data = form["data"];
            string card = ctx.Request.Query["card_id"];

            if (string.IsNullOrEmpty(item) || string.IsNullOrEmpty(data) || string.IsNullOrEmpty(card))
                return false;

            if (!IsZeroRoad(data)) return false;

            string user = gid;
            string profile = ctx.Request.Query["profile_id"];
            if (!string.IsNullOrEmpty(profile) && profile != "0")
                user = gid + "_" + profile;   // тот же ключ, что собирает TimeCodeController.getUserid

            return QbitController.TimecodeHasProgress(user, card, item);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] groups zeroguard: " + ex.Message); return false; }
    }

    /// <summary>
    /// Запись, в которой нет НИКАКОЙ информации о просмотре: ни позиции, ни процента.
    /// ⚠️ duration намеренно не смотрим: у нативных плееров Android/iOS он штатно ноль,
    /// и проверка по нему выбрасывала настоящий прогресс (см. регрессию выше).
    /// </summary>
    internal static bool IsZeroRoad(string data)
    {
        try
        {
            var o = JObject.Parse(data);
            double time = o.Value<double?>("time") ?? 0;
            double percent = o.Value<double?>("percent") ?? 0;
            return time <= 0 && percent <= 0;
        }
        catch { return false; }   // не разобрали — не наше дело, пусть пишет контроллер
    }

    #endregion
}
