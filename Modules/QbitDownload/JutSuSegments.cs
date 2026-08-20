using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// «Автопилот» jut.su: границы опенинга для плеера + прогрев следующей серии.
//
// Опенинг. Сайт сам размечает заставку переменными video_intro_start/video_intro_end
// (их читает его же кнопка «Пропустить заставку»), парсер достаёт их вместе с outro.
// Здесь эти секунды превращаются в сегмент для плеера:
//   web-Lampa   — data.segments = {skip:[{start,end}]}, модуль Segments скипает сам;
//   нативы      — то же поле в элементе плейлиста (mac/win/Android).
// 🔥 duration_ms в segments НЕ кладём: web-бандл при известной длительности «подгоняет»
// метки под свою эвристику, а наши секунды точны для этого самого файла.
//
// Кеш сегментов отдельный от кеша ссылок: ссылка живёт 240 с (TTL токена CDN), а разметка
// серии не меняется никогда — держим её 30 дней, чтобы переключение на 20-й минуте серии
// не требовало повторной загрузки HTML.
//
// Прогрев. Дорогая часть старта серии — резолв (полная загрузка HTML-страницы, ~1 с).
// Триггер — не старт серии, а её ПРОГРЕСС (jutPrewarmAtPercent): на старте прогревать
// бессмысленно, ссылка протухнет за 240 с задолго до автоперехода. Работает для всех
// платформ разом: сервер видит байты и для веба, и для нативных плееров, а Android,
// например, о переключении серии вообще не сообщает.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Канарейка на смену формата страницы jut.su: если сайт переименует переменные,
/// intro тихо исчезнет у всех серий. Счётчики видны в /qdl/jut/diag.
/// </summary>
public static class JutSegCounters
{
    static long _seen, _miss;

    public static void Note(bool hasIntro)
    {
        if (hasIntro) Interlocked.Increment(ref _seen);
        else Interlocked.Increment(ref _miss);
    }

    public static (long seen, long miss) State() => (Interlocked.Read(ref _seen), Interlocked.Read(ref _miss));
}

public partial class QbitController
{
    #region сегменты серии

    static readonly TimeSpan JutSegTtl = TimeSpan.FromDays(30);

    static string JutSegKey(string slug, int season, int ep, string kind)
        => slug + "-s" + season.ToString(CultureInfo.InvariantCulture)
                + "e" + ep.ToString(CultureInfo.InvariantCulture)
                + "-" + (string.IsNullOrEmpty(kind) ? "episode" : kind);

    /// <summary>Разметка серии в форме, готовой и для web-Lampa, и для нативных плееров.</summary>
    static JObject JutSegJson(JutLink link)
    {
        var skip = new JArray();
        if (link.hasIntro)
            skip.Add(new JObject
            {
                ["start"] = link.introStart,
                ["end"] = link.introEnd,
                ["name"] = "intro"
            });

        return new JObject
        {
            ["ok"] = true,
            ["slug"] = link.slug,
            ["season"] = link.season,
            ["ep"] = link.ep,
            ["kind"] = link.kind,
            ["duration"] = link.duration,
            ["intro_start"] = link.hasIntro ? link.introStart : (JToken)JValue.CreateNull(),
            ["intro_end"] = link.hasIntro ? link.introEnd : (JToken)JValue.CreateNull(),
            ["outro"] = link.outro,
            // 🔥 без duration_ms — см. шапку файла
            ["segments"] = new JObject { ["skip"] = skip }
        };
    }

