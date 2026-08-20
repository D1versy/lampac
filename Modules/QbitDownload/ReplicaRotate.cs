using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Удаление на реплике: два класса ─────────────────────────────────────────────
//
// 🔴 Это единственное место во всём модуле, которое удаляет файлы САМО, по таймеру, без
// человека и на удалённой машине. Прецедент известен: модуль уже удалял раздачу вместе с
// файлами (§AK), и тогда речь шла о ручном действии — здесь оно фоновое и ежедневное.
// Поэтому гарды написаны раньше самой логики, а боевой режим выключен по умолчанию.
//
// Классы удаления РАЗНЫЕ, и путать их нельзя:
//
//   • СИРОТА (ReplicaMirrorDeletes) — хеша нет у дома ВООБЩЕ. Удаляется независимо от
//     ватерлиний: бюджет тут ни при чём, контент не вернётся. Выключатели свои
//     (replicaMirrorDeletes / replicaMirrorDryRun), кап свой.
//   • ИЗЛИШЕК (ReplicaRotate) — у дома есть, но не влез в план. Выселяется только выше
//     верхней ватерлинии и вернётся, когда освободится место.
//
// Восемь условий бюджетного выселения, каждое ловит свой класс беды:
//   1. dry-run по умолчанию      — первая неделя только журнал, ничего не удаляется;
//   2. аудит-журнал              — «оно само пропало» иначе неотлаживаемо через туннель;
//   3. только своя категория     — чужие раздачи на той же машине не наши;
//   4. путь внутри downloadsPath — защита от маркера с чужим путём;
//   5. общая папка               — раздача рядом с другой удаляется БЕЗ файлов;
//   6. лизинг/незавершённость    — то, что качается прямо сейчас, не трогаем;
//   7. свежесть и просмотр       — резиденция N часов и «играли за сутки»;
//   8. кап удалений за тик       — ошибка в отборе стоит N карточек, а не библиотеки.
//
// Канон обоих классов — claude/02-rotation.md в репозитории реплики.

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
        long highMark,
        HashSet<string> gone = null)
    {
        long budget = Math.Max(1, ModInit.conf.replicaBudgetGb) * GiB;
        long lowMark = budget * Math.Clamp(ModInit.conf.replicaLowWatermark, 10, 99) / 100;

        // 🔴 gone — то, что зеркальный проход УЖЕ снёс в этом тике. Словари mine/myLocal при
        // этом НЕ мутируются намеренно: ReplicaEvictTorrent сканирует mine в поисках соседа по
        // папке, и запись, выкинутая из словаря, спрятала бы факт «папка общая» — файлы соседа
        // уехали бы вместе с раздачей. Поэтому снимок остаётся полным, а исключения точечные.
        bool Gone(string h) => gone != null && gone.Contains(h);

        long total = 0;
        foreach (var kv in mine) { if (!Gone(kv.Key)) total += kv.Value.Value<long?>("size") ?? 0; }
        foreach (var kv in myLocal) { if (!Gone(kv.Key)) total += LocalMarkerSize(kv.Value); }

        if (total <= highMark)
            return 0;

        Console.WriteLine($"[QbitDownload] replica: занято {Bytes(total)} при верхней ватерлинии {Bytes(highMark)} — чистим до {Bytes(lowMark)}");

        JObject act; lock (_activityLock) act = ActivityLoad();
        JObject played; lock (_replicaPlayedLock) played = JsonStore.ReadObject(ReplicaPlayedPath) ?? new JObject();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long residence = Math.Max(0, ModInit.conf.replicaMinResidenceHours) * 3600L;
        const long PlayedGrace = 24 * 3600;

        // Кандидаты — всё, чего НЕТ в плане. Порядок — от самого залежавшегося.
        var cands = new List<(string hash, long size, long activity, long added, bool jut)>();
        foreach (var kv in mine)
        {
            if (targetSet.Contains(kv.Key) || Gone(kv.Key)) continue;
            cands.Add((kv.Key, kv.Value.Value<long?>("size") ?? 0, ActivityStored(act, kv.Key),
                       kv.Value.Value<long?>("added_on") ?? 0, false));
        }
        foreach (var kv in myLocal)
        {
            if (targetSet.Contains(kv.Key) || Gone(kv.Key)) continue;
            cands.Add((kv.Key, LocalMarkerSize(kv.Value), ActivityStored(act, kv.Key),
                       kv.Value.Value<long?>("added") ?? 0, true));
        }

        int cap = Math.Max(1, ModInit.conf.replicaMaxDeletesPerTick);
        bool dry = ModInit.conf.replicaRotateDryRun;
        int done = 0;

        foreach (var cand in cands.OrderBy(x => ReplicaEvictOrder(x.activity, x.added)).ThenBy(x => x.hash, StringComparer.Ordinal))
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

    /// <param name="allowFiles">
    /// false = снять только раздачу, файлы не трогать. Ставится зеркальным проходом, когда
    /// content_path пуст или ведёт наружу downloadsPath: раздачу, которой нет у дома, снять
    /// надо в любом случае (иначе магнет без метаданных стучится в трекер вечно), а вот файлы
    /// без доказательства «они наши» не удаляем никогда.
    /// </param>
    static async Task<bool> ReplicaEvictTorrent(string hash, Dictionary<string, JObject> mine, bool allowFiles = true, string tag = "бюджет")
    {
        try
        {
            string cpath = mine[hash].Value<string>("content_path");

            // Общая папка: рядом живёт другая раздача (сезон, разложенный по одной директории).
            // Тогда снимаем ТОЛЬКО раздачу, файлы оставляем — иначе унесём чужие серии.
            // 🔴 Это же условие бесплатно чинит перекачку: после смены инфохеша дома новый хеш
            // уже добран репликой в ту же папку, поэтому старый снимается БЕЗ файлов, и новый
            // засчитывает их своим recheck'ом вместо повторной выкачки всего релиза.
            bool shared = false;
            foreach (var kv in mine)
            {
                if (string.Equals(kv.Key, hash, StringComparison.OrdinalIgnoreCase)) continue;
                string other = kv.Value.Value<string>("content_path");
                if (string.IsNullOrEmpty(other) || string.IsNullOrEmpty(cpath)) continue;
                if (PathTouches(cpath, other)) { shared = true; break; }
            }

            bool dropFiles = allowFiles && !shared;

            using var c = await Qbit();
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("deleteFiles", dropFiles ? "true" : "false")
            });
            var r = await c.PostAsync("/api/v2/torrents/delete", form);
            if (!r.IsSuccessStatusCode) return false;

            if (shared) ReplicaEvictLog($"⚠️ {hash}: папка общая с другой раздачей — снят torrent, файлы оставлены", tag);

            DropHlsCache(hash);
            DropResolveCache(hash);
            ActivityRemove(hash);
            DropListCache();   // иначе грид до listCacheSeconds показывает уже снятую раздачу
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica evict " + hash + ": " + ex.Message);
            return false;
        }
    }

    static bool ReplicaEvictLocal(string hash, JObject loc, bool allowFiles = true)
    {
        try
        {
            // allowFiles=false — маркер ссылается наружу downloadsPath: карточку снимаем,
            // чужие файлы не трогаем.
            if (allowFiles) DeleteLocalFiles(loc);

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
    /// Ключ сортировки бюджетных кандидатов: сначала штамп активности, при его отсутствии —
    /// собственная дата появления раздачи, и только когда нет ни того ни другого — 0, то есть
    /// «самое старое», как и написано в каноне.
    /// ⚠️ Прежний код давал в последнем случае long.MaxValue: запись БЕЗ штампа выселялась
    /// последней — ровно наоборот канону. Но и буквальный фикс был бы неточен: activity==0
    /// означает «не знаем», а не «самое старое», и своя дата появления у кандидата почти
    /// всегда есть — выбрасывать её незачем.
    /// </summary>
    internal static long ReplicaEvictOrder(long activity, long added)
        => activity > 0 ? activity : (added > 0 ? added : 0);

    /// <summary>
    /// Аудит удалений. Отдельный файл, а не только stdout: логи контейнера ротируются, а вопрос
    /// «куда делся фильм» задаётся через неделю и с другой машины.
    /// Тег класса первым полем — чтобы grep отделял зеркалирование от бюджетной ротации.
    /// </summary>
    static void ReplicaEvictLog(string line, string tag = "бюджет")
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine($"[QbitDownload] replica {(tag == "сирота" ? "mirror" : "rotate")}: {line}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReplicaEvictLogPath));
            System.IO.File.AppendAllText(ReplicaEvictLogPath, $"{stamp}  [{tag}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    // ── Зеркалирование удалений: чего нет у дома, тому здесь не место ───────────────────────
    const string OrphanTag = "сирота";

    /// <summary>
    /// Разбор поля known манифеста плюс санити-проверка покрытия. null = доверять нельзя,
    /// зеркалирование в этом тике не работает (why — что написать в лог и в хелс-чек).
    /// 🔴 Проверка покрытия обязательна: known, не накрывающий того, что дом сам же прислал к
    /// репликации, собран не тем кодом — а «пробел в known» читается как «дома этого нет».
    /// </summary>
    internal static HashSet<string> ReplicaKnownSet(JArray knownArr, IEnumerable<string> mustCover, out string why)
    {
        why = null;

        if (knownArr == null)
        {
            why = "дом не отдаёт known — зеркалирование удалений выключено";
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in knownArr)
        {
            string h = ((string)k ?? "").ToLowerInvariant();
            if (ValidHash(h)) set.Add(h);
        }

        foreach (string h in mustCover ?? Array.Empty<string>())
        {
            if (set.Contains(h)) continue;
            why = "known не покрывает манифест — зеркалирование удалений выключено";
            return null;
        }

        return set;
    }

    /// <summary>
    /// Кандидаты в сироты: есть у нас, но нет в known дома.
    /// 🔴 Вынесено чистой функцией ради регресса «есть у дома, но не в плане — это НЕ сирота».
    /// Перепутать targetSet (бюджетный план, 85% от 240 ГБ) с homeKnown (всё, что есть у дома,
    /// сегодня 598 ГБ) — самая дорогая ошибка, какую здесь можно сделать: она превратила бы
    /// «не влезло» в «удалить».
    /// </summary>
    internal static List<(string hash, bool jut)> ReplicaOrphanCandidates(
        Dictionary<string, JObject> mine,
        Dictionary<string, JObject> myLocal,
        HashSet<string> homeKnown)
    {
        var res = new List<(string hash, bool jut)>();

        // Пустой known — не команда «удали всё». Дом с пустой библиотекой и дом, отдавший мусор,
        // снаружи неразличимы, а цена ошибки различается на три порядка.
        if (homeKnown == null || homeKnown.Count == 0) return res;

        foreach (var kv in mine)
            if (!homeKnown.Contains(kv.Key)) res.Add((kv.Key, false));

        foreach (var kv in myLocal)
            if (!homeKnown.Contains(kv.Key)) res.Add((kv.Key, true));

        return res;
    }

    /// <summary>
    /// Подтверждение пропажи. Чистая: мутирует state в памяти, на диск не пишет и в сеть не ходит.
    /// Готовность требует ОБОИХ условий сразу — и числа промахов, и времени по стенным часам.
    /// Одних тиков мало: три промаха набегают за три минуты после рестарта. Одного времени мало:
    /// скачок часов контейнера дал бы мгновенное удаление.
    /// </summary>
    internal static (List<string> ready, int pending) ReplicaOrphanConfirm(
        JObject state, IReadOnlyCollection<string> missing, long now,
        int confirmTicks, int confirmMinutes, out bool changed)
    {
        changed = false;
        var ready = new List<string>();
        int pending = 0;

        int needTicks = Math.Max(1, confirmTicks);
        long needSec = Math.Max(0, confirmMinutes) * 60L;

        var missingSet = new HashSet<string>(missing ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // Снятие записи: хеш вернулся у дома ЛИБО его больше нет у нас. Второе — заодно прунинг,
        // иначе файл рос бы вечно на каждом удалённом и вручную снятом хеше.
        foreach (var p in state.Properties().ToList())
            if (!missingSet.Contains(p.Name)) { state.Remove(p.Name); changed = true; }

        foreach (string h in missingSet)
        {
            var rec = state[h] as JObject;
            if (rec == null)
            {
                rec = new JObject { ["since"] = now, ["misses"] = 0 };
                state[h] = rec;
            }

            if ((rec.Value<long?>("since") ?? 0) <= 0) rec["since"] = now;
            rec["misses"] = (rec.Value<int?>("misses") ?? 0) + 1;
            changed = true;

            long since = rec.Value<long?>("since") ?? now;
            int misses = rec.Value<int?>("misses") ?? 1;

            if (misses >= needTicks && now - since >= needSec) ready.Add(h);
            else pending++;
        }

        return (ready, pending);
    }

    /// <summary>
    /// Гарды сироты. Отличаются от бюджетных намеренно (канон 02-rotation.md, таблица гардов):
    /// возраст и прогресс НЕ проверяются — дом это удалил, и недокачанный огрызок удалённого не
    /// просто не нужен, он ещё и занимает канал.
    /// filesOk=false означает «раздачу снять, файлы не трогать».
    /// </summary>
    internal static bool ReplicaMayEvictOrphan(
        string hash, bool jut,
        Dictionary<string, JObject> mine, Dictionary<string, JObject> myLocal,
        JObject played, long now, long playedGrace,
        out bool filesOk, out string why)
    {
        filesOk = false;
        why = null;

        // 🔴 «Играли здесь» — ОТСРОЧКА, а не вето: счётчик подтверждений не сбрасывается, и в
        // первый же тик после грейса сирота уходит. Удаление гарантировано, вопрос только «когда».
        long p = ReplicaPlayedAt(played, hash);
        if (playedGrace > 0 && p > 0 && now - p < playedGrace)
        {
            why = $"играли {Math.Max(1, (now - p) / 60)} мин назад (грейс {playedGrace / 60} мин)";
            return false;
        }

        if (jut)
        {
            if (!myLocal.TryGetValue(hash, out var loc)) { why = "маркер исчез"; return false; }

            filesOk = true;
            foreach (var f in LocalFiles(loc))
                if (!ReplicaInsideDownloads(f.path)) { filesOk = false; break; }

            if (!filesOk) why = "файл вне downloadsPath — маркер снят, файлы оставлены";
            return true;
        }

        if (!mine.TryGetValue(hash, out var t)) { why = "раздача исчезла"; return false; }

        string cat = t.Value<string>("category") ?? "";
        if (!string.Equals(cat, ModInit.conf.category, StringComparison.OrdinalIgnoreCase))
        { why = $"чужая категория «{cat}»"; return false; }

        // 🔴 Путь работает иначе, чем в бюджетном выселении. Там «путь не наш» = не трогаем
        // вовсе; здесь раздачу снять НАДО в любом случае — иначе магнет без метаданных
        // (content_path пуст) стучался бы в трекер вечно уже после удаления дома. Но файлы без
        // доказательства «они внутри downloadsPath» не удаляем никогда: инвариант дословный.
        string cpath = t.Value<string>("content_path");
        filesOk = !string.IsNullOrEmpty(cpath) && ReplicaInsideDownloads(cpath);
        if (!filesOk) why = $"путь не наш ({(string.IsNullOrEmpty(cpath) ? "пусто" : cpath)}) — снята раздача без файлов";

        return true;
    }

    /// <summary>
    /// Артефакты сироты. В отличие от бюджетного выселения мету и постер УДАЛЯЕМ: там карточка
    /// ждёт возврата, здесь дом её удалил и она не вернётся.
    /// 🔴 PurgeCache переиспользовать нельзя: в его шапке «вызывать ТОЛЬКО из /qdl/delete», он
    /// лезет в watch.json и в qdl.db по seriesKey — а это данные ДОМА, которые на реплику
    /// приезжают отдельным потоком истории.
    /// </summary>
    static void ReplicaForgetArtifacts(string hash)
    {
        foreach (var path in new[] { MetaPath(hash), PosterPath(hash), LinkPath(hash) })
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
            JsonStore.Forget(path);
        }

        JsonStore.ForgetDir(Path.Combine(ModInit.conf.cachePath, "meta"));
        JsonStore.ForgetDir(Path.Combine(ModInit.conf.cachePath, "img"));
        PosterWritten();   // §BV: снимок img/ иначе врёт про has_poster до рестарта

        // Протухшая отметка «играли» затормозила бы будущее появление того же инфохеша.
        try
        {
            lock (_replicaPlayedLock)
            {
                var j = JsonStore.ReadObject(ReplicaPlayedPath);
                if (j != null && j.Remove(hash)) JsonStore.Write(ReplicaPlayedPath, j);
            }
        }
        catch { }

        DropListCache();
    }

    /// <summary>
    /// Зеркальный проход. Возвращает (сколько удалено, сколько ждёт подтверждения, что снесено).
    /// homeKnown == null → дом не отдал полный набор, проход не выполняется вовсе.
    /// </summary>
    static async Task<(int done, int pending, HashSet<string> gone)> ReplicaMirrorDeletes(
        Dictionary<string, JObject> mine,
        Dictionary<string, JObject> myLocal,
        HashSet<string> homeKnown)
    {
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ModInit.conf?.replicaMirrorDeletes != true) return (0, 0, gone);
        if (homeKnown == null) return (0, 0, gone);   // причину печатает вызывающий

        var cands = ReplicaOrphanCandidates(mine, myLocal, homeKnown);

        // Тормоз массовости. shrink-guard сравнивает СЧЁТЧИК манифеста с прошлым тиком, а этот —
        // ПЕРЕСЕЧЕНИЕ нашего набора с домашним: валидный, но не тот снимок дома shrink пропустит,
        // а здесь он виден сразу. Нормальный день — одна-три сироты.
        int mineTotal = mine.Count + myLocal.Count;
        int sharePct = Math.Clamp(ModInit.conf.replicaOrphanMaxSharePercent, 1, 100);
        if (cands.Count > 0 && mineTotal > 0 && cands.Count * 100 > mineTotal * sharePct)
        {
            string stop = $"сирот {cands.Count} из {mineTotal} — зеркалирование остановлено (порог {sharePct}%)";
            ReplicaEvictLog(stop, OrphanTag);
            HealthState.Degraded(HealthState.Ids.Replica, stop);
            return (0, cands.Count, gone);
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        JObject state;
        lock (_replicaOrphansLock) state = JsonStore.ReadObject(ReplicaOrphansPath) ?? new JObject();

        var byHash = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cands) byHash[c.hash] = c.jut;

        string NameOf(string h, bool jut)
        {
            JObject o = jut ? (myLocal.TryGetValue(h, out var l) ? l : null)
                            : (mine.TryGetValue(h, out var t) ? t : null);
            return o?.Value<string>("name") ?? h;
        }

        long SizeOf(string h, bool jut)
        {
            if (jut) return myLocal.TryGetValue(h, out var l) ? LocalMarkerSize(l) : 0;
            return mine.TryGetValue(h, out var t) ? (t.Value<long?>("size") ?? 0) : 0;
        }

        // «Отмена» пишется ДО пересчёта: после него записи уже не будет. Логируем только те,
        // что у нас всё ещё лежат — исчезнувшие с диска это просто прунинг, а не событие.
        foreach (var p in state.Properties().ToList())
        {
            if (byHash.ContainsKey(p.Name)) continue;
            if (!mine.ContainsKey(p.Name) && !myLocal.ContainsKey(p.Name)) continue;
            ReplicaEvictLog($"отмена «{(p.Value as JObject)?.Value<string>("name") ?? p.Name}» — хеш снова есть у дома", OrphanTag);
        }

        var (ready, pending) = ReplicaOrphanConfirm(
            state, byHash.Keys, now,
            ModInit.conf.replicaOrphanConfirmTicks, ModInit.conf.replicaOrphanConfirmMinutes,
            out bool changed);

        // Имя и размер — только для читаемости журнала и диагностики, в логике не участвуют.
        // Заодно строка «подтверждение» — ОДИН раз, на первом промахе: при большом окне
        // подтверждения строка на каждый тик дала бы десятки записей на один хеш.
        foreach (var kv in byHash)
        {
            if (state[kv.Key] is not JObject rec) continue;

            string nm = NameOf(kv.Key, kv.Value);
            long sz = SizeOf(kv.Key, kv.Value);
            rec["name"] = nm;
            rec["size"] = sz;

            if ((rec.Value<int?>("misses") ?? 0) == 1)
                ReplicaEvictLog($"подтверждение 1/{Math.Max(1, ModInit.conf.replicaOrphanConfirmTicks)} «{nm}» {Bytes(sz)} — нет у дома", OrphanTag);
        }

        JObject played; lock (_replicaPlayedLock) played = JsonStore.ReadObject(ReplicaPlayedPath) ?? new JObject();

        long playedGrace = Math.Max(0, ModInit.conf.replicaOrphanPlayedGraceMinutes) * 60L;
        int cap = Math.Max(1, ModInit.conf.replicaMaxOrphanDeletesPerTick);
        bool dry = ModInit.conf.replicaMirrorDryRun;
        int done = 0;

        // Порядок — от самой давней пропажи: если сирот больше капа, за тик уходят те, что
        // подтверждены дольше всех, а не случайные.
        foreach (string h in ready
            .OrderBy(x => (state[x] as JObject)?.Value<long?>("since") ?? 0)
            .ThenBy(x => x, StringComparer.Ordinal))
        {
            if (done >= cap) break;

            bool jut = byHash[h];
            var rec = state[h] as JObject;
            long since = rec?.Value<long?>("since") ?? now;
            int misses = rec?.Value<int?>("misses") ?? 0;
            string name = NameOf(h, jut);
            long size = SizeOf(h, jut);

            if (!ReplicaMayEvictOrphan(h, jut, mine, myLocal, played, now, playedGrace, out bool filesOk, out string why))
            {
                // Причина отсрочки пишется на ПЕРЕХОД, а не каждый тик: «играли N мин назад»
                // меняется ежеминутно и залила бы журнал сотней строк на один хеш.
                if (rec != null && rec.Value<string>("hold") == null)
                {
                    ReplicaEvictLog($"отсрочка «{name}»: {why}", OrphanTag);
                    rec["hold"] = why;
                    changed = true;
                }
                continue;
            }

            if (rec != null && rec["hold"] != null) { rec.Remove("hold"); changed = true; }

            string filesNote = filesOk ? "с файлами" : "без файлов";
            string ageNote = $"нет у дома {Math.Max(1, (now - since) / 60)} мин, тиков {misses}";

            if (dry)
            {
                ReplicaEvictLog($"[dry-run] удалил бы «{name}» {Bytes(size)} {filesNote} ({ageNote})", OrphanTag);
                done++;
                continue;
            }

            if (!filesOk) ReplicaEvictLog($"⚠️ {h}: {why}", OrphanTag);

            bool ok = jut
                ? ReplicaEvictLocal(h, myLocal[h], filesOk)
                : await ReplicaEvictTorrent(h, mine, filesOk, OrphanTag);

            if (!ok) continue;

            ReplicaForgetArtifacts(h);
            ReplicaEvictLog($"удалено «{name}» {Bytes(size)} {filesNote} ({ageNote})", OrphanTag);

            gone.Add(h);
            state.Remove(h);
            changed = true;
            done++;
        }

        if (changed)
        {
            try { lock (_replicaOrphansLock) JsonStore.Write(ReplicaOrphansPath, state); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] replica orphans save: " + ex.Message); }
        }

        if (dry && done > 0)
            Console.WriteLine($"[QbitDownload] replica: 🔸 зеркало в dry-run — удалил бы {done} шт. Журнал: {ReplicaEvictLogPath}. Боевой режим: replicaMirrorDryRun=false");

        return (done, pending, gone);
    }
}
