using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

public class QbitController : BaseController
{
    #region qBittorrent client (cookie auth)
    async Task<HttpClient> qbit()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false
        };
        var c = new HttpClient(handler)
        {
            BaseAddress = new Uri(ModInit.conf.qbitHost),
            Timeout = TimeSpan.FromSeconds(ModInit.conf.timeoutSeconds)
        };
        // qBittorrent CSRF: Referer должен совпадать с хостом WebUI
        c.DefaultRequestHeaders.Referrer = new Uri(ModInit.conf.qbitHost);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", ModInit.conf.qbitUser),
            new KeyValuePair<string, string>("password", ModInit.conf.qbitPass)
        });
        await c.PostAsync("/api/v2/auth/login", form);
        return c;
    }
    #endregion

    #region qdl.js (клиентский плагин Lampa)
    [HttpGet, AllowAnonymous]
    [Route("qdl.js")]
    public ActionResult Plugin()
    {
        string js = FileCache.ReadAllText($"{ModInit.modpath}/plugins/qdl.js", "qdl.js")
            .Replace("{localhost}", host);

        return ContentTo(js, "application/javascript; charset=utf-8");
    }
    #endregion

    #region /qdl/search — поиск торрентов через JacRed (тот же источник, что у Lampa)
    [HttpGet, AllowAnonymous]
    [Route("qdl/search")]
    async public Task<ActionResult> Search(string query, int year = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ContentTo("[]", "application/json; charset=utf-8");

        string url = $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}/api/v1.0/torrents?search={HttpUtility.UrlEncode(query)}";
        string raw = await Http.Get(url, timeoutSeconds: 40);

        var result = new JArray();
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                foreach (var t in JArray.Parse(raw))
                {
                    int relased = t.Value<int?>("relased") ?? 0;
                    if (year > 0 && relased > 0 && relased != year)
                        continue;

                    result.Add(new JObject
                    {
                        ["title"] = t.Value<string>("title"),
                        ["magnet"] = t.Value<string>("magnet"),
                        ["parselink"] = t.Value<string>("parselink"),
                        ["tracker"] = t.Value<string>("tracker") ?? t.Value<string>("trackerName"),
                        ["sid"] = t.Value<int?>("sid") ?? 0,
                        ["size"] = t.Value<string>("sizeName"),
                        ["quality"] = t.Value<int?>("quality") ?? 0
                    });
                }
            }
            catch { }
        }

        return ContentTo(result.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }
    #endregion

    #region /qdl/add — добавить magnet в qBittorrent (резолв parselink при необходимости)
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/add")]
    async public Task<ActionResult> Add(string magnet = null, string parselink = null, string title = null)
    {
        try
        {
            // link: настоящий "magnet:?...", ИЛИ URL-резолвер JacRed (parselink).
            // Резолвер может отдать: 302→magnet (rutracker/kinozal/nnm), magnet в теле, или .torrent-файл.
            string link = !string.IsNullOrWhiteSpace(magnet) ? magnet : parselink;
            byte[] torrentFile = null;

            if (!string.IsNullOrWhiteSpace(link) && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                using var rh = new HttpClientHandler { AllowAutoRedirect = false };
                using var rc = new HttpClient(rh) { Timeout = TimeSpan.FromSeconds(45) };
                var resp = await rc.GetAsync(link);

                if (resp.Headers.Location != null && resp.Headers.Location.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    link = resp.Headers.Location.OriginalString;          // 302 → magnet
                }
                else
                {
                    string ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (ct.Contains("bittorrent") || ct.Contains("octet-stream"))
                    {
                        torrentFile = await resp.Content.ReadAsByteArrayAsync();
                        if (torrentFile == null || torrentFile.Length < 50) torrentFile = null;
                    }
                    if (torrentFile == null)
                    {
                        string b = await resp.Content.ReadAsStringAsync();
                        var m = Regex.Match(b ?? "", "magnet:\\?[^\"'\\s<]+");
                        if (m.Success) link = m.Value;
                        else return Json(new { success = false, error = "resolve: " + (b ?? "").Trim() });
                    }
                }
            }

            var c = await qbit();
            MultipartFormDataContent content;
            string usedMagnet = null;

            if (torrentFile != null)
            {
                var fc = new ByteArrayContent(torrentFile);
                fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                content = new MultipartFormDataContent
                {
                    { fc, "torrents", "file.torrent" },
                    { new StringContent(ModInit.conf.downloadsPath), "savepath" },
                    { new StringContent(ModInit.conf.category), "category" }
                };
            }
            else
            {
                usedMagnet = link;
                if (string.IsNullOrWhiteSpace(usedMagnet) || !usedMagnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = false, error = "no magnet" });
                content = new MultipartFormDataContent
                {
                    { new StringContent(usedMagnet), "urls" },
                    { new StringContent(ModInit.conf.downloadsPath), "savepath" },
                    { new StringContent(ModInit.conf.category), "category" }
                };
            }

            var r = await c.PostAsync("/api/v2/torrents/add", content);
            string body = (await r.Content.ReadAsStringAsync())?.Trim() ?? "";

            // qBittorrent отвечает либо "Ok." (старые версии), либо JSON {"success_count":1,...} (v5+)
            bool ok = r.IsSuccessStatusCode && (
                body == "Ok." ||
                (body.StartsWith("{") && body.Contains("\"success_count\"") && !body.Contains("\"success_count\":0"))
            );

            string hash = "";
            if (usedMagnet != null)
            {
                var hm = Regex.Match(usedMagnet, "btih:([0-9a-fA-F]{40}|[0-9a-zA-Z]{32})", RegexOptions.IgnoreCase);
                if (hm.Success) hash = hm.Groups[1].Value.ToLower();
            }

            return Json(new { success = ok, hash, body });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }
    #endregion

    #region /qdl/list — список загрузок (категория lampa)
    [HttpGet, AllowAnonymous]
    [Route("qdl/list")]
    async public Task<ActionResult> List()
    {
        try
        {
            var c = await qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}&sort=added_on&reverse=true");

            var result = new JArray();
            foreach (var t in JArray.Parse(raw))
            {
                result.Add(new JObject
                {
                    ["hash"] = t.Value<string>("hash"),
                    ["name"] = t.Value<string>("name"),
                    ["progress"] = t.Value<double?>("progress") ?? 0,
                    ["state"] = t.Value<string>("state"),
                    ["size"] = t.Value<long?>("size") ?? 0,
                    ["save_path"] = t.Value<string>("save_path"),
                    ["content_path"] = t.Value<string>("content_path")
                });
            }

            return ContentTo(result.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }
    #endregion

    #region /qdl/files — файлы торрента (для сериалов/мультифайла)
    [HttpGet, AllowAnonymous]
    [Route("qdl/files")]
    async public Task<ActionResult> Files(string hash)
    {
        try
        {
            var c = await qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/files?hash={hash}");
            return ContentTo(raw ?? "[]", "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }
    #endregion

    #region /qdl/stream — отдать файл с диска D с поддержкой перемотки (оффлайн-плеер)
    [HttpGet, AllowAnonymous]
    [Route("qdl/stream")]
    async public Task<ActionResult> Stream(string hash, int index = -1)
    {
        try
        {
            var c = await qbit();

            string infoRaw = await c.GetStringAsync($"/api/v2/torrents/info?hashes={hash}");
            var info = JArray.Parse(infoRaw);
            if (info.Count == 0) return NotFound();
            string savePath = info[0].Value<string>("save_path") ?? ModInit.conf.downloadsPath;

            string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={hash}");
            var files = JArray.Parse(filesRaw);
            if (files.Count == 0) return NotFound();

            JToken file = null;
            if (index >= 0)
            {
                foreach (var f in files)
                    if ((f.Value<int?>("index") ?? -1) == index) { file = f; break; }
            }
            if (file == null)
            {
                long max = -1;
                foreach (var f in files)
                {
                    long s = f.Value<long?>("size") ?? 0;
                    if (s > max) { max = s; file = f; }
                }
            }
            if (file == null) return NotFound();

            string rel = file.Value<string>("name");
            string full = Combine(savePath, rel);
            if (!System.IO.File.Exists(full))
                full = Combine(ModInit.conf.downloadsPath, rel);   // remap на наш mount, если save_path иной
            if (!System.IO.File.Exists(full))
                return NotFound();

            return PhysicalFile(full, MimeType(full), enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    static string Combine(string baseDir, string rel)
        => baseDir.TrimEnd('/', '\\') + "/" + (rel ?? "").Replace('\\', '/').TrimStart('/');

    static string MimeType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".mp4":
            case ".m4v": return "video/mp4";
            case ".mkv": return "video/x-matroska";
            case ".avi": return "video/x-msvideo";
            case ".ts": return "video/mp2t";
            case ".webm": return "video/webm";
            case ".mov": return "video/quicktime";
            default: return "application/octet-stream";
        }
    }
    #endregion

    #region /qdl/delete — удалить загрузку (опционально с файлами)
    [HttpGet, AllowAnonymous]
    [Route("qdl/delete")]
    async public Task<ActionResult> Delete(string hash, bool deleteFiles = false)
    {
        try
        {
            var c = await qbit();
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")
            });
            var r = await c.PostAsync("/api/v2/torrents/delete", form);
            return Json(new { success = r.IsSuccessStatusCode });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }
    #endregion
}
