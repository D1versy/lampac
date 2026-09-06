using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Shared.Services.Utilities;
using System;
using System.Collections.Generic;
using System.IO;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// УБОРКА СЛЕДОВ ТЕСТОВОГО УСТРОЙСТВА (qdl 2.64)
//
// Прогон гейта гоняет ЖИВОЙ интерфейс ЖИВОГО сервера — в этом весь его смысл, и менять это
// нельзя: тест, который пишет в заглушку, перестаёт ловить регрессии (грабля 2.38 — зелёный
// прогон на сломанном коде). Значит стенд оставляет ровно те же следы, что настоящий клиент,
// и убирать их надо ПОСЛЕ, а не запрещать до.
//
// Шесть хранилищ — это ВСЁ, что пишется по айди устройства. Список собран по коду, не на глаз:
//
//   1. /qdl-data/access.json                      реестр устройств и гранты     (Perms)
//   2. /qdl-data/jut/history/<uid>.json           история и поиски jut.su       (JutSuHistory)
//   3. database/Sync.sql      bookmarks(user,…)   закладки + ИСТОРИЯ ПРОСМОТРОВ (модуль Sync)
//   4. database/TimeCode.sql  timecodes(user,…)   позиции просмотра             (модуль Sync)
//   5. database/storage/syncview/<md5(uid)>       блоб localStorage             (модуль Storage)
//   6. database/music/Music.sql                   прослушивания, «Твой топ»,    (модуль Music)
//      playback_history / track_stats_daily /     плейлисты и креды профиля
//      user_playlists / auth_credentials          ⚠️ колонка profile_id, а НЕ user
//
// ⚠️ Почему лезем в чужие хранилища напрямую: модули компилируются в ОТДЕЛЬНЫЕ сборки и API
// друг друга не видят. Схема таблиц и путь блоба — те же, что уже используются в
// ReplicaHistory.cs (там же подробное обоснование). Больше уборка не знает НИЧЕГО:
// ни qdl.db, ни watch.json, ни кешей, ни init.conf.
//
// 🔴 КРАСНАЯ ЛИНИЯ — код здесь УДАЛЯЕТ, поэтому:
//
//   1. Работаем по белому списку: только те айди, которые вернул Perms.IsTestDevice.
//      Ни одного удаления «по маске», «по возрасту», «по платформе», «по пустому имени».
//   2. Двойной замок: классификатор зовётся при сборе списка И ещё раз в TestPurge перед
//      каждым айди. Второй замок защищает от будущей правки, которая позовёт метод напрямую.
//   3. Реестр сносится ПОСЛЕДНИМ. Пока строка на месте, улики (UA, имя, гранты) целы, и
//      прерванную на середине уборку можно просто повторить. Снеси мы её первой — остальные
//      четыре хранилища осиротели бы вместе с доказательством, что их вообще можно трогать.
//   4. Умолчание не разрушительно: пустой запрос — ошибка, а не «убрать всё».
//   5. Каждое удаление печатается в лог контейнера: айди, хранилище, сколько строк.
//   6. Ошибка одного хранилища не роняет ни сервер, ни прогон — уходит полем в отчёт.
//
// Регулярный путь (после каждого прогона гейта) убирает РОВНО ОДИН свой айди:
// POST /admin/d1v/api/test-purge {"uid":"d1v-test-…"}. Режим all:true существует для разовой
// уборки накопившегося и зовётся руками после сухого прогона (GET той же ручки).
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    /// <summary>
    /// Убрать следы тестовых устройств. apply=false — сухой прогон: только считает, не пишет.
    /// uid — один айди (регулярный путь), all — все тестовые (разовая уборка руками).
    /// </summary>
    internal static JObject TestPurge(string uid, bool all, bool apply)
    {
        var errors = new JArray();
        var report = new JObject
        {
            ["apply"] = apply,
            ["uids"] = new JArray(),
            ["devices"] = 0,
            ["jut"] = 0,
            ["xsmart"] = 0,
            ["bookmarks"] = 0,
            ["timecodes"] = 0,
            ["blobs"] = 0,
            ["music"] = 0,
            ["errors"] = errors
        };

        if (!Perms.SandboxEnabled)
        {
            report["error"] = "песочница выключена (testSandbox: false) — уборка отказывает всем";
            return report;
        }

        var targets = new List<string>();

        if (!string.IsNullOrWhiteSpace(uid))
        {
            string key = Perms.NormUid(uid);
            if (key == null)
            {
                report["error"] = "пустой или мусорный uid";
                return report;
            }

            // Первый замок: явно названный айди обязан быть тестовым. Нет — отказ целиком,
            // без частичной уборки: половина операции хуже, чем ни одной.
            if (!Perms.IsTestDevice(key))
            {
                report["error"] = "не тестовое устройство, уборка отклонена: " + key;
                return report;
            }

            targets.Add(key);
        }
        else if (all)
        {
            targets.AddRange(Perms.TestDevices());
        }
        else
        {
            report["error"] = "нужен uid или all: true — пустой запрос ничего не убирает";
            return report;
        }

        foreach (string key in targets)
        {
            // 🔴 ВТОРОЙ ЗАМОК. Список мог быть собран раньше, реестр — измениться.
            if (!Perms.IsTestDevice(key))
            {
                errors.Add("пропущено, перестало быть тестовым: " + key);
                continue;
            }

            ((JArray)report["uids"]).Add(key);

            Bump(report, "jut", PurgeJutHistory(key, apply, errors));
            Bump(report, "xsmart", PurgeXsmartHistory(key, apply, errors));
            Bump(report, "blobs", PurgeStorageBlob(key, apply, errors));
            Bump(report, "bookmarks", PurgeSqlRows(SyncDbPath, "bookmarks", "user", key, apply, errors));
            Bump(report, "timecodes", PurgeSqlRows(TimeCodeDbPath, "timecodes", "user", key, apply, errors));

            // Модуль Music ключует всё по profile_id (не по user). Четыре таблицы — полный набор
            // писателей по профилю: grep "INSERT INTO|UPDATE|DELETE FROM" по Modules/Music/Services.
            foreach (string table in new[] { "playback_history", "track_stats_daily", "user_playlists", "auth_credentials" })
                Bump(report, "music", PurgeSqlRows(MusicDbPath, table, "profile_id", key, apply, errors));

            // Реестр — последним (см. п.3 красной линии).
            Bump(report, "devices", PurgeDeviceRow(key, apply));
        }

        if (apply && ((JArray)report["uids"]).Count > 0)
        {
            // 🔴 Уборка обязана быть СИНХРОННОЙ: гейт сразу после неё делает сухой прогон и
            // краснеет, если что-то осталось. Горячий слой JSON пишет на диск фоновым
            // писателем — без Flush проверка увидела бы файл, которого уже нет в РАМ.
            JsonStore.Flush();
            Console.WriteLine("[QbitDownload] test-purge: " + report.ToString(Newtonsoft.Json.Formatting.None));
        }

        return report;

    }

    static void Bump(JObject report, string field, int n) => report[field] = (int)report[field] + n;

    /// <summary>История и поиски jut.su: один файл-бакет на устройство.</summary>
    static int PurgeJutHistory(string key, bool apply, JArray errors)
    {
        try
        {
            string bucket = JutHistoryBucket(key);

            // Общий бакет безымянных — не устройство и не тест. Сюда уборка не ходит никогда.
            if (bucket == JutSharedBucket) return 0;

            string path = JutHistoryPath(bucket);
            if (!JsonStore.Exists(path)) return 0;
            if (!apply) return 1;

            JsonStore.Remove(path);                       // и из РАМ, и с диска
            JsonStore.ForgetDir(JutHistoryDir());         // листинг каталога тоже кешируется
            Console.WriteLine("[QbitDownload] test-purge jut: " + bucket);
            return 1;
        }
        catch (Exception ex) { errors.Add("jut " + key + ": " + ex.Message); return 0; }
    }

    /// <summary>История и поиски XSMART (qdl 2.114): тот же бакет-на-устройство, что у jut.</summary>
    static int PurgeXsmartHistory(string key, bool apply, JArray errors)
    {
        try
        {
            string bucket = JutHistoryBucket(key);
            if (bucket == JutSharedBucket) return 0;          // общий бакет — не устройство и не тест

            string path = XsmartHistoryPath(bucket);
            if (!JsonStore.Exists(path)) return 0;
            if (!apply) return 1;

            JsonStore.Remove(path);
            JsonStore.ForgetDir(XsmartHistoryDir());
            Console.WriteLine("[QbitDownload] test-purge xsmart: " + bucket);
            return 1;
        }
        catch (Exception ex) { errors.Add("xsmart " + key + ": " + ex.Message); return 0; }
    }

    /// <summary>
    /// Блоб localStorage модуля Storage: database/storage/syncview/&lt;md5(uid)&gt;.
    /// Имя считается ровно так же, как в Storage/Controller.cs::getFilePath (pathfile пустой).
    /// Удаление неразрушающе даже по ошибке: клиент, не найдя файла, делает export заново.
    /// </summary>
    static int PurgeStorageBlob(string key, bool apply, JArray errors)
    {
        try
        {
            string md5 = CrypTo.md5(key);
            if (string.IsNullOrEmpty(md5) || md5.Length != 32) return 0;   // путь строится только из hex

            string path = Path.Combine(StorageDir, "syncview", md5.Substring(0, 2), md5.Substring(2));
            if (!System.IO.File.Exists(path)) return 0;
            if (!apply) return 1;

            System.IO.File.Delete(path);
            Console.WriteLine("[QbitDownload] test-purge blob: " + key);
            return 1;
        }
        catch (Exception ex) { errors.Add("blob " + key + ": " + ex.Message); return 0; }
    }

    /// <summary>
    /// Строки одного устройства в чужой БД. Имя таблицы И имя колонки — константы из НАШЕГО
    /// кода (у модуля Sync это user, у модуля Music — profile_id), айди уходит параметром:
    /// собрать «delete всего» из внешних данных здесь по-прежнему физически нечем.
    /// </summary>
    static int PurgeSqlRows(string dbPath, string table, string column, string key, bool apply, JArray errors)
    {
        try
        {
            if (!System.IO.File.Exists(dbPath)) return 0;

            using var db = OpenDb(dbPath);
            if (!TableExists(db, table)) return 0;        // модуль ещё не поднялся — не наше дело

            int n;
            using (var cnt = db.CreateCommand())
            {
                cnt.CommandText = "select count(*) from " + table + " where " + column + "=$u";
                cnt.Parameters.AddWithValue("$u", key);
                n = Convert.ToInt32(cnt.ExecuteScalar());
            }

            if (n == 0 || !apply) return n;

            using (var del = db.CreateCommand())
            {
                del.CommandText = "delete from " + table + " where " + column + "=$u";
                del.Parameters.AddWithValue("$u", key);
                n = del.ExecuteNonQuery();
            }

            Console.WriteLine("[QbitDownload] test-purge " + table + ": " + key + " → " + n);
            return n;
        }
        catch (Exception ex) { errors.Add(table + " " + key + ": " + ex.Message); return 0; }
    }

    /// <summary>Строка реестра устройств. Снимается последней.</summary>
    static int PurgeDeviceRow(string key, bool apply)
    {
        if (!Perms.Known(key)) return 0;
        if (!apply) return 1;

        bool ok = Perms.Forget(key);
        if (ok) Console.WriteLine("[QbitDownload] test-purge device: " + key);
        return ok ? 1 : 0;
    }
}
