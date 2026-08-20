using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Память экрана поиска jut.su: что смотрели и что искали — ОТДЕЛЬНО НА КАЖДОЕ УСТРОЙСТВО.
//
// Зачем на СЕРВЕРЕ, а не в Lampa.Storage: сервер знает оба события без единой строчки
// в клиенте — «смотрел» по факту байтов через /qdl/jut/stream (любая платформа, любой
// плеер, включая нативные), «искал» по выдаче /qdl/jut/search.
//
// 🔥 Раздельно по устройствам (требование владельца 2026-08-20; до этого история была
// одна на сервер). Устройство = lampac_unic_id — тот же канонический uid, с которым
// ходят Sync/TimeCode/Bookmark; своё второе понятие «устройства» немедленно разъехалось
// бы с прогрессом просмотра. Клиент шлёт его параметром uid=, сервер читает готовым
// requestInfo.user_uid (RequestInfo.getuid разбирает query сам).
//
// ⚠️ Ограничение, названное вслух: у браузера и Tizen нативного KV нет, их uid живёт
// в localStorage конкретного origin — заход из LAN и снаружи там будет двумя устройствами.
// Для нативных клиентов (mac/iOS/Android/Windows) uid переживает смену origin.
//
// Бакет _shared — приёмник запросов БЕЗ uid (старый закешированный qdl.js, браузер до
// генерации uid) и одновременно цель миграции прежней общей истории. Устройство со своим
// бакетом добирает из _shared, пока своего мало: иначе новый клиент видел бы пустой экран.
//
// Порядок в выдаче: сначала просмотренное (свежее выше), потом добор из поисковых выдач.
// Правило владельца: «если просмотренных нет — собирать топ из того, что прилетело из поиска».
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region хранилище

    const int JutHistCap = 200;        // на каждую из двух секций, на устройство
    const int JutWatchDedupSec = 300;  // seek-переоткрытия стрима не должны спамить записью
    const int JutHistDevCap = 24;      // сколько устройств помним
    const int JutUidMaxLen = 48;
    const string JutSharedBucket = "_shared";

    static string JutHistoryDir() => Path.Combine(JutNet.JutDataDir(), "history");
    static string JutHistoryPath(string bucket) => Path.Combine(JutHistoryDir(), bucket + ".json");
    static string JutHistoryLegacyPath() => Path.Combine(JutNet.JutDataDir(), "history.json");

    // Дедуп записи «смотрел»: плеер за серию открывает поток многократно (seek, докачка).
    // ⚠️ Ключ ОБЯЗАН включать бакет: иначе просмотр на телефоне на 5 минут заглушил бы
    // запись того же тайтла на ТВ.
    static readonly ConcurrentDictionary<string, DateTime> _jutWatchSeen = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _jutHistLock = new();

    static readonly Regex _jutUidRx = new(@"[^a-z0-9\-_\.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Зарезервированные имена устройств Win32: файл con.json на Windows — это консоль,
    // а не файл (тесты гоняются именно на Windows). На томе Linux безобидно, но правило
    // должно быть одно для обеих сред.
    static readonly HashSet<string> _jutWinReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    /// <summary>
    /// uid → имя файла бакета. 🔴 Санация обязательна, а не гигиена: ValidateIdentity по
    /// умолчанию выключен, и RequestInfo отдаёт значение query без единой проверки символов,
    /// а оно идёт в имя файла на диске. Пустой/мусорный uid → общий бакет.
    /// </summary>
    internal static string JutHistoryBucket(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return JutSharedBucket;

        string s = _jutUidRx.Replace(uid, "");
        if (s.Length > JutUidMaxLen) s = s.Substring(0, JutUidMaxLen);
        s = s.Trim('.');                       // «.» и «..» после чистки — это путь, а не имя
        if (s.Length == 0) return JutSharedBucket;

        // Источник истины лежит на ext4 (Abc.json ≠ abc.json), а зеркало JsonStore пишется
        // на drvfs (Abc.json == abc.json) — без приведения регистра бакеты склеились бы в зеркале.
        s = s.ToLowerInvariant();
        if (_jutWinReserved.Contains(s)) s = "d_" + s;
        return s;
    }

    /// <summary>Бакет текущего запроса. uid приходит из query — его разбирает RequestInfo.</summary>
    string JutHistoryBucketOfRequest()
    {
        try { return JutHistoryBucket(requestInfo?.user_uid); }
        catch { return JutSharedBucket; }
    }

    // Миграция помечается ПО КАТАЛОГУ, а не флагом на процесс: cachePath меняется при
    // перечитывании конфига (и в каждом тесте), и флаг-int заблокировал бы миграцию навсегда.
    static readonly ConcurrentDictionary<string, byte> _jutHistMigrated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Прежняя общая история → бакет _shared, один раз на каталог. Идемпотентность держится
    /// на ФАКТЕ существования _shared, а не на флаге: слияние повторным проходом удвоило бы
    /// счётчики просмотров. Старый файл НЕ удаляем — это бесплатный откат на версию назад.
    /// ⚠️ Контракт: зовётся из JutHistoryRead, то есть всегда под _jutHistLock.
    /// </summary>
    static void JutHistoryMigrateOnce()
    {
        string dir = JutHistoryDir();
        if (_jutHistMigrated.ContainsKey(dir)) return;

        try
        {
            string shared = JutHistoryPath(JutSharedBucket);
            if (JsonStore.Exists(shared)) { _jutHistMigrated[dir] = 1; return; }

            var old = JsonStore.ReadObject(JutHistoryLegacyPath());
            if (old != null)
            {
                old["at"] = DateTime.UtcNow;
                JsonStore.WriteNow(shared, old);                // сразу на диск: по нему считают устройства
                JsonStore.ForgetDir(dir);
                JutNet.Log("history", "общая история перенесена в " + JutSharedBucket);
            }
            _jutHistMigrated[dir] = 1;
        }
        catch (Exception ex) { JutNet.Log("history", "миграция: " + ex.Message); }
    }

    static JObject JutHistoryRead(string bucket)
    {
        JutHistoryMigrateOnce();
        var jo = JsonStore.ReadObject(JutHistoryPath(bucket)) ?? new JObject();
        if (jo["watched"] is not JObject) jo["watched"] = new JObject();
        if (jo["searched"] is not JObject) jo["searched"] = new JObject();
        return jo;
    }

    static void JutHistoryWrite(string bucket, JObject jo)
    {
        string path = JutHistoryPath(bucket);
        bool isNew = !JsonStore.Exists(path);
        jo["at"] = DateTime.UtcNow;                             // метка свежести устройства для прунинга

        if (isNew)
        {
            // 🔴 WriteNow, а не Write: обычная запись кладёт значение в РАМ и ставит диск
            // в очередь на 200 мс, а прунинг считает устройства по ДИСКОВОМУ листингу —
            // новый бакет он бы систематически не видел. Раз в жизни устройства это дёшево.
            JsonStore.WriteNow(path, jo);
            JsonStore.ForgetDir(JutHistoryDir());               // листинг каталога кешируется
            JutHistoryPruneDevicesAsync();
        }
        else JsonStore.Write(path, jo);
    }

    /// <summary>
    /// Держим JutHistDevCap самых свежих устройств; _shared не вытесняется никогда.
    /// ⚠️ Фоном и ВНЕ _jutHistLock: инвариант JsonStore — «диск не читается/пишется под локом,
    /// который держит запрос», а этот лок держит /qdl/jut/stream на каждом seek.
    /// </summary>
    static void JutHistoryPruneDevicesAsync() => _ = Task.Run(() =>
    {
        try
        {
            string dir = JutHistoryDir();
            var files = JsonStore.List(dir, "*.json")
                .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f), JutSharedBucket,
                                           StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count <= JutHistDevCap) return;

            // Возраст берём из поля at ВНУТРИ файла, а не из mtime: на drvfs-зеркале и на томе
            // они расходятся, да и диск пишется с задержкой — mtime тут не источник истины.
            var stale = files
                .Select(f => (f, at: JsonStore.ReadObject(f)?["at"]?.Value<DateTime?>() ?? DateTime.MinValue))
                .OrderByDescending(x => x.at)
                .Skip(JutHistDevCap)
                .ToList();

            foreach (var (f, _) in stale) JsonStore.Remove(f);
            if (stale.Count > 0)
            {
                JsonStore.ForgetDir(dir);
                JutNet.Log("history", "забыто устройств: " + stale.Count);
            }
        }
        catch (Exception ex) { JutNet.Log("history", "прунинг устройств: " + ex.Message); }
    });

    /// <summary>Обрезка секции до капа: держим самые свежие по at.</summary>
    static void JutHistoryPrune(JObject section)
    {
        if (section.Count <= JutHistCap) return;

        var keep = section.Properties()
                          .OrderByDescending(p => p.Value?["at"]?.Value<DateTime?>() ?? DateTime.MinValue)
                          .Take(JutHistCap)
                          .Select(p => p.Name)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string name in section.Properties().Select(p => p.Name).ToList())
            if (!keep.Contains(name)) section.Remove(name);
    }

    /// <summary>
    /// Отметка просмотра. Зовётся из /qdl/jut/stream — то есть срабатывает одинаково
    /// для веб-плеера и для всех нативных, включая Android, который о своих переключениях
    /// серий вебу вообще не рассказывает.
    /// </summary>
    internal static void JutHistoryTouchWatch(string slug, string uid)
    {
        if (ReplicaMode) return;                            // реплика в дом не пишет
        if (!JutSuParse.IsValidSlug(slug)) return;

        string bucket = JutHistoryBucket(uid);
        var now = DateTime.UtcNow;
        string dedupKey = bucket + "|" + slug;
        if (_jutWatchSeen.TryGetValue(dedupKey, out var seen) && (now - seen).TotalSeconds < JutWatchDedupSec)
            return;
        _jutWatchSeen[dedupKey] = now;

        // Ключей теперь «устройства × слаги» — чистим протухшие (образец: JutPrewarmClaim).
        if (_jutWatchSeen.Count > 512)
        {
            foreach (var kv in _jutWatchSeen)
                if ((now - kv.Value).TotalSeconds > JutWatchDedupSec) _jutWatchSeen.TryRemove(kv.Key, out _);
        }

        try
        {
            lock (_jutHistLock)
            {
                var jo = JutHistoryRead(bucket);
                var watched = (JObject)jo["watched"];
                int count = watched[slug]?["count"]?.Value<int>() ?? 0;
                watched[slug] = new JObject { ["at"] = now, ["count"] = count + 1 };
                JutHistoryPrune(watched);
                JutHistoryWrite(bucket, jo);
            }
        }
        catch (Exception ex) { JutNet.Log("history", "watch: " + ex.Message); }
    }

    /// <summary>Первые карточки поисковой выдачи — чтобы экрану поиска было чем заполниться.</summary>
    static void JutHistoryRecordSearch(JToken items, string uid, int take = 12)
    {
        if (ReplicaMode) return;
        if (items is not JArray arr || arr.Count == 0) return;

        string bucket = JutHistoryBucket(uid);

        try
        {
            lock (_jutHistLock)
            {
                var jo = JutHistoryRead(bucket);
                var searched = (JObject)jo["searched"];
                var now = DateTime.UtcNow;

                foreach (var it in arr.Take(take))
                {
                    string slug = it?["slug"]?.Value<string>();
                    if (string.IsNullOrEmpty(slug) || !JutSuParse.IsValidSlug(slug)) continue;
                    searched[slug] = new JObject { ["at"] = now };
                }

                JutHistoryPrune(searched);
                JutHistoryWrite(bucket, jo);
            }
        }
        catch (Exception ex) { JutNet.Log("history", "search: " + ex.Message); }
    }

    #endregion

    #region выдача экрана поиска

    /// <summary>
    /// Карточка по слагу из того, что уже есть на сервере: сперва кеш тайтла (он точнее и
    /// почти всегда прогрет — тайтл открывают перед просмотром), затем снапшот-индекс
    /// каталога. Не нашлось ничего — отдаём голый слаг: постер и название подтянутся,
    /// когда карточку откроют, а строку выдачи терять нельзя.
    /// </summary>
    static JObject JutHistoryCard(string slug, string src)
    {
        var title = JutCacheRead("title", slug, TimeSpan.MaxValue, out _);
        if (title != null)
        {
            return new JObject
            {
                ["slug"] = slug,
                ["id"] = title["id"],
                ["title"] = title["title"],
                ["original"] = title["original"],
                ["descr"] = title["descr"],
                ["episodes"] = title["count"],
                ["ongoing"] = title["ongoing"],
                ["years"] = title["years"] ?? new JArray(),
                ["genres"] = title["genres"] ?? new JArray(),
                ["src"] = src
            };
        }

        var card = JutIdxFindCard(slug);
        if (card != null)
        {
            var c = (JObject)card.DeepClone();
            c["src"] = src;
            return c;
        }

        return new JObject { ["slug"] = slug, ["title"] = slug, ["src"] = src };
    }

    /// <summary>Сброс дедуп-окна просмотров. Только для тестов: в бою окно живёт 5 минут.</summary>
    internal static void JutHistoryResetForTests()
    {
        _jutWatchSeen.Clear();
        _jutHistMigrated.Clear();
    }

    /// <summary>
    /// Топ последних тайтлов устройства: сперва просмотренные (свежие выше), затем добор из
    /// поисковых выдач. Своего мало — добираем из общего бакета, иначе новое устройство
    /// увидело бы пустой экран. Вынесено из роута, чтобы порядок и дедуп проверялись тестами.
    /// </summary>
    static JObject JutRecentPayload(int limit, string uid)
    {
        string bucket = JutHistoryBucket(uid);

        JObject own, shared = null;
        lock (_jutHistLock)
        {
            own = JutHistoryRead(bucket);
            if (bucket != JutSharedBucket) shared = JutHistoryRead(JutSharedBucket);
        }

        static IEnumerable<(string slug, DateTime at)> Rows(JToken section)
            => (section as JObject)?.Properties()
                   .Select(p => (p.Name, p.Value?["at"]?.Value<DateTime?>() ?? DateTime.MinValue))
                   .OrderByDescending(x => x.Item2)
               ?? Enumerable.Empty<(string, DateTime)>();

        var items = new JArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Take(JToken section, string src)
        {
            foreach (var (slug, _) in Rows(section))
            {
                if (items.Count >= limit) return;
                if (!seen.Add(slug)) continue;
                items.Add(JutHistoryCard(slug, src));
            }
        }

        // Своё устройство — вперёд: просмотренное, затем искомое (требование владельца).
        Take(own["watched"], "watch");
        Take(own["searched"], "search");

        // Добор из общего бакета — то, что накопилось до разделения истории и от клиентов без uid.
        if (shared != null)
        {
            Take(shared["watched"], "watch");
            Take(shared["searched"], "search");
        }

        return new JObject
        {
            ["ok"] = true,
            ["page"] = 1,
            ["hasNext"] = false,
            ["total"] = items.Count,
            ["items"] = items
        };
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/recent")]
    public ActionResult JutRecent(int limit = 50)
    {
        if (!JutOn) return JutErr("DISABLED");

        // JutJsonArt — постеры получают версию и посев апгрейда, как в каталоге.
        return JutJsonArt(JutRecentPayload(Math.Clamp(limit, 1, 100), requestInfo?.user_uid));
    }

    #endregion
}
