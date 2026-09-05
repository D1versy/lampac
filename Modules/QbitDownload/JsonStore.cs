using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Горячий слой JSON-кеша: РАМ — источник истины, диск — место, откуда кеш поднимается
// после рестарта. Решение владельца (12.08.2026): «пускай хранится в оперативке, а на диск
// записывается как место, откуда тянуть кеш; всё работает на горячую с оперативки
// и параллельно синхронизируется на диск».
//
// 🔥 Зачем: /qdl/list дёргается на КАЖДОЕ открытие карточки (qdl.js подписан на 'full')
// и пересобирал ответ с нуля — ~200-300 File.Exists/ReadAllText + 62 JObject.Parse
// + два захвата глобального лока с файловым I/O внутри. Замер до: 0.58-1.03 с,
// под 4 параллельными клиентами — 1.2 с. Каждая скачанная серия аниме добавляла файл
// в маркер и делала это медленнее — отсюда «во время скачивания приложение подлагивает».
//
// ⚠️ ЧТО СЮДА НЕ КЛАДЁМ:
//   • qdl.db (SQLite) — блокировки файлов через drvfs/9p ненадёжны, это известный источник
//     «database is locked» и повреждения БД. БД остаётся в именованном томе на ext4.
//   • картинки (img/*.jpg, jut/bg) — это не текст, а отдаются они и так за 7-33 мс.
//   • кеши jut/title и jut/catalog — у них СВОЙ двухуровневый кеш (JutCacheRead/Write)
//     со своей политикой TTL и stale; дублировать его этим слоем нельзя.
//
// Пять инвариантов (закреплены JsonStoreTests):
//   1. чтение прогретого ключа НИКОГДА не идёт на диск;
//   2. диск НИКОГДА не пишется под локом, который держит запрос;
//   3. потеря диска = потеря скорости холодного старта, но не корректности;
//   4. writer коалесит по ключу: N правок одного файла между тиками = одна запись;
//   5. Flush() форсирует запись — для остановки модуля и для тестов.
//
// Канон: E:\Media-server\claude\jut\06-cache.md
// ─────────────────────────────────────────────────────────────────────────────
public static class JsonStore
{
    sealed class Entry
    {
        public JToken data;              // null = «файла нет» (негативный кеш, тоже экономит syscall)
        public bool onDisk;              // совпадает ли РАМ с тем, что лежит на диске
    }

    static readonly ConcurrentDictionary<string, Entry> _mem = new(StringComparer.OrdinalIgnoreCase);

