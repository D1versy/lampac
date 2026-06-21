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
using System.Linq;
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
    static async Task<HttpClient> Qbit()
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

    #region /qdl/search — раздачи через нативный индексатор Lampa (правильный фильм + все трекеры)
    [HttpGet, AllowAnonymous]
    [Route("qdl/search")]
    async public Task<ActionResult> Search(string query, string title = null, string title_original = null,
                                           int year = 0, int is_serial = -1, string apikey = null)
    {
        string search = !string.IsNullOrWhiteSpace(query) ? query
                      : !string.IsNullOrWhiteSpace(title) ? title : title_original;
        if (string.IsNullOrWhiteSpace(search))
            return ContentTo("[]", "application/json; charset=utf-8");

        // ВАЖНО: тот же эндпоинт, что использует нативный «Смотреть через торрент» (jackett-совместимый),
        // с ПОЛНЫМ TMDB-контекстом (title+original+year+is_serial) → корректный матчинг именно нужного фильма
        // и опрос ВСЕХ трекеров (rutracker/kinozal/rutor/nnmclub). Старый /api/v1.0/torrents?search= делал
        // лишь текстовый поиск по локальной БД → выдавал саундтреки/однофамильцев (не тот фильм). См. claude/06.
        var sb = new StringBuilder();
        sb.Append($"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}/api/v2.0/indexers/all/results");
        sb.Append("?apikey=").Append(HttpUtility.UrlEncode(apikey ?? ""));   // не настроен → проверка пропускает любой
        sb.Append("&Query=").Append(HttpUtility.UrlEncode(search));
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("&title=").Append(HttpUtility.UrlEncode(title));
        if (!string.IsNullOrWhiteSpace(title_original)) sb.Append("&title_original=").Append(HttpUtility.UrlEncode(title_original));
        if (year > 0) sb.Append("&year=").Append(year);
        if (is_serial >= 0) sb.Append("&is_serial=").Append(is_serial);

        string raw = await Http.Get(sb.ToString(), timeoutSeconds: 40);

        var result = new JArray();
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                var arr = JObject.Parse(raw)["Results"] as JArray;
                if (arr != null)
                {
                    var seen = new HashSet<string>();
                    foreach (var t in arr)
                    {
                        string mag = t.Value<string>("MagnetUri");
                        string link = t.Value<string>("Link");
                        if (string.IsNullOrWhiteSpace(mag) && string.IsNullOrWhiteSpace(link)) continue;   // нечего качать

                        string ttl = t.Value<string>("Title") ?? "";
                        string dedupe = !string.IsNullOrWhiteSpace(mag) ? MagnetHash(mag) : link;          // дедуп по btih / parselink
                        if (!string.IsNullOrEmpty(dedupe) && !seen.Add(dedupe)) continue;

                        result.Add(new JObject
                        {
                            ["title"] = ttl,
                            ["magnet"] = mag,
                            ["parselink"] = link,
                            ["tracker"] = t.Value<string>("Tracker"),
                            ["sid"] = t.Value<int?>("Seeders") ?? 0,
                            ["size"] = HumanSize(t.Value<long?>("Size") ?? 0),
                            ["quality"] = QualityFromTitle(ttl)
                        });
                    }
                }
            }
            catch { }
        }

        // самые «живые» раздачи сверху (надёжнее докачиваются)
        var sorted = new JArray(result.OrderByDescending(x => x.Value<int?>("sid") ?? 0));
        return ContentTo(sorted.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
    }

    static string HumanSize(long b)
    {
        if (b <= 0) return "";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double s = b; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return (i >= 3 ? s.ToString("0.0") : s.ToString("0")) + " " + u[i];
    }
    static int QualityFromTitle(string t)
    {
        var m = Regex.Match(t ?? "", "(2160|1080|720|480)p?", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
    #endregion

    #region /qdl/add — добавить magnet/.torrent в qBittorrent (резолв parselink при необходимости)
    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/add")]
    async public Task<ActionResult> Add(string magnet = null, string parselink = null, string title = null, string query = null)
    {
        try
        {
            // link: настоящий "magnet:?...", либо URL-резолвер JacRed (parselink).
            // Резолвер может отдать: 302→magnet (rutracker/kinozal/nnm), magnet в теле, или .torrent-файл.
            string link = !string.IsNullOrWhiteSpace(magnet) ? magnet : parselink;
            string origLink = link;                  // исходный указатель на раздачу (для слежения)
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

            using var c = await Qbit();
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

            // сохраняем исходный указатель на раздачу — нужен для слежения за сериалом (пере-резолв)
            if (ok && !string.IsNullOrEmpty(hash) && !string.IsNullOrWhiteSpace(origLink))
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "links"));
                    System.IO.File.WriteAllText(LinkPath(hash), new JObject { ["link"] = origLink, ["query"] = query }.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { }
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
            using var c = await Qbit();
            string raw = await c.GetStringAsync($"/api/v2/torrents/info?category={HttpUtility.UrlEncode(ModInit.conf.category)}&sort=added_on&reverse=true");

            var watched = new HashSet<string>();
            foreach (var w in LoadWatch()) { var wh = w.Value<string>("hash"); if (!string.IsNullOrEmpty(wh)) watched.Add(wh); }

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
                    ["has_poster"] = ValidHash(h) && System.IO.File.Exists(PosterPath(h)),
                    ["watched"] = watched.Contains(h)
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
            using var c = await Qbit();
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
            using var c = await Qbit();
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
    static async Task<string> ResolveFile(HttpClient c, string hash, int index)
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
            using var c = await Qbit();
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
    static readonly ConcurrentDictionary<string, DateTime> _hlsFailed = new();   // негатив-кэш упавших ffmpeg
    static readonly ConcurrentDictionary<string, DateTime> _hlsTouch = new();    // последняя активность (защита от удаления при просмотре)
    static readonly TimeSpan _hlsFailTtl = TimeSpan.FromMinutes(3);
    static readonly TimeSpan _hlsTouchTtl = TimeSpan.FromMinutes(30);

    [HttpGet, AllowAnonymous]
    [Route("qdl/hls/{key}/{file}")]
    async public Task<ActionResult> Hls(string key, string file)
    {
        var mk = Regex.Match(key ?? "", "^([0-9a-fA-F]{40}|[0-9A-Za-z]{32})_(-?\\d+)(?:_(o|e\\d+|d[0-9a-f]{8}|f\\d+))?$");
        if (!mk.Success) return BadRequest();
        if (!Regex.IsMatch(file ?? "", "^(playlist\\.m3u8|seg\\d{1,6}\\.ts)$")) return BadRequest();

        string hash = mk.Groups[1].Value;
        int index = int.Parse(mk.Groups[2].Value);
        string audio = mk.Groups[3].Success ? mk.Groups[3].Value : "o";   // o=ориг, eN=встроенная дорожка, fN=внешний файл-озвучка
        string dir = Path.Combine(ModInit.conf.hlsPath, key);
        string target = Path.Combine(dir, file);

        try
        {
            _hlsTouch[key] = DateTime.UtcNow;   // отметка активности (и .ts, и .m3u8) → CleanupHls не удалит используемую папку

            // сегмент: отдаём с FileShare.ReadWrite (ffmpeg может ещё держать соседние файлы)
            if (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                if (!System.IO.File.Exists(target)) return NotFound();
                var ts = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return File(ts, "video/mp2t", enableRangeProcessing: true);
            }

            // playlist.m3u8 — генерим при первом запросе
            if (!System.IO.File.Exists(target))
            {
                // негатив-кэш: ffmpeg недавно упал на этом ключе → не спамим перезапуском
                if (_hlsFailed.TryGetValue(key, out var failedAt))
                {
                    if (DateTime.UtcNow - failedAt < _hlsFailTtl) return StatusCode(503);
                    _hlsFailed.TryRemove(key, out _);
                }

                string src, extAudio = null, audioMap = "0:a:0?";
                using (var c = await Qbit())
                {
                    src = await ResolveFile(c, hash, index);
                    if (src == null) return NotFound();
                    if (audio.StartsWith("e")) audioMap = "0:a:" + audio.Substring(1);       // встроенная дорожка N
                    else if (audio.StartsWith("d"))                                            // внешняя озвучка по СТУДИИ — файл для ЭТОЙ серии
                    {
                        extAudio = await ResolveDubFile(c, hash, index, audio);
                        if (!string.IsNullOrEmpty(extAudio)) audioMap = "1:a:0";
                    }
                    else if (audio.StartsWith("f"))                                            // back-compat: внешний файл по индексу
                    {
                        extAudio = await ResolveFile(c, hash, int.Parse(audio.Substring(1)));
                        if (!string.IsNullOrEmpty(extAudio)) audioMap = "1:a:0";
                    }
                }

                CleanupHls();
                StartHls(key, dir, src, extAudio, audioMap);

                // ждём появления плейлиста + первого сегмента (event-playlist растёт по мере транскода)
                for (int i = 0; i < 60; i++)
                {
                    if (System.IO.File.Exists(target) && Directory.Exists(dir) && Directory.GetFiles(dir, "seg*.ts").Length >= 1) break;
                    if (!_hlsRunning.ContainsKey(key) && !System.IO.File.Exists(target)) break;   // ffmpeg вышел без результата → не ждём 30с
                    await Task.Delay(500);
                }
                if (!System.IO.File.Exists(target)) { _hlsFailed[key] = DateTime.UtcNow; return StatusCode(503); }
            }

            // ffmpeg продолжает ДОПИСЫВАТЬ playlist.m3u8 → читаем с FileShare.ReadWrite (иначе sharing violation → 500)
            string m3u8;
            using (var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                m3u8 = await sr.ReadToEndAsync();
            return Content(m3u8, "application/vnd.apple.mpegurl");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] hls: " + ex);
            return StatusCode(503);
        }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/audio")]
    async public Task<ActionResult> Audio(string hash, int index = -1)
    {
        if (!ValidHash(hash)) return BadRequest();
        try
        {
            using var c = await Qbit();
            string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
            var files = JArray.Parse(filesRaw);

            // найти видеофайл (по index или самый большой)
            JToken vf = null;
            if (index >= 0) foreach (var f in files) if ((f.Value<int?>("index") ?? -1) == index) { vf = f; break; }
            if (vf == null)
            {
                long max = -1;
                foreach (var f in files)
                {
                    string n = f.Value<string>("name") ?? "";
                    if (!Regex.IsMatch(n, "\\.(mkv|mp4|avi|ts|m4v|webm|mov)$", RegexOptions.IgnoreCase)) continue;
                    long s = f.Value<long?>("size") ?? 0; if (s > max) { max = s; vf = f; }
                }
            }
            if (vf == null) return ContentTo("[]", "application/json; charset=utf-8");

            string vname = (vf.Value<string>("name") ?? "").Replace('\\', '/');
            string vbase = Path.GetFileNameWithoutExtension(vname.Substring(vname.LastIndexOf('/') + 1));
            int vindex = vf.Value<int?>("index") ?? index;

            var opts = new JArray();

            // встроенные аудиодорожки (ffprobe видео)
            string vpath = await ResolveFile(c, hash, vindex);
            foreach (var a in ProbeAudio(vpath)) opts.Add(a);

            // внешние озвучки — устойчивый матчер (студия + серия, много фолбэков; claude/06 §T)
            foreach (var d in DubsForVideo(files, vf))
                opts.Add(new JObject { ["id"] = d.id, ["label"] = d.label, ["lang"] = "rus" });

            return ContentTo(opts.ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] audio: " + ex); return ContentTo("[]", "application/json; charset=utf-8"); }
    }

    static List<JObject> ProbeAudio(string path)
    {
        var res = new List<JObject>();
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return res;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ModInit.conf.ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", "a", path })
                psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            var streams = JObject.Parse(outp)["streams"] as JArray ?? new JArray();
            int ord = 0;
            foreach (var s in streams)
            {
                var tags = s["tags"] as JObject;
                string lang = tags?.Value<string>("language") ?? "";
                string title = tags?.Value<string>("title");
                string label = !string.IsNullOrWhiteSpace(title) ? title : LangName(lang);
                res.Add(new JObject { ["id"] = "e" + ord, ["label"] = label + " (ориг.)", ["lang"] = lang });
                ord++;
            }
        }
        catch { }
        return res;
    }

    static string LangName(string l)
    {
        switch ((l ?? "").ToLowerInvariant())
        {
            case "jpn": case "ja": return "Японский";
            case "eng": case "en": return "Английский";
            case "rus": case "ru": return "Русский";
            case "": return "Оригинал";
            default: return l;
        }
    }

    // ───────── Устойчивый матчер озвучек (видео↔внешние аудио). См. claude/06 §T ─────────
    sealed class Ep { public string kind; public int season = -1; public int ep = -1; public int ep2 = -1; public bool any => kind != null || ep >= 0; }

    static readonly Regex[] _noiseRx =
    {
        new Regex(@"(?i)\b(?:19|20)\d{2}\b"),
        new Regex(@"(?i)\b\d{3,4}[pi]\b"),
        new Regex(@"(?i)\b(?:2160|1080|720|480|576|360)\b"),
        new Regex(@"(?i)\b(?:x?264|x?265|h\.?26[45]|hevc|avc|av1|vp9|xvid|divx)\b"),
        new Regex(@"(?i)\b(?:10|8)\s?bit\b"),
        new Regex(@"(?i)\b(?:aac|ac3|eac3|dts(?:-hd)?|flac|opus|truehd|mp3)\b"),
        new Regex(@"(?i)\b\d+(?:\.\d+)?\s?(?:fps|kbps|mbps|hz|khz)\b"),
        new Regex(@"(?i)\b(?:bdrip|bluray|webdl|web-?dl|webrip|hdtv|dvdrip|remux|uhd)\b"),
        new Regex(@"(?i)\b[257]\.[01]\b"),
        new Regex(@"(?i)\b\d{3,4}x\d{3,4}\b"),
    };
    static string StripNoise(string s) { foreach (var r in _noiseRx) s = r.Replace(s, " "); return s; }

    static Ep ParseEp(string baseName)
    {
        string s = baseName ?? "";
        Match m;
        if ((m = Regex.Match(s, @"(?i)\b(OVA|ONA|OAD)\s*0*(\d{1,2})?\b")).Success) return new Ep { kind = m.Groups[1].Value.ToUpperInvariant(), ep = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : -1 };
        if ((m = Regex.Match(s, @"(?i)\b(?:SP|Special|Спецвыпуск|Спешл)\s*0*(\d{1,2})?\b")).Success) return new Ep { kind = "SP", ep = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : -1 };
        if ((m = Regex.Match(s, @"(?i)\b(NCOP|NCED|Creditless\s*OP|Creditless\s*ED|Clean\s*Opening|Clean\s*Ending)\b")).Success) { string k = m.Value.ToUpperInvariant(); return new Ep { kind = (k.Contains("ED") || k.Contains("ENDING")) ? "NCED" : "NCOP" }; }
        if ((m = Regex.Match(s, @"(?i)(?<![A-Za-z])(OP|ED|PV|CM|Menu|Trailer|Preview|Teaser)\s*0*(\d{1,2})?(?![A-Za-z])")).Success) return new Ep { kind = m.Groups[1].Value.ToUpperInvariant() };
        string c = StripNoise(s);
        if ((m = Regex.Match(c, @"(?i)(?:S\d{1,2})?E0*(\d{1,3})\s*-\s*E?0*(\d{1,3})")).Success) return new Ep { kind = "RANGE", ep = int.Parse(m.Groups[1].Value), ep2 = int.Parse(m.Groups[2].Value) };
        if ((m = Regex.Match(c, @"(?:^|[\s._\[-])0*(\d{1,3})\s*-\s*0*(\d{1,3})(?=[\s._\]-]|$)")).Success) return new Ep { kind = "RANGE", ep = int.Parse(m.Groups[1].Value), ep2 = int.Parse(m.Groups[2].Value) };
        if ((m = Regex.Match(c, @"(?i)(?<![A-Za-z0-9])S(\d{1,2})E0*(\d{1,3})(?!\d)")).Success) return new Ep { season = int.Parse(m.Groups[1].Value), ep = int.Parse(m.Groups[2].Value) };
        if ((m = Regex.Match(c, @"(?i)(?<![A-Za-z0-9])(\d{1,2})x0*(\d{1,3})(?!\d)")).Success) return new Ep { season = int.Parse(m.Groups[1].Value), ep = int.Parse(m.Groups[2].Value) };
        if ((m = Regex.Match(c, @"(?i)(?<![A-Za-z0-9])E[Pp]?\.?\s*0*(\d{1,3})(?!\d)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"(?i)(?:серия|episode|эпизод|вып(?:уск)?)\s*[№#]?\s*0*(\d{1,3})(?!\d)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"#0*(\d{1,3})(?!\d)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"(?:^|\s)-\s+0*(\d{1,3})(?=\s|$|\[|\()")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"\[\s*0*(\d{1,3})\s*\]")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c, @"(?:^|[._ ])0*(\d{1,3})(?=[._ \[]|$)")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        if ((m = Regex.Match(c.Trim(), @"^0*(\d{1,3})$")).Success) return new Ep { ep = int.Parse(m.Groups[1].Value) };
        return new Ep();
    }

    static bool EpEqual(Ep v, Ep a)
    {
        if (v == null || a == null || !v.any || !a.any) return false;
        if (v.kind != a.kind) return false;
        if (v.kind == "RANGE") return v.ep == a.ep && v.ep2 == a.ep2;
        if (v.ep != a.ep) return false;
        if (v.season >= 0 && a.season >= 0 && v.season != a.season) return false;
        return v.kind != null || v.ep >= 0;
    }

    static readonly Regex _genericFolderRx = new Regex(@"(?i)^(rus[ ._-]?sound[s]?|sound[s]?|audio|звук|озвучк\w*|voice|dub|дубляж|переводы?|дорожк\w*|tracks?|rus|русск\w*)$");
    static bool IsGenericFolder(string name) => string.IsNullOrWhiteSpace(name) || _genericFolderRx.IsMatch(name.Trim());

    static string CleanStudio(string s)
    {
        s = Regex.Replace(s ?? "", @"[._]+", " ");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '_', '.', '[', ']', '(', ')');
        return string.IsNullOrWhiteSpace(s) ? "Озвучка" : s;
    }

    static string StudioId(string studio)
    {
        string norm = Regex.Replace((studio ?? "").ToLowerInvariant(), @"[\s._\-]+", "");
        uint h = 2166136261;
        foreach (char ch in norm) { h ^= ch; h *= 16777619; }
        return "d" + h.ToString("x8");
    }

    // студия озвучки: суффикс после имени видео → НЕ-generic подпапка → имя без хвостового номера → [скобки]
    static string StudioOf(string fullPath, string videoBase)
    {
        string p = (fullPath ?? "").Replace('\\', '/');
        string fbase = Path.GetFileNameWithoutExtension(p.Substring(p.LastIndexOf('/') + 1));

        if (fbase.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase) && fbase.Length > videoBase.Length)
        {
            string suf = fbase.Substring(videoBase.Length).Trim('.', ' ', '-', '_', '[', ']', '(', ')');
            if (!string.IsNullOrWhiteSpace(suf)) return CleanStudio(suf);
        }
        var parts = p.Split('/');
        for (int i = parts.Length - 2; i >= 1; i--)
            if (!IsGenericFolder(parts[i])) return CleanStudio(parts[i]);

        // остаток после общего префикса с видео (после вырезания тех-шума) — устойчиво к разным тегам качества
        string na = Regex.Replace(Regex.Replace(StripNoise(fbase), @"\[\s*\]|\(\s*\)", " "), @"\s{2,}", " ").Trim();
        string nv = Regex.Replace(Regex.Replace(StripNoise(videoBase), @"\[\s*\]|\(\s*\)", " "), @"\s{2,}", " ").Trim();
        int kk = 0; while (kk < na.Length && kk < nv.Length && char.ToLowerInvariant(na[kk]) == char.ToLowerInvariant(nv[kk])) kk++;
        string rem = Regex.Replace(na.Substring(kk), @"(?i)(S\d{1,2}E\d{1,3}|\d{1,2}x\d{1,3}|EP?\.?\d{1,3}|OVA\s*\d*|SP\s*\d*|NCOP|NCED|\d{1,3})", " ");
        rem = Regex.Replace(rem, @"\s{2,}", " ").Trim(' ', '-', '_', '.', '[', ']', '(', ')');
        if (!string.IsNullOrWhiteSpace(rem) && !Regex.IsMatch(rem, @"^\d+$")) return CleanStudio(rem);

        var b = Regex.Match(fbase, @"\[([^\]]+)\]");
        if (b.Success && !IsGenericFolder(b.Groups[1].Value)) return CleanStudio(b.Groups[1].Value);
        return "Озвучка";
    }

    static bool NormStarts(string a, string b)
    {
        a = (a ?? "").Replace('_', ' ').Replace('.', ' ').Trim();
        b = (b ?? "").Replace('_', ' ').Replace('.', ' ').Trim();
        return b.Length > 0 && a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    static readonly Regex _audioExtRx = new Regex(@"(?i)\.(mka|aac|ac3|eac3|dts|flac|opus|m4a|wav|mp3|thd)$");
    static readonly Regex _videoExtRx = new Regex(@"(?i)\.(mkv|mp4|avi|ts|m2ts|webm|mov|m4v)$");

    static string BaseNoExt(JToken f) { string n = (f.Value<string>("name") ?? "").Replace('\\', '/'); return Path.GetFileNameWithoutExtension(n.Substring(n.LastIndexOf('/') + 1)); }

    static int NaturalCompare(string a, string b)
    {
        a = a ?? ""; b = b ?? ""; int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a.Substring(si, i - si).TrimStart('0'); string nb = b.Substring(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int cmp = string.CompareOrdinal(na, nb); if (cmp != 0) return cmp;
            }
            else { int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j])); if (cmp != 0) return cmp; i++; j++; }
        }
        return (a.Length - i) - (b.Length - j);
    }

    // для видеофайла → список озвучек (studioId, label, индекс аудиофайла)
    static List<(string id, string label, int idx)> DubsForVideo(JArray files, JToken video)
    {
        var res = new List<(string, string, int)>();
        if (video == null) return res;
        string vbase = BaseNoExt(video);
        var vEp = ParseEp(vbase);

        var videos = new List<JToken>(); var audios = new List<JToken>();
        foreach (var f in files)
        {
            string n = f.Value<string>("name") ?? "";
            if (_videoExtRx.IsMatch(n)) videos.Add(f);
            else if (_audioExtRx.IsMatch(n)) audios.Add(f);
        }
        bool isMovie = videos.Count == 1;

        var byStudio = new Dictionary<string, List<JToken>>();
        var labelOf = new Dictionary<string, string>();
        foreach (var a in audios)
        {
            string studio = StudioOf(a.Value<string>("name") ?? "", vbase);
            string id = StudioId(studio);
            if (!byStudio.TryGetValue(id, out var lst)) { lst = new List<JToken>(); byStudio[id] = lst; labelOf[id] = studio; }
            lst.Add(a);
        }

        videos.Sort((x, y) => NaturalCompare(x.Value<string>("name"), y.Value<string>("name")));
        int vPos = videos.FindIndex(x => (x.Value<int?>("index") ?? -2) == (video.Value<int?>("index") ?? -1));

        foreach (var kv in byStudio)
        {
            var lst = kv.Value;
            JToken best = null; int bestRank = 0;
            foreach (var a in lst)
            {
                var aEp = ParseEp(BaseNoExt(a));
                int rank = 0;
                if (vEp.any && aEp.any && EpEqual(vEp, aEp)) rank = 6;          // A: точная серия
                else if (NormStarts(BaseNoExt(a), vbase)) rank = 5;             // B: префикс имени
                else if (isMovie && !vEp.any) rank = 3;                         // D: фильм — любая дорожка
                else if (!aEp.any && lst.Count == 1) rank = 2;                  // E: season-pack (1 файл студии без серии)
                if (rank > bestRank) { best = a; bestRank = rank; }
            }
            if (best == null && lst.Count == videos.Count && vPos >= 0)         // F: позиционный (равные счётчики, без серий)
            {
                bool noeps = true; foreach (var a in lst) if (ParseEp(BaseNoExt(a)).any) { noeps = false; break; }
                if (noeps) { lst.Sort((x, y) => NaturalCompare(x.Value<string>("name"), y.Value<string>("name"))); if (vPos < lst.Count) best = lst[vPos]; }
            }
            if (best != null) res.Add((kv.Key, labelOf[kv.Key], best.Value<int?>("index") ?? -1));
        }
        return res;
    }

    static JToken FindVideo(JArray files, int index)
    {
        if (index >= 0) foreach (var f in files) if ((f.Value<int?>("index") ?? -1) == index) return f;
        JToken vf = null; long max = -1;
        foreach (var f in files) { if (!_videoExtRx.IsMatch(f.Value<string>("name") ?? "")) continue; long s = f.Value<long?>("size") ?? 0; if (s > max) { max = s; vf = f; } }
        return vf;
    }

    // найти файл-озвучку выбранной студии именно для серии videoIndex
    static async Task<string> ResolveDubFile(HttpClient c, string hash, int videoIndex, string dubId)
    {
        string filesRaw = await c.GetStringAsync($"/api/v2/torrents/files?hash={HttpUtility.UrlEncode(hash)}");
        var files = JArray.Parse(filesRaw);
        var video = FindVideo(files, videoIndex);
        if (video == null) return null;
        foreach (var d in DubsForVideo(files, video))
            if (d.id == dubId) return await ResolveFile(c, hash, d.idx);
        return null;
    }

    static void StartHls(string key, string dir, string videoPath, string extAudio, string audioMap)
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
            var args = new List<string> { "-y", "-i", videoPath };
            if (!string.IsNullOrEmpty(extAudio)) { args.Add("-i"); args.Add(extAudio); }   // внешняя озвучка — вторым входом
            args.AddRange(new[]
            {
                "-map", "0:v:0?", "-map", string.IsNullOrEmpty(audioMap) ? "0:a:0?" : audioMap,
                "-c:v", "copy",                    // видео не трогаем (AVC браузер играет)
                "-c:a", "aac", "-ac", "2", "-b:a", "256k",   // звук → AAC stereo
                "-f", "hls", "-hls_time", "6", "-hls_playlist_type", "event",
                "-hls_flags", "independent_segments",
                "-hls_segment_filename", Path.Combine(dir, "seg%05d.ts"),
                Path.Combine(dir, "playlist.m3u8")
            });
            foreach (var a in args) psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            _ = Task.Run(async () =>
            {
                string err = "";
                try
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    err = await errTask; await outTask;
                }
                catch { }
                _hlsRunning.TryRemove(key, out _);
                try
                {
                    bool ok = p.HasExited && p.ExitCode == 0 && System.IO.File.Exists(Path.Combine(dir, "playlist.m3u8"));
                    if (!ok)
                    {
                        _hlsFailed[key] = DateTime.UtcNow;
                        Console.WriteLine("[QbitDownload] hls ffmpeg failed key=" + key + " exit=" + (p.HasExited ? p.ExitCode.ToString() : "?") + ": " + (err ?? "").Trim());
                    }
                    else _hlsFailed.TryRemove(key, out _);
                }
                catch { }
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

            var now = DateTime.UtcNow;
            list.Sort((a, b) => a.atime.CompareTo(b.atime));   // старые первыми
            foreach (var it in list)
            {
                if (total <= cap) break;
                if (_hlsRunning.ContainsKey(it.d.Name)) continue;   // активный транскод не трогаем
                if (_hlsTouch.TryGetValue(it.d.Name, out var t) && (now - t) < _hlsTouchTtl) continue;   // активное воспроизведение не трогаем
                try { it.d.Delete(true); total -= it.size; _hlsTouch.TryRemove(it.d.Name, out _); _hlsFailed.TryRemove(it.d.Name, out _); } catch { }
            }
        }
        catch { }
    }
    #endregion

    #region /qdl/watch — слежение за сериалами (авто-докачка новых серий)
    static string WatchFile => Path.Combine(ModInit.conf.cachePath, "watch.json");
    static string LinkPath(string hash) => Path.Combine(ModInit.conf.cachePath, "links", hash + ".json");
    static readonly object _watchLock = new();

    static JArray LoadWatch()
    {
        try { if (System.IO.File.Exists(WatchFile)) return JArray.Parse(System.IO.File.ReadAllText(WatchFile)); } catch { }
        return new JArray();
    }
    static void SaveWatch(JArray a)
    {
        try { Directory.CreateDirectory(ModInit.conf.cachePath); System.IO.File.WriteAllText(WatchFile, a.ToString(Newtonsoft.Json.Formatting.None)); } catch { }
    }

    [HttpGet, HttpPost, AllowAnonymous]
    [Route("qdl/watch")]
    public ActionResult WatchAdd(string hash)
    {
        if (!ValidHash(hash)) return BadRequest(new { error = "invalid hash" });
        try
        {
            string link = null, query = null;
            if (System.IO.File.Exists(LinkPath(hash)))
            {
                var lj = JObject.Parse(System.IO.File.ReadAllText(LinkPath(hash)));
                link = lj.Value<string>("link"); query = lj.Value<string>("query");
            }
            if (string.IsNullOrWhiteSpace(link))
                return Json(new { success = false, error = "no link" });   // перекачай раздачу, чтобы включить слежение

            JObject meta = System.IO.File.Exists(MetaPath(hash)) ? JObject.Parse(System.IO.File.ReadAllText(MetaPath(hash))) : new JObject();
            lock (_watchLock)
            {
                var a = LoadWatch();
                foreach (var m in a) if (m.Value<string>("hash") == hash) return Json(new { success = true });
                a.Add(new JObject { ["hash"] = hash, ["link"] = link, ["query"] = query, ["id"] = meta.Value<int?>("id"), ["title"] = meta.Value<string>("title") });
                SaveWatch(a);
            }
            return Json(new { success = true });
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] watch add: " + ex); return Json(new { success = false, error = "internal error" }); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/watch/remove")]
    public ActionResult WatchRemove(string hash)
    {
        try
        {
            lock (_watchLock)
            {
                var a = LoadWatch(); var b = new JArray();
                foreach (var m in a) if (m.Value<string>("hash") != hash) b.Add(m);
                SaveWatch(b);
            }
            return Json(new { success = true });
        }
        catch (Exception ex) { Console.WriteLine("[QbitDownload] watch remove: " + ex); return Json(new { success = false }); }
    }

    [HttpGet, AllowAnonymous]
    [Route("qdl/watch/list")]
    public ActionResult WatchListAll() => ContentTo(LoadWatch().ToString(Newtonsoft.Json.Formatting.None), "application/json; charset=utf-8");

    [HttpGet, AllowAnonymous]
    [Route("qdl/watch/check")]
    async public Task<ActionResult> WatchCheckNow() { int n = await CheckWatches(); return Json(new { success = true, regrabbed = n }); }

    // Фоновая проверка: пере-резолвим раздачу; если infohash изменился (добавили серии) —
    // до-добавляем новую раздачу (qBit перепроверит файлы и дотянет только новые серии).
    public static async Task<int> CheckWatches()
    {
        int regrabbed = 0;
        JArray list; lock (_watchLock) { list = LoadWatch(); }
        bool changed = false;

        foreach (var m in list)
        {
            try
            {
                string link = m.Value<string>("link");
                string curHash = m.Value<string>("hash");
                if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(curHash)) continue;

                string magnet = await ResolveMagnetStatic(link);
                string newHash = MagnetHash(magnet);
                if (string.IsNullOrWhiteSpace(newHash) || newHash.Equals(curHash, StringComparison.OrdinalIgnoreCase)) continue;

                using var c = await Qbit();
                if (!await QbitAddMagnet(c, magnet)) continue;     // qBit перепроверит и дотянет новые серии

                try   // убрать старую раздачу (файлы оставить)
                {
                    var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("hashes", curHash), new KeyValuePair<string, string>("deleteFiles", "false") });
                    await c.PostAsync("/api/v2/torrents/delete", form);
                }
                catch { }

                MigrateCache(curHash, newHash);
                m["hash"] = newHash;
                changed = true; regrabbed++;
                Console.WriteLine("[QbitDownload] watch: re-grab " + m.Value<string>("title") + " " + curHash + "->" + newHash);
            }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] watch item: " + ex); }
        }

        if (changed) lock (_watchLock) { SaveWatch(list); }
        return regrabbed;
    }

    static void MigrateCache(string oldH, string newH)
    {
        void mv(string a, string b) { try { if (System.IO.File.Exists(a)) { Directory.CreateDirectory(Path.GetDirectoryName(b)); System.IO.File.Copy(a, b, true); System.IO.File.Delete(a); } } catch { } }
        mv(MetaPath(oldH), MetaPath(newH));
        mv(PosterPath(oldH), PosterPath(newH));
        mv(LinkPath(oldH), LinkPath(newH));
    }

    static string MagnetHash(string magnet)
    {
        var hm = Regex.Match(magnet ?? "", "btih:([0-9a-fA-F]{40}|[0-9a-zA-Z]{32})", RegexOptions.IgnoreCase);
        return hm.Success ? hm.Groups[1].Value.ToLower() : "";
    }

    // резолв нашего loopback-парселинка в magnet (фоновая проверка, без request-host)
    static async Task<string> ResolveMagnetStatic(string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return link;   // прямой magnet не меняется
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u) || !IsLoopbackSelf(u)) return null;

        using var rh = new HttpClientHandler { AllowAutoRedirect = false };
        using var rc = new HttpClient(rh) { Timeout = TimeSpan.FromSeconds(20) };
        HttpResponseMessage resp = null;
        try
        {
            var current = u;
            for (int hop = 0; hop < 5; hop++)
            {
                resp?.Dispose();
                resp = await rc.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);
                int code = (int)resp.StatusCode; var loc = resp.Headers.Location;
                if (code < 300 || code >= 400 || loc == null) break;
                var next = loc.IsAbsoluteUri ? loc : new Uri(resp.RequestMessage?.RequestUri ?? current, loc);
                if (next.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return next.OriginalString;
                if (!IsLoopbackSelf(next)) break;
                current = next;
            }
            if (resp != null)
            {
                try { await resp.Content.LoadIntoBufferAsync(5_000_000); } catch { return null; }
                var mm = Regex.Match(await resp.Content.ReadAsStringAsync() ?? "", "magnet:\\?[^\"'\\s<]+");
                if (mm.Success) return mm.Value;
            }
        }
        catch { }
        finally { resp?.Dispose(); }
        return null;
    }

    static bool IsLoopbackSelf(Uri u)
    {
        if (u == null || (u.Scheme != "http" && u.Scheme != "https")) return false;
        if (u.Port != CoreInit.conf.listen.port) return false;
        string h = u.Host.ToLowerInvariant();
        if (h == "127.0.0.1" || h == "localhost" || h == "::1") return true;
        if (!string.IsNullOrEmpty(CoreInit.conf.listen.localhost) && h == CoreInit.conf.listen.localhost.ToLowerInvariant()) return true;
        return false;
    }

    static async Task<bool> QbitAddMagnet(HttpClient c, string magnet)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(magnet), "urls" },
            { new StringContent(ModInit.conf.downloadsPath), "savepath" },
            { new StringContent(ModInit.conf.category), "category" }
        };
        var r = await c.PostAsync("/api/v2/torrents/add", content);
        string body = (await r.Content.ReadAsStringAsync())?.Trim() ?? "";
        if ((int)r.StatusCode == 409 || body.Equals("Conflict", StringComparison.OrdinalIgnoreCase)) return true;
        if (!r.IsSuccessStatusCode) return false;
        if (body == "Ok." || body.Length == 0) return true;
        if (body.StartsWith("{")) { try { var j = JObject.Parse(body); return (j.Value<int?>("success_count") ?? 0) > 0 || (j.Value<int?>("pending_count") ?? 0) > 0; } catch { return false; } }
        return false;
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
