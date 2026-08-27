using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace QbitDownload;

// ── Лента уведомлений и память экрана jut.su: дом → реплика (односторонне) ──────
//
// Своих уведомлений у реплики не бывает НИ ОДНОГО: слежение за сериалами там отдаёт 403
// (Controller.cs), граберы jut/XSMART — тоже, а сканер серий ходит по watch.json, который на
// реплике пуст. Поэтому колокольчик на tv2 был пуст всегда, и лечится это только переносом.
//
// Возим два хранилища:
//
//   <cachePath>/qdl.db, таблица noti      — сама лента (SqlContext.cs)
//   <cachePath>/jut/history/<bucket>.json — что смотрели и что искали в аниме, по устройствам
//
// 🔴 ЧЕТЫРЕ ИНВАРИАНТА:
//
//  1. Поток ОДНОСТОРОННИЙ, как и у истории просмотров: ручка дома — GET за ReplicaBridgeDeny(),
//     писать в дом по-прежнему физически некуда (мост пускает только GET|HEAD).
//  2. Лента — ПОЛНОЕ ЗЕРКАЛО, а не дозаливка. Только так доезжают ретенция (NotiPrune), ручная
//     очистка и снятые дома отметки «прочитано»: дозаливка по курсору привезла бы новые строки,
//     но навсегда сохранила бы удалённые дома.
//  3. Флаг read переносится ТОЛЬКО ВВЕРХ (NotiReadMerge). Иначе: открыл центр на tv2 → бейдж
//     погас → через пять минут приехало домашнее read=false → бейдж воскрес, и так по кругу.
//  4. Id строк остаются ДОМАШНИМИ. Лента сортируется по Id (Controller.cs), а у нативных клиентов
//     отсечка тостов qdl_noti_lastid живёт в общем для всех хостов KV — свои Id на реплике дали бы
//     переехавшему с дома клиенту либо залп повторных тостов, либо немую ленту. Отсюда вставка
//     сырым SQL: EF для ключа с генерацией значения явный Id вставляет лишь по негласному
//     соглашению провайдера, а чинить это ValueGeneratedNever нельзя — модель одна на оба сервера,
//     и дома первый же db.noti.Add() без Id начал бы писать нули.
//
// Трафик экономит сигнатура ленты в манифесте (Replica.cs): пока maxId, число строк, число
// непрочитанных и штамп jut-памяти не изменились, запрос не делается вовсе. Без неё 288 тиков
// в сутки возили бы один и тот же снапшот по шейпленному мосту.

public partial class QbitController
{
    // Версия контракта — СВОЯ, независимая от манифеста. Расхождение здесь обязано пропускать
    // только ленту, а не отменять тик целиком (у манифеста ровно наоборот, и это осознанно).
    internal const int ReplicaNotiVersion = 1;

    // Сколько строк ленты возим. Клиенту показывается notiFeedLimit (50), ретенция дома держит
    // notiKeepRows (500) — 200 покрывают ленту с запасом и не превращают тик в перекачку БД.
    const int ReplicaNotiRows = 200;

    // Постеры для НОВЫХ строк добираем понемногу: лента без картинок работает (клиент рисует
    // нейтральную плитку), а мост шейплется одним ведром на процесс вместе с метой и обложками.
    const int ReplicaNotiPosterCap = 10;

    // Файл памяти экрана jut.su — это два раздела по 200 записей, то есть единицы килобайт.
    // Больше четверти мегабайта означает что-то другое, и в перенос оно не входит.
    const int JutHistMaxChars = 256 * 1024;

    #region чистые функции (их и проверяют тесты)

    /// <summary>
    /// Сигнатура состояния ленты. Меняется на всё, что делает зеркало устаревшим: новая строка
    /// (maxId), ретенция или очистка (total), «прочитано» дома (unread), правка памяти jut (штамп).
    /// </summary>
    internal static string NotiSig(long maxId, int total, int unread, string jutStamp)
        => maxId + ":" + total + ":" + unread + ":" + (jutStamp ?? "");

    /// <summary>🔴 Инвариант 3: read только вверх. Прочитанное здесь домашним нулём не воскрешается.</summary>
    internal static bool NotiReadMerge(bool home, bool local) => home || local;

