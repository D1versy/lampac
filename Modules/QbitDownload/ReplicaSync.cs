using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// ── Цикл репликации (роль "replica") ────────────────────────────────────────────
//
// Тик: манифест дома → отбор по бюджету → добор недостающего → мета/постеры → ротация.
// Всё, что тут делается, делается ТОЛЬКО у себя: дом опрашивается исключительно GET-ами.
//
// Порядок шагов не случаен:
//   1) сначала fail-safe по манифесту — иначе авария дома читается как «всё удалили»;
//   2) добор ДО ротации — свободное место проверяется отдельно, а удалять ради ещё не
//      начатой закачки нельзя: связь может отвалиться, и мы останемся без обоих;
//   3) ротация последней и только выше верхней ватерлинии.

public partial class QbitController
{
    static int _replicaTicking;
    static readonly HttpClient _replicaHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    const long GiB = 1024L * 1024 * 1024;

    static string ReplicaStatePath => Path.Combine(ModInit.conf.cachePath, "replica-state.json");
    static string ReplicaPlayedPath => Path.Combine(ModInit.conf.cachePath, "replica-played.json");
    static readonly object _replicaPlayedLock = new object();

    // Подтверждение пропажи хеша у дома: {hash: {since, misses, name, size}}. Отдельный файл, а
    // не поле в replica-state.json: состояние тика перезаписывается целиком каждые пять минут,
    // а здесь копится история наблюдений, терять которую нельзя — на ней держится окно.
    static string ReplicaOrphansPath => Path.Combine(ModInit.conf.cachePath, "replica-orphans.json");
    static readonly object _replicaOrphansLock = new object();

    // ── шейпер моста: ОДИН на процесс ───────────────────────────────────────────
    // Личный лимит на поток дал бы кратную скорость при параллельных докачках и съел бы
    // домашний аплинк ровно тогда, когда владелец считает, что ограничил его пятью МБ/с.
    sealed class TokenBucket
    {
        readonly object _lock = new object();
        double _tokens, _rate, _cap;
        DateTime _last = DateTime.UtcNow;

        public TokenBucket(double bytesPerSec) { _rate = _cap = Math.Max(1024, bytesPerSec); _tokens = _cap; }

        public void SetRate(double bytesPerSec)
        {
            lock (_lock) { _rate = _cap = Math.Max(1024, bytesPerSec); if (_tokens > _cap) _tokens = _cap; }
        }

