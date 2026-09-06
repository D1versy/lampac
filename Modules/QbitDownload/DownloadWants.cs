using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Персистентный список НАМЕРЕНИЙ скачивания («хочу иметь эти серии на диске»).
//
// 🔥 Зачем вообще. Очереди обоих контуров (_xsQueue/_jutQueue) живут в РАМ, а рестарт
// контейнера — событие штатное: пересборка форка, правка init.conf, ~23 падения хоста
// в месяц по питанию. Боевой случай 28.08.2026: из восьми поставленных серий XSMART
// пережила ОДНА — та, у которой на диске остался хвост .parts. Реконсиляция на старте
// подбирает только .part/.parts, а элемент, не дошедший до первого байта, следов
// на диске не оставляет вообще.
//
// 🔴 ПОЧЕМУ ИМЕННО СПИСОК НАМЕРЕНИЙ, А НЕ СНИМОК ОЧЕРЕДИ. Снимок чинит ровно тот
// инцидент и не переживает следующий. Резолв делается ДО цикла ретраев и при ошибке
// делает return, а не retry (XsmartGrab.cs, JutSuGrab.cs), после чего finally воркера
// снимает элемент. lampac перезапускают вместе со стеком, xsmart-proxy поднимается
// не мгновенно — снимок восстановится, воркер получит UPSTREAM_DOWN на всех восьми
// и аккуратно снимет все восемь. Потеря та же, только на три секунды позже.
// Поэтому: запись снимается ТОЛЬКО по факту готового файла (*FinishFile), а не
// в *Forget. «Сдался после ретраев» → tries++ и бэкофф, запись жива.
//
// 🔴 АСИММЕТРИЧНАЯ ДОЛГОВЕЧНОСТЬ. Постановка пишется write-through (SaveNow), снятие —
// write-behind (Save). Потеря постановки = потеря серии навсегда; потеря снятия = одна
// лишняя проверка «а не лежит ли уже на диске» при следующем старте, которая гасится
// ключом диска и стоит ноль. На хук остановки полагаться нельзя: Core/Startup.cs при
// Program._reload не зовёт Dispose вообще, а kill -9 и пропадание питания его не
// переживают в принципе.
//
// Стор НИЧЕГО не знает про контуры: ключ тайтла — строка, payload — непрозрачный JObject.
// Вся семантика в адаптерах Xsmart*/Jut* ниже.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Одна запись намерения: «серия <see cref="e"/> тайтла <see cref="t"/> должна лежать на диске».</summary>
sealed class WantRec
{
    public string t;            // ключ тайтла: sref у XSMART, slug у jut
    public string e;            // epkey — тот же, что в _xsQueued/_jutQueued
    public long n;              // порядковый номер постановки (порядок восстановления)
    public long at;             // когда поставлено, unix
    public string src;          // manual | watch | reconcile | upgrade
    public int tries;           // неудачных попыток подряд
    public long nextAt;         // не пробовать раньше этого времени, unix
    public string err;          // последняя ошибка (для статуса и диагностики)
    public JObject p;           // payload контура

    public string Key => t + ":" + e;

    public JObject ToJson()
    {
        var jo = new JObject { ["t"] = t, ["e"] = e, ["n"] = n, ["at"] = at };
        if (!string.IsNullOrEmpty(src)) jo["src"] = src;
        if (tries > 0) jo["tries"] = tries;
        if (nextAt > 0) jo["nextAt"] = nextAt;
        if (!string.IsNullOrEmpty(err)) jo["err"] = err;
        jo["p"] = p ?? new JObject();
        return jo;
    }

    public static WantRec FromJson(JObject jo)
    {
        string t = jo?.Value<string>("t");
        string e = jo?.Value<string>("e");
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(e)) return null;
        return new WantRec
        {
            t = t,
            e = e,
            n = jo.Value<long?>("n") ?? 0,
            at = jo.Value<long?>("at") ?? 0,
            src = jo.Value<string>("src"),
            tries = jo.Value<int?>("tries") ?? 0,
            nextAt = jo.Value<long?>("nextAt") ?? 0,
            err = jo.Value<string>("err"),
            p = jo["p"] as JObject ?? new JObject()
        };
    }
}

