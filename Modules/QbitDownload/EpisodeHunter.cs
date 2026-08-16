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
        string body = (await r.Content.ReadAsStringAsync())?.Trim() ?? "";
        if ((int)r.StatusCode == 409 || body.Equals("Conflict", StringComparison.OrdinalIgnoreCase)) return QbitAddStatus.Duplicate;
        if (!r.IsSuccessStatusCode) return QbitAddStatus.Failed;
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
        public int minSeeds, minQuality, minMb, maxGb;
        public string titleNorm, originalNorm;  // нормализованные названия сериала для строгого гейта имени
        public string selfTopicKey;             // топик САМОЙ основной раздачи (её перерегистрация — не донор)
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

            // ТОТ ЖЕ топик, что у основной раздачи, только перевыложенный (новые серии → новый infohash).
            // Это не «другая раздача-донор», это обновление НАШЕЙ же — её заберёт re-grab в CheckWatches.
            // Взяв её донором, охота получает торрент, который вот-вот станет основной, и контур
            // замещения сносит его с файлами (инцидент 2026-07-25, «Укрытие»). knownHashes тут не спасают:
            // у перерегистрации ДРУГОЙ infohash. Сверяем именно топик.
            if (h.selfTopicKey != null && TopicKey(parselink) == h.selfTopicKey) continue;

            if (!SeasonOk(title, h)) continue;

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

    // сколько серий заявляет ОДИН кандидат («N из M» / одиночка / диапазон)
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
    static bool IdentityMatches(JObject t, HuntCtx h)
    {
        string title = t.Value<string>("title") ?? "";
        return NameMatchesSeries(title, h.titleNorm, h.originalNorm) && SeasonOk(title, h);
    }

    static List<JObject> ClaimCandidates(JArray scored, HuntCtx h)
        => scored.OfType<JObject>().Where(t => IdentityMatches(t, h)).ToList();

    #region апгрейд донорской серии на раздачу получше
    // Текущие «ставки» донора: скор и качество. Сперва по СВЕЖЕЙ выдаче (скор плывёт вместе с сидами
    // и датой), иначе — то, что записали при захвате. (-1, -1) = не с чем сравнивать → не апгрейдим.
    static (double score, int quality) DonorBaseline(JObject donor, JArray scored)
    {
        string dh = donor.Value<string>("hash") ?? "", link = donor.Value<string>("link") ?? "";
        foreach (var t in scored.OfType<JObject>())
        {
            bool same = (!string.IsNullOrEmpty(dh) && string.Equals(MagnetHash(t.Value<string>("magnet")), dh, StringComparison.OrdinalIgnoreCase))
                     || (!string.IsNullOrWhiteSpace(link) && string.Equals(t.Value<string>("parselink"), link, StringComparison.OrdinalIgnoreCase));
            if (same) return (t.Value<double?>("score") ?? 0, t.Value<int?>("quality") ?? 0);
        }
        var s = donor.Value<double?>("score");
        return s.HasValue ? (s.Value, donor.Value<int?>("quality") ?? 0) : (-1, -1);
    }

    // Серия «временно с другой раздачи» стоит апгрейда, если среди годных кандидатов есть раздача
    // ЯВНО лучше той, с которой мы её взяли: выше качество или скор выше на minScore (⭐ — это и есть
    // верх скора, отдельного признака не нужно). Серии, которые уже есть в ОСНОВНОЙ, не трогаем:
    // основная всегда приоритетнее донора, её версия придёт штатным замещением.
    // why (может быть null) — заполняется человекочитаемым «с чего на что» для лога.
    static List<int> ComputeUpgrades(JArray donors, JArray scored, List<JObject> eligible, HashSet<int> mainEps,
                                     int season, int minScoreGain, Dictionary<int, string> why)
    {
        var res = new List<int>();
        if (donors == null || eligible.Count == 0) return res;

        foreach (var d in donors.OfType<JObject>())
        {
            var (bscore, bquality) = DonorBaseline(d, scored);
            if (bscore < 0) continue;   // не с чем сравнивать (старая запись и раздачи нет в выдаче)

            foreach (var e in (d["eps"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (e.Value<string>("status") != "hunted") continue;
                int ep = e.Value<int?>("ep") ?? -1;
                if (ep < 0 || mainEps.Contains(ep) || res.Contains(ep)) continue;

                foreach (var t in eligible)   // eligible уже отсортирован по скору (выдача SearchScored)
                {
                    if (TitleCoversEp(t.Value<string>("title") ?? "", season, ep) != DonorCover.Yes) continue;
                    double cs = t.Value<double?>("score") ?? 0;
                    int cq = t.Value<int?>("quality") ?? 0;
                    bool better = (cq > 0 && bquality > 0 && cq > bquality) || cs >= bscore + minScoreGain;
                    if (!better) continue;
                    res.Add(ep);
                    if (why != null) why[ep] = $"E{ep}: {bquality}p/{Math.Round(bscore, 1)} → {cq}p/{Math.Round(cs, 1)}";
                    break;
                }
            }
        }
        res.Sort();
        return res;
    }
    #endregion

    // Сколько серий заявляет ТОТ ЖЕ топик, что у основной раздачи (перевыложенная наша же раздача).
    // Донором его брать нельзя (§AK) — но это единственный сигнал «пора делать re-grab».
    static int SelfTopicClaim(JArray scored, HuntCtx h)
    {
        if (h.selfTopicKey == null) return 0;
        int max = 0;
        foreach (var t in scored.OfType<JObject>())
            if (TopicKey(t.Value<string>("parselink")) == h.selfTopicKey && IdentityMatches(t, h))
                max = Math.Max(max, ClaimOf(t));
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
                if (s <= 0) s = season;
                if (season <= 1 || s == season)   // тот же fail-closed для одиночки
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
    sealed class HuntOneResult { public int grabbed; public bool searched; public bool barren; public bool regrab; }

    // Ранний повтор оправдан только если пусто у ВСЕХ опрошенных сериалов: у одного — бывает
    // (нишевый тайтл), у всех сразу — это индексатор/трекеры лежат.
    static bool ShouldRetryHunt(int searched, int barren, int retries)
        => searched > 0 && barren == searched && retries < HuntRetryMax;

    public static async Task<int> HuntAll(string onlyHash = null)
    {
        if (!ModInit.conf.episodeHunt) return 0;
        if (!await _watchGate.WaitAsync(0))   // общий фоновый гейт (был _hunting): сериализуем с CheckWatches/ScanEpisodeNotifications
        {
            Console.WriteLine("[QbitDownload] hunt: тик пропущен — занят другой фоновый проход (watch/notify/hunt)");
            return 0;
        }
        int grabbed = 0, series = 0, searched = 0, barren = 0;
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
                    var r = await HuntOne(c, m);   // штамп hunt.lastRun ставит сам HuntOne — и только на удачном проходе
                    grabbed += r.grabbed; changed = true; series++;
                    if (r.searched) searched++;
                    if (r.barren) barren++;
                    if (r.regrab) regrabAsk = true;
                }
                catch (Exception ex) { Console.WriteLine("[QbitDownload] hunt item: " + ex); }
            }
            if (changed) SaveWatchReconciled(list, orig);
            if (series > 0)
                Console.WriteLine($"[QbitDownload] hunt: проход завершён — записей {series}, опрошено {searched}, пустых выдач {barren}, добыто серий {grabbed}");
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] hunt: " + ex); }
        finally { _watchGate.Release(); }

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
        // перезаписью объекта целиком сбрасывается и счётчик пустых выдач — проход удачный
        m["hunt"] = new JObject { ["lastRun"] = now.ToString("o"), ["lastMaxClaim"] = maxClaim };
    }

    // Пустая выдача индексатора: lastRun НЕ трогаем (иначе сбой трекеров выглядит как «новых серий
    // нет» и стоит целый интервал), пишем только диагностику подряд идущих пустых попыток.
    static void MarkHuntBarren(JObject m, DateTime now)
    {
        if (m["hunt"] is not JObject h) { h = new JObject(); m["hunt"] = h; }
        h["lastEmpty"] = now.ToString("o");
        h["emptyStreak"] = (h.Value<int?>("emptyStreak") ?? 0) + 1;
    }

    // ── сводка отсева для лога ────────────────────────────────────────────
    // Причина отсева ТОЛЬКО для лога: решение принимает FilterDonorCandidates (её не трогаем),
    // здесь повторяется порядок её проверок. Расхождение испортит текст лога, но не решение;
    // от дрейфа стережёт тест HunterCoverageTests.DropReason_MirrorsFilter.
    static string DropReason(JObject t, HuntCtx h)
    {
        string title = t.Value<string>("title") ?? "";
        if (!NameMatchesSeries(title, h.titleNorm, h.originalNorm)) return "имя";
        if ((t.Value<int?>("sid") ?? 0) < h.minSeeds) return "сиды";

        int q = t.Value<int?>("quality") ?? 0;
        if (h.minQuality > 0 && q > 0 && q < h.minQuality) return "качество";

        string btih = MagnetHash(t.Value<string>("magnet"));
        string parselink = t.Value<string>("parselink");
        if (!string.IsNullOrEmpty(btih) && h.knownHashes.Contains(btih)) return "уже есть";
        if (!string.IsNullOrEmpty(btih) && h.blacklistKeys.Contains(btih)) return "blacklist";
        if (!string.IsNullOrWhiteSpace(parselink) && h.blacklistKeys.Contains(parselink)) return "blacklist";
        if (string.IsNullOrEmpty(btih) && string.IsNullOrWhiteSpace(parselink)) return "без ссылки";
        if (h.selfTopicKey != null && TopicKey(parselink) == h.selfTopicKey) return "своя раздача";

        var seasons = TorrentScoring.ParseSeasons(title);   // порядок повторяет SeasonOk, но с разными подписями причин
        if (h.season > 0 && seasons.Count > 0 && !seasons.Contains(h.season)) return "сезон";
        if (h.season > 1 && seasons.Count == 0) return "сезон не заявлен";

        long sizeBytes = t.Value<long?>("sizeBytes") ?? 0;
        var cov = TorrentScoring.ParseEpCoverage(title);
        int haveCount = cov?.have ?? 0;
        if (haveCount == 0)
        {
            var pe = ParseEp(StripSeasonMarks(title));
            if (pe != null && pe.any && pe.kind == "RANGE" && pe.ep2 >= pe.ep) haveCount = pe.ep2 - pe.ep + 1;
            else if (pe != null && pe.any && pe.kind == null && pe.ep >= 0) haveCount = 1;
        }
        if (sizeBytes > 0 && haveCount > 0 && !EpSizeOk(EstimateEpBytes(sizeBytes, haveCount), h.minMb, h.maxGb)) return "вес серии";

        return null;
    }

    static string DropSummary(JArray scored, int keptCount, HuntCtx h)
    {
        int dropped = scored.Count - keptCount;
        if (dropped <= 0) return "";
        var by = new Dictionary<string, int>();
        foreach (var t in scored.OfType<JObject>())
        {
            string r = DropReason(t, h);
            if (r != null) by[r] = by.TryGetValue(r, out int n) ? n + 1 : 1;
        }
        var top = by.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key + " " + x.Value).ToList();
        return " (отсев " + dropped + (top.Count > 0 ? ": " + string.Join(", ", top) : "") + ")";
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

    static async Task<HuntOneResult> HuntOne(HttpClient c, JObject m)
    {
        var res = new HuntOneResult();
        var conf = ModInit.conf;
        string mainHash = m.Value<string>("hash");
        if (!ValidHash(mainHash)) return res;

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
        if (!isSerial) return res;

        string ctitle = ctx?.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("query");
        if (string.IsNullOrWhiteSpace(ctitle)) ctitle = m.Value<string>("title");
        if (string.IsNullOrWhiteSpace(ctitle)) return res;

        var mainFiles = await QbitFiles(c, mainHash);
        if (mainFiles == null || mainFiles.Count == 0) return res;   // основная сама ещё без метаданных

        var now = DateTime.UtcNow;
        PruneBlacklist(m, now);
        var donors = m["donors"] as JArray;

        int season = DominantSeason(mainFiles);
        if (season <= 0) season = Math.Max(1, ctx?.Value<int?>("season") ?? 1);

        var inv = InventoryEps(mainFiles, donors, season);
        string stitle = m.Value<string>("title");
        if (string.IsNullOrWhiteSpace(stitle)) stitle = ctitle;

        // Кап доноров. При включённом апгрейде проход всё же идём: замена плохой серии на хорошую
        // важнее экономии одного слота, а перебор ограничен ровно +1 донором и самоустраняется —
        // проигравший уходит в ScanReplacements, опустевший донор снимается.
        bool atCap = (donors?.Count ?? 0) >= conf.donorMaxPerSeries;
        if (atCap && !conf.donorUpgrade)
        {
            SetHuntStamp(m, now, 0);
            Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: пропуск — доноров уже {donors?.Count ?? 0}/{conf.donorMaxPerSeries}");
            return res;
        }

        var scored = await SearchScored(ctitle, ctitle, ctx?.Value<string>("title_original"),
                                        ctx?.Value<int?>("year") ?? 0, 2, season, null);
        res.searched = true;

        // Пусто = либо трекеры отдали ошибку (JacRed InternalServerError и т.п.), либо индексатор лёг.
        // Это НЕ «новых серий нет»: штамп не ставим (иначе следующая диагностика соврёт), сигналим
        // наверх — HuntAll попросит таймер прийти раньше.
        if (scored.Count == 0)
        {
            MarkHuntBarren(m, now);
            res.barren = true;
            Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: индексатор не дал кандидатов (подряд {(m["hunt"] as JObject)?.Value<int?>("emptyStreak") ?? 1}) — проход не засчитан");
            return res;
        }

        var h = new HuntCtx
        {
            mainHash = mainHash.ToLowerInvariant(),
            season = season,
            knownHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainHash },
            blacklistKeys = BlacklistKeys(m, now),
            minSeeds = conf.donorMinSeeds,
            minQuality = conf.donorMinQuality,
            minMb = conf.epSizeMinMb,
            maxGb = conf.epSizeMaxGb,
            titleNorm = Shared.Services.Utilities.SearchNameTo.Convert(ctitle),
            originalNorm = Shared.Services.Utilities.SearchNameTo.Convert(ctx?.Value<string>("title_original")),
            selfTopicKey = TopicKey(m.Value<string>("link"))
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
        var claims = ClaimCandidates(scored, h);     // ⊇ eligible: гейты пригодности тут не применяются
        int maxClaim = MaxClaim(claims);
        int eligibleClaim = MaxClaim(eligible);

        // Потолок по РЕАЛЬНО ВЫШЕДШИМ сериям (TMDB). «1-6 серии из 10» в названии — план сезона, а не
        // факт эфира: у «Укрытия» это давало wanted E7–E10, которых ещё нет, и охота, не найдя их в
        // третьем сезоне, утащила серии 7–10 ВТОРОГО. Fail-open: TMDB недоступен → работаем как раньше.
        int aired = conf.tmdbAiredCap ? await AiredEpisodes(m.Value<int?>("id") ?? 0, season) : 0;
        if (aired > 0 && maxClaim > aired)
        {
            Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: потолок серий {maxClaim} → {aired} (по TMDB вышло {aired})");
            maxClaim = aired;
        }

        var wanted = ComputeWanted(inv, maxClaim);

        // Апгрейд: серия уже лежит «временно с другой раздачи», но в выдаче есть раздача получше.
        // Идёт ДОПОЛНИТЕЛЬНО к недостающим сериям (ComputeWanted смотрит только вперёд от максимума).
        var upgrades = new List<int>();
        if (conf.donorUpgrade)
        {
            var mainEps = new HashSet<int>();
            foreach (var f in mainFiles)
            {
                if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue;
                var fe = ParseEp(BaseNoExt(f));
                if (fe != null && fe.any && fe.kind == null && fe.ep >= 0) mainEps.Add(fe.ep);
            }
            var upWhy = new Dictionary<int, string>();
            upgrades = ComputeUpgrades(donors, scored, eligible, mainEps, season, conf.donorUpgradeMinScore, upWhy);
            foreach (int ep in upgrades)
                if (!wanted.Contains(ep)) wanted.Add(ep);
            if (upgrades.Count > 0)
            {
                wanted.Sort();
                Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: апгрейд донорских серий — {string.Join("; ", upgrades.Select(x => upWhy[x]))}");
            }
        }

        if (atCap) wanted = upgrades;   // слотов нет: берём ТОЛЬКО ради замены на лучшее, новые серии ждут

        SetHuntStamp(m, now, maxClaim);
        string claimNote = maxClaim != eligibleClaim ? $" (годные заявляют {eligibleClaim})" : "";
        string upNote = upgrades.Count > 0 ? $", апгрейд {upgrades.Count}" : "";
        string capNote = atCap ? $" [доноров {donors?.Count ?? 0}/{conf.donorMaxPerSeries} — только апгрейд]" : "";
        Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: кандидатов {scored.Count} → годных {eligible.Count}{DropSummary(scored, eligible.Count, h)}; заявлено серий до {maxClaim}{claimNote}, нужно {WantedText(wanted)}{upNote}{capNote}");

        // Свой топик перевыложен с бо́льшим числом серий. Донором его брать НЕЛЬЗЯ (§AK: он вот-вот
        // станет основной, и контур замещения снёс бы его С ФАЙЛАМИ) — владелец обновления только
        // re-grab в CheckWatches. Раньше охота молча выбрасывала этот кандидат, и обновление
        // раздачи ждало своего 6-часового тика («Великий расхититель гробниц»: 2 серии вместо 5).
        int mainVideos = mainFiles.Count(f => _videoExtRx.IsMatch(f.Value<string>("name") ?? ""));
        int selfClaim = SelfTopicClaim(scored, h);
        if (selfClaim > mainVideos)
        {
            res.regrab = true;
            Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: свой топик заявляет {selfClaim} серий, у основной {mainVideos} файлов — раздача перевыложена, запрашиваю re-grab");
        }

        if (wanted.Count == 0) return res;   // новее ничего не заявлено — основная и так самая полная

        int grabbed = 0, probes = 0;
        long minB = conf.epSizeMinMb * 1024L * 1024, maxB = conf.epSizeMaxGb * 1024L * 1024 * 1024;

        // Топ-N из выдачи Лампа-торрента, N = donorProbesPerRun: первая по релевантности раздача
        // часто не подтверждается файлами (нет серии/нет метаданных), и на топ-1 проход уходил
        // впустую до следующего интервала. Перебор всё равно жёстко ограничен: probes ниже и кап
        // donorMaxPerSeries (перечитывается на каждой итерации — доноры добавляются прямо в цикле).
        foreach (var cand in ProbeCandidates(eligible, season, wanted, conf.donorProbesPerRun))
        {
            if (probes >= Math.Max(1, conf.donorProbesPerRun)) break;
            if (((m["donors"] as JArray)?.Count ?? 0) >= conf.donorMaxPerSeries + (atCap ? 1 : 0)) break;
            probes++;

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
                    Console.WriteLine($"[QbitDownload] hunt: парселинк не резолвится (попытка {at}) — пауза {ttl} мин («{cand.Value<string>("title")}»)");
                    continue;
                }
            }
            string btih = MagnetHash(magnet);
            if (string.IsNullOrEmpty(btih) || h.knownHashes.Contains(btih) || h.blacklistKeys.Contains(btih)) continue;

            // двойная страховка (сверх knownHashes из категории lampa): если инфохеш уже есть в qBit
            // и это НЕ наш донор — чужая загрузка, не усыновляем (QbitAddMagnetEx на дубле неотличим от add)
            var pre = await QbitTorrentInfo(c, btih);
            if (pre != null && pre.Value<string>("category") != DonorCategory)
            { BlacklistAdd(m, btih, parselink, "foreign", conf.donorBlacklistTtlDays); continue; }

            // двухфазный захват: add со стопом после метаданных → подтверждение по файлам
            if (!await QbitAddMagnetEx(c, magnet, DonorCategory, DonorTag, stopAfterMeta: true))
            {
                int at = BlacklistAttempts(m, btih, "add-failed") + 1;
                BlacklistAddMinutes(m, btih, parselink, "add-failed", TransientFailMinutes(at), at);   // сбой qBit — транзиент
                continue;
            }

            // Аварийные выходы ниже удаляют кандидата С ФАЙЛАМИ — только через QbitDeleteDonorSafe.
            // Оба гейта выше (knownHashes из категории lampa и pre-check) FAIL-OPEN: запрос к qBit мог
            // упасть/таймаутнуть, тогда «наш новый донор» — на самом деле чужой торрент, а add на дубликате
            // категорию не сменил. Слепой QbitDelete(..., true) снёс бы чужие файлы.
            var dfiles = await QbitWaitFiles(c, btih, conf.donorMetadataTimeoutSec);
            if (dfiles == null || dfiles.Count == 0)
            {
                await QbitDeleteDonorSafe(c, btih, mainHash);
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
                await QbitDeleteDonorSafe(c, btih, mainHash);   // только safe-путь (§AK)
                BlacklistAdd(m, btih, parselink, "wrong-season", conf.donorBlacklistTtlDays);
                Console.WriteLine($"[QbitDownload] hunt: донор {btih} отвергнут — в файлах сезон(ы) {string.Join(",", dseasons.OrderBy(x => x))}, охотим S{season} («{cand.Value<string>("title")}»)");
                continue;
            }

            var titleEp = ParseEp(StripSeasonMarks(cand.Value<string>("title") ?? ""));
            var found = FindEpFiles(dfiles, season, wanted, titleEp, DonorSeason(dfiles, cand.Value<string>("title")));
            found.RemoveAll(f => f.size > 0 && (f.size < minB || f.size > maxB));   // теперь вес известен точно
            if (found.Count == 0)
            {
                await QbitDeleteDonorSafe(c, btih, mainHash);
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
                ["sid"] = cand.Value<int?>("sid") ?? 0, ["addedAt"] = now.ToString("o"),
                // база для сравнения при апгрейде: с чем именно мы согласились, когда брали эту серию
                ["score"] = cand.Value<double?>("score") ?? 0, ["quality"] = cand.Value<int?>("quality") ?? 0,
                ["eps"] = epsArr
            });
            h.knownHashes.Add(btih);
            grabbed += found.Count;
            Console.WriteLine("[QbitDownload] hunt: донор " + btih + " (" + cand.Value<string>("tracker") + ") — серии " + string.Join(",", found.Select(f => f.ep)) + " для «" + m.Value<string>("title") + "»");
            if (wanted.Count == 0) break;
        }
        if (grabbed > 0)
            ActivityTouch(h.mainHash);   // карточка всплывает в момент ЗАХВАТА серии, не дожидаясь докачки
        else
            Console.WriteLine($"[QbitDownload] hunt «{stitle}» S{season}: ничего не добыто (проб {probes} из {eligible.Count} годных)");
        res.grabbed = grabbed;
        return res;
    }

    // Сколько кандидатов пробуем за проход: топ-N в порядке OrderByCover (уверенные Yes, затем Maybe).
    // Кламп ≥1 — нулевой/отрицательный donorProbesPerRun не должен выключать охоту молча.
    static List<JObject> ProbeCandidates(List<JObject> eligible, int season, List<int> wanted, int probesPerRun)
        => OrderByCover(eligible, season, wanted).Take(Math.Max(1, probesPerRun)).ToList();

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

        string mainHash = item.Value<string>("hash") ?? "";

        // Сезон основной для самолечения записей: только ОДНОЗНАЧНЫЙ. 0 (не определить или пак из
        // нескольких сезонов) → чужой сезон у донора не диагностируем и ничего не трогаем.
        var mainSeasons = DonorSeasons(mainFiles);
        int mainSeason = mainSeasons.Count == 1 ? mainSeasons.First() : 0;

        // Апгрейд: одну и ту же серию держат два донора (охота добавила лучшего). Победитель —
        // ДОКАЧАННЫЙ и с бо́льшим скором; проигравшего снимаем. Пока новый файл не готов, старый не
        // трогаем ни при каких условиях — иначе зритель остался бы без серии.
        var upgradeLosers = new HashSet<JObject>();
        {
            var byEp = new Dictionary<string, List<(JObject donor, JObject ep, double score, bool done)>>();
            foreach (var d in donors.OfType<JObject>())
            {
                string dh0 = d.Value<string>("hash") ?? "";
                if (donorFiles == null || !donorFiles.TryGetValue(dh0, out JArray df) || df == null) continue;
                double sc = d.Value<double?>("score") ?? -1;
                foreach (var e in (d["eps"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (e.Value<string>("status") != "hunted") continue;
                    int en0 = e.Value<int?>("ep") ?? -1, es0 = e.Value<int?>("season") ?? -1;
                    if (en0 < 0) continue;
                    int fi = e.Value<int?>("fileIndex") ?? -1;
                    var f = df.FirstOrDefault(x => (x.Value<int?>("index") ?? -1) == fi);
                    bool done = f != null && (f.Value<double?>("progress") ?? 0) >= 0.999;
                    string k = es0 + ":" + en0;
                    if (!byEp.TryGetValue(k, out var lst)) byEp[k] = lst = new List<(JObject, JObject, double, bool)>();
                    lst.Add((d, e, sc, done));
                }
            }
            foreach (var kv in byEp)
            {
                if (kv.Value.Count < 2) continue;
                var winner = kv.Value.Where(x => x.done).OrderByDescending(x => x.score).FirstOrDefault();
                if (winner.ep == null) continue;   // ни одна копия не докачана — ждём
                foreach (var x in kv.Value)
                {
                    if (ReferenceEquals(x.ep, winner.ep)) continue;
                    // НЕдокачанную копию с не меньшим скором не трогаем: это и есть апгрейд в полёте,
                    // ради которого охота её и добавила. Снести её значило бы отменять апгрейд каждый
                    // проход и качать заново.
                    if (!x.done && x.score >= winner.score) continue;
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
                        bool newWins = newP >= 0.999 && haveP < 0.999 || (newP >= 0.999) == (haveP >= 0.999) && newS > haveS;
                        if (!newWins) continue;
                        parsed.Remove(donorSame);
                    }

                    var de = entry(dh, f, "donor", es, ep);
                    de["dscore"] = donor.Value<double?>("score") ?? -1;   // служебное: только для выбора копии выше
                    parsed.Add(de);
                }
            }

        var ordered = parsed.OrderBy(x => x.Value<int?>("season") ?? 0).ThenBy(x => x.Value<int?>("episode") ?? 0).ToList();
        ordered.AddRange(unparsed);
        foreach (var o in ordered) o.Remove("dscore");   // служебное поле наружу не отдаём
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

                    if (epkey != null) { o["epkey"] = epkey; o["tl"] = (jutTl ?? sk) + ":" + epkey; }
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
