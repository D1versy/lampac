using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace QbitDownload;

// ── Пассивные хелс-чеки: реестр наблюдений (qdl 2.44) ───────────────────────────
// Модель, выбранная владельцем: НЕ ходим к сервисам ради экрана. Приложение работает в
// штатном режиме и само отмечает исход каждого реального обращения — отвалилось, значит
// в хелс-чеках красное; заработало снова — статус вернулся сам.
//
// Почему так, а не пробами: прежние пробы били в корень хоста и считали успехом любой
// ответ <500 (Health.cs), поэтому 400/403/404 рисовались зелёным. Проба «хост отвечает»
// в принципе не может ответить на вопрос «сервис работает»: у AniList боевой путь POST,
// у Shikimori нужен свой User-Agent, а картинка TMDB существует только по конкретному
// пути. Единственный источник правды — исход НАСТОЯЩЕЙ операции, которую делает код.
//
// Пишут сюда боевые чокпоинты (JutNet.Run, JutShikiSearch, CatalogWarmup.Fetch и т.д.),
// читает только отчёт /qdl/health. Живые пробы остались лишь для СВОИХ контейнеров —
// они в своей сети и стоят единицы миллисекунд (см. Health.cs).
public static class HealthState
{
    /// <summary>Идентификаторы строк отчёта: общие для наблюдателей и Health.cs.</summary>
    public static class Ids
    {
        public const string JutHost = "jut-host";
        public const string JutAuth = "jut-auth";
        public const string Shikimori = "shikimori";
        public const string AniList = "anilist";
        public const string TmdbApi = "tmdb-api";
        public const string TmdbImg = "tmdb-img";
        public const string Cub = "cub";
        public const string Indexer = "indexer-live";
        public const string FfWorker = "ffworker";
        // Репликация (роль replica): «тихо перестала синхронизироваться» обязано быть видно
        // ДО аварии дома, иначе вскроется ровно в момент, когда бекап понадобился.
        public const string Replica = "replica";

        // Прогрев полок «Музыки» (MusicWarm.cs). Строка НЕ проходит через реестр наблюдений:
        // вердикт считается из music-warm.json целиком, id здесь — чтобы ключ строки отчёта
        // жил в одном месте со всеми остальными.
        public const string MusicWarm = "music-warm";

        // Аудит номера страницы в рядах каталога CUB (qdl 2.112, §DI/§DO). Как и MusicWarm,
        // через реестр наблюдений НЕ проходит: вердикт считается из снимка обхода прогрева
        // целиком. Отдельно от Ids.Cub намеренно — тот отвечает на вопрос «апстрим жив»,
        // а этот на «на полке лежит то, что просили», и смешивать их нельзя: при отравленной
        // записи апстрим как раз здоров.
        public const string CubPage = "cub-page";

        // Замена раздач (Successor.cs, qdl 2.115): через реестр наблюдений не проходит — вердикт
        // считается из watch.json (поле next) целиком, id здесь ради единого места ключей.
        public const string Successor = "successor";
    }

    public const string StatusOk = "ok";
    public const string StatusWarn = "warn";
    public const string StatusFail = "fail";
    public const string StatusOff = "off";

    // Окно флапа: 12 бакетов по 5 минут = час. Кольцо, а не список отметок — запись стоит
    // инкремент одного int, память фиксирована, и «протухание» бесплатно (бакет прошлого
    // круга опознаётся по своему номеру пятиминутки и обнуляется при первом попадании).
    const int BucketMinutes = 5;
    const int BucketCount = 12;

    #region запись (боевые чокпоинты)
    sealed class Rec
    {
        public readonly object sync = new object();
        public DateTime? lastOk, lastFail, degradedAt;
        public string lastFailText, degradedText;
        public int failStreak;
        public long okTotal, failTotal;
        public readonly int[] bucket = new int[BucketCount];
        public readonly long[] bucketSlot = new long[BucketCount];
    }

    static readonly ConcurrentDictionary<string, Rec> _recs = new();
    static volatile bool _dirty;

    // Блокировка на запись, а не Interlocked: точка вызова — завершение сетевой операции
    // (миллисекунды), на её фоне неконтендуемый lock не виден, зато снапшот получается
    // целостным, а не собранным из полей разных моментов.
    static Rec Of(string id) => _recs.GetOrAdd(id, _ => new Rec());

