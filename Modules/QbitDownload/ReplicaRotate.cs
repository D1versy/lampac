using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Ротация реплики: «новое пришло — старое вычистить» ──────────────────────────
//
// 🔴 Это единственное место во всём модуле, которое удаляет файлы САМО, по таймеру, без
// человека и на удалённой машине. Прецедент известен: модуль уже удалял раздачу вместе с
// файлами (§AK), и тогда речь шла о ручном действии — здесь оно фоновое и ежедневное.
// Поэтому гарды написаны раньше самой логики, а боевой режим выключен по умолчанию.
//
// Восемь условий, и каждое ловит свой класс беды:
//   1. dry-run по умолчанию      — первая неделя только журнал, ничего не удаляется;
//   2. аудит-журнал              — «оно само пропало» иначе неотлаживаемо через туннель;
//   3. только своя категория     — чужие раздачи на той же машине не наши;
//   4. путь внутри downloadsPath — защита от маркера с чужим путём;
//   5. общая папка               — раздача рядом с другой удаляется БЕЗ файлов;
//   6. лизинг/незавершённость    — то, что качается прямо сейчас, не трогаем;
//   7. свежесть и просмотр       — резиденция N часов и «играли за сутки»;
//   8. кап удалений за тик       — ошибка в отборе стоит N карточек, а не библиотеки.

public partial class QbitController
{
    static string ReplicaEvictLogPath => Path.Combine(ModInit.conf.cachePath, "replica-evictions.log");

    /// <summary>
    /// Чистит вниз до нижней ватерлинии, но только если перевалили за верхнюю.
    /// Возвращает число вычищенных карточек (в dry-run — сколько вычистил бы).
    /// </summary>
    static async Task<int> ReplicaRotate(
        Dictionary<string, JObject> mine,
        Dictionary<string, JObject> myLocal,
        HashSet<string> targetSet,
        long highMark)
    {
        long budget = Math.Max(1, ModInit.conf.replicaBudgetGb) * GiB;
        long lowMark = budget * Math.Clamp(ModInit.conf.replicaLowWatermark, 10, 99) / 100;

        long total = 0;
        foreach (var t in mine.Values) total += t.Value<long?>("size") ?? 0;
        foreach (var l in myLocal.Values) total += LocalMarkerSize(l);

        if (total <= highMark)
            return 0;

        Console.WriteLine($"[QbitDownload] replica: занято {Bytes(total)} при верхней ватерлинии {Bytes(highMark)} — чистим до {Bytes(lowMark)}");

        JObject act; lock (_activityLock) act = ActivityLoad();
        JObject played; lock (_replicaPlayedLock) played = JsonStore.ReadObject(ReplicaPlayedPath) ?? new JObject();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long residence = Math.Max(0, ModInit.conf.replicaMinResidenceHours) * 3600L;
        const long PlayedGrace = 24 * 3600;

        // Кандидаты — всё, чего НЕТ в плане. Порядок — от самого залежавшегося.
        var cands = new List<(string hash, long size, long activity, bool jut)>();
        foreach (var kv in mine)
        {
            if (targetSet.Contains(kv.Key)) continue;
            cands.Add((kv.Key, kv.Value.Value<long?>("size") ?? 0, ActivityStored(act, kv.Key), false));
        }
        foreach (var kv in myLocal)
        {
            if (targetSet.Contains(kv.Key)) continue;
            cands.Add((kv.Key, LocalMarkerSize(kv.Value), ActivityStored(act, kv.Key), true));
        }

        int cap = Math.Max(1, ModInit.conf.replicaMaxDeletesPerTick);
        bool dry = ModInit.conf.replicaRotateDryRun;
        int done = 0;

        foreach (var cand in cands.OrderBy(x => x.activity == 0 ? long.MaxValue : x.activity))
        {
            if (total <= lowMark || done >= cap) break;

            string why;
            if (!ReplicaMayEvict(cand.hash, cand.jut, mine, myLocal, played, now, residence, PlayedGrace, out why))
            {
                ReplicaEvictLog($"пропуск {cand.hash} ({Bytes(cand.size)}): {why}");
                continue;
            }

            string name = cand.jut
                ? (myLocal[cand.hash].Value<string>("name") ?? cand.hash)
                : (mine[cand.hash].Value<string>("name") ?? cand.hash);

            if (dry)
            {
                ReplicaEvictLog($"[dry-run] удалил бы «{name}» {Bytes(cand.size)} (activity {cand.activity})");
                done++;
                total -= cand.size;
                continue;
            }

            bool ok = cand.jut
                ? ReplicaEvictLocal(cand.hash, myLocal[cand.hash])
                : await ReplicaEvictTorrent(cand.hash, mine);

            if (!ok) continue;

            ReplicaEvictLog($"удалено «{name}» {Bytes(cand.size)} (activity {cand.activity})");
            done++;
            total -= cand.size;
        }

        if (dry && done > 0)
            Console.WriteLine($"[QbitDownload] replica: 🔸 dry-run — удалил бы {done} шт. Журнал: {ReplicaEvictLogPath}. Боевой режим: replicaRotateDryRun=false");

        return done;
    }

