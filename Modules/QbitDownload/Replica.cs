using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

// ── Сервер-реплика: маленький бекап-сервер на второй площадке ────────────────────
//
// Дом единственный, и падает он регулярно (питание, ИБП нет). Реплика держит СРЕЗ свежих
// загрузок в пределах бюджета и отдаёт их клиентам, когда дом недоступен: клиенты сами
// сваливаются на неё штатной гонкой хостов.
//
// Роль задаётся ключом replicaRole в init.conf:
//   "main" / пусто → дом. Добавляются ТОЛЬКО ручки /qdl/replica/* на чтение, больше ничего
//                    не меняется. Ни один существующий контур не трогается.
//   "replica"      → реплика. Включается цикл репликации и ЗАПРЕЩАЮТСЯ мутирующие ручки.
//
// ГЛАВНЫЕ ИНВАРИАНТЫ (нарушение любого = потеря данных, а не неудобство):
//
//  1. 🔴 Реплика НИКОГДА не пишет в дом. Все ручки дома — GET и только чтение. Удаление живёт
//     исключительно на реплике и только в её собственной категории.
//  2. 🔴 Пустой/подозрительный манифест — это авария дома, а НЕ команда «всё удалить».
//     Источники манифеста несут статус, и при !ok ротация в этот тик не работает вовсе.
//     Прецедент рядом: ReconcileDonors при пустом watch.json (§AK).
//  3. 🔴 Частично скачанный файл не должен выглядеть готовым: /qdl/list считает локальную
//     карточку живой, если существует ХОТЬ ОДИН файл из маркера (Controller.cs, FileInDir),
//     поэтому в маркер файл попадает только после rename из .part.
//  4. ⚠️ Мост шейпится ОДНИМ token bucket на процесс: три параллельные докачки с личным
//     лимитом дали бы тройную скорость и съели домашний аплинк.
//  5. ⚠️ Реплика не сидирует. Публичная раздача с датацентрового адреса — это abuse-письма
//     и потеря площадки.
//
// Канон: E:\D1vision-replica\claude\. План: E:\Media-server\claude\.

public partial class QbitController
{
    // ── роль ────────────────────────────────────────────────────────────────────
    // Читается в ОДНОМ месте и регистронезависимо: опечатка в init.conf не должна тихо
    // превратить реплику обратно в дом, который начнёт вести себя как источник правды.
    internal static bool ReplicaMode
        => string.Equals(ModInit.conf?.replicaRole, "replica", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Гейт мутирующих ручек. Реплика — read-only ПО СЕРВЕРУ, а не спрятанной кнопкой:
    /// ручки [AllowAnonymous] и доступны снаружи за ключом периметра, поэтому «мы не будем
    /// показывать кнопку» защитой не является. Возвращает null, когда действие разрешено.
    /// </summary>
    internal ActionResult ReplicaReadOnlyDeny()
    {
        if (!ReplicaMode) return null;
        return StatusCode(403, new { success = false, error = "сервер-реплика: только чтение" });
    }

    #region /qdl/replica/* — контракт «дом → реплика», только чтение

    // Версия контракта. Дом и реплика обновляются в разное время; молчаливое расхождение
    // здесь означало бы неверную ротацию, поэтому реплика при несовпадении громко отказывает.
    internal const int ReplicaManifestVersion = 1;

    /// <summary>
    /// Охрана ручек моста.
    /// ⚠️ Честно про проверку адреса: под Docker Desktop RemoteIpAddress подменяется на шлюз
    /// сети контейнеров (172.x.x.1) у ВСЕХ запросов на проброшенный порт — это уже описано в
    /// JacRed/NodeController. То есть проверка вырождается в «дотянулся до порта».
    /// Настоящая защита — сегментация: в туннель проброшен отдельный порт Caddy, где разрешены
    /// только GET /qdl/replica/*, /qdl/stream, /qdl/poster, /qdl/files. Проверки ниже —
    /// второй слой, а не единственный.
    /// </summary>
    ActionResult ReplicaBridgeDeny()
    {
        // На самой реплике ручки бессмысленны: она не источник правды и не должна становиться
        // им для третьей машины.
        if (ReplicaMode) return StatusCode(403, new { error = "replica role" });

        // Запрос с маркером edge пришёл ИЗ ИНТЕРНЕТА через Caddy — адрес источника у него
        // приватный (контейнер Caddy), проверку адреса он бы прошёл. Мост живёт в туннеле.
        string edgeHeader = CoreInit.conf?.d1v?.edgeHeader;
        if (!string.IsNullOrEmpty(edgeHeader) && HttpContext.Request.Headers.ContainsKey(edgeHeader))
            return StatusCode(403, new { error = "external" });

        if (!IsPrivateAddress(HttpContext.Connection.RemoteIpAddress))
            return StatusCode(403, new { error = "not a private address" });

        return null;
    }

    // Свой IsPrivate: NodeRegistry живёт в модуле JacRed, а модули компилируются Roslyn'ом
    // в ОТДЕЛЬНЫЕ сборки и не видят друг друга (та же причина, по которой в ModuleConf
    // продублирован адрес FlareSolverr).
    internal static bool IsPrivateAddress(IPAddress addr)
    {
        if (addr == null) return false;
        if (IPAddress.IsLoopback(addr)) return true;

        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();

        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;   // link-local
            return false;
        }

        return addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal
            || addr.GetAddressBytes()[0] == 0xfd || addr.GetAddressBytes()[0] == 0xfc;   // ULA
    }