    /// <summary>
    /// Кладёт разметку в кеш. Зовётся из любого места, где ссылка только что зарезолвилась
    /// (resolve, стрим, прогрев) — тогда сегменты переживают TTL самой ссылки.
    /// </summary>
    static void JutSegStore(JutLink link)
    {
        if (link == null || link.error != null || string.IsNullOrEmpty(link.slug)) return;
        try { JutCacheWrite("seg", JutSegKey(link.slug, link.season, link.ep, link.kind), JutSegJson(link)); }
        catch { }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/jut/segments")]
    async public Task<ActionResult> JutSegments(string t)
    {
        if (!JutOn) return JutErr("DISABLED");

        // ParseToken проверяет подпись и валидность slug — отдельный гейт не нужен.
        var seed = JutNet.ParseToken(t);
        if (seed == null) return JutErr("NOT_FOUND");

        var cached = JutCacheRead("seg", JutSegKey(seed.slug, seed.season, seed.ep, seed.kind), JutSegTtl, out bool stale);
        if (cached != null && !stale) return JutJson(cached);

        // Промах — резолвим по горячему пути (клиент ждёт его перед переключением серии).
        // Single-flight внутри EnsureLink склеит параллельные запросы.
        var link = await JutNet.EnsureLink(t, force: false, HttpContext.RequestAborted);
        if (link == null) return JutErr("NOT_FOUND");
        if (link.error != null)
        {
            // Протухшая разметка лучше пустоты: сама-то серия не менялась.
            if (cached != null) { cached["stale"] = true; return JutJson(cached); }
            return JutErr(link.error);
        }

        JutSegStore(link);
        return JutJson(JutSegJson(link));
    }

    #endregion

    #region прогрев следующей серии

    // Дедуп: у одной серии прогресс перевалит порог один раз, но seek-переоткрытия
    // и второй зритель того же тайтла не должны множить фоновые резолвы.
    static readonly ConcurrentDictionary<string, DateTime> _jutPrewarmed = new();
    const int PrewarmDedupSec = 180;

    static bool JutPrewarmClaim(string token)
    {
        var now = DateTime.UtcNow;
        if (_jutPrewarmed.TryGetValue(token, out var at) && (now - at).TotalSeconds < PrewarmDedupSec)
            return false;

        _jutPrewarmed[token] = now;
        if (_jutPrewarmed.Count > 256)
        {
            foreach (var kv in _jutPrewarmed)
                if ((now - kv.Value).TotalSeconds > PrewarmDedupSec) _jutPrewarmed.TryRemove(kv.Key, out _);
        }
        return true;
    }

    /// <summary>
    /// Токен следующей серии по кешу тайтла. «Следующая» — тот же kind и тот же сезон,
    /// минимальный ep больше текущего: ровно так клиент строит плейлист, кросс-сезонного
    /// перехода у него нет. Нет кеша тайтла (его открывают перед просмотром, плюс держит
    /// тёплым JutWarmTitles) — молча пропускаем, прогрев необязателен.
    /// </summary>
    /// <summary>
    /// kind из JSON тайтла → kind токена. ⚠️ В JSON лежит имя enum'а (gameova), а в токене
    /// и в параметрах роутов — дефисный вид (game-ova); без нормализации игровые OVA
    /// молча не находились бы.
    /// </summary>
    static string JutKindNorm(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return "episode";
        return kind.ToLowerInvariant() switch
        {
            "film" => "film",
            "ova" => "ova",
            "gameova" or "game-ova" => "game-ova",
            "special" => "special",
            _ => "episode"
        };
    }

    static string JutNextToken(JutLink cur)
    {
        if (cur == null || string.IsNullOrEmpty(cur.slug)) return null;

        var title = JutCacheRead("title", cur.slug, TimeSpan.MaxValue, out _);
        var items = title?["items"] as JArray;
        if (items == null || items.Count == 0) return null;

        string curKind = JutKindNorm(cur.kind);

        JToken best = null;
        int bestEp = int.MaxValue;
        foreach (var it in items)
        {
            string kind = JutKindNorm(it["kind"]?.Value<string>());
            if (!string.Equals(kind, curKind, StringComparison.OrdinalIgnoreCase)) continue;
            if ((it["season"]?.Value<int>() ?? 1) != cur.season) continue;

            int ep = it["ep"]?.Value<int>() ?? 0;
            if (ep <= cur.ep || ep >= bestEp) continue;

            bestEp = ep;
            best = it;
        }

        if (best == null) return null;

        // Токен из кеша тайтла (подпись детерминирована и переживает рестарт), иначе строим сами.
        string tok = best["tok"]?.Value<string>();
        return !string.IsNullOrEmpty(tok)
            ? tok
            : JutNet.MakeToken(cur.slug, cur.season, bestEp, curKind, cur.quality);
    }

    /// <summary>
    /// Фоновый прогрев следующей серии: резолв ссылки (главная задержка старта) + разметка
    /// опенинга в кеш + опционально первые килобайты у CDN, чтобы прогреть их edge.
    /// 🔥 Строго под BackgroundScope: иначе фон отберёт слоты интерактивного гейта
    /// (jutMaxConcurrent=3) у карточки, которую зритель открывает прямо сейчас.
    /// </summary>
    static void JutPrewarmNext(JutLink cur)
    {
        if (cur == null || cur.error != null) return;
        if (ModInit.conf?.jutPrewarmNext != true) return;
        if (ReplicaMode) return;

        string next = JutNextToken(cur);
        if (string.IsNullOrEmpty(next)) return;
        if (!JutPrewarmClaim(next)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var bg = JutNet.BackgroundScope();

                var link = await JutNet.EnsureLink(next);
                if (link == null || link.error != null)
                {
                    JutNet.Log("prewarm", "не вышло: " + cur.slug + " → " + (link?.error ?? "NOT_FOUND"));
                    return;
                }

                JutSegStore(link);
                JutNet.Log("prewarm", "готова " + link.slug + " " + link.season + "x" + link.ep
                                    + (link.hasIntro ? " (интро " + link.introStart + "–" + link.introEnd + ")" : ""));

                int kb = Math.Max(0, ModInit.conf?.jutPrewarmCdnKb ?? 2048);
                if (kb > 0) await JutPrewarmCdn(link, kb);
            }
            catch (Exception ex) { JutNet.Log("prewarm", ex.Message); }
        });
    }

    /// <summary>Первый Range к CDN: греет их edge, наш сервер байты выбрасывает.</summary>
    static async Task JutPrewarmCdn(JutLink link, int kb)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, link.url);
            req.Headers.TryAddWithoutValidation("User-Agent", JutNet.Ua);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (kb * 1024 - 1));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await JutNet.Media(link.exitId)
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[64 * 1024];
            while (await s.ReadAsync(buf, 0, buf.Length, cts.Token) > 0) { }
        }
        catch { }   // прогрев необязателен: не вышло — просто не вышло
    }

    #endregion
}