    /// <summary>Гарды. false = не трогаем, why — почему (уходит в журнал).</summary>
    internal static bool ReplicaMayEvict(
        string hash, bool jut,
        Dictionary<string, JObject> mine,
        Dictionary<string, JObject> myLocal,
        JObject played, long now, long residence, long playedGrace,
        out string why)
    {
        // играли здесь недавно — не выдёргиваем из-под зрителя ни при каком бюджете
        long p = ReplicaPlayedAt(played, hash);
        if (p > 0 && now - p < playedGrace) { why = "играли " + ((now - p) / 3600) + " ч назад"; return false; }

        if (jut)
        {
            var loc = myLocal[hash];
            long added = loc.Value<long?>("added") ?? 0;
            if (added > 0 && now - added < residence) { why = "свежее резиденции"; return false; }

            foreach (var f in LocalFiles(loc))
                if (!ReplicaInsideDownloads(f.path)) { why = "файл вне downloadsPath: " + f.path; return false; }

            why = null;
            return true;
        }

        var t = mine[hash];

        long addedOn = t.Value<long?>("added_on") ?? 0;
        if (addedOn > 0 && now - addedOn < residence) { why = "свежее резиденции"; return false; }

        // качается прямо сейчас — пусть докачается или уйдёт из плана сам
        double prog = t.Value<double?>("progress") ?? 0;
        if (prog < 0.999) { why = "ещё качается (" + Math.Round(prog * 100) + "%)"; return false; }

        string cat = t.Value<string>("category") ?? "";
        if (!string.Equals(cat, ModInit.conf.category, StringComparison.OrdinalIgnoreCase))
        { why = "чужая категория «" + cat + "»"; return false; }

        string cpath = t.Value<string>("content_path");
        if (string.IsNullOrEmpty(cpath) || !ReplicaInsideDownloads(cpath))
        { why = "content_path вне downloadsPath: " + cpath; return false; }

        why = null;
        return true;
    }

    /// <summary>Абсолютный путь строго внутри downloadsPath (или он сам).</summary>
    internal static bool ReplicaInsideDownloads(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            string root = Path.GetFullPath(ModInit.conf.downloadsPath);
            string full = Path.GetFullPath(path);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(full, root, cmp)) return false;   // сам корень удалять нельзя никогда
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, cmp);
        }
        catch { return false; }
    }

    static async Task<bool> ReplicaEvictTorrent(string hash, Dictionary<string, JObject> mine)
    {
        try
        {
            string cpath = mine[hash].Value<string>("content_path");

            // Общая папка: рядом живёт другая раздача (сезон, разложенный по одной директории).
            // Тогда снимаем ТОЛЬКО раздачу, файлы оставляем — иначе унесём чужие серии.
            bool shared = false;
            foreach (var kv in mine)
            {
                if (string.Equals(kv.Key, hash, StringComparison.OrdinalIgnoreCase)) continue;
                string other = kv.Value.Value<string>("content_path");
                if (string.IsNullOrEmpty(other) || string.IsNullOrEmpty(cpath)) continue;
                if (PathTouches(cpath, other)) { shared = true; break; }
            }

            using var c = await Qbit();
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("deleteFiles", shared ? "false" : "true")
            });
            var r = await c.PostAsync("/api/v2/torrents/delete", form);
            if (!r.IsSuccessStatusCode) return false;

            if (shared) ReplicaEvictLog($"⚠️ {hash}: папка общая с другой раздачей — снят torrent, файлы оставлены");

            DropHlsCache(hash);
            DropResolveCache(hash);
            ActivityRemove(hash);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica evict " + hash + ": " + ex.Message);
            return false;
        }
    }

    static bool ReplicaEvictLocal(string hash, JObject loc)
    {
        try
        {
            DeleteLocalFiles(loc);

            string marker = LocalPath(hash);
            try { if (System.IO.File.Exists(marker)) System.IO.File.Delete(marker); } catch { }
            JsonStore.Forget(marker);
            JsonStore.ForgetDir(Path.Combine(ModInit.conf.cachePath, "local"));

            DropHlsCache(hash);
            DropResolveCache(hash);
            ActivityRemove(hash);
            DropListCache();

            // мету и постер НЕ удаляем: килобайты, зато вернувшаяся карточка не поедет за ними заново
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica evict local " + hash + ": " + ex.Message);
            return false;
        }
    }

    // Пути пересекаются: один внутри другого или совпадают.
    internal static bool PathTouches(string a, string b)
    {
        try
        {
            string fa = Path.GetFullPath(a), fb = Path.GetFullPath(b);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(fa, fb, cmp)) return true;
            string sep = Path.DirectorySeparatorChar.ToString();
            return fb.StartsWith(fa.EndsWith(sep) ? fa : fa + sep, cmp)
                || fa.StartsWith(fb.EndsWith(sep) ? fb : fb + sep, cmp);
        }
        catch { return false; }
    }

    static long LocalMarkerSize(JObject loc)
    {
        long s = loc?.Value<long?>("size") ?? 0;
        if (s > 0) return s;
        foreach (var f in LocalFiles(loc)) s += f.size;
        return s;
    }

    /// <summary>
    /// Аудит удалений. Отдельный файл, а не только stdout: логи контейнера ротируются, а вопрос
    /// «куда делся фильм» задаётся через неделю и с другой машины.
    /// </summary>
    static void ReplicaEvictLog(string line)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine("[QbitDownload] replica rotate: " + line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReplicaEvictLogPath));
            System.IO.File.AppendAllText(ReplicaEvictLogPath, stamp + "  " + line + Environment.NewLine);
        }
        catch { }
    }
}
