using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// ИСТОРИЯ ПРОСМОТРОВ ОДНОГО ПОЛЬЗОВАТЕЛЯ ДЛЯ АДМИНКИ (qdl 2.105)
//
// Клик по айди устройства в /admin/d1v открывает страницу с его историей. Здесь — сбор данных;
// роуты в Admin.cs, вёрстка в admin/history.html.
//
// 🔴 СЕРВЕР НЕ ХРАНИТ ИСТОРИЮ «НА УСТРОЙСТВО». Ключ хранения — Groups.Resolve(uid): у одиночного
// устройства это его айди, у устройства в группе — айди ГРУППЫ (Groups.cs подменяет user_uid на
// входе в /bookmark/* и /timecode/*). Поэтому страница обязана честно говорить, чью историю
// показывает, и не делать вид, что разложила её по устройствам. Единственная доступная
// атрибуция — косвенная: наш /tmdb/img штампует в ссылку постера «?uid=<кто записал>», и то
// только у карточек CUB/TMDB; у jut.su и старых карточек её нет вовсе.
//
// 🔴 ТОЛЬКО ЧТЕНИЕ. Ни одной записи: ни очистки, ни удаления пунктов. Отсюда и посадка соединения
// (OpenDbRo ниже) — она физически не даёт взять write-lock. Инвариант стережёт отдельный тест
// «ручка не меняет файлы БД» (AdminHistoryTests) — без него первая же правка тихо его сломает.
//
// Почему не общий OpenDb (ReplicaHistory.cs): тот открывает ReadWrite + Cache=Shared + Pooling.
// Сейчас обе базы в WAL (проверено на живых файлах: байты 18/19 заголовка = 2, рядом лежат
// -wal/-shm), и там читатель с писателем не конфликтуют вовсе. НО режим WAL — свойство ФАЙЛА,
// а не кода: ни одна строка форка не ставит journal_mode=WAL для Sync.sql/TimeCode.sql (ставят
// только LogUserRequest-Lite, Telemetry и наш qdl.db). На чистом томе — новый сервер, реплика
// с нуля — базы поднимутся в rollback-режиме, где читатель уже держит SHARED и способен
// притормозить коммит писателя. Read-only + приватный кеш + короткий busy_timeout делают так,
// что даже там просмотр истории в админке не может уронить клиентский /timecode/add.
// Прецедент такой посадки в репозитории — Modules/DatabaseEditor/DatabaseStore.cs.
// Замок SemaphorManager НЕ берём сознательно: на чтении он не нужен, а взятый — сериализовал бы
// страницу админки с живым плеером, который прямо сейчас пишет позицию.
//
// Замеры на боевом срезе (самый толстый ключ, блоб закладок 45 КБ):
//   select закладок 0.044 мс · select таймкодов пользователя 0.050 мс · открыть+прочитать+закрыть 0.49 мс.
// Для сравнения: /qdl/replica/history читает ОБЕ таблицы целиком каждые replicaIntervalMin=5 минут.
// Настоящая цена страницы — не SQLite, а резолв тайтлов (см. капы ниже).
// ─────────────────────────────────────────────────────────────────────────────

public partial class QbitController
{
    #region капы

    // Отдаём не больше — но ВСЕГДА говорим, сколько было всего (counts.*): молча резать нельзя.
    // Боевой максимум на сегодня — 42 карточки и 66 строк журнала, так что капы это страховка
    // от испорченного блоба, а не режим работы.
    const int AdminHistoryCardsCap = 500;
    const int AdminHistoryPlaysCap = 2000;
    const int AdminHistoryBlobMaxBytes = 2 * 1024 * 1024;

    // Сколько тайтлов разрешено дорезолвить ЧЕРЕЗ TMDB за один запрос. Локальные ступени
    // (карточки закладок → meta/*.json → jut) бесплатны и на боевых данных закрывают почти всё.
    // 🔴 Кап маленький намеренно: HistoryResolveCard на ведре без типа ("tmdb") пробует сначала
    // tv, потом movie — то есть ДО ДВУХ запросов с таймаутом 10 с на один тайтл.
    const int AdminHistoryTmdbCap = 12;