/// <summary>
/// Механика хранения. Источник истины — словарь в РАМ, диск через <see cref="JsonStore"/>
/// (там уже есть атомарная запись .tmp→Move, коалесинг 200 мс и синхронный Flush
/// при остановке модуля).
/// </summary>
/// <remarks>
/// 🔴 ЛОК — ЛИСТ. Берётся только сам по себе либо под _xsEnqLock/_jutEnqLock, НИКОГДА
/// наоборот, и изнутри не зовёт ни одной функции контроллера. Иначе дедлок между
/// постановкой в очередь и свипом — вопрос времени.
/// </remarks>
sealed class WantStore
{
    readonly string _tag;
    readonly Func<string> _path;
    readonly Func<bool> _on;
    readonly object _lock = new();
    readonly Dictionary<string, WantRec> _items = new(StringComparer.Ordinal);
    long _seq;
    bool _loaded;

    // Бэкофф неудач. Последнее значение повторяется: серия, которую портал отдаст через
    // неделю, не должна перестать проверяться, но и молотить чаще раза в сутки незачем.
    static readonly int[] _backoffMin = { 5, 15, 60, 180, 360, 720, 1440 };

    public WantStore(string tag, Func<string> path, Func<bool> on)
    {
        _tag = tag; _path = path; _on = on;
    }

    public bool Enabled => _on == null || _on();

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    void Log(string msg) => Console.WriteLine("[QbitDownload] " + _tag + "/wants: " + msg);

    #region загрузка и запись

