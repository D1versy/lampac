using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// ── Доноры охоты на реплике (qdl 2.116.1) ────────────────────────────────────────
//
// Боевой повод — 11.09.2026. Серия 11 «Изгнанного… тяжёлого рыцаря» пришла дома донором охоты
// (чужая раздача, из которой качается ОДИН файл) в 11:01; дом лёг в 14:48 и в 15:53, клиенты
// штатно ушли на tv2 — а там у сериала 10 серий: манифест доноров не отдавал вовсе, а лента
// уведомлений (она зеркало дома) при этом честно показывала «вышла новая серия 11».
//
// Как устроено. Дом ничего нового не хранит: у пункта torrents[] манифеста появляется поле
// donors — те же записи, что в watch.json (hash, score, quality, eps[]), плюс размер нужных
// файлов и признак приватности из qBit (ReplicaDonorsSnapshot). Реплика:
//   • кладёт снимок в /qdl-data/replica-donors.json — им EpisodesJson и ProgressFilesFor
//     подменяют watch.json, которого здесь нет (WatchItemFor, прецедент — noti-live.json);
//   • качает донора так же, как дом: .torrent через /qdl/replica/torrent, в ДОНОРСКОЙ категории,
//     остановленным, всё filePrio=0 кроме нужных серий, старт (ReplicaDonorInstall);
//   • снимает донора, когда дом его снял или основная раздача ушла с реплики — через то же
//     подтверждение по тикам и минутам, что у сирот, и тем же safe-путём QbitDeleteDonorSafe.
//
// 🔴 Инварианты:
//   1. Доноры в torrents[] манифеста НЕ входят (бюджет считал бы их самостоятельными раздачами),
//      в known — входят (иначе зеркало приняло бы их за сирот). Их байты в бюджете идут
//      приложением к своей основной (ReplicaItem.planSize).
//   2. donorsOk:false (или поля нет — старый дом) = «про доноров ничего не знаю»: ни добора, ни
//      снятия, ни перезаписи снимка в этот тик. Пустой список при ok:true — честное «доноров нет».
//   3. Строка серии появляется у зрителя только когда файл донора реально лежит в qBit реплики
//      (MergeEpisodeFiles: dfiles == null → пропуск) — показ не опережает закачку.
//   4. ReconcileDonors на реплике не работает: там доноров ведёт манифест, а не watch.json.
//   5. Приватный донор и донор без сторонних сидов идут в счётчик «мост ждёт», как основные.
//   6. Добавляем ОСТАНОВЛЕННЫМ и только потом ставим приоритеты: у свежедобавленной раздачи все
//      файлы «нужны», и без остановки реплика успела бы начать качать весь сезон.