    /// <summary>
    /// Снимок того, что реплике надо иметь. Единственный «толстый» запрос цикла: всё остальное
    /// реплика тянет точечно и только по изменившемуся.
    /// </summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/replica/manifest")]
    async public Task<ActionResult> ReplicaManifest()
    {
        var deny = ReplicaBridgeDeny(); if (deny != null) return deny;

        string qbitStatus = "ok", localStatus = "ok";
        var torrents = new JArray();
        var local = new JArray();

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JObject act; lock (_activityLock) act = ActivityLoad();

        // Доноры охоты — по тому же фильтру, что в /qdl/list. Прямой снимок category=lampa их
        // НЕ отсекает, и реплика начала бы качать промежуточные раздачи охоты, съедая бюджет
        // мусором, который дома живёт часы.
        var donorHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var w in LoadWatch())
                if (w["donors"] is JArray ds)
                    foreach (var d in ds)
                    {
                        var dh = d.Value<string>("hash");
                        if (!string.IsNullOrEmpty(dh)) donorHashes.Add(dh);
                    }
        }
        catch { }

        try
        {
            using var c = await Qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}&sort=added_on&reverse=true");

            foreach (var t in JArray.Parse(raw))
            {
                string h = (t.Value<string>("hash") ?? "").ToLowerInvariant();
                if (!ValidHash(h) || donorHashes.Contains(h)) continue;

                double prog = t.Value<double?>("progress") ?? 0;
                long addedOn = t.Value<long?>("added_on") ?? 0;

                // 🔴 Признак приватности — fail-closed: пока метаданные не скачаны, qBit отдаёт
                // null, и «неизвестно» обязано читаться как «приватная». Ошибка в эту сторону
                // стоит лишней докачки по мосту; ошибка в другую — бана аккаунта на трекере,
                // потому что тем же passkey раздача засветится со второго адреса.
                bool priv = t.Value<bool?>("private") ?? true;

                torrents.Add(new JObject
                {
                    ["hash"] = h,
                    ["name"] = t.Value<string>("name"),
                    ["size"] = t.Value<long?>("size") ?? 0,
                    ["progress"] = prog,
                    ["added"] = addedOn,
                    ["activity"] = CardActivity(addedOn, t.Value<long?>("completion_on") ?? 0, prog, ActivityStored(act, h), nowUnix),
                    ["private"] = priv,
                    // сколько сидов ВИДИТ трекер/DHT помимо нас. <=0 означает «единственный
                    // источник — дом», и тогда swarm-загрузка на реплике пошла бы прямо с
                    // домашнего аплинка, мимо шейпера моста.
                    ["numComplete"] = t.Value<int?>("num_complete") ?? -1,
                    ["metaAt"] = FileStampUtc(MetaPath(h)),
                    ["posterAt"] = FileStampUtc(PosterPath(h))
                });
            }
        }
        catch (Exception ex)
        {
            qbitStatus = "fail";
            Console.WriteLine("[QbitDownload] replica manifest: qBit недоступен: " + ex.Message);
        }

        // Локальные карточки: только jut.su. Транскоды (overlay и /downloads/transcoded)
        // реплике не нужны — там нет транскода, и играется оригинал торрента.
        try
        {
            string localDir = Path.Combine(ModInit.conf.cachePath, "local");
            foreach (var lf in JsonStore.List(localDir, "*.json"))
            {
                string h = Path.GetFileNameWithoutExtension(lf);
                if (!ValidHash(h)) continue;

                JObject loc = LoadLocal(h);
                if (loc == null || LocalIsOverlay(loc)) continue;

                var jut = loc["jut"] as JObject;
                string slug = jut?.Value<string>("slug");
                if (string.IsNullOrEmpty(slug)) continue;

                var files = new JArray();
                long total = 0;
                foreach (var f in LocalFiles(loc))
                {
                    if (!FileInDir(f.path)) continue;
                    // ⚠️ Отдаём ИМЯ, а не путь: у маркера пути абсолютные, и на реплике корень
                    // загрузок свой. Путь она соберёт сама из своего jutDownloadsPath + slug.
                    files.Add(new JObject { ["index"] = f.index, ["name"] = f.name, ["size"] = f.size });
                    total += f.size;
                }
                if (files.Count == 0) continue;

                long added = loc.Value<long?>("added") ?? MarkerFallbackAdded(lf);
                local.Add(new JObject
                {
                    ["hash"] = h,
                    ["name"] = loc.Value<string>("name") ?? slug,
                    ["slug"] = slug,
                    ["size"] = total,
                    ["added"] = added,
                    ["activity"] = Math.Max(added, ActivityStored(act, h)),
                    ["files"] = files,
                    ["metaAt"] = FileStampUtc(MetaPath(h)),
                    ["posterAt"] = FileStampUtc(PosterPath(h))
                });
            }
        }
        catch (Exception ex)
        {
            localStatus = "fail";
            Console.WriteLine("[QbitDownload] replica manifest: локальные карточки: " + ex.Message);
        }

        var manifest = new JObject
        {
            ["manifestVersion"] = ReplicaManifestVersion,
            ["at"] = nowUnix,
            // Пер-источник статусы — вход для fail-safe реплики. Один общий флаг «ошибка» не
            // годится: падение qBit и падение файлового слоя означают РАЗНЫЕ решения.
            ["sources"] = new JObject { ["qbit"] = qbitStatus, ["local"] = localStatus },
            ["counts"] = new JObject { ["torrents"] = torrents.Count, ["local"] = local.Count },
            ["torrents"] = torrents,
            ["local"] = local,
            // Ряды прогрева каталога: реплика перепишет в них host на свой и наполнит СВОЙ
            // Staticache. Файлы кеша копировать бессмысленно — ключ считается из Scheme+Host,
            // а исходного URL в имени файла нет (Core/Middlewares/Staticache.cs).
            ["rows"] = new JArray(CatalogWarmup.ExportRowPaths().Cast<object>().ToArray()),
            ["notiMaxId"] = NotiMaxIdSafe()
        };

        return ContentTo(manifest.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    /// <summary>.torrent для раздачи, которую реплика качает из swarm сама.</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/replica/torrent")]
    async public Task<ActionResult> ReplicaTorrent(string hash)
    {
        var deny = ReplicaBridgeDeny(); if (deny != null) return deny;
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });

        try
        {
            using var c = await Qbit();
            string he = HttpUtility.UrlEncode(hash);

            // 🔴 Приватным здесь отказываем ЖЁСТКО, не полагаясь на дисциплину вызывающего:
            // внутри .torrent приватной раздачи лежит announce с личным passkey, и отдать его
            // наружу — значит отдать доступ к аккаунту трекера. Неизвестность = приватная.
            var info = JArray.Parse(await c.GetStringAsync($"/api/v2/torrents/info?hashes={he}"));
            if (info.Count == 0) return NotFound();
            bool priv = info[0].Value<bool?>("private") ?? true;
            if (priv) return StatusCode(403, new { error = "private torrent" });

            var r = await c.GetAsync($"/api/v2/torrents/export?hash={he}");
            if (!r.IsSuccessStatusCode) return StatusCode((int)r.StatusCode, new { error = "export failed" });

            var bytes = await r.Content.ReadAsByteArrayAsync();
            if (bytes == null || bytes.Length == 0) return NotFound();
            return File(bytes, "application/x-bittorrent");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] replica torrent: " + ex.Message);
            return Json(new { error = "internal error" });
        }
    }

    /// <summary>Мета карточки (slimCard). Реплика тянет её только когда metaAt изменился.</summary>
    [HttpGet, AllowAnonymous]
    [Route("qdl/replica/meta")]
    public ActionResult ReplicaMeta(string hash)
    {
        var deny = ReplicaBridgeDeny(); if (deny != null) return deny;
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });

        var meta = LoadMeta(hash);
        if (meta == null) return NotFound();
        return ContentTo(meta.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    // mtime файла в unix-секундах; 0 = файла нет. Реплика по нему решает, тянуть ли заново.
    static long FileStampUtc(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return 0;
            return new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
        }
        catch { return 0; }
    }

    static int NotiMaxIdSafe()
    {
        try
        {
            using var db = new SqlContext();
            return db.noti.Max(x => (int?)x.Id) ?? 0;
        }
        catch { return 0; }
    }

    #endregion
}