    static long Slot(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute / BucketMinutes;

    /// <summary>Операция удалась. Гасит липкий сбой и обнуляет счётчик «подряд».</summary>
    public static void Ok(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var r = Of(id);
        lock (r.sync)
        {
            r.lastOk = DateTime.UtcNow;
            r.failStreak = 0;
            r.okTotal++;
        }
        _dirty = true;
    }

    /// <summary>
    /// Операция провалилась. reason обязан быть своей константой или HTTP-кодом:
    /// ex.Message тащит в отчёт хосты, порты и куски строк подключения.
    /// </summary>
    public static void Fail(string id, string reason)
    {
        if (string.IsNullOrEmpty(id)) return;
        var now = DateTime.UtcNow;
        var r = Of(id);
        lock (r.sync)
        {
            r.lastFail = now;
            r.lastFailText = string.IsNullOrWhiteSpace(reason) ? "ошибка" : reason.Trim();
            r.failStreak++;
            r.failTotal++;

            long slot = Slot(now);
            int i = (int)(slot % BucketCount);
            if (r.bucketSlot[i] != slot) { r.bucketSlot[i] = slot; r.bucket[i] = 0; }
            r.bucket[i]++;
        }
        _dirty = true;
    }

    /// <summary>
    /// Короткая причина из исключения. Только имя типа: message тащит хосты, порты и куски
    /// строк подключения, а отчёт отдаётся клиенту.
    /// </summary>
    public static string ShortErr(Exception ex) => ex switch
    {
        OperationCanceledException or System.Threading.Tasks.TaskCanceledException => "таймаут",
        null => "ошибка",
        _ => ex.GetType().Name
    };

    /// <summary>Работает, но через запасной путь (прокси-фолбэк, CPU вместо NVENC, лимит API).</summary>
    public static void Degraded(string id, string reason)
    {
        if (string.IsNullOrEmpty(id)) return;
        var r = Of(id);
        lock (r.sync)
        {
            r.degradedAt = DateTime.UtcNow;
            r.degradedText = string.IsNullOrWhiteSpace(reason) ? "работает через запасной путь" : reason.Trim();
        }
        _dirty = true;
    }

    /// <summary>Вернулись на основной путь.</summary>
    public static void ClearDegraded(string id)
    {
        if (string.IsNullOrEmpty(id) || !_recs.TryGetValue(id, out var r)) return;
        lock (r.sync)
        {
            if (r.degradedAt == null) return;
            r.degradedAt = null;
            r.degradedText = null;
        }
        _dirty = true;
    }

    /// <summary>Успех + снятие деградации одним вызовом (основной путь отработал штатно).</summary>
    public static void OkDirect(string id)
    {
        Ok(id);
        ClearDegraded(id);
    }
    #endregion

    #region чтение
    public sealed class Snap
    {
        public bool known;                    // было ли хоть одно наблюдение
        public DateTime? lastOk, lastFail, degradedAt;
        public string lastFailText, degradedText;
        public int failStreak, failsInWindow;
        public long okTotal, failTotal;
    }

    public static Snap Get(string id, int flapWindowMinutes = 60) => Get(id, DateTime.UtcNow, flapWindowMinutes);

    internal static Snap Get(string id, DateTime now, int flapWindowMinutes)
    {
        if (string.IsNullOrEmpty(id) || !_recs.TryGetValue(id, out var r))
            return new Snap();

        lock (r.sync)
        {
            long cur = Slot(now);
            int keep = Math.Clamp((flapWindowMinutes + BucketMinutes - 1) / BucketMinutes, 1, BucketCount);
            int inWindow = 0;
            for (int i = 0; i < BucketCount; i++)
                if (r.bucket[i] > 0 && cur - r.bucketSlot[i] < keep) inWindow += r.bucket[i];

            return new Snap
            {
                // 🔥 degradedAt тоже наблюдение (qdl 2.65). Раньше запись, где была ТОЛЬКО
                // деградация, считалась «нет данных» и рисовалась ⏸ вместо ⚠️. Достижимо не
                // только у CUB (429 от прогрева): Shikimori и AniList на 429 зовут Degraded и
                // выходят, не трогая ни Ok, ни Fail — их лимит частоты показывался как «с
                // рестарта не обращались», хотя апстрим ответил.
                known = r.lastOk != null || r.lastFail != null || r.degradedAt != null,
                lastOk = r.lastOk,
                lastFail = r.lastFail,
                lastFailText = r.lastFailText,
                degradedAt = r.degradedAt,
                degradedText = r.degradedText,
                failStreak = r.failStreak,
                failsInWindow = inWindow,
                okTotal = r.okTotal,
                failTotal = r.failTotal
            };
        }
    }
    #endregion

    #region вердикт (чистая функция — тестируется без сети)
    /// <summary>
    /// Правила (сверху вниз, первое сработавшее побеждает):
    ///   нет наблюдений              → off  «нет данных»
    ///   последняя операция — сбой   → fail (липнет до первого успеха — решение владельца)
    ///   активна деградация          → warn (работает, но не своим путём)
    ///   в окне были ошибки          → warn (флап)
    ///   иначе                       → ok
    /// </summary>
    internal static (string status, string detail) Verdict(Snap s, DateTime now, int flapWindowMinutes = 60)
    {
        if (s == null || !s.known)
            return (StatusOff, "нет данных — с рестарта не обращались");

        bool failing = s.lastFail != null && (s.lastOk == null || s.lastFail > s.lastOk);
        if (failing)
        {
            string d = "сбой " + Ago(now - s.lastFail.Value) + ": " + (s.lastFailText ?? "ошибка");
            if (s.failStreak > 1)
                d += " · " + s.failStreak + " " + Plural(s.failStreak, "ошибка", "ошибки", "ошибок") + " подряд";
            return (StatusFail, d);
        }

        if (s.degradedAt != null)
        {
            string d = s.degradedText ?? "работает через запасной путь";
            if (s.lastOk != null) d += " · последний успех " + Ago(now - s.lastOk.Value);
            return (StatusWarn, d);
        }

        if (s.failsInWindow > 0)
        {
            string d = "работает · " + s.failsInWindow + " " + Plural(s.failsInWindow, "ошибка", "ошибки", "ошибок")
                     + " за " + (flapWindowMinutes >= 60 ? "час" : flapWindowMinutes + " мин");
            if (s.lastFail != null) d += ", последняя " + Ago(now - s.lastFail.Value);
            return (StatusWarn, d);
        }

        return (StatusOk, s.lastOk == null ? "работает" : Ago(now - s.lastOk.Value) + " · без ошибок");
    }

    internal static string Ago(TimeSpan d)
    {
        if (d.Ticks < 0) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "только что";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " мин назад";
        if (d.TotalHours < 24) return (int)d.TotalHours + " ч назад";
        return (int)d.TotalDays + " " + Plural((int)d.TotalDays, "день", "дня", "дней") + " назад";
    }

    // 1 ошибка / 2 ошибки / 5 ошибок — иначе подпись читается как машинный лог
    internal static string Plural(int n, string one, string few, string many)
    {
        int a = Math.Abs(n) % 100, b = a % 10;
        if (a is > 10 and < 20) return many;
        if (b == 1) return one;
        if (b is >= 2 and <= 4) return few;
        return many;
    }
    #endregion

    #region состояние на диске
    // Хост падает от скачков напряжения (ИБП нет), контейнер перезапускается часто — без диска
    // липкий сбой стирался бы каждым рестартом и модель врала бы ровно тогда, когда нужна.
    // Окно флапа НЕ сохраняем: после перерыва оно относится к событиям, которых уже нет.
    static readonly object _fileLock = new object();

    static string StatePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "health-state.json");

