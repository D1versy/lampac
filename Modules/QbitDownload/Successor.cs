using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Преемник раздачи (qdl 2.115): перекачка без пропажи уже скачанных серий.
//
// 🔥 Зачем. Re-grab перевыложенного топика (CheckWatches) и переключение на более полную раздачу
// (ExecuteSwitch) снимали старый торрент СРАЗУ после add нового, а MigrateCache тут же переносил
// мету. Пока новый перепроверял или качал, зритель видел «загрузку 0 %» и запертые серии.
// «Фонари» 06.09.2026: перевыложенный релиз с теми же именами, но другими байтами качался
// ПОВЕРХ старых файлов — E01–E03 разрушены, новые ещё не приехали. Разбор — медиасервер claude/06 §DR.
//
// Модель. Watch-запись сохраняет hash = старая раздача (она остаётся «основной» для зрителя и всех
// контуров) и получает поле next = {hash, reason, link, mode, savepath, since, deadline}.
// Преемник живёт в qBit в категории lampa с тегом qdl-next и ВСЕГДА добавляется в свою подпапку
// /downloads/.next/<hash8> — по построению не может ни байта лечь поверх старых файлов. Старая
// ставится на стоп (один писатель, без двойного трафика), но играется как раньше.
// После метаданных сравниваются списки файлов (DecideSuccessorMode):
//   flat  — путей-совпадений нет или все совпадения с теми же размерами → переезд в общий корень
//           (только при нуле скачанных байт) и перепроверка: докачиваются лишь новые серии;
//   aside — совпадение пути с ДРУГИМ размером («Фонари») → остаётся в подпапке, старая не тронута.
// Зритель: преемник скрыт из /qdl/list (как доноры), его серии подмешиваются в /qdl/episodes
// источником "next" (MergeEpisodeFiles), прогресс — через /qdl/progress по его хешу.
// Жнец ScanSuccessors (15-минутный тик ScanEpisodeNotifications): каждая докачанная серия старой
// есть у преемника докачанной (SuccessorCovers) или преемник готов целиком → жатва
// (SuccessorCutOver: старая снимается, файлы — только в aside и только по safe-пути, MigrateCache,
// hash записи = преемник). deadline прошёл → принудительная жатва как раньше, файлы старой остаются.
//
// 🔴 Красные линии (§AK): удаление с файлами — только своя папка и только при доказанном
// непересечении (SharesFilesWithAnyDownload, fail-safe); любой add разбирает Duplicate через
// PromoteIfDonor; порог готовности — один ProgressDone; преемник НЕ в донорской категории
// (торрент там без ссылки ReconcileDonors удаляет с файлами — а потерянная ссылка на преемника
// в lampa даёт лишь видимую вторую карточку, ReconcileSuccessors снимает с неё тег).
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    internal const string SuccessorTag = "qdl-next";
    internal const string SuccessorModeMeta = "meta";    // ждём метаданные, в своей подпапке, ни байта не пишет
    internal const string SuccessorModeFlat = "flat";    // общий корень /downloads, перепроверка существующих файлов
    internal const string SuccessorModeAside = "aside";  // своя подпапка до самой жатвы
    const string SuccessorSubdir = ".next";

    internal enum SuccessorStart { Started, Immediate, Failed }

    static bool SuccessorOn => ModInit.conf?.successorEnabled ?? true;
    static int SuccessorMaxDays => Math.Max(1, ModInit.conf?.successorMaxDays ?? 7);

    static string SuccessorDir(string hash)
        => (ModInit.conf.downloadsPath ?? "/downloads").TrimEnd('/', '\\') + "/" + SuccessorSubdir + "/" + hash.Substring(0, 8).ToLowerInvariant();

    static bool IsSuccessorPath(string savePath)
    {
        string n = NormPath(savePath);
        return n != null && (n.Contains("/" + SuccessorSubdir + "/") || n.EndsWith("/" + SuccessorSubdir));
    }

    static JObject NextOf(JToken m) => (m as JObject)?["next"] as JObject;

    static string NextHashOf(JToken m)
    {
        string h = NextOf(m)?.Value<string>("hash");
        return ValidHash(h) ? h.ToLowerInvariant() : null;
    }

    static HashSet<string> NextHashes(IEnumerable<JToken> list)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in list ?? Enumerable.Empty<JToken>()) { var h = NextHashOf(m); if (h != null) s.Add(h); }
        return s;
    }

    /// <summary>Идёт ли замена, в которой участвует хеш (основная с преемником либо сам преемник).</summary>
    static bool SuccessorPendingFor(string hash)
    {
        if (!ValidHash(hash)) return false;
        JArray list; lock (_watchLock) list = LoadWatch();
        foreach (var m in list.OfType<JObject>())
        {
            string nh = NextHashOf(m);
            if (nh == null) continue;
            if (hash.Equals(m.Value<string>("hash"), StringComparison.OrdinalIgnoreCase) || hash.Equals(nh, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static bool OtherRecordsReferenceNext(IEnumerable<JObject> all, JObject self, string hash)
        => (all ?? Enumerable.Empty<JObject>()).Any(x => !ReferenceEquals(x, self) && hash.Equals(NextHashOf(x), StringComparison.OrdinalIgnoreCase));

    static DateTime? ParseIsoUtc(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;

    #region qBit-хелперы
    // Зеркало QbitStartTorrent: qBit v5 → stop, v4 → pause.
    static async Task QbitStopTorrent(HttpClient c, string hash)
    {
        FormUrlEncodedContent form() => new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("hashes", hash) });
        try
        {
            var r = await c.PostAsync("/api/v2/torrents/stop", form());
            if (!r.IsSuccessStatusCode) await c.PostAsync("/api/v2/torrents/pause", form());
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] successor: stop " + hash + " — " + ex.Message); }
    }

    static async Task<bool> QbitSetLocation(HttpClient c, string hash, string location)
    {
        try
        {
            var r = await c.PostAsync("/api/v2/torrents/setLocation", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("location", location)
            }));
            if (!r.IsSuccessStatusCode) Console.WriteLine("[QbitDownload] successor: setLocation " + hash + " → " + location + " — " + (int)r.StatusCode);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] successor: setLocation " + hash + " — " + ex.Message); return false; }
    }

    static async Task QbitRemoveTags(HttpClient c, string hash, string tags)
    {
        try
        {
            await c.PostAsync("/api/v2/torrents/removeTags", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("tags", tags)
            }));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] successor: removeTags " + hash + " — " + ex.Message); }
    }

    static bool HasTag(JObject info, string tag)
        => (info?.Value<string>("tags") ?? "").Split(',').Select(t => t.Trim()).Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));

    // Ждём метаданные БЕЗ «пинка» (в отличие от QbitWaitFiles): преемник обязан стоять на
    // MetadataReceived в своей подпапке, пока не выбран режим. timeoutSec = 0 → одна проба.
    static async Task<JArray> QbitWaitFilesNoKick(HttpClient c, string hash, int timeoutSec)
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            var files = await QbitFiles(c, hash);
            if (files != null && files.Count > 0) return files;
            double left = timeoutSec - (DateTime.UtcNow - start).TotalSeconds;
            if (left <= 0) return null;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, left)));
        }
    }

    /// <summary>
    /// Делит ли contentPath папку хоть с одной другой загрузкой (категория lampa и доноры), кроме
    /// exceptHash. FAIL-SAFE: qBit не ответил → true, файлы не трогаем (§AK).
    /// </summary>
    static async Task<bool> SharesFilesWithAnyDownload(HttpClient c, string contentPath, string exceptHash)
    {
        if (string.IsNullOrWhiteSpace(contentPath)) return true;
        try
        {
            foreach (string cat in new[] { ModInit.conf.category, DonorCategory })
            {
                JArray arr;
                try { arr = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(cat)}")); }
                catch { if (cat == ModInit.conf.category) throw; continue; }   // донорской категории может не быть вовсе
                foreach (var it in arr)
                {
                    string h = it.Value<string>("hash");
                    if (!string.IsNullOrEmpty(exceptHash) && exceptHash.Equals(h, StringComparison.OrdinalIgnoreCase)) continue;
                    if (PathsOverlap(contentPath, it.Value<string>("content_path"))) return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] successor: не смог сверить папки загрузок (" + ex.Message + ") — файлы не трогаю");
            return true;
        }
    }
    #endregion

    #region чистая логика (под тестами)
    /// <summary>
    /// Режим преемника по спискам файлов старой и новой раздач (name — путь относительно save_path).
    /// Путей-совпадений нет или все совпадения с теми же размерами → flat (перепроверка засчитает
    /// старые файлы); хоть одно совпадение с другим размером → aside (иначе новая перепишет старые
    /// файлы на месте — случай «Фонарей»); нет метаданных → meta.
    /// ⚠️ Тот же путь + тот же размер + другие байты не различается: перепроверка не сойдётся и
    /// файл перепишется — для показа это закрывает MergeEpisodeFiles (nextTrustsSharedPath).
    /// </summary>
    internal static string DecideSuccessorMode(JArray oldFiles, JArray newFiles)
    {
        if (newFiles == null || newFiles.Count == 0) return SuccessorModeMeta;
        var oldSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var f in oldFiles ?? new JArray())
        {
            string n = NormPath(f.Value<string>("name"));
            if (n != null) oldSizes[n] = f.Value<long?>("size") ?? -1;
        }
        foreach (var f in newFiles)
        {
            string n = NormPath(f.Value<string>("name"));
            if (n == null || !oldSizes.TryGetValue(n, out long os)) continue;
            if (os != (f.Value<long?>("size") ?? -2)) return SuccessorModeAside;
        }
        return SuccessorModeFlat;
    }

    // Ключ серии файла: sSeE по имени и пути; null — экстра/RANGE/непарсибельное.
    static string SuccessorEpKey(JToken f, int seasonFallback)
    {
        if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) return null;
        var e = ParseEp(BaseNoExt(f));
        if (e == null || !e.any || e.kind != null || e.ep < 0) return null;
        int s = FileSeason(f);
        if (s <= 0) s = e.season > 0 ? e.season : seasonFallback;
        return "s" + s + "e" + e.ep;
    }

    /// <summary>
    /// Покрыл ли преемник старую: каждая ДОКАЧАННАЯ серия старой с распознанным номером есть у
    /// преемника докачанной. Экстры не считаются. У старой ни одной такой серии → false: пусть
    /// решает общий прогресс преемника (иначе жатва снимала бы старую, ничего не сверив).
    /// </summary>
    internal static bool SuccessorCovers(JArray oldFiles, JArray newFiles, int season)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in newFiles ?? new JArray())
        {
            if ((f.Value<double?>("progress") ?? 0) < ProgressDone) continue;
            string k = SuccessorEpKey(f, season);
            if (k != null) have.Add(k);
        }
        int need = 0;
        foreach (var f in oldFiles ?? new JArray())
        {
            if ((f.Value<double?>("progress") ?? 0) < ProgressDone) continue;
            string k = SuccessorEpKey(f, season);
            if (k == null) continue;
            need++;
            if (!have.Contains(k)) return false;
        }
        return need > 0;
    }
    #endregion

    #region старт замены
    /// <summary>
    /// Начать замену: преемник — в свою подпапку, старая — на стоп, запись next в m (сохраняет
    /// вызывающий). Immediate — хранить нечего, вызывающий делает прежнюю мгновенную замену:
    /// старая без единой докачанной серии, старой нет в qBit, либо новая уже сидит видимой
    /// загрузкой категории lampa (кто-то нажал «Скачать»). Failed — qBit не принял магнет.
    /// </summary>
    static async Task<SuccessorStart> StartSuccessor(HttpClient c, JObject m, string magnet, string newHash, string reason,
                                                     string newLink, JObject switchInfo, IEnumerable<JObject> allRecords)
    {
        if (!SuccessorOn) return SuccessorStart.Immediate;
        string oldHash = m.Value<string>("hash");
        string title = m.Value<string>("title") ?? "";
        if (!ValidHash(oldHash) || !ValidHash(newHash) || string.IsNullOrWhiteSpace(magnet)) return SuccessorStart.Immediate;
        newHash = newHash.ToLowerInvariant();

        // 1. Есть что хранить? Старая без единой докачанной серии — держать нечего.
        var oldInfo = await QbitTorrentInfo(c, oldHash);
        if (oldInfo == null)
        {
            Console.WriteLine("[QbitDownload] successor: «" + title + "» — старой раздачи " + oldHash + " в qBit нет, обычная замена");
            return SuccessorStart.Immediate;
        }
        var oldFiles = await QbitFiles(c, oldHash) ?? new JArray();
        bool anyDone = oldFiles.Any(f => _videoExtRx.IsMatch(f.Value<string>("name") ?? "") && (f.Value<double?>("progress") ?? 0) >= ProgressDone);
        if (!anyDone)
        {
            Console.WriteLine("[QbitDownload] successor: «" + title + "» — у старой нет докачанных серий, обычная замена");
            return SuccessorStart.Immediate;
        }

        // 2. add в свою подпапку, стоп на метаданных
        string dir = SuccessorDir(newHash);
        var add = await QbitAddMagnetStatus(c, magnet, ModInit.conf.category, SuccessorTag, stopAfterMeta: true, savepath: dir);
        if (add == QbitAddStatus.Failed) return SuccessorStart.Failed;
        if (add == QbitAddStatus.Duplicate)
        {
            // мог сидеть донором — промоушен (категория/приоритеты/донорские записи), потом смотрим, чем стал
            await PromoteIfDonor(c, newHash, allRecords ?? new[] { m }, title);
            var dupInfo = await QbitTorrentInfo(c, newHash);
            if (dupInfo == null) return SuccessorStart.Failed;
            bool ours = HasTag(dupInfo, SuccessorTag) && !OtherRecordsReferenceNext(allRecords, m, newHash);
            if (!ours)
            {
                Console.WriteLine("[QbitDownload] successor: «" + title + "» — " + newHash + " уже сидит в qBit обычной загрузкой, обычная замена");
                return SuccessorStart.Immediate;
            }
            // наш же брошенный преемник (ссылка потерялась) — усыновляем как есть
        }

        // 3. запись + стоп старой
        var now = DateTime.UtcNow;
        var next = new JObject
        {
            ["hash"] = newHash, ["reason"] = reason, ["link"] = newLink,
            ["mode"] = SuccessorModeMeta, ["savepath"] = dir,
            ["since"] = now.ToString("o"), ["deadline"] = now.AddDays(SuccessorMaxDays).ToString("o")
        };
        if (switchInfo != null) next["switch"] = switchInfo;
        m["next"] = next;
        await QbitStopTorrent(c, oldHash);

        // 4. метаданные и режим (не дождались — решит жнец на следующем тике)
        var newFiles = await QbitWaitFilesNoKick(c, newHash, Math.Max(0, ModInit.conf?.successorMetaWaitSec ?? 60));
        if (newFiles != null) await ApplySuccessorMode(c, m, oldFiles, newFiles);
        else Console.WriteLine("[QbitDownload] successor: «" + title + "» — метаданных " + newHash + " пока нет, режим выберет жнец");

        string sk = SeriesKey(m.Value<int?>("id") ?? 0, m.Value<string>("link"));
        QdlEvents.Log(QdlEvents.CatRelease, title,
                      (reason == "switch" ? "переключение начато: " : "раздача обновилась: ")
                      + "новая версия качается рядом (" + (next.Value<string>("mode")) + "), старая пока доступна", newHash, sk);
        Console.WriteLine("[QbitDownload] successor: «" + title + "» " + oldHash + " → " + newHash + " начат (" + reason + ", " + next.Value<string>("mode") + ")");
        return SuccessorStart.Started;
    }

    /// <summary>Выбрать режим по спискам файлов и запустить преемника.</summary>
    static async Task ApplySuccessorMode(HttpClient c, JObject m, JArray oldFiles, JArray newFiles)
    {
        var next = NextOf(m); if (next == null) return;
        string nh = next.Value<string>("hash");
        string title = m.Value<string>("title") ?? "";
        string mode = DecideSuccessorMode(oldFiles, newFiles);
        if (mode == SuccessorModeMeta) return;

        if (mode == SuccessorModeFlat)
        {
            // В общий корень — только с нулём скачанных байт: перенос частичных файлов лёг бы поверх старых.
            var info = await QbitTorrentInfo(c, nh);
            long completed = info?.Value<long?>("completed") ?? -1;
            long downloaded = info?.Value<long?>("downloaded") ?? -1;
            if (completed == 0 && downloaded == 0 && await QbitSetLocation(c, nh, ModInit.conf.downloadsPath))
                next["savepath"] = ModInit.conf.downloadsPath;
            else
            {
                mode = SuccessorModeAside;
                Console.WriteLine("[QbitDownload] successor: «" + title + "» — общий корень невозможен (скачано " + Math.Max(completed, downloaded) + " Б), качаем рядом");
            }
        }
        next["mode"] = mode;
        await QbitStartTorrent(c, nh);
        Console.WriteLine("[QbitDownload] successor: «" + title + "» " + nh + " — режим " + mode + ", запущен");
    }
    #endregion

    #region жнец
    /// <summary>
    /// Тик замены (из ScanEpisodeNotifications, под _watchGate). Покрыл старую или готов целиком →
    /// жатва; срок вышел → принудительная жатва (файлы старой остаются); пропал из qBit → старая
    /// возвращается в работу. qBit не ответил → тик пропущен целиком (fail-safe).
    /// </summary>
    static async Task ScanSuccessors(HttpClient c, JArray list, HashSet<string> orig)
    {
        if (!list.OfType<JObject>().Any(x => NextOf(x) != null)) return;

        var byHash = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var lampa = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}"));
            foreach (var t in lampa.OfType<JObject>()) { string h = t.Value<string>("hash"); if (ValidHash(h)) byHash[h] = t; }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] successor: qBit не ответил (" + ex.Message + ") — тик пропущен"); return; }

        bool changed = false;
        var now = DateTime.UtcNow;
        foreach (var m in list.OfType<JObject>())
        {
            var next = NextOf(m); if (next == null) continue;
            string title = m.Value<string>("title") ?? "";
            try
            {
                string oh = m.Value<string>("hash"), nh = NextHashOf(m);
                if (nh == null) { m["next"] = null; changed = true; continue; }

                if (!byHash.TryGetValue(nh, out var info)) info = await QbitTorrentInfo(c, nh);   // могли сменить категорию руками
                if (info == null)
                {
                    m["next"] = null; changed = true;
                    if (ValidHash(oh)) await QbitStartTorrent(c, oh);
                    QdlEvents.Log(QdlEvents.CatRelease, title, "замена отменена: новая раздача пропала из qBittorrent, старая возвращена в работу", oh);
                    Console.WriteLine("[QbitDownload] successor: «" + title + "» — преемник " + nh + " пропал из qBit, старая " + oh + " возвращена в работу");
                    continue;
                }

                DateTime deadline = ParseIsoUtc(next.Value<string>("deadline")) ?? now.AddDays(SuccessorMaxDays);
                bool overdue = now >= deadline;
                string mode = next.Value<string>("mode") ?? SuccessorModeMeta;

                if (mode == SuccessorModeMeta)
                {
                    var nf = await QbitFiles(c, nh);
                    if (nf != null && nf.Count > 0)
                    {
                        await ApplySuccessorMode(c, m, await QbitFiles(c, oh) ?? new JArray(), nf);
                        changed = true;
                    }
                    else if (overdue)
                    {
                        await AbortSuccessor(c, m, "метаданные так и не пришли за " + SuccessorMaxDays + " дн.", resumeOld: true);
                        changed = true;
                    }
                    continue;
                }

                var newFiles = await QbitFiles(c, nh);
                if (newFiles == null) continue;   // qBit моргнул — следующий тик
                if (!byHash.TryGetValue(oh, out var oldInfo)) oldInfo = ValidHash(oh) ? await QbitTorrentInfo(c, oh) : null;
                JArray oldFiles = oldInfo == null ? new JArray() : await QbitFiles(c, oh);
                if (oldInfo != null && oldFiles == null) continue;   // сбой связи по старой — не решаем

                int season = DominantSeason(oldFiles);
                if (season <= 0) season = DominantSeason(newFiles);
                if (season <= 0) season = Math.Max(1, (m["ctx"] as JObject)?.Value<int?>("season") ?? 1);

                double np = info.Value<double?>("progress") ?? 0;
                bool covered = oldInfo == null || np >= ProgressDone || SuccessorCovers(oldFiles, newFiles, season);
                if (!covered && !overdue) continue;

                if (covered && !overdue && oldInfo != null && (HlsBusy(oh) || PlayedRecently(oh)))
                {
                    Console.WriteLine("[QbitDownload] successor: «" + title + "» — старую сейчас смотрят, жатва отложена");
                    continue;
                }

                await SuccessorCutOver(c, m, forced: !covered);
                changed = true;
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] successor scan «" + title + "»: " + ex); }
        }
        if (changed) SaveWatchReconciled(list, orig);
    }

    /// <summary>
    /// Жатва: старая снимается (файлы — только aside, только safe-путь и только не по сроку),
    /// преемник теряет тег и при возможности переезжает в общий корень, кеш переезжает (MigrateCache),
    /// hash записи = преемник. Порядок — снимать зависимости ДО удаления (§AK).
    /// </summary>
    static async Task SuccessorCutOver(HttpClient c, JObject m, bool forced)
    {
        var next = NextOf(m); if (next == null) return;
        string oh = m.Value<string>("hash"), nh = NextHashOf(m);
        string title = m.Value<string>("title") ?? "";
        string mode = next.Value<string>("mode") ?? SuccessorModeMeta;
        string reason = next.Value<string>("reason");
        string sk = SeriesKey(m.Value<int?>("id") ?? 0, m.Value<string>("link"));

        var oldInfo = ValidHash(oh) ? await QbitTorrentInfo(c, oh) : null;
        var newInfo = await QbitTorrentInfo(c, nh);
        string oldPath = oldInfo?.Value<string>("content_path"), newPath = newInfo?.Value<string>("content_path");

        bool delFiles = false; string keepWhy = null;
        if (oldInfo != null)
        {
            if (forced) keepWhy = "замена по сроку";
            else if (mode != SuccessorModeAside) keepWhy = "общая папка";
            else if (!(ModInit.conf?.successorDeleteOldFiles ?? true)) keepWhy = "successorDeleteOldFiles=false";
            else if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) keepWhy = "путь неизвестен";
            else if (PathsOverlap(oldPath, newPath)) keepWhy = "папки пересекаются";
            else if (await SharesFilesWithAnyDownload(c, oldPath, oh)) keepWhy = "папку делит другая загрузка";
            else delFiles = true;
            await QbitDelete(c, oh, delFiles);
        }
        await QbitRemoveTags(c, nh, SuccessorTag);

        // aside → в общий корень, но только если там нет папки с таким именем (файлы старой могли остаться)
        string placed = null;
        if (mode == SuccessorModeAside && newInfo != null && IsSuccessorPath(newInfo.Value<string>("save_path")))
        {
            string root = (ModInit.conf.downloadsPath ?? "/downloads").TrimEnd('/', '\\');
            string leaf = Path.GetFileName((newPath ?? "").TrimEnd('/', '\\'));
            string dest = string.IsNullOrEmpty(leaf) ? null : root + "/" + leaf;
            bool busy = dest == null || Directory.Exists(dest) || System.IO.File.Exists(dest);
            if (!busy && await QbitSetLocation(c, nh, root)) placed = "новая перенесена в общий корень";
            else placed = "новая осталась в " + next.Value<string>("savepath");
        }

        MigrateCache(oh, nh);
        m["hash"] = nh;
        if (reason == "switch")
        {
            string nl = next.Value<string>("link");
            if (!string.IsNullOrWhiteSpace(nl)) m["link"] = nl;
            m["switchCount"] = (m.Value<int?>("switchCount") ?? 0) + 1;
            m["lastSwitch"] = DateTime.UtcNow.ToString("o");
        }
        m["stale"] = 0;
        m["pendingSwitch"] = null;
        m["next"] = null;
        ActivityTouch(nh);
        DropHlsCache(oh);
        DropListCache();

        string text = (forced ? "замена по сроку: новая раздача докачала не всё, " : "замена завершена: ")
            + "старая раздача снята" + (oldInfo == null ? " (её уже не было в qBit)" : delFiles ? ", файлы удалены" : " (файлы оставлены — " + keepWhy + ")")
            + (placed != null ? ", " + placed : "");
        QdlEvents.Log(QdlEvents.CatRelease, title, text, nh, sk);
        Console.WriteLine("[QbitDownload] successor: «" + title + "» " + oh + " → " + nh + ": " + text);
    }

    /// <summary>Отменить замену: преемник снимается (с файлами — только из своей подпапки), старая при resumeOld возвращается в работу.</summary>
    static async Task AbortSuccessor(HttpClient c, JObject m, string why, bool resumeOld)
    {
        var next = NextOf(m); if (next == null) return;
        await AbortSuccessorCore(c, m.Value<string>("hash"), next, m.Value<string>("title"), why, resumeOld);
        m["next"] = null;
    }

    static async Task AbortSuccessorCore(HttpClient c, string oldHash, JObject next, string title, string why, bool resumeOld)
    {
        string nh = next?.Value<string>("hash");
        if (ValidHash(nh))
        {
            var info = await QbitTorrentInfo(c, nh);
            if (info != null)
            {
                string cp = info.Value<string>("content_path");
                // с файлами — только из СВОЕЙ подпапки .next (общий корень делят старая и соседи) и только
                // если её не делит никто; на сомнении — торрент снимаем, файлы оставляем
                bool delFiles = HasTag(info, SuccessorTag) && IsSuccessorPath(info.Value<string>("save_path"))
                                && !string.IsNullOrWhiteSpace(cp) && !await SharesFilesWithAnyDownload(c, cp, nh);
                await QbitDelete(c, nh, delFiles);
                Console.WriteLine("[QbitDownload] successor: «" + title + "» — преемник " + nh + " снят " + (delFiles ? "с файлами" : "БЕЗ файлов") + " (" + why + ")");
            }
        }
        if (resumeOld && ValidHash(oldHash)) await QbitStartTorrent(c, oldHash);
        QdlEvents.Log(QdlEvents.CatRelease, title ?? "", "замена отменена (" + why + ")" + (resumeOld ? ", старая раздача возвращена в работу" : ""), oldHash);
    }

    /// <summary>Отмена по хешу основной из интерактивной ручки: запись ищется и сохраняется под _watchLock.</summary>
    static async Task AbortSuccessorOf(HttpClient c, string mainHash, string why, bool resumeOld)
    {
        JObject next = null; string title = null;
        lock (_watchLock)
        {
            foreach (var m in LoadWatch().OfType<JObject>())
                if (mainHash.Equals(m.Value<string>("hash"), StringComparison.OrdinalIgnoreCase)) { next = NextOf(m); title = m.Value<string>("title"); break; }
        }
        if (next == null) return;
        await AbortSuccessorCore(c, mainHash, next, title, why, resumeOld);
        lock (_watchLock)
        {
            var a = LoadWatch(); bool ch = false;
            foreach (var m in a.OfType<JObject>())
                if (mainHash.Equals(m.Value<string>("hash"), StringComparison.OrdinalIgnoreCase) && m["next"] != null) { m["next"] = null; ch = true; }
            if (ch) SaveWatch(a);
        }
    }

    /// <summary>
    /// Стартовая уборка (ModInit, рядом с ReconcileDonors): тег qdl-next без ссылки в watch.json →
    /// снять тег, торрент показывается обычной загрузкой (никаких удалений — потерянная ссылка не
    /// повод сносить); запись с next, у которой торрента нет → снять next, старую вернуть в работу.
    /// </summary>
    public static async Task ReconcileSuccessors()
    {
        try
        {
            if (!System.IO.File.Exists(WatchFile))
            {
                Console.WriteLine("[QbitDownload] successor: watch.json отсутствует — стартовая сверка преемников пропущена");
                return;
            }
            JArray list; lock (_watchLock) list = LoadWatch();
            var refs = NextHashes(list);

            using var c = await Qbit();
            var lampa = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}"));
            var inQbit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in lampa.OfType<JObject>())
            {
                string h = t.Value<string>("hash");
                if (!ValidHash(h)) continue;
                inQbit.Add(h);
                if (!HasTag(t, SuccessorTag) || refs.Contains(h)) continue;
                await QbitRemoveTags(c, h, SuccessorTag);
                Console.WriteLine("[QbitDownload] successor: " + h + " — тег без ссылки в watch.json, снят; торрент показывается обычной загрузкой"
                    + (IsSuccessorPath(t.Value<string>("save_path")) ? " (лежит в " + t.Value<string>("save_path") + ")" : ""));
            }

            bool changed = false;
            foreach (var m in list.OfType<JObject>())
            {
                string nh = NextHashOf(m);
                if (nh == null || inQbit.Contains(nh)) continue;
                if (await QbitTorrentInfo(c, nh) != null) continue;
                m["next"] = null; changed = true;
                string oh = m.Value<string>("hash");
                if (ValidHash(oh)) await QbitStartTorrent(c, oh);
                Console.WriteLine("[QbitDownload] successor: «" + m.Value<string>("title") + "» — преемника " + nh + " в qBit нет, старая " + oh + " возвращена в работу");
            }
            if (changed) lock (_watchLock) SaveWatch(list);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] successor reconcile: " + ex.Message); }
    }
    #endregion

    #region «сейчас смотрят»
    /// <summary>Свежая отметка /qdl/stream по хешу (ReplicaTouchPlayed пишет её и дома с 2.115).</summary>
    static bool PlayedRecently(string hash)
    {
        int grace = Math.Max(0, ModInit.conf?.successorPlayedGraceMinutes ?? 30);
        if (grace == 0 || !ValidHash(hash)) return false;
        try
        {
            JObject j; lock (_replicaPlayedLock) j = JsonStore.ReadObject(ReplicaPlayedPath);
            long at = ReplicaPlayedAt(j, hash);
            return at > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at < grace * 60L;
        }
        catch { return false; }
    }

    /// <summary>Живая HLS-сессия по хешу: бегущий ffmpeg или сегменты, которые запрашивали недавно.</summary>
    static bool HlsBusy(string hash)
    {
        if (!ValidHash(hash)) return false;
        string p = hash + "_";
        if (_hlsRunning.Keys.Any(k => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
        var now = DateTime.UtcNow;
        foreach (var kv in _hlsTouch)
            if (kv.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase) && now - kv.Value < _hlsTouchTtl) return true;
        return false;
    }
    #endregion

    #region админка и хелс
    /// <summary>Замены в ходу — для вкладки «Решения» админки (с живым прогрессом преемника).</summary>
    internal static async Task<JArray> AdminPendingSuccessors()
    {
        var res = new JArray();
        try
        {
            JArray list; lock (_watchLock) list = LoadWatch();
            var pend = list.OfType<JObject>().Where(x => NextOf(x) != null).ToList();
            if (pend.Count == 0) return res;
            HttpClient c = null;
            try { c = await Qbit(); } catch { }
            using (c)
                foreach (var m in pend)
                {
                    var n = NextOf(m);
                    string nh = n.Value<string>("hash");
                    var o = new JObject
                    {
                        ["hash"] = m.Value<string>("hash"),
                        ["next"] = nh,
                        ["title"] = m.Value<string>("title"),
                        ["reason"] = n.Value<string>("reason"),
                        ["mode"] = n.Value<string>("mode"),
                        ["since"] = n.Value<string>("since"),
                        ["deadline"] = n.Value<string>("deadline"),
                        ["candidate"] = (n["switch"] as JObject)?.Value<string>("title")
                    };
                    if (c != null && ValidHash(nh))
                    {
                        var info = await QbitTorrentInfo(c, nh);
                        if (info != null) { o["progress"] = info.Value<double?>("progress") ?? 0; o["state"] = info.Value<string>("state"); }
                        else o["state"] = "missing";
                    }
                    res.Add(o);
                }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] pending successors: " + ex.Message); }
        return res;
    }

    /// <summary>Строка хелса «Замена раздач»: ok — нет замен или все идут; warn — без метаданных дольше часа или срок вышел.</summary>
    internal static (string status, string detail) SuccessorHealthVerdict(JArray list, DateTime now)
    {
        int total = 0, meta = 0, overdue = 0;
        var names = new List<string>();
        foreach (var m in (list ?? new JArray()).OfType<JObject>())
        {
            var n = NextOf(m); if (n == null) continue;
            total++;
            string t = m.Value<string>("title"); if (!string.IsNullOrWhiteSpace(t) && names.Count < 3) names.Add(t);
            var since = ParseIsoUtc(n.Value<string>("since"));
            if ((n.Value<string>("mode") ?? SuccessorModeMeta) == SuccessorModeMeta && since != null && (now - since.Value).TotalHours >= 1) meta++;
            var dl = ParseIsoUtc(n.Value<string>("deadline"));
            if (dl != null && now >= dl.Value) overdue++;
        }
        if (total == 0) return (HealthState.StatusOk, "замен в ходу нет");
        string d = "в ходу: " + total + (names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "");
        if (meta > 0) return (HealthState.StatusWarn, d + " · без метаданных дольше часа: " + meta);
        if (overdue > 0) return (HealthState.StatusWarn, d + " · срок вышел, ждёт жатвы: " + overdue);
        return (HealthState.StatusOk, d);
    }
    #endregion
}
