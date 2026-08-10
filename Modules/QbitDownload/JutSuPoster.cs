using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Апгрейд постеров jut.su: сеть, очередь, кеш решений.
// Чистая логика сопоставления живёт отдельно — JutSuMatch.cs (она же под тестами).
//
// Цепочка: романдзи с jut.su → Shikimori (точное совпадение) → MAL id → AniList ПО ID
// → обложка 460×690. Замеры, из-за которых всё это затевалось:
//   постер jut.su      186×186, 10–25 КБ  (квадрат — карточка Lampa ждёт 2:3)
//   Shikimori original 240×360, ~20 КБ    (запасной источник)
//   AniList extraLarge 460×690, ~110 КБ   (основной)
//
// 🔥 Главный инвариант: НУЛЕВАЯ РЕГРЕССИЯ. Любая ошибка — сеть, гео, лимит, кривой JSON,
//    неуверенный матч — заканчивается тем, что решения просто нет, и /qdl/jut/poster
//    отдаёт ровно тот же постер jut.su, что и раньше. Ни одного throw наружу.
//
// ⚠️ Грабли:
//  1. shikimori.one отвечает 301 → shikimori.io. Ходим сразу на .io (ключ jutShikimoriHost).
//  2. Shikimori требует User-Agent и держит лимит ~5 rps / 90 rpm → один воркер + пауза
//     jutPosterPaceMs между задачами. Никаких параллельных запросов.
//  3. Отрицательное решение обязано протухать (jutPosterRetryDays): онгоинги и новинки
//     появляются в базах ПОЗЖЕ, чем на jut.su. Вечный «нет» оставил бы их без обложки навсегда.
//  4. Обложка AniList бывает PNG, а не JPEG → тип отдачи определяем по сигнатуре файла,
//     а не по расширению.
//
// Документация: E:\Media-server\claude\jut\02-architecture.md
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region состояние очереди

    sealed class JutPosterJob
    {
        public string slug;
        public string titleRu;
        public string titleOrig;
        public int[] years;
        public string rawPoster;      // постер jut.su, уже разобранный каталогом (экономит загрузку страницы тайтла)
    }

    const int JutPosterQueueCap = 500;

    static readonly ConcurrentQueue<JutPosterJob> _jutPq = new();
    static readonly ConcurrentDictionary<string, byte> _jutPqSeen = new();   // дедуп: слаг уже в очереди/в работе
    static readonly ConcurrentDictionary<string, string> _jutUpCtype = new();
    static readonly ConcurrentQueue<string> _jutPosterRefusals = new();      // последние отказы — для /qdl/jut/diag
    static int _jutPqRunning;

    internal static bool JutPosterOn => JutOn && ModInit.conf?.jutPosterUpgrade != false;

    static string JutMatchPath(string slug) => Path.Combine(JutDir("match"), slug + ".json");

    /// <summary>Путь апгрейженного постера. ⚠️ Отдельный файл от сырого: фолбэк не должен теряться.</summary>
    internal static string JutUpPosterPath(string slug) => Path.Combine(JutDir("img"), slug + ".up.jpg");

    internal static bool JutHasUpPoster(string slug)
    {
        if (!JutPosterOn) return false;
        try { return System.IO.File.Exists(JutUpPosterPath(slug)); } catch { return false; }
    }

    #endregion

    #region кеш решений

    static JObject JutMatchRead(string slug)
    {
        try
        {
            string p = JutMatchPath(slug);
            if (!System.IO.File.Exists(p)) return null;
            return JObject.Parse(System.IO.File.ReadAllText(p));
        }
        catch { return null; }
    }

    static void JutMatchWrite(string slug, JObject data)
    {
        try
        {
            data["ts"] = DateTime.UtcNow.ToString("o");
            System.IO.File.WriteAllText(JutMatchPath(slug), data.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch { }
    }

    /// <summary>
    /// Решение ещё актуально? Положительное — навсегда, отрицательное — только jutPosterRetryDays:
    /// новинки и онгоинги приезжают в базы позже, чем на jut.su.
    /// </summary>
    static bool JutMatchFresh(JObject d)
    {
        if (d == null) return false;
        if (d["ok"]?.Value<bool>() == true) return true;

        if (!DateTime.TryParse(d["ts"]?.Value<string>(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts)) return false;

        int days = Math.Max(1, ModInit.conf?.jutPosterRetryDays ?? 14);
        return DateTime.UtcNow - ts.ToUniversalTime() < TimeSpan.FromDays(days);
    }

    /// <summary>Сырой постер jut.su из решения — чтобы /qdl/jut/poster не тянул страницу тайтла ради URL.</summary>
    internal static string JutRawPosterFromMatch(string slug)
        => JutMatchRead(slug)?["raw"]?.Value<string>();

    #endregion

    #region постановка в очередь

    /// <summary>
    /// Поставить слаг в очередь апгрейда. Вызывается из каталога/поиска/тайтла — там уже
    /// разобраны titleOrig, titleRu и годы, поэтому сопоставление не стоит НИ ОДНОГО
    /// лишнего запроса к jut.su. Полностью неблокирующая: возвращается сразу.
    /// </summary>
    internal static void JutPosterEnqueue(string slug, string titleRu, string titleOrig,
                                          IReadOnlyList<int> years, string rawPoster)
    {
        if (!JutPosterOn || string.IsNullOrEmpty(slug) || !JutSuParse.IsValidSlug(slug)) return;
        if (string.IsNullOrWhiteSpace(titleOrig) && string.IsNullOrWhiteSpace(titleRu)) return;

        try
        {
            if (System.IO.File.Exists(JutUpPosterPath(slug))) return;
            if (JutMatchFresh(JutMatchRead(slug))) return;
            if (_jutPq.Count >= JutPosterQueueCap) return;
            if (!_jutPqSeen.TryAdd(slug, 1)) return;

            _jutPq.Enqueue(new JutPosterJob
            {
                slug = slug,
                titleRu = titleRu,
                titleOrig = titleOrig,
                years = years?.ToArray() ?? Array.Empty<int>(),
                rawPoster = rawPoster
            });
            JutPosterWorkerStart();
        }
        catch { }
    }

    /// <summary>
    /// Пометить карточки версией постера и засеять очередь апгрейда.
    /// 🔥 Вешать это на JutCardsJson НЕЛЬЗЯ: тот вызывается только при СВЕЖЕМ разборе, а
    /// закешированная страница (30 мин) возвращается готовым JSON — тёплый каталог не засеял бы
    /// ничего и никогда. Поэтому декоратор зовётся на ВСЕХ путях возврата, включая кеш и stale.
    /// Это безопасно: JutCacheRead парсит JSON заново на каждое чтение (JutSu.cs:80-97),
    /// так что правка объекта кеш не отравляет.
    /// </summary>
    internal static void JutPosterStamp(JToken payload)
    {
        if (payload is not JObject jo) return;

        try
        {
            // ⚠️ Слаг проверяем ПЕРВЫМ: у тайтла тоже есть items, но там СЕРИИ, а не карточки.
            if (jo["slug"] != null) JutPosterStampOne(jo);
            else if (jo["items"] is JArray items)
            {
                foreach (var it in items)
                    if (it is JObject card) JutPosterStampOne(card);
            }
        }
        catch { }
    }

    static void JutPosterStampOne(JObject c)
    {
        string slug = c["slug"]?.Value<string>();
        if (string.IsNullOrEmpty(slug)) return;

        if (JutHasUpPoster(slug)) { c["pv"] = 1; return; }
        if (!JutPosterOn) return;

        var years = new List<int>();
        if (c["years"] is JArray ya)
            foreach (var y in ya) { int v = y?.Value<int?>() ?? 0; if (v > 0) years.Add(v); }

        JutPosterEnqueue(slug, c["title"]?.Value<string>(), c["original"]?.Value<string>(),
                         years, c["poster"]?.Value<string>());
    }

    /// <summary>
    /// Засеять очередь уже СКАЧАННЫМИ jut-тайтлами. Их в каталоге может не быть годами
    /// (страница 1 показывает новинки), а карточка в «Загрузках» без этого так и осталась бы
    /// с квадратом 186×186. Вся нужная мета уже лежит в meta/&lt;hash&gt;.json — сеть не нужна.
    /// Зовётся один раз на старте, рядом с JutReconcile.
    /// </summary>
    internal static void JutPosterSeedDownloads()
    {
        if (!JutPosterOn) return;
        try
        {
            string dir = Path.Combine(ModInit.conf.cachePath, "meta");
            if (!Directory.Exists(dir)) return;

            int seeded = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var jo = JObject.Parse(System.IO.File.ReadAllText(f));
                    if (jo["source"]?.Value<string>() != "jutsu") continue;

                    string slug = jo["slug"]?.Value<string>();
                    if (string.IsNullOrEmpty(slug)) continue;

                    var years = new List<int>();
                    int y = jo["year"]?.Value<int?>() ?? 0;
                    if (y > 0) years.Add(y);

                    JutPosterEnqueue(slug, jo["title"]?.Value<string>(),
                                     jo["original_title"]?.Value<string>(), years, null);
                    seeded++;
                }
                catch { }
            }
            if (seeded > 0) Console.WriteLine($"[QbitDownload] jut poster: скачанных тайтлов к проверке {seeded}");
        }
        catch { }
    }

    #endregion

    #region воркер

    // Один воркер на процесс: лимит Shikimori (~5 rps) не переживёт параллельных запросов,
    // а спешить некуда — постер доедет к следующему показу карточки.
    static void JutPosterWorkerStart()
    {
        if (Interlocked.CompareExchange(ref _jutPqRunning, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    while (_jutPq.TryDequeue(out var job))
                    {
                        try { await JutPosterProcess(job); }
                        catch (Exception ex) { Console.WriteLine("[QbitDownload] jut poster: " + ex.Message); }
                        finally { _jutPqSeen.TryRemove(job.slug, out _); }

                        await Task.Delay(Math.Max(50, ModInit.conf?.jutPosterPaceMs ?? 350));
                    }
                    Interlocked.Exchange(ref _jutPqRunning, 0);
                    // Гонка: между опустошением очереди и сбросом флага могли положить задачу.
                    // Если так — забираем воркера обратно, иначе она пролежит до следующего Enqueue.
                } while (!_jutPq.IsEmpty && Interlocked.CompareExchange(ref _jutPqRunning, 1, 0) == 0);
            }
            catch { Interlocked.Exchange(ref _jutPqRunning, 0); }
        });
    }

    static async Task JutPosterProcess(JutPosterJob job)
    {
        if (!JutPosterOn) return;

        var known = JutMatchRead(job.slug);
        bool haveFile = System.IO.File.Exists(JutUpPosterPath(job.slug));

        // Решение есть и оно свежее: либо всё готово, либо надо просто докачать картинку.
        if (JutMatchFresh(known))
        {
            if (haveFile) return;
            string cached = known?["cover"]?.Value<string>();
            if (known?["ok"]?.Value<bool>() == true && !string.IsNullOrEmpty(cached))
                await JutPosterStore(job.slug, cached);
            return;
        }

        // ── Шаг 1: Shikimori ищет по названию ─────────────────────────────────
        string query = !string.IsNullOrWhiteSpace(job.titleOrig) ? job.titleOrig : job.titleRu;
        var arr = await JutShikiSearch(query);
        if (arr == null)
        {
            // Сеть/лимит — это НЕ отказ по существу: решение не пишем, попробуем в другой раз.
            await Task.Delay(2000);
            return;
        }

        var cands = JutSuMatch.ParseCandidates(arr);
        var m = JutSuMatch.Pick(job.titleOrig, job.titleRu, job.years, cands);

        if (!m.ok)
        {
            JutMatchWrite(job.slug, new JObject
            {
                ["ok"] = false,
                ["reason"] = m.reason,
                ["romaji"] = job.titleOrig,
                ["title"] = job.titleRu,
                ["raw"] = job.rawPoster
            });
            JutPosterNoteRefusal(job.slug + " · " + (job.titleRu ?? "") + " · " + m.reason);
            return;
        }

        // ── Шаг 2: обложка. AniList ПО MAL id — детерминированно, без угадывания ──
        var (cover, transient) = await JutAniListCover(m.pick.id);
        string src = "anilist";

        if (string.IsNullOrEmpty(cover))
        {
            // 🔥 Лимит/сеть — это НЕ «обложки нет». Решение не пишем, тайтл вернётся в очередь
            // при следующем показе каталога и получит хорошую обложку. Пока показывается
            // прежний постер jut.su — ровно то поведение, что было до фичи.
            // Но если AniList недоступен упорно (гео, долгий бан), не зависаем навсегда:
            // с третьей попытки берём то, что есть у Shikimori (240×360 — хуже 460×690,
            // но всё равно лучше квадрата 186×186).
            int tries = _jutAniTries.AddOrUpdate(job.slug, 1, (_, v) => v + 1);
            if (transient && tries < 3) return;

            cover = JutShikiImageUrl(m.pick.image);
            src = "shikimori";
        }

        if (string.IsNullOrEmpty(cover) || !await JutPosterStore(job.slug, cover))
        {
            JutMatchWrite(job.slug, new JObject
            {
                ["ok"] = false,
                ["reason"] = "no_image",
                ["mal"] = m.pick.id,
                ["romaji"] = job.titleOrig,
                ["title"] = job.titleRu,
                ["raw"] = job.rawPoster
            });
            JutPosterNoteRefusal(job.slug + " · " + (job.titleRu ?? "") + " · no_image");
            return;
        }

        _jutAniTries.TryRemove(job.slug, out _);

        JutMatchWrite(job.slug, new JObject
        {
            ["ok"] = true,
            ["mal"] = m.pick.id,
            ["src"] = src,
            ["reason"] = m.reason,
            ["romaji"] = job.titleOrig,
            ["matched"] = m.pick.name,
            ["title"] = job.titleRu,
            ["cover"] = cover,
            ["raw"] = job.rawPoster
        });

        await JutPosterSyncDownloads(job.slug);
    }

    static void JutPosterNoteRefusal(string line)
    {
        _jutPosterRefusals.Enqueue(line);
        while (_jutPosterRefusals.Count > 10) _jutPosterRefusals.TryDequeue(out _);
    }

    #endregion

    #region внешние базы

    // 🔥 Свой UA, ОПИСАТЕЛЬНЫЙ. Shikimori режет браузерные строки, поэтому JutNet.Ua (Chrome)
    // сюда слать нельзя — получим сплошные отказы без единого внятного симптома.
    const string JutDbUa = "D1Vision/1.0 (home media server)";

    // 🔥 Свой клиент, а НЕ Shared.Services.Http: тот подставляет globalproxy по regex URL даже
    // при proxy:null (§BB.5) — запрос к базе ушёл бы через платный выход Proton, которых конечное
    // число и которые нужны видео, — и подменяет UA своим. Обе беды тихие. Причины те же, по
    // которым свой транспорт есть у самого jut.su (шапка JutSuHttp.cs).
    static readonly Lazy<HttpClient> _jutDbHttp = new(() => new HttpClient(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true,        // shikimori.one отвечает 301 → shikimori.io
        MaxAutomaticRedirections = 5,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    { Timeout = TimeSpan.FromSeconds(15) });

    static string JutShikiHost => (ModInit.conf?.jutShikimoriHost ?? "https://shikimori.io").TrimEnd('/');

    static async Task<JArray> JutShikiSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            string url = JutShikiHost + "/api/animes?limit=8&search=" + Uri.EscapeDataString(query.Trim());
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", JutDbUa);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await _jutDbHttp.Value.SendAsync(req);
            // ⚠️ Не-200 (429 «слишком часто», 5xx) — это НЕ «не найдено»: возвращаем null,
            // решение не пишется, слаг попробуем в другой раз. Иначе лимит частоты тихо
            // превратился бы в вечный отказ.
            if (!resp.IsSuccessStatusCode) return null;

            return JArray.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    static string JutShikiImageUrl(string image)
    {
        if (string.IsNullOrEmpty(image)) return null;
        if (image.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return image;
        return JutShikiHost + (image.StartsWith('/') ? image : "/" + image);
    }

    // Темп и кулдаун AniList. 🔥 Без них холодная страница каталога (30 тайтлов) улетает
    // в лимит: один запрос на сматченный тайтл при паузе 350 мс — это ~170 запросов в минуту
    // при лимите 90 (а в деградации 30). Проверено на живом сервере: приходит 429.
    static DateTime _jutAniCooldownUntil = DateTime.MinValue;
    static DateTime _jutAniLastCall = DateTime.MinValue;
    static readonly ConcurrentDictionary<string, int> _jutAniTries = new();

    /// <summary>
    /// Обложка AniList по MAL id. Это поиск ПО КЛЮЧУ, а не по названию — ошибиться тут нельзя.
    /// Матчить по названию у AniList как раз НЕЛЬЗЯ: у него своё написание («SPY×FAMILY»).
    ///
    /// 🔥 transient=true означает «не смогли спросить» (лимит, сеть, кулдаун), а НЕ «обложки нет».
    /// Путать эти два исхода нельзя: иначе временный 429 запишется вечным отказом.
    /// </summary>
    static async Task<(string cover, bool transient)> JutAniListCover(int malId)
    {
        if (malId <= 0) return (null, false);
        if (DateTime.UtcNow < _jutAniCooldownUntil) return (null, true);

        // Выдерживаем паузу между запросами к AniList (воркер один, гонки тут нет)
        int pace = Math.Max(0, ModInit.conf?.jutAniListPaceMs ?? 900);
        var since = DateTime.UtcNow - _jutAniLastCall;
        if (since < TimeSpan.FromMilliseconds(pace))
            await Task.Delay(TimeSpan.FromMilliseconds(pace) - since);
        _jutAniLastCall = DateTime.UtcNow;

        try
        {
            var body = new JObject
            {
                ["query"] = "query($m:Int){Media(idMal:$m,type:ANIME){id coverImage{extraLarge large}}}",
                ["variables"] = new JObject { ["m"] = malId }
            };

            string url = ModInit.conf?.jutAniListUrl ?? "https://graphql.anilist.co";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None),
                                            Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", JutDbUa);

            using var resp = await _jutDbHttp.Value.SendAsync(req);

            if ((int)resp.StatusCode == 429)
            {
                int wait = 60;
                if (resp.Headers.TryGetValues("Retry-After", out var ra) &&
                    int.TryParse(ra.FirstOrDefault(), out int sec) && sec > 0) wait = Math.Min(sec, 600);
                _jutAniCooldownUntil = DateTime.UtcNow.AddSeconds(wait);
                Console.WriteLine($"[QbitDownload] jut poster: AniList 429, пауза {wait} с");
                return (null, true);
            }
            if (!resp.IsSuccessStatusCode) return (null, true);

            var r = JObject.Parse(await resp.Content.ReadAsStringAsync());

            // GraphQL отвечает 200 даже на ошибку — лимит приезжает именно так
            if (r?["errors"] != null && r["data"]?["Media"] == null)
            {
                _jutAniCooldownUntil = DateTime.UtcNow.AddSeconds(60);
                return (null, true);
            }

            var img = r?["data"]?["Media"]?["coverImage"];
            string cover = img?["extraLarge"]?.Value<string>() ?? img?["large"]?.Value<string>();

            // Ответ получен и он пустой — записи в AniList просто нет. Это уже НЕ transient.
            return (cover, false);
        }
        catch { return (null, true); }
    }

    #endregion

    #region запись картинки

    /// <summary>
    /// Скачать обложку и положить РЯДОМ с сырым постером. false — оставляем всё как было.
    /// Санити обязательна: заменить квадрат 186×186 на другую мелочь — это регресс, а не апгрейд.
    /// </summary>
    static async Task<bool> JutPosterStore(string slug, string url)
    {
        try
        {
            byte[] bytes;
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", JutDbUa);
                using var resp = await _jutDbHttp.Value.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return false;
                bytes = await resp.Content.ReadAsByteArrayAsync();
            }

            int minW = Math.Max(1, ModInit.conf?.jutPosterMinWidth ?? 300);
            if (!JutSuMatch.ArtAcceptable(bytes, minW, out _, out _, out string ctype)) return false;

            string path = JutUpPosterPath(slug);
            string tmp = path + ".part";
            await System.IO.File.WriteAllBytesAsync(tmp, bytes);
            // Атомарно: оборванный на полпути контейнер не оставит обрезанный файл,
            // который роут потом честно отдаст клиенту.
            System.IO.File.Move(tmp, path, true);

            _jutUpCtype[slug] = ctype;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Тип уже лежащего апгрейженного постера (12 байт с диска, результат кешируем).</summary>
    internal static string JutUpPosterCtype(string slug)
    {
        if (_jutUpCtype.TryGetValue(slug, out string c)) return c;
        try
        {
            using var fs = System.IO.File.OpenRead(JutUpPosterPath(slug));
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return "image/jpeg";
            string t = head[0] == 0x89 ? "image/png"
                     : (head[0] == 'R' && head[8] == 'W') ? "image/webp"
                     : "image/jpeg";
            _jutUpCtype[slug] = t;
            return t;
        }
        catch { return "image/jpeg"; }
    }

    #endregion

    #region мост в «Загрузки»

    /// <summary>
    /// Скачанный тайтл показывается в «Загрузках» по HASH-пути (/qdl-data/img/&lt;hash&gt;.jpg).
    /// Когда апгрейд доехал — перекладываем и туда, иначе карточка скачанного останется
    /// с квадратом 186×186. has_poster — это File.Exists, перезапись его не ломает.
    /// </summary>
    internal static async Task JutPosterSyncDownloads(string slug)
    {
        try
        {
            string up = JutUpPosterPath(slug);
            if (!System.IO.File.Exists(up)) return;

            string hash = JutNet.Hash(slug);
            if (!System.IO.File.Exists(MetaPath(hash))) return;   // тайтл не скачан — нечего обновлять

            string pp = PosterPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(pp));
            await Task.Run(() => System.IO.File.Copy(up, pp, true));
        }
        catch { }
    }

    #endregion

    #region диагностика

    internal static JObject JutPosterStats()
    {
        var jo = new JObject
        {
            ["enabled"] = JutPosterOn,
            ["queue"] = _jutPq.Count,
            ["working"] = _jutPqRunning == 1
        };

        try
        {
            jo["upgraded"] = Directory.EnumerateFiles(JutDir("img"), "*.up.jpg").Count();
            jo["decided"] = Directory.EnumerateFiles(JutDir("match"), "*.json").Count();
        }
        catch { }

        jo["refusals"] = new JArray(_jutPosterRefusals.ToArray());
        return jo;
    }

    /// <summary>Живая проверка доступности баз — платный режим /qdl/jut/diag?probe=1.</summary>
    internal static async Task<JObject> JutPosterProbe()
    {
        var jo = new JObject();
        try
        {
            var arr = await JutShikiSearch("One Piece");
            jo["shikimori"] = arr != null && arr.Count > 0 ? "ok" : "fail";
        }
        catch { jo["shikimori"] = "fail"; }

        try
        {
            var (cover, transient) = await JutAniListCover(21);   // One Piece — заведомо существует
            jo["anilist"] = !string.IsNullOrEmpty(cover) ? "ok" : (transient ? "throttled" : "fail");
        }
        catch { jo["anilist"] = "fail"; }

        if (_jutAniCooldownUntil > DateTime.UtcNow)
            jo["anilistCooldownSec"] = (int)(_jutAniCooldownUntil - DateTime.UtcNow).TotalSeconds;

        return jo;
    }

    #endregion
}