    // Очередь грязных ключей. ConcurrentQueue + множество: множество коалесит (инвариант 4),
    // очередь держит порядок — владелец просил «на диск по факту, в порядке очереди».
    static readonly ConcurrentQueue<string> _dirtyOrder = new();
    static readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);

    static int _writer;
    static long _reads, _diskReads, _writes, _diskWrites, _dropped;

    /// <summary>
    /// Ворота записи на диск (Deploy.cs, qdl 2.110). У дежурного и замороженного экземпляра false:
    /// РАМ живёт как раньше (процесс сам с собой согласован), а диск принадлежит ведущему —
    /// иначе два процесса писали бы один документ целиком и последний затирал бы чужое.
    /// Отброшенные записи считаются в Dropped; при promote РАМ забывается без флаша.
    /// </summary>
    public static volatile bool WritesEnabled = true;

    public static long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Диагностика для /qdl/jut/diag и тестов: сколько чтений реально дошло до диска.</summary>
    public static (long reads, long diskReads, long writes, long diskWrites) Stats()
        => (Interlocked.Read(ref _reads), Interlocked.Read(ref _diskReads),
            Interlocked.Read(ref _writes), Interlocked.Read(ref _diskWrites));

    #region чтение

    /// <summary>
    /// JSON по пути. Прогретый ключ отдаётся из РАМ без единого обращения к ФС.
    /// ⚠️ Возвращается КЛОН: вызывающие вешают результат в дерево ответа (item["meta"] = …)
    /// и правят его — отдав живой экземпляр, мы бы порвали кеш и получили гонки на JToken.
    /// </summary>
    public static JObject ReadObject(string path) => Read(path) as JObject;

    public static JArray ReadArray(string path) => Read(path) as JArray;

    static JToken Read(string path)
    {
        Interlocked.Increment(ref _reads);
        if (_mem.TryGetValue(path, out var hit))
            return hit.data?.DeepClone();

        // Промах: единственный поход на диск за всю жизнь ключа
        var e = new Entry { onDisk = true };
        try
        {
            Interlocked.Increment(ref _diskReads);
            if (File.Exists(path)) e.data = JToken.Parse(File.ReadAllText(path));
        }
        catch { e.data = null; }         // битый JSON = «нет файла»; иначе падаем на каждом чтении

        _mem[path] = e;
        return e.data?.DeepClone();
    }

    /// <summary>Есть ли значение (не трогая диск, если ключ прогрет).</summary>
    public static bool Exists(string path)
    {
        if (_mem.TryGetValue(path, out var hit)) return hit.data != null;
        return Read(path) != null;
    }

    #endregion

    #region запись

    /// <summary>
    /// Кладёт значение в РАМ немедленно и ставит ключ в очередь на диск.
    /// Возврат мгновенный: инвариант 2 — запрос не ждёт диска.
    /// </summary>
    public static void Write(string path, JToken value)
    {
        Interlocked.Increment(ref _writes);
        _mem[path] = new Entry { data = value?.DeepClone(), onDisk = false };
        MarkDirty(path);
    }

    /// <summary>
    /// Запись с немедленной укладкой на диск. Для РЕДКИХ и важных значений (мета карточки):
    /// отложить их нечего ради — пишутся раз на тайтл, а потеря при убийстве контейнера
    /// в окне дебаунса означала бы карточку без метаданных при живых файлах.
    /// ⚠️ Для горячих значений (маркер после каждой серии, activity) звать Write, а не это.
    /// </summary>
    public static void WriteNow(string path, JToken value)
    {
        Interlocked.Increment(ref _writes);
        var e = new Entry { data = value?.DeepClone(), onDisk = false };
        _mem[path] = e;
        WriteThrough(path, e);
    }

    /// <summary>Удаление: и из РАМ, и с диска (через ту же очередь, чтобы не блокировать запрос).</summary>
    public static void Remove(string path)
    {
        _mem[path] = new Entry { data = null, onDisk = false };
        MarkDirty(path);
    }

    /// <summary>
    /// Забыть ключ совсем — следующее чтение перечитает диск.
    /// Нужно там, где файл мог измениться мимо нас (docker cp, ручная правка, миграция).
    /// </summary>
    public static void Forget(string path) => _mem.TryRemove(path, out _);

    public static void ForgetAll() => _mem.Clear();

    /// <summary>
    /// Забыть ВСЁ без флаша — и РАМ, и листинги, и очередь грязного. Для promote в Deploy:
    /// диск только что записал предыдущий экземпляр, наша РАМ-копия устарела по определению,
    /// и довести её до диска значило бы затереть его.
    /// </summary>
    public static void ForgetAllNoFlush()
    {
        _mem.Clear();
        _dirs.Clear();
        _dirty.Clear();
        while (_dirtyOrder.TryDequeue(out _)) { }
    }

    static void MarkDirty(string path)
    {
        if (_dirty.TryAdd(path, 0)) _dirtyOrder.Enqueue(path);
        Kick();
    }

    static void Kick()
    {
        if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Небольшая пауза — она и даёт коалесинг: пачка правок одного файла
                // (маркер после каждой серии) схлопывается в одну запись.
                await Task.Delay(200);
                Drain();
            }
            finally
            {
                Interlocked.Exchange(ref _writer, 0);
                if (!_dirtyOrder.IsEmpty) Kick();
            }
        });
    }

    static void Drain()
    {
        while (_dirtyOrder.TryDequeue(out string path))
        {
            _dirty.TryRemove(path, out _);
            if (!_mem.TryGetValue(path, out var e) || e.onDisk) continue;
            WriteThrough(path, e);
        }
    }

    static void WriteThrough(string path, Entry e)
    {
        if (!WritesEnabled)
        {
            // не ведущий: ключ остаётся «не на диске», диск не трогаем (ни основной, ни зеркало)
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            Interlocked.Increment(ref _diskWrites);
            WriteOne(path, e.data);
            e.onDisk = true;
        }
        catch (Exception ex)
        {
            // Инвариант 3: диск недоступен — теряем только скорость холодного старта.
            // Ключ остаётся грязным в РАМ и уедет на диск при следующем Flush.
            Console.WriteLine("[QbitDownload] jsonstore: не записался " + path + " — " + ex.Message);
        }

        // Зеркало на диск E: (bind ./.qdl-cache/hot). Отдельный try: сбой зеркала не имеет
        // права влиять на основную запись — том на ext4 остаётся источником истины.
        string mirror = MirrorFor(path);
        if (mirror == null) return;
        try { WriteOne(mirror, e.data); }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] jsonstore: зеркало не записалось " + mirror + " — " + ex.Message);
        }
    }

    static void WriteOne(string path, JToken data)
    {
        if (data == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // ⚠️ Через .tmp + Move: на drvfs (зеркало на E: смонтировано биндом) обрыв посреди
        // WriteAllText оставил бы обрезанный JSON, и холодное чтение получило бы мусор.
        // Move атомарен в пределах тома.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, data.ToString(Newtonsoft.Json.Formatting.None));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    /// <summary>
    /// Путь зеркала для файла из cachePath, или null если зеркало выключено.
    /// 🔴 Зеркало — ИМЕННО зеркало, а не хранилище: meta/*.json и local/*.json это не кеш,
    /// а единственная запись о том, что скачано. Держать их authoritative на drvfs значит
    /// поставить состав «Загрузок» в зависимость от файловых локов bind-маунта. Поэтому
    /// источник истины остаётся на ext4-томе, а на E: лежит читаемая из Windows копия.
    /// </summary>
    static string MirrorFor(string path)
    {
        string root = ModInit.conf?.hotMirrorPath;
        string cache = ModInit.conf?.cachePath;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(cache)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            string from = Path.GetFullPath(cache);
            if (!full.StartsWith(from, StringComparison.OrdinalIgnoreCase)) return null;
            string rel = full.Substring(from.Length).TrimStart('/', '\\');
            return Path.Combine(root, rel);
        }
        catch { return null; }
    }

    /// <summary>Форс-запись всего грязного. Синхронная — звать из остановки модуля и из тестов.</summary>
    public static void Flush() => Drain();

    /// <summary>
    /// Перечит init.conf: cachePath мог смениться, а файлы — поменяться мимо нас.
    /// ⚠️ ПОРЯДОК ВАЖЕН: сначала доводим грязное до диска, потом забываем. Иначе перечит
    /// конфига молча терял бы ещё не записанные правки (маркер, activity).
    /// </summary>
    public static void ResetForConfigReload()
    {
        Flush();
        ForgetAll();
        ForgetAllDirs();
    }

    #endregion

    #region листинг каталога

    // Листинг тоже кешируется: Directory.GetFiles(local) звался на каждом /qdl/list,
    // а меняется он только когда мы сами кладём/удаляем маркер.
    static readonly ConcurrentDictionary<string, string[]> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public static string[] List(string dir, string pattern)
    {
        string key = dir + "|" + pattern;
        if (_dirs.TryGetValue(key, out var hit)) return hit;
        string[] files;
        try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); }
        catch { files = Array.Empty<string>(); }
        _dirs[key] = files;
        return files;
    }

    /// <summary>Сбросить листинг каталога — звать там, где сами создали или удалили файл.</summary>
    public static void ForgetDir(string dir)
    {
        foreach (var k in _dirs.Keys.Where(k => k.StartsWith(dir + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _dirs.TryRemove(k, out _);
    }

    public static void ForgetAllDirs() => _dirs.Clear();

    #endregion

    #region прогрев и сверка

    /// <summary>
    /// Поднять каталог JSON-файлов в РАМ одним проходом. Зовётся на старте модуля:
    /// после этого /qdl/list не трогает диск вовсе.
    /// </summary>
    public static int Warm(string dir, string pattern = "*.json")
    {
        int n = 0;
        foreach (string f in List(dir, pattern))
        {
            if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (_mem.ContainsKey(f)) continue;
            Read(f);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Сверка с диском: файлы могли поменяться мимо нас. Дешёвая (только mtime),
    /// зовётся фоновым таймером, а не из запроса.
    /// ⚠️ Грязные ключи НЕ трогаем: в РАМ версия свежее, чем на диске, по определению.
    /// </summary>
    public static int Reconcile(string dir, string pattern = "*.json")
    {
        int changed = 0;
        ForgetDir(dir);
        var onDisk = new HashSet<string>(List(dir, pattern), StringComparer.OrdinalIgnoreCase);

        foreach (string f in onDisk)
        {
            if (_dirty.ContainsKey(f)) continue;
            if (!_mem.TryGetValue(f, out var e)) { Read(f); changed++; continue; }
            if (!e.onDisk) continue;
            try
            {
                // РАМ считает «файла нет», а он появился — перечитать
                if (e.data == null) { Forget(f); Read(f); changed++; }
            }
            catch { }
        }

        // Пропавшие с диска
        foreach (var k in _mem.Keys.Where(k => k.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (onDisk.Contains(k) || _dirty.ContainsKey(k)) continue;
            if (_mem.TryGetValue(k, out var e) && e.data != null && e.onDisk) { Forget(k); changed++; }
        }
        return changed;
    }

    #endregion
}