    // И общий дедлайн на всю сетевую фазу: даже 12 промахов подряд не имеют права держать
    // страницу две минуты. Вышли за него — остальное честно уходит в «не определилось».
    const int AdminHistoryNetBudgetMs = 5000;

    // XSMART считаем отдельным бюджетом: у одного боевого устройства 12 из 19 строк журнала —
    // именно XSMART, и общий кап с TMDB съедал бы их первым же тайтлом.
    // Портал отвечает 2–3 мс (контейнер кеширует карточки), поэтому кап щедрый, а таймаут злой.
    const int AdminHistoryXsmartCap = 24;
    const int AdminHistoryXsmartTimeoutMs = 2500;

    // Досмотренным клиент считает ≥ 90 % (qdl.js) — берём тот же порог, чтобы админка и
    // пользователь говорили об одном и том же.
    const int AdminHistoryDonePercent = 90;

    #endregion

    #region соединение только на чтение

    /// <summary>
    /// Открыть базу строго на чтение. Mode=ReadOnly физически не даёт взять write-lock;
    /// приватный кеш (а не общий, как у OpenDb) убирает SQLITE_LOCKED_SHAREDCACHE, который
    /// busy_timeout не лечит; Pooling=false — соединение живёт ровно один запрос и не может
    /// остаться занятым на время сетевого await.
    /// ⚠️ На несуществующем файле Mode=ReadOnly бросает. Ловим это дважды — File.Exists перед
    /// вызовом и catch внутри каждого читателя; снятие любой одной страховки поведения не меняет
    /// (проверено негативным прогоном), обеих — роняет страницу в 500.
    /// </summary>
    static SqliteConnection OpenDbRo(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        c.Open();

        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=3000";
        pragma.ExecuteNonQuery();

        return c;
    }

    #endregion

    #region чтение хранилищ

    /// <summary>Закладки ключа синка (весь объект Lampa: card/history/like/...). Нет базы — null.</summary>
    static JObject AdminHistoryBookmarks(string user)
    {
        try
        {
            if (!System.IO.File.Exists(SyncDbPath)) return null;

            using var db = OpenDbRo(SyncDbPath);
            if (!TableExists(db, "bookmarks")) return null;

            using var cmd = db.CreateCommand();
            cmd.CommandText = "select data from bookmarks where user=$u limit 1";
            cmd.Parameters.AddWithValue("$u", user);

            string raw = cmd.ExecuteScalar()?.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Блоб приезжает от клиента через /bookmark/set. Испорченный гигант не должен
            // превращать открытие админки в подвисший браузер.
            if (raw.Length > AdminHistoryBlobMaxBytes)
            {
                Console.WriteLine($"[QbitDownload] admin history: блоб {user} — {raw.Length} байт, пропущен");
                return null;
            }

            return JObject.Parse(raw);
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] admin history bookmarks: " + ex.Message); return null; }
    }

