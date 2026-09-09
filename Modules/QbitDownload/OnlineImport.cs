using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Раздел «Online» (контейнер `online` медиасервера, E:\Media-server\online\service): эфир по
// ссылке для всех клиентов. Сам эфир и записи живут ЦЕЛИКОМ в контейнере — здесь только одна
// ручка: «Сохранить в Загрузки». Контейнер копирует запись в общий бинд /downloads/online/<id>/
// и зовёт POST /qdl/online/import — мы регистрируем готовый mp4 как локальную карточку «Загрузок»
// ровно тем же набором файлов, что jut.su и XSMART: meta/<hash>.json + img/<hash>.jpg +
// local/<hash>.json. Дальше карточка живёт штатно: /qdl/list, /qdl/stream (Range), qdl_card,
// удаление по праву «действия».
//
// 🔴 Изоляция: `source:"online"` в мете — пояс IndexCrawler.NonTorrentSource (за «раздачей»
// эфира на трекеры ходить нельзя), `id:0` — клиент открывает свой экран qdl_card, а не TMDB.
// 🔴 Доступ: ручка ТОЛЬКО из LAN/докер-сети — запрос с маркером edge (Caddy) получает 404 даже
// с валидным ключом платформы; путь обязан лежать под onlineDownloadsPath после GetFullPath.
// Права «действия» не требуются: вызов сервер-сервер, uid устройства у него нет.
// ─────────────────────────────────────────────────────────────────────────────
static class OnlineNet
{
    /// <summary>Псевдо-infohash записи эфира: соль «online:» не пересекается с jutsu/xsmart.</summary>
    public static string Hash(string id)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes("online:" + id))).ToLowerInvariant();
    }
}

public partial class QbitController
{
    internal static readonly Regex OnlineIdRx = new Regex(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", RegexOptions.Compiled);

    internal static string OnlineDownloadsRoot() => ModInit.conf?.onlineDownloadsPath ?? "/downloads/online";

    /// <summary>Путь лежит внутри корня (после нормализации — «..» и симлинки-обманки не проходят).</summary>
    internal static bool OnlineUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            string r = Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    static string OnlineSafeFileName(string title)
    {
        string s = Regex.Replace(title ?? "", @"[\\/:*?""<>|\r\n\t]+", " ");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        if (s.Length > 120) s = s.Substring(0, 120).Trim();
        return string.IsNullOrEmpty(s) ? "online" : s;
    }

    /// <summary>Мета карточки. Поля — те же, что у jut/XSMART (qdl_card читает title/year/runtime/overview).</summary>
    internal static JObject OnlineMetaJson(string id, string title, DateTimeOffset? started, int durationSec, string sourceUrl)
    {
        string host = "";
        try { if (!string.IsNullOrEmpty(sourceUrl) && !sourceUrl.StartsWith("rec:")) host = new Uri(sourceUrl).Host; } catch { }
        string when = started?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";
        var m = new JObject
        {
            ["source"] = "online",
            ["online_id"] = id,
            ["title"] = title,
            ["original_title"] = title,
            ["year"] = started?.Year ?? 0,
            ["release_date"] = started?.ToString("yyyy-MM-dd") ?? "",
            ["id"] = 0,
            ["media_type"] = "movie",
            ["runtime"] = Math.Max(0, durationSec / 60),
            ["overview"] = "Запись эфира" + (when != "" ? " " + when : "") + (host != "" ? " · " + host : "")
        };
        return m;
    }

    /// <summary>
    /// Применить импорт: проверки + три записи. Возвращает (status, error, hash); status 200 = ок.
    /// Вынесено из экшена, чтобы тест гонял ровно ту же кухню без HTTP.
    /// </summary>
    internal static (int status, string error, string hash) OnlineImportApply(JObject body)
    {
        string id = body?.Value<string>("id");
        if (string.IsNullOrEmpty(id) || !OnlineIdRx.IsMatch(id)) return (400, "некорректный id", null);

        string path = body.Value<string>("path");
        string root = OnlineDownloadsRoot();
        if (!OnlineUnderRoot(path, root)) return (400, "путь вне " + root, null);
        if (!System.IO.File.Exists(path)) return (404, "файла нет: " + path, null);

        string poster = body.Value<string>("poster");
        if (!string.IsNullOrEmpty(poster) && (!OnlineUnderRoot(poster, root) || !System.IO.File.Exists(poster))) poster = null;

        string title = (body.Value<string>("title") ?? "").Trim();
        if (title.Length > 200) title = title.Substring(0, 200);
        if (title == "") title = "Эфир " + id;

        DateTimeOffset? started = null;
        if (DateTimeOffset.TryParse(body.Value<string>("started"), out var st)) started = st;
        int durationSec = Math.Max(0, body.Value<int?>("durationSec") ?? 0);
        string sourceUrl = body.Value<string>("sourceUrl");

        string hash = OnlineNet.Hash(id);
        long size;
        try { size = new FileInfo(path).Length; } catch { size = body.Value<long?>("bytes") ?? 0; }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetaPath(hash)));
            SaveMeta(hash, OnlineMetaJson(id, title, started, durationSec, sourceUrl));

