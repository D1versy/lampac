using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace QbitDownload;

public class QbitController : BaseController
{
    static readonly Regex _hashRx = new Regex("^([0-9a-fA-F]{40}|[0-9A-Za-z]{32})$", RegexOptions.Compiled);
    static bool ValidHash(string h) => !string.IsNullOrEmpty(h) && _hashRx.IsMatch(h);

    #region qBittorrent client (cookie auth, проверяем логин)
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
        var resp = await c.PostAsync("/api/v2/auth/login", form);
        string login = (await resp.Content.ReadAsStringAsync())?.Trim();

        // Успех = 2xx + выставлена сессионная кука. qBit v5 отдаёт 204 + QBT_SID (тело пустое),
        // старые версии — 200 + "Ok.". Неверные креды: 403 (v5) или 200 + "Fails." без куки.
        bool hasSid = false;
        foreach (Cookie ck in handler.CookieContainer.GetCookies(new Uri(ModInit.conf.qbitHost)))
            if (ck.Name.StartsWith("QBT_SID", StringComparison.OrdinalIgnoreCase)) { hasSid = true; break; }

        if (!resp.IsSuccessStatusCode || (!hasSid && login != "Ok."))
        {
            c.Dispose();
            throw new Exception("qbit auth failed");
        }
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

    #region /qdl/add — добавить magnet/.torrent в qBittorrent (резолв parselink при необходимости)
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/add")]
    async public Task<ActionResult> Add(string magnet = null, string parselink = null, string title = null)
    {
        try
        {
            // link: настоящий "magnet:?...", либо URL-резолвер JacRed (parselink).
            // Резолвер может отдать: 302→magnet (rutracker/kinozal/nnm), magnet в теле, или .torrent-файл.
            string link = !string.IsNullOrWhiteSpace(magnet) ? magnet : parselink;
            byte[] torrentFile = null;
            const long MaxBytes = 10L * 1024 * 1024;

            if (!string.IsNullOrWhiteSpace(link) && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                // SSRF-защита: ходим ТОЛЬКО на собственный JacRed-резолвер (loopback/наш listen-хост:порт).
                // Публичные трекеры дают готовый magnet (сюда не попадают). См. claude/06 §A,§J.
                if (!Uri.TryCreate(link, UriKind.Absolute, out var startUri) || !IsSelfResolver(startUri))
                    return Json(new { success = false, error = "bad link" });

                using var rh = new HttpClientHandler { AllowAutoRedirect = false };
                using var rc = new HttpClient(rh) { Timeout = TimeSpan.FromSeconds(15) };

                HttpResponseMessage resp = null;
                try
                {
                    var current = startUri;
                    for (int hop = 0; hop < 5; hop++)        // следуем редиректам (302→magnet и т.п.)
                    {
                        resp?.Dispose();
                        resp = await rc.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);

                        int code = (int)resp.StatusCode;
                        var loc = resp.Headers.Location;
                        if (code < 300 || code >= 400 || loc == null) break;   // терминальный ответ

                        var next = loc.IsAbsoluteUri ? loc : new Uri(resp.RequestMessage?.RequestUri ?? current, loc);
                        if (next.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                        { link = next.OriginalString; resp.Dispose(); resp = null; break; }

                        if (!IsSelfResolver(next)) { resp.Dispose(); resp = null; break; }   // наружу не ходим
                        current = next;
                    }

                    if (resp != null && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (resp.Content.Headers.ContentLength > MaxBytes)
                            return Json(new { success = false, error = "too big" });
                        try { await resp.Content.LoadIntoBufferAsync(MaxBytes); }
                        catch { return Json(new { success = false, error = "too big" }); }

                        byte[] data = await resp.Content.ReadAsByteArrayAsync();
                        if (LooksLikeTorrent(data))
                        {
                            torrentFile = data;
                        }
                        else
                        {
                            string b = Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
                            var m = Regex.Match(b ?? "", "magnet:\\?[^\"'\\s<]+");
                            if (m.Success) link = m.Value;
                            else
                            {
                                Console.WriteLine("[QbitDownload] resolve failed: " + (b ?? "").Trim());
                                return Json(new { success = false, error = "resolve failed" });
                            }
                        }
                    }
                }
                finally { resp?.Dispose(); }
            }

            using var c = await qbit();
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

            // qBit-ответы зависят от версии (проверено эмпирически на v5/linuxserver):
            //   новый торрент → 200 + {"success_count":1,"added_torrent_ids":[..]} (старые: "Ok." / 204)
            //   дубликат      → 409 + "Conflict"
            bool ok = false, duplicate = false;
            if ((int)r.StatusCode == 409 || body.Equals("Conflict", StringComparison.OrdinalIgnoreCase))
            {
                duplicate = true; ok = true;          // уже в загрузках — это успех
            }
            else if (r.IsSuccessStatusCode)
            {
                if (body == "Ok." || body.Length == 0) ok = true;
                else if (body.StartsWith("{"))
                {
                    try
                    {
                        var j = JObject.Parse(body);
                        int success = j.Value<int?>("success_count") ?? 0;
                        int pending = j.Value<int?>("pending_count") ?? 0;
                        int dup = j.Value<int?>("duplicate_count") ?? 0;
                        duplicate = dup > 0;
                        ok = success > 0 || pending > 0 || duplicate;
                    }
                    catch { ok = false; }
                }
            }

            string hash = "";
            if (usedMagnet != null)
            {
                var hm = Regex.Match(usedMagnet, "btih:([0-9a-fA-F]{40}|[0-9a-zA-Z]{32})", RegexOptions.IgnoreCase);
                if (hm.Success) hash = hm.Groups[1].Value.ToLower();
            }

            return Json(new { success = ok, duplicate, hash, body });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] add: " + ex);
            return Json(new { success = false, error = "internal error" });
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
            using var c = await qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}&sort=added_on&reverse=true");

