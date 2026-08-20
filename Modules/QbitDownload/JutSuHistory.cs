using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Память экрана поиска jut.su: что смотрели и что искали.
//
// Зачем на СЕРВЕРЕ, а не в Lampa.Storage: владелец хочет одинаковую выдачу на всех
// устройствах (телефон, ТВ, мак, винда). Сервер тут единственная общая точка, и он
// уже знает оба события без единой строчки в клиенте:
//   • «смотрел» — по факту байтов через /qdl/jut/stream (любая платформа, любой плеер);
//   • «искал»   — по выдаче /qdl/jut/search.
// Понятия пользователя в модуле нет (все /qdl/* анонимны) — история одна на сервер.
// Дом у владельца один, это осознанно.
//
// Порядок в выдаче: сначала просмотренное (свежее выше), потом добор из поисковых
// выдач. Правило владельца: «если просмотренных нет — собирать топ из того, что
// прилетело из поиска».
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region хранилище

    const int JutHistCap = 200;        // на каждую из двух секций
    const int JutWatchDedupSec = 300;  // seek-переоткрытия стрима не должны спамить записью

    static string JutHistoryPath() => Path.Combine(JutNet.JutDataDir(), "history.json");

    // Дедуп записи «смотрел»: плеер за серию открывает поток многократно (seek, докачка).
    static readonly ConcurrentDictionary<string, DateTime> _jutWatchSeen = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _jutHistLock = new();

    static JObject JutHistoryRead()
    {
        var jo = JsonStore.ReadObject(JutHistoryPath()) ?? new JObject();
        if (jo["watched"] is not JObject) jo["watched"] = new JObject();
        if (jo["searched"] is not JObject) jo["searched"] = new JObject();
        return jo;
    }

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
    internal static void JutHistoryTouchWatch(string slug)
    {
        if (ReplicaMode) return;                            // реплика в дом не пишет
        if (!JutSuParse.IsValidSlug(slug)) return;

        var now = DateTime.UtcNow;
        if (_jutWatchSeen.TryGetValue(slug, out var seen) && (now - seen).TotalSeconds < JutWatchDedupSec)
            return;
        _jutWatchSeen[slug] = now;

        try
        {
            lock (_jutHistLock)
            {
                var jo = JutHistoryRead();
                var watched = (JObject)jo["watched"];
                int count = watched[slug]?["count"]?.Value<int>() ?? 0;
                watched[slug] = new JObject { ["at"] = now, ["count"] = count + 1 };
                JutHistoryPrune(watched);
                JsonStore.Write(JutHistoryPath(), jo);
            }
        }
        catch (Exception ex) { JutNet.Log("history", "watch: " + ex.Message); }
    }

    /// <summary>Первые карточки поисковой выдачи — чтобы экрану поиска было чем заполниться.</summary>
    static void JutHistoryRecordSearch(JToken items, int take = 12)
    {
        if (ReplicaMode) return;
        if (items is not JArray arr || arr.Count == 0) return;

        try
        {
            lock (_jutHistLock)
            {
                var jo = JutHistoryRead();
                var searched = (JObject)jo["searched"];
                var now = DateTime.UtcNow;

                foreach (var it in arr.Take(take))
                {
                    string slug = it?["slug"]?.Value<string>();
                    if (string.IsNullOrEmpty(slug) || !JutSuParse.IsValidSlug(slug)) continue;
                    searched[slug] = new JObject { ["at"] = now };
                }

                JutHistoryPrune(searched);
                JsonStore.Write(JutHistoryPath(), jo);
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
    internal static void JutHistoryResetForTests() => _jutWatchSeen.Clear();

    /// <summary>
    /// Топ последних тайтлов: сперва просмотренные (свежие выше), затем добор из поисковых
    /// выдач. Вынесено из роута, чтобы порядок и дедуп проверялись тестами напрямую.
    /// </summary>
    static JObject JutRecentPayload(int limit)
    {
        JObject jo;
        lock (_jutHistLock) jo = JutHistoryRead();

        static IEnumerable<(string slug, DateTime at)> Rows(JToken section)
            => (section as JObject)?.Properties()
                   .Select(p => (p.Name, p.Value?["at"]?.Value<DateTime?>() ?? DateTime.MinValue))
                   .OrderByDescending(x => x.Item2)
               ?? Enumerable.Empty<(string, DateTime)>();

        var items = new JArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Просмотренное — вперёд и по свежести (требование владельца).
        foreach (var (slug, _) in Rows(jo["watched"]))
        {
            if (items.Count >= limit) break;
            if (!seen.Add(slug)) continue;
            items.Add(JutHistoryCard(slug, "watch"));
        }

        // Добор из поисковых выдач — тем же порядком свежести.
        foreach (var (slug, _) in Rows(jo["searched"]))
        {
            if (items.Count >= limit) break;
            if (!seen.Add(slug)) continue;
            items.Add(JutHistoryCard(slug, "search"));
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
        return JutJsonArt(JutRecentPayload(Math.Clamp(limit, 1, 100)));
    }

    #endregion
}