    /// <summary>Поднять файл в РАМ. Идемпотентно, один поход на диск за жизнь ключа JsonStore.</summary>
    void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!Enabled) return;
        try
        {
            var jo = JsonStore.ReadObject(_path());
            if (jo?["items"] is not JObject items) return;
            foreach (var prop in items.Properties())
            {
                var r = WantRec.FromJson(prop.Value as JObject);
                if (r == null) continue;
                _items[r.Key] = r;
                if (r.n >= _seq) _seq = r.n + 1;
            }
            if (_items.Count > 0) Log("поднято записей — " + _items.Count);
        }
        catch (Exception ex) { Log("чтение: " + ex.Message); }
    }

    public void Load() { lock (_lock) EnsureLoaded(); }

    JObject Snapshot()
    {
        var items = new JObject();
        foreach (var kv in _items) items[kv.Key] = kv.Value.ToJson();
        return new JObject { ["v"] = 1, ["items"] = items };
    }

    /// <summary>Write-behind: для снятия и служебных правок. Потеря безопасна.</summary>
    void SaveLocked()
    {
        if (!Enabled) return;
        try { JsonStore.Write(_path(), Snapshot()); }
        catch (Exception ex) { Log("запись: " + ex.Message); }
    }

    /// <summary>Write-through: ТОЛЬКО для постановки. Потеря = потеря серии навсегда.</summary>
    void SaveNowLocked()
    {
        if (!Enabled) return;
        try { JsonStore.WriteNow(_path(), Snapshot()); }
        catch (Exception ex) { Log("запись: " + ex.Message); }
    }

    public void Save() { lock (_lock) { EnsureLoaded(); SaveLocked(); } }

    #endregion

    #region постановка

    /// <summary>
    /// Фаза 1 двухфазного коммита: намерение на диске ДО того, как ключ попадёт
    /// в _xsQueued/_jutQueued. Один файл на пачку, не на серию.
    /// </summary>
    public int Commit(string title, IEnumerable<(string epkey, JObject payload)> units, string src)
    {
        if (string.IsNullOrEmpty(title) || units == null) return 0;
        int added = 0;
        lock (_lock)
        {
            EnsureLoaded();
            foreach (var (epkey, payload) in units)
            {
                if (string.IsNullOrEmpty(epkey)) continue;
                string key = title + ":" + epkey;
                if (_items.TryGetValue(key, out var old))
                {
                    // Повторная постановка того же — обновляем payload (мог поменяться epId
                    // или появиться флаг апгрейда) и снимаем бэкофф: владелец просит СЕЙЧАС.
                    old.p = payload ?? old.p;
                    old.src = src ?? old.src;
                    old.tries = 0; old.nextAt = 0; old.err = null;
                    continue;
                }
                _items[key] = new WantRec
                {
                    t = title, e = epkey, n = _seq++, at = Now,
                    src = src, p = payload ?? new JObject()
                };
                added++;
            }
            SaveNowLocked();
        }
        return added;
    }

    #endregion

    #region снятие и отказы

    /// <summary>Намерение исполнено — файл на диске. Единственная точка снятия по успеху.</summary>
    public void Done(string title, string epkey)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(epkey)) return;
        lock (_lock)
        {
            EnsureLoaded();
            if (_items.Remove(title + ":" + epkey)) SaveLocked();
        }
    }

    /// <summary>
    /// Попытка не удалась. 🔴 Запись НЕ удаляется — этим список намерений и отличается
    /// от снимка очереди: серия переживает и «сдался после ретраев», и лежачий портал.
    /// </summary>
    public void Fail(string title, string epkey, string err)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(epkey)) return;
        lock (_lock)
        {
            EnsureLoaded();
            if (!_items.TryGetValue(title + ":" + epkey, out var r)) return;
            r.tries++;
            r.err = err;
            int min = _backoffMin[Math.Min(r.tries - 1, _backoffMin.Length - 1)];
            r.nextAt = Now + min * 60L;
            SaveLocked();
        }
    }

    /// <summary>Снять все намерения тайтла: отмена, удаление карточки, уборка хвостов.</summary>
    public int DropTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return 0;
        lock (_lock)
        {
            EnsureLoaded();
            string p = title + ":";
            var drop = _items.Keys.Where(k => k.StartsWith(p, StringComparison.Ordinal)).ToList();
            foreach (string k in drop) _items.Remove(k);
            if (drop.Count > 0) SaveLocked();
            return drop.Count;
        }
    }

    #endregion

    #region чтение

    public bool Has(string title, string epkey)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(epkey)) return false;
        lock (_lock) { EnsureLoaded(); return _items.ContainsKey(title + ":" + epkey); }
    }

    /// <summary>
    /// Самая ранняя постановка тайтла (unix), 0 — записей нет. Даёт карточке «в полёте»
    /// в /qdl/list устойчивое «added»: «сейчас» там нельзя — тело ETag'ится, а порядок
    /// «Загрузок» считается по этому полю и прыгал бы на каждом запросе.
    /// </summary>
    public long OldestAt(string title)
    {
        if (string.IsNullOrEmpty(title)) return 0;
        string p = title + ":";
        long best = 0;
        lock (_lock)
        {
            EnsureLoaded();
            foreach (var kv in _items)
            {
                if (!kv.Key.StartsWith(p, StringComparison.Ordinal)) continue;
                long at = kv.Value.at;
                if (at > 0 && (best == 0 || at < best)) best = at;
            }
        }
        return best;
    }

    public bool HasTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        string p = title + ":";
        lock (_lock)
        {
            EnsureLoaded();
            foreach (string k in _items.Keys)
                if (k.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    public int CountFor(string title)
    {
        if (string.IsNullOrEmpty(title)) return 0;
        string p = title + ":";
        lock (_lock)
        {
            EnsureLoaded();
            return _items.Keys.Count(k => k.StartsWith(p, StringComparison.Ordinal));
        }
    }

    /// <summary>Припаркованные: порог попыток исчерпан, автоматически больше не ставим.</summary>
    public bool IsParked(WantRec r) => r != null && r.tries >= MaxTries;

    int MaxTries => Math.Max(1, _maxTries?.Invoke() ?? 12);
    Func<int> _maxTries;
    public WantStore WithMaxTries(Func<int> f) { _maxTries = f; return this; }

    /// <summary>
    /// Долг тайтла: не припарковано и время подошло. Копии записей — вызывающий
    /// работает с ними вне лока.
    /// </summary>
    public List<WantRec> Owed(string title)
    {
        var res = new List<WantRec>();
        if (string.IsNullOrEmpty(title)) return res;
        string p = title + ":";
        long now = Now;
        lock (_lock)
        {
            EnsureLoaded();
            foreach (var kv in _items)
            {
                if (!kv.Key.StartsWith(p, StringComparison.Ordinal)) continue;
                var r = kv.Value;
                if (IsParked(r) || r.nextAt > now) continue;
                res.Add(r);
            }
        }
        return res.OrderBy(x => x.n).ToList();
    }

    /// <summary>Все тайтлы, у которых есть хоть одна запись. Для свипа и восстановления.</summary>
    public List<string> Titles()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items.Values.Select(x => x.t).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Сводка для статуса: сколько должны и сколько застряло.</summary>
    public (int owed, int parked, string err) Stat(string title)
    {
        int owed = 0, parked = 0; string err = null;
        if (string.IsNullOrEmpty(title)) return (0, 0, null);
        string p = title + ":";
        lock (_lock)
        {
            EnsureLoaded();
            foreach (var kv in _items)
            {
                if (!kv.Key.StartsWith(p, StringComparison.Ordinal)) continue;
                if (IsParked(kv.Value)) { parked++; err ??= kv.Value.err; }
                else owed++;
            }
        }
        return (owed, parked, err);
    }

    #endregion

    #region уборка и сброс

    /// <summary>
    /// Прополка: TTL для припаркованных и кап на тайтл. Без неё файл растёт вечно —
    /// снятая с портала серия висела бы в нём годами.
    /// </summary>
    public int Prune(int keepDays, int maxPerTitle)
    {
        long edge = Now - Math.Max(1, keepDays) * 86400L;
        int cap = Math.Max(1, maxPerTitle);
        int removed = 0;
        lock (_lock)
        {
            EnsureLoaded();
            var dropParked = _items.Where(kv => IsParked(kv.Value) && kv.Value.at < edge)
                                   .Select(kv => kv.Key).ToList();
            foreach (string k in dropParked) { _items.Remove(k); removed++; }

            foreach (var g in _items.Values.GroupBy(x => x.t, StringComparer.Ordinal).ToList())
            {
                if (g.Count() <= cap) continue;
                // Режем самые старые: свежие намерения нужнее.
                foreach (var r in g.OrderBy(x => x.n).Take(g.Count() - cap).ToList())
                { _items.Remove(r.Key); removed++; }
            }
            if (removed > 0) { Log("прополото записей — " + removed); SaveLocked(); }
        }
        return removed;
    }

    /// <summary>Убрать запись без бэкоффа — для битого payload, который не починится сам.</summary>
    public void Drop(string title, string epkey, string why)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(epkey)) return;
        lock (_lock)
        {
            EnsureLoaded();
            if (_items.Remove(title + ":" + epkey))
            {
                Log("выброшена запись " + title + ":" + epkey + " — " + why);
                SaveLocked();
            }
        }
    }

    /// <summary>Сброс. flush=true — довести грязное до диска (остановка модуля, смена cachePath).</summary>
    public void Reset(bool flush)
    {
        lock (_lock)
        {
            if (flush && _loaded) SaveLocked();
            _items.Clear();
            _seq = 0;
            _loaded = false;
        }
    }

    #endregion
}

