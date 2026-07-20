using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// Донор покрывает ли серию — вердикт ТОЛЬКО по названию раздачи (до добавления в qBit).
public enum DonorCover { Yes, No, Maybe }

// ───────────────────────────────────────────────────────────────────────────────
// EpisodeHunter — охота за сериями по ВСЕМ раздачам («мини-Sonarr» уровня эпизодов).
//
// Основная раздача остаётся приоритетной (re-grab по смене infohash топика — в CheckWatches).
// Этот контур раз в episodeHuntIntervalHours ищет серии, которые вышли РАНЬШЕ на других
// раздачах: выбирает лучшего «донора» по скору (TorrentScoring), добавляет его в qBit в
// отдельной категории и качает ТОЛЬКО файл нужной серии (filePrio). Когда основная догоняет
// и докачивает свою версию — файл донора удаляется (ScanReplacements), опустевший донор
// снимается целиком. Доноры не видны в гриде «Загрузок», их серии попадают в общий плейлист
// сериала через /qdl/episodes.
//
// partial: доступ к private-хелперам Controller.cs (ParseEp/EpKey/Qbit()/watch.json и т.д.).
// ───────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    static string DonorCategory => string.IsNullOrWhiteSpace(ModInit.conf.donorCategory)
        ? ModInit.conf.category + "-donor" : ModInit.conf.donorCategory;

    #region qBit-хелперы (инъецируемый HttpClient — тестируются FakeQbit)
    // Расширенный add: категория/теги/остановка после метаданных (qBit >= 4.6 понимает stopCondition;
    // старые версии игнорируют — фолбэк в QbitWaitFiles). Разбор ответа — как в /qdl/add (v4/v5).
    static async Task<bool> QbitAddMagnetEx(HttpClient c, string magnet, string category, string tags = null, bool stopAfterMeta = false)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(magnet), "urls" },
            { new StringContent(ModInit.conf.downloadsPath), "savepath" },
            { new StringContent(category ?? ModInit.conf.category), "category" }
        };
        if (!string.IsNullOrWhiteSpace(tags)) content.Add(new StringContent(tags), "tags");
        if (stopAfterMeta) content.Add(new StringContent("MetadataReceived"), "stopCondition");

        var r = await c.PostAsync("/api/v2/torrents/add", content);
        string body = (await r.Content.ReadAsStringAsync())?.Trim() ?? "";
        if ((int)r.StatusCode == 409 || body.Equals("Conflict", StringComparison.OrdinalIgnoreCase)) return true;   // дубликат — работаем с существующим
        if (!r.IsSuccessStatusCode) return false;
        if (body == "Ok." || body.Length == 0) return true;
        if (body.StartsWith("{")) { try { var j = JObject.Parse(body); return (j.Value<int?>("success_count") ?? 0) > 0 || (j.Value<int?>("pending_count") ?? 0) > 0 || (j.Value<int?>("duplicate_count") ?? 0) > 0; } catch { return false; } }
        return false;
    }

    static async Task<JArray> QbitFiles(HttpClient c, string hash)
    {
        try
        {
            string raw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
            return JArray.Parse(raw);
        }
        catch { return null; }
    }

    static async Task<JObject> QbitTorrentInfo(HttpClient c, string hash)
    {
        try
        {
            var arr = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?hashes={HttpUtility.UrlEncode(hash)}"));
            return arr.Count > 0 ? arr[0] as JObject : null;
        }
        catch { return null; }
    }

    // Ждём метаданные magnet-донора (список файлов). Фолбэк для qBit без stopCondition:
    // остановленный торрент мету не тянет — через 20 с без файлов стартуем его сами.
    static async Task<JArray> QbitWaitFiles(HttpClient c, string hash, int timeoutSec)
    {
        var start = DateTime.UtcNow;
        bool kicked = false;
        while ((DateTime.UtcNow - start).TotalSeconds < Math.Max(10, timeoutSec))
        {
            var files = await QbitFiles(c, hash);
            if (files != null && files.Count > 0) return files;

            if (!kicked && (DateTime.UtcNow - start).TotalSeconds > 20)
            {
                kicked = true;
                var info = await QbitTorrentInfo(c, hash);
                string st = info?.Value<string>("state") ?? "";
                if (st.StartsWith("stopped", StringComparison.OrdinalIgnoreCase) || st.StartsWith("paused", StringComparison.OrdinalIgnoreCase))
                    await QbitStartTorrent(c, hash);
            }
            await Task.Delay(3000);
        }
        return null;
    }

    static async Task<bool> QbitFilePrio(HttpClient c, string hash, IEnumerable<int> ids, int prio)
    {
        string list = string.Join("|", ids ?? Enumerable.Empty<int>());
        if (list.Length == 0) return true;
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("hash", hash),
            new KeyValuePair<string, string>("id", list),
            new KeyValuePair<string, string>("priority", prio.ToString())
        });
        var r = await c.PostAsync("/api/v2/torrents/filePrio", form);
        return r.IsSuccessStatusCode;
    }

    static async Task QbitStartTorrent(HttpClient c, string hash)
    {
        FormUrlEncodedContent form() => new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("hashes", hash) });
        var r = await c.PostAsync("/api/v2/torrents/start", form());   // qBit v5
        if (!r.IsSuccessStatusCode)
            await c.PostAsync("/api/v2/torrents/resume", form());      // фолбэк v4
    }

    static async Task QbitDelete(HttpClient c, string hash, bool deleteFiles)
    {
        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")
            });
            await c.PostAsync("/api/v2/torrents/delete", form);
        }
        catch { }
    }

    static async Task<string> QbitCategory(HttpClient c, string hash)
        => (await QbitTorrentInfo(c, hash))?.Value<string>("category");

    // Удалить донора С ФАЙЛАМИ, но ТОЛЬКО если он реально в донорской категории. Защита от катастрофы:
    // если из-за коллизии infohash в watch.donors просочилась пользовательская загрузка (категория lampa)
    // или чужой торрент — мы НЕ удаляем его файлы. Никогда не delete-with-files вслепую по записи донора.
    static async Task QbitDeleteDonorSafe(HttpClient c, string hash)
    {
        if (!ValidHash(hash)) return;
        string cat = await QbitCategory(c, hash);
        if (cat == null) return;   // торрента уже нет — нечего удалять
        if (cat == DonorCategory) { await QbitDelete(c, hash, true); return; }
        Console.WriteLine("[QbitDownload] hunt: НЕ удаляю " + hash + " с файлами — категория «" + cat + "» не донорская");
    }
    #endregion

    #region чистая логика охоты (без IO — тестируется напрямую через Access)
    sealed class HuntCtx
    {
        public string mainHash;
        public int season;
        public HashSet<string> knownHashes;    // основная + текущие доноры (lower)
        public HashSet<string> blacklistKeys;  // btih/parselink активного blacklist
        public int minSeeds, minQuality, minMb, maxGb;
        public string titleNorm, originalNorm;  // нормализованные названия сериала для строгого гейта имени
    }

    sealed class EpFile { public int index; public int ep; public int season; public string epkey; public long size; public string name; }

    static long EstimateEpBytes(long sizeBytes, int haveCount) => haveCount > 0 ? sizeBytes / haveCount : sizeBytes;
    static bool EpSizeOk(long estBytes, int minMb, int maxGb) => estBytes >= minMb * 1024L * 1024 && estBytes <= maxGb * 1024L * 1024 * 1024;

    // ParseEp писан под ИМЕНА ФАЙЛОВ — на названии раздачи голая цифра сезона («2 сезон», «S02»)
    // ложно читается как номер серии. Перед эпизодным парсингом тайтла сезонные маркеры вырезаем
    // (S02E05 — одиночная серия — выживает: лукахед не даёт срезать S с E-хвостом).
    static readonly System.Text.RegularExpressions.Regex[] _seasonMarkRx =
    {
        new System.Text.RegularExpressions.Regex(@"(?i)(\d{1,2}\s*-\s*)?\d{1,2}\s*сезон\w*", System.Text.RegularExpressions.RegexOptions.Compiled),
        new System.Text.RegularExpressions.Regex(@"(?i)сезон\w*\s*[:№#]?\s*\d{1,2}(\s*-\s*\d{1,2})?", System.Text.RegularExpressions.RegexOptions.Compiled),
        new System.Text.RegularExpressions.Regex(@"(?i)season\s*\d{1,2}(\s*-\s*\d{1,2})?", System.Text.RegularExpressions.RegexOptions.Compiled),
        new System.Text.RegularExpressions.Regex(@"(?i)(?<![A-Za-z0-9])S\d{1,2}(\s*-\s*S?\d{1,2})?(?!E?\d)", System.Text.RegularExpressions.RegexOptions.Compiled),
    };
    static string StripSeasonMarks(string t)
    {
        foreach (var rx in _seasonMarkRx) t = rx.Replace(t, " ");
        return t;
    }

    // Tri-state «раздача содержит серию ep сезона season» по одному только названию.
    // Yes — «N из M» с N>=ep, одиночная «Серия ep», диапазон ∋ ep; No — явно нет; Maybe — сезонник без счётчика.
    static DonorCover TitleCoversEp(string title, int season, int ep)
    {
        string t = title ?? "";
        var seasons = TorrentScoring.ParseSeasons(t);
        if (season > 0 && seasons.Count > 0 && !seasons.Contains(season)) return DonorCover.No;

        var cov = TorrentScoring.ParseEpCoverage(t);
        if (cov != null && cov.have > 0) return cov.have >= ep ? DonorCover.Yes : DonorCover.No;

        var pe = ParseEp(StripSeasonMarks(t));
        if (pe != null && pe.any && pe.kind == "RANGE" && pe.ep2 >= pe.ep)
            return (ep >= pe.ep && ep <= pe.ep2) ? DonorCover.Yes : DonorCover.No;
        if (pe != null && pe.any && pe.kind == null && pe.ep >= 0)
        {
            if (pe.season >= 0 && season > 0 && pe.season != season) return DonorCover.No;
            return pe.ep == ep ? DonorCover.Yes : DonorCover.No;
        }
        return DonorCover.Maybe;
    }

    // СТРОГИЙ гейт имени для донора: раздача — это ТОТ ЖЕ сериал (а не однофамилец).
    // Обычный скоринг матчит Contains по ПОЛНОМУ названию → «Счастливчик Люк / Лаки Люк / Lucky Luke»
    // прошёл бы для запроса «Лаки». Для автодонора этого мало (лишний рип — не беда, ЧУЖОЙ сериал — беда):
    // разбиваем название по «/» и «|», из каждого сегмента срезаем сезон/год/скобки и требуем ТОЧНОГО
    // равенства нормализованного сегмента названию сериала (рус ИЛИ ориг). «Счастливчик Люк»/«Лаки Люк»/
    // «Lucky Luke» → «счастливчиклюк»/«лакилюк»/«luckyluke» ≠ «лаки»/«lucky» → отсев. «Лаки / Lucky» → совпало.
    static readonly System.Text.RegularExpressions.Regex _yearInName = new System.Text.RegularExpressions.Regex(@"(?i)(19|20)\d{2}", System.Text.RegularExpressions.RegexOptions.Compiled);
    static bool NameMatchesSeries(string title, string titleNorm, string originalNorm)
    {
        if (string.IsNullOrEmpty(titleNorm) && string.IsNullOrEmpty(originalNorm)) return true;   // нет контекста имён — не гейтим
        foreach (var raw in (title ?? "").Split('/', '|'))
        {
            string seg = raw;
            int b = seg.IndexOfAny(new[] { '[', '(' });
            if (b >= 0) seg = seg.Substring(0, b);
            seg = _yearInName.Replace(StripSeasonMarks(seg), " ");
            string n = Shared.Services.Utilities.SearchNameTo.Convert(seg);
            if (string.IsNullOrEmpty(n)) continue;
            if ((!string.IsNullOrEmpty(titleNorm) && n == titleNorm) ||
                (!string.IsNullOrEmpty(originalNorm) && n == originalNorm)) return true;
        }
        return false;
    }

    // Жёсткие гейты кандидата-донора: имя (строго!)/сиды/качество/сезон/вес серии/не-свои/не-blacklist.
    static List<JObject> FilterDonorCandidates(JArray scored, HuntCtx h)
    {
        var res = new List<JObject>();
        foreach (var t in scored.OfType<JObject>())
        {
            string title = t.Value<string>("title") ?? "";
            if (!NameMatchesSeries(title, h.titleNorm, h.originalNorm)) continue;   // ЧУЖОЙ сериал (коллизия имени) — не донор
            if ((t.Value<int?>("sid") ?? 0) < h.minSeeds) continue;

            int q = t.Value<int?>("quality") ?? 0;
            if (h.minQuality > 0 && q > 0 && q < h.minQuality) continue;   // 0 в тайтле = неизвестно → пропускаем гейт

            string btih = MagnetHash(t.Value<string>("magnet"));
            string parselink = t.Value<string>("parselink");
            if (!string.IsNullOrEmpty(btih) && (h.knownHashes.Contains(btih) || h.blacklistKeys.Contains(btih))) continue;
            if (!string.IsNullOrWhiteSpace(parselink) && h.blacklistKeys.Contains(parselink)) continue;
            if (string.IsNullOrEmpty(btih) && string.IsNullOrWhiteSpace(parselink)) continue;

            var seasons = TorrentScoring.ParseSeasons(title);
            if (h.season > 0 && seasons.Count > 0 && !seasons.Contains(h.season)) continue;
            if (h.season > 1 && seasons.Count == 0) continue;   // охотим не первый сезон, а раздача сезон не заявляет — риск не тот

            // оценка веса одной серии по названию (точная проверка — после метаданных, по самому файлу)
            long sizeBytes = t.Value<long?>("sizeBytes") ?? 0;
            var cov = TorrentScoring.ParseEpCoverage(title);
            int haveCount = cov?.have ?? 0;
            if (haveCount == 0)
            {
                var pe = ParseEp(StripSeasonMarks(title));
                if (pe != null && pe.any && pe.kind == "RANGE" && pe.ep2 >= pe.ep) haveCount = pe.ep2 - pe.ep + 1;
                else if (pe != null && pe.any && pe.kind == null && pe.ep >= 0) haveCount = 1;
            }
            if (sizeBytes > 0 && haveCount > 0 && !EpSizeOk(EstimateEpBytes(sizeBytes, haveCount), h.minMb, h.maxGb)) continue;

            res.Add(t);
        }
        return res;
    }

    // порядок проб: сперва уверенные Yes (по скору), затем Maybe-пробы «вслепую» (по скору); No — вон
    static List<JObject> OrderByCover(List<JObject> eligible, int season, List<int> wanted)
    {
        var yes = new List<JObject>(); var maybe = new List<JObject>();
        foreach (var t in eligible)
        {
            string title = t.Value<string>("title") ?? "";
            var best = DonorCover.No;
            foreach (int ep in wanted)
            {
                var cv = TitleCoversEp(title, season, ep);
                if (cv == DonorCover.Yes) { best = DonorCover.Yes; break; }
                if (cv == DonorCover.Maybe) best = DonorCover.Maybe;
            }
            if (best == DonorCover.Yes) yes.Add(t);
            else if (best == DonorCover.Maybe) maybe.Add(t);
        }
        int score(JObject t) => (int)Math.Round(t.Value<double?>("score") ?? 0);
        yes.Sort((a, b) => score(b).CompareTo(score(a)));
        maybe.Sort((a, b) => score(b).CompareTo(score(a)));
        yes.AddRange(maybe);
        return yes;
    }

    // серии, которые у нас уже есть: видеофайлы основной (с любым прогрессом — qBit их дотянет)
    // плюс серии живых доноров
    static HashSet<int> InventoryEps(JArray mainFiles, JArray donors, int season)
    {
        var inv = new HashSet<int>();
        foreach (var f in mainFiles ?? new JArray())
        {
            if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
            var e = ParseEp(BaseNoExt(f));
            if (e == null || !e.any) continue;
            if (e.kind == "RANGE" && e.ep >= 0 && e.ep2 >= e.ep)
                for (int i = e.ep; i <= Math.Min(e.ep2, e.ep + 400); i++) inv.Add(i);
            else if (e.kind == null && e.ep >= 0 && (e.season < 0 || season <= 0 || e.season == season))
                inv.Add(e.ep);
        }
        if (donors != null)
            foreach (var d in donors.OfType<JObject>())
                foreach (var e in (d["eps"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    int ep = e.Value<int?>("ep") ?? -1;
                    int es = e.Value<int?>("season") ?? -1;
                    if (ep >= 0 && (es <= 0 || season <= 0 || es == season)) inv.Add(ep);
                }
        return inv;
    }

    // охотим только ВПЕРЁД от максимума имеющегося: «дырки» в середине — чаще кривой парсинг имён
    static List<int> ComputeWanted(HashSet<int> inventory, int maxClaim)
    {
        int start = inventory.Count > 0 ? inventory.Max() : 0;
        var res = new List<int>();
        for (int e = start + 1; e <= Math.Min(maxClaim, start + 200); e++) res.Add(e);
        return res;
    }

    // максимум серий, который заявляют кандидаты («N из M» / одиночки / диапазоны)
    static int MaxClaim(List<JObject> eligible)
    {
        int max = 0;
        foreach (var t in eligible)
        {
            string title = t.Value<string>("title") ?? "";
            var cov = TorrentScoring.ParseEpCoverage(title);
            if (cov != null && cov.have > max) max = cov.have;
            var e = ParseEp(StripSeasonMarks(title));
            if (e != null && e.any)
            {
                if (e.kind == "RANGE" && e.ep2 > max) max = e.ep2;
                else if (e.kind == null && e.ep > max) max = e.ep;
            }
        }
        return max;
    }

    // Подтверждение по РЕАЛЬНЫМ файлам донора: какие из wanted-серий в нём есть.
    // RANGE-файлы отвергаем (серию из склейки адресно не сыграть); спецвыпуски не охотим.
    // Спецслучай: единственный видеофайл без номера + Title-одиночка → номер из Title.
    static List<EpFile> FindEpFiles(JArray files, int season, List<int> wanted, Ep titleEp)
    {
        var res = new List<EpFile>();
        var vids = new List<JToken>();
        foreach (var f in files ?? new JArray())
            if (_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) vids.Add(f);

        foreach (var f in vids)
        {
            var e = ParseEp(BaseNoExt(f));
            if (e == null || !e.any || e.kind != null || e.ep < 0) continue;
            if (e.season >= 0 && season > 0 && e.season != season) continue;
            if (!wanted.Contains(e.ep)) continue;
            if (res.Any(x => x.ep == e.ep)) continue;   // один файл на серию
            res.Add(new EpFile
            {
                index = f.Value<int?>("index") ?? -1,
                ep = e.ep,
                season = e.season >= 0 ? e.season : season,
                epkey = EpKey(e),
                size = f.Value<long?>("size") ?? 0,
                name = f.Value<string>("name")
            });
        }

        if (res.Count == 0 && vids.Count == 1 && titleEp != null && titleEp.any && titleEp.kind == null
            && titleEp.ep >= 0 && wanted.Contains(titleEp.ep))
        {
            var f = vids[0];
            var fe = ParseEp(BaseNoExt(f));
            if (fe == null || !fe.any || fe.ep < 0)
            {
                int s = titleEp.season >= 0 ? titleEp.season : season;
                var e = new Ep { season = s, ep = titleEp.ep };
                res.Add(new EpFile
                {
                    index = f.Value<int?>("index") ?? -1,
                    ep = titleEp.ep,
                    season = s,
                    epkey = EpKey(e),
                    size = f.Value<long?>("size") ?? 0,
                    name = f.Value<string>("name")
                });
            }
        }

        res.RemoveAll(x => x.index < 0);
        return res;
    }
    #endregion

    #region blacklist пустышек (per watch-запись, TTL)
    static void BlacklistAdd(JObject item, string btih, string parselink, string reason, int ttlDays)
    {
        if (item["blacklist"] is not JArray bl) { bl = new JArray(); item["blacklist"] = bl; }
        bl.Add(new JObject
        {
            ["btih"] = string.IsNullOrEmpty(btih) ? null : btih.ToLowerInvariant(),
            ["parselink"] = parselink,
            ["reason"] = reason,
            ["until"] = DateTime.UtcNow.AddDays(Math.Max(1, ttlDays)).ToString("o")
        });
    }

    static void PruneBlacklist(JObject item, DateTime now)
    {
        if (item["blacklist"] is not JArray bl) return;
        for (int i = bl.Count - 1; i >= 0; i--)
            if (!DateTime.TryParse(bl[i].Value<string>("until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u) || u < now)
                bl.RemoveAt(i);
    }

    static HashSet<string> BlacklistKeys(JObject item)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item["blacklist"] is JArray bl)
            foreach (var b in bl)
            {
                var h = b.Value<string>("btih"); if (!string.IsNullOrEmpty(h)) res.Add(h);
                var p = b.Value<string>("parselink"); if (!string.IsNullOrEmpty(p)) res.Add(p);
            }
        return res;
    }
    #endregion

    #region HuntAll / HuntOne — сама охота
    public static async Task<int> HuntAll(string onlyHash = null)
    {
        if (!ModInit.conf.episodeHunt) return 0;
        if (!await _watchGate.WaitAsync(0)) return 0;   // общий фоновый гейт (был _hunting): сериализуем с CheckWatches/ScanEpisodeNotifications
        int grabbed = 0;
        try
        {
            JArray list; HashSet<string> orig;
            lock (_watchLock) { list = LoadWatch(); orig = WatchHashes(list); }
            if (list.Count == 0) return 0;

            using var c = await Qbit();
            bool changed = false;
            foreach (var m in list.OfType<JObject>())
            {
                if (onlyHash != null && !onlyHash.Equals(m.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)) continue;
                try { grabbed += await HuntOne(c, m); changed = true; }   // HuntOne всегда обновляет hunt.lastRun
                catch (Exception ex) { Console.WriteLine("[QbitDownload] hunt item: " + ex); }
            }
            if (changed) SaveWatchReconciled(list, orig);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] hunt: " + ex); }
        finally { _watchGate.Release(); }
        return grabbed;
    }

    static void SetHuntStamp(JObject m, DateTime now, int maxClaim)
    {
        m["hunt"] = new JObject { ["lastRun"] = now.ToString("o"), ["lastMaxClaim"] = maxClaim };
    }

    // доминирующий сезон по видеофайлам (то, что реально качаем); 0 = не определить
    static int DominantSeason(JArray files)
    {
        var counts = new Dictionary<int, int>();
        foreach (var f in files ?? new JArray())
        {
            if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
            var e = ParseEp(BaseNoExt(f));
            if (e != null && e.any && e.kind == null && e.season > 0)
                counts[e.season] = counts.TryGetValue(e.season, out int n) ? n + 1 : 1;
        }
        return counts.Count > 0 ? counts.OrderByDescending(x => x.Value).First().Key : 0;
    }

    static async Task<int> HuntOne(HttpClient c, JObject m)
    {
        var conf = ModInit.conf;
        string mainHash = m.Value<string>("hash");
        if (!ValidHash(mainHash)) return 0;

        // только сериалы: ctx.is_serial==2, иначе media_type из меты
        var ctx = m["ctx"] as JObject;
        int cserial = ctx?.Value<int?>("is_serial") ?? -1;
        bool isSerial = cserial == 2;
        if (!isSerial && cserial < 0)
        {
            try
            {
                if (System.IO.File.Exists(MetaPath(mainHash)))
                    isSerial = JObject.Parse(System.IO.File.ReadAllText(MetaPath(mainHash))).Value<string>("media_type") == "tv";
            }
            catch { }
        }
        if (!isSerial) return 0;

        string ctitle = ctx?.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("query");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) return 0;

        var mainFiles = await QbitFiles(c, mainHash);
        if (mainFiles == null || mainFiles.Count == 0) return 0;   // основная сама ещё без метаданных

        var now = DateTime.UtcNow;
        PruneBlacklist(m, now);
        var donors = m["donors"] as JArray;

        int season = DominantSeason(mainFiles);
        if (season <= 0) season = Math.Max(1, ctx?.Value<int?>("season") ?? 1);

        var inv = InventoryEps(mainFiles, donors, season);

        if ((donors?.Count ?? 0) >= conf.donorMaxPerSeries) { SetHuntStamp(m, now, 0); return 0; }

        var scored = await SearchScored(ctitle, ctitle, ctx?.Value<string>("title_original"),
                                        ctx?.Value<int?>("year") ?? 0, 2, season, null);

        var h = new HuntCtx
        {
            mainHash = mainHash.ToLowerInvariant(),
            season = season,
            knownHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainHash },
            blacklistKeys = BlacklistKeys(m),
            minSeeds = conf.donorMinSeeds,
            minQuality = conf.donorMinQuality,
            minMb = conf.epSizeMinMb,
            maxGb = conf.epSizeMaxGb,
            titleNorm = Shared.Services.Utilities.SearchNameTo.Convert(ctitle),
            originalNorm = Shared.Services.Utilities.SearchNameTo.Convert(ctx?.Value<string>("title_original"))
        };
        if (donors != null)
            foreach (var d in donors.OfType<JObject>())
            { var dh = d.Value<string>("hash"); if (!string.IsNullOrEmpty(dh)) h.knownHashes.Add(dh); }

        // защита от «усыновления» чужого: хеши всех пользовательских загрузок (категория lampa —
        // другие сериалы/фильмы, вторая карточка того же шоу) в knownHashes, чтобы кандидат с таким
        // infohash не прошёл гейты. Иначе QbitAddMagnetEx на дубликате вернул бы true, filePrio сбросил
        // бы выбор файлов чужого торрента, а замещение потом удалило бы его с файлами.
        try
        {
            var mainCat = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}"));
            foreach (var it in mainCat) { var hh = it.Value<string>("hash"); if (!string.IsNullOrEmpty(hh)) h.knownHashes.Add(hh); }
        }
        catch { }

        var eligible = FilterDonorCandidates(scored, h);
        int maxClaim = MaxClaim(eligible);
        var wanted = ComputeWanted(inv, maxClaim);
        SetHuntStamp(m, now, maxClaim);
        if (wanted.Count == 0) return 0;   // новее ничего не заявлено — основная и так самая полная

        int grabbed = 0, probes = 0;
        long minB = conf.epSizeMinMb * 1024L * 1024, maxB = conf.epSizeMaxGb * 1024L * 1024 * 1024;

        // ТОЛЬКО топ-1 из выдачи Лампа-торрента (по требованию владельца: не перебирать всё, брать
        // единственную самую релевантную альтернативу). Не подошла по файлам → в этот проход больше не ищем.
        foreach (var cand in OrderByCover(eligible, season, wanted).Take(1))
        {
            if (probes >= Math.Max(1, conf.donorProbesPerRun)) break;
            if (((m["donors"] as JArray)?.Count ?? 0) >= conf.donorMaxPerSeries) break;
            probes++;

            string parselink = cand.Value<string>("parselink");
            string magnet = cand.Value<string>("magnet");
            if (string.IsNullOrWhiteSpace(magnet))
            {
                magnet = await ResolveMagnetStatic(parselink);
                if (string.IsNullOrWhiteSpace(magnet)) { BlacklistAdd(m, null, parselink, "resolve-failed", 1); continue; }
            }
            string btih = MagnetHash(magnet);
            if (string.IsNullOrEmpty(btih) || h.knownHashes.Contains(btih) || h.blacklistKeys.Contains(btih)) continue;

            // двойная страховка (сверх knownHashes из категории lampa): если инфохеш уже есть в qBit
            // и это НЕ наш донор — чужая загрузка, не усыновляем (QbitAddMagnetEx на дубле неотличим от add)
            var pre = await QbitTorrentInfo(c, btih);
            if (pre != null && pre.Value<string>("category") != DonorCategory)
            { BlacklistAdd(m, btih, parselink, "foreign", conf.donorBlacklistTtlDays); continue; }

            // двухфазный захват: add со стопом после метаданных → подтверждение по файлам
            if (!await QbitAddMagnetEx(c, magnet, DonorCategory, "qdl-donor", stopAfterMeta: true))
            { BlacklistAdd(m, btih, parselink, "add-failed", 1); continue; }

            var dfiles = await QbitWaitFiles(c, btih, conf.donorMetadataTimeoutSec);
            if (dfiles == null || dfiles.Count == 0)
            {
                await QbitDelete(c, btih, true);
                BlacklistAdd(m, btih, parselink, "meta-timeout", 1);   // короткий TTL: возможно, просто не было сидов
                continue;
            }

            var titleEp = ParseEp(StripSeasonMarks(cand.Value<string>("title") ?? ""));
            var found = FindEpFiles(dfiles, season, wanted, titleEp);
            found.RemoveAll(f => f.size > 0 && (f.size < minB || f.size > maxB));   // теперь вес известен точно
            if (found.Count == 0)
            {
                await QbitDelete(c, btih, true);
                BlacklistAdd(m, btih, parselink, "no-episode", conf.donorBlacklistTtlDays);
                continue;
            }

            var all = dfiles.Select(f => f.Value<int?>("index") ?? -1).Where(i => i >= 0).ToList();
            await QbitFilePrio(c, btih, all, 0);                       // всё выключить…
            await QbitFilePrio(c, btih, found.Select(f => f.index), 1); // …кроме нужных серий
            await QbitStartTorrent(c, btih);

            var epsArr = new JArray();
            foreach (var f in found)
            {
                epsArr.Add(new JObject
                {
                    ["epkey"] = f.epkey, ["season"] = f.season, ["ep"] = f.ep, ["fileIndex"] = f.index,
                    ["status"] = "hunted", ["grabbedAt"] = now.ToString("o"), ["replacedAt"] = null
                });
                wanted.Remove(f.ep);
                inv.Add(f.ep);
            }
            if (m["donors"] is not JArray dl) { dl = new JArray(); m["donors"] = dl; }
            dl.Add(new JObject
            {
                ["hash"] = btih, ["link"] = string.IsNullOrWhiteSpace(parselink) ? magnet : parselink,
                ["title"] = cand.Value<string>("title"), ["tracker"] = cand.Value<string>("tracker"),
                ["sid"] = cand.Value<int?>("sid") ?? 0, ["addedAt"] = now.ToString("o"), ["eps"] = epsArr
            });
            h.knownHashes.Add(btih);
            grabbed += found.Count;
            Console.WriteLine("[QbitDownload] hunt: донор " + btih + " (" + cand.Value<string>("tracker") + ") — серии " + string.Join(",", found.Select(f => f.ep)) + " для «" + m.Value<string>("title") + "»");
            if (wanted.Count == 0) break;
        }
        return grabbed;
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/hunt/run")]
    async public Task<ActionResult> HuntRun(string hash = null)
    {
        int n = await HuntAll(string.IsNullOrWhiteSpace(hash) ? null : hash);
        return Json(new { success = true, grabbed = n });
    }
    #endregion

    #region ScanReplacements — замещение серий донора версией из основной
    sealed class ReplaceAction { public string kind; public string donorHash; public int fileIndex = -1; public JObject ep; public JObject donor; }

    // Чистое планирование: что снять/удалить. donorFiles: hash → files (null = донора в qBit больше нет).
    static List<ReplaceAction> PlanReplacements(JArray mainFiles, JObject item, Dictionary<string, JArray> donorFiles, DateTime now, int staleDays)
    {
        var res = new List<ReplaceAction>();
        if (item["donors"] is not JArray donors || donors.Count == 0) return res;

        // готовые серии основной: (season, ep) c progress>=0.999
        var mainDone = new List<(int season, int ep)>();
        foreach (var f in mainFiles ?? new JArray())
        {
            if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
            if ((f.Value<double?>("progress") ?? 0) < 0.999) continue;
            var e = ParseEp(BaseNoExt(f));
            if (e != null && e.any && e.kind == null && e.ep >= 0) mainDone.Add((e.season, e.ep));
        }

        foreach (var d in donors.OfType<JObject>())
        {
            string dh = d.Value<string>("hash") ?? "";
            bool known = donorFiles != null && donorFiles.ContainsKey(dh);
            JArray dfiles = known ? donorFiles[dh] : null;

            if (known && dfiles == null)
            { res.Add(new ReplaceAction { kind = "forget-donor", donorHash = dh, donor = d }); continue; }   // удалили извне

            var eps = (d["eps"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            int drops = 0;
            foreach (var e in eps)
            {
                if (e.Value<string>("status") != "hunted") continue;
                int en = e.Value<int?>("ep") ?? -1, es = e.Value<int?>("season") ?? -1;
                bool mainHas = en >= 0 && mainDone.Any(md => md.ep == en && (md.season < 0 || es <= 0 || md.season == es));
                if (mainHas)
                {
                    res.Add(new ReplaceAction { kind = "drop-file", donorHash = dh, fileIndex = e.Value<int?>("fileIndex") ?? -1, ep = e, donor = d });
                    drops++;
                }
            }

            int remainingHunted = eps.Count(e => e.Value<string>("status") == "hunted") - drops;
            if (eps.Count > 0 && remainingHunted <= 0)
            { res.Add(new ReplaceAction { kind = "delete-donor", donorHash = dh, donor = d }); continue; }

            // мёртвый донор: висит дольше staleDays и целевой файл так и не докачался
            if (DateTime.TryParse(d.Value<string>("addedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var added)
                && (now - added).TotalDays > Math.Max(1, staleDays) && dfiles != null)
            {
                bool stuck = false;
                foreach (var e in eps)
                {
                    if (e.Value<string>("status") != "hunted") continue;
                    int fi = e.Value<int?>("fileIndex") ?? -1;
                    var f = dfiles.FirstOrDefault(x => (x.Value<int?>("index") ?? -1) == fi);
                    if (f == null || (f.Value<double?>("progress") ?? 0) < 0.999) { stuck = true; break; }
                }
                if (stuck) res.Add(new ReplaceAction { kind = "dead-donor", donorHash = dh, donor = d });
            }
        }
        return res;
    }

    static async Task DeleteDonorFile(HttpClient c, string donorHash, int fileIndex, JArray dfiles)
    {
        try
        {
            if (fileIndex < 0 || dfiles == null) return;
            var f = dfiles.FirstOrDefault(x => (x.Value<int?>("index") ?? -1) == fileIndex);
            string rel = f?.Value<string>("name");
            if (string.IsNullOrEmpty(rel)) return;
            var info = await QbitTorrentInfo(c, donorHash);
            if (info == null || info.Value<string>("category") != DonorCategory) return;   // не наш донор — файл не трогаем
            string savePath = info.Value<string>("save_path") ?? ModInit.conf.downloadsPath;
            string full = ConfinedCombine(savePath, rel) ?? ConfinedCombine(ModInit.conf.downloadsPath, rel);
            if (full != null && System.IO.File.Exists(full)) System.IO.File.Delete(full);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] donor file delete: " + ex.Message); }
    }

    // Исполнитель: вызывается из ScanEpisodeNotifications (15-минутный каденс, уже есть qBit-клиент).
    // list/orig — тот же снимок и его исходные hash, что у вызывающего (сохранение реконсилится).
    static async Task ScanReplacements(HttpClient c, JArray list, HashSet<string> orig)
    {
        bool changed = false;
        foreach (var m in list.OfType<JObject>())
        {
            if (m["donors"] is not JArray donors || donors.Count == 0) continue;
            string mainHash = m.Value<string>("hash");
            if (!ValidHash(mainHash)) continue;

            var mainFiles = await QbitFiles(c, mainHash) ?? new JArray();
            var donorFiles = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in donors.OfType<JObject>())
            {
                string dh = d.Value<string>("hash");
                if (!string.IsNullOrEmpty(dh)) donorFiles[dh] = await QbitFiles(c, dh);
            }

            var actions = PlanReplacements(mainFiles, m, donorFiles, DateTime.UtcNow, ModInit.conf.donorStaleDays);
            foreach (var a in actions.OrderBy(x => x.kind == "drop-file" ? 0 : 1))   // сперва файлы, потом снятие доноров
            {
                try
                {
                    if (a.kind == "drop-file")
                    {
                        if (a.fileIndex >= 0) await QbitFilePrio(c, a.donorHash, new[] { a.fileIndex }, 0);
                        await DeleteDonorFile(c, a.donorHash, a.fileIndex, donorFiles.GetValueOrDefault(a.donorHash));
                        a.ep["status"] = "replaced";
                        a.ep["replacedAt"] = DateTime.UtcNow.ToString("o");
                        changed = true;
                        Console.WriteLine("[QbitDownload] hunt: серия " + a.ep.Value<string>("epkey") + " замещена основной (донор " + a.donorHash + ")");
                    }
                    else if (a.kind == "delete-donor" || a.kind == "dead-donor")
                    {
                        await QbitDeleteDonorSafe(c, a.donorHash);   // удаляем с файлами ТОЛЬКО если категория донорская
                        if (a.kind == "dead-donor")
                            BlacklistAdd(m, a.donorHash, a.donor.Value<string>("link"), "dead", ModInit.conf.donorBlacklistTtlDays);
                        donors.Remove(a.donor);
                        changed = true;
                        Console.WriteLine("[QbitDownload] hunt: донор " + a.donorHash + " снят (" + a.kind + ")");
                    }
                    else if (a.kind == "forget-donor") { donors.Remove(a.donor); changed = true; }
                }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] replace action: " + ex.Message); }
            }
        }
        if (changed) SaveWatchReconciled(list, orig);
    }

    // Стартовая уборка (ModInit): доноры в qBit (категория DonorCategory), на которых не ссылается
    // ни одна watch-запись — осиротели после рестарта контейнера посреди HuntAll (add в qBit прошёл,
    // watch.json ещё не сохранился). Belt-and-suspenders к любой остаточной гонке: качались бы вечно,
    // невидимые в /qdl/list. Категория гарантирована фильтром запроса → удаляем с файлами безопасно.
    public static async Task ReconcileDonors()
    {
        try
        {
            JArray list; lock (_watchLock) { list = LoadWatch(); }
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in list.OfType<JObject>())
                if (m["donors"] is JArray ds)
                    foreach (var d in ds.OfType<JObject>())
                    { var dh = d.Value<string>("hash"); if (!string.IsNullOrEmpty(dh)) referenced.Add(dh); }

            using var c = await Qbit();
            var inQbit = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(DonorCategory)}"));
            foreach (var t in inQbit)
            {
                string h = t.Value<string>("hash");
                if (!string.IsNullOrEmpty(h) && !referenced.Contains(h))
                {
                    await QbitDelete(c, h, true);
                    Console.WriteLine("[QbitDownload] hunt: осиротевший донор " + h + " удалён при старте");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] donor reconcile: " + ex.Message); }
    }

    // каскад при удалении/снятии слежения основной: доноры этой загрузки — с файлами
    static async Task DeleteDonorsOf(HttpClient c, string mainHash)
    {
        try
        {
            JArray list; lock (_watchLock) { list = LoadWatch(); }
            var m = list.OfType<JObject>().FirstOrDefault(x => mainHash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase));
            if (m?["donors"] is not JArray donors) return;
            foreach (var d in donors.OfType<JObject>())
                await QbitDeleteDonorSafe(c, d.Value<string>("hash"));   // с файлами ТОЛЬКО если категория донорская
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] delete donors: " + ex.Message); }
    }
    #endregion

    #region /qdl/episodes — объединённый плейлист сериала (основная + серии доноров)
    // Формат /qdl/files + hash/source/season/episode/epkey/tl. tl = seriesKey:sSeE — стабильный
    // ключ таймлайна просмотра: переживает и замещение донор→основная, и re-grab (смену infohash).
    // На epkey одна запись: основная, если её файл докачан (или серии у доноров нет);
    // донор — если серии в основной нет или она там ещё качается. Экстры/RANGE — только из основной.
    static JArray MergeEpisodeFiles(string mainHash, JArray mainFiles, List<(JObject donor, JArray files)> donorData, string seriesKey, int season)
    {
        var parsed = new List<JObject>();     // серии с распознанным номером
        var unparsed = new List<JObject>();   // экстры/RANGE/непарсибельное — только из основной

        JObject entry(string hash, JToken f, string source, int s, int ep)
        {
            var o = new JObject
            {
                ["hash"] = hash,
                ["index"] = f.Value<int?>("index") ?? -1,
                ["name"] = f.Value<string>("name"),
                ["size"] = f.Value<long?>("size") ?? 0,
                ["progress"] = f.Value<double?>("progress") ?? 0,
                ["source"] = source
            };
            if (ep >= 0)
            {
                int ss = s > 0 ? s : season;
                o["season"] = ss; o["episode"] = ep;
                o["epkey"] = "s" + ss + "e" + ep;
                o["tl"] = seriesKey + ":s" + ss + "e" + ep;
            }
            return o;
        }

        foreach (var f in mainFiles ?? new JArray())
        {
            if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
            var e = ParseEp(BaseNoExt(f));
            if (e != null && e.any && e.kind == null && e.ep >= 0 && (e.season < 0 || season <= 0 || e.season == season))
                parsed.Add(entry(mainHash, f, "main", e.season, e.ep));
            else
                unparsed.Add(entry(mainHash, f, "main", -1, -1));
        }

        if (donorData != null)
            foreach (var (donor, dfiles) in donorData)
            {
                if (dfiles == null) continue;
                string dh = donor.Value<string>("hash");
                foreach (var e in (donor["eps"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (e.Value<string>("status") == "replaced") continue;   // файл донора уже удалён
                    int fi = e.Value<int?>("fileIndex") ?? -1, ep = e.Value<int?>("ep") ?? -1, es = e.Value<int?>("season") ?? -1;
                    if (fi < 0 || ep < 0) continue;
                    var f = dfiles.FirstOrDefault(x => (x.Value<int?>("index") ?? -1) == fi);
                    if (f == null) continue;

                    var mainSame = parsed.FirstOrDefault(x => x.Value<string>("source") == "main"
                        && x.Value<int?>("episode") == ep
                        && (es <= 0 || x.Value<int?>("season") == (es > 0 ? es : season)));
                    if (mainSame != null)
                    {
                        if ((mainSame.Value<double?>("progress") ?? 0) >= 0.999) continue;   // основная готова — донор не нужен
                        parsed.Remove(mainSame);                                             // основная ещё качается → пока донор
                    }
                    parsed.Add(entry(dh, f, "donor", es, ep));
                }
            }

        var ordered = parsed.OrderBy(x => x.Value<int?>("season") ?? 0).ThenBy(x => x.Value<int?>("episode") ?? 0).ToList();
        ordered.AddRange(unparsed);
        return new JArray(ordered);
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/episodes")]
    async public Task<ActionResult> Episodes(string hash)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            // watch-запись (доноры) + стабильный ключ сериала
            JObject watchItem;
            lock (_watchLock) { watchItem = LoadWatch().OfType<JObject>().FirstOrDefault(x => hash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)); }
            int seriesId = watchItem?.Value<int?>("id") ?? 0;
            string link = watchItem?.Value<string>("link");
            if (seriesId == 0)
                try { if (System.IO.File.Exists(MetaPath(hash))) seriesId = JObject.Parse(System.IO.File.ReadAllText(MetaPath(hash))).Value<int?>("id") ?? 0; } catch { }
            if (string.IsNullOrEmpty(link))
                try { if (System.IO.File.Exists(LinkPath(hash))) link = JObject.Parse(System.IO.File.ReadAllText(LinkPath(hash))).Value<string>("link"); } catch { }
            string sk = SeriesKey(seriesId, link);

            // локальный (не-торрент) маркер-финал: как /qdl/files, доноров у него не бывает
            var loc = LoadLocal(hash);
            if (loc != null && !LocalIsOverlay(loc))
            {
                var arr0 = new JArray();
                foreach (var f in LocalFiles(loc))
                {
                    if (!System.IO.File.Exists(f.path)) continue;
                    var o = new JObject { ["hash"] = hash, ["index"] = f.index, ["name"] = f.name, ["size"] = f.size, ["progress"] = 1.0, ["source"] = "main" };
                    var e = ParseEp(System.IO.Path.GetFileNameWithoutExtension(f.name ?? ""));
                    if (e != null && e.any && e.kind == null && e.ep >= 0)
                    {
                        int ss = e.season > 0 ? e.season : 1;
                        o["season"] = ss; o["episode"] = e.ep; o["epkey"] = "s" + ss + "e" + e.ep; o["tl"] = sk + ":s" + ss + "e" + e.ep;
                    }
                    arr0.Add(o);
                }
                return ContentTo(arr0.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
            }

            using var c = await Qbit();
            var mainFiles = await QbitFiles(c, hash) ?? new JArray();
            // оверлей-сирота: торрент удалён извне — фолбэк на файлы маркера (как /qdl/files)
            if (mainFiles.Count == 0 && loc != null)
            {
                foreach (var f in LocalFiles(loc))
                    if (System.IO.File.Exists(f.path))
                        mainFiles.Add(new JObject { ["index"] = f.index, ["name"] = f.name, ["size"] = f.size, ["progress"] = 1.0 });
            }

            int season = DominantSeason(mainFiles);
            if (season <= 0) season = Math.Max(1, (watchItem?["ctx"] as JObject)?.Value<int?>("season") ?? 1);

            var donorData = new List<(JObject donor, JArray files)>();
            if (watchItem?["donors"] is JArray donors)
                foreach (var d in donors.OfType<JObject>())
                {
                    string dh = d.Value<string>("hash");
                    if (ValidHash(dh)) donorData.Add((d, await QbitFiles(c, dh)));
                }

            var merged = MergeEpisodeFiles(hash, mainFiles, donorData, sk, season);
            return ContentTo(merged.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] episodes: " + ex);
            return Json(new { error = "internal error" });
        }
    }
    #endregion

    #region переключение заброшенной основной раздачи (stale → pendingSwitch → подтверждение)
    static string Fnv(string s)
    {
        uint h = 2166136261; foreach (char ch in s ?? "") { h ^= ch; h *= 16777619; }
        return h.ToString("x8");
    }

    // Вызывается из CheckWatches, когда infohash топика не изменился (застой). Мутирует m —
    // сохранение списка делает CheckWatches. Уведомление kind=SWITCH кладём в noti с дедупом.
    static async Task ConsiderSwitch(JObject m)
    {
        var conf = ModInit.conf;
        string mode = (conf.watchAutoSwitch ?? "notify").ToLowerInvariant();
        if (mode == "off") return;
        if ((m.Value<int?>("stale") ?? 0) < Math.Max(1, conf.watchStaleChecks)) return;

        var now = DateTime.UtcNow;
        if (DateTime.TryParse(m.Value<string>("lastSwitch"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ls)
            && (now - ls).TotalDays < Math.Max(1, conf.watchSwitchCooldownDays)) return;

        if (m["pendingSwitch"] is JObject ps0)
        {
            // свежее предложение уже висит — не спамим; протухшее (кандидат мог умереть) снимаем
            if (DateTime.TryParse(ps0.Value<string>("foundAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fa)
                && (now - fa).TotalDays <= 30) return;
            m["pendingSwitch"] = null;
        }

        string curHash = m.Value<string>("hash");
        var ctx = m["ctx"] as JObject;
        string ctitle = ctx?.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("query");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) return;

        using var c = await Qbit();
        var info = await QbitTorrentInfo(c, curHash);
        if (info == null || (info.Value<double?>("progress") ?? 0) < 1) return;   // недокачанную основную не трогаем
        var mainFiles = await QbitFiles(c, curHash) ?? new JArray();
        int mainVideos = 0;
        foreach (var f in mainFiles) if (_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) mainVideos++;

        int season = DominantSeason(mainFiles);
        if (season <= 0) season = Math.Max(1, ctx?.Value<int?>("season") ?? 1);

        var scored = await SearchScored(ctitle, ctitle, ctx?.Value<string>("title_original"),
                                        ctx?.Value<int?>("year") ?? 0, 2, season, null);

        // скор текущей раздачи, если она видна в выдаче (для ветки кандидатов без «N из M»)
        double? curScore = null;
        foreach (var t in scored.OfType<JObject>())
            if (curHash.Equals(MagnetHash(t.Value<string>("magnet")), StringComparison.OrdinalIgnoreCase))
            { curScore = t.Value<double?>("score"); break; }

        long addedOn = info.Value<long?>("added_on") ?? 0;
        DateTime? addedDate = addedOn > 0 ? DateTimeOffset.FromUnixTimeSeconds(addedOn).UtcDateTime : (DateTime?)null;

        JObject best = null; EpCoverage bestCov = null;
        foreach (var t in scored.OfType<JObject>())   // список уже по скору — первый прошедший и есть лучший
        {
            string btih = MagnetHash(t.Value<string>("magnet"));
            if (!string.IsNullOrEmpty(btih) && btih.Equals(curHash, StringComparison.OrdinalIgnoreCase)) continue;
            if ((t.Value<int?>("sid") ?? 0) < conf.recommendMinSeeds) continue;
            string title = t.Value<string>("title") ?? "";
            var ss2 = TorrentScoring.ParseSeasons(title);
            if (season > 0 && ss2.Count > 0 && !ss2.Contains(season)) continue;
            if (season > 1 && ss2.Count == 0) continue;

            var cov = TorrentScoring.ParseEpCoverage(title);
            bool better;
            if (cov != null && cov.have > 0)
                better = cov.have > mainVideos;   // ЯВНО больше серий, чем файлов в основной
            else
            {
                DateTime? d = null;
                if (DateTime.TryParse(t.Value<string>("date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dd)) d = dd;
                better = addedDate != null && d != null && (d.Value - addedDate.Value).TotalDays >= 14
                      && curScore != null && (t.Value<double?>("score") ?? 0) - curScore.Value >= 15;
            }
            if (!better) continue;
            best = t; bestCov = cov; break;
        }
        if (best == null) return;

        string bMagnet = best.Value<string>("magnet");
        string bParselink = best.Value<string>("parselink");
        string bBtih = MagnetHash(bMagnet);
        string label = "Найдена более полная раздача"
            + (bestCov != null ? " — серии " + bestCov.have + (bestCov.total > 0 ? " из " + bestCov.total : "") : "")
            + " · " + (best.Value<int?>("sid") ?? 0) + " сид. Переключение перекачает сезон заново.";

        m["pendingSwitch"] = new JObject
        {
            ["magnet"] = bMagnet, ["parselink"] = bParselink, ["title"] = best.Value<string>("title"),
            ["tracker"] = best.Value<string>("tracker"), ["sid"] = best.Value<int?>("sid") ?? 0,
            ["score"] = best.Value<double?>("score") ?? 0,
            ["ep"] = bestCov != null ? new JObject { ["have"] = bestCov.have, ["total"] = bestCov.total } : null,
            ["foundAt"] = now.ToString("o")
        };

        int seriesId = m.Value<int?>("id") ?? 0;
        string sk = SeriesKey(seriesId, m.Value<string>("link"));
        string epkeyN = "switch:" + (!string.IsNullOrEmpty(bBtih) ? bBtih : Fnv(bParselink));
        try
        {
            using var db = new SqlContext();
            if (!db.noti.Any(x => x.seriesKey == sk && x.epkey == epkeyN))   // один и тот же кандидат — одно уведомление
            {
                db.noti.Add(new NotiModel
                {
                    seriesKey = sk, seriesId = seriesId, hash = curHash, title = m.Value<string>("title"),
                    season = -1, episode = -1, kind = "SWITCH", epkey = epkeyN,
                    label = label, created = now, read = false
                });
                db.SaveChanges();
                Console.WriteLine("[QbitDownload] watch: предложено переключение «" + m.Value<string>("title") + "» → " + best.Value<string>("title"));
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] switch noti: " + ex.Message); }

        if (mode == "auto")
        {
            var (ok, newHash, _) = await ExecuteSwitch(m);
            if (ok)
            {
                try
                {
                    using var db = new SqlContext();
                    string ek = "switched:" + newHash;
                    if (!db.noti.Any(x => x.seriesKey == sk && x.epkey == ek))
                    {
                        db.noti.Add(new NotiModel
                        {
                            seriesKey = sk, seriesId = seriesId, hash = newHash, title = m.Value<string>("title"),
                            season = -1, episode = -1, kind = "INFO", epkey = ek,
                            label = "Переключено на более полную раздачу — сезон перекачивается", created = DateTime.UtcNow, read = false
                        });
                        db.SaveChanges();
                    }
                }
                catch { }
            }
        }
    }

    // Само переключение: добавить кандидата в основную категорию, снять старый торрент
    // (файлы по умолчанию оставляем — другой рип, qBit-перепроверка их не сматчит), перенести кэш.
    static async Task<(bool ok, string newHash, string error)> ExecuteSwitch(JObject m)
    {
        if (m["pendingSwitch"] is not JObject ps) return (false, null, "no pending");
        string magnet = ps.Value<string>("magnet");
        string parselink = ps.Value<string>("parselink");
        if (string.IsNullOrWhiteSpace(magnet)) magnet = await ResolveMagnetStatic(parselink);
        string newHash = MagnetHash(magnet);
        if (string.IsNullOrWhiteSpace(newHash)) return (false, null, "resolve failed");
        string curHash = m.Value<string>("hash");
        if (newHash.Equals(curHash, StringComparison.OrdinalIgnoreCase))
        { m["pendingSwitch"] = null; return (false, null, "same hash"); }

        using var c = await Qbit();
        if (!await QbitAddMagnetEx(c, magnet, ModInit.conf.category)) return (false, null, "qbit add failed");
        await QbitDelete(c, curHash, ModInit.conf.switchDeleteOldFiles);
        MigrateCache(curHash, newHash);
        m["hash"] = newHash;
        m["link"] = !string.IsNullOrWhiteSpace(parselink) ? parselink : magnet;
        m["stale"] = 0;
        m["switchCount"] = (m.Value<int?>("switchCount") ?? 0) + 1;
        m["lastSwitch"] = DateTime.UtcNow.ToString("o");
        m["pendingSwitch"] = null;
        Console.WriteLine("[QbitDownload] watch: переключение " + curHash + " -> " + newHash);
        return (true, newHash, null);
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/watch/switch")]
    async public Task<ActionResult> WatchSwitch(string hash, int accept = 0)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            JArray list; lock (_watchLock) { list = LoadWatch(); }
            var m = list.OfType<JObject>().FirstOrDefault(x => hash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase));
            if (m == null) return Json(new { success = false, error = "not watched" });
            if (m["pendingSwitch"] is not JObject) return Json(new { success = false, error = "no pending" });

            if (accept != 1)
            {
                m["pendingSwitch"] = null;
                m["stale"] = 0;   // отказ: не предлагать сразу снова
                lock (_watchLock) { SaveWatch(list); }
                return Json(new { success = true, switched = false });
            }

            var (ok, newHash, err) = await ExecuteSwitch(m);
            lock (_watchLock) { SaveWatch(list); }
            return ok ? Json(new { success = true, switched = true, hash = newHash })
                      : Json(new { success = false, error = err });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] watch switch: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
    #endregion
}