    /// <summary>Строки таймкодов ключа синка. updated — ТЕКСТ ('2026-08-16 16:36:16.17'), не DateTime.</summary>
    static List<(string card, string data, string updated)> AdminHistoryTimecodes(string user)
    {
        var rows = new List<(string, string, string)>();

        try
        {
            if (!System.IO.File.Exists(TimeCodeDbPath)) return rows;

            using var db = OpenDbRo(TimeCodeDbPath);
            if (!TableExists(db, "timecodes")) return rows;

            using var cmd = db.CreateCommand();
            cmd.CommandText = "select card, data, updated from timecodes where user=$u limit $n";
            cmd.Parameters.AddWithValue("$u", user);
            cmd.Parameters.AddWithValue("$n", AdminHistoryPlaysCap);

            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0),
                          r.IsDBNull(1) ? null : r.GetString(1),
                          r.IsDBNull(2) ? null : r.GetValue(2)?.ToString()));
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] admin history timecodes: " + ex.Message); }

        return rows;
    }

    /// <summary>
    /// Ключи вида «&lt;user&gt;_&lt;profile_id&gt;» — ОТДЕЛЬНЫЕ профили Lampa (getUserid в
    /// BookmarkController/TimeCodeController клеит суффикс, когда profile_id пришёл и не «0»).
    /// Сливать их в одну историю нельзя, поэтому только показываем, что они есть.
    /// В бою таких ключей нет ни одного, но потерять их молча — хуже, чем показать лишнюю строку.
    /// ⚠️ Фильтруем в C#, а не через LIKE: в SQLite '_' — это подстановочный символ на один знак,
    /// и «user_%» поймал бы заодно «userX…». Строк в обеих таблицах десятки — перебор дешевле трюка.
    /// </summary>
    static List<string> AdminHistoryProfileKeys(string user)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        string prefix = user + "_";

        void Probe(string path, string table, string sql)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;

                using var db = OpenDbRo(path);
                if (!TableExists(db, table)) return;

                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string u = r.IsDBNull(0) ? null : r.GetString(0);
                    if (u != null && u.StartsWith(prefix, StringComparison.Ordinal)) found.Add(u);
                }
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] admin history profiles: " + ex.Message); }
        }

        Probe(SyncDbPath, "bookmarks", "select user from bookmarks limit 500");
        Probe(TimeCodeDbPath, "timecodes", "select distinct user from timecodes limit 500");

        return found.ToList();
    }

    #endregion

    #region карточки

    static readonly Regex _adminHistoryImgUid = new(@"[?&]uid=([A-Za-z0-9\-_.]{1,64})", RegexOptions.Compiled);
    static readonly Regex _adminHistoryXsmartBucket = new(@"^qdl_xsmart:(\d{1,4}):(\d{1,12})$", RegexOptions.Compiled);
    static readonly Regex _adminHistoryPosterPath = new(@"^/[A-Za-z0-9_\-]+\.(?:jpg|png|webp)$", RegexOptions.Compiled);

    /// <summary>Кто из группы записал карточку. Штамп ставит наш /tmdb/img — значит только у CUB/TMDB.</summary>
    static string AdminHistoryWriterUid(string img)
    {
        if (string.IsNullOrEmpty(img)) return "";
        var m = _adminHistoryImgUid.Match(img);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// Ссылка постера для страницы админки — ОБЯЗАТЕЛЬНО относительная, на наш же origin.
    ///
    /// 🔴 Отдать card.img как есть нельзя. Замер по всем 20 ключам закладок: 63 ссылки уже ведут на
    /// наш LAN-хост, но 31 — на «https://tv.d1versy.com:9443/tmdb/img/…» (клиент записал карточку,
    /// сидя снаружи), а этот адрес из локалки не открывается вовсе — hairpin-NAT нет. Ещё 20
    /// карточек несут img пустым, но с живым poster_path, 5 указывают прямо на image.tmdb.org
    /// (браузер админки полез бы в интернет), и 20 — на /qdl/jut/poster, половина с прибитым хостом.
    /// Без нормализации владелец увидел бы стену битых картинок у самой большой группы.
    ///
    /// Лестница: наш /tmdb/img или image.tmdb.org → свой прокси (TmdbPosterPath заодно
    /// проверяет путь по белому списку и отрезает query вместе с uid=); постер jut.su → его
    /// корневой путь; пусто, но есть poster_path → собираем сами. Ничего — пустая строка,
    /// страница нарисует плитку-заглушку.
    /// </summary>
    static string AdminHistoryPoster(JObject card)
    {
        string img = card?.Value<string>("img") ?? "";

        string path = TmdbPosterPath(img, null);
        if (path != null) return "/tmdb/img/" + path;

        int jut = img.IndexOf("/qdl/jut/poster", StringComparison.OrdinalIgnoreCase);
        if (jut >= 0) return img.Substring(jut);

        string pp = card?.Value<string>("poster_path");
        if (!string.IsNullOrEmpty(pp) && _adminHistoryPosterPath.IsMatch(pp))
            return "/tmdb/img/t/p/w300" + pp;

        return "";
    }

    static string AdminHistoryYear(JObject c)
    {
        string d = c.Value<string>("release_date");
        if (string.IsNullOrEmpty(d)) d = c.Value<string>("first_air_date");
        return (d != null && d.Length >= 4) ? d.Substring(0, 4) : "";
    }

    /// <summary>
    /// «сериал» / «фильм» / пусто. 🔴 Пусто — не лень, а честность: слепок карточки jut.su несёт
    /// только id/title/img (HistoryJutCard), признаков типа там нет вовсе, и эвристика «не сериал
    /// ⇒ фильм» подписывала бы каждое аниме фильмом.
    /// </summary>
    static string AdminHistoryType(JObject c)
    {
        if (AdminHistoryIsTv(c)) return "tv";

        bool movie = c.Value<string>("media_type") == "movie"
                  || !string.IsNullOrEmpty(c.Value<string>("release_date"))
                  || !string.IsNullOrEmpty(c.Value<string>("original_title"));

        return movie ? "movie" : "";
    }

    static bool AdminHistoryIsTv(JObject c)
        => c.Value<string>("media_type") == "tv"
        || (c.Value<int?>("number_of_seasons") ?? 0) > 0
        || (c.Value<int?>("number_of_episodes") ?? 0) > 0
        || !string.IsNullOrEmpty(c.Value<string>("first_air_date"))
        || !string.IsNullOrEmpty(c.Value<string>("original_name"));

    static string AdminHistoryTitle(JObject c)
    {
        string t = c.Value<string>("title");
        return string.IsNullOrWhiteSpace(t) ? (c.Value<string>("name") ?? "") : t;
    }

    static string AdminHistoryOriginal(JObject c)
    {
        string t = c.Value<string>("original_title");
        return string.IsNullOrWhiteSpace(t) ? (c.Value<string>("original_name") ?? "") : t;
    }


    /// <summary>
    /// Ведро-инфохеш сводим к айди тайтла из его меты — иначе одна и та же раздача, открытая и по
    /// хешу, и по карточке, дала бы в журнале две строки. Меты нет — остаётся сам хеш.
    /// </summary>
    static string AdminHistoryHashCanon(string hash, Dictionary<string, JObject> byHash)
    {
        if (byHash.TryGetValue(hash, out var meta))
        {
            int id = meta.Value<int?>("id") ?? 0;
            if (id > 0) return id.ToString();
        }

        return "hash:" + hash;
    }

    #endregion
    #region XSMART

    // Живёт весь процесс: тайтл по айди не меняется, а страницу открывают по многу раз подряд.
    // 🔴 Только в памяти. Кеш на диске сделал бы «ручку только на чтение» неправдой.
    static readonly ConcurrentDictionary<string, (string title, string year)> _adminXsmartTitles = new();

    /// <summary>
    /// Тайтл раздела XSMART по ведру «qdl_xsmart:&lt;cat&gt;:&lt;id&gt;». ParseHistoryBucket его не
    /// разбирает вовсе (ни загрузки, ни меты у онлайн-раздела нет) — проверено: у всех 13 боевых
    /// вёдер meta/*.json отсутствует, так что офлайн-резолв дал бы ровно ноль попаданий.
    /// Спрашиваем свой же контейнер, тот самый, который и так пингует хелс-чек.
    ///
    /// 🔴 Сырой конфиг, а НЕ XsmartNet.Api: тот подставляет вшитый адрес вместо пустой строки, и на
    /// реплике (где раздел выключен профилем compose) явный киллсвитч превратился бы в запрос в
    /// никуда — с 45-секундным таймаутом клиента XsmartNet. Та же причина, что у ProbeXsmart.
    /// 🔴 Любая ошибка — null, и строка деградирует до «XSMART · 6-9147477». Лёгший портал не имеет
    /// права уронить страницу истории.
    /// </summary>
    static async Task<(string title, string year)> AdminHistoryXsmartTitle(int cat, string id)
    {
        if (!XsmartNet.Valid(cat, id)) return (null, null);

        string key = XsmartNet.Ref(cat, id);
        if (_adminXsmartTitles.TryGetValue(key, out var cached)) return cached;

        string api = ModInit.conf?.xsmartApi;
        if (string.IsNullOrWhiteSpace(api)) return (null, null);

        try
        {
            using var cts = new CancellationTokenSource(AdminHistoryXsmartTimeoutMs);
            using var resp = await _healthHttp.GetAsync($"{NoSlash(api)}/xsmart/item/{cat}/{id}", cts.Token);
            if (!resp.IsSuccessStatusCode) return (null, null);

            var o = JObject.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (o.Value<bool?>("ok") != true) return (null, null);

            var item = o["item"] as JObject;
            string title = item?.Value<string>("title");
            if (string.IsNullOrWhiteSpace(title)) return (null, null);

            int y = item.Value<int?>("year") ?? 0;
            var res = (title, y > 0 ? y.ToString() : "");

            _adminXsmartTitles[key] = res;
            return res;
        }
        catch { return (null, null); }
    }

    #endregion

    #region сборка ответа

    /// <summary>
    /// История одного пользователя для админки. null — такого айди нет в реестре и это не айди
    /// группы (контроллер отдаст 404). Ничего не пишет.
    /// </summary>
    internal static async Task<JObject> AdminHistory(string uid)
    {
        string key = Perms.NormUid(uid);
        if (key == null) return null;

        bool isGid = key.StartsWith(Groups.GidPrefix, StringComparison.Ordinal);
        if (!isGid && !Perms.Known(key)) return null;
        if (isGid && !Groups.Exists(key)) return null;

        bool groupsOn = ModInit.conf?.groupsEnabled != false;

        // ── кто это ─────────────────────────────────────────────────────────────
        var device = isGid
            ? new JObject { ["uid"] = key, ["name"] = Groups.NameOf(key), ["platform"] = "group" }
            : Perms.List().OfType<JObject>().FirstOrDefault(d => (string)d["uid"] == key)
              ?? new JObject { ["uid"] = key };

        // ── чья это история ─────────────────────────────────────────────────────
        // 🔴 Ключ берём у Groups.Resolve, а НЕ у GroupOf: Resolve уважает киллсвитч groupsEnabled
        // (выключено → возвращает айди устройства), а GroupOf его не смотрит вовсе. Разойтись они
        // могут ровно в один момент — когда группы выключили, не расформировав. Тогда сервер уже
        // пишет в личный ключ, и показать «историю группы» значило бы соврать. Показываем личную,
        // но говорим, что общая никуда не делась и лежит под g-…
        string gid = isGid ? key : Groups.GroupOf(key);
        string scopeKey = isGid ? key : Groups.Resolve(key);
        bool grouped = gid != null && string.Equals(scopeKey, gid, StringComparison.Ordinal);

        var members = new JArray();
        if (gid != null)
        {
            foreach (string m in Groups.MembersOf(gid))
            {
                var c = Perms.Card(m);
                members.Add(new JObject { ["uid"] = m, ["name"] = c["name"], ["platform"] = c["platform"] });
            }
        }

        var scope = new JObject
        {
            ["key"] = scopeKey,
            ["kind"] = grouped ? "group" : "device",
            ["gid"] = gid ?? "",
            ["groupName"] = gid != null ? Groups.NameOf(gid) : "",
            ["groupsEnabled"] = groupsOn,
            ["members"] = members,
            ["profiles"] = new JArray(AdminHistoryProfileKeys(scopeKey))
        };

        // Объединение КОПИРУЕТ историю, а не переносит (так и написано пользователю в админке),
        // поэтому у устройства в группе остаётся собственная строка «до объединения». Не показать
        // её — значит нарваться на «я же смотрел это с телефона, где оно».
        scope["personal"] = grouped ? GroupsStats(key) : null;

        // ── карточки истории ────────────────────────────────────────────────────
        var bookmarks = AdminHistoryBookmarks(scopeKey);

        // ⚠️ Айди в card[] бывают и числом, и строкой в одном массиве — сравниваем по ToString().
        var byCardId = new Dictionary<string, JObject>(StringComparer.Ordinal);
        if (bookmarks?["card"] is JArray cardArr)
        {
            foreach (var c in cardArr.OfType<JObject>())
            {
                string id = c["id"]?.ToString();
                if (!string.IsNullOrEmpty(id) && !byCardId.ContainsKey(id)) byCardId[id] = c;
            }
        }

        // Идём по history[], а НЕ по card[]: в card[] лежат ещё и закладки («буду смотреть»),
        // и попасть в историю они не должны.
        var histIds = (bookmarks?["history"] as JArray)?
            .Select(t => t?.ToString())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList() ?? new List<string>();

        var cards = new JArray();
        foreach (string id in histIds.Take(AdminHistoryCardsCap))
        {
            if (!byCardId.TryGetValue(id, out var c))
            {
                // Айди в history[] без карточки в card[]: показываем заглушку, строку не теряем —
                // иначе счётчик разойдётся с тем, что видит сам пользователь в Lampa.
                cards.Add(new JObject
                {
                    ["id"] = id, ["title"] = "", ["original"] = "", ["year"] = "",
                    ["type"] = "", ["source"] = "", ["img"] = "", ["byUid"] = "", ["byName"] = ""
                });
                continue;
            }

            string writer = AdminHistoryWriterUid(c.Value<string>("img"));

            cards.Add(new JObject
            {
                ["id"] = id,
                ["title"] = AdminHistoryTitle(c),
                ["original"] = AdminHistoryOriginal(c),
                ["year"] = AdminHistoryYear(c),
                ["type"] = AdminHistoryType(c),
                ["source"] = c.Value<string>("source") ?? "",
                ["img"] = AdminHistoryPoster(c),
                ["byUid"] = writer,
                ["byName"] = writer == "" ? "" : (string)Perms.Card(writer)["name"] ?? ""
            });
        }

        // ── журнал воспроизведения ──────────────────────────────────────────────
        // 🔴 Обе базы дочитаны и закрыты ДО первого await: OpenDbRo не пулится, но держать
        // открытое соединение на время похода в сеть всё равно незачем.
        var rows = AdminHistoryTimecodes(scopeKey);
        var plays = await AdminHistoryPlays(rows, byCardId);

        return new JObject
        {
            ["device"] = device,
            ["scope"] = scope,
            ["cards"] = cards,
            ["plays"] = plays.list,
            ["db"] = new JObject
            {
                ["sync"] = System.IO.File.Exists(SyncDbPath),
                ["timecode"] = System.IO.File.Exists(TimeCodeDbPath)
            },
            ["replica"] = ReplicaMode,
            ["counts"] = new JObject
            {
                ["history"] = histIds.Count,
                ["cardsShown"] = cards.Count,
                ["timecodes"] = rows.Count,
                ["titles"] = plays.list.Count,
                ["unresolved"] = plays.unresolved
            }
        };
    }

    /// <summary>
    /// Строки таймкодов → журнал, СХЛОПНУТЫЙ ПО ТАЙТЛУ.
    ///
    /// Номера серий сознательно не разбираем (решение владельца): item — это хеш имени файла,
    /// посчитанный клиентом, и развернуть его обратно можно только перебором файлов раздачи.
    /// Нужно «что смотрел», а не «какую серию».
    ///
    /// 🔴 Схлопывать надо по ТАЙТЛУ, а не по ведру: у одного сериала вёдер штатно два —
    /// «270603_tv» пишет полная карточка, «qdl_t270603» экран серий. По ведру каждый сериал
    /// удвоился бы.
    /// </summary>
    static async Task<(JArray list, int unresolved)> AdminHistoryPlays(
        List<(string card, string data, string updated)> rows,
        Dictionary<string, JObject> byCardId)
    {
        var agg = new Dictionary<string, JObject>(StringComparer.Ordinal);
        int unresolved = 0;

        var (byId, byHash) = HistoryMetaIndex();
        var memo = new Dictionary<string, JObject>(StringComparer.Ordinal);

        // Ступень №1 лестницы — карточки самих закладок: тот же пользователь, те же тайтлы, ноль
        // сети. Кладём их прямо в memo, которым HistoryResolveCard пользуется как кешем.
        foreach (var kv in byCardId)
        {
            var norm = HistoryNormalizeCard(kv.Value);
            if (norm == null) continue;

            if (kv.Key.StartsWith("jut:", StringComparison.Ordinal))
            {
                memo["jut:" + kv.Key.Substring(4)] = norm;
            }
            else if (long.TryParse(kv.Key, out long n) && n > 0)
            {
                memo["tv:" + kv.Key] = norm;
                memo["movie:" + kv.Key] = norm;
                memo["tmdb:" + kv.Key] = norm;
            }
        }

        int tmdbCalls = 0, xsmartCalls = 0;
        var budget = Stopwatch.StartNew();

        foreach (var row in rows)
        {
            string bucket = row.card ?? "";
            double percent = row.data == null ? 0 : RoadPercent(row.data);
            string updated = row.updated ?? "";

            string aggKey, title = null, year = "", source = "", resolvedBy = "none";
            bool resolved = false;

            var xs = _adminHistoryXsmartBucket.Match(bucket);
            if (xs.Success)
            {
                int cat = int.Parse(xs.Groups[1].Value);
                string xid = xs.Groups[2].Value;

                aggKey = "xsmart:" + cat + ":" + xid;
                source = "xsmart";

                if (!agg.ContainsKey(aggKey))
                {
                    if (xsmartCalls < AdminHistoryXsmartCap && budget.ElapsedMilliseconds < AdminHistoryNetBudgetMs)
                    {
                        xsmartCalls++;
                        var (xt, xy) = await AdminHistoryXsmartTitle(cat, xid);
                        if (xt != null) { title = xt; year = xy; resolved = true; resolvedBy = "xsmart"; }
                    }

                    // Не разрезолвилось — строка всё равно остаётся: просмотр был, и прятать его
                    // из-за лёгшего портала нельзя.
                    title ??= "XSMART · " + XsmartNet.Ref(cat, xid);
                }
            }
            else
            {
                var (kind, value) = ParseHistoryBucket(bucket);
                if (kind == null) { unresolved++; continue; }

                // 🔴 Схлопываем по ТАЙТЛУ, а не по ведру. У одного сериала вёдер штатно два:
                // «280095_tv» пишет полная карточка, «qdl_t280095» — экран серий (разбор в
                // HistoryBackfill). Ключ «kind:value» развёл бы их по разным строкам, и в журнале
                // «Повелитель духов» стоял бы дважды — ровно это и вылезло на боевых данных.
                // Поэтому у tv/movie/tmdb ключ — сам айди TMDB, а раздача сводится к айди своей меты.
                string canon =
                    kind == "jut" ? "jut:" + value
                  : kind == "hash" ? AdminHistoryHashCanon(value, byHash)
                  : value;

                aggKey = canon;
                string memoKey = kind + ":" + value;

                if (!agg.ContainsKey(aggKey))
                {
                    // Пойдёт ли резолв в сеть — решаем ДО вызова: memo и локальные индексы
                    // бесплатны, а последняя ступень HistoryResolveCard — это до двух GET
                    // с таймаутом 10 с на один тайтл.
                    bool local = memo.ContainsKey(memoKey)
                              || kind == "jut"
                              || (kind == "hash" ? byHash.ContainsKey(value) : byId.ContainsKey(value));

                    if (!local && (tmdbCalls >= AdminHistoryTmdbCap || budget.ElapsedMilliseconds >= AdminHistoryNetBudgetMs))
                    {
                        unresolved++;
                        continue;
                    }

                    if (!local) tmdbCalls++;

                    var card = await HistoryResolveCard(kind, value, byId, byHash, memo);
                    if (card == null) { unresolved++; continue; }

                    title = AdminHistoryTitle(card);
                    year = AdminHistoryYear(card);
                    source = card.Value<string>("source") ?? "";
                    resolved = true;
                    resolvedBy = local ? "local" : "tmdb";
                }
            }

            if (!agg.TryGetValue(aggKey, out var o))
            {
                agg[aggKey] = o = new JObject
                {
                    ["key"] = aggKey,
                    ["title"] = title ?? "",
                    ["year"] = year ?? "",
                    ["source"] = source ?? "",
                    ["resolved"] = resolved,
                    ["resolvedBy"] = resolvedBy,
                    ["buckets"] = new JArray(),
                    ["rows"] = 0,
                    ["done"] = 0,
                    ["percentMax"] = 0,
                    ["first"] = updated,
                    ["last"] = updated
                };
            }

            var buckets = (JArray)o["buckets"];
            if (!buckets.Any(b => (string)b == bucket)) buckets.Add(bucket);

            o["rows"] = (int)o["rows"] + 1;
            if (percent >= AdminHistoryDonePercent) o["done"] = (int)o["done"] + 1;
            if ((int)o["percentMax"] < (int)percent) o["percentMax"] = (int)percent;
            if (HistoryNewer(updated, (string)o["last"])) o["last"] = updated;
            if (HistoryNewer((string)o["first"], updated)) o["first"] = updated;
        }

        var list = new JArray(agg.Values
            .OrderByDescending(x => HistoryParseTime((string)x["last"]))
            .ThenBy(x => (string)x["title"], StringComparer.OrdinalIgnoreCase));

        return (list, unresolved);
    }

    #endregion
}