    /// <summary>
    /// Нужен ли запрос ленты в этом тике. Сигнатуры нет (дом старой версии) — тянем, данные важнее
    /// трафика. Сигнатура та же — тянем только если местная лента разъехалась с тем, что мы
    /// применили в прошлый раз (том пересоздали, строки кто-то удалил): молча пустой колокольчик
    /// хуже лишнего запроса. Счётчик &lt; 0 = «посчитать не удалось», поводом для перезаливки не служит.
    /// </summary>
    internal static bool NotiPullNeeded(string remoteSig, string localSig, int localRows, int wasRows)
    {
        if (string.IsNullOrEmpty(remoteSig)) return true;
        if (!string.Equals(remoteSig, localSig, StringComparison.Ordinal)) return true;
        if (localRows < 0 || wasRows < 0) return false;
        return localRows != wasRows;
    }

    /// <summary>
    /// Разбор снапшота: строки без Id выбрасываем (без него не собрать ни порядок, ни отсечку
    /// тостов), дубли Id схлопываем, порядок — тот же, что у ленты, и кап на всякий случай свой:
    /// сколько отдал дом, реплика не решает.
    /// </summary>
    internal static List<JObject> NotiSnapshotRows(JArray items)
    {
        var res = new List<JObject>();
        if (items == null) return res;

        var seen = new HashSet<long>();
        foreach (var t in items.OfType<JObject>())
        {
            long id = t.Value<long?>("id") ?? 0;
            if (id <= 0 || !seen.Add(id)) continue;
            res.Add(t);
        }

        res.Sort((a, b) => (b.Value<long?>("id") ?? 0).CompareTo(a.Value<long?>("id") ?? 0));
        if (res.Count > ReplicaNotiRows) res.RemoveRange(ReplicaNotiRows, res.Count - ReplicaNotiRows);
        return res;
    }

    /// <summary>
    /// Имя бакета пришло ПО СЕТИ и идёт в имя файла на диске. Санатор уже есть и проверен
    /// (JutHistoryBucket), поэтому здесь не второй санатор, а требование совпадения со своей
    /// санацией: «../../init.conf» ей не равно и будет отвергнуто с записью в лог.
    /// </summary>
    internal static bool JutBucketAcceptable(string bucket)
        => !string.IsNullOrEmpty(bucket) && string.Equals(bucket, JutHistoryBucket(bucket), StringComparison.Ordinal);

