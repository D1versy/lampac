using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
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

// Итог add в qBit. Дубликат (торрент уже сидит) РАЗЛИЧАЕТСЯ отдельно: на дубликате qBit НЕ меняет
// категорию/теги существующего торрента. Инцидент 2026-07-25 («Укрытие»): перерегистрацию раздачи
// уже качала охота донором, re-grab «добавил» её как основную, категория осталась донорской — и
// уборка доноров сняла «донора» С ФАЙЛАМИ, то есть весь сериал. Вызывающий обязан разбирать Duplicate.
public enum QbitAddStatus { Failed, Added, Duplicate }

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

    // Тег донора в qBit (виден и глазами в WebUI). Снимается при промоушене донора в основную.
    const string DonorTag = "qdl-donor";

    #region qBit-хелперы (инъецируемый HttpClient — тестируются FakeQbit)
    // Расширенный add: категория/теги/остановка после метаданных (qBit >= 4.6 понимает stopCondition;
    // старые версии игнорируют — фолбэк в QbitWaitFiles). Разбор ответа — как в /qdl/add (v4/v5).
    // ВАЖНО: дубликат отдаётся отдельным статусом — на нём qBit НЕ применяет переданные категорию/теги
    // к уже сидящему торренту (см. комментарий у QbitAddStatus).
    static async Task<QbitAddStatus> QbitAddMagnetStatus(HttpClient c, string magnet, string category, string tags = null, bool stopAfterMeta = false)
    {
        // Последний барьер перед qBittorrent. Здесь, а не на входе, потому что сюда сходятся
        // ЧЕТЫРЕ фоновых контура (re-grab, QbitAddMagnet, захват донора, переключение), и все
        // они читают магнеты из watch.json / индекса, записанных ДО появления санитайза.
        magnet = SanitizeMagnet(magnet);

        var content = new MultipartFormDataContent
        {
            { new StringContent(magnet), "urls" },
            { new StringContent(ModInit.conf.downloadsPath), "savepath" },
            { new StringContent(category ?? ModInit.conf.category), "category" }
        };
        if (!string.IsNullOrWhiteSpace(tags)) content.Add(new StringContent(tags), "tags");
        if (stopAfterMeta) content.Add(new StringContent("MetadataReceived"), "stopCondition");

        var r = await c.PostAsync("/api/v2/torrents/add", content);
        return QbitAddOutcome((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Разбор ответа /api/v2/torrents/add. Вынесен отдельно, потому что точек добавления ДВЕ —
    /// магнет (охота, re-grab, /qdl/add) и .torrent файлом (реплика), — а главная ловушка в них
    /// общая: 🔴 qBittorrent на НЕУДАЧНОЕ добавление отвечает 200 с телом «Fails.». Наивное
    /// `IsSuccessStatusCode` читает это как успех, и провал молча ретраится каждый тик вечно.
    /// </summary>
    internal static QbitAddStatus QbitAddOutcome(int status, string rawBody)
    {
        string body = (rawBody ?? "").Trim();

        if (status == 409 || body.Equals("Conflict", StringComparison.OrdinalIgnoreCase)) return QbitAddStatus.Duplicate;
        if (status < 200 || status >= 300) return QbitAddStatus.Failed;
        if (body == "Ok." || body.Length == 0) return QbitAddStatus.Added;
        if (body.StartsWith("{"))
        {
            try
            {
                var j = JObject.Parse(body);
                if ((j.Value<int?>("success_count") ?? 0) > 0 || (j.Value<int?>("pending_count") ?? 0) > 0) return QbitAddStatus.Added;
                if ((j.Value<int?>("duplicate_count") ?? 0) > 0) return QbitAddStatus.Duplicate;
            }
            catch { }
            return QbitAddStatus.Failed;
        }
        return QbitAddStatus.Failed;
    }

    // «Добавилось ли вообще» (дубликат = да). Кому важна разница — берёт QbitAddMagnetStatus.
    static async Task<bool> QbitAddMagnetEx(HttpClient c, string magnet, string category, string tags = null, bool stopAfterMeta = false)
        => await QbitAddMagnetStatus(c, magnet, category, tags, stopAfterMeta) != QbitAddStatus.Failed;

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

    static string NormPath(string p) => string.IsNullOrWhiteSpace(p) ? null : p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    // Лежат ли два торрента в одних и тех же файлах на диске: совпадающий content_path или один
    // внутри другого. Общий корень загрузок (downloadsPath) пересечением НЕ считается — в нём лежит всё.
    static bool PathsOverlap(string a, string b)
    {
        string x = NormPath(a), y = NormPath(b);
        if (x == null || y == null) return false;
        string root = NormPath(ModInit.conf.downloadsPath);
        if (root != null && (x == root || y == root)) return false;
        return x == y || x.StartsWith(y + "/", StringComparison.Ordinal) || y.StartsWith(x + "/", StringComparison.Ordinal);
    }

    // Пересекается ли содержимое донора с файлами ЛЮБОЙ пользовательской загрузки: донор мог сесть
    // не только в папку своей основной, но и в папку соседней раздачи. mainContentPath — путь основной,
    // переданный вызывающим (нужен, когда её самой в qBit уже нет: /qdl/delete снимает её первой).
    // FAIL-SAFE: не смогли спросить qBit → считаем «пересекается». Лучше оставить лишние файлы, чем стереть чужие.
    static async Task<bool> DonorSharesUserFiles(HttpClient c, JObject donorInfo, string mainHash, string mainContentPath)
    {
        string dp = donorInfo?.Value<string>("content_path");
        if (string.IsNullOrWhiteSpace(dp)) return false;   // qBit не знает пути (нет метаданных) — файлов тоже нет

        if (PathsOverlap(dp, mainContentPath)) return true;
        if (ValidHash(mainHash))
        {
            var mi = await QbitTorrentInfo(c, mainHash);
            if (mi != null && PathsOverlap(dp, mi.Value<string>("content_path"))) return true;
        }
        try
        {
            var mainCat = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}"));
            foreach (var it in mainCat)
                if (PathsOverlap(dp, it.Value<string>("content_path"))) return true;
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] hunt: не смог сверить папки загрузок (" + ex.Message + ") — снимаю донора БЕЗ файлов");
            return true;
        }
    }

    // Удалить донора С ФАЙЛАМИ, но ТОЛЬКО если он реально в донорской категории. Защита от катастрофы:
    // если из-за коллизии infohash в watch.donors просочилась пользовательская загрузка (категория lampa)
    // или чужой торрент — мы НЕ удаляем его файлы. Никогда не delete-with-files вслепую по записи донора.
    //
    // Плюс две страховки по файлам (инцидент 2026-07-25):
    //   1) донор И ЕСТЬ основная (топик перерегистрировали, re-grab пере-резолвил основную в тот же
    //      infohash) → не трогаем совсем;
    //   2) донор пишет в папку какой-либо пользовательской загрузки → снимаем торрент, файлы оставляем.
    static async Task QbitDeleteDonorSafe(HttpClient c, string hash, string mainHash = null, string mainContentPath = null)
    {
        if (!ValidHash(hash)) return;
        if (!string.IsNullOrEmpty(mainHash) && hash.Equals(mainHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[QbitDownload] hunt: НЕ удаляю " + hash + " — это сама основная раздача");
            return;
        }

        var info = await QbitTorrentInfo(c, hash);
        if (info == null) return;   // торрента уже нет — нечего удалять
        string cat = info.Value<string>("category");
        if (cat != DonorCategory)
        {
            Console.WriteLine("[QbitDownload] hunt: НЕ удаляю " + hash + " с файлами — категория «" + cat + "» не донорская");
            return;
        }

        bool shared = await DonorSharesUserFiles(c, info, mainHash, mainContentPath);
        await QbitDelete(c, hash, !shared);
        Console.WriteLine("[QbitDownload] hunt: донор " + hash + " снят " + (shared ? "БЕЗ файлов — общая папка с загрузкой" : "С ФАЙЛАМИ"));
    }

    // Донор оказался той самой раздачей, в которую пере-резолвился топик основной (перерегистрация).
    // Снимать его нельзя — это теперь ЕДИНСТВЕННАЯ копия сериала. Переводим в основную категорию,
    // снимаем донорский тег и возвращаем в загрузку ВСЕ файлы: у донора всё, кроме серии-цели, было
    // выключено через filePrio=0, иначе «основная» осталась бы качать одну серию.
    // Возвращает true ТОЛЬКО если промоушен доведён до конца (категория сменилась И приоритеты
    // восстановлены): наполовину промоутнутый торрент — это «сериал в донорской категории», который
    // уборка сирот принимает за донора. Вызывающий на false обязан оставить донорские записи и повторить.
    static async Task<bool> PromoteDonorToMain(HttpClient c, string hash)
    {
        try
        {
            var r = await c.PostAsync("/api/v2/torrents/setCategory", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("category", ModInit.conf.category)
            }));
            if (!r.IsSuccessStatusCode)
            {
                Console.WriteLine("[QbitDownload] promote donor: setCategory " + (int)r.StatusCode + " — торрент остался донорским");
                return false;
            }
            try
            {
                await c.PostAsync("/api/v2/torrents/removeTags", new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("hashes", hash),
                    new KeyValuePair<string, string>("tags", DonorTag)
                }));
            }
            catch { }

            // у донора всё, кроме серии-цели, выключено (filePrio=0) — без возврата приоритетов
            // «основная» осталась бы качать одну серию
            var files = await QbitFiles(c, hash);
            if (files == null)
            {
                Console.WriteLine("[QbitDownload] promote donor: список файлов недоступен — приоритеты НЕ восстановлены");
                return false;
            }
            var all = files.Select(f => f.Value<int?>("index") ?? -1).Where(i => i >= 0).ToList();
            if (!await QbitFilePrio(c, hash, all, 1))
            {
                Console.WriteLine("[QbitDownload] promote donor: filePrio не применился — сезон остался бы недокачанным");
                return false;
            }
            await QbitStartTorrent(c, hash);
            return true;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] promote donor: " + ex.Message); return false; }
    }

    // Записи о доноре с таким infohash во ВСЕХ watch-записях (тот же торрент мог числиться донором
    // и у соседнего сериала). Возвращает, сколько записей снято.
    static int DropDonorRefs(IEnumerable<JObject> items, string hash)
    {
        int n = 0;
        foreach (var m in items ?? Enumerable.Empty<JObject>())
        {
            if (m["donors"] is not JArray ds) continue;
            for (int i = ds.Count - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(hash) && hash.Equals(ds[i].Value<string>("hash"), StringComparison.OrdinalIgnoreCase))
                { ds.RemoveAt(i); n++; }
        }
        return n;
    }

    static bool IsDonorRef(IEnumerable<JObject> items, string hash)
        => (items ?? Enumerable.Empty<JObject>()).Any(m => (m["donors"] as JArray)?.OfType<JObject>()
            .Any(d => !string.IsNullOrEmpty(hash) && hash.Equals(d.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)) == true);

    // Общая пост-обработка «в основную категорию добавили торрент, который уже сидел донором охоты»
    // (re-grab перерегистрированного топика, переключение раздачи). Промоутим его и стираем донорские
    // записи — иначе контур замещения снял бы «донора» с файлами. Вызывать СРАЗУ после add.
    static async Task<bool> PromoteIfDonor(HttpClient c, string newHash, IEnumerable<JObject> items, string title)
    {
        if (!ValidHash(newHash)) return false;
        var list = (items ?? Enumerable.Empty<JObject>()).ToList();
        bool referenced = IsDonorRef(list, newHash);
        if (!referenced && await QbitCategory(c, newHash) != DonorCategory) return false;

        // записи донора снимаем ТОЛЬКО после удавшегося промоушена: иначе торрент остался бы в донорской
        // категории и без единой ссылки на себя — ровно то, что уборка сирот удаляет с файлами
        if (!await PromoteDonorToMain(c, newHash))
        {
            Console.WriteLine("[QbitDownload] watch: промоушен " + newHash + " («" + title
                + "») не довёлся — донорские записи оставлены, повтор в следующем проходе");
            return false;
        }
        int dropped = DropDonorRefs(list, newHash);
        Console.WriteLine("[QbitDownload] watch: донор " + newHash + " промоутнут в основную «" + title
            + "» — та же раздача перерегистрирована (снято донорских записей: " + dropped + ")");
        return true;
    }
    #endregion

    #region чистая логика охоты (без IO — тестируется напрямую через Access)
    sealed class HuntCtx
    {
        public string mainHash;
        public int season;
        public HashSet<string> knownHashes;    // основная + текущие доноры (lower)
        public HashSet<string> blacklistKeys;  // btih/parselink активного blacklist
        public Dictionary<string, string> blacklistLinkTitles;   // no-episode: parselink → название на момент бана
        public int minSeeds, minQuality, minMb, maxGb;
        public string titleNorm, originalNorm;  // нормализованные названия сериала для строгого гейта имени
        public string selfTopicKey;             // топик САМОЙ основной раздачи (её перерегистрация — не донор)

        // qdl 2.107 — гейты, которых требовал инцидент «Укрытие» 2026-09-04 (XviD 720×400 донором):
        public bool requireRussian;                       // донор только с русской дорожкой (IsRussian)
        public bool rejectUnknownQuality = true;          // quality==0 = ниже порога, а не пропуск гейта
        public bool rejectLegacy = true;                  // XviD/DivX/MPEG4/MPEG2 — отсев независимо от порога
        public bool rejectScreener = true;                // CAM/TELESYNC/TELECINE/WORKPRINT/SCREENER — отсев
        public int targetQuality = 1080;                  // цель ранга качества (как у основной, ≥1080)
        // Файловая подпись основной (BaseNoExt|size) и нормализованное имя её торрента/корневой папки —
        // замена шлюза §AK-1 (TopicKey) для DHT-строк без parselink: перезалив нашей же раздачи
        // несёт те же файлы байт-в-байт (или то же имя у over_threshold без списка файлов).
        public HashSet<string> mainSig;
        public int mainVideoCount;
        public string mainNameNorm;
    }

    // ── ранг качества относительно цели (qdl 2.107) ────────────────────────
    // Один компаратор на четыре места: порядок проб, апгрейд, победитель в PlanReplacements, показ в
    // MergeEpisodeFiles. Иначе новый 1080p-донор с меньшим score «проигрывал» бы старому XviD-паку,
    // пока качается (повтор §BA-5). 0 — ровно цель; 1..99 — выше цели (ближе — меньше);
    // 100..999 — ниже цели (выше — меньше); 1000 — качество не распознано (худшее).
    static int QualityRank(int q, int target)
    {
        if (q <= 0) return 1000;
        if (target <= 0) return Math.Max(1, 500 - Math.Min(q, 499));   // без цели: выше = лучше
        if (q == target) return 0;
        if (q > target) return 1 + Math.Min(98, (q - target) / 100);
        // Ниже цели: половина разницы. Полная разница с капом 899 при цели 2160 схлопывала 1080 и 720 в
        // один ранг 999 (обе «≥899 ниже»), и порядок проб решали сиды/бакет — 720p шёл раньше 1080p.
        return 100 + Math.Min(899, (target - q) / 2);
    }

    // Доминирующее качество по ПОЛНЫМ путям видеофайлов (папка несёт «1080p», сам файл — не всегда).
    static int DominantQuality(IEnumerable<string> paths)
    {
        var counts = new Dictionary<int, int>();
        foreach (var p in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrEmpty(p) || !_videoExtRx.IsMatch(p)) continue;
            int q = QualityFromTitle(p);
            if (q > 0) counts[q] = counts.TryGetValue(q, out int n) ? n + 1 : 1;
        }
        return counts.Count > 0 ? counts.OrderByDescending(x => x.Value).ThenByDescending(x => x.Key).First().Key : 0;
    }

    // Цель донора: явный donorQualityTarget, иначе качество основной по её файлам. Цель — не потолок:
    // основная 720p/XviD → цель 1080 (решение владельца: «лучшее из доступного, не ниже 720p»).
    static int DonorTargetQuality(JArray mainFiles, ModuleConf conf)
    {
        int explicitT = conf?.donorQualityTarget ?? 0;
        if (explicitT > 0) return explicitT;
        int q = DominantQuality((mainFiles ?? new JArray()).Select(f => f.Value<string>("name")));
        return Math.Max(1080, q);
    }

    // Грубый тай-брейк внутри ранга: 5 ГБ WEB-DL выше 0.7 ГБ x265 при равном 1080p. Именно бакеты,
    // а не байты: тонкая разница размеров не должна перебивать score.
    static int SizeBucket(long bytesPerEp)
        => bytesPerEp >= 2_500_000_000L ? 2 : bytesPerEp >= 1_000_000_000L ? 1 : 0;

    // Сколько серий несёт кандидат: по данным классификатора (bm_*), иначе по названию.
    static int CandidateHaveCount(JObject t)
    {
        if (t.Value<bool?>("bm_pack") == true) return Math.Max(1, t.Value<int?>("files_count") ?? 1);
        if (t["bm_eps"] is JArray be && be.Count > 0) return be.Count;
        string title = t.Value<string>("title") ?? "";
        var cov = TorrentScoring.ParseEpCoverage(title);
        int haveCount = cov?.have ?? 0;
        if (haveCount == 0)
        {
            var pe = ParseEp(StripSeasonMarks(title));
            if (pe != null && pe.any && pe.kind == "RANGE" && pe.ep2 >= pe.ep) haveCount = pe.ep2 - pe.ep + 1;
            else if (pe != null && pe.any && pe.kind == null && pe.ep >= 0) haveCount = 1;
        }
        return haveCount;
    }

    static long CandidateBytesPerEp(JObject t)
    {
        long sizeBytes = t.Value<long?>("sizeBytes") ?? 0;
        int have = CandidateHaveCount(t);
        return have > 0 ? sizeBytes / have : sizeBytes;
    }

    // «Кого-то видели в рое»: у bitmagnet это подсказка, у трекеров — измерение; в обоих случаях
    // годится только как ключ ПОРЯДКА (мёртвые 0/0 — 12 % сезонной выборки — в хвост), не гейт.
    static bool CandidateLive(JObject t) => (t.Value<int?>("sid") ?? 0) > 0 || (t.Value<int?>("pir") ?? 0) > 0;

    static readonly System.Text.RegularExpressions.Regex _legacyCodecTitleRx = new(@"(?i)(?<![a-z0-9])(xvid|divx)(?![a-z0-9])", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex _screenerTitleRx = new(@"(?i)(?<![a-z0-9])(camrip|hdcam|telesync|telecine|workprint|screener|dvdscr)(?![a-z0-9])", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── файловая подпись основной (замена §AK-1 для DHT) ───────────────────
    static string SigKey(string name, long size)
    {
        if (string.IsNullOrEmpty(name) || size <= 0) return null;
        string n = name.Replace('\\', '/');
        string b = Path.GetFileNameWithoutExtension(n.Substring(n.LastIndexOf('/') + 1));
        return string.IsNullOrEmpty(b) ? null : b.ToLowerInvariant() + "|" + size;
    }

    static HashSet<string> MainSignature(JArray mainFiles)
    {
        var sig = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in mainFiles ?? new JArray())
        {
            if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
            string k = SigKey(f.Value<string>("name"), f.Value<long?>("size") ?? 0);
            if (k != null) sig.Add(k);
        }
        return sig;
    }

    // Корневая папка путей основной («Silo (Season 3) WEB-DL 1080p/…») — имя раздачи у перезалива
    // не меняется, а у over_threshold-строк bitmagnet списка файлов нет вовсе.
    static string MainRootFolder(JArray mainFiles)
    {
        string root = null;
        foreach (var f in mainFiles ?? new JArray())
        {
            string n = (f.Value<string>("name") ?? "").Replace('\\', '/');
            int cut = n.IndexOf('/');
            if (cut <= 0) return null;   // файл в корне — общей папки нет
            string r = n.Substring(0, cut);
            if (root == null) root = r;
            else if (!string.Equals(root, r, StringComparison.Ordinal)) return null;
        }
        return root;
    }

    // Кандидат — наша же основная раздача (её перезалив/зеркало): ≥2 видеофайлов (или ≥50 % основной)
    // совпали по имени и байт-точному размеру, либо — у строк без списка файлов — нормализованное имя
    // торрента равно имени основной. Порог «≥2» намеренно: русский сезонный пак содержит тот же файл,
    // что уже стоит одиночкой-донором, и по одному совпадению вылетал бы из кандидатов на все серии.
    static bool LooksLikeOwnRelease(JObject t, HuntCtx h)
    {
        if (t["bm_files"] is JArray bf && bf.Count > 0)
        {
            if (h.mainSig == null || h.mainSig.Count == 0) return false;
            int hits = 0;
            foreach (var f in bf.OfType<JObject>())
            {
                string k = SigKey(f.Value<string>("name"), f.Value<long?>("size") ?? 0);
                if (k != null && h.mainSig.Contains(k)) hits++;
            }
            if (hits == 0) return false;
            int need = Math.Max(2, (h.mainVideoCount + 1) / 2);
            if (hits >= need) return true;
            return bf.Count == 1 && h.mainVideoCount == 1;   // основная из одного файла — и он совпал
        }
        if (string.IsNullOrEmpty(h.mainNameNorm)) return false;
        string tn = Shared.Services.Utilities.SearchNameTo.Convert(t.Value<string>("title"));
        return !string.IsNullOrEmpty(tn) && tn == h.mainNameNorm;
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

        // Мультисезонный пак («1-3 сезон: 1-27 серии из 30»): счётчик серий СКВОЗНОЙ по сезонам и
        // ничего не доказывает про серию ep ВНУТРИ сезона — раньше 27 ≥ 10 давало ложный Yes, пак
        // вставал первым в пробах (score 123.9) и сгорал в no-episode (20 записей blacklist «Укрытия»).
        bool multiSeason = seasons.Count > 1;

        var cov = TorrentScoring.ParseEpCoverage(t);
        if (cov != null && cov.have > 0) return multiSeason ? DonorCover.Maybe : (cov.have >= ep ? DonorCover.Yes : DonorCover.No);

        var pe = ParseEp(StripSeasonMarks(t));
        if (pe != null && pe.any && pe.kind == "RANGE" && pe.ep2 >= pe.ep)
            return multiSeason ? DonorCover.Maybe : ((ep >= pe.ep && ep <= pe.ep2) ? DonorCover.Yes : DonorCover.No);
        if (pe != null && pe.any && pe.kind == null && pe.ep >= 0)
        {
            if (pe.season >= 0 && season > 0 && pe.season != season) return DonorCover.No;
            return pe.ep == ep ? DonorCover.Yes : DonorCover.No;
        }
        return DonorCover.Maybe;
    }

    // Вердикт по ЭЛЕМЕНТУ выдачи (qdl 2.107): у bitmagnet-строк есть данные классификатора —
    // bm_season/bm_eps из episodes jsonb. Отдельное имя, не перегрузка: тестовый мост Access.Call
    // различает перегрузки только по числу аргументов.
    //   • одиночка {"3":{"10":{}}} → точное сравнение номера;
    //   • мультисерийный {"1":{"1":{},…}} → ep ∈ списку, и файлов не меньше серий (иначе — по имени:
    //     «S01E01…» с episodes на весь сезон — ошибка парсера);
    //   • пак {"3":{}} с files_count ≥ 2 → Maybe (проверит FindEpFiles после метаданных);
    //   • пустой episodes при files_count < 2 («Silo.S02S10…») → по имени, а не в одиночку по bm_eps[0].
    static DonorCover TitleCoversEpItem(JObject t, int season, int ep)
    {
        string title = t.Value<string>("title") ?? "";
        if (t["bm_eps"] is JArray eps)
        {
            int bs = t.Value<int?>("bm_season") ?? 0;
            if (season > 0 && bs > 0 && bs != season) return DonorCover.No;
            var list = eps.Select(x => x.Value<int?>() ?? -1).Where(x => x >= 0).ToList();
            if (list.Count == 1) return list[0] == ep ? DonorCover.Yes : DonorCover.No;
            if (list.Count > 1)
            {
                if (!list.Contains(ep)) return DonorCover.No;
                return (t.Value<int?>("files_count") ?? 0) >= list.Count ? DonorCover.Yes : TitleCoversEp(title, season, ep);
            }
            if (t.Value<bool?>("bm_pack") == true) return DonorCover.Maybe;
            return TitleCoversEp(title, season, ep);
        }
        if (t.Value<bool?>("bm_multi") == true) return DonorCover.Maybe;
        return TitleCoversEp(title, season, ep);
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

    // Гейт имени для строк, найденных по TMDB id (qdl 2.107). Scene-имя не делится на сегменты «/» и
    // нормализуется целиком («Silo.S03E10.1080p.ColdFilm.mkv» → «silos03e101080pcoldfilm»), поэтому
    // строгий NameMatchesSeries отсекал ВСЕ строки bitmagnet и наш же индекс — 0 из 76 в снимке прохода.
    // Полного доверия id_match (как в скоринге) донору не даём: берём ГОЛОВУ имени до первого маркера
    // сезона/серии/года/разрешения/источника и требуем точного равенства карточке ИЛИ эталону из
    // bitmagnet (content.title / original_title — для аниме с японским оригиналом на карточке).
    // Один ведущий «[группа]» срезается, кроме похожих на домен трекера («[ Torrent911.lol ]»).
    // 🔴 Гард на пустую нормализацию обязателен: SearchNameTo.Convert отдаёт null для CJK/скобок, и
    // без гарда null ∈ {…, null} открыл бы гейт для любой строки.
    static readonly System.Text.RegularExpressions.Regex _headMarkerRx = new(
        @"(?i)(?:(?<![a-z0-9а-яё])(?:S\d{1,2}(?:E\d{1,3})?|E\d{1,3}|Ep\.?\s?\d{1,3}|\d{1,2}x\d{1,3}|(?:19|20)\d{2}|\d{3,4}[pi]|4K|UHD|season|сезон\w*|сери\w*|WEB-?DL|WEB-?RIP|BDRip|BluRay|HDTV|HDRip|DVDRip|[xh]\.?26[45]|HEVC|AV1|MULTI|Complete)(?![a-z0-9]))|\s[-–]\s*\d{1,3}(?![0-9a-z])|[\[\(]",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex _leadGroupRx = new(@"^\s*\[([^\]]{1,40})\]\s*[_\-\. ]*", System.Text.RegularExpressions.RegexOptions.Compiled);
    // «.tv» доменом не считаем: это релиз-группы (AniLibria.TV, BaibaKo.tv, Kerob.tv), а не пиратские витрины.
    static readonly System.Text.RegularExpressions.Regex _domainLikeRx = new(@"(?i)www\.|\.(org|com|net|lol|to|info|cz|ru|su|me|xyz|club|pw)(?![a-z])", System.Text.RegularExpressions.RegexOptions.Compiled);

    static string TitleHeadBeforeMarker(string title)
    {
        string t = (title ?? "").Trim();
        t = _videoExtRx.Replace(t, "");
        var g = _leadGroupRx.Match(t);
        if (g.Success && !_domainLikeRx.IsMatch(g.Groups[1].Value)) t = t.Substring(g.Length);
        var m = _headMarkerRx.Match(t);
        return m.Success ? t.Substring(0, m.Index) : t;
    }

    static bool NameMatchesSeriesOrId(JObject t, HuntCtx h)
    {
        string title = t.Value<string>("title") ?? "";
        if (NameMatchesSeries(title, h.titleNorm, h.originalNorm)) return true;
        if (t.Value<bool?>("id_match") != true) return false;
        string hn = Shared.Services.Utilities.SearchNameTo.Convert(TitleHeadBeforeMarker(title));
        if (string.IsNullOrEmpty(hn)) return false;
        string[] refs =
        {
            h.titleNorm, h.originalNorm,
            Shared.Services.Utilities.SearchNameTo.Convert(t.Value<string>("id_title")),
            Shared.Services.Utilities.SearchNameTo.Convert(t.Value<string>("id_title_original"))
        };
        foreach (var r in refs)
            if (!string.IsNullOrEmpty(r) && hn == r) return true;
        return false;
    }

    // Ключ «того же топика трекера» для сравнения ссылок. Наши parselink-и — loopback-ссылки вида
    // http://127.0.0.1:9118/rutracker/parsemagnet?id=6878482&apikey=… : один и тот же топик может
    // приехать с другим apikey/хостом, поэтому сравниваем путь + значимые параметры запроса.
    static readonly HashSet<string> _topicKeyIgnore = new(StringComparer.OrdinalIgnoreCase) { "apikey", "account_email", "host", "life" };
    static string TopicKey(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            string bh = MagnetHash(link);
            return string.IsNullOrEmpty(bh) ? null : "btih:" + bh;
        }
        try
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var u)) return null;
            var q = HttpUtility.ParseQueryString(u.Query);
            var parts = new List<string>();
            foreach (string k in q.AllKeys)
            {
                if (k == null || _topicKeyIgnore.Contains(k)) continue;
                parts.Add(k.ToLowerInvariant() + "=" + (q[k] ?? ""));
            }
            parts.Sort(StringComparer.Ordinal);
            return u.AbsolutePath.ToLowerInvariant() + "?" + string.Join("&", parts);
        }
        catch { return null; }
    }

    // Жёсткие гейты кандидата-донора. Решение и подпись причины — ОДНА функция (DropReason): раньше
    // фильтр и его «зеркало для лога» жили порознь и стереглись тестом на дрейф; с qdl 2.107 гейтов
    // стало вдвое больше, и единственный источник истины дешевле сторожа.
    static List<JObject> FilterDonorCandidates(JArray scored, HuntCtx h)
        => scored.OfType<JObject>().Where(t => DropReason(t, h) == null).ToList();

    // Порядок проб (qdl 2.107): уверенные Yes, затем Maybe (паки «вслепую»); внутри — ранг качества
    // относительно цели ↑ → живые (кого-то видели в рое) вперёд → бакет байт/серия ↓ → score ↓.
    // Раньше решал только score, где качество весит ≤8 баллов, а «пак 10 из 10» +8 и parselink +6 —
    // так русский XviD-пак 720×400 структурно обгонял русский 1080p-одиночник.
    static List<JObject> OrderByCover(List<JObject> eligible, int season, List<int> wanted, int targetQuality)
    {
        var yes = new List<JObject>(); var maybe = new List<JObject>();
        foreach (var t in eligible)
        {
            var best = DonorCover.No;
            foreach (int ep in wanted)
            {
                var cv = TitleCoversEpItem(t, season, ep);
                if (cv == DonorCover.Yes) { best = DonorCover.Yes; break; }
                if (cv == DonorCover.Maybe) best = DonorCover.Maybe;
            }
            if (best == DonorCover.Yes) yes.Add(t);
            else if (best == DonorCover.Maybe) maybe.Add(t);
        }
        var cmp = DonorOrder(targetQuality);
        yes.Sort(cmp);
        maybe.Sort(cmp);
        yes.AddRange(maybe);
        return yes;
    }

    static Comparison<JObject> DonorOrder(int targetQuality) => (a, b) =>
    {
        int c = QualityRank(a.Value<int?>("quality") ?? 0, targetQuality).CompareTo(QualityRank(b.Value<int?>("quality") ?? 0, targetQuality));
        if (c != 0) return c;
        c = CandidateLive(b).CompareTo(CandidateLive(a));
        if (c != 0) return c;
        c = SizeBucket(CandidateBytesPerEp(b)).CompareTo(SizeBucket(CandidateBytesPerEp(a)));
        if (c != 0) return c;
        return ((int)Math.Round(b.Value<double?>("score") ?? 0)).CompareTo((int)Math.Round(a.Value<double?>("score") ?? 0));
    };

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

    // сколько серий заявляет ОДИН кандидат («N из M» / одиночка / диапазон / episodes классификатора)
    static int ClaimOf(JObject t)
    {
        int max = 0;
        string title = t.Value<string>("title") ?? "";
        var cov = TorrentScoring.ParseEpCoverage(title);
        if (cov != null && cov.have > max) max = cov.have;
        var e = ParseEp(StripSeasonMarks(title));
        if (e != null && e.any)
        {
            if (e.kind == "RANGE" && e.ep2 > max) max = e.ep2;
            else if (e.kind == null && e.ep > max) max = e.ep;
        }
        if (t["bm_eps"] is JArray be)
            foreach (var x in be) { int n = x.Value<int?>() ?? 0; if (n > max) max = n; }
        return max;
    }

    // максимум серий, который заявляют кандидаты («N из M» / одиночки / диапазоны)
    static int MaxClaim(List<JObject> candidates)
    {
        int max = 0;
        foreach (var t in candidates) { int c = ClaimOf(t); if (c > max) max = c; }
        return max;
    }

    // Сезонный гейт кандидата (общий для FilterDonorCandidates, DropReason и ClaimCandidates).
    static bool SeasonOk(string title, HuntCtx h)
    {
        var seasons = TorrentScoring.ParseSeasons(title);
        if (h.season > 0 && seasons.Count > 0 && !seasons.Contains(h.season)) return false;
        if (h.season > 1 && seasons.Count == 0) return false;   // охотим не первый сезон, а раздача сезон не заявляет — риск не тот
        return true;
    }

    // «Этот кандидат ДОКАЗЫВАЕТ, что серия существует» — только идентичность: тот же сериал, тот же
    // сезон. Сиды/качество/вес/blacklist/своя раздача отвечают на ДРУГОЙ вопрос — «можно ли качать
    // отсюда», к факту выхода серии отношения не имеют.
    // Раньше MaxClaim считался по донорски пригодным, и у «Великого расхитителя гробниц» три
    // кандидата с «5 из 12» (blacklist после сбоя резолва / свой перевыложенный топик / 720p)
    // выпали из подсчёта — охота записала «заявлено серий до 1, нужно —» при пяти вышедших.
    // Сезонный гейт по ЭЛЕМЕНТУ (qdl 2.107): имя, противоречащее сезону, — отсев как было; имя без
    // сезона при season > 1 — ок только если сезон заявляет классификатор bitmagnet (bm_season из
    // episodes). Отдельное имя, не перегрузка SeasonOk — см. TitleCoversEpItem.
    static bool SeasonOkItem(JObject t, HuntCtx h)
    {
        string title = t.Value<string>("title") ?? "";
        var seasons = TorrentScoring.ParseSeasons(title);
        if (h.season > 0 && seasons.Count > 0 && !seasons.Contains(h.season)) return false;
        if (h.season > 1 && seasons.Count == 0)
            return (t.Value<int?>("bm_season") ?? 0) == h.season;
        return true;
    }

    static bool IdentityMatches(JObject t, HuntCtx h)
        => NameMatchesSeriesOrId(t, h) && SeasonOkItem(t, h);

    static List<JObject> ClaimCandidates(JArray scored, HuntCtx h)
        => scored.OfType<JObject>().Where(t => IdentityMatches(t, h)).ToList();

    #region апгрейд донорской серии на раздачу получше
    // Текущие «ставки» донора: скор и качество. Сперва по СВЕЖЕЙ выдаче (скор плывёт вместе с сидами
    // и датой), иначе — то, что записали при захвате. (-1, -1) = не с чем сравнивать → не апгрейдим.
    // База сравнения для апгрейда: score/качество/бакет размера раздачи, с которой взята серия. Бакет
    // известен только когда раздача донора есть в текущей выдаче (-1 = неизвестен → сравниваем по score).
    static (double score, int quality, int bucket) DonorBaseline3(JObject donor, JArray scored)
    {
        string dh = donor.Value<string>("hash") ?? "", link = donor.Value<string>("link") ?? "";
        foreach (var t in scored.OfType<JObject>())
        {
            bool same = (!string.IsNullOrEmpty(dh) && string.Equals(MagnetHash(t.Value<string>("magnet")), dh, StringComparison.OrdinalIgnoreCase))
                     || (!string.IsNullOrWhiteSpace(link) && string.Equals(t.Value<string>("parselink"), link, StringComparison.OrdinalIgnoreCase));
            if (same) return (t.Value<double?>("score") ?? 0, t.Value<int?>("quality") ?? 0, SizeBucket(CandidateBytesPerEp(t)));
        }
        var s = donor.Value<double?>("score");
        return s.HasValue ? (s.Value, donor.Value<int?>("quality") ?? 0, -1) : (-1, -1, -1);
    }

    // Серия «временно с другой раздачи» стоит апгрейда, если среди годных кандидатов есть раздача
    // ЯВНО лучше той, с которой мы её взяли: выше качество или скор выше на minScore (⭐ — это и есть
    // верх скора, отдельного признака не нужно). Серии, которые уже есть в ОСНОВНОЙ, не трогаем:
    // основная всегда приоритетнее донора, её версия придёт штатным замещением.
    // why (может быть null) — заполняется человекочитаемым «с чего на что» для лога.
    // qdl 2.107: «лучше» = строго меньший ранг качества относительно цели, либо равный ранг и score выше
    // на minScoreGain. Донор с quality:0 имеет ранг 1000 — любой распознанный кандидат лучше (раньше
    // guard bquality>0 делал XviD-донора неуязвимым). Апгрейд на ХУДШИЙ ранг невозможен даже при +40.
    // Maybe-кандидаты (паки, в т.ч. мультисезонные) допускаются только при строго лучшем ранге — пак
    // проверит FindEpFiles, повторные пробы ограничены blacklist по btih.
    // donorSig — подписи файлов текущих доноров: тот же файл под другим btih — не апгрейд.
    static List<int> ComputeUpgrades(JArray donors, JArray scored, List<JObject> eligible, HashSet<int> mainEps,
                                     int season, int minScoreGain, Dictionary<int, string> why,
                                     int targetQuality, HashSet<string> donorSig)
    {
        var res = new List<int>();
        if (donors == null || eligible.Count == 0) return res;
        var ordered = eligible.ToList();
        ordered.Sort(DonorOrder(targetQuality));

        var best = UpgradeBaselines(donors, scored, mainEps, targetQuality);
        foreach (var kv in best.OrderBy(x => x.Key))
        {
            int ep = kv.Key; var b = kv.Value;
            foreach (var t in ordered)
            {
                var cover = TitleCoversEpItem(t, season, ep);
                if (cover == DonorCover.No) continue;
                if (!BetterThanBaseline(t, b, targetQuality, minScoreGain, cover)) continue;
                if (SharesFilesWith(t, donorSig)) continue;   // тот же контент под другим btih
                res.Add(ep);
                if (why != null) why[ep] = $"E{ep}: {b.quality}p/{Math.Round(b.score, 1)} → {t.Value<int?>("quality") ?? 0}p/{Math.Round(t.Value<double?>("score") ?? 0, 1)}";
                break;
            }
        }
        res.Sort();
        return res;
    }

    // База сравнения на серию — ЛУЧШАЯ из уже взятых копий (ранг → бакет → score): серию могут держать
    // два донора (старый + апгрейд в полёте), и сравнение со старым взводило бы третью копию.
    static Dictionary<int, (double score, int quality, int bucket, int rank)> UpgradeBaselines(JArray donors, JArray scored, HashSet<int> mainEps, int targetQuality)
    {
        var best = new Dictionary<int, (double score, int quality, int bucket, int rank)>();
        if (donors == null) return best;
        foreach (var d in donors.OfType<JObject>())
        {
            var (bscore, bquality, bbucket) = DonorBaseline3(d, scored);
            if (bscore < 0) continue;   // не с чем сравнивать (старая запись и раздачи нет в выдаче)
            int brank = QualityRank(bquality, targetQuality);
            foreach (var e in (d["eps"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (e.Value<string>("status") != "hunted") continue;
                int ep = e.Value<int?>("ep") ?? -1;
                if (ep < 0 || (mainEps != null && mainEps.Contains(ep))) continue;
                if (!best.TryGetValue(ep, out var cur) || brank < cur.rank
                    || (brank == cur.rank && (bbucket > cur.bucket || (bbucket == cur.bucket && bscore > cur.score))))
                    best[ep] = (bscore, bquality, bbucket, brank);
            }
        }
        return best;
    }

    // ТОТ ЖЕ компаратор, что у порядка проб (DonorOrder) и победителя в PlanReplacements:
    // ранг → бакет → score(+minScoreGain). Иначе решение «апгрейд оправдан» принимала одна раздача
    // (score +15), а в пробу первой шла другая (бакет выше) — и так по кругу. Maybe-кандидат (пак)
    // допускается только при строго лучшем ранге.
    static bool BetterThanBaseline(JObject t, (double score, int quality, int bucket, int rank) b, int targetQuality, int minScoreGain, DonorCover cover)
    {
        double cs = t.Value<double?>("score") ?? 0;
        int crank = QualityRank(t.Value<int?>("quality") ?? 0, targetQuality);
        int cbucket = SizeBucket(CandidateBytesPerEp(t));
        if (cover == DonorCover.Maybe) return crank < b.rank;
        return crank < b.rank
            || (crank == b.rank && (b.bucket < 0 ? cs >= b.score + minScoreGain
                                    : cbucket > b.bucket || (cbucket == b.bucket && cs >= b.score + minScoreGain)));
    }

    // Пул проб при апгрейде: кандидат, который закрывает ТОЛЬКО серии-апгрейды, обязан быть лучше их
    // текущей копии — иначе третьим в пробы шёл 720p-одиночник «на замену» 1080p-донору, HuntOne его
    // хватал (в цикле проверки «лучше ли» нет), а PlanReplacements потом выбрасывал недокачанным.
    static List<JObject> ProbePool(List<JObject> eligible, int season, List<int> wanted, List<int> upgrades,
                                   Dictionary<int, (double score, int quality, int bucket, int rank)> baselines, int targetQuality, int minScoreGain)
    {
        if (upgrades == null || upgrades.Count == 0 || baselines == null) return eligible;
        var pool = new List<JObject>();
        foreach (var t in eligible)
        {
            bool keep = false;
            foreach (int ep in wanted)
            {
                var cover = TitleCoversEpItem(t, season, ep);
                if (cover == DonorCover.No) continue;
                if (!upgrades.Contains(ep) || !baselines.TryGetValue(ep, out var b)) { keep = true; break; }   // настоящая дыра — любой годный
                if (BetterThanBaseline(t, b, targetQuality, minScoreGain, cover)) { keep = true; break; }
            }
            if (keep) pool.Add(t);
        }
        return pool;
    }

    static bool SharesFilesWith(JObject t, HashSet<string> sig)
    {
        if (sig == null || sig.Count == 0 || t["bm_files"] is not JArray bf || bf.Count == 0) return false;
        foreach (var f in bf.OfType<JObject>())
        {
            string k = SigKey(f.Value<string>("name"), f.Value<long?>("size") ?? 0);
            if (k != null && sig.Contains(k)) return true;
        }
        return false;
    }
    #endregion

    // Сколько серий заявляет ТОТ ЖЕ топик, что у основной раздачи (перевыложенная наша же раздача).
    // Донором его брать нельзя (§AK) — но это единственный сигнал «пора делать re-grab».
    static int SelfTopicClaim(JArray scored, HuntCtx h)
    {
        int max = 0;
        foreach (var t in scored.OfType<JObject>())
        {
            if (!IdentityMatches(t, h)) continue;
            bool self = (h.selfTopicKey != null && TopicKey(t.Value<string>("parselink")) == h.selfTopicKey)
                     // DHT-перезалив нашей основной (qdl 2.107): parselink'а нет, узнаём по файлам/имени —
                     // ТОЛЬКО против основной (mainSig), никогда против файлов доноров, иначе собственный
                     // донор-одиночка взводил бы «re-grab» на каждом проходе
                     || LooksLikeOwnRelease(t, h);
            if (!self) continue;
            // собственный донор/основная под своим же btih — не сигнал
            string btih = MagnetHash(t.Value<string>("magnet"));
            if (!string.IsNullOrEmpty(btih) && h.knownHashes != null && h.knownHashes.Contains(btih)) continue;
            max = Math.Max(max, ClaimOf(t));
        }
        return max;
    }

    // Подтверждение по РЕАЛЬНЫМ файлам донора: какие из wanted-серий в нём есть.
    // RANGE-файлы отвергаем (серию из склейки адресно не сыграть); спецвыпуски не охотим.
    // Спецслучай: единственный видеофайл без номера + Title-одиночка → номер из Title.
    // donorSeason — сезон донора целиком (DonorSeason), подпорка для файлов без номера сезона; 0 = неизвестен.
    // Сезонный гейт FAIL-CLOSED, но асимметрично:
    //   • season > 1 — требуем ПОЛОЖИТЕЛЬНОГО доказательства сезона (имя файла / папка / сезон донора).
    //     Раньше гейт падал открытым (season < 0 проходил и получал сезон основной вслепую) — так в
    //     3-й сезон «Укрытия» и приехали файлы Silo.S02.E07…E10 (инцидент 2026-08-09);
    //   • season <= 1 — как раньше: аниме и односезонники сплошь без сезонных маркеров, строгость
    //     там просто выключила бы охоту.
    // Та же асимметрия уже есть на уровне кандидата: FilterDonorCandidates отвергает раздачу без
    // заявленного сезона при h.season > 1.
    static List<EpFile> FindEpFiles(JArray files, int season, List<int> wanted, Ep titleEp, int donorSeason)
    {
        var res = new List<EpFile>();
        var vids = new List<JToken>();
        foreach (var f in files ?? new JArray())
            if (_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) vids.Add(f);

        foreach (var f in vids)
        {
            var e = ParseEp(BaseNoExt(f));
            if (e == null || !e.any || e.kind != null || e.ep < 0) continue;

            int fs = FileSeason(f);                              // имя файла → папка
            if (fs <= 0) fs = donorSeason;                       // → сезон донора целиком
            if (season > 0 && fs > 0 && fs != season) continue;  // явное расхождение
            if (season > 1 && fs <= 0) continue;                 // не первый сезон и ничем не подтверждён

            if (!wanted.Contains(e.ep)) continue;
            if (res.Any(x => x.ep == e.ep)) continue;   // один файл на серию
            int es = fs > 0 ? fs : season;
            res.Add(new EpFile
            {
                index = f.Value<int?>("index") ?? -1,
                ep = e.ep,
                season = es,
                epkey = EpKey(new Ep { season = es, ep = e.ep }),
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
                int s = titleEp.season >= 0 ? titleEp.season : (donorSeason > 0 ? donorSeason : SeasonFromPath(f.Value<string>("name")));
                // fail-closed и здесь (qdl 2.107): при season > 1 сезон охоты вслепую не подставляем —
                // раньше «s <= 0 → s = season» делал проверку ниже тождеством. Сезон одиночки должен
                // подтвердить сам донор (имя/папка/episodes классификатора → donorSeason).
                if (s <= 0 && season <= 1) s = season;
                if (s > 0 && (season <= 1 || s == season))   // тот же fail-closed для одиночки
                {
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
        }

        res.RemoveAll(x => x.index < 0);
        return res;
    }
    #endregion

    #region blacklist пустышек (per watch-запись, TTL)
    static void BlacklistAdd(JObject item, string btih, string parselink, string reason, int ttlDays)
        => BlacklistAddMinutes(item, btih, parselink, reason, Math.Max(1, ttlDays) * 1440, 1);

    // «Нужных серий в файлах нет»: ключ — btih (перевыкладка топика с новым btih пробуется снова), но
    // трекерная строка без магнета до резолва btih не знает — для неё запоминаем (parselink, название):
    // тот же топик с ТЕМ ЖЕ названием = тот же контент, с новым («1-10 из 10» вместо «1-9») — новая проба.
    // Без этого забаненные parselink-строки занимали все три слота проб на каждом проходе.
    static void BlacklistAddNoEpisode(JObject item, string btih, string parselink, string title, int ttlDays)
    {
        BlacklistAddMinutes(item, btih, parselink, "no-episode", Math.Max(1, ttlDays) * 1440, 1);
        if (item["blacklist"] is JArray bl && bl.Count > 0 && bl[bl.Count - 1] is JObject last) last["title"] = title;
    }

    static Dictionary<string, string> BlacklistLinkTitles(JObject item, DateTime now)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item["blacklist"] is JArray bl)
            foreach (var b in bl)
            {
                if (b.Value<string>("reason") != "no-episode") continue;
                if (!DateTime.TryParse(b.Value<string>("until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u) || u <= now) continue;
                string p = b.Value<string>("parselink"), t = b.Value<string>("title");
                if (!string.IsNullOrEmpty(p) && !string.IsNullOrEmpty(t)) res[p] = t;
            }
        return res;
    }

    // TTL в МИНУТАХ + номер попытки. Старая форма клампила ttlDays к «не меньше суток», поэтому
    // разовый сетевой сбой резолва парселинка банил ЛУЧШЕГО кандидата на сутки — и охота ещё и
    // переставала учитывать его серии («Великий расхититель гробниц»: 5 вышедших серий, а в логе
    // «заявлено серий до 1, нужно —»).
    static void BlacklistAddMinutes(JObject item, string btih, string parselink, string reason, int minutes, int attempt)
    {
        if (item["blacklist"] is not JArray bl) { bl = new JArray(); item["blacklist"] = bl; }
        bl.Add(new JObject
        {
            ["btih"] = string.IsNullOrEmpty(btih) ? null : btih.ToLowerInvariant(),
            ["parselink"] = parselink,
            ["reason"] = reason,
            ["attempt"] = attempt,
            ["until"] = DateTime.UtcNow.AddMinutes(Math.Max(1, minutes)).ToString("o")
        });
    }

    // Транзиентные отказы (сеть/трекер), а не дефект раздачи: 30м → 1ч → 2ч → 4ч → 8ч → 12ч, и лишь
    // с 6-й неудачи подряд — сутки (похоже, раздача правда мертва).
    static int TransientFailMinutes(int attempt)
        => attempt >= 6 ? 1440 : Math.Min(720, 30 * (1 << Math.Max(0, attempt - 1)));

    // Сколько раз подряд этот кандидат уже падал с той же причиной (история переживает истечение TTL
    // благодаря grace в PruneBlacklist).
    static int BlacklistAttempts(JObject item, string key, string reason)
    {
        if (string.IsNullOrWhiteSpace(key) || item["blacklist"] is not JArray bl) return 0;
        int max = 0;
        foreach (var b in bl.OfType<JObject>())
        {
            if (b.Value<string>("reason") != reason) continue;
            if (!string.Equals(b.Value<string>("btih"), key, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(b.Value<string>("parselink"), key, StringComparison.OrdinalIgnoreCase)) continue;
            max = Math.Max(max, b.Value<int?>("attempt") ?? 1);
        }
        return max;
    }

    // Grace: запись живёт ещё сутки ПОСЛЕ истечения TTL — только ради счётчика попыток (иначе бэкофф
    // обнулялся бы на каждом разбане и вечно сидел на 30 минутах). Действующей блокировкой она уже
    // не является — это решает BlacklistKeys по времени.
    const int BlacklistGraceHours = 24;

    static void PruneBlacklist(JObject item, DateTime now)
    {
        if (item["blacklist"] is not JArray bl) return;
        for (int i = bl.Count - 1; i >= 0; i--)
            if (!DateTime.TryParse(bl[i].Value<string>("until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u)
                || u.AddHours(BlacklistGraceHours) < now)
                bl.RemoveAt(i);
    }

    // Ключи ДЕЙСТВУЮЩИХ блокировок (until > now). Раньше функция полагалась на то, что PruneBlacklist
    // позвали прямо перед ней; с grace-хранением истории это стало обязательным условием, поэтому
    // проверка времени теперь здесь и явная.
    static HashSet<string> BlacklistKeys(JObject item, DateTime now)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item["blacklist"] is JArray bl)
            foreach (var b in bl)
            {
                if (!DateTime.TryParse(b.Value<string>("until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u) || u <= now) continue;
                var h = b.Value<string>("btih"); if (!string.IsNullOrEmpty(h)) res.Add(h);
                if (b.Value<string>("reason") == "no-episode") continue;   // parselink у no-episode — не ключ (см. BlacklistAddNoEpisode)
                var p = b.Value<string>("parselink"); if (!string.IsNullOrEmpty(p)) res.Add(p);
            }
        return res;
    }
    #endregion

    #region HuntAll / HuntOne — сама охота

    // Ранний повтор при пустой выдаче индексатора: не чаще раза в HuntRetryMinutes и не более
    // HuntRetryMax раз подряд — иначе долгий даунтайм трекеров превращается в бесконечный опрос.
    const int HuntRetryMinutes = 15;
    const int HuntRetryMax = 4;
    static int _huntRetries;

    // Итог прохода по одному сериалу. searched — дошли до опроса индексатора; barren — выдача пустая
    // (сбой трекеров неотличим от «новых серий нет», поэтому такой проход не засчитываем).
    // regrab — свой топик перевыложен с бо́льшим числом серий: просим CheckWatches отработать вне очереди.
    sealed class HuntOneResult
    {
        public int grabbed; public bool searched; public bool barren; public bool regrab;
        public bool bmFail;        // локальный тик: сезонная выборка bitmagnet упала (таймаут/сеть) — пропуск, не изменение
        public bool changed;       // запись мутирована (штампы/blacklist/донор) — сохранять
        public bool localSkipped;  // локальный тик: fingerprint не изменился / ждать нечего — ни скоринга, ни лога
        public bool waiting;       // локальный тик: у сериала есть недостающие серии
        public bool newRows;       // локальный тик: в скоупе появились новые/исчезли строки
        public int probes;
    }

    // Ранний повтор оправдан только если пусто у ВСЕХ опрошенных сериалов: у одного — бывает
    // (нишевый тайтл), у всех сразу — это индексатор/трекеры лежат.
    static bool ShouldRetryHunt(int searched, int barren, int retries)
        => searched > 0 && barren == searched && retries < HuntRetryMax;

    // localOnly (qdl 2.107) — локальный тик: только bitmagnet + наш индекс, без трекеров, без апгрейдов,
    // без сигнала re-grab, без штампа lastRun; тихий (строки лога только при новых строках/пробах).
    public static async Task<int> HuntAll(string onlyHash = null, bool localOnly = false)
    {
        if (!ModInit.conf.episodeHunt) return 0;
        if (!await _watchGate.WaitAsync(0))   // общий фоновый гейт (был _hunting): сериализуем с CheckWatches/ScanEpisodeNotifications
        {
            if (localOnly) Interlocked.Increment(ref _ltBusy);
            else
            {
                // Гейт теперь держит и локальный тик (пробы — до 3×90 с на сериал), поэтому плановый трекерный
                // проход не теряем на весь период: просим таймер вернуться через 5 минут. Ручной прогон
                // (onlyHash) расписание не двигает.
                Console.WriteLine("[QbitDownload] hunt: тик пропущен — занят другой фоновый проход (watch/notify/hunt)" + (onlyHash == null ? ", повтор через 5 мин" : ""));
                if (onlyHash == null) ModInit.RescheduleHunt(TimeSpan.FromMinutes(5));
            }
            return 0;
        }
        int grabbed = 0, series = 0, searched = 0, barren = 0, waiting = 0, newRows = 0, probes = 0, bmFail = 0;
        bool regrabAsk = false;
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
                try
                {
                    var r = await HuntOne(c, m, localOnly, list);   // штамп hunt.lastRun ставит сам HuntOne — и только на удачном проходе
                    grabbed += r.grabbed; series++;
                    if (localOnly) { if (r.changed) changed = true; }
                    else changed = true;
                    if (r.searched) searched++;
                    if (r.barren) barren++;
                    if (r.regrab) regrabAsk = true;
                    if (r.waiting) waiting++;
                    if (r.newRows) newRows++;
                    if (r.bmFail) bmFail++;
                    probes += r.probes;
                }
                // Упавшая посреди проб запись всё равно сохраняется: правки записи append-only (донор уже
                // добавлен в qBit, blacklist, штампы) — без сохранения донор жил бы невидимкой до рестарта.
                catch (Exception ex) { changed = true; Console.WriteLine($"[QbitDownload] {(localOnly ? "hunt-local" : "hunt")} item: " + ex); }
            }
            if (changed) SaveWatchReconciled(list, orig);
            if (localOnly)
            {
                if (waiting > _ltWaiting) Interlocked.Exchange(ref _ltWaiting, waiting);   // число сериалов, а не сумма по тикам
                Interlocked.Add(ref _ltNewRows, newRows);
                Interlocked.Add(ref _ltBmFail, bmFail);
                Interlocked.Add(ref _ltProbes, probes);
                Interlocked.Add(ref _ltGrabbed, grabbed);
                if (probes > 0 || grabbed > 0)
                    Console.WriteLine($"[QbitDownload] hunt-local: проход — сериалов с ожиданием {waiting}, новых строк у {newRows}, проб {probes}, добыто серий {grabbed}");
            }
            else if (series > 0)
                Console.WriteLine($"[QbitDownload] hunt: проход завершён — записей {series}, опрошено {searched}, пустых выдач {barren}, добыто серий {grabbed}");
        }
        // Локальный тик при лёгшем qBit иначе печатал бы стек раз в 10 минут под тегом полного прохода.
        catch (Exception ex) { Console.WriteLine("[QbitDownload] " + (localOnly ? "hunt-local: " + ex.Message : "hunt: " + ex)); }
        finally { _watchGate.Release(); }

        if (localOnly) return grabbed;   // ни re-grab, ни расписания трекерного прохода локальный тик не трогает

        // Своя раздача перевыложена — просим штатного владельца обновления отработать сейчас, а не
        // через свой 6-часовой тик. Строго ПОСЛЕ release: _watchGate не реентрантный (тот же приём,
        // что CheckWatches → ScanEpisodeNotifications). Мы ничего не добавляем и не удаляем сами —
        // все защиты §AK остаются внутри CheckWatches.
        if (regrabAsk)
        {
            Console.WriteLine("[QbitDownload] hunt: обнаружено обновление своей раздачи — внеочередная проверка слежения");
            try { await CheckWatches(); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] hunt→watch: " + ex); }
        }

        // Пустая выдача у всех — почти всегда сбой индексатора/трекеров, а не «новых серий нет»:
        // при обычном интервале (часы) такая тишина стоила бы суток. Просим таймер прийти раньше.
        // Точечный прогон (onlyHash) — ручной, общее расписание им не двигаем.
        if (onlyHash == null)
        {
            if (ShouldRetryHunt(searched, barren, _huntRetries))
            {
                _huntRetries++;
                ModInit.RescheduleHunt(TimeSpan.FromMinutes(HuntRetryMinutes));
                Console.WriteLine($"[QbitDownload] hunt: индексатор не дал кандидатов ни по одному из {searched} сериалов — ранний повтор через {HuntRetryMinutes} мин (попытка {_huntRetries}/{HuntRetryMax})");
            }
            else if (searched > 0 && barren == searched)
                Console.WriteLine("[QbitDownload] hunt: индексатор по-прежнему молчит, лимит ранних повторов исчерпан — ждём обычный интервал");
            else
                _huntRetries = 0;   // проход не «глухой» (или опрашивать было некого) — серия повторов закрыта
        }
        return grabbed;
    }

    // Догон пропущенных тиков (М2.5): контейнер перезапускается чаще, чем срабатывает редкий таймер
    // охоты, поэтому она могла не идти сутками. true = с прошлого удачного прогона прошло больше
    // 1.5 периода либо штампа нет вовсе (охота ни разу не доходила до конца). Только чтение — зовём
    // со старта модуля, любая ошибка = «не просрочено» (обычное расписание).
    public static bool HuntOverdue(TimeSpan period, out TimeSpan since)
    {
        since = TimeSpan.Zero;
        try
        {
            if (ModInit.conf == null || !ModInit.conf.episodeHunt) return false;
            JArray list;
            lock (_watchLock) { list = LoadWatch(); }
            if (list.Count == 0) return false;

            DateTime? last = null;
            foreach (var m in list.OfType<JObject>())
            {
                string s = (m["hunt"] as JObject)?.Value<string>("lastRun");
                if (!string.IsNullOrEmpty(s) &&
                    DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) &&
                    (last == null || t > last)) last = t;
            }
            if (last == null) return true;
            since = DateTime.UtcNow - last.Value;
            return since > period * 1.5;
        }
        catch { return false; }
    }

    static void SetHuntStamp(JObject m, DateTime now, int maxClaim)
    {
        // перезаписью объекта целиком сбрасывается и счётчик пустых выдач — проход удачный.
        // lastLocal/localWanted (локальный тик) переживают перезапись: их пишет другой контур.
        var prev = m["hunt"] as JObject;
        var h = new JObject { ["lastRun"] = now.ToString("o"), ["lastMaxClaim"] = maxClaim };
        if (prev?["lastLocal"] != null) h["lastLocal"] = prev["lastLocal"];
        if (prev?["localWanted"] != null) h["localWanted"] = prev["localWanted"];
        m["hunt"] = h;
    }

    // Что полному проходу не хватило (после потолка TMDB) — вход локального тика: он обрабатывает
    // только сериалы, у которых есть чего ждать. Пустой список = сезон закрыт, локальный тик молчит.
    static void SetLocalWanted(JObject m, List<int> wanted)
    {
        if (m["hunt"] is not JObject h) { h = new JObject(); m["hunt"] = h; }
        h["localWanted"] = new JArray(wanted ?? new List<int>());
    }

    static void SetLocalStamp(JObject m, DateTime now)
    {
        if (m["hunt"] is not JObject h) { h = new JObject(); m["hunt"] = h; }
        h["lastLocal"] = now.ToString("o");
    }

    // Пустая выдача индексатора: lastRun НЕ трогаем (иначе сбой трекеров выглядит как «новых серий
    // нет» и стоит целый интервал), пишем только диагностику подряд идущих пустых попыток.
    static void MarkHuntBarren(JObject m, DateTime now)
    {
        if (m["hunt"] is not JObject h) { h = new JObject(); m["hunt"] = h; }
        h["lastEmpty"] = now.ToString("o");
        h["emptyStreak"] = (h.Value<int?>("emptyStreak") ?? 0) + 1;
    }

    // ── гейты донора: решение и подпись причины (qdl 2.107 — единственный источник истины) ──
    // null = кандидат годен. Порядок: принадлежность (имя, язык) → живость-подсказка → качество →
    // свои/blacklist → своя раздача по топику → своя раздача по файлам → сезон → вес серии.
    static string DropReason(JObject t, HuntCtx h)
    {
        string title = t.Value<string>("title") ?? "";
        if (!NameMatchesSeriesOrId(t, h)) return "имя";
        // Требование владельца: донор только с русской дорожкой. Трекерные кириллические названия
        // проходят автоматически; отсекаются английские/китайские/украинские поштучные серии из DHT.
        if (h.requireRussian && !TorrentScoring.IsRussian(title, t.Value<bool?>("lang_ru"))) return "язык";
        // Сиды bitmagnet — подсказка (sid_hint): не гейт, только порядок; живость доказывает проба.
        if (t.Value<bool?>("sid_hint") != true && (t.Value<int?>("sid") ?? 0) < h.minSeeds) return "сиды";

        int q = t.Value<int?>("quality") ?? 0;
        // 🔴 Политика 2026-09-04: «не распознано» = ниже порога. Раньше q==0 пропускал гейт, и донором
        // «Укрытия» стал 720×400 XviD с kinozal («…/ WEBRip» без цифры разрешения).
        if (h.rejectUnknownQuality && q <= 0) return "качество не распознано";
        if (h.minQuality > 0 && q > 0 && q < h.minQuality) return "качество";
        if (h.rejectLegacy && (t.Value<bool?>("bm_legacy") == true || _legacyCodecTitleRx.IsMatch(title))) return "кодек";
        if (h.rejectScreener && (t.Value<bool?>("bm_screener") == true || _screenerTitleRx.IsMatch(title))) return "экранка";

        string btih = MagnetHash(t.Value<string>("magnet"));
        string parselink = t.Value<string>("parselink");
        if (!string.IsNullOrEmpty(btih) && h.knownHashes.Contains(btih)) return "уже есть";
        if (!string.IsNullOrEmpty(btih) && h.blacklistKeys.Contains(btih)) return "blacklist";
        if (!string.IsNullOrWhiteSpace(parselink) && h.blacklistKeys.Contains(parselink)) return "blacklist";
        if (string.IsNullOrEmpty(btih) && !string.IsNullOrWhiteSpace(parselink) && h.blacklistLinkTitles != null
            && h.blacklistLinkTitles.TryGetValue(parselink, out var bannedTitle) && string.Equals(bannedTitle, title, StringComparison.Ordinal))
            return "blacklist";   // тот же топик с тем же названием, что уже проверяли по файлам
        if (string.IsNullOrEmpty(btih) && string.IsNullOrWhiteSpace(parselink)) return "без ссылки";

        // ТОТ ЖЕ топик, что у основной раздачи, только перевыложенный (новые серии → новый infohash).
        // Это не «другая раздача-донор», это обновление НАШЕЙ же — её заберёт re-grab в CheckWatches.
        // Взяв её донором, охота получает торрент, который вот-вот станет основной, и контур
        // замещения сносит его с файлами (инцидент 2026-07-25, «Укрытие»). knownHashes тут не спасают:
        // у перерегистрации ДРУГОЙ infohash. Сверяем именно топик.
        if (h.selfTopicKey != null && TopicKey(parselink) == h.selfTopicKey) return "своя раздача";
        // …а у DHT-строк топика нет — узнаём перезалив по файлам/имени основной. Стоит ПОСЛЕ knownHashes,
        // чтобы собственный донор отсекался как «уже есть», а не как «своя».
        if (LooksLikeOwnRelease(t, h)) return "своя раздача (файлы)";

        var seasons = TorrentScoring.ParseSeasons(title);   // порядок повторяет SeasonOkItem, но с разными подписями причин
        if (h.season > 0 && seasons.Count > 0 && !seasons.Contains(h.season)) return "сезон";
        if (h.season > 1 && seasons.Count == 0 && (t.Value<int?>("bm_season") ?? 0) != h.season) return "сезон не заявлен";

        // оценка веса одной серии по названию/данным классификатора (точная — после метаданных, по файлу)
        long sizeBytes = t.Value<long?>("sizeBytes") ?? 0;
        int haveCount = CandidateHaveCount(t);
        if (sizeBytes > 0 && haveCount > 0 && !EpSizeOk(EstimateEpBytes(sizeBytes, haveCount), h.minMb, h.maxGb)) return "вес серии";

        return null;
    }

    static Dictionary<string, int> DropCounts(JArray scored, HuntCtx h)
    {
        var by = new Dictionary<string, int>();
        foreach (var t in scored.OfType<JObject>())
        {
            string r = DropReason(t, h);
            if (r != null) by[r] = by.TryGetValue(r, out int n) ? n + 1 : 1;
        }
        return by;
    }

    static string DropSummary(JArray scored, int keptCount, HuntCtx h)
    {
        int dropped = scored.Count - keptCount;
        if (dropped <= 0) return "";
        // все причины, не топ-4: у «Укрытия» «качество 33, blacklist 19, вес серии 10» в лог не попадали
        var all = DropCounts(scored, h).OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value).ToList();
        return " (отсев " + dropped + (all.Count > 0 ? ": " + string.Join(", ", all) : "") + ")";
    }

    static string WantedText(List<int> wanted)
        => wanted.Count == 0 ? "—"
         : wanted.Count == 1 ? "E" + wanted[0]
         : "E" + wanted[0] + "–E" + wanted[wanted.Count - 1] + " (" + wanted.Count + ")";

    // Сезон из ПУТИ внутри торрента. BaseNoExt (Controller.cs) отрезает папки, а сезон часто написан
    // именно там: «Укрытие.S02.WEB-DLRip.NewComers/Silo.S02.E07….avi». Уверенный ответ — только когда
    // сегмент называет РОВНО один сезон; «Silo.S01-S03/…» → 0 (пусть решает сам файл).
    static int SeasonFromPath(string relName)
    {
        string p = (relName ?? "").Replace('\\', '/');
        int cut = p.LastIndexOf('/');
        if (cut <= 0) return 0;
        var segs = p.Substring(0, cut).Split('/');
        for (int i = segs.Length - 1; i >= 0; i--)   // ближняя папка важнее корневой
        {
            var ss = TorrentScoring.ParseSeasons(segs[i]);
            if (ss.Count == 1) return ss[0];
            if (ss.Count > 1) return 0;
        }
        return 0;
    }

    // Сезон конкретного видеофайла: сперва имя файла, затем путь. 0 = неизвестно.
    static int FileSeason(JToken f)
    {
        var e = ParseEp(BaseNoExt(f));
        if (e != null && e.any && e.kind == null && e.season > 0) return e.season;
        return SeasonFromPath(f?.Value<string>("name") ?? "");
    }

    // Сезоны, которые РЕАЛЬНО лежат в файлах раздачи (только уверенные).
    static HashSet<int> DonorSeasons(JArray files)
    {
        var set = new HashSet<int>();
        foreach (var f in files ?? new JArray())
        {
            if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
            int s = FileSeason(f);
            if (s > 0) set.Add(s);
        }
        return set;
    }

    // Сезон донора «одним числом» — подпорка для файлов без номера сезона: файлы → название раздачи.
    // 0 = неизвестен (в т.ч. многосезонный пак: там каждый файл отвечает сам за себя).
    static int DonorSeason(JArray files, string title)
    {
        var set = DonorSeasons(files);
        if (set.Count == 1) return set.First();
        if (set.Count > 1) return 0;
        var ts = TorrentScoring.ParseSeasons(title ?? "");
        return ts.Count == 1 ? ts[0] : 0;
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

    #region потолок по реально вышедшим сериям (TMDB)
    // Кэш на процесс: сезон опрашивается раз в 6 ч. Ключ — tmdbId:season.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int aired, DateTime at)> _airedCache = new();

    // Сколько серий сезона УЖЕ ВЫШЛО (air_date <= сегодня). 0 = неизвестно → потолок берётся из
    // названий раздач, как раньше (fail-open: TMDB лежит — охота не должна вставать).
    //
    // Зачем: «1-6 серии из 10» в названии — это план сезона, а не факт эфира. Охота считала
    // недостающими E7–E10, которых ещё нет в природе, и закрывала эту дыру чем попало — у «Укрытия»
    // приехали серии 7–10 ВТОРОГО сезона (инцидент 2026-08-09).
    //
    // Ходим в СВОЙ же прокси /tmdb/api/... на 127.0.0.1 (тот же приём, что CatalogWarmup): ответ уже
    // кешируется Staticache, отдельного ключа TMDB и внешнего доступа не нужно.
    static async Task<int> AiredEpisodes(int tmdbId, int season)
    {
        if (tmdbId <= 0 || season <= 0) return 0;
        string key = tmdbId + ":" + season;
        if (_airedCache.TryGetValue(key, out var e) && (DateTime.UtcNow - e.at).TotalHours < 6) return e.aired;

        int port = 9118;
        try { if (CoreInit.conf.listen.port > 0) port = CoreInit.conf.listen.port; } catch { }

        int aired = 0;
        try
        {
            using var rc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string body = await rc.GetStringAsync($"http://127.0.0.1:{port}/tmdb/api/3/tv/{tmdbId}/season/{season}");
            var eps = JObject.Parse(body)["episodes"] as JArray;
            if (eps == null) return 0;
            var today = DateTime.UtcNow.Date;
            foreach (var ep in eps.OfType<JObject>())
            {
                // без даты выхода серия считается вышедшей: пустой air_date у TMDB частый, и
                // трактовать его как «не вышла» значило бы глушить охоту на ровном месте
                string ad = ep.Value<string>("air_date");
                if (!string.IsNullOrWhiteSpace(ad)
                    && DateTime.TryParse(ad, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    && d.Date > today) continue;
                int n = ep.Value<int?>("episode_number") ?? 0;
                if (n > aired) aired = n;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] hunt: TMDB сезон " + tmdbId + "/" + season + " недоступен (" + ex.Message + ") — потолок серий по названиям");
            return 0;
        }

        _airedCache[key] = (aired, DateTime.UtcNow);
        return aired;
    }
    #endregion

    // ── план прохода как чистая функция (qdl 2.107) ────────────────────────
    // Всё, что охота РЕШАЕТ, — без IO: собирается из уже загруженных данных и одинаково служит живому
    // проходу (HuntOne), сухому (HuntDry) и оффлайн-реплею в тестах. IO (qBit, поиск, TMDB) — в HuntPrepare.
    sealed class HuntPlan
    {
        public HuntCtx h;
        public List<JObject> eligible, claims, probes;
        public int maxClaim, claimBeforeCap, eligibleClaim, aired, bitmagnet, selfClaim;
        public List<int> missing = new(), wanted = new(), upgrades = new();
        public Dictionary<int, (double score, int quality, int bucket, int rank)> upBase;   // базы апгрейда на серию (лучшая копия)
        public Dictionary<int, string> upWhy = new();
        public Dictionary<string, int> drops = new();
        public bool atCap;
        public HashSet<int> inv;
    }

    static HuntPlan BuildHuntPlan(JObject m, JArray mainFiles, string mainName, JArray donors, HashSet<string> donorSig,
                                  JArray scored, IEnumerable<string> lampaHashes, string ctitle, string titleOriginal,
                                  int season, int aired, DateTime now, ModuleConf conf, bool localOnly)
    {
        string mainHash = m.Value<string>("hash") ?? "";
        var plan = new HuntPlan { aired = aired };
        var h = new HuntCtx
        {
            mainHash = mainHash.ToLowerInvariant(),
            season = season,
            knownHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainHash },
            blacklistKeys = BlacklistKeys(m, now),
            blacklistLinkTitles = BlacklistLinkTitles(m, now),
            minSeeds = conf.donorMinSeeds,
            minQuality = conf.donorMinQuality,
            minMb = conf.epSizeMinMb,
            maxGb = conf.epSizeMaxGb,
            titleNorm = Shared.Services.Utilities.SearchNameTo.Convert(ctitle),
            originalNorm = Shared.Services.Utilities.SearchNameTo.Convert(titleOriginal),
            selfTopicKey = TopicKey(m.Value<string>("link")),
            requireRussian = conf.donorRequireRussian,
            rejectUnknownQuality = conf.donorRejectUnknownQuality,
            rejectLegacy = conf.donorRejectLegacy,
            rejectScreener = conf.donorRejectScreener,
            targetQuality = DonorTargetQuality(mainFiles, conf),
            mainSig = MainSignature(mainFiles),
            mainVideoCount = (mainFiles ?? new JArray()).Count(f => _videoExtRx.IsMatch(f.Value<string>("name") ?? "")),
            mainNameNorm = Shared.Services.Utilities.SearchNameTo.Convert(MainRootFolder(mainFiles) ?? mainName)
        };
        if (donors != null)
            foreach (var d in donors.OfType<JObject>())
            { var dh = d.Value<string>("hash"); if (!string.IsNullOrEmpty(dh)) h.knownHashes.Add(dh); }
        // защита от «усыновления» чужого: хеши всех пользовательских загрузок (категория lampa —
        // другие сериалы/фильмы, вторая карточка того же шоу) в knownHashes, чтобы кандидат с таким
        // infohash не прошёл гейты. Иначе QbitAddMagnetEx на дубликате вернул бы true, filePrio сбросил
        // бы выбор файлов чужого торрента, а замещение потом удалило бы его с файлами.
        foreach (var hh in lampaHashes ?? Enumerable.Empty<string>())
            if (!string.IsNullOrEmpty(hh)) h.knownHashes.Add(hh);
        plan.h = h;

        plan.inv = InventoryEps(mainFiles, donors, season);
        plan.atCap = (donors?.Count ?? 0) >= conf.donorMaxPerSeries;
        plan.bitmagnet = scored.OfType<JObject>().Count(t => t.Value<string>("tracker") == "bitmagnet");

        plan.eligible = FilterDonorCandidates(scored, h);
        plan.claims = ClaimCandidates(scored, h);     // ⊇ eligible: гейты пригодности тут не применяются
        plan.maxClaim = plan.claimBeforeCap = MaxClaim(plan.claims);
        plan.eligibleClaim = MaxClaim(plan.eligible);

        // Потолок по РЕАЛЬНО ВЫШЕДШИМ сериям (TMDB). «1-6 серии из 10» в названии — план сезона, а не
        // факт эфира: у «Укрытия» это давало wanted E7–E10, которых ещё нет, и охота, не найдя их в
        // третьем сезоне, утащила серии 7–10 ВТОРОГО. Fail-open: TMDB недоступен → работаем как раньше.
        if (aired > 0 && plan.maxClaim > aired) plan.maxClaim = aired;

        plan.missing = ComputeWanted(plan.inv, plan.maxClaim);
        plan.wanted = new List<int>(plan.missing);

        // Апгрейд: серия уже лежит «временно с другой раздачи», но в выдаче есть раздача получше.
        // Идёт ДОПОЛНИТЕЛЬНО к недостающим сериям (ComputeWanted смотрит только вперёд от максимума).
        // Локальный тик апгрейды не считает: иначе каждый новый DHT-релиз той же серии дёргал бы перекачку.
        if (!localOnly && conf.donorUpgrade)
        {
            var mainEps = new HashSet<int>();
            foreach (var f in mainFiles ?? new JArray())
            {
                if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
                var fe = ParseEp(BaseNoExt(f));
                if (fe != null && fe.any && fe.kind == null && fe.ep >= 0) mainEps.Add(fe.ep);
            }
            plan.upgrades = ComputeUpgrades(donors, scored, plan.eligible, mainEps, season, conf.donorUpgradeMinScore, plan.upWhy, h.targetQuality, donorSig);
            foreach (int ep in plan.upgrades)
                if (!plan.wanted.Contains(ep)) plan.wanted.Add(ep);
            if (plan.upgrades.Count > 0) { plan.wanted.Sort(); plan.upBase = UpgradeBaselines(donors, scored, mainEps, h.targetQuality); }
        }

        if (plan.atCap) plan.wanted = localOnly ? new List<int>() : new List<int>(plan.upgrades);   // слотов нет: только ради замены на лучшее

        plan.drops = DropCounts(scored, h);
        plan.selfClaim = localOnly ? 0 : SelfTopicClaim(scored, h);   // сигнал re-grab — дело трекерного прохода
        plan.probes = ProbeCandidates(ProbePool(plan.eligible, season, plan.wanted, plan.upgrades, plan.upBase, h.targetQuality, conf.donorUpgradeMinScore),
                                      season, plan.wanted, conf.donorProbesPerRun, h.targetQuality);
        return plan;
    }

    // ── подготовка прохода (IO) ────────────────────────────────────────────
    sealed class HuntPrep
    {
        public bool ok;                    // дошли до плана
        public string skip;                // почему нет (для лога/dry-отчёта)
        public string mainHash, stitle, ctitle, mainName;
        public JArray mainFiles, donors, scored;
        public int season, aired, donorsCount;
        public bool barren, localWaiting, localChanged;
        public bool bmFail, bmTruncated;   // статус сезонной выборки bitmagnet (BmScopedStatus)
        public string localFp;
        public HuntPlan plan;
        public DateTime now;
    }

    static async Task<HuntPrep> HuntPrepare(HttpClient c, JObject m, bool localOnly, bool dry)
    {
        var p = new HuntPrep { now = DateTime.UtcNow };
        var conf = ModInit.conf;
        string mainHash = m.Value<string>("hash");
        if (!ValidHash(mainHash)) { p.skip = "invalid-hash"; return p; }
        p.mainHash = mainHash;

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
        if (!isSerial) { p.skip = "not-serial"; return p; }

        string ctitle = ctx?.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("query");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) { p.skip = "no-title"; return p; }
        p.ctitle = ctitle;
        p.stitle = string.IsNullOrWhiteSpace(m.Value<string>("title")) ? ctitle : m.Value<string>("title");

        var mainFiles = await QbitFiles(c, mainHash);
        if (mainFiles == null || mainFiles.Count == 0) { p.skip = "main-no-metadata"; return p; }   // основная сама ещё без метаданных
        p.mainFiles = mainFiles;
        p.mainName = (await QbitTorrentInfo(c, mainHash))?.Value<string>("name");

        PruneBlacklist(m, p.now);
        var donors = m["donors"] as JArray;
        p.donors = donors;
        p.donorsCount = donors?.Count ?? 0;

        int season = DominantSeason(mainFiles);
        if (season <= 0) season = Math.Max(1, ctx?.Value<int?>("season") ?? 1);
        p.season = season;

        // Кап доноров. При включённом апгрейде проход всё же идём: замена плохой серии на хорошую
        // важнее экономии одного слота, а перебор ограничен ровно +1 донором и самоустраняется —
        // проигравший уходит в ScanReplacements, опустевший донор снимается.
        bool atCap = (donors?.Count ?? 0) >= conf.donorMaxPerSeries;
        if (atCap && (!conf.donorUpgrade || localOnly)) { p.skip = "cap"; return p; }

        string tmdbId = conf.huntBitmagnet && (m.Value<int?>("id") ?? 0) > 0 ? m.Value<int?>("id").ToString() : null;
        string titleOriginal = ctx?.Value<string>("title_original");
        int year = ctx?.Value<int?>("year") ?? 0;

        int tmdbNum = m.Value<int?>("id") ?? 0;
        if (localOnly)
        {
            // Локальный тик: только сериалы, у которых есть чего ждать; только локальные базы;
            // только при изменении состава скоупа. Ни одного похода в трекеры и ни строки лога впустую.
            if (tmdbId == null) { p.skip = "no-tmdb"; return p; }
            p.localWaiting = LocalTickWaiting(m, mainFiles, donors, season);
            if (!p.localWaiting) { p.skip = "nothing-wanted"; return p; }
            // Потолок серий — ТОЛЬКО из кеша эфира (правило «TMDB из кеша»: поход в прокси под _watchGate
            // стоил бы 15 с на каждый сдвиг состава при лежащем TMDB и снимал бы потолок, отправляя паки
            // «1-10 из 10» при вышедших 9 в пробу и no-episode на 30 дней). Кеша нет (рестарт, трекеры ещё
            // не отвечали) → потолок = максимум localWanted: эти серии полный проход уже сверил с эфиром.
            p.aired = conf.tmdbAiredCap ? AiredCached(tmdbNum, season) : 0;
            if (conf.tmdbAiredCap && p.aired <= 0)
            {
                var lw = (m["hunt"] as JObject)?["localWanted"] as JArray;
                p.aired = lw == null ? 0 : lw.Select(x => x.Value<int>()).DefaultIfEmpty(0).Max();
                if (p.aired <= 0) { p.skip = "no-aired-cache"; return p; }
            }
            var (raw, fp) = await LocalFetch(ctitle, year, season, tmdbId);
            var bmSt = BmScopedStatus(tmdbId, season);
            p.bmTruncated = bmSt.truncated;
            // Отказ источника ≠ «строк нет»: отпечаток не трогаем, скоринга и лога нет — следующий тик повторит.
            if (!bmSt.ok) { p.bmFail = true; p.skip = "bitmagnet-fail"; return p; }
            p.localFp = fp;
            p.localChanged = !(_localFp.TryGetValue(mainHash, out var prevFp) && prevFp == fp);
            if (!p.localChanged && !dry) { p.skip = "unchanged"; return p; }
            p.scored = ScoreResult(raw, ctitle, ctitle, titleOriginal, year, 2, season, tmdbId, store: false);
        }
        else
        {
            // Кеш эфира заполняем ДО похода в трекеры: он нужен локальному тику (ветка «вышло больше, чем
            // есть»), а при лежащем индексаторе после рестарта иначе оставался бы пустым до первой удачной
            // выдачи — и тик молчал бы ровно тогда, когда серия есть только в DHT.
            p.aired = conf.tmdbAiredCap ? await AiredEpisodes(tmdbNum, season) : 0;
            // Полный проход: трекеры + bitmagnet сезонным скоупом (без записи кеша/индекса) + наш индекс.
            p.scored = await SearchScored(ctitle, ctitle, titleOriginal, year, 2, season, null, tmdbId, tmdbId != null ? season : 0);
            if (tmdbId != null) { var bmSt = BmScopedStatus(tmdbId, season); p.bmFail = !bmSt.ok; p.bmTruncated = bmSt.truncated; }
            // Пусто = либо трекеры отдали ошибку (JacRed InternalServerError и т.п.), либо индексатор лёг.
            // Это НЕ «новых серий нет»: штамп не ставим (иначе следующая диагностика соврёт), сигналим
            // наверх — HuntAll попросит таймер прийти раньше.
            if (p.scored.Count == 0) { p.barren = true; p.skip = "barren"; return p; }
        }

        var lampaHashes = new List<string>();
        try
        {
            var mainCat = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}"));
            foreach (var it in mainCat) { var hh = it.Value<string>("hash"); if (!string.IsNullOrEmpty(hh)) lampaHashes.Add(hh); }
        }
        catch { }

        // подписи файлов текущих доноров — чтобы апгрейд не взял тот же контент под другим btih
        HashSet<string> donorSig = null;
        if (!localOnly && donors != null && donors.Count > 0)
        {
            donorSig = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in donors.OfType<JObject>())
            {
                string dh = d.Value<string>("hash");
                if (!ValidHash(dh)) continue;
                var df = await QbitFiles(c, dh);
                if (df != null) donorSig.UnionWith(MainSignature(df));
            }
        }

        p.plan = BuildHuntPlan(m, mainFiles, p.mainName, donors, donorSig, p.scored, lampaHashes, ctitle, titleOriginal, season, p.aired, p.now, conf, localOnly);
        p.ok = true;
        return p;
    }

    // records — весь список записей прохода (HuntAll): нужен, чтобы не усыновить донора ДРУГОЙ записи.
    static async Task<HuntOneResult> HuntOne(HttpClient c, JObject m, bool localOnly = false, JArray records = null)
    {
        var res = new HuntOneResult();
        var conf = ModInit.conf;
        var p = await HuntPrepare(c, m, localOnly, dry: false);
        if (!p.ok)
        {
            if (p.skip == "cap" && !localOnly)
            {
                SetHuntStamp(m, p.now, 0); res.changed = true;
                Console.WriteLine($"[QbitDownload] hunt «{p.stitle}» S{p.season}: пропуск — доноров уже {p.donorsCount}/{conf.donorMaxPerSeries}");
            }
            else if (p.barren)
            {
                MarkHuntBarren(m, p.now); res.changed = true;
                res.searched = true; res.barren = true;
                Console.WriteLine($"[QbitDownload] hunt «{p.stitle}» S{p.season}: индексатор не дал кандидатов (подряд {(m["hunt"] as JObject)?.Value<int?>("emptyStreak") ?? 1}) — проход не засчитан");
            }
            else if (localOnly)
            {
                res.waiting = p.localWaiting;
                res.localSkipped = true;
                res.bmFail = p.bmFail;
            }
            return res;
        }

        var plan = p.plan; var h = plan.h;
        int season = p.season; string stitle = p.stitle; var now = p.now;
        var mainFiles = p.mainFiles; var scored = p.scored;
        bool atCap = plan.atCap;
        var wanted = plan.wanted;

        if (localOnly)
        {
            res.waiting = true; res.newRows = true;
            SetLocalStamp(m, now); res.changed = true;
        }
        else
        {
            res.searched = true;
            SetHuntStamp(m, now, plan.maxClaim);
            SetLocalWanted(m, plan.missing);
            res.changed = true;
            if (plan.aired > 0 && plan.claimBeforeCap > plan.aired)
                Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: потолок серий {plan.claimBeforeCap} → {plan.aired} (по TMDB вышло {plan.aired})");
            if (plan.upgrades.Count > 0)
                Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: апгрейд донорских серий — {string.Join("; ", plan.upgrades.Select(x => plan.upWhy[x]))}");
        }

        // fingerprint состава фиксируем только на НОРМАЛЬНОМ выходе (обе точки ниже): исключение посреди
        // проб (qBit оборвался после add) — состав не считается обработанным, следующий тик повторит план.
        void FpDone() { if (localOnly) _localFp[p.mainHash] = p.localFp; }

        string tag = localOnly ? "hunt-local" : "hunt";
        string claimNote = plan.maxClaim != plan.eligibleClaim ? $" (годные заявляют {plan.eligibleClaim})" : "";
        string upNote = plan.upgrades.Count > 0 ? $", апгрейд {plan.upgrades.Count}" : "";
        string capNote = atCap ? $" [доноров {p.donorsCount}/{conf.donorMaxPerSeries} — только апгрейд]" : "";
        string bmNote = p.bmTruncated ? ", срез по bitmagnetHuntLimit" : p.bmFail ? ", bitmagnet недоступен" : "";
        Console.WriteLine($"[QbitDownload] {tag} «{stitle}» S{season}: кандидатов {scored.Count} (bitmagnet {plan.bitmagnet}{bmNote}) → годных {plan.eligible.Count}{DropSummary(scored, plan.eligible.Count, h)}; цель {h.targetQuality}p; заявлено серий до {plan.maxClaim}{claimNote}, нужно {WantedText(wanted)}{upNote}{capNote}");

        // Свой топик перевыложен с бо́льшим числом серий. Донором его брать НЕЛЬЗЯ (§AK: он вот-вот
        // станет основной, и контур замещения снёс бы его С ФАЙЛАМИ) — владелец обновления только
        // re-grab в CheckWatches. Раньше охота молча выбрасывала этот кандидат, и обновление
        // раздачи ждало своего 6-часового тика («Великий расхититель гробниц»: 2 серии вместо 5).
        if (!localOnly)
        {
            int mainVideos = mainFiles.Count(f => _videoExtRx.IsMatch(f.Value<string>("name") ?? ""));
            if (plan.selfClaim > mainVideos)
            {
                res.regrab = true;
                Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: свой топик заявляет {plan.selfClaim} серий, у основной {mainVideos} файлов — раздача перевыложена, запрашиваю re-grab");
            }
        }

        if (wanted.Count == 0) { FpDone(); return res; }   // новее ничего не заявлено — основная и так самая полная

        int grabbed = 0, probes = 0;
        long minB = conf.epSizeMinMb * 1024L * 1024, maxB = conf.epSizeMaxGb * 1024L * 1024 * 1024;
        var inv = plan.inv;

        // Топ-N из выдачи, N = donorProbesPerRun: первая по релевантности раздача часто не подтверждается
        // файлами (нет серии/нет метаданных), и на топ-1 проход уходил впустую до следующего интервала.
        // Перебор всё равно жёстко ограничен: probes ниже и кап donorMaxPerSeries (перечитывается на
        // каждой итерации — доноры добавляются прямо в цикле).
        foreach (var cand in plan.probes)
        {
            if (probes >= Math.Max(1, conf.donorProbesPerRun)) break;
            if (((m["donors"] as JArray)?.Count ?? 0) >= conf.donorMaxPerSeries + (atCap ? 1 : 0)) break;
            string parselink = cand.Value<string>("parselink");
            string magnet = cand.Value<string>("magnet");
            if (string.IsNullOrWhiteSpace(magnet))
            {
                magnet = await ResolveMagnetStatic(parselink);
                if (string.IsNullOrWhiteSpace(magnet))
                {
                    // сеть/трекер, а не дефект раздачи → короткая пауза с бэкоффом; в учёте серий
                    // (ClaimCandidates) кандидат при этом ОСТАЁТСЯ
                    int at = BlacklistAttempts(m, parselink, "resolve-failed") + 1;
                    int ttl = TransientFailMinutes(at);
                    BlacklistAddMinutes(m, null, parselink, "resolve-failed", ttl, at);
                    res.changed = true;
                    Console.WriteLine($"[QbitDownload] hunt: парселинк не резолвится (попытка {at}) — пауза {ttl} мин («{cand.Value<string>("title")}»)");
                    continue;
                }
            }
            string btih = MagnetHash(magnet);
            if (string.IsNullOrEmpty(btih) || h.knownHashes.Contains(btih) || h.blacklistKeys.Contains(btih)) continue;
            // проба = поход в qBit; уже известный/забаненный btih, выяснившийся после резолва, слот не тратит
            probes++; res.probes = probes; res.changed = true;

            // двойная страховка (сверх knownHashes из категории lampa): если инфохеш уже есть в qBit
            // и это НЕ наш донор — чужая загрузка, не усыновляем (QbitAddMagnetEx на дубле неотличим от add)
            var pre = await QbitTorrentInfo(c, btih);
            if (pre != null && pre.Value<string>("category") != DonorCategory)
            { BlacklistAdd(m, btih, parselink, "foreign", conf.donorBlacklistTtlDays); continue; }

            // двухфазный захват: add со стопом после метаданных → подтверждение по файлам
            var addSt = await QbitAddMagnetStatus(c, magnet, DonorCategory, DonorTag, stopAfterMeta: true);
            if (addSt == QbitAddStatus.Failed)
            {
                int at = BlacklistAttempts(m, btih, "add-failed") + 1;
                BlacklistAddMinutes(m, btih, parselink, "add-failed", TransientFailMinutes(at), at);   // сбой qBit — транзиент
                continue;
            }
            if (addSt == QbitAddStatus.Duplicate)
            {
                // Торрент уже сидел в qBit. Усыновляем ТОЛЬКО собственного сироту: донор нашей категории, на
                // которого не ссылается другая запись (add прошёл, watch.json не сохранился). Остальное — чужое:
                // pre-check не ответил (qBit моргнул) → это мог быть пользовательский торрент категории lampa,
                // и filePrio(all,0) ниже остановил бы ему загрузку; донор ДРУГОЙ записи (общий многосезонный
                // пак) → его выбор файлов сбросился бы, а delete-donor первой записи снёс бы файлы второй.
                bool ownOrphan = pre != null && pre.Value<string>("category") == DonorCategory && !OtherRecordsReference(records, m, btih);
                if (!ownOrphan)
                {
                    BlacklistAdd(m, btih, parselink, "foreign", conf.donorBlacklistTtlDays);
                    Console.WriteLine($"[QbitDownload] {tag}: кандидат {btih} уже сидит в qBit не нашим донором — не усыновляем («{cand.Value<string>("title")}»)");
                    continue;
                }
            }

            // Аварийные выходы ниже удаляют кандидата С ФАЙЛАМИ — только через QbitDeleteDonorSafe.
            // Оба гейта выше (knownHashes из категории lampa и pre-check) FAIL-OPEN: запрос к qBit мог
            // упасть/таймаутнуть, тогда «наш новый донор» — на самом деле чужой торрент, а add на дубликате
            // категорию не сменил. Слепой QbitDelete(..., true) снёс бы чужие файлы.
            var dfiles = await QbitWaitFiles(c, btih, conf.donorMetadataTimeoutSec);
            if (dfiles == null || dfiles.Count == 0)
            {
                await QbitDeleteDonorSafe(c, btih, p.mainHash);
                int at = BlacklistAttempts(m, btih, "meta-timeout") + 1;
                BlacklistAddMinutes(m, btih, parselink, "meta-timeout", TransientFailMinutes(at), at);   // возможно, просто не было сидов
                continue;
            }

            // Сезонный отсев ПО ФАЙЛАМ — последний рубеж перед скачиванием. Название могло соврать
            // (kinozal «2 сезон: 1-10 серии» долго читался как сезоны 1..10), файлы — не врут.
            // Многосезонный пак ({2,3} при охоте на 3) не отвергаем: из него возьмутся только файлы S03.
            var dseasons = DonorSeasons(dfiles);
            if (season > 0 && dseasons.Count > 0 && !dseasons.Contains(season))
            {
                await QbitDeleteDonorSafe(c, btih, p.mainHash);   // только safe-путь (§AK)
                BlacklistAdd(m, btih, parselink, "wrong-season", conf.donorBlacklistTtlDays);
                Console.WriteLine($"[QbitDownload] hunt: донор {btih} отвергнут — в файлах сезон(ы) {string.Join(",", dseasons.OrderBy(x => x))}, охотим S{season} («{cand.Value<string>("title")}»)");
                continue;
            }

            var titleEp = ParseEp(StripSeasonMarks(cand.Value<string>("title") ?? ""));
            // сезон донора: файлы → название → сезон из episodes классификатора bitmagnet (у scene-одиночек
            // «Silo.Ep10.mkv» в имени сезона нет, а bm_season его знает)
            int donorSeason = DonorSeason(dfiles, cand.Value<string>("title"));
            if (donorSeason <= 0) donorSeason = cand.Value<int?>("bm_season") ?? 0;
            var found = FindEpFiles(dfiles, season, wanted, titleEp, donorSeason);
            found.RemoveAll(f => f.size > 0 && (f.size < minB || f.size > maxB));   // теперь вес известен точно
            if (found.Count == 0)
            {
                await QbitDeleteDonorSafe(c, btih, p.mainHash);
                // Ключ — ТОЛЬКО btih (qdl 2.107): у обновляемого топика parselink стабилен, и бан по нему
                // выключал лучший 1080p-пак на 30 дней, хотя следующая перевыкладка (новый btih) серию
                // уже несёт. Тот же btih остаётся забаненным; перевыкладка = 5–7 btih за сезон → ~1 проба.
                BlacklistAddNoEpisode(m, btih, parselink, cand.Value<string>("title"), conf.donorBlacklistTtlDays);
                Console.WriteLine($"[QbitDownload] {tag}: донор {btih} отвергнут — нужных серий в файлах нет («{cand.Value<string>("title")}»)");
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
                ["sid"] = cand.Value<int?>("sid") ?? 0, ["addedAt"] = now.ToString("o"),
                // база для сравнения при апгрейде: с чем именно мы согласились, когда брали эту серию
                ["score"] = cand.Value<double?>("score") ?? 0, ["quality"] = cand.Value<int?>("quality") ?? 0,
                ["eps"] = epsArr
            });
            h.knownHashes.Add(btih);
            grabbed += found.Count;
            Console.WriteLine("[QbitDownload] " + tag + ": донор " + btih + " (" + cand.Value<string>("tracker") + ", " + (cand.Value<int?>("quality") ?? 0) + "p) — серии " + string.Join(",", found.Select(f => f.ep)) + " для «" + m.Value<string>("title") + "»");
            if (wanted.Count == 0) break;
        }
        if (grabbed > 0)
        {
            ActivityTouch(h.mainHash);   // карточка всплывает в момент ЗАХВАТА серии, не дожидаясь докачки
            TrimLocalWanted(m, inv);     // добытое — из hunt.localWanted, иначе тик до следующего полного прохода считал бы сериал «ожидающим»
        }
        else
            Console.WriteLine($"[QbitDownload] {tag} «{stitle}» S{season}: ничего не добыто (проб {probes} из {plan.eligible.Count} годных)");
        res.grabbed = grabbed;
        FpDone();
        return res;
    }

    // Ссылается ли на торрент другая запись прохода (её основная или её донор).
    static bool OtherRecordsReference(JArray all, JObject self, string hash)
    {
        if (all == null || string.IsNullOrEmpty(hash)) return false;
        string selfHash = self?.Value<string>("hash");
        foreach (var x in all.OfType<JObject>())
        {
            if (ReferenceEquals(x, self) || (!string.IsNullOrEmpty(selfHash) && selfHash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase))) continue;
            if (hash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)) return true;
            if (x["donors"] is JArray ds && ds.OfType<JObject>().Any(d => hash.Equals(d.Value<string>("hash"), StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    // Добытые серии вычитаем из hunt.localWanted (список пишет полный проход ДО захвата).
    static void TrimLocalWanted(JObject m, HashSet<int> inv)
    {
        if (inv == null || (m["hunt"] as JObject)?["localWanted"] is not JArray lw || lw.Count == 0) return;
        var keep = lw.Select(x => x.Value<int>()).Where(e => !inv.Contains(e)).ToList();
        if (keep.Count != lw.Count) ((JObject)m["hunt"])["localWanted"] = new JArray(keep);
    }

    static int AiredCached(int id, int season)
        => id > 0 && _airedCache.TryGetValue(id + ":" + season, out var e) ? e.aired : 0;

    // Сколько кандидатов пробуем за проход: топ-N в порядке OrderByCover (уверенные Yes, затем Maybe).
    // Кламп ≥1 — нулевой/отрицательный donorProbesPerRun не должен выключать охоту молча.
    static List<JObject> ProbeCandidates(List<JObject> eligible, int season, List<int> wanted, int probesPerRun, int targetQuality)
        => OrderByCover(eligible, season, wanted, targetQuality).Take(Math.Max(1, probesPerRun)).ToList();

    // ── локальный тик (qdl 2.107): только bitmagnet + наш индекс, без трекеров ──────────────
    // fingerprint скоупа на сериал: совпал с прошлым тиком → выход до скоринга и без лога.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _localFp = new(StringComparer.OrdinalIgnoreCase);
    static DateTime _lastLocalRun = DateTime.MinValue, _lastLocalSummary = DateTime.MinValue;
    static int _ltTicks, _ltBusy, _ltWaiting, _ltNewRows, _ltProbes, _ltGrabbed, _ltBmFail;

    // Есть ли у сериала чего ждать — ЛЮБОЕ из двух: полный проход записал недостающие серии
    // (hunt.localWanted — серии, которые кто-то на трекерах уже заявил, а у нас их нет), ИЛИ по кешу
    // эфира TMDB вышло больше, чем лежит в инвентаре (серия вышла, но её ещё нет ни на одном
    // трекере — ровно тот случай, ради которого тик и ходит в DHT каждые 10 минут). Иначе — нечего,
    // ни одного SELECT. 🐞 05.09.2026: первая версия возвращала lw.Count > 0, как только localWanted
    // был записан, и до ветки эфира не доходила — «Чёрный Факел» с вышедшей по TMDB E10 без единой
    // трекерной заявки тик не опрашивал.
    static bool LocalTickWaiting(JObject m, JArray mainFiles, JArray donors, int season)
    {
        if ((m["hunt"] as JObject)?["localWanted"] is JArray lw && lw.Count > 0) return true;
        var inv = InventoryEps(mainFiles, donors, season);
        string key = (m.Value<int?>("id") ?? 0) + ":" + season;
        if (_airedCache.TryGetValue(key, out var e)) return e.aired > (inv.Count > 0 ? inv.Max() : 0);
        return false;
    }

    // Локальная выборка: сезонный скоуп bitmagnet ∪ bitmagnet-эхо нашего индекса (трекерное эхо не берём —
    // его проба требует резолва парселинка на трекере, это дело четырёхчасового прохода). Возвращает
    // сырые строки (дедуп по btih, санитайз магнетов — как в SearchScored) и fingerprint состава.
    static async Task<(JArray raw, string fp)> LocalFetch(string ctitle, int year, int season, string tmdbId)
    {
        var bm = await FetchBitmagnet(tmdbId, 2, season);
        string qn = Shared.Services.Utilities.SearchNameTo.Convert(ctitle) ?? "";
        var li = await FetchLocalIndex(tmdbId, qn, year, 2);

        var raw = new JArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fpKeys = new List<string>();
        // fingerprint — ТОЛЬКО скоупная выборка bitmagnet (она без среза: bitmagnetHuntLimit выше числа
        // строк сезона). Эхо индекса в отпечаток не входит: это окно top-200 по sid/last_seen, которое
        // перетасовывает каждый /qdl/search и обходчик (IndexStore обновляет sid и last_seen у всех строк
        // выдачи) — состав «менялся» бы без единой новой DHT-строки, а тик на каждое такое «изменение»
        // делал скоринг, писал лог и watch.json.
        foreach (var (t, isBm) in bm.OfType<JObject>().Select(x => (x, true)).Concat(li.OfType<JObject>().Where(x => x.Value<string>("tracker") == "bitmagnet").Select(x => (x, false))))
        {
            string mag = t.Value<string>("magnet");
            if (!string.IsNullOrWhiteSpace(mag))
            {
                string clean = SanitizeMagnet(mag);
                if (!ReferenceEquals(clean, mag)) { mag = clean; t["magnet"] = clean; }
            }
            string key = !string.IsNullOrWhiteSpace(mag) ? MagnetHash(mag) : t.Value<string>("parselink");
            if (!string.IsNullOrEmpty(key) && !seen.Add(key)) continue;
            raw.Add(t);
            if (isBm && !string.IsNullOrEmpty(key)) fpKeys.Add(key);
        }
        fpKeys.Sort(StringComparer.Ordinal);
        return (raw, Fnv(string.Join(",", fpKeys)) + ":" + fpKeys.Count);
    }

    // Таймер зовёт раз в минуту; интервал — из conf на лету (huntLocalIntervalMinutes, 0 = выкл).
    public static async Task HuntLocalTick()
    {
        var conf = ModInit.conf;
        if (conf == null || !conf.episodeHunt || !conf.huntBitmagnet) return;
        int iv = conf.huntLocalIntervalMinutes;
        if (iv <= 0) return;
        var now = DateTime.UtcNow;
        if ((now - _lastLocalRun).TotalMinutes < iv) return;
        _lastLocalRun = now;
        Interlocked.Increment(ref _ltTicks);

        await HuntAll(null, localOnly: true);

        int sm = Math.Max(5, conf.huntLocalSummaryMinutes);
        if ((now - _lastLocalSummary).TotalMinutes >= sm)
        {
            _lastLocalSummary = now;
            Console.WriteLine($"[QbitDownload] hunt-local: сводка — тиков {_ltTicks}, занят гейт {_ltBusy}, сериалов с ожиданием {_ltWaiting}, новых строк у {_ltNewRows}, проб {_ltProbes}, добыто {_ltGrabbed}{(_ltBmFail > 0 ? $", сбоев bitmagnet {_ltBmFail}" : "")} (интервал {iv} мин)");
            _ltTicks = 0; _ltBusy = 0; _ltWaiting = 0; _ltNewRows = 0; _ltProbes = 0; _ltGrabbed = 0; _ltBmFail = 0;
        }
    }

    // ── сухой прогон (qdl 2.107) ─────────────────────────────────────────────
    // Единственный след решений охоты — Console.WriteLine, живущий пока жив контейнер; dry-run отдаёт
    // весь план по каждой записи, ничего не меняя: работает на DeepClone записи, без _watchGate
    // (только чтение под _watchLock), без add/blacklist/штампов/SaveWatch; поиск — со store:false.
    static async Task<JArray> HuntDry(string onlyHash, bool localOnly)
    {
        var items = new JArray();
        JArray list; lock (_watchLock) { list = LoadWatch(); }
        using var c = await Qbit();
        foreach (var m0 in list.OfType<JObject>())
        {
            if (onlyHash != null && !onlyHash.Equals(m0.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)) continue;
            var m = (JObject)m0.DeepClone();
            JObject rep;
            try { rep = DryReport(m, await HuntPrepareDry(c, m, localOnly)); }
            catch (Exception ex) { rep = new JObject { ["hash"] = m0.Value<string>("hash"), ["title"] = m0.Value<string>("title"), ["error"] = ex.Message }; }
            items.Add(rep);
        }
        return items;
    }

    // Обёртка ради теста «dry не пишет»: сухой путь ничего не меняет ни в qBit, ни в watch.json, ни в
    // blacklist/штампах. Поиск: при localOnly — LocalFetch/ScoreResult(store:false); при полном режиме
    // SearchScored со скоупом (bmScopeSeason > 0) не пишет ни кеш, ни индекс. Оговорка: у записи без
    // tmdb id (или при huntBitmagnet:false) скоупа нет, и SearchScored пишет кеш/индекс — ровно то же,
    // что пишет и обычный /qdl/search, и боевой проход в той же конфигурации; DHT-строк там нет.
    static Task<HuntPrep> HuntPrepareDry(HttpClient c, JObject m, bool localOnly) => HuntPrepare(c, m, localOnly, dry: true);

    static JObject DryReport(JObject m, HuntPrep p)
    {
        var o = new JObject
        {
            ["hash"] = m.Value<string>("hash"), ["title"] = m.Value<string>("title"), ["season"] = p.season,
            ["skip"] = p.skip, ["waiting"] = p.localWaiting, ["fingerprintChanged"] = p.localChanged
        };
        if (!p.ok) return o;
        var plan = p.plan; var h = plan.h;
        o["target"] = h.targetQuality;
        o["candidates"] = p.scored.Count; o["bitmagnet"] = plan.bitmagnet; o["bitmagnetTruncated"] = p.bmTruncated; o["eligible"] = plan.eligible.Count;
        o["drops"] = JObject.FromObject(plan.drops);
        o["maxClaim"] = plan.maxClaim; o["aired"] = p.aired; o["atCap"] = plan.atCap;
        o["inventory"] = new JArray(plan.inv.OrderBy(x => x));
        o["missing"] = new JArray(plan.missing); o["wanted"] = new JArray(plan.wanted); o["upgrades"] = new JArray(plan.upgrades);
        o["selfClaim"] = plan.selfClaim;

        JObject row(JObject t, int ep)
        {
            var r = new JObject
            {
                ["title"] = t.Value<string>("title"), ["tracker"] = t.Value<string>("tracker"),
                ["quality"] = t.Value<int?>("quality") ?? 0, ["rank"] = QualityRank(t.Value<int?>("quality") ?? 0, h.targetQuality),
                ["sid"] = t.Value<int?>("sid") ?? 0, ["sid_hint"] = t.Value<bool?>("sid_hint") == true,
                ["score"] = t.Value<double?>("score") ?? 0, ["btih"] = MagnetHash(t.Value<string>("magnet"))
            };
            if (ep > 0) r["cover"] = TitleCoversEpItem(t, p.season, ep).ToString();
            return r;
        }
        int firstEp = plan.wanted.Count > 0 ? plan.wanted[0] : (plan.missing.Count > 0 ? plan.missing[0] : 0);
        var ordered = plan.wanted.Count > 0 ? OrderByCover(plan.eligible, p.season, plan.wanted, h.targetQuality) : plan.eligible.ToList();
        if (plan.wanted.Count == 0) ordered.Sort(DonorOrder(h.targetQuality));
        o["order"] = new JArray(ordered.Take(15).Select(t => row(t, firstEp)));
        o["wouldProbe"] = new JArray(plan.probes.Select(t => row(t, firstEp)));
        var dropped = new JArray();
        foreach (var t in p.scored.OfType<JObject>())
        {
            if (dropped.Count >= 30) break;
            string r = DropReason(t, h);
            if (r == null) continue;
            var d = row(t, 0); d["reason"] = r; dropped.Add(d);
        }
        o["dropped"] = dropped;
        return o;
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/hunt/run")]
    async public Task<ActionResult> HuntRun(string hash = null, int dry = 0, int local = 0)
    {
        string only = string.IsNullOrWhiteSpace(hash) ? null : hash;
        if (only != null)
        {
            // Опечатка в хеше или снятая запись иначе выглядели бы как «wanted пуст / ничего не добыто».
            bool known; lock (_watchLock) { known = LoadWatch().OfType<JObject>().Any(x => only.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)); }
            if (!known)
                return ContentTo(new JObject { ["success"] = false, ["error"] = "hash не в watch.json" }.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        if (dry == 1)
        {
            // сухой прогон только читает — годится и на реплике, поэтому ДО ReplicaReadOnlyDeny.
            // ContentTo, а не Json(...): System.Text.Json про JToken не знает (см. SeasonWatchCheck).
            // busy — «за время прогона гейт был занят»: снимок watch.json берётся в начале, и живой проход,
            // уложившийся внутрь сухого, иначе оставался бы невидимым.
            bool busyBefore = _watchGate.CurrentCount == 0;
            var items = await HuntDry(only, local == 1);
            var body = new JObject { ["success"] = true, ["dry"] = true, ["local"] = local == 1, ["busy"] = busyBefore || _watchGate.CurrentCount == 0, ["items"] = items };
            return ContentTo(body.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;   // охота живёт только дома
        int n = await HuntAll(only, local == 1);
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

        string mainHash = item.Value<string>("hash") ?? "";

        // Сезон основной для самолечения записей: только ОДНОЗНАЧНЫЙ. 0 (не определить или пак из
        // нескольких сезонов) → чужой сезон у донора не диагностируем и ничего не трогаем.
        var mainSeasons = DonorSeasons(mainFiles);
        int mainSeason = mainSeasons.Count == 1 ? mainSeasons.First() : 0;

        // Апгрейд: одну и ту же серию держат два донора (охота добавила лучшего). Победитель —
        // ДОКАЧАННЫЙ с лучшим рангом качества (qdl 2.107; тот же компаратор, что у охоты), затем бакет
        // байт/серия, затем score; проигравшего снимаем. Пока новый файл не готов, старый не трогаем
        // ни при каких условиях — иначе зритель остался бы без серии.
        int target = DonorTargetQuality(mainFiles, ModInit.conf);
        var upgradeLosers = new HashSet<JObject>();
        {
            var byEp = new Dictionary<string, List<(JObject donor, JObject ep, double score, int rank, int bucket, bool done)>>();
            foreach (var d in donors.OfType<JObject>())
            {
                string dh0 = d.Value<string>("hash") ?? "";
                if (donorFiles == null || !donorFiles.TryGetValue(dh0, out JArray df) || df == null) continue;
                double sc = d.Value<double?>("score") ?? -1;
                int rank = QualityRank(d.Value<int?>("quality") ?? 0, target);
                foreach (var e in (d["eps"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (e.Value<string>("status") != "hunted") continue;
                    int en0 = e.Value<int?>("ep") ?? -1, es0 = e.Value<int?>("season") ?? -1;
                    if (en0 < 0) continue;
                    int fi = e.Value<int?>("fileIndex") ?? -1;
                    var f = df.FirstOrDefault(x => (x.Value<int?>("index") ?? -1) == fi);
                    bool done = f != null && (f.Value<double?>("progress") ?? 0) >= 0.999;
                    int bucket = SizeBucket(f?.Value<long?>("size") ?? 0);
                    string k = es0 + ":" + en0;
                    if (!byEp.TryGetValue(k, out var lst)) byEp[k] = lst = new List<(JObject, JObject, double, int, int, bool)>();
                    lst.Add((d, e, sc, rank, bucket, done));
                }
            }
            foreach (var kv in byEp)
            {
                if (kv.Value.Count < 2) continue;
                var winner = kv.Value.Where(x => x.done).OrderBy(x => x.rank).ThenByDescending(x => x.bucket).ThenByDescending(x => x.score).FirstOrDefault();
                if (winner.ep == null) continue;   // ни одна копия не докачана — ждём
                foreach (var x in kv.Value)
                {
                    if (ReferenceEquals(x.ep, winner.ep)) continue;
                    // НЕдокачанную копию не хуже победителя (ранг ≤, при равном ранге score ≥) не трогаем:
                    // это и есть апгрейд в полёте, ради которого охота её и добавила. Снести её значило бы
                    // отменять апгрейд каждый проход и качать заново (§BA-5).
                    if (!x.done && (x.rank < winner.rank || (x.rank == winner.rank && (x.bucket > winner.bucket || (x.bucket == winner.bucket && x.score >= winner.score))))) continue;
                    upgradeLosers.Add(x.ep);
                }
            }
        }

        foreach (var d in donors.OfType<JObject>())
        {
            string dh = d.Value<string>("hash") ?? "";

            // Донор == основная раздача: топик перерегистрировали, CheckWatches пере-резолвил основную
            // в тот же infohash, а донорская запись осталась. Дальше пошло бы сравнение торрента с самим
            // собой → ложное «серия замещена основной» → delete-donor снёс бы весь сериал С ФАЙЛАМИ
            // (инцидент 2026-07-25, «Укрытие»). Ничего не удаляем — просто забываем запись донора.
            if (dh.Length > 0 && dh.Equals(mainHash, StringComparison.OrdinalIgnoreCase))
            { res.Add(new ReplaceAction { kind = "forget-donor", donorHash = dh, donor = d }); continue; }

            // Ключа нет = состояние донора НЕИЗВЕСТНО (qBit не ответил) → в этом проходе не трогаем:
            // иначе временный сбой сети выглядел бы как «серия замещена» и донор ушёл бы с файлами.
            if (donorFiles == null || !donorFiles.TryGetValue(dh, out JArray dfiles)) continue;
            if (dfiles == null)
            { res.Add(new ReplaceAction { kind = "forget-donor", donorHash = dh, donor = d }); continue; }   // удалили извне

            var eps = (d["eps"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            int drops = 0;
            foreach (var e in eps)
            {
                if (e.Value<string>("status") != "hunted") continue;
                int en = e.Value<int?>("ep") ?? -1, es = e.Value<int?>("season") ?? -1;

                // Самолечение записей, сделанных до fail-closed сезонного гейта: сезон файла читается
                // уверенно и НЕ совпадает с сезоном основной → это чужой сезон, замещения не было и не
                // будет. Снимаем как drop-file (не «replaced»: серия не замещена, а ошибочна) —
                // опустевший донор дальше уйдёт штатным delete-donor через QbitDeleteDonorSafe.
                int fi0 = e.Value<int?>("fileIndex") ?? -1;
                var df0 = fi0 >= 0 ? dfiles.FirstOrDefault(x => (x.Value<int?>("index") ?? -1) == fi0) : null;
                int realSeason = df0 != null ? FileSeason(df0) : 0;
                if (mainSeason > 0 && realSeason > 0 && realSeason != mainSeason)
                {
                    res.Add(new ReplaceAction { kind = "wrong-season", donorHash = dh, fileIndex = fi0, ep = e, donor = d });
                    drops++;
                    continue;
                }

                if (upgradeLosers.Contains(e))   // ту же серию уже держит донор получше, и он докачан
                {
                    res.Add(new ReplaceAction { kind = "upgraded", donorHash = dh, fileIndex = fi0, ep = e, donor = d });
                    drops++;
                    continue;
                }

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

    // mainPaths — полные пути файлов основной раздачи: если донор указывает на ТОТ ЖЕ файл на диске
    // (общая папка, тот же рип), «замещение» удалило бы файл самой основной. Тогда файл не трогаем —
    // достаточно снять приоритет.
    static async Task DeleteDonorFile(HttpClient c, string donorHash, int fileIndex, JArray dfiles, HashSet<string> mainPaths = null)
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
            if (full == null) return;
            if (mainPaths != null && mainPaths.Contains(NormPath(full)))
            {
                Console.WriteLine("[QbitDownload] hunt: файл донора " + rel + " НЕ удаляю — это файл самой основной раздачи");
                return;
            }
            if (System.IO.File.Exists(full)) System.IO.File.Delete(full);
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
            // Пустой список файлов приходит и когда донора удалили извне, и когда qBit просто не ответил.
            // Различаем по torrents/info: живой торрент без файлов = сбой связи → ключ не кладём, и
            // PlanReplacements пропустит донора до следующего прохода (иначе — ложное «замещено»).
            var donorFiles = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in donors.OfType<JObject>())
            {
                string dh = d.Value<string>("hash");
                if (string.IsNullOrEmpty(dh)) continue;
                var df = await QbitFiles(c, dh);
                if (df != null) donorFiles[dh] = df;
                else if (await QbitTorrentInfo(c, dh) == null) donorFiles[dh] = null;
                else Console.WriteLine("[QbitDownload] hunt: файлы донора " + dh + " недоступны — пропускаю в этом проходе");
            }

            // пути файлов основной — чтобы «замещение» не удалило её собственный файл (общая папка)
            var mainInfo = await QbitTorrentInfo(c, mainHash);
            string mainSave = mainInfo?.Value<string>("save_path") ?? ModInit.conf.downloadsPath;
            var mainPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in mainFiles)
            {
                string p = NormPath(ConfinedCombine(mainSave, f.Value<string>("name")));
                if (p != null) mainPaths.Add(p);
            }

            var actions = PlanReplacements(mainFiles, m, donorFiles, DateTime.UtcNow, ModInit.conf.donorStaleDays);
            foreach (var a in actions.OrderBy(x => (x.kind == "drop-file" || x.kind == "wrong-season" || x.kind == "upgraded") ? 0 : 1))   // сперва файлы, потом снятие доноров
            {
                try
                {
                    if (a.kind == "drop-file" || a.kind == "wrong-season" || a.kind == "upgraded")
                    {
                        if (a.fileIndex >= 0) await QbitFilePrio(c, a.donorHash, new[] { a.fileIndex }, 0);
                        await DeleteDonorFile(c, a.donorHash, a.fileIndex, donorFiles.GetValueOrDefault(a.donorHash), mainPaths);
                        DropResolveCache(a.donorHash);   // файл донора удалён с диска — путь из кеша резолва больше не валиден
                        a.ep["status"] = "replaced";
                        a.ep["replacedAt"] = DateTime.UtcNow.ToString("o");
                        // Запись оказалась ОШИБОЧНОЙ (чужой сезон), а не «серия замещена основной» —
                        // значит и след в уведомлениях ошибочен. Чистим seen/noti, иначе ключ навсегда
                        // глушит настоящую серию с тем же номером: ровно так «Укрытие» потеряло S03E07
                        // (§BS). drop-file/upgraded не трогаем — там серия у зрителя реально есть.
                        if (a.kind == "wrong-season")
                            ForgetEpisodeNoti(SeriesKey(m.Value<int?>("id") ?? 0, m.Value<string>("link")), a.ep);
                        changed = true;
                        Console.WriteLine("[QbitDownload] hunt: серия " + a.ep.Value<string>("epkey") + (a.kind switch
                        {
                            "wrong-season" => " снята — файл оказался ЧУЖОГО сезона",
                            "upgraded" => " снята — ту же серию держит раздача получше",
                            _ => " замещена основной"
                        }) + " (донор " + a.donorHash + ")");
                    }
                    else if (a.kind == "delete-donor" || a.kind == "dead-donor")
                    {
                        await QbitDeleteDonorSafe(c, a.donorHash, mainHash);   // с файлами ТОЛЬКО если категория донорская и папка не общая с основной
                        DropResolveCache(a.donorHash);
                        if (a.kind == "dead-donor")
                            BlacklistAdd(m, a.donorHash, a.donor.Value<string>("link"), "dead", ModInit.conf.donorBlacklistTtlDays);
                        donors.Remove(a.donor);
                        changed = true;
                        Console.WriteLine("[QbitDownload] hunt: донор " + a.donorHash + " снят (" + a.kind + ")");
                    }
                    else if (a.kind == "forget-donor")
                    {
                        donors.Remove(a.donor);
                        changed = true;
                        Console.WriteLine("[QbitDownload] hunt: запись донора " + a.donorHash + " забыта без удаления"
                            + (a.donorHash.Equals(mainHash, StringComparison.OrdinalIgnoreCase) ? " — это сама основная раздача" : " — торрента больше нет"));
                    }
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
            // FAIL-SAFE миграции хранилища: watch.json ФИЗИЧЕСКИ отсутствует (пустой/несмигрированный
            // том /qdl-data), а в qBit живут доноры → это НЕ сироты, это потерянное состояние.
            // Уборка удалила бы всех доноров С ФАЙЛАМИ (ср. инцидент «Укрытие», claude/06 §AK).
            if (!System.IO.File.Exists(WatchFile))
            {
                Console.WriteLine("[QbitDownload] hunt: watch.json отсутствует (пустой /qdl-data? миграция тома?) — стартовая уборка доноров ПРОПУЩЕНА");
                return;
            }

            JArray list; lock (_watchLock) { list = LoadWatch(); }
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mainHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in list.OfType<JObject>())
            {
                var mh = m.Value<string>("hash"); if (!string.IsNullOrEmpty(mh)) mainHashes.Add(mh);
                if (m["donors"] is JArray ds)
                    foreach (var d in ds.OfType<JObject>())
                    { var dh = d.Value<string>("hash"); if (!string.IsNullOrEmpty(dh)) referenced.Add(dh); }
            }

            using var c = await Qbit();

            // папки пользовательских загрузок: осиротевший донор, пишущий в ту же папку (перерегистрация
            // той же раздачи / тот же рип), удаляется БЕЗ файлов — это файлы основной загрузки.
            // FAIL-SAFE: не смогли получить список — файлы не трогаем вообще.
            var mainPaths = new List<string>();
            bool pathsKnown = false;
            try
            {
                var mainCat = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}"));
                foreach (var it in mainCat) { var cp = it.Value<string>("content_path"); if (!string.IsNullOrWhiteSpace(cp)) mainPaths.Add(cp); }
                pathsKnown = true;
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] hunt: папки загрузок недоступны (" + ex.Message + ") — сирот снимаю без файлов"); }

            var inQbit = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(DonorCategory)}"));
            foreach (var t in inQbit)
            {
                string h = t.Value<string>("hash");
                if (string.IsNullOrEmpty(h)) continue;

                // Основная раздача watch-записи, застрявшая в донорской категории: промоушен не довёлся
                // (qBit моргнул на setCategory/filePrio). Это НЕ сирота — это сериал. Доводим промоушен.
                if (mainHashes.Contains(h))
                {
                    Console.WriteLine("[QbitDownload] hunt: " + h + " — основная раздача в донорской категории, довожу промоушен");
                    await PromoteDonorToMain(c, h);
                    continue;
                }
                if (referenced.Contains(h)) continue;

                bool shared = !pathsKnown || mainPaths.Any(p => PathsOverlap(t.Value<string>("content_path"), p));
                await QbitDelete(c, h, !shared);
                Console.WriteLine("[QbitDownload] hunt: осиротевший донор " + h + " удалён при старте"
                    + (shared ? " (БЕЗ файлов — общая папка с загрузкой)" : " (с файлами)"));
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] donor reconcile: " + ex.Message); }
    }

    // каскад при удалении/снятии слежения основной: доноры этой загрузки — с файлами.
    // mainContentPath передаётся, когда основную уже удалили из qBit (/qdl/delete) — иначе сверить
    // папки было бы не с чем и донор в общей папке ушёл бы вместе с файлами соседа.
    static async Task DeleteDonorsOf(HttpClient c, string mainHash, string mainContentPath = null)
    {
        try
        {
            JArray list; lock (_watchLock) { list = LoadWatch(); }
            var m = list.OfType<JObject>().FirstOrDefault(x => mainHash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase));
            if (m?["donors"] is not JArray donors) return;
            foreach (var d in donors.OfType<JObject>())
            {
                await QbitDeleteDonorSafe(c, d.Value<string>("hash"), mainHash, mainContentPath);   // с файлами ТОЛЬКО если категория донорская и папка не общая
                DropResolveCache(d.Value<string>("hash"));
            }
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
        int target = DonorTargetQuality(mainFiles, ModInit.conf);   // тот же ранг качества, что у охоты (qdl 2.107)

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

                    // Сезон берём из САМОГО ФАЙЛА, запись — только фолбэк. Записи, сделанные до фикса
                    // сезонного гейта, врут: у «Укрытия» файлы Silo.S02.E07…E10 лежали как season=3.
                    // Так чужой сезон исчезает из карточки сразу после рестарта, не дожидаясь охоты.
                    int fseason = FileSeason(f);
                    if (fseason > 0) es = fseason;
                    if (season > 0 && es > 0 && es != season) continue;

                    var mainSame = parsed.FirstOrDefault(x => x.Value<string>("source") == "main"
                        && x.Value<int?>("episode") == ep
                        && (es <= 0 || x.Value<int?>("season") == (es > 0 ? es : season)));
                    if (mainSame != null)
                    {
                        if ((mainSame.Value<double?>("progress") ?? 0) >= 0.999) continue;   // основная готова — донор не нужен
                        parsed.Remove(mainSame);                                             // основная ещё качается → пока донор
                    }

                    // Апгрейд в процессе: ту же серию держат два донора (старый и новый, получше).
                    // Пока проигравшего не снял ScanReplacements, показываем ОДНУ копию — докачанную,
                    // а при равенстве ту, у которой скор выше.
                    var donorSame = parsed.FirstOrDefault(x => x.Value<string>("source") == "donor"
                        && x.Value<int?>("episode") == ep && x.Value<int?>("season") == (es > 0 ? es : season));
                    if (donorSame != null)
                    {
                        double haveP = donorSame.Value<double?>("progress") ?? 0, newP = f.Value<double?>("progress") ?? 0;
                        double haveS = donorSame.Value<double?>("dscore") ?? -1, newS = donor.Value<double?>("score") ?? -1;
                        int haveR = QualityRank(donorSame.Value<int?>("dquality") ?? 0, target), newR = QualityRank(donor.Value<int?>("quality") ?? 0, target);
                        bool sameDone = (newP >= 0.999) == (haveP >= 0.999);
                        bool newWins = (newP >= 0.999 && haveP < 0.999)
                                    || (sameDone && (newR < haveR || (newR == haveR && newS > haveS)));
                        if (!newWins) continue;
                        parsed.Remove(donorSame);
                    }

                    var de = entry(dh, f, "donor", es, ep);
                    de["dscore"] = donor.Value<double?>("score") ?? -1;      // служебное: только для выбора копии выше
                    de["dquality"] = donor.Value<int?>("quality") ?? 0;      // служебное: ранг качества той же копии
                    parsed.Add(de);
                }
            }

        var ordered = parsed.OrderBy(x => x.Value<int?>("season") ?? 0).ThenBy(x => x.Value<int?>("episode") ?? 0).ToList();
        ordered.AddRange(unparsed);
        foreach (var o in ordered) { o.Remove("dscore"); o.Remove("dquality"); }   // служебные поля наружу не отдаём
        return new JArray(ordered);
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/episodes")]
    async public Task<ActionResult> Episodes(string hash)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            // Сезоны одного сериала — одной карточкой (qdl 2.78, SeriesMerge.cs): по ЛЮБОМУ хешу
            // группы отдаём ОБЩИЙ плейлист всех её раздач. Клиенту новых понятий не нужно — файл
            // серии и так несёт свой `hash` (механика доноров охоты), а сортировку по
            // (вид, сезон, номер) qdl.js делает сам.
            // ⚠️ Порядок обхода — канонический (по хешу), а НЕ «сначала свой»: дедуп разрешает
            // ничью в пользу первой записи, и «сначала свой» дал бы РАЗНЫЙ выбор копии в
            // зависимости от того, с какой раздачи группы зритель зашёл.
            // ⚠️ Сиблинг — в своём try: мёртвая мета (раздачу снесли мимо PurgeCache) не должна
            // ронять плейлист карточки, которую реально открыли. Свой хеш — без глушилки:
            // его сбой обязан остаться ошибкой ручки (клиент уйдёт в фолбэк /qdl/files).
            var group = SeriesGroupHashes(hash);
            if (group != null)
            {
                var all = new JArray();
                foreach (string gh in group)
                {
                    if (gh.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        foreach (var e in await EpisodesJson(gh)) all.Add(e);
                    else
                        try { foreach (var e in await EpisodesJson(gh)) all.Add(e); }
                        catch (Exception sx) { Console.WriteLine("[QbitDownload] episodes sibling " + gh + ": " + sx.Message); }
                }
                return ContentTo(MergeGroupEpisodes(all).ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
            }

            return ContentTo((await EpisodesJson(hash)).ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] episodes: " + ex);
            return Json(new { error = "internal error" });
        }
    }

    /// <summary>Серии ОДНОЙ карточки: локальный маркер-финал либо торрент + доноры охоты.</summary>
    static async Task<JArray> EpisodesJson(string hash)
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
            // jut.su: свой префикс таймлайна, чтобы прогресс ОНЛАЙН-просмотра не потерялся
            // после скачивания (клиент строит тот же ключ qdltl:jut:<slug>:s1e7).
            string jutTl = (loc["jut"] as JObject)?.Value<string>("tlPrefix");
            bool isJut = jutTl != null;
            var rows = new List<(int kind, int season, int ep, string name, JObject o)>();
            foreach (var f in LocalFiles(loc))
            {
                if (!System.IO.File.Exists(f.path)) continue;
                var o = new JObject { ["hash"] = hash, ["index"] = f.index, ["name"] = f.name, ["size"] = f.size, ["progress"] = 1.0, ["source"] = "main" };
                string bn = System.IO.Path.GetFileNameWithoutExtension(f.name ?? "");
                int kindRank = 9, sn = -1, en = -1;   // sn/en, а не season/ep: имена заняты внешней областью
                string epkey = null;

                // Имена jut разбираем СВОИМ парсером (точная инверсия JutFileName): общий ParseEp
                // читает «film1» как серию 1 — фильм получал ключ таймлайна первой серии вместе
                // с её отметкой просмотра, — экстрам ключа не давал вовсе, а серии ≥1000 терял.
                if (isJut && TryParseJutFileName(bn, out var jk, out int js, out int jn))
                {
                    var je = new JutEp { kind = jk, season = js, num = jn };
                    epkey = je.epkey;
                    kindRank = jk == JutEpKind.Episode ? 0 : jk == JutEpKind.Film ? 1
                             : jk == JutEpKind.Ova ? 2 : jk == JutEpKind.GameOva ? 3 : 4;
                    sn = jk == JutEpKind.Episode ? js : 0;
                    en = jn;
                    if (jk == JutEpKind.Episode) { o["season"] = js; o["episode"] = jn; }
                }
                else
                {
                    var e = ParseEp(bn);
                    if (e != null && e.any && e.kind == null && e.ep >= 0)
                    {
                        int ss = e.season > 0 ? e.season : 1;
                        o["season"] = ss; o["episode"] = e.ep; epkey = "s" + ss + "e" + e.ep;
                        kindRank = 0; sn = ss; en = e.ep;
                    }
                }

                if (epkey != null) o["epkey"] = epkey;
                // 🔴 Явный ключ из маркера ПОБЕЖДАЕТ вычисленный. У XSMART имя файла несёт
                // порядковые номера (s01e05), а ключ таймлайна — идентификаторы серии
                // (s32215e524438), и вывести один из другого нельзя в принципе. Собрали бы
                // ключ по имени — прогресс скачанной копии разошёлся бы с онлайн-просмотром.
                if (!string.IsNullOrEmpty(f.tl)) o["tl"] = f.tl;
                else if (epkey != null) o["tl"] = (jutTl ?? sk) + ":" + epkey;
                rows.Add((kindRank, sn, en, f.name ?? "", o));
            }

            // Порядок отдаём МЫ: файлы маркера лежат отсортированными по ПУТИ, то есть
            // лексикографически — s1e100 попадал между s1e10 и s1e11, а film/ova вставали
            // в начало списка. Клиент на этот порядок опирался («сервер уже отсортировал»),
            // и «Продолжить» промахивалась мимо серии. Как в торрентной ветке выше:
            // серии → экстры, внутри — по сезону и номеру; неразобранное в конец.
            foreach (var r in rows.OrderBy(x => x.kind).ThenBy(x => x.season).ThenBy(x => x.ep)
                                  .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase))
                arr0.Add(r.o);
            return arr0;
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

        return MergeEpisodeFiles(hash, mainFiles, donorData, sk, season);
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
        // кандидат мог уже сидеть донором охоты — на дубликате add категорию не меняет (см. PromoteIfDonor)
        await PromoteIfDonor(c, newHash, new[] { m }, m.Value<string>("title"));

        // switchDeleteOldFiles=true (не дефолт): удалять файлы старой раздачи можно только убедившись,
        // что новая качает НЕ в ту же папку — иначе снесём то, что она уже перепроверила и приняла
        bool delOld = ModInit.conf.switchDeleteOldFiles;
        if (delOld)
        {
            string oldPath = (await QbitTorrentInfo(c, curHash))?.Value<string>("content_path");
            string newPath = (await QbitTorrentInfo(c, newHash))?.Value<string>("content_path");
            if (string.IsNullOrWhiteSpace(newPath) || PathsOverlap(oldPath, newPath))
            {
                delOld = false;
                Console.WriteLine("[QbitDownload] watch: старая раздача " + curHash + " снята БЕЗ файлов — папка новой " + (string.IsNullOrWhiteSpace(newPath) ? "ещё неизвестна" : "та же"));
            }
        }
        await QbitDelete(c, curHash, delOld);
        MigrateCache(curHash, newHash);
        ActivityTouch(newHash);   // переключение = свежая загрузка, даже если торрент был дубликатом (added_on старый)
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
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;
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