        public async Task ConsumeAsync(long bytes, CancellationToken ct)
        {
            while (bytes > 0)
            {
                int wait;
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    _tokens = Math.Min(_cap, _tokens + (now - _last).TotalSeconds * _rate);
                    _last = now;

                    if (_tokens >= 1)
                    {
                        double take = Math.Min(_tokens, bytes);
                        _tokens -= take;
                        bytes -= (long)take;
                    }
                    wait = bytes > 0 ? (int)Math.Min(1000, Math.Max(10, 1000.0 * Math.Min(bytes, _cap) / _rate)) : 0;
                }
                if (wait > 0) await Task.Delay(wait, ct);
            }
        }
    }

    static TokenBucket _bridgeBucket;
    static TokenBucket BridgeBucket
    {
        get
        {
            double rate = Math.Max(1, ModInit.conf?.replicaBridgeMBps ?? 5) * 1024.0 * 1024.0;
            if (_bridgeBucket == null) _bridgeBucket = new TokenBucket(rate);
            else _bridgeBucket.SetRate(rate);
            return _bridgeBucket;
        }
    }

    // ── элемент плана ───────────────────────────────────────────────────────────
    internal sealed class ReplicaItem
    {
        public string hash;
        public string name;
        public string slug;          // не пусто → аниме jut.su (локальная карточка, не торрент)
        public long size;
        public long added;
        public long activity;
        public bool priv;
        public int numComplete;
        public long metaAt;
        public long posterAt;
        public bool IsJut => !string.IsNullOrEmpty(slug);
    }

    /// <summary>
    /// Отметка «это играли здесь». Ротация обязана её уважать: домашний activity про местный
    /// просмотр ничего не знает, а выбросить фильм из-под зрителя — худшее, что может сделать
    /// бекап-сервер. Пишется из /qdl/stream. С qdl 2.115 — и дома: жнец преемника (Successor.cs)
    /// по ней откладывает снятие старой раздачи, которую только что смотрели.
    /// </summary>
    static readonly ConcurrentDictionary<string, long> _playedMem = new(StringComparer.OrdinalIgnoreCase);

    internal static void ReplicaTouchPlayed(string hash)
    {
        if (!ValidHash(hash)) return;
        try
        {
            // Дома /qdl/stream — горячий путь (Range-seek'и очередями): троттлим в памяти,
            // до JsonStore доходит один раз в 5 минут на хеш.
            long nowMem = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_playedMem.TryGetValue(hash, out long lastMem) && lastMem > nowMem - 300) return;
            _playedMem[hash] = nowMem;
            lock (_replicaPlayedLock)
            {
                var j = JsonStore.ReadObject(ReplicaPlayedPath) ?? new JObject();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // монотонно: перемотки внутри сеанса не должны дёргать диск каждым Range
                if ((j.Value<long?>(hash) ?? 0) > now - 300) return;
                j[hash] = now;
                JsonStore.Write(ReplicaPlayedPath, j);
            }
        }
        catch { }
    }

    static long ReplicaPlayedAt(JObject played, string hash)
        => played?.Value<long?>(hash) ?? 0;

    // ── тик ─────────────────────────────────────────────────────────────────────
    internal static async Task ReplicaTick()
    {
        if (!ReplicaMode) return;

        // skip-if-busy: опоздавший тик просто пропадает до следующего периода. Тик может идти
        // минутами (мост), и очередь из тиков тут никому не нужна.
        if (Interlocked.Exchange(ref _replicaTicking, 1) == 1) return;
        try
        {
            await ReplicaTickCore();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica tick: " + ex);
            HealthState.Fail(HealthState.Ids.Replica, "тик репликации упал: " + ex.Message);
        }
        finally { Interlocked.Exchange(ref _replicaTicking, 0); }
    }

    static async Task ReplicaTickCore()
    {
        string main = (ModInit.conf?.replicaMainUrl ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(main))
        {
            HealthState.Fail(HealthState.Ids.Replica, "не задан replicaMainUrl");
            return;
        }

        // ── 1. манифест ──────────────────────────────────────────────────────────
        JObject manifest;
        try
        {
            string raw = await _replicaHttp.GetStringAsync(main + "/qdl/replica/manifest");
            manifest = JObject.Parse(raw);
        }
        catch (Exception ex)
        {
            HealthState.Fail(HealthState.Ids.Replica, "дом недоступен: " + ex.Message);
            Console.WriteLine("[QbitDownload] replica: манифест не получен: " + ex.Message);
            return;   // дом молчит — это НЕ повод что-то удалять
        }

        int ver = manifest.Value<int?>("manifestVersion") ?? 0;
        if (ver != ReplicaManifestVersion)
        {
            // Молчаливое расхождение версий = неверная ротация. Лучше встать и кричать.
            HealthState.Fail(HealthState.Ids.Replica, $"версия манифеста {ver}, ожидается {ReplicaManifestVersion} — обновите дом или реплику");
            Console.WriteLine($"[QbitDownload] replica: ⚠️ версия манифеста {ver} != {ReplicaManifestVersion}, тик отменён");
            return;
        }

        // ── настройка фильтра каталога приезжает из дома (qdl 2.90) ──────────────
        // Пишем ТОЛЬКО при расхождении: JsonStore.WriteNow кладёт на диск сразу, а CubProxy
        // перечитывает файл по mtime — лишняя запись каждые пять минут дёргала бы его впустую.
        // Поле необязательное: старый дом его не отдаёт, и тогда своё значение не трогаем.
        try
        {
            var cf = manifest["catalogFilter"] as JObject;
            if (cf != null)
            {
                bool en = cf.Value<bool?>("enabled") ?? false;
                int my = cf.Value<int?>("movieYear") ?? CatalogFilter.DefMovieYear;
                int ty = cf.Value<int?>("tvYear") ?? CatalogFilter.DefTvYear;

                var cur = CatalogFilter.Load();
                if (cur.Value<bool?>("enabled") != en || cur.Value<int?>("movieYear") != my || cur.Value<int?>("tvYear") != ty)
                {
                    CatalogFilter.Save(en, my, ty);
                    Console.WriteLine($"[QbitDownload] replica: фильтр каталога из дома — enabled={en}, кино≥{my}, сериалы≥{ty}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica: фильтр каталога: " + ex.Message); }

        var sources = manifest["sources"] as JObject;
        bool qbitOk = (sources?.Value<string>("qbit") ?? "fail") == "ok";
        bool localOk = (sources?.Value<string>("local") ?? "fail") == "ok";

        var state = JsonStore.ReadObject(ReplicaStatePath) ?? new JObject();

        // ── 2. fail-safe: можно ли вообще удалять в этот тик ─────────────────────
        bool allowRotate = qbitOk && localOk;
        string rotateBlockedWhy = allowRotate ? null : "источники манифеста: qbit=" + (qbitOk ? "ok" : "fail") + ", local=" + (localOk ? "ok" : "fail");

        int cntTorrents = manifest["counts"]?.Value<int?>("torrents") ?? 0;
        int cntLocal = manifest["counts"]?.Value<int?>("local") ?? 0;
        int cntNow = cntTorrents + cntLocal;
        int cntWas = state.Value<int?>("lastGoodCount") ?? 0;

        int shrinkPct = Math.Max(1, ModInit.conf.replicaShrinkGuardPercent);
        if (allowRotate && cntWas > 0 && cntNow < cntWas * (100 - shrinkPct) / 100.0)
        {
            allowRotate = false;
            rotateBlockedWhy = $"манифест съёжился {cntWas} → {cntNow} (порог {shrinkPct}%)";
            Console.WriteLine("[QbitDownload] replica: ⚠️ " + rotateBlockedWhy + " — ротация в этом тике отключена");
            HealthState.Degraded(HealthState.Ids.Replica, rotateBlockedWhy);
        }

        // ── 3. план: что вообще должно лежать на реплике ─────────────────────────
        var all = new List<ReplicaItem>();
        foreach (var t in (manifest["torrents"] as JArray) ?? new JArray())
        {
            string h = (t.Value<string>("hash") ?? "").ToLowerInvariant();
            if (!ValidHash(h)) continue;
            all.Add(new ReplicaItem
            {
                hash = h,
                name = t.Value<string>("name"),
                size = t.Value<long?>("size") ?? 0,
                added = t.Value<long?>("added") ?? 0,
                activity = t.Value<long?>("activity") ?? 0,
                priv = t.Value<bool?>("private") ?? true,
                numComplete = t.Value<int?>("numComplete") ?? -1,
                metaAt = t.Value<long?>("metaAt") ?? 0,
                posterAt = t.Value<long?>("posterAt") ?? 0
            });
        }
        foreach (var l in (manifest["local"] as JArray) ?? new JArray())
        {
            string h = (l.Value<string>("hash") ?? "").ToLowerInvariant();
            if (!ValidHash(h)) continue;
            all.Add(new ReplicaItem
            {
                hash = h,
                name = l.Value<string>("name"),
                slug = l.Value<string>("slug"),
                size = l.Value<long?>("size") ?? 0,
                added = l.Value<long?>("added") ?? 0,
                activity = l.Value<long?>("activity") ?? 0,
                metaAt = l.Value<long?>("metaAt") ?? 0,
                posterAt = l.Value<long?>("posterAt") ?? 0
            });
        }

        // ── 3б. множество «что вообще есть у дома» ───────────────────────────────
        // 🔴 Это НЕ то же самое, что torrents+local: там «что реплицировать», и живые
        // транскод-карточки, доноры охоты и jut-карточки с отвалившимся маунтом туда намеренно
        // не попадают. Зеркалирование удалений обязано опираться только на known.
        var homeKnown = ReplicaKnownSet(manifest["known"] as JArray, all.Select(x => x.hash), out string knownWhy);

        // Молчим, если зеркалирование и так выключено: отсутствие known тогда не новость.
        if (knownWhy != null && !ModInit.conf.replicaMirrorDeletes) knownWhy = null;
        if (knownWhy != null) Console.WriteLine("[QbitDownload] replica: ⚠️ " + knownWhy);

        long budget = Math.Max(1, ModInit.conf.replicaBudgetGb) * GiB;
        long highMark = budget * Math.Clamp(ModInit.conf.replicaHighWatermark, 20, 100) / 100;

        var target = ReplicaPlan(all, budget, ModInit.conf.replicaLowWatermark, ModInit.conf.replicaMaxItemPercent);
        long planned = target.Sum(x => x.size);
        var targetSet = new HashSet<string>(target.Select(x => x.hash), StringComparer.OrdinalIgnoreCase);

        // ── 4. что уже есть у нас ────────────────────────────────────────────────
        var mine = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var c = await Qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}");
            foreach (var t in JArray.Parse(raw))
            {
                string h = (t.Value<string>("hash") ?? "").ToLowerInvariant();
                if (ValidHash(h)) mine[h] = (JObject)t;
            }
        }
        catch (Exception ex)
        {
            // Свой qBit мёртв — ни добирать, ни удалять нельзя (удалять особенно: состав
            // локальных раздач неизвестен).
            HealthState.Fail(HealthState.Ids.Replica, "свой qBittorrent недоступен: " + ex.Message);
            Console.WriteLine("[QbitDownload] replica: свой qBit недоступен: " + ex.Message);
            return;
        }

        var myLocal = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string localDir = Path.Combine(ModInit.conf.cachePath, "local");
            foreach (var lf in JsonStore.List(localDir, "*.json"))
            {
                string h = Path.GetFileNameWithoutExtension(lf);
                if (!ValidHash(h)) continue;
                var loc = LoadLocal(h);
                if (loc != null && !LocalIsOverlay(loc)) myLocal[h] = loc;
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica: локальные маркеры: " + ex.Message); }

        // ── 5. добор недостающего ────────────────────────────────────────────────
        int added = 0, bridgePending = 0;
        string lowDiskWhy = null;
        foreach (var it in target)
        {
            if (mine.ContainsKey(it.hash) || myLocal.ContainsKey(it.hash)) continue;

            if (it.IsJut || ShouldBridge(it))
            {
                // Мост (аниме, приватные раздачи и те, у кого единственный источник — дом).
                // Этап 2; сейчас считаем и показываем, чтобы отставание было видно, а не молчало.
                bridgePending++;
                continue;
            }

            // Предполётная проверка места. В модуле до сих пор не было ни одного DriveInfo:
            // на реплике переполнение диска роняет и qBit, и SQLite на том же томе.
            if (!ReplicaHasRoomFor(it.size))
            {
                Console.WriteLine($"[QbitDownload] replica: нет места под «{it.name}» ({Bytes(it.size)}) — добор остановлен до следующего тика");
                lowDiskWhy = "мало места на диске, добор приостановлен";
                break;
            }

            if (await ReplicaAddFromSwarm(main, it)) added++;
        }

        // ── 6. мета и постеры (килобайты, идут вперёд контента) ──────────────────
        int metaSynced = 0, postersSynced = 0;
        foreach (var it in target)
        {
            try
            {
                if (it.metaAt > 0 && FileStampUtc(MetaPath(it.hash)) < it.metaAt)
                    if (await ReplicaPullMeta(main, it.hash)) metaSynced++;

                if (it.posterAt > 0 && FileStampUtc(PosterPath(it.hash)) < it.posterAt)
                    if (await ReplicaPullPoster(main, it.hash)) postersSynced++;

                // Порядок «Загрузок» на реплике должен совпадать с домашним. ActivityTouch
                // монотонен, поэтому местный просмотр домашним штампом не затирается.
                if (it.activity > 0) ActivityTouch(it.hash, it.activity);
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] replica meta/poster " + it.hash + ": " + ex.Message); }
        }

        // ── 7. засев рядов прогрева ──────────────────────────────────────────────
        int rowsSeeded = ReplicaSeedRows(manifest, state);

        // ── 7б. история просмотров ───────────────────────────────────────────────
        // Отдельным запросом и ПОСЛЕ карточек: «Продолжить» без карточки бессмысленно,
        // а карточка без «Продолжить» работает.
        string historyNote = await ReplicaPullHistory(main);

        // ── 7в. лента уведомлений и память экрана jut.su ─────────────────────────
        // После истории и по той же причине: своих событий у реплики нет ни одного, лента здесь —
        // зеркало дома. Запрос делается, только если сигнатура в манифесте изменилась.
        string notiNote = await ReplicaPullNoti(main, manifest, state);

        // ── 8. удаления ──────────────────────────────────────────────────────────
        // Оба класса под одним fail-safe: манифест получен, версия сошлась, оба источника ok,
        // набор не съёжился. Зеркало идёт ПЕРВЫМ — освобождённое им место часто уводит
        // занятость под верхнюю ватерлинию, и бюджетный проход не запускается вовсе.
        int evicted = 0, mirrored = 0, orphansPending = 0;
        string brakeWhy = null;
        if (allowRotate)
        {
            var (m, pend, gone, brake) = await ReplicaMirrorDeletes(mine, myLocal, homeKnown);
            mirrored = m;
            orphansPending = pend;
            brakeWhy = brake;

            evicted = await ReplicaRotate(mine, myLocal, targetSet, highMark, gone);
        }
        else
            Console.WriteLine("[QbitDownload] replica: удаления пропущены — " + rotateBlockedWhy);

        // ── 9. состояние и наблюдаемость ─────────────────────────────────────────
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (qbitOk && localOk) state["lastGoodCount"] = cntNow;
        state["lastManifestAt"] = now;
        state["planned"] = planned;
        state["bridgePending"] = bridgePending;
        state["rowsSeeded"] = (state.Value<int?>("rowsSeeded") ?? 0) + rowsSeeded;
        state["orphansPending"] = orphansPending;
        state["orphansDeleted"] = (state.Value<int?>("orphansDeleted") ?? 0) + mirrored;
        state["mirror"] = !ModInit.conf.replicaMirrorDeletes ? "выключено"
            : homeKnown == null ? "нет known"
            : ModInit.conf.replicaMirrorDryRun ? "dry-run" : "боевой";
        if (mirrored > 0) state["lastOrphanAt"] = now;
        JsonStore.Write(ReplicaStatePath, state);

        string mirrorNote = !ModInit.conf.replicaMirrorDeletes ? ""
            : homeKnown == null ? "зеркало: нет known; "
            : $"зеркало −{mirrored}, ждут {orphansPending}; ";

        string summary = $"план {target.Count} шт / {Bytes(planned)}; добавлено {added}; мост ждёт {bridgePending}; мета {metaSynced}, постеры {postersSynced}; {mirrorNote}вычищено {evicted}"
            + (historyNote != null ? "; " + historyNote : "")
            + (notiNote != null ? "; " + notiNote : "");
        Console.WriteLine("[QbitDownload] replica: " + summary);

        // 🔴 Вердикт здоровья ставится РОВНО ЗДЕСЬ и больше нигде. Раньше его писали три места
        // вразнобой, и это давало вечно висящее предупреждение: HealthState.Ok() фиксирует успех,
        // но флаг деградации НЕ снимает (для этого есть OkDirect). Тормоз массовости однажды
        // поставил Degraded — и строка «Репликация» осталась жёлтой навсегда, хотя тики давно
        // проходили штатно. Обратная ошибка так же реальна: успешный тик не должен гасить чужое
        // предупреждение (мало места), поэтому порядок веток — от самого серьёзного к мелкому.
        string degradeWhy = !allowRotate ? rotateBlockedWhy
            : knownWhy ?? brakeWhy ?? lowDiskWhy;

        if (degradeWhy != null) HealthState.Degraded(HealthState.Ids.Replica, degradeWhy);
        else HealthState.OkDirect(HealthState.Ids.Replica);
    }

    /// <summary>
    /// Отбор: что вообще должно лежать на реплике. Вынесено отдельной чистой функцией — это
    /// самая тонкая часть цикла (ватерлиния, кап на элемент, skip-and-continue), и проверяться
    /// она обязана тестами, а не наблюдением за диском удалённой машины.
    /// </summary>
    internal static List<ReplicaItem> ReplicaPlan(IEnumerable<ReplicaItem> all, long budget, int lowPct, int maxItemPct)
    {
        long lowMark = budget * Math.Clamp(lowPct, 10, 99) / 100;
        long maxItem = budget * Math.Clamp(maxItemPct, 5, 100) / 100;

        var target = new List<ReplicaItem>();
        long planned = 0;

        foreach (var it in all.OrderByDescending(x => x.activity).ThenByDescending(x => x.added))
        {
            if (it == null || it.size <= 0) continue;

            // Элемент крупнее капа пропускаем НАВСЕГДА, а не останавливаем набор: жадный обход
            // на 300-ГБ ремуксе сверху не набрал бы вообще ничего, а без капа один такой файл
            // вытеснил бы всю библиотеку.
            if (it.size > maxItem) continue;

            // skip-and-continue: не влез — пробуем следующий, остаток добьётся мелкими.
            // Разрыв между ватерлиниями (85→95) гасит дребезг на границе.
            if (planned + it.size > lowMark) continue;

            target.Add(it);
            planned += it.size;
        }

        return target;
    }

    /// <summary>
    /// Качать по мосту, а не из swarm? Приватные — всегда (passkey внутри .torrent, второй
    /// адрес = риск бана). Плюс раздачи без сторонних сидов: их «swarm» это и есть домашний
    /// аплинк, и шейпер моста обошли бы стороной.
    /// ⚠️ numComplete &lt; 0 = трекер не ответил, состав swarm неизвестен. Отправлять всё
    /// неизвестное в мост нельзя — он узкий; идём в swarm, как раньше.
    /// </summary>
    internal static bool ShouldBridge(ReplicaItem it)
    {
        if (it.priv) return true;
        if (!ModInit.conf.replicaBridgeWhenOnlyHomeSeeds) return false;
        return it.numComplete >= 0 && it.numComplete <= 1;
    }

    static bool ReplicaHasRoomFor(long size)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(ModInit.conf.downloadsPath));
            var di = new DriveInfo(string.IsNullOrEmpty(root) ? "/" : root);
            long reserve = Math.Max(1, ModInit.conf.replicaFreeReserveGb) * GiB;
            return di.AvailableFreeSpace >= size + reserve;
        }
        catch { return true; }   // не смогли измерить — не блокируем работу
    }

    static async Task<bool> ReplicaAddFromSwarm(string main, ReplicaItem it)
    {
        try
        {
            var r = await _replicaHttp.GetAsync(main + "/qdl/replica/torrent?hash=" + HttpUtility.UrlEncode(it.hash));
            if (!r.IsSuccessStatusCode)
            {
                Console.WriteLine($"[QbitDownload] replica: .torrent для {it.hash} не отдан ({(int)r.StatusCode})");
                return false;
            }

            var bytes = await r.Content.ReadAsByteArrayAsync();
            if (bytes == null || bytes.Length == 0) return false;
            await BridgeBucket.ConsumeAsync(bytes.Length, CancellationToken.None);

            using var c = await Qbit();
            var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
            content.Add(file, "torrents", it.hash + ".torrent");
            content.Add(new StringContent(ModInit.conf.downloadsPath), "savepath");
            content.Add(new StringContent(ModInit.conf.category), "category");
            // Лимиты отдачи новой раздачи; историю решения см. в ModuleConf.replicaSeedUpLimitKBps
            // (до 25.08.2026 реплика не сидировала вовсе — 1 КБ/с и остановка на финише).
            // ⚠️ Ноль в upLimit у qBit означает «без ограничения», а не «не отдавать» —
            // поэтому «не ограничивать» и передаётся нулём. Общий потолок ставится не тут,
            // а в самом qBittorrent реплики, иначе лимит был бы на каждую раздачу отдельно.
            int upKBps = ModInit.conf.replicaSeedUpLimitKBps;
            content.Add(new StringContent((upKBps > 0 ? upKBps * 1024 : 0).ToString()), "upLimit");
            content.Add(new StringContent(ModInit.conf.replicaSeedRatioLimit
                .ToString(System.Globalization.CultureInfo.InvariantCulture)), "ratioLimit");

            var add = await c.PostAsync("/api/v2/torrents/add", content);

            // 🔴 Не IsSuccessStatusCode: qBit на провал отвечает 200 с телом «Fails.», и
            // прежний код печатал «поставлено в закачку», молча ретраясь каждые пять минут.
            var outcome = QbitAddOutcome((int)add.StatusCode, await add.Content.ReadAsStringAsync());
            bool ok = outcome != QbitAddStatus.Failed;

            Console.WriteLine($"[QbitDownload] replica: + «{it.name}» ({Bytes(it.size)}) — "
                + (ok ? (outcome == QbitAddStatus.Duplicate ? "уже был в qBit" : "поставлено в закачку")
                      : $"ОШИБКА добавления (http {(int)add.StatusCode})"));
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica add " + it.hash + ": " + ex.Message);
            return false;
        }
    }

    static async Task<bool> ReplicaPullMeta(string main, string hash)
    {
        var r = await _replicaHttp.GetAsync(main + "/qdl/replica/meta?hash=" + HttpUtility.UrlEncode(hash));
        if (!r.IsSuccessStatusCode) return false;

        string body = await r.Content.ReadAsStringAsync();
        await BridgeBucket.ConsumeAsync(body.Length, CancellationToken.None);

        var meta = JObject.Parse(body);
        SaveMeta(hash, meta);   // сам сбросит горячий слой и кеш списка
        return true;
    }

    static async Task<bool> ReplicaPullPoster(string main, string hash)
    {
        var r = await _replicaHttp.GetAsync(main + "/qdl/poster?hash=" + HttpUtility.UrlEncode(hash));
        if (!r.IsSuccessStatusCode) return false;

        var bytes = await r.Content.ReadAsByteArrayAsync();
        if (bytes == null || bytes.Length < 64) return false;
        await BridgeBucket.ConsumeAsync(bytes.Length, CancellationToken.None);

        string path = PosterPath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // .part → rename: постер, пойманный листингом на середине записи, дал бы битую обложку
        string tmp = path + ".part";
        await System.IO.File.WriteAllBytesAsync(tmp, bytes);
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
        System.IO.File.Move(tmp, path);

        PosterWritten();   // §BV: без этого has_poster врёт до рестарта
        return true;
    }

    /// <summary>
    /// Засев рядов прогрева. Делается ОДИН раз (и повторно — только если дом прислал больше):
    /// дальше CatalogWarmup реплики живёт своей жизнью и учится на своих клиентах.
    /// </summary>
    static int ReplicaSeedRows(JObject manifest, JObject state)
    {
        try
        {
            string warm = (ModInit.conf?.replicaWarmHost ?? "").Trim();
            if (string.IsNullOrEmpty(warm)) return 0;

            var rows = (manifest["rows"] as JArray)?.Select(x => (string)x).Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (rows == null || rows.Count == 0) return 0;

            if ((state.Value<int?>("rowsSeededCount") ?? 0) >= rows.Count) return 0;

            Uri u;
            try { u = new Uri(warm.Contains("://") ? warm : "https://" + warm); }
            catch { Console.WriteLine("[QbitDownload] replica: replicaWarmHost не разобран: " + warm); return 0; }

            int n = CatalogWarmup.ImportRowPaths(rows, u.Scheme, u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port);
            state["rowsSeededCount"] = rows.Count;
            if (n > 0) Console.WriteLine($"[QbitDownload] replica: засеяно рядов прогрева: {n} (host {u.Host})");
            return n;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica rows: " + ex.Message); return 0; }
    }

    static string Bytes(long b)
    {
        if (b >= GiB) return Math.Round(b / (double)GiB, 1) + " ГБ";
        if (b >= 1024 * 1024) return Math.Round(b / 1048576.0, 1) + " МБ";
        return b + " Б";
    }
}