/// <summary>Два стора и общие операции жизненного цикла.</summary>
static class DownloadWants
{
    public static readonly WantStore Xsmart =
        new WantStore("xsmart",
                      () => Path.Combine(XsmartNet.DataDir(), "queue.json"),
                      () => ModInit.conf?.xsmartQueuePersist ?? true)
            .WithMaxTries(() => ModInit.conf?.xsmartWantMaxTries ?? 12);

    public static readonly WantStore Jut =
        new WantStore("jut",
                      () => Path.Combine(JutNet.JutDataDir(), "queue.json"),
                      () => ModInit.conf?.jutQueuePersist ?? true)
            .WithMaxTries(() => ModInit.conf?.jutWantMaxTries ?? 12);

    /// <summary>
    /// Довести грязное до JsonStore. Зовётся из ModInit.Dispose ПЕРЕД JsonStore.Flush():
    /// мы пишем через него, значит он обязан флашиться последним.
    /// ⚠️ Метод статический — Core/Startup.cs создаёт для Dispose НОВЫЙ экземпляр ModInit.
    /// </summary>
    public static void Flush()
    {
        try { Xsmart.Save(); } catch { }
        try { Jut.Save(); } catch { }
    }

    /// <summary>Смена cachePath: сперва довести грязное, потом забыть (порядок как у JsonStore).</summary>
    public static void ResetForConfigReload()
    {
        Xsmart.Reset(flush: true);
        Jut.Reset(flush: true);
    }