    /// <summary>
    /// Метка свежести памяти устройства. Берётся из поля at ВНУТРИ файла, а не из mtime — по той
    /// же причине, что и в прунинге устройств: на зеркале JsonStore mtime источником истины не является.
    /// </summary>
    internal static DateTime JutAtUtc(JObject jo)
    {
        try
        {
            var v = jo?["at"];
            if (v == null || v.Type == JTokenType.Null) return DateTime.MinValue;
            if (v.Type == JTokenType.Date) return v.Value<DateTime>().ToUniversalTime();

            return DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? d : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    #endregion

    #region роль main: посчитать сигнатуру и отдать снапшот

    /// <summary>Штамп памяти jut.su для сигнатуры: сколько устройств и насколько свежее самое свежее.</summary>
    static string JutHistStamp()
    {
        try
        {
            var files = JsonStore.List(JutHistoryDir(), "*.json");
            if (files == null || files.Length == 0) return "0";

            long max = 0;
            foreach (string f in files)
            {
                long t = JutAtUtc(JsonStore.ReadObject(f)).Ticks;
                if (t > max) max = t;
            }
            return files.Length + "/" + max;
        }
        catch { return "?"; }
    }

    /// <summary>
    /// Сигнатура для манифеста. При ошибке — null: реплика прочитает это как «сигнатуры нет» и
    /// потянет ленту, то есть отказ считалки стоит трафика, а не пустого колокольчика.
    /// </summary>
    internal static string NotiSigSafe()
    {
        try
        {
            using var db = new SqlContext();
            long maxId = db.noti.Max(x => (long?)x.Id) ?? 0;
            return NotiSig(maxId, db.noti.Count(), db.noti.Count(x => !x.read), JutHistStamp());
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] noti sig: " + ex.Message); return null; }
    }

    /// <summary>Снимок ленты и памяти экрана jut.su для реплики. Только чтение, только через мост.</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/replica/noti")]
    public ActionResult ReplicaNotiFeed()
    {
        var deny = ReplicaBridgeDeny(); if (deny != null) return deny;

        var items = new JArray();
        var jut = new JArray();
        long maxId = 0;
        int total = 0, unread = 0, skippedBig = 0;
        bool ok = false;

        try
        {
            using var db = new SqlContext();
            maxId = db.noti.Max(x => (long?)x.Id) ?? 0;
            total = db.noti.Count();
            unread = db.noti.Count(x => !x.read);

            // Штамп постера резолвим раз на хеш: строк 200, а тайтлов в них десяток — File.Exists
            // на каждую строку тут так же лишний, как и в самой ленте (см. purl в Notifications()).
            var stamp = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in db.noti.OrderByDescending(x => x.Id).Take(ReplicaNotiRows).ToList())
            {
                string h = n.hash ?? "";
                if (!stamp.TryGetValue(h, out long posterAt))
                    stamp[h] = posterAt = ValidHash(h) ? FileStampUtc(PosterPath(h)) : 0;

                items.Add(new JObject
                {
                    ["id"] = n.Id,
                    // 🔴 seriesKey и epkey отдаём сырыми, хотя КЛИЕНТУ торрентный seriesKey закрыт
                    // (гейт в JutSlugFromSeriesKey): без них на реплике не собрать ни UNIQUE-ключ
                    // строки, ни постер по живому хешу тайтла. Мост — не клиент, он за
                    // ReplicaBridgeDeny(): своя сеть, отдельный порт, только GET.
                    ["seriesKey"] = n.seriesKey,
                    ["epkey"] = n.epkey,
                    ["seriesId"] = n.seriesId,
                    ["hash"] = n.hash,
                    ["title"] = n.title,
                    ["season"] = n.season,
                    ["episode"] = n.episode,
                    ["kind"] = n.kind,
                    ["label"] = n.label,
                    // Помечаем UTC явно: в базе Kind=Unspecified, а на реплике строка ляжет обратно
                    // в ту же колонку — разъехавшийся на часовой пояс created сдвинул бы всю ленту.
                    ["created"] = DateTime.SpecifyKind(n.created, DateTimeKind.Utc).ToString("o"),
                    ["read"] = n.read,
                    // 🔴 Готовый posterUrl не передаём — его считает КАЖДЫЙ сервер по своему диску
                    // (NotiPosterUrl), иначе клиент tv2 получил бы путь к чужому файлу. Передаём
                    // штамп: 0 = постера нет и у дома, гоняться за ним незачем (для jut-строк это
                    // норма — их обложку отдаёт /qdl/jut/poster по слагу).
                    ["posterAt"] = stamp[h]
                });
            }

            ok = true;
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti: лента: " + ex.Message); }

        try
        {
            foreach (string f in JsonStore.List(JutHistoryDir(), "*.json") ?? Array.Empty<string>())
            {
                string bucket = Path.GetFileNameWithoutExtension(f);
                if (!JutBucketAcceptable(bucket)) continue;

                var jo = JsonStore.ReadObject(f);
                if (jo == null) continue;

                string body = jo.ToString(Formatting.None);
                if (body.Length > JutHistMaxChars) { skippedBig++; continue; }

                jut.Add(new JObject
                {
                    ["bucket"] = bucket,
                    ["at"] = JutAtUtc(jo).ToString("o"),
                    ["data"] = jo
                });
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti: jut-память: " + ex.Message); }

        if (skippedBig > 0)
            Console.WriteLine("[QbitDownload] replica noti: пропущено крупных файлов jut-памяти — " + skippedBig);

        // Карта «seriesKey → живой хеш тайтла»: ею NotiPosterUrl спасает строки, чей хеш увёл
        // SWITCH или перезахват (третья ветка резолва). Дома она строится из watch.json, которого
        // на реплике нет и не будет, — поэтому едет готовой.
        var live = new JObject();
        try { foreach (var kv in WatchHashBySeriesKey()) live[kv.Key] = kv.Value; }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti: живые хеши: " + ex.Message); }

        var j = new JObject
        {
            ["notiVersion"] = ReplicaNotiVersion,
            ["live"] = live,
            // 🔴 ok различает «лента пуста» и «прочитать не смог». Без этого флага сбой чтения БД
            // выглядел бы на реплике как «дом всё почистил» — и зеркало стёрлось бы по аварии.
            ["ok"] = ok,
            ["maxId"] = maxId,
            ["total"] = total,
            ["unread"] = unread,
            ["items"] = items,
            ["jut"] = jut,
            ["skippedBig"] = skippedBig
        };

        return ContentTo(j.ToString(Formatting.None), "application/json; charset=utf-8");
    }

    #endregion

    #region роль replica: применить снапшот

    /// <summary>Файл с привезённой картой «seriesKey → живой хеш» (только на реплике).</summary>
    static string NotiLivePath => Path.Combine(ModInit.conf?.cachePath ?? "/qdl-data", "noti-live.json");

    /// <summary>
    /// Карта живых хешей для резолва постеров ленты. Здесь она — привезённая копия домашней:
    /// собственного watch.json у реплики нет. Пусто = строки с уведённым хешем останутся без
    /// картинки, то есть ровно то, что было до переноса, — не авария.
    /// </summary>
    internal static Dictionary<string, string> NotiLiveHashes()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var jo = JsonStore.ReadObject(NotiLivePath);
            if (jo == null) return map;

            foreach (var p in jo.Properties())
            {
                string h = p.Value?.ToString();
                if (!string.IsNullOrEmpty(p.Name) && ValidHash(h)) map[p.Name] = h;
            }
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti live: " + ex.Message); }

        return map;
    }

    /// <summary>Сколько строк в местной ленте. -1 = посчитать не удалось (решение принимает вызывающий).</summary>
    static int NotiRowCountSafe()
    {
        try { using var db = new SqlContext(); return db.noti.Count(); }
        catch { return -1; }
    }

    /// <summary>
    /// Тянет ленту с дома и применяет у себя. Возвращает «что изменилось» для общей строки лога,
    /// null — когда трогать было нечего.
    /// </summary>
    static async Task<string> ReplicaPullNoti(string main, JObject manifest, JObject state)
    {
        if (ModInit.conf?.replicaNoti == false) return null;

        // 🔴 Сравниваем и сохраняем сигнатуру МАНИФЕСТА, а не ответа: снапшот отдаёт только то,
        // что прошло фильтры (кап на файл jut-памяти), и его собственная сигнатура законно
        // отличалась бы от манифестной — реплика тянула бы ленту каждый тик и не понимала почему.
        string sig = manifest?.Value<string>("notiSig");
        if (!NotiPullNeeded(sig, state.Value<string>("notiSig"), NotiRowCountSafe(), state.Value<int?>("notiRows") ?? -1))
            return null;

        JObject j;
        try
        {
            string raw = await _replicaHttp.GetStringAsync(main + "/qdl/replica/noti");
            j = JObject.Parse(raw);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica noti: не получена: " + ex.Message);
            return null;
        }

        int ver = j.Value<int?>("notiVersion") ?? 0;
        if (ver != ReplicaNotiVersion)
        {
            Console.WriteLine($"[QbitDownload] replica noti: версия {ver} != {ReplicaNotiVersion}, пропуск");
            return null;
        }

        // ⚠️ Fail-safe в духе манифеста (Replica.cs): пустой ответ — это авария дома, а НЕ команда
        // «сотри зеркало». Честно опустевшая лента (ok:true, total 0) при этом переносится.
        if (j.Value<bool?>("ok") != true) { Console.WriteLine("[QbitDownload] replica noti: дом ленту не прочитал, пропуск"); return null; }
        if (j["items"] is not JArray raws) { Console.WriteLine("[QbitDownload] replica noti: в ответе нет ленты, пропуск"); return null; }

        int homeTotal = j.Value<int?>("total") ?? 0;
        if (raws.Count == 0 && homeTotal > 0)
        {
            Console.WriteLine("[QbitDownload] replica noti: дом заявил " + homeTotal + " строк и не прислал ни одной — пропуск");
            return null;
        }

        var rows = NotiSnapshotRows(raws);
        var (n, fresh, unread, prevMaxId) = ApplyNoti(rows);
        int jn = ApplyJutHistory(j["jut"] as JArray);

        // Карту живых хешей кладём ДО того, как клиент придёт за лентой: без неё строки с уведённым
        // хешем нарисуются плиткой, а обновится картинка только со следующей сменой сигнатуры.
        if (j["live"] is JObject live) { try { JsonStore.WriteNow(NotiLivePath, live); } catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti live: " + ex.Message); } }

        // Курсор двигаем только после того, как строки реально легли: иначе одна неудачная запись
        // означала бы «зеркало актуально» до следующей смены сигнатуры дома.
        if (n == rows.Count)
        {
            state["notiSig"] = sig;
            state["notiRows"] = n;
        }

        if (fresh > 0)
        {
            PushNotiSignal(unread);                       // бейдж на tv2 не ждёт 90-секундного опроса
            await ReplicaPullNotiPosters(main, NotiPostersWanted(rows, prevMaxId, h => FileStampUtc(PosterPath(h)), ReplicaNotiPosterCap));
        }

        if (n == 0 && jn == 0) return null;
        return "уведомления: " + n + (fresh > 0 ? " (новых " + fresh + ")" : "") + (jn > 0 ? ", jut-память: " + jn : "");
    }

    /// <summary>
    /// Замена ленты целиком в одной транзакции.
    ///
    /// 🔴 Только полная замена, дозаливка «новых по Id» невозможна: у noti есть UNIQUE
    /// (seriesKey, epkey) (SqlContext.cs), и та же пара ключей рано или поздно приедет под другим
    /// Id (дом перевыдал строку) — вставка упала бы. Снапшот же пришёл из таблицы, которая этому
    /// индексу уже удовлетворяет. Всё в ОДНОЙ транзакции: упавшая на середине заливка оставила бы
    /// пустую ленту, а сигнатуру мы бы не двинули и повторили в следующем тике — но до него
    /// колокольчик молчал бы.
    ///
    /// Возвращает (строк, новых, непрочитанных, прежний максимум Id) — последнее нужно, чтобы
    /// понять, за какими постерами идти.
    /// </summary>
    internal static (int rows, int fresh, int unread, long prevMaxId) ApplyNoti(List<JObject> rows)
    {
        if (rows == null) return (0, 0, 0, 0);

        int n = 0, fresh = 0, unread = 0;
        long prevMax = 0;
        try
        {
            // 🔴 Соединение берём У EF, а не открываем своё по пути файла: qdl.db — НАША база, и
            // строка подключения к ней должна быть ровно одна (SqlContext). Второе соединение с
            // Cache=Shared вдобавок ловило бы блокировки от собственного же контекста в этом
            // процессе. Дальше — сырой ADO: нужен полный контроль над параметрами (явный Id, NULL).
            using var ef = new SqlContext();
            var db = (Microsoft.Data.Sqlite.SqliteConnection)ef.Database.GetDbConnection();
            if (db.State != System.Data.ConnectionState.Open) db.Open();
            if (!TableExists(db, "noti")) return (0, 0, 0, 0);

            // Местные отметки «прочитано» и верхняя граница — ДО замены таблицы.
            var localRead = new HashSet<long>();
            using (var sel = db.CreateCommand())
            {
                sel.CommandText = "select Id, read from noti";
                using var r = sel.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    if (id > prevMax) prevMax = id;
                    if (!r.IsDBNull(1) && r.GetInt64(1) != 0) localRead.Add(id);
                }
            }

            using var tx = db.BeginTransaction();

            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "delete from noti";
                del.ExecuteNonQuery();
            }

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "insert into noti (Id, seriesKey, seriesId, hash, title, season, episode, kind, epkey, label, created, read)" +
                              " values ($id,$sk,$si,$h,$t,$s,$e,$k,$ek,$l,$c,$r)";

            foreach (var row in rows)
            {
                long id = row.Value<long?>("id") ?? 0;
                if (id <= 0) continue;

                bool read = NotiReadMerge(row.Value<bool?>("read") ?? false, localRead.Contains(id));
                string hash = row.Value<string>("hash");

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$sk", (object)row.Value<string>("seriesKey") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$si", row.Value<int?>("seriesId") ?? 0);
                cmd.Parameters.AddWithValue("$h", (object)hash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", (object)row.Value<string>("title") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", row.Value<int?>("season") ?? -1);
                cmd.Parameters.AddWithValue("$e", row.Value<int?>("episode") ?? -1);
                cmd.Parameters.AddWithValue("$k", (object)row.Value<string>("kind") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ek", (object)row.Value<string>("epkey") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$l", (object)row.Value<string>("label") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", NotiCreatedUtc(row.Value<string>("created")));
                cmd.Parameters.AddWithValue("$r", read ? 1 : 0);
                cmd.ExecuteNonQuery();

                n++;
                if (!read) unread++;
                if (id > prevMax) fresh++;
            }

            tx.Commit();
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti apply: " + ex.Message); return (0, 0, 0, prevMax); }

        return (n, fresh, unread, prevMax);
    }

    /// <summary>
    /// За какими постерами идти после применения снапшота. Только новые строки (Id больше прежнего
    /// максимума), только те, у кого постер ЕСТЬ У ДОМА (posterAt &gt; 0), и только если местный файл
    /// старее. 🔴 Не «все, у кого нет файла»: у половины ленты постера нет и дома — NotiPosterUrl
    /// штатно отдаёт null, а jut-строки берут обложку по слагу, — и мы бились бы в 404 каждый раз,
    /// когда лента меняется.
    /// </summary>
    internal static List<string> NotiPostersWanted(List<JObject> rows, long sinceId, Func<string, long> localStamp, int cap)
    {
        var res = new List<string>();
        if (rows == null) return res;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (res.Count >= cap) break;
            if ((r.Value<long?>("id") ?? 0) <= sinceId) continue;

            long at = r.Value<long?>("posterAt") ?? 0;
            if (at <= 0) continue;

            string h = r.Value<string>("hash");
            if (!ValidHash(h) || !seen.Add(h)) continue;
            if ((localStamp?.Invoke(h) ?? 0) >= at) continue;

            res.Add(h);
        }

        return res;
    }

    /// <summary>
    /// created приходит строкой ISO-8601. Формат в базе задаёт EF (yyyy-MM-dd HH:mm:ss.FFFFFFF),
    /// и Microsoft.Data.Sqlite пишет DateTime ровно так же — поэтому параметр именно DateTime, а
    /// не строка: собранная руками строка разъехалась бы с чтением при первой же смене культуры.
    /// </summary>
    static DateTime NotiCreatedUtc(string iso)
    {
        if (!string.IsNullOrEmpty(iso) &&
            DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                              DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return d;

        return DateTime.UtcNow;
    }

    /// <summary>Память экрана jut.su: побеждает более свежее at, как у блобов истории — mtime едет с содержимым.</summary>
    internal static int ApplyJutHistory(JArray rows)
    {
        if (rows == null || rows.Count == 0) return 0;

        int n = 0;
        foreach (var row in rows.OfType<JObject>())
        {
            try
            {
                string bucket = row.Value<string>("bucket");
                if (!JutBucketAcceptable(bucket))
                {
                    Console.WriteLine("[QbitDownload] replica noti: отвергнут бакет jut-памяти «" + bucket + "»");
                    continue;
                }

                if (row["data"] is not JObject data) continue;

                string path = JutHistoryPath(bucket);
                var local = JsonStore.ReadObject(path);
                if (local != null && JutAtUtc(row) <= JutAtUtc(local)) continue;

                // WriteNow, а не Write: файл кладётся впервые, а прунинг устройств считает их по
                // дисковому листингу — отложенная на 200 мс запись систематически терялась бы.
                JsonStore.WriteNow(path, data);
                n++;
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti jut: " + ex.Message); }
        }

        // Листинг каталога кешируется — без сброса /qdl/jut/recent отдавал бы пустоту до рестарта.
        if (n > 0) JsonStore.ForgetDir(JutHistoryDir());
        return n;
    }

    /// <summary>Качает отобранные постеры. Что не отдалось — приедет следующей сменой ленты.</summary>
    static async Task ReplicaPullNotiPosters(string main, List<string> hashes)
    {
        if (hashes == null || hashes.Count == 0) return;

        foreach (string h in hashes)
        {
            try { await ReplicaPullPoster(main, h); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] replica noti poster " + h + ": " + ex.Message); }
        }
    }

    #endregion
}
