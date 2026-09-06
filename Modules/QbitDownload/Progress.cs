using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// ───────────────────────────────────────────────────────────────────────────────
// Живой прогресс загрузок (qdl 2.93) — ручка /qdl/progress.
//
// Жалоба владельца: «проценты загрузки на клиенте не обновляются» и «когда видео докачалось,
// клиент об этом ещё не знает и вылетает „смотреть всё равно“ — но только если идут активные
// загрузки на данный момент».
//
// 🔴 Почему НЕ поллинг /qdl/list. Тот ответ весит 110 КБ сырых / ~45 КБ br при 65 загрузках
// (замер в шапке HttpCache.cs), кешируется 30 с и стоит 0.58-1.03 с пересборки. Поллить его
// раз в 5 с — это 22 КБ/с на устройство и возврат пересборки на горячий путь открытия карточки.
// ⚠️ Дорогая часть List() — это НЕ qBittorrent, а LoadMeta/HasPoster/File.Exists на каждый
// элемент (Controller.cs:892-897, 917-930), склейка сезонов и SeasonWaitMap. Здесь нет ничего
// из этого, поэтому ручка стоит единицы миллисекунд. Не «оптимизировать» её обратно в /qdl/list.
//
// Контракты, на которых стоит клиент (qdl.js, поллер pgSubscribe/pgGet/pgFile):
//
//   1. `items` содержит ТОЛЬКО недокачанное. Отсутствие хеша при ok:true = «готово».
//      Именно это делает докачку наблюдаемой без диффов и без «а вдруг просто не приехало».
//      Обратная сторона: удалённая раздача тоже читается как «готово» — это fail-open, то есть
//      сегодняшнее поведение, и оно допустимо (гейт обязан ошибаться в сторону «пустить»).
//
//   2. `ok:false` — это НЕ «всё скачано». Единственная развилка fail-open/fail-closed во всём
//      изменении: клиент по ok:false не делает выводов вообще и держит прежний вердикт.
//      Сюда попадают лёгший qBittorrent, киллсвитч и сервер-реплика (там qBit нет вовсе).
//
//   3. В теле НЕТ времени. `stamp` — хеш полезной нагрузки, а не now: стенные часы в теле
//      убили бы ETag (у стоящей раздачи ответ обязан быть побайтово тем же, иначе 304 не
//      случится никогда, и весь смысл ревалидации пропадёт).
//
// Киллсвитчи на лету (init.conf, секция QbitDownload): progressPollSeconds=0 гасит ручку и
// клиентский таймер целиком; partialPlayBlock=false снимает жёсткую блокировку недокачанного.
// ───────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region порог «докачано»
    /// <summary>
    /// Порог готовности файла/раздачи. 🔴 ОДИН на весь модуль: тот же 0.999 стоит в
    /// MergeEpisodeFiles (донор не нужен), MergeGroupEpisodes (выбор копии) и в гейте
    /// транскода. Ставить здесь 1.0 нельзя: взвешенный прогресс полностью скачанной группы
    /// сезонов на double даёт значение чуть МЕНЬШЕ единицы (SeriesMerge.cs, деление weighted/size),
    /// и «дождитесь загрузки» вылезло бы на готовом сериале. Равенство порогов проверяет
    /// ProgressTests — он сканирует весь модуль и краснеет на любом близком, но ДРУГОМ литерале.
    /// </summary>
    internal const double ProgressDone = 0.999;
    #endregion

    #region классификация состояний qBittorrent
    // ⚠️ Имена состояний между qBit 4.x и 5.x разъехались (paused* → stopped*), и в форке это
    // уже видно: QbitStartTorrent пробует start, затем resume (EpisodeHunter.cs). Поэтому знаем
    // ОБА набора, а неизвестное состояние трактуем как «стоит» — недобор здесь означал бы
    // молча запертую карточку, а перебор всего лишь лишний тик опроса.
    static readonly HashSet<string> _stMoving = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloading", "forcedDL", "metaDL", "forcedMetaDL", "allocating",
        "checkingDL", "checkingResumeData", "moving"
    };

    // Стоит «сейчас», но живое: раздача в очереди или без пиров. qBit ставит stalledDL и когда
    // прямо в эту секунду никто не отдаёт, поэтому ненулевая скорость переводит её в активные.
    static readonly HashSet<string> _stIdle = new(StringComparer.OrdinalIgnoreCase)
    {
        "stalledDL", "queuedDL"
    };

    static bool StateMoving(string state, long dlspeed)
    {
        if (state != null && _stMoving.Contains(state)) return true;
        return dlspeed > 0 && state != null && _stIdle.Contains(state);
    }
    #endregion

    #region снапшоты (коалесинг нескольких устройств)
    // Два независимых снимка. TTL короткий намеренно: N устройств с опросом раз в 5 с и
    // случайной фазой схлопываются примерно в один поход к qBit, а отставание никогда не
    // превышает TTL — незаметно рядом с интервалом опроса.
    // ⚠️ Это ОСОЗНАННО не listCacheSeconds (30 с): там длинный TTL прячет ~1 с пересборки,
    // здесь пересборки нет, и вся ценность ручки — в свежести.
    static readonly object _progLock = new();
    static List<JObject> _progInfo;          // torrents/info обеих категорий
    static DateTime _progInfoAt;
    static readonly Dictionary<string, (JArray files, DateTime at)> _progFiles = new(StringComparer.OrdinalIgnoreCase);

    static int SnapshotSec => Math.Max(0, ModInit.conf?.progressSnapshotSeconds ?? 2);
    static int PollSec => Math.Max(0, ModInit.conf?.progressPollSeconds ?? 5);
    static int IdlePollSec => Math.Max(0, ModInit.conf?.progressIdlePollSeconds ?? 30);
    static int IdleBudgetMin => Math.Max(0, ModInit.conf?.progressIdleBudgetMinutes ?? 10);
    static bool PartialBlock => ModInit.conf?.partialPlayBlock ?? true;

    /// <summary>Сбросить снимки (мутации списка, тесты).</summary>
    internal static void DropProgressCache()
    {
        lock (_progLock) { _progInfo = null; _progFiles.Clear(); }
    }

    /// <summary>Блок настроек поллера для клиента — уезжает в /qdl/features (Perms.cs).</summary>
    internal static JObject ProgressClientConf() => new JObject
    {
        ["poll"] = PollSec,
        ["idle"] = IdlePollSec,
        ["budget"] = IdleBudgetMin,
        ["block"] = PartialBlock
    };

    /// <summary>
    /// torrents/info основной и донорской категорий. null = qBittorrent не ответил.
    /// 🔴 Без filter=downloading: семантика бакетов менялась между версиями qBit, а недобор
    /// здесь = запертая карточка. Ответ идёт по локалхосту и парсится за единицы мс.
    /// ⚠️ Доноры в своём try: их категории может не быть вовсе, и это не повод терять сводку.
    /// </summary>
    static async Task<List<JObject>> ProgressInfo()
    {
        int ttl = SnapshotSec;
        if (ttl > 0)
        {
            lock (_progLock)
                if (_progInfo != null && (DateTime.UtcNow - _progInfoAt).TotalSeconds < ttl)
                    return _progInfo;
        }

        var all = new List<JObject>();
        using var c = await Qbit();

        string raw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}");
        all.AddRange(JArray.Parse(raw).OfType<JObject>());

        // Доноры охоты в гриде не видны, но их докачка разблокирует серию на экране серий —
        // значит они обязаны попадать в active, иначе опрос замолчит посреди работы.
        try
        {
            string draw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(DonorCategory)}");
            all.AddRange(JArray.Parse(draw).OfType<JObject>());
        }
        catch (Exception dex) { Console.WriteLine("[QbitDownload] progress donors: " + dex.Message); }

        if (ttl > 0)
            lock (_progLock) { _progInfo = all; _progInfoAt = DateTime.UtcNow; }
        return all;
    }

    /// <summary>Файлы раздачи со снимком. null = qBit не ответил по этому хешу.</summary>
    static async Task<JArray> ProgressFiles(HttpClient c, string hash)
    {
        int ttl = SnapshotSec;
        if (ttl > 0)
        {
            lock (_progLock)
                if (_progFiles.TryGetValue(hash, out var e) && (DateTime.UtcNow - e.at).TotalSeconds < ttl)
                    return e.files;
        }

        var files = await QbitFiles(c, hash);
        if (files != null && ttl > 0)
            lock (_progLock)
            {
                _progFiles[hash] = (files, DateTime.UtcNow);
                // выметание: без него словарь растёт на каждый открытый сериал за всю сессию
                if (_progFiles.Count > 64)
                    foreach (var k in _progFiles.Where(kv => (DateTime.UtcNow - kv.Value.at).TotalSeconds > 30).Select(kv => kv.Key).ToList())
                        _progFiles.Remove(k);
            }
        return files;
    }
    #endregion

    #region /qdl/progress — лёгкий живой прогресс
    /// <summary>
    /// Без hash — сводка по НЕдокачанным раздачам обеих категорий.
    /// С hash — плюс per-file прогресс этой раздачи, её сиблингов по группе сезонов и её доноров
    /// (ровно то множество, которое обходит EpisodesJson — иначе на экране серий часть строк
    /// осталась бы без живых данных и залипла на снимке /qdl/episodes).
    ///
    /// ⚠️ torrents/files в СВОДНОЙ форме не зовём никогда: при 65 загрузках это 65 обращений
    /// к qBit на каждый тик каждого устройства.
    /// </summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/progress")]
    async public Task<ActionResult> Progress(string hash = null)
    {
        int poll = PollSec;

        // Киллсвитч: ручка отвечает мгновенно и НЕ трогает qBittorrent. ok:false — клиент
        // таймер не заводит и вердикт гейтов не меняет (то есть падает на данные /qdl/list).
        if (poll <= 0)
            return ProgressBody(false, 0, 0, 0, new JArray(), null);

        if (hash != null && !ValidHash(hash))
            return BadRequest(new { error = "invalid hash" });

        try
        {
            var info = await ProgressInfo();

            var items = new JArray();
            int active = 0, pending = 0;

            foreach (var t in info)
            {
                double p = t.Value<double?>("progress") ?? 0;
                if (p >= ProgressDone) continue;   // готовое в items не попадает — это контракт

                string h = t.Value<string>("hash") ?? "";
                if (h.Length == 0) continue;
                string state = t.Value<string>("state");
                long speed = t.Value<long?>("dlspeed") ?? 0;

                if (StateMoving(state, speed)) active++; else pending++;

                items.Add(new JObject
                {
                    ["h"] = h,
                    ["p"] = Math.Round(Math.Clamp(p, 0, 1), 4),   // 4 знака: клиент рисует целые проценты, а лишний джиттер double дёргал бы ETag
                    ["s"] = state
                });
            }

            // qdl 2.114: закачки XSMART/jut «в полёте» — под своим псевдо-infohash, тем же контрактом
            // (в items только недокачанное; «качается» → active, «в очереди/застряло» → pending —
            // от этого зависит, будет ли клиент опрашивать раз в 5 с). Внутри того же try: при
            // лёгшем qBit ответ обязан остаться ok:false, иначе недокачанные торренты прочитались
            // бы как готовые. Прогресс единицы там капается на 99 % до ремукса и маркера (XsmartInflight).
            foreach (var inf in XsmartInflight().Concat(JutInflight()))
            {
                double ip = inf.Value<double?>("p") ?? 0;
                if (ip >= ProgressDone) continue;
                string ih = inf.Value<string>("hash");
                if (string.IsNullOrEmpty(ih)) continue;
                string istate = inf.Value<string>("state") ?? "queued";
                if (istate == "downloading") active++; else pending++;
                items.Add(new JObject { ["h"] = ih, ["p"] = Math.Round(Math.Clamp(ip, 0, 1), 4), ["s"] = istate });
            }

            JObject files = null;
            if (hash != null)
                files = await ProgressFilesFor(hash);

            return ProgressBody(true, poll, active, pending, items, files);
        }
        catch (Exception ex)
        {
            // qBittorrent лёг / сервер-реплика (там его нет вовсе). 200 с ok:false, а не 500:
            // клиент обязан отличить «не знаю» от «всё скачано», и 500 он бы просто проглотил
            // как сетевую ошибку с бэкоффом. Мягкая деградация — тот же инвариант, что у List().
            Console.WriteLine("[QbitDownload] progress: " + ex.Message);
            return ProgressBody(false, poll, 0, 0, new JArray(), null);
        }
    }

    /// <summary>Per-file прогресс раздачи + её сиблингов по группе + её доноров.</summary>
    static async Task<JObject> ProgressFilesFor(string hash)
    {
        var res = new JObject();

        // Множество хешей строим ровно так же, как EpisodesJson: группа сезонов + доноры
        // КАЖДОЙ раздачи группы. Иначе строки донорских серий на экране остались бы без данных.
        var targets = new List<string>();
        var group = SeriesGroupHashes(hash);
        if (group != null) targets.AddRange(group); else targets.Add(hash);

        JArray watch;
        lock (_watchLock) watch = LoadWatch();
        foreach (var w in watch.OfType<JObject>())
        {
            string wh = w.Value<string>("hash");
            if (wh == null || !targets.Any(t => t.Equals(wh, StringComparison.OrdinalIgnoreCase))) continue;
            // преемник раздачи (Successor.cs, qdl 2.115): его строки на экране серий живут по своему хешу
            var nhp = NextHashOf(w); if (nhp != null) targets.Add(nhp);
            if (w["donors"] is not JArray ds) continue;
            foreach (var d in ds.OfType<JObject>())
            {
                string dh = d.Value<string>("hash");
                if (ValidHash(dh)) targets.Add(dh);
            }
        }

        using var c = await Qbit();
        foreach (string th in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Локальный не-оверлейный маркер (транскод, jut.su, XSMART): в qBit его нет вовсе,
            // а файлы готовы по построению. Ключа в files не будет, и клиент честно возьмёт
            // progress:1.0 из /qdl/episodes — отдельной ветки для этого не нужно.
            var loc = LoadLocal(th);
            if (loc != null && !LocalIsOverlay(loc)) continue;

            var files = await ProgressFiles(c, th);
            if (files == null) continue;

            var arr = new JArray();
            foreach (var f in files.OfType<JObject>())
            {
                int idx = f.Value<int?>("index") ?? -1;
                if (idx < 0) continue;
                arr.Add(new JArray(idx, Math.Round(Math.Clamp(f.Value<double?>("progress") ?? 0, 0, 1), 4)));
            }
            if (arr.Count > 0) res[th] = arr;
        }

        return res.Count > 0 ? res : null;
    }

    /// <summary>
    /// Сборка тела + ETag. stamp считается ПО ТЕЛУ без себя самого, поэтому не может
    /// разъехаться с содержимым, и в теле нет ни одного значения времени.
    /// </summary>
    ActionResult ProgressBody(bool ok, int poll, int active, int pending, JArray items, JObject files)
    {
        var body = new JObject
        {
            ["ok"] = ok,
            ["poll"] = poll,
            ["idle"] = IdlePollSec,
            ["budget"] = IdleBudgetMin,
            ["block"] = PartialBlock,
            ["active"] = active,
            ["pending"] = pending,
            ["items"] = items
        };
        if (files != null) body["files"] = files;

        body["stamp"] = Fnv1a.HashName(body.ToString(Newtonsoft.Json.Formatting.None));

        // ⚠️ Только JsonWithEtag: он ставит Cache-Control: no-cache («храни, но перепроверяй»).
        // SetHeadersNoCache() поставил бы no-store, который убивает саму ревалидацию и 304
        // не случится никогда (разбор — в шапке HttpCache.cs).
        return JsonWithEtag(body.ToString(Newtonsoft.Json.Formatting.None));
    }
    #endregion
}