    /// <summary>
    /// Для тестов: забыть БЕЗ флаша. Сброс с записью уронил бы состояние прошлого кейса
    /// в новый временный cachePath.
    /// </summary>
    public static void ResetForTests() => ResetNoFlush();

    /// <summary>Promote в Deploy: забыть БЕЗ флаша — журнал на диске дописал предыдущий экземпляр.</summary>
    public static void ResetNoFlush()
    {
        Xsmart.Reset(flush: false);
        Jut.Reset(flush: false);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Адаптеры контуров. Живут здесь, а не в XsmartGrab.cs/JutSuGrab.cs, ровно по одной
// причине: им нужен доступ к private-nested XsmartGrabItem/JutGrabItem, и partial-класс
// в этом же файле его даёт, оставляя правки горячих файлов минимальными.
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region XSMART

    static JObject XsmartWantPayload(int cat, string id, string source, string titleRu,
                                     XsmartEp e, int upgradeTo)
    {
        var p = new JObject
        {
            ["cat"] = cat,
            ["id"] = id,
            ["kind"] = e.kind == XsmartKind.Film ? "film" : "episode",
            ["sno"] = e.seasonNo,
            ["epno"] = e.epNo
        };
        if (!string.IsNullOrEmpty(source)) p["source"] = source;
        if (!string.IsNullOrEmpty(e.seasonId)) p["sid"] = e.seasonId;
        if (!string.IsNullOrEmpty(e.epId)) p["eid"] = e.epId;
        if (!string.IsNullOrEmpty(titleRu)) p["titleRu"] = titleRu;
        if (upgradeTo > 0) { p["upgrade"] = true; p["wantQ"] = upgradeTo; }
        return p;
    }

    /// <summary>Фаза 1: намерение на диск. Зовётся ДО постановки в очередь и ДО сдвига baseline.</summary>
    internal static int XsmartWantsCommit(string sref, int cat, string id, string source,
                                          string titleRu, IEnumerable<XsmartEp> eps, string src,
                                          int upgradeTo = 0)
    {
        if (eps == null) return 0;
        return DownloadWants.Xsmart.Commit(
            sref,
            eps.Select(e => (e.epkey, XsmartWantPayload(cat, id, source, titleRu, e, upgradeTo))),
            src);
    }

    internal static void XsmartWantsDone(string sref, string epkey)
        => DownloadWants.Xsmart.Done(sref, epkey);

    internal static void XsmartWantsFail(string sref, string epkey, string err)
        => DownloadWants.Xsmart.Fail(sref, epkey, err);

    internal static int XsmartWantsDropTitle(string sref)
        => DownloadWants.Xsmart.DropTitle(sref);

    internal static bool XsmartWantsHas(string sref) => DownloadWants.Xsmart.HasTitle(sref);

    /// <summary>
    /// Восстановление XsmartEp из payload + санитарные ворота. Без них один битый JSON
    /// отравил бы очередь на все последующие старты: серия без sid/eid не резолвится
    /// НИКОГДА, и паркой после двенадцати бесполезных попыток тут не отделаться.
    ///
    /// 🔴 Фильму eid НЕ нужен — и требовать его нельзя. У фильма XSMART epId пуст по
    /// конструкции (Xsmart.cs: единица «film» строится без id серии), а резолв шлёт
    /// season/episode только у kind == Episode. До qdl 2.114 ворота требовали eid и от
    /// фильма, и первый же свип (4 мин после старта, потом каждые 5) выбрасывал запись
    /// как битую: боевая строка 06.09.2026 — «xsmart/wants: выброшена запись
    /// 6-10425171:film — нет eid». Страховка «пережить рестарт» на фильмы не действовала
    /// вовсе; ловится QueuePersistTests.Свип_не_выбрасывает_фильм.
    /// </summary>
    static XsmartEp XsmartEpFromWant(WantRec r, out string bad)
    {
        bad = null;
        var p = r.p ?? new JObject();
        bool film = string.Equals(p.Value<string>("kind"), "film", StringComparison.OrdinalIgnoreCase);
        var e = new XsmartEp
        {
            kind = film ? XsmartKind.Film : XsmartKind.Episode,
            seasonNo = p.Value<int?>("sno") ?? 1,
            epNo = p.Value<int?>("epno") ?? 0,
            seasonId = p.Value<string>("sid"),
            epId = p.Value<string>("eid"),
            playable = true
        };
        if (!film && (string.IsNullOrEmpty(e.seasonId) || string.IsNullOrEmpty(e.epId)))
        { bad = "нет sid/eid"; return null; }
        if (e.epkey != r.e) { bad = "epkey не сходится (" + e.epkey + " ≠ " + r.e + ")"; return null; }
        return e;
    }

    /// <summary>Целевое качество апгрейда, 0 — обычная загрузка.</summary>
    static int XsmartWantUpgradeTo(WantRec r)
        => (r.p?.Value<bool?>("upgrade") ?? false) ? (r.p.Value<int?>("wantQ") ?? 0) : 0;

    /// <summary>
    /// Общий цикл постановки долгов тайтла. Возвращает, сколько реально встало.
    /// 🔴 JobForBatch зовётся ОДИН раз на пачку: поштучный вызов обнулял бы filesTotal
    /// на каждой серии (freshBatch) и разбил бы агрегат уведомлений на N строк.
    /// </summary>
    static int XsmartWantsPut(string sref, List<WantRec> owed)
    {
        if (owed == null || owed.Count == 0) return 0;

        int cat = 0; string id = null;
        var disk = XsmartDiskKeys(sref);
        var items = new List<XsmartGrabItem>();

        foreach (var r in owed.OrderBy(x => x.n))
        {
            var p = r.p ?? new JObject();
            int rcat = p.Value<int?>("cat") ?? 0;
            string rid = p.Value<string>("id");
            if (!XsmartNet.Valid(rcat, rid))
            { DownloadWants.Xsmart.Drop(sref, r.e, "битые cat/id"); continue; }

            var e = XsmartEpFromWant(r, out string bad);
            if (e == null) { DownloadWants.Xsmart.Drop(sref, r.e, bad); continue; }

            int up = XsmartWantUpgradeTo(r);
            // Уже на диске → намерение исполнено. Исключение — апгрейд: ключ диска
            // качества не различает, и без этой оговорки upgrade-запись снималась бы
            // молча, а механизм апгрейда просто не работал бы.
            if (disk.Contains(r.e) && !(up > 0 && XsmartDiskQualityOf(sref, r.e) < up))
            { DownloadWants.Xsmart.Done(sref, r.e); continue; }

            cat = rcat; id = rid;
            items.Add(new XsmartGrabItem
            {
                cat = rcat, id = rid, sref = sref, source = p.Value<string>("source"),
                ep = e, titleRu = p.Value<string>("titleRu"),
                upgradeTo = up
                // gen проставим под локом: сохранённого поколения в JSON нет намеренно
            });
        }
        if (items.Count == 0 || id == null) return 0;

        bool freshBatch = XsmartPendingFor(sref) == 0;
        int put = 0;
        lock (_xsEnqLock)
        {
            int gen = XsmartGenOf(sref);
            foreach (var it in items)
            {
                // 🔴 Перепроверка намерения ВНУТРИ лока: между сбором списка и постановкой
                // могла пройти отмена, которая снесла wants и двинула поколение.
                if (!DownloadWants.Xsmart.Has(sref, it.ep.epkey)) continue;
                if (!_xsQueued.Add(XsmartQueueKey(sref, it.ep.epkey))) continue;
                it.gen = gen;
                _xsQueue.Enqueue(it);
                put++;
            }
        }
        if (put > 0) XsmartJobForBatch(sref, freshBatch, put);
        return put;
    }

    /// <summary>Старт: поднять намерения и вернуть в очередь всё, чего нет на диске.</summary>
    internal static void XsmartWantsRestore()
    {
        if (!XsmartNet.On || !DownloadWants.Xsmart.Enabled) return;
        DownloadWants.Xsmart.Load();
        DownloadWants.Xsmart.Prune(ModInit.conf?.wantsKeepDays ?? 30,
                                   ModInit.conf?.wantsMaxPerTitle ?? 1000);

        int total = 0;
        foreach (string sref in DownloadWants.Xsmart.Titles())
        {
            try
            {
                XsmartDropDiskKeys(sref);          // снимок с прошлой жизни процесса не годится
                total += XsmartWantsPut(sref, DownloadWants.Xsmart.Owed(sref));
            }
            catch (Exception ex) { XsmartNet.Log("wants", sref + ": " + ex.Message); }
        }
        if (total > 0)
        {
            XsmartNet.Log("wants", "восстановлено " + total);
            XsmartKickWorker();
        }
    }

    /// <summary>
    /// Свип: вернуть в очередь просроченные долги. Ни одного сетевого запроса — payload
    /// несёт sid/eid, а значит резолв соберётся и без кеша карточки (именно на нём
    /// спотыкается XsmartReconcile при t == null).
    /// </summary>
    internal static int XsmartWantsSweep()
    {
        if (!XsmartNet.On || !DownloadWants.Xsmart.Enabled) return 0;
        int put = 0;
        foreach (string sref in DownloadWants.Xsmart.Titles())
        {
            try { put += XsmartWantsPut(sref, DownloadWants.Xsmart.Owed(sref)); }
            catch (Exception ex) { XsmartNet.Log("wants", sref + ": " + ex.Message); }
        }
        if (put > 0) { XsmartNet.Log("wants", "свип поставил " + put); XsmartKickWorker(); }
        return put;
    }

    /// <summary>Долг тайтла как список XsmartEp — для тика слежения.</summary>
    internal static List<XsmartEp> XsmartWantsOwedEps(string sref)
    {
        var res = new List<XsmartEp>();
        foreach (var r in DownloadWants.Xsmart.Owed(sref))
        {
            var e = XsmartEpFromWant(r, out _);
            if (e != null) res.Add(e);
        }
        return res;
    }

    #endregion

    #region jut.su

    static JObject JutWantPayload(string slug, JutEp e, string titleRu, int upgradeTo)
    {
        var p = new JObject
        {
            ["slug"] = slug,
            ["kind"] = JutKindParam(e.kind),
            ["season"] = e.season,
            ["ep"] = e.num
        };
        if (!string.IsNullOrEmpty(titleRu)) p["titleRu"] = titleRu;
        if (upgradeTo > 0) { p["upgrade"] = true; p["wantQ"] = upgradeTo; }
        return p;
    }

    internal static int JutWantsCommit(string slug, string titleRu, IEnumerable<JutEp> eps,
                                       string src, int upgradeTo = 0)
    {
        if (eps == null) return 0;
        return DownloadWants.Jut.Commit(
            slug,
            eps.Select(e => (e.epkey, JutWantPayload(slug, e, titleRu, upgradeTo))),
            src);
    }

    internal static void JutWantsDone(string slug, string epkey)
        => DownloadWants.Jut.Done(slug, epkey);

    internal static void JutWantsFail(string slug, string epkey, string err)
        => DownloadWants.Jut.Fail(slug, epkey, err);

    internal static int JutWantsDropTitle(string slug) => DownloadWants.Jut.DropTitle(slug);

    internal static bool JutWantsHas(string slug) => DownloadWants.Jut.HasTitle(slug);

    /// <summary>Ключ серии по составляющим — точная копия JutEp.epkey (тот вычисляемый).</summary>
    static string JutEpKeyOf(JutEpKind k, int season, int num) => k switch
    {
        JutEpKind.Episode => "s" + season + "e" + num,
        JutEpKind.Film => "film" + num,
        JutEpKind.Ova => "ova" + num,
        JutEpKind.GameOva => "gameova" + num,
        _ => "sp" + num
    };

    static JutEp JutEpFromWant(WantRec r, out string bad)
    {
        bad = null;
        var p = r.p ?? new JObject();
        var e = new JutEp
        {
            slug = p.Value<string>("slug"),
            kind = JutKindFromString(p.Value<string>("kind")),
            season = Math.Max(1, p.Value<int?>("season") ?? 1),
            num = p.Value<int?>("ep") ?? -1
        };
        if (!JutSuParse.IsValidSlug(e.slug)) { bad = "битый slug"; return null; }
        if (e.num < 0) { bad = "нет номера"; return null; }
        if (JutEpKeyOf(e.kind, e.season, e.num) != r.e)
        { bad = "epkey не сходится (" + JutEpKeyOf(e.kind, e.season, e.num) + " ≠ " + r.e + ")"; return null; }
        return e;
    }

    static int JutWantUpgradeTo(WantRec r)
        => (r.p?.Value<bool?>("upgrade") ?? false) ? (r.p.Value<int?>("wantQ") ?? 0) : 0;

    /// <summary>
    /// ⚠️ Два разных пространства ключей. В сторе и в _jutQueued живёт epkey БЕЗ паддинга
    /// (s1e5), а на диске ключ — имя файла С паддингом (slug.s01e05.mp4). Конвертация
    /// обязана идти через JutEpKey, а не сравнением строк.
    /// </summary>
    static int JutWantsPut(string slug, List<WantRec> owed)
    {
        if (owed == null || owed.Count == 0) return 0;

        var disk = JutDiskKeys(slug);
        var items = new List<JutGrabItem>();

        foreach (var r in owed.OrderBy(x => x.n))
        {
            var e = JutEpFromWant(r, out string bad);
            if (e == null) { DownloadWants.Jut.Drop(slug, r.e, bad); continue; }

            int up = JutWantUpgradeTo(r);
            if (disk.Contains(JutEpKey(slug, e)) && !(up > 0 && JutDiskQualityOf(slug, e) < up))
            { DownloadWants.Jut.Done(slug, r.e); continue; }

            items.Add(new JutGrabItem
            {
                slug = slug, season = e.season, ep = e.num, kind = JutKindParam(e.kind),
                epkey = e.epkey, titleRu = r.p?.Value<string>("titleRu"),
                upgradeTo = up
            });
        }
        if (items.Count == 0) return 0;

        bool freshBatch = JutPendingFor(slug) == 0;
        int put = 0;
        lock (_jutEnqLock)
        {
            int gen = JutGenOf(slug);
            foreach (var it in items)
            {
                if (!DownloadWants.Jut.Has(slug, it.epkey)) continue;
                if (!_jutQueued.Add(JutQueueKey(slug, it.epkey))) continue;
                it.gen = gen;
                _jutQueue.Enqueue(it);
                put++;
            }
        }
        if (put > 0) JutJobForBatch(slug, freshBatch, put);
        return put;
    }

    internal static void JutWantsRestore()
    {
        if (!JutOn || !DownloadWants.Jut.Enabled) return;
        DownloadWants.Jut.Load();
        DownloadWants.Jut.Prune(ModInit.conf?.wantsKeepDays ?? 30,
                                ModInit.conf?.wantsMaxPerTitle ?? 1000);

        int total = 0;
        foreach (string slug in DownloadWants.Jut.Titles())
        {
            try
            {
                JutDropDiskKeys(slug);
                total += JutWantsPut(slug, DownloadWants.Jut.Owed(slug));
            }
            catch (Exception ex) { JutNet.Log("wants", slug + ": " + ex.Message); }
        }
        if (total > 0)
        {
            JutNet.Log("wants", "восстановлено " + total);
            JutKickWorker();
        }
    }

    internal static int JutWantsSweep()
    {
        if (!JutOn || !DownloadWants.Jut.Enabled) return 0;
        int put = 0;
        foreach (string slug in DownloadWants.Jut.Titles())
        {
            try { put += JutWantsPut(slug, DownloadWants.Jut.Owed(slug)); }
            catch (Exception ex) { JutNet.Log("wants", slug + ": " + ex.Message); }
        }
        if (put > 0) { JutNet.Log("wants", "свип поставил " + put); JutKickWorker(); }
        return put;
    }

    /// <summary>Долг тайтла как список JutEp — для тика слежения.</summary>
    internal static List<JutEp> JutWantsOwedEps(string slug)
    {
        var res = new List<JutEp>();
        foreach (var r in DownloadWants.Jut.Owed(slug))
        {
            var e = JutEpFromWant(r, out _);
            if (e != null) res.Add(e);
        }
        return res;
    }

    #endregion
}