            var result = new JArray();
            foreach (var t in JArray.Parse(raw))
            {
                string h = t.Value<string>("hash") ?? "";
                var item = new JObject
                {
                    ["hash"] = h,
                    ["name"] = t.Value<string>("name"),
                    ["progress"] = t.Value<double?>("progress") ?? 0,
                    ["state"] = t.Value<string>("state"),
                    ["size"] = t.Value<long?>("size") ?? 0,
                    ["save_path"] = t.Value<string>("save_path"),
                    ["content_path"] = t.Value<string>("content_path"),
                    ["has_poster"] = ValidHash(h) && System.IO.File.Exists(PosterPath(h))
                };
                if (ValidHash(h) && System.IO.File.Exists(MetaPath(h)))
                {
                    try { item["meta"] = JObject.Parse(System.IO.File.ReadAllText(MetaPath(h))); } catch { }
                }
                result.Add(item);
            }

            return ContentTo(result.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] list: " + ex);
            return Json(new { error = "internal error" });
        }
    }
    #endregion

    #region /qdl/files — файлы торрента (для сериалов/мультифайла)
    [HttpGet, AllowAnonymous]
    [Route("qdl/files")]
    async public Task<ActionResult> Files(string hash)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            using var c = await qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
            return ContentTo(raw ?? "[]", "application/json; charset=utf-8");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] files: " + ex);
            return Json(new { error = "internal error" });
        }
    }
    #endregion

    #region /qdl/stream — отдать файл с диска D с поддержкой перемотки (оффлайн-плеер)
    [HttpGet, AllowAnonymous]
    [Route("qdl/stream")]
    async public Task<ActionResult> Stream(string hash, int index = -1)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            using var c = await qbit();
            string full = await ResolveFile(c, hash, index);
            if (full == null) return NotFound();
            return PhysicalFile(full, MimeType(full), enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] stream: " + ex);
            return Json(new { error = "internal error" });
        }
    }

    // Находит локальный путь к видеофайлу торрента (index<0 → самый большой). null если нет.
    async Task<string> ResolveFile(HttpClient c, string hash, int index)
    {
        string he = HttpUtility.UrlEncode(hash);

        string infoRaw = await c.GetStringAsync($"/api/v2/torrents/info?hashes={he}");
        var info = JArray.Parse(infoRaw);
        if (info.Count == 0) return null;
        string savePath = info[0].Value<string>("save_path") ?? ModInit.conf.downloadsPath;
        string contentPath = info[0].Value<string>("content_path");

        string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={he}");
        var files = JArray.Parse(filesRaw);
        if (files.Count == 0) return null;

        JToken file = null;
        if (index >= 0)
            foreach (var f in files)
                if ((f.Value<int?>("index") ?? -1) == index) { file = f; break; }
        if (file == null)
        {
            long max = -1;
            foreach (var f in files) { long s = f.Value<long?>("size") ?? 0; if (s > max) { max = s; file = f; } }
        }
        if (file == null) return null;

        string rel = file.Value<string>("name");
        string full = null;
        if (files.Count == 1 && !string.IsNullOrEmpty(contentPath) && System.IO.File.Exists(contentPath))
            full = contentPath;
        if (full == null) full = ConfinedCombine(savePath, rel);
        if (full == null || !System.IO.File.Exists(full))
            full = ConfinedCombine(ModInit.conf.downloadsPath, rel);
        if (full == null || !System.IO.File.Exists(full)) return null;
        return full;
    }

    // Безопасная сборка пути: выкидываем .. / . / пустые сегменты, канонизируем и проверяем,
    // что результат строго внутри baseDir (защита от path traversal в file.name).
    static string ConfinedCombine(string baseDir, string rel)
    {
        if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(rel)) return null;

        var parts = rel.Replace('\\', '/').Split('/');
        var clean = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == "..") continue;
            clean.Add(p);
        }
        if (clean.Count == 0) return null;

        string baseFull = Path.GetFullPath(baseDir);
        string candidate = Path.GetFullPath(Path.Combine(baseFull, string.Join("/", clean)));

        string prefix = baseFull.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, cmp)) return null;

        return candidate;
    }

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
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            using var c = await qbit();
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
            Console.WriteLine("[QbitDownload] delete: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
    #endregion

    #region /qdl/save — сохранить метаданные TMDB + закэшировать постер локально (SSD)
    [HttpPost, AllowAnonymous]
    [Route("qdl/save")]
    async public Task<ActionResult> Save(string hash, string card = null, string poster_url = null)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "meta"));
            Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "img"));

            // Клиент шлёт уже подготовленную карточку (slimCard) со всеми нужными полями —
            // храним как есть (валидируем JSON + кап размера), чтобы метаданные были богатыми.
            if (!string.IsNullOrWhiteSpace(card) && card.Length < 65536)
            {
                try
                {
                    var j = JObject.Parse(card);
                    System.IO.File.WriteAllText(MetaPath(hash), j.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { }
            }

            // постер качаем сами (только https + image/* + кап 6МБ; loopback/приват запрещены)
            if (!string.IsNullOrWhiteSpace(poster_url)
                && Uri.TryCreate(poster_url, UriKind.Absolute, out var pu)
                && pu.Scheme == "https" && !IsPrivateHost(pu))
            {
                try
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    using var pr = await hc.GetAsync(pu, HttpCompletionOption.ResponseHeadersRead);
                    string ct = pr.Content.Headers.ContentType?.MediaType ?? "";
                    if (pr.IsSuccessStatusCode && ct.StartsWith("image/"))
                    {
                        await pr.Content.LoadIntoBufferAsync(6_000_000);
                        byte[] img = await pr.Content.ReadAsByteArrayAsync();
                        if (img != null && img.Length > 200)
                            System.IO.File.WriteAllBytes(PosterPath(hash), img);
                    }
                }
                catch { }
            }

            return Json(new { success = true, has_poster = System.IO.File.Exists(PosterPath(hash)) });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] save: " + ex);
            return Json(new { success = false, error = "internal error" });
        }
    }
    #endregion

    #region /qdl/poster — отдать локально закэшированный постер
    [HttpGet, AllowAnonymous]
    [Route("qdl/poster")]
    public ActionResult Poster(string hash)
    {
        if (!ValidHash(hash)) return BadRequest();
        string p = PosterPath(hash);
        if (!System.IO.File.Exists(p)) return NotFound();
        return PhysicalFile(p, "image/jpeg");
    }
    #endregion

    #region /qdl/hls — HLS-транскод для браузера (звук EAC3/AC3/DTS → AAC, видео copy)
    static readonly ConcurrentDictionary<string, byte> _hlsRunning = new();

    [HttpGet, AllowAnonymous]
    [Route("qdl/hls/{key}/{file}")]
    async public Task<ActionResult> Hls(string key, string file)
    {
        var mk = Regex.Match(key ?? "", "^([0-9a-fA-F]{40}|[0-9A-Za-z]{32})_(-?\\d+)$");
        if (!mk.Success) return BadRequest();
        if (!Regex.IsMatch(file ?? "", "^(playlist\\.m3u8|seg\\d{1,6}\\.ts)$")) return BadRequest();

        string hash = mk.Groups[1].Value;
        int index = int.Parse(mk.Groups[2].Value);
        string dir = Path.Combine(ModInit.conf.hlsPath, key);
        string target = Path.Combine(dir, file);

        if (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            if (!System.IO.File.Exists(target)) return NotFound();
            return PhysicalFile(target, "video/mp2t", enableRangeProcessing: true);
        }

        // playlist.m3u8 — генерим при первом запросе
        if (!System.IO.File.Exists(target))
        {
            string src;
            try { using (var c = await qbit()) src = await ResolveFile(c, hash, index); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] hls resolve: " + ex); return Json(new { error = "internal error" }); }
            if (src == null) return NotFound();

            CleanupHls();
            StartHls(key, src, dir);

            // ждём появления плейлиста + первого сегмента (event-playlist растёт по мере транскода)
            for (int i = 0; i < 60; i++)
            {
                if (System.IO.File.Exists(target) && Directory.Exists(dir) && Directory.GetFiles(dir, "seg*.ts").Length >= 1) break;
                await Task.Delay(500);
            }
            if (!System.IO.File.Exists(target)) return StatusCode(503);
        }

        return PhysicalFile(target, "application/vnd.apple.mpegurl");
    }

    static void StartHls(string key, string src, string dir)
    {
        if (!_hlsRunning.TryAdd(key, 1)) return;   // уже генерится
        try
        {
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo
            {
                FileName = ModInit.conf.ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "-y", "-i", src,
                "-map", "0:v:0?", "-map", "0:a:0?",
                "-c:v", "copy",                    // видео не трогаем (AVC браузер играет)
                "-c:a", "aac", "-ac", "2", "-b:a", "256k",   // звук EAC3/AC3/DTS → AAC stereo
                "-f", "hls", "-hls_time", "6", "-hls_playlist_type", "event",
                "-hls_flags", "independent_segments",
                "-hls_segment_filename", Path.Combine(dir, "seg%05d.ts"),
                Path.Combine(dir, "playlist.m3u8")
            }) psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            _ = Task.Run(async () =>
            {
                try { _ = p.StandardError.ReadToEndAsync(); _ = p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); }
                catch { }
                _hlsRunning.TryRemove(key, out _);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] hls start: " + ex);
            _hlsRunning.TryRemove(key, out _);
        }
    }

    // не даём HLS-кэшу (дублирует видео) разрастаться: при превышении капа чистим старые папки
    static void CleanupHls()
    {
        try
        {
            string root = ModInit.conf.hlsPath;
            if (!Directory.Exists(root)) return;
            long cap = Math.Max(1, ModInit.conf.hlsCacheCapGb) * 1024L * 1024 * 1024;

            var list = new List<(DirectoryInfo d, long size, DateTime atime)>();
            long total = 0;
            foreach (var d in new DirectoryInfo(root).GetDirectories())
            {
                long s = 0; DateTime at = d.CreationTimeUtc;
                foreach (var f in d.GetFiles()) { s += f.Length; if (f.LastWriteTimeUtc > at) at = f.LastWriteTimeUtc; }
                total += s; list.Add((d, s, at));
            }
            if (total <= cap) return;

            list.Sort((a, b) => a.atime.CompareTo(b.atime));   // старые первыми
            foreach (var it in list)
            {
                if (total <= cap) break;
                if (_hlsRunning.ContainsKey(it.d.Name)) continue;   // активные не трогаем
                try { it.d.Delete(true); total -= it.size; } catch { }
            }
        }
        catch { }
    }
    #endregion

    #region helpers
    static string MetaPath(string hash) => Path.Combine(ModInit.conf.cachePath, "meta", hash + ".json");
    static string PosterPath(string hash) => Path.Combine(ModInit.conf.cachePath, "img", hash + ".jpg");

    // постер не должен указывать на loopback/приватную сеть (анти-SSRF для внешних картинок)
    static bool IsPrivateHost(Uri u)
    {
        string h = u.Host.ToLowerInvariant();
        if (h == "localhost" || h == "127.0.0.1" || h == "::1" || h == "0.0.0.0") return true;
        if (System.Net.IPAddress.TryParse(u.Host, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || b[0] == 127;
            if (ip.IsIPv6LinkLocal || System.Net.IPAddress.IsLoopback(ip)) return true;
        }
        return false;
    }

    // Разрешаем фетчить только собственный JacRed-резолвер (loopback / наш listen-хост, наш порт)
    bool IsSelfResolver(Uri u)
    {
        if (u == null) return false;
        if (u.Scheme != "http" && u.Scheme != "https") return false;
        if (u.Port != CoreInit.conf.listen.port) return false;

        string h = u.Host.ToLowerInvariant();
        if (h == "127.0.0.1" || h == "localhost" || h == "::1") return true;
        if (!string.IsNullOrEmpty(CoreInit.conf.listen.localhost) && h == CoreInit.conf.listen.localhost.ToLowerInvariant()) return true;
        try { if (h == new Uri(host).Host.ToLowerInvariant()) return true; } catch { }
        return false;
    }

    // .torrent — это bencode-словарь: первый значимый байт = 'd' (0x64).
    static bool LooksLikeTorrent(byte[] data)
    {
        if (data == null || data.Length < 50) return false;
        int i = 0;
        while (i < data.Length && (data[i] == (byte)' ' || data[i] == (byte)'\t' || data[i] == (byte)'\r' || data[i] == (byte)'\n')) i++;
        return i < data.Length && data[i] == 0x64;
    }
    #endregion
}