    public static void Load()
    {
        lock (_fileLock)
        {
            try
            {
                if (!System.IO.File.Exists(StatePath)) return;
                var root = JObject.Parse(System.IO.File.ReadAllText(StatePath));
                if (root["services"] is not JObject svcs) return;

                foreach (var p in svcs.Properties())
                {
                    if (p.Value is not JObject o) continue;
                    var r = Of(p.Name);
                    lock (r.sync)
                    {
                        r.lastOk = o.Value<DateTime?>("lastOk")?.ToUniversalTime();
                        r.lastFail = o.Value<DateTime?>("lastFail")?.ToUniversalTime();
                        r.lastFailText = o.Value<string>("lastFailText");
                        r.degradedAt = o.Value<DateTime?>("degradedAt")?.ToUniversalTime();
                        r.degradedText = o.Value<string>("degradedText");
                        r.failStreak = o.Value<int?>("failStreak") ?? 0;
                        r.okTotal = o.Value<long?>("okTotal") ?? 0;
                        r.failTotal = o.Value<long?>("failTotal") ?? 0;
                    }
                }
                _dirty = false;
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] health state read: " + ex.Message); }
        }
    }

    /// <summary>Пишем только если что-то менялось — тик таймера на спокойном сервере бесплатен.</summary>
    public static void FlushIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;

        var svcs = new JObject();
        foreach (var kv in _recs)
        {
            var r = kv.Value;
            lock (r.sync)
            {
                if (r.lastOk == null && r.lastFail == null && r.degradedAt == null) continue;
                svcs[kv.Key] = new JObject
                {
                    ["lastOk"] = r.lastOk,
                    ["lastFail"] = r.lastFail,
                    ["lastFailText"] = r.lastFailText,
                    ["degradedAt"] = r.degradedAt,
                    ["degradedText"] = r.degradedText,
                    ["failStreak"] = r.failStreak,
                    ["okTotal"] = r.okTotal,
                    ["failTotal"] = r.failTotal
                };
            }
        }

        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                System.IO.File.WriteAllText(StatePath,
                    new JObject { ["v"] = 1, ["services"] = svcs }.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                _dirty = true;   // не смогли записать — попробуем на следующем тике
                Console.WriteLine("[QbitDownload] health state write: " + ex.Message);
            }
        }
    }

    /// <summary>Только для тестов: реестр — статика, между кейсами его надо чистить.</summary>
    internal static void ResetForTests()
    {
        _recs.Clear();
        _dirty = false;
    }

    /// <summary>Перечитать с диска, забыв РАМ (promote в Deploy: файл писал предыдущий экземпляр).</summary>
    public static void Reload()
    {
        _recs.Clear();
        _dirty = false;
        Load();
    }
    #endregion
}