            if (poster != null)
            {
                string pp = PosterPath(hash);
                Directory.CreateDirectory(Path.GetDirectoryName(pp));
                System.IO.File.Copy(poster, pp, true);
                PosterWritten();   // снимок каталога img/ устарел — иначе has_poster врёт до рестарта (§BV)
            }

            string dirNorm = (Path.GetDirectoryName(Path.GetFullPath(path)) ?? root).Replace('\\', '/');
            SaveLocal(hash, new JObject
            {
                ["name"] = title,
                ["dir"] = dirNorm,
                ["size"] = size,
                ["added"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["overlay"] = false,
                ["files"] = new JArray
                {
                    new JObject
                    {
                        ["index"] = 0,
                        ["name"] = OnlineSafeFileName(title) + ".mp4",
                        ["path"] = Path.GetFullPath(path).Replace('\\', '/'),
                        ["size"] = size
                    }
                },
                // Своё поле: LocalFiles лишнее игнорирует.
                ["online"] = new JObject
                {
                    ["id"] = id,
                    ["started"] = started?.ToString("o"),
                    ["sourceUrl"] = sourceUrl
                }
            });
            JsonStore.ForgetDir(Path.GetDirectoryName(Path.GetFullPath(path)));
            // Повторный импорт того же id: /qdl/stream мог кешировать прежний путь.
            DropResolveCache(hash);
        }
        catch (Exception ex)
        {
            return (500, "запись карточки: " + ex.Message, null);
        }

        return (200, null, hash);
    }

    /// <summary>Запрос прошёл через edge (Caddy 9443) — снаружи этой ручки нет вовсе.</summary>
    bool OnlineExternal()
    {
        string edge = CoreInit.conf?.d1v?.edgeHeader;
        return !string.IsNullOrEmpty(edge) && HttpContext.Request.Headers.ContainsKey(edge);
    }

    /// <summary>
    /// POST /qdl/online/import — контейнер `online` регистрирует скопированную запись.
    /// Тело: { id, title, started, durationSec, sourceUrl, path, poster, bytes }. Ответ: { ok, hash }.
    /// </summary>
    [HttpPost, AllowAnonymous]
    [Route("qdl/online/import")]
    async public Task<ActionResult> OnlineImport()
    {
        if (OnlineExternal()) return NotFound();
        var ro = ReplicaReadOnlyDeny(); if (ro != null) return ro;

        JObject body;
        try
        {
            using var sr = new StreamReader(Request.Body);
            string txt = await sr.ReadToEndAsync();
            if (txt.Length > 64 * 1024) return StatusCode(400, new { ok = false, message = "слишком большое тело" });
            body = JObject.Parse(txt);
        }
        catch { return StatusCode(400, new { ok = false, message = "тело не JSON" }); }

        var (status, error, hash) = OnlineImportApply(body);
        if (status != 200)
        {
            Console.WriteLine("[QbitDownload] online/import отказ: " + error);
            return StatusCode(status, new { ok = false, message = error });
        }
        Console.WriteLine("[QbitDownload] online/import: " + body.Value<string>("id") + " → " + hash);
        return Json(new { ok = true, hash });
    }
}