public partial class QbitController
{
    static string ReplicaDonorsPath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "replica-donors.json");
    static string ReplicaDonorOrphansPath => Path.Combine(ModInit.conf.cachePath, "replica-donor-orphans.json");
    static readonly object _replicaDonorsLock = new object();
    const string DonorTagLog = "донор";

    internal sealed class ReplicaDonor
    {
        public string hash;
        public string name;
        public bool priv;
        public int numComplete;
        public long size;          // размер ВЫБРАННЫХ файлов у дома = нужные серии
        public JObject rec;        // запись как в watch.json: hash/score/quality/eps — её читает MergeEpisodeFiles

        public List<int> Wanted => (rec?["eps"] as JArray ?? new JArray()).OfType<JObject>()
            .Select(e => e.Value<int?>("fileIndex") ?? -1).Where(i => i >= 0).Distinct().ToList();

        public string EpKeys => string.Join(",", (rec?["eps"] as JArray ?? new JArray()).OfType<JObject>()
            .Select(e => e.Value<string>("epkey")).Where(k => !string.IsNullOrEmpty(k)));
    }

    #region дом: снимок доноров для манифеста

    /// <summary>
    /// Карта «основная → доноры» для манифеста. ok=false → в манифест уйдёт donorsOk:false, и реплика
    /// в этот тик доноров не трогает. Донор без живой записи в qBit дома в карту не попадает: .torrent
    /// для него дом всё равно не отдаст, а реплика такого снимет как снятого домом.
    /// </summary>
    static async Task<(Dictionary<string, JArray> map, bool ok)> ReplicaDonorsSnapshot()
    {
        var map = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 🔴 Строже LoadWatch: тот молча отдаёт пустой список на битом файле, и «доноров нет»
            // уехало бы реплике как факт. Нет файла — тоже не факт (как в ReconcileDonors).
            if (!System.IO.File.Exists(WatchFile)) return (map, false);
            JArray watch;
            lock (_watchLock) watch = JArray.Parse(System.IO.File.ReadAllText(WatchFile));

            var wanted = new Dictionary<string, (string main, JObject rec)>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in watch.OfType<JObject>())
            {
                string mh = (w.Value<string>("hash") ?? "").ToLowerInvariant();
                if (!ValidHash(mh) || w["donors"] is not JArray ds) continue;
                foreach (var d in ds.OfType<JObject>())
                {
                    string dh = (d.Value<string>("hash") ?? "").ToLowerInvariant();
                    if (!ValidHash(dh) || dh == mh) continue;
                    var eps = new JArray();
                    foreach (var e in (d["eps"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        if (e.Value<string>("status") == "replaced") continue;   // файл донора дома уже удалён
                        if ((e.Value<int?>("fileIndex") ?? -1) < 0) continue;
                        eps.Add(new JObject
                        {
                            ["epkey"] = e.Value<string>("epkey"),
                            ["season"] = e.Value<int?>("season") ?? -1,
                            ["ep"] = e.Value<int?>("ep") ?? -1,
                            ["fileIndex"] = e.Value<int?>("fileIndex") ?? -1,
                            ["status"] = e.Value<string>("status") ?? "hunted"
                        });
                    }
                    if (eps.Count == 0) continue;   // качать нечего — дом сам снимет донора ближайшим проходом
                    wanted[dh] = (mh, new JObject
                    {
                        ["hash"] = dh,
                        ["score"] = d.Value<double?>("score") ?? -1,
                        ["quality"] = d.Value<int?>("quality") ?? 0,
                        ["eps"] = eps
                    });
                }
            }
            if (wanted.Count == 0) return (map, true);

            using var c = await Qbit();
            var raw = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(DonorCategory)}"));
            foreach (var t in raw)
            {
                string h = (t.Value<string>("hash") ?? "").ToLowerInvariant();
                if (!wanted.TryGetValue(h, out var w)) continue;
                var rec = w.rec;
                rec["name"] = t.Value<string>("name");
                rec["size"] = t.Value<long?>("size") ?? 0;
                // 🔴 fail-closed, как у основных: пока метаданных нет, qBit отдаёт null — читаем как «приватная»
                rec["private"] = t.Value<bool?>("private") ?? true;
                rec["numComplete"] = t.Value<int?>("num_complete") ?? -1;
                if (!map.TryGetValue(w.main, out var arr)) map[w.main] = arr = new JArray();
                arr.Add(rec);
            }
            return (map, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica manifest: доноры: " + ex.Message);
            return (map, false);
        }
    }

    #endregion

    #region реплика: разбор манифеста и снимок для читателей

    /// <summary>Доноры пункта torrents[] манифеста. Старый дом поля не отдаёт → пусто.</summary>
    internal static List<ReplicaDonor> ReplicaParseDonors(JToken item)
    {
        var res = new List<ReplicaDonor>();
        if (item?["donors"] is not JArray arr) return res;
        foreach (var d in arr.OfType<JObject>())
        {
            string h = (d.Value<string>("hash") ?? "").ToLowerInvariant();
            if (!ValidHash(h)) continue;
            var eps = new JArray();
            foreach (var e in (d["eps"] as JArray ?? new JArray()).OfType<JObject>())
                if (e.Value<string>("status") != "replaced" && (e.Value<int?>("fileIndex") ?? -1) >= 0) eps.Add(e.DeepClone());
            if (eps.Count == 0) continue;
            res.Add(new ReplicaDonor
            {
                hash = h,
                name = d.Value<string>("name") ?? h,
                priv = d.Value<bool?>("private") ?? true,
                numComplete = d.Value<int?>("numComplete") ?? -1,
                size = d.Value<long?>("size") ?? 0,
                rec = new JObject
                {
                    ["hash"] = h,
                    ["score"] = d.Value<double?>("score") ?? -1,
                    ["quality"] = d.Value<int?>("quality") ?? 0,
                    ["eps"] = eps
                }
            });
        }
        return res;
    }

    /// <summary>
    /// Снимок «основная → доноры» для читателей реплики (EpisodesJson, ProgressFilesFor).
    /// Пишется только при donorsOk и только если изменился: JsonStore.WriteNow кладёт на диск сразу.
    /// </summary>
    static void ReplicaDonorsSave(IEnumerable<ReplicaItem> all)
    {
        var j = new JObject();
        foreach (var it in all)
        {
            if (it?.donors == null || it.donors.Count == 0) continue;
            j[it.hash] = new JArray(it.donors.Select(d => (object)d.rec.DeepClone()).ToArray());
        }
        lock (_replicaDonorsLock)
        {
            var cur = JsonStore.ReadObject(ReplicaDonorsPath);
            if (cur != null && JToken.DeepEquals(cur, j)) return;
            JsonStore.WriteNow(ReplicaDonorsPath, j);
        }
    }

    /// <summary>
    /// Watch-запись сериала по хешу основной: дома — из watch.json, на реплике — синтетическая
    /// {hash, donors} из привезённого снимка (там ни слежения, ни преемников нет по построению).
    /// Дома можно передать уже прочитанный watch, чтобы не перечитывать файл на каждый хеш группы.
    /// </summary>
    static JObject WatchItemFor(string hash, JArray watch = null)
    {
        if (!ValidHash(hash)) return null;
        if (ReplicaMode)
        {
            JObject snap;
            lock (_replicaDonorsLock) snap = JsonStore.ReadObject(ReplicaDonorsPath);
            if (snap?.GetValue(hash, StringComparison.OrdinalIgnoreCase) is not JArray ds || ds.Count == 0) return null;
            return new JObject { ["hash"] = hash, ["donors"] = ds.DeepClone() };
        }
        if (watch == null) lock (_watchLock) watch = LoadWatch();
        return watch.OfType<JObject>().FirstOrDefault(x => hash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region реплика: тик — добор, доводка, снятие

    /// <summary>
    /// Шаг тика репликации. Возвращает строку для сводки (null — доноров не было и делать было нечего)
    /// и число доноров, которым нужен мост (приватные / без сторонних сидов).
    /// </summary>
    static async Task<(string note, int bridgePending)> ReplicaDonorsSync(
        string main, JObject manifest, List<ReplicaItem> all, HashSet<string> targetSet,
        Dictionary<string, JObject> mine, Dictionary<string, JObject> myDonors, bool allowRotate)
    {
        if (ModInit.conf?.replicaDonors != true) return (null, 0);

        // 🔴 Нет поля (старый дом) или false (дом не смог прочитать watch.json / категорию) —
        // ничего не знаем, ничего не делаем. Снимок для читателей тоже не трогаем.
        if (manifest?.Value<bool?>("donorsOk") != true)
        {
            if (manifest?["donorsOk"] != null) Console.WriteLine("[QbitDownload] replica: дом не отдал доноров (donorsOk=false) — доноры в этот тик не трогаем");
            return (null, 0);
        }
        if (myDonors == null) return ("доноры: своя категория не прочитана", 0);

        ReplicaDonorsSave(all);

        // Что должно быть у нас: доноры основных, которые здесь ЕСТЬ. Добор — только для основных
        // из плана; «упомянут домом у основной, которая здесь есть» — защита от снятия.
        var wanted = new Dictionary<string, (ReplicaItem main, ReplicaDonor d)>(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestMains = new HashSet<string>(all.Select(x => x.hash), StringComparer.OrdinalIgnoreCase);
        foreach (var it in all)
        {
            if (it.donors == null || !mine.ContainsKey(it.hash)) continue;
            foreach (var d in it.donors)
            {
                referenced.Add(d.hash);
                if (targetSet.Contains(it.hash)) wanted[d.hash] = (it, d);
            }
        }

        if (wanted.Count == 0 && myDonors.Count == 0) return (null, 0);

        int added = 0, healed = 0, bridge = 0, deleted = 0, pending = 0;
        string lowDisk = null;
        using (var c = await Qbit())
        {
            foreach (var kv in wanted)
            {
                string dh = kv.Key;
                var (it, d) = kv.Value;

                // Коллизия «донор и есть чья-то основная у нас» — ничего не делаем: это чужая
                // загрузка в категории lampa, и трогать её приоритеты нельзя.
                if (mine.ContainsKey(dh)) continue;

                if (myDonors.TryGetValue(dh, out var have))
                {
                    if (await ReplicaDonorHeal(c, dh, have, d.Wanted)) healed++;
                    continue;
                }

                if (ShouldBridge(d.priv, d.numComplete)) { bridge++; continue; }

                if (!ReplicaHasRoomFor(d.size))
                {
                    Console.WriteLine($"[QbitDownload] replica: нет места под донора «{d.name}» ({Bytes(d.size)}) — добор доноров остановлен до следующего тика");
                    lowDisk = "мало места";
                    break;
                }

                if (await ReplicaAddDonor(main, c, it, d)) added++;
            }

            // Снятие — под тем же fail-safe и тем же выключателем, что зеркало удалений: это
            // тоже «дома этого больше нет», а не бюджет.
            if (allowRotate && ModInit.conf.replicaMirrorDeletes)
            {
                var cands = ReplicaDonorOrphans(myDonors.Keys, referenced, manifestMains);
                (deleted, pending) = await ReplicaDonorCleanup(c, cands, myDonors);
            }
        }

        var parts = new List<string>();
        if (added > 0) parts.Add("+" + added);
        if (healed > 0) parts.Add("доведено " + healed);
        if (bridge > 0) parts.Add("мост " + bridge);
        if (deleted > 0) parts.Add("−" + deleted);
        if (pending > 0) parts.Add("ждут " + pending);
        if (lowDisk != null) parts.Add(lowDisk);
        parts.Add("всего " + myDonors.Count);
        return ("доноры: " + string.Join(", ", parts), bridge);
    }

    /// <summary>
    /// Кандидаты на снятие: наш донор, которого дом больше не упоминает у основной, которая у нас есть.
    /// Хеш, ставший у дома ОСНОВНОЙ, не кандидат — его повышает добор (PromoteDonorToMain), а не снятие.
    /// Чистая функция ради тестов: перепутать «не в плане» с «дома нет» здесь так же дорого, как у сирот.
    /// </summary>
    internal static List<string> ReplicaDonorOrphans(IEnumerable<string> myDonors, HashSet<string> referenced, HashSet<string> manifestMains)
    {
        var res = new List<string>();
        foreach (string h in myDonors ?? Array.Empty<string>())
        {
            if (!ValidHash(h)) continue;
            if (referenced != null && referenced.Contains(h)) continue;
            if (manifestMains != null && manifestMains.Contains(h)) continue;
            res.Add(h.ToLowerInvariant());
        }
        return res;
    }

    static async Task<bool> ReplicaAddDonor(string main, HttpClient c, ReplicaItem it, ReplicaDonor d)
    {
        try
        {
            var bytes = await ReplicaFetchTorrent(main, d.hash);
            if (bytes == null) return false;

            // 🔴 Остановленным: у свежедобавленной раздачи «нужны» все файлы, и без стопа qBit
            // успел бы начать качать весь сезон до того, как мы выставим приоритеты.
            var (outcome, status) = await ReplicaUploadTorrent(c, bytes, d.hash, DonorCategory, DonorTag, stopped: true);
            if (outcome == QbitAddStatus.Failed)
            {
                Console.WriteLine($"[QbitDownload] replica: донор «{d.name}» — ОШИБКА добавления (http {status})");
                return false;
            }

            bool installed = await ReplicaDonorInstall(c, d.hash, d.Wanted);
            Console.WriteLine($"[QbitDownload] replica: + донор «{d.name}» ({Bytes(d.size)}) для «{it.name}» — "
                + (installed ? "поставлен в закачку (серии " + d.EpKeys + ")" : "добавлен остановленным, приоритеты — в следующий тик"));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica donor add " + d.hash + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Приоритеты как у дома (EpisodeHunter: всё выключить, нужные серии включить) и старт.
    /// false = список файлов не пришёл или нужных индексов в раздаче нет; торрент остаётся остановленным.
    /// </summary>
    static async Task<bool> ReplicaDonorInstall(HttpClient c, string hash, List<int> wanted)
    {
        var files = await QbitWaitFiles(c, hash, 15);
        if (files == null || files.Count == 0)
        {
            Console.WriteLine("[QbitDownload] replica: у донора " + hash + " нет списка файлов — приоритеты в следующий тик");
            return false;
        }
        var all = files.Select(f => f.Value<int?>("index") ?? -1).Where(i => i >= 0).ToList();
        var want = (wanted ?? new List<int>()).Where(all.Contains).ToList();
        if (want.Count == 0)
        {
            Console.WriteLine("[QbitDownload] replica: у донора " + hash + " нет нужных индексов файлов — остаётся остановленным");
            return false;
        }
        await QbitFilePrio(c, hash, all, 0);
        await QbitFilePrio(c, hash, want, 1);
        await QbitStartTorrent(c, hash);
        return true;
    }

    /// <summary>
    /// Доводка уже лежащего донора: добавили остановленным и упали до приоритетов; дом стал хотеть
    /// ещё одну серию с той же раздачи; или лишние файлы оказались включены. true = что-то поправили.
    /// Докачанного (progress ≥ порога) не трогаем даже остановленного — это qBit остановил его по ратио.
    /// </summary>
    static async Task<bool> ReplicaDonorHeal(HttpClient c, string hash, JObject info, List<int> wanted)
    {
        var files = await QbitFiles(c, hash);
        if (files == null || files.Count == 0) return false;

        var prio = new Dictionary<int, int>();
        foreach (var f in files.OfType<JObject>())
        {
            int idx = f.Value<int?>("index") ?? -1;
            if (idx >= 0) prio[idx] = f.Value<int?>("priority") ?? 1;
        }
        var wantSet = new HashSet<int>(wanted ?? new List<int>());
        var turnOn = wantSet.Where(i => prio.TryGetValue(i, out int p) && p == 0).ToList();
        var turnOff = prio.Where(kv => kv.Value > 0 && !wantSet.Contains(kv.Key)).Select(kv => kv.Key).ToList();

        double prog = info?.Value<double?>("progress") ?? 0;
        string st = info?.Value<string>("state") ?? "";
        bool stopped = st.StartsWith("stopped", StringComparison.OrdinalIgnoreCase) || st.StartsWith("paused", StringComparison.OrdinalIgnoreCase);
        bool needStart = stopped && prog < ProgressDone;

        if (turnOn.Count == 0 && turnOff.Count == 0 && !needStart) return false;

        if (turnOff.Count > 0) await QbitFilePrio(c, hash, turnOff, 0);
        if (turnOn.Count > 0) await QbitFilePrio(c, hash, turnOn, 1);
        if (turnOn.Count > 0 || needStart) await QbitStartTorrent(c, hash);
        Console.WriteLine($"[QbitDownload] replica: донор {hash} доведён — включено {turnOn.Count}, выключено {turnOff.Count}{(needStart ? ", запущен" : "")}");
        return true;
    }

    /// <summary>
    /// Снятие доноров, которых дом больше не держит. Подтверждение — как у сирот (тики И минуты,
    /// своё состояние replica-donor-orphans.json), «играли здесь» — отсрочка, файлы — только safe-путём.
    /// </summary>
    static async Task<(int done, int pending)> ReplicaDonorCleanup(HttpClient c, List<string> cands, Dictionary<string, JObject> myDonors)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        JObject state;
        lock (_replicaOrphansLock) state = JsonStore.ReadObject(ReplicaDonorOrphansPath) ?? new JObject();

        string NameOf(string h) => myDonors.TryGetValue(h, out var t) ? (t.Value<string>("name") ?? h) : h;
        long SizeOf(string h) => myDonors.TryGetValue(h, out var t) ? (t.Value<long?>("size") ?? 0) : 0;

        var (ready, pending) = ReplicaOrphanConfirm(
            state, cands, now,
            ModInit.conf.replicaOrphanConfirmTicks, ModInit.conf.replicaOrphanConfirmMinutes,
            out bool changed);

        foreach (string h in cands)
        {
            if (state[h] is not JObject rec) continue;
            rec["name"] = NameOf(h);
            rec["size"] = SizeOf(h);
            if ((rec.Value<int?>("misses") ?? 0) == 1)
                ReplicaEvictLog($"подтверждение 1/{Math.Max(1, ModInit.conf.replicaOrphanConfirmTicks)} донор «{NameOf(h)}» {Bytes(SizeOf(h))} — дом его больше не держит", DonorTagLog);
        }

        JObject played;
        lock (_replicaPlayedLock) played = JsonStore.ReadObject(ReplicaPlayedPath) ?? new JObject();

        long playedGrace = Math.Max(0, ModInit.conf.replicaOrphanPlayedGraceMinutes) * 60L;
        int cap = Math.Max(1, ModInit.conf.replicaMaxOrphanDeletesPerTick);
        bool dry = ModInit.conf.replicaMirrorDryRun;
        int done = 0;

        foreach (string h in ready
            .OrderBy(x => (state[x] as JObject)?.Value<long?>("since") ?? 0)
            .ThenBy(x => x, StringComparer.Ordinal))
        {
            if (done >= cap) break;
            var rec = state[h] as JObject;
            string name = NameOf(h);

            long p = ReplicaPlayedAt(played, h);
            if (playedGrace > 0 && p > 0 && now - p < playedGrace)
            {
                if (rec != null && rec.Value<string>("hold") == null)
                {
                    string why = $"играли {Math.Max(1, (now - p) / 60)} мин назад";
                    ReplicaEvictLog($"отсрочка донора «{name}»: {why}", DonorTagLog);
                    rec["hold"] = why;
                    changed = true;
                }
                continue;
            }
            if (rec != null && rec["hold"] != null) { rec.Remove("hold"); changed = true; }

            if (dry)
            {
                ReplicaEvictLog($"[dry-run] снял бы донора «{name}» {Bytes(SizeOf(h))}", DonorTagLog);
                done++;
                continue;
            }

            // С файлами ТОЛЬКО если категория донорская и папка не общая ни с одной загрузкой —
            // то же правило, что дома. Основной тут не передаём: её у нас может уже не быть.
            await QbitDeleteDonorSafe(c, h);
            if (await QbitTorrentInfo(c, h) != null)
            {
                ReplicaEvictLog($"донор «{name}» не снят (qBit не подтвердил удаление) — повтор в следующий тик", DonorTagLog);
                continue;
            }
            DropResolveCache(h);
            DropHlsCache(h);
            ReplicaEvictLog($"донор «{name}» {Bytes(SizeOf(h))} снят — дом его больше не держит", DonorTagLog);
            state.Remove(h);
            changed = true;
            done++;
        }

        if (changed)
        {
            try { lock (_replicaOrphansLock) JsonStore.Write(ReplicaDonorOrphansPath, state); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] replica donor orphans save: " + ex.Message); }
        }

        return (done, pending);
    }

    #endregion
}
