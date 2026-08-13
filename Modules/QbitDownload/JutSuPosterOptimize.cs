using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Вес постеров jut.su и полнота каталога.
//
// Апгрейд (JutSuPoster.cs) складывал обложку побайтово, как она приехала от AniList, а часть
// обложек там — PNG. Замеры на живом сервере:
//   на диске           462 файла .up.jpg, 79.4 МБ, среднее 176 КБ, максимум 678 КБ
//   из них PNG         94 штуки (под именем .up.jpg — тип определяется сигнатурой, не расширением)
//   460×650 PNG        665 КБ  ←→  тот же кадр в WebP q92 — 123 КБ
//   страница витрины   30 карточек = 6.1 МБ против 0.72 МБ у CUB (757 мс против 501 мс, гигабит)
//
// 🔥 Качество не режем: разрешение остаётся родным (jutPosterWidth = 0), q92 — визуально без
//    потерь. Экономия берётся с контейнера, а не с картинки.
//
// Второе: постер высокого качества был лишь у 462 тайтлов из 1357, потому что очередь апгрейда
// засевается только страницами, которые кто-то ОТКРЫЛ. JutPosterBackfillAll доводит остаток,
// переиспользуя обычный конвейер JutPosterEnqueue целиком — своего матчинга здесь нет.
//
// ⚠️ Грабли:
//  1. ArtAcceptable нельзя натравливать на ВЫХОД кодека: JutSuMatch.ImageSize до этой правки
//     знал WebP только как VP8X, а libvips для непрозрачной картинки пишет простой "VP8 ".
//     Санити зарубила бы почти все пережатые постеры МОЛЧА. Поэтому ArtAcceptable судит
//     исходник (как и раньше), а у выхода своя дешёвая проверка.
//  2. Курсор на диске не нужен ни проходу, ни бэкфиллу: состояние — сами файлы (.up.jpg и
//     match/<slug>.json). Прерванная работа продолжается со следующего старта.
//  3. _jutUpCtype обязан обновляться ВМЕСТЕ с файлом, иначе роут отдаст Content-Type от старого
//     формата, а pv останется прежним — и клиент новый файл не запросит никогда.
//
// Документация: E:\Media-server\claude\jut\02-architecture.md
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region кодек

    static bool _jutVipsInit;
    static readonly object _jutVipsLock = new();

    /// <summary>
    /// Настройки NetVips на процесс. Значения — как в Core/Middlewares/ProxyImg.cs: кеши по нулям
    /// (постер не переиспользуется, а память общая с транскодом), Concurrency=1 — фоновая работа
    /// не должна отбирать ядра у ffmpeg-воркера и отдачи стрима.
    /// </summary>
    static void JutVipsEnsure()
    {
        if (_jutVipsInit) return;
        lock (_jutVipsLock)
        {
            if (_jutVipsInit) return;
            NetVips.Cache.Max = 0;
            NetVips.Cache.MaxMem = 0;
            NetVips.Cache.MaxFiles = 0;
            NetVips.Cache.Trace = false;
            NetVips.NetVips.Leak = false;
            NetVips.NetVips.Profile = false;
            NetVips.NetVips.Concurrency = 1;
            _jutVipsInit = true;
        }
    }

    internal static string JutPosterFormat()
    {
        string f = (ModInit.conf?.jutPosterFormat ?? "webp").Trim().ToLowerInvariant();
        return f is "webp" or "jpeg" or "none" ? f : "webp";
    }

    /// <summary>
    /// Пережать постер. null — «оставь как было»: выключено, не смогли, или выгоды нет.
    /// 🔥 Наружу не бросает НИКОГДА: любой сбой обязан выродиться в сохранение оригинала,
    /// иначе апгрейд начал бы терять постеры там, где раньше просто работал.
    /// </summary>
    internal static byte[] JutPosterEncode(byte[] src)
    {
        string fmt = JutPosterFormat();
        if (fmt == "none" || src == null || src.Length == 0) return null;

        int q = Math.Clamp(ModInit.conf?.jutPosterQuality ?? 92, 40, 100);
        int maxW = Math.Max(0, ModInit.conf?.jutPosterWidth ?? 0);   // 0 = родное разрешение

        try
        {
            JutVipsEnsure();

            using var ms = new MemoryStream(src, writable: false);
            using var img = NetVips.Image.NewFromStream(ms, access: NetVips.Enums.Access.Sequential);
            if (img.Width <= 0 || img.Height <= 0) return null;

            NetVips.Image outImg = img;
            // ⚠️ Только вниз: запасной источник (Shikimori 225×350) при раздувании до кападал бы
            // мыло, и санити «постер объективно лучше квадрата» перестала бы что-то значить.
            if (maxW > 0 && img.Width > maxW)
                outImg = img.ThumbnailImage(maxW, size: NetVips.Enums.Size.Down,
                                            crop: NetVips.Enums.Interesting.None);

            byte[] enc;
            try
            {
                using var outMs = new MemoryStream();
                // ⚠️ keep, а НЕ strip: в NetVips 3.x параметра strip уже нет, метаданные
                // отбрасываются через ForeignKeep.None (libvips 8.15+).
                if (fmt == "webp")
                    outImg.WebpsaveStream(outMs, q: q, keep: NetVips.Enums.ForeignKeep.None);
                else
                    outImg.JpegsaveStream(outMs, q: q, optimizeCoding: true,
                                          keep: NetVips.Enums.ForeignKeep.None);
                enc = outMs.ToArray();
            }
            finally
            {
                if (!ReferenceEquals(outImg, img)) outImg.Dispose();
            }

            // Санити ВЫХОДА — своя и дешёвая. ArtAcceptable сюда не годится: он требует портрет
            // и минимальную ширину, а это свойства ИСХОДНИКА, уже проверенные выше.
            if (enc == null || enc.Length < 2048) return null;
            if (JutSuMatch.SniffMime(enc) == null) return null;
            if (JutSuMatch.ImageSize(enc).w <= 0) return null;    // не разобрали — не подменяем
            if (enc.Length >= src.Length) return null;            // пережимать смысла нет

            return enc;
        }
        catch (Exception ex)
        {
            JutPosterOptNote("encode: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Поколение постера: 2 — пережат в WebP, 1 — лежит как приехал.
    /// 🔥 Этим бампается pv. Без смены версии пережатый файл до клиента не доедет никогда:
    /// на ?v= стоит immutable на год, и уже закешированный постер он больше не запросит.
    /// Отдельного состояния не заводим — сигнатуру и так читает JutUpPosterCtype (12 байт + кеш).
    /// </summary>
    internal static int JutUpPosterGen(string slug)
        => JutUpPosterCtype(slug) == "image/webp" ? 2 : 1;

    #endregion

    #region диагностика прохода

    static readonly ConcurrentQueue<string> _jutOptErrors = new();
    static JObject _jutOptStat = new JObject { ["state"] = "idle" };
    static JObject _jutBfStat = new JObject { ["state"] = "idle" };

    static void JutPosterOptNote(string line)
    {
        _jutOptErrors.Enqueue(DateTime.UtcNow.ToString("HH:mm:ss") + " " + line);
        while (_jutOptErrors.Count > 10) _jutOptErrors.TryDequeue(out _);
    }

    internal static JObject JutPosterOptDiag()
    {
        return new JObject
        {
            ["format"] = JutPosterFormat(),
            ["quality"] = Math.Clamp(ModInit.conf?.jutPosterQuality ?? 92, 40, 100),
            ["width"] = Math.Max(0, ModInit.conf?.jutPosterWidth ?? 0),
            ["reencode"] = _jutOptStat?.DeepClone(),
            ["backfill"] = _jutBfStat?.DeepClone(),
            ["errors"] = new JArray(_jutOptErrors.ToArray())
        };
    }

    #endregion

    #region разовый проход по уже лежащим постерам

    static int _jutOptRunning;

    /// <summary>
    /// Пережать апгрейженные постеры, лежащие на диске с прошлых запусков.
    /// Идемпотентно (файл уже в целевом формате — пропуск), возобновляемо (состояние = сами
    /// файлы, курсор не нужен), безопасно на живом сервере (атомарная подмена + пауза).
    /// </summary>
    internal static async Task JutPosterReencodeAll()
    {
        if (!JutPosterOn) return;
        if (ModInit.conf?.jutPosterReencode == false) return;

        string fmt = JutPosterFormat();
        if (fmt == "none") return;
        if (Interlocked.CompareExchange(ref _jutOptRunning, 1, 0) != 0) return;

        int done = 0, skip = 0, fail = 0;
        long before = 0, after = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int pace = Math.Max(0, ModInit.conf?.jutPosterReencodePaceMs ?? 150);
        string want = fmt == "webp" ? "image/webp" : "image/jpeg";

        try
        {
            _jutOptStat = new JObject { ["state"] = "running" };

            string dir = JutDir("img");
            if (!Directory.Exists(dir)) return;

            // ToList: каталог во время прохода пополняется живым апгрейдом, а перечисление
            // по изменяющейся директории — источник неожиданных исключений.
            foreach (string path in Directory.EnumerateFiles(dir, "*.up.jpg").ToList())
            {
                try
                {
                    // Файл прямо сейчас пишет JutPosterStore — не мешаем.
                    if (System.IO.File.Exists(path + ".part")) { skip++; continue; }

                    string name = Path.GetFileName(path);
                    string slug = name.Substring(0, name.Length - ".up.jpg".Length);

                    byte[] src = await System.IO.File.ReadAllBytesAsync(path);
                    if (src.Length < 128) { skip++; continue; }

                    // Уже в целевом формате: это и идемпотентность, и «возобновление с места».
                    if (JutSuMatch.SniffMime(src) == want) { skip++; continue; }

                    byte[] enc = JutPosterEncode(src);
                    if (enc == null) { skip++; continue; }   // не смогли / не выгодно — оригинал живёт

                    string tmp = path + ".part";
                    await System.IO.File.WriteAllBytesAsync(tmp, enc);
                    System.IO.File.Move(tmp, path, true);

                    // 🔥 Обязательно и ИМЕННО здесь: иначе роут отдаст Content-Type: image/jpeg
                    // на WebP-байтах, а pv останется 1 и клиент новый файл не запросит.
                    _jutUpCtype[slug] = JutSuMatch.SniffMime(enc) ?? want;

                    // Скачанный тайтл показывается в «Загрузках» по HASH-пути — туда тоже.
                    await JutPosterSyncDownloads(slug);

                    before += src.Length;
                    after += enc.Length;
                    done++;
                }
                catch (Exception ex)
                {
                    fail++;
                    JutPosterOptNote(Path.GetFileName(path) + ": " + ex.Message);
                }

                if (pace > 0) await Task.Delay(pace);   // живой сервер: не выедаем CPU одним куском
            }

            if (done > 0 || fail > 0)
                Console.WriteLine($"[QbitDownload] jut poster: пережато {done}, пропущено {skip}, "
                                + $"ошибок {fail}, {before / 1048576} МБ → {after / 1048576} МБ "
                                + $"за {sw.Elapsed.TotalSeconds:F0} с");
        }
        catch (Exception ex) { JutPosterOptNote("reencode: " + ex.Message); }
        finally
        {
            _jutOptStat = new JObject
            {
                ["state"] = "done",
                ["done"] = done,
                ["skipped"] = skip,
                ["failed"] = fail,
                ["beforeMb"] = Math.Round(before / 1048576.0, 1),
                ["afterMb"] = Math.Round(after / 1048576.0, 1),
                ["sec"] = (int)sw.Elapsed.TotalSeconds
            };
            Interlocked.Exchange(ref _jutOptRunning, 0);
        }
    }

    #endregion

    #region довод каталога до полноты

    static int _jutBfRunning;

    /// <summary>
    /// Довести ВЕСЬ каталог до постеров высокого качества, а не только открытые кем-то страницы.
    /// Своего матчинга здесь нет: слаги просто ставятся в обычную очередь JutPosterEnqueue,
    /// у которой уже есть дедуп, кап, единственный воркер, пейс и BackgroundScope.
    /// ⚠️ Инвариант #3 цел: сидер и голова каталога апгрейд по-прежнему не зовут — это
    /// отдельная джоба с собственным киллсвитчем.
    /// </summary>
    internal static async Task JutPosterBackfillAll()
    {
        if (!JutPosterOn) return;
        if (ModInit.conf?.jutPosterBackfill == false) return;
        if (Interlocked.CompareExchange(ref _jutBfRunning, 1, 0) != 0) return;

        int queued = 0, have = 0, decided = 0, total = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _jutBfStat = new JObject { ["state"] = "running" };

            // Снимок под локом: индекс параллельно пополняется головой тика.
            List<JObject> items;
            var st = JutIdxLoad();
            lock (_jutIdxLock)
            {
                if (!st.complete || st.items.Count == 0) return;   // неполный снапшот не обходим
                items = st.items.Select(x => (JObject)x.DeepClone()).ToList();
            }

            total = items.Count;
            int batch = Math.Clamp(ModInit.conf?.jutPosterBackfillBatch ?? 100, 10, JutPosterQueueCap - 50);

            foreach (var c in items)
            {
                string slug = c.Value<string>("slug");
                if (string.IsNullOrEmpty(slug) || !JutSuParse.IsValidSlug(slug)) continue;

                // Постер уже есть — самый дешёвый отсев, без чтения файла.
                if (System.IO.File.Exists(JutUpPosterPath(slug))) { have++; continue; }

                // Решение уже принято и живо (в том числе отрицательное — оно помнится
                // jutPosterRetryDays). Повторять запрос к Shikimori незачем.
                if (JutMatchFresh(JutMatchRead(slug))) { decided++; continue; }

                // Не переполняем очередь: JutPosterEnqueue при упёртом капе молча теряет слаг.
                while (_jutPq.Count >= batch)
                {
                    await Task.Delay(1000);
                    if (!JutPosterOn || ModInit.conf?.jutPosterBackfill == false) return;
                }

                var years = new List<int>();
                if (c["years"] is JArray ya)
                    foreach (var y in ya) { int v = y?.Value<int?>() ?? 0; if (v > 0) years.Add(v); }

                JutPosterEnqueue(slug, c.Value<string>("title"), c.Value<string>("original"),
                                 years, c.Value<string>("poster"));
                queued++;
            }

            if (queued > 0)
                Console.WriteLine($"[QbitDownload] jut poster: к апгрейду поставлено {queued} из {total} "
                                + $"(уже с постером {have}, решение есть {decided})");
        }
        catch (Exception ex) { JutPosterOptNote("backfill: " + ex.Message); }
        finally
        {
            _jutBfStat = new JObject
            {
                ["state"] = "done",
                ["total"] = total,
                ["queued"] = queued,
                ["havePoster"] = have,
                ["decided"] = decided,
                ["sec"] = (int)sw.Elapsed.TotalSeconds
            };
            Interlocked.Exchange(ref _jutBfRunning, 0);
        }
    }

    #endregion
}
