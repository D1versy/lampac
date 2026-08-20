using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Снапшот-индекс каталога jut.su (qdl 2.38).
//
// Зачем. Витрина `order-by-add` — это единственный фильтр, который шлёт клиент, и меняется
// она ТОЛЬКО сверху (1–2 новинки в день). До этого каждая страница кешировалась отдельно
// с TTL 30 минут, то есть листание каталога раз в полчаса снова платило ~1.1 с за страницу.
//
// 🔥 Почему не «пер-страничный кеш с длинным TTL». Новинки СДВИГАЮТ ленту: карточка со дна
// страницы 1 уезжает на страницу 2. При независимых TTL страниц свежая p1 и старая p2 дают
// либо дубли, либо молча потерянные тайтлы. Поэтому храним ОДИН упорядоченный список
// (порядок = как на сайте), а страницы нарезаем из него — сдвиг обрабатывается вставкой
// в голову, и никакого шва между страницами нет.
//
// Три режима тика (в порядке приоритета): сид → ресид → голова.
//
// Устройство и решения: E:\Media-server\claude\jut\02-architecture.md §6
// ─────────────────────────────────────────────────────────────────────────────
public partial class QbitController
{
    #region состояние

    /// <summary>Столько карточек на странице отдаёт сам jut.su — тем же шагом режем снапшот.</summary>
    internal const int JutPageSize = 30;

    internal sealed class JutIdxState
    {
        public DateTime seededAt, updatedAt;
        public bool complete;
        public int cursorPage;                       // докуда дошёл сид (0 = ещё ничего)
        public List<JObject> items = new();
        public HashSet<string> slugs = new(StringComparer.OrdinalIgnoreCase);

        public bool Add(JObject card)
        {
            string s = card?.Value<string>("slug");
            if (string.IsNullOrEmpty(s) || !slugs.Add(s)) return false;
            items.Add(card);
            return true;
        }
    }

    static JutIdxState _jutIdx;
    static readonly object _jutIdxLock = new();
    static readonly SemaphoreSlim _jutIdxGate = new SemaphoreSlim(1, 1);   // single-flight тика

    static bool JutIdxOn => ModInit.conf?.jutCatalogIndex ?? true;

    static string JutIdxPath() => Path.Combine(JutNet.JutDataDir(), "catalog-index.json");

    /// <summary>Сброс РАМ-слоя (смена cachePath, тесты). Файл не трогаем — он перечитается.</summary>
    internal static void JutIdxReset()
    {
        lock (_jutIdxLock) _jutIdx = null;
    }

    /// <summary>
    /// Ручной пересид: индекс отдаётся как прежде, но следующий тик соберёт его заново
    /// в теневой буфер (см. JutIdxReseed). Пустой индекс просто пойдёт в сид.
    /// </summary>
    internal static void JutIdxDropForReseed()
    {
        var st = JutIdxLoad();
        lock (_jutIdxLock) st.seededAt = DateTime.MinValue;
        JutIdxSave(st);
    }

    #endregion

    #region диск

    static JutIdxState JutIdxLoad()
    {
        lock (_jutIdxLock)
        {
            if (_jutIdx != null) return _jutIdx;
            var st = new JutIdxState();
            try
            {
                string p = JutIdxPath();
                if (System.IO.File.Exists(p))
                {
                    var jo = JObject.Parse(System.IO.File.ReadAllText(p));
                    st.complete = jo.Value<bool?>("complete") ?? false;
                    st.cursorPage = jo.Value<int?>("cursorPage") ?? 0;
                    st.seededAt = jo.Value<DateTime?>("seededAt") ?? DateTime.MinValue;
                    st.updatedAt = jo.Value<DateTime?>("updatedAt") ?? DateTime.MinValue;
                    foreach (var c in (jo["items"] as JArray ?? new JArray()).OfType<JObject>())
                        st.Add(c);
                }
            }
            catch (Exception ex)
            {
                // Битый файл = «индекса нет»: отдача уйдёт старым путём, тик пересоберёт с нуля
                JutNet.Log("catalog", "индекс не прочитан: " + ex.Message);
                st = new JutIdxState();
            }
            _jutIdx = st;
            return st;
        }
    }

    /// <summary>
    /// ⚠️ Через .tmp + Move: обрыв посреди WriteAllText оставил бы обрезанный JSON на мегабайт,
    /// и следующий старт прочитал бы мусор (та же грабля, что у JsonStore).
    /// </summary>
    static void JutIdxSave(JutIdxState st)
    {
        try
        {
            var jo = new JObject
            {
                ["ver"] = 1,
                ["complete"] = st.complete,
                ["cursorPage"] = st.cursorPage,
                ["seededAt"] = st.seededAt,
                ["updatedAt"] = st.updatedAt,
                ["items"] = new JArray(st.items)
            };
            string path = JutIdxPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, jo.ToString(Newtonsoft.Json.Formatting.None));
            System.IO.File.Move(tmp, path, true);
        }
        catch (Exception ex) { JutNet.Log("catalog", "индекс не записан: " + ex.Message); }
    }

    #endregion

    #region отдача страницы

    /// <summary>
    /// Страница дефолтной витрины из снапшота, без единого запроса к jut.su.
    /// Отдаём только ПОЛНЫЙ снапшот: пока сид не дошёл до конца, граница между «снапшотом»
    /// и живой пагинацией сдвигалась бы новинками — источник дублей и пропусков.
    /// </summary>
    internal static bool JutIdxTryServe(int page, out JObject payload)
    {
        payload = null;
        if (!JutIdxOn) return false;

        var st = JutIdxLoad();
        List<JObject> slice;
        int count;
        lock (_jutIdxLock)
        {
            if (!st.complete || st.items.Count == 0) return false;
            count = st.items.Count;
            int from = Math.Max(0, (page - 1) * JutPageSize);
            // ⚠️ Клоны: JutPosterStamp дописывает в карточку pv, а один и тот же JObject
            // в двух параллельных ответах — это и порча индекса, и гонка на JToken.
            slice = st.items.Skip(from).Take(JutPageSize)
                            .Select(x => (JObject)x.DeepClone()).ToList();
        }

        payload = new JObject
        {
            ["ok"] = true,
            ["page"] = page,
            ["order"] = "add",
            ["hasNext"] = page * JutPageSize < count,
            ["total"] = slice.Count,
            ["via"] = "index",
            ["index"] = true,
            ["stale"] = false,
            ["items"] = new JArray(slice)
        };
        return true;
    }

    /// <summary>
    /// Карточка по слагу из снапшота. Нужна экрану поиска (/qdl/jut/recent), когда кеша
    /// тайтла ещё нет: показать название и постер лучше, чем голый слаг.
    /// Отдаётся КЛОН по той же причине, что и в JutIdxTryServe.
    /// </summary>
    internal static JObject JutIdxFindCard(string slug)
    {
        if (!JutIdxOn || string.IsNullOrEmpty(slug)) return null;

        var st = JutIdxLoad();
        lock (_jutIdxLock)
        {
            if (st.items.Count == 0 || !st.slugs.Contains(slug)) return null;
            var hit = st.items.FirstOrDefault(x =>
                string.Equals(x?.Value<string>("slug"), slug, StringComparison.OrdinalIgnoreCase));
            return hit == null ? null : (JObject)hit.DeepClone();
        }
    }

    #endregion

    #region тик: сид, голова, ресид

    /// <summary>
    /// Один запрос страницы дефолтной витрины. Вынесен ради точки подмены сети:
    /// у JutNet своя фабрика HttpClient без места под HttpMessageHandler, иначе тик
    /// в тестах не прогнать (тот же приём, что у JutWatchTick).
    /// ⚠️ Только page-N/: ?page=N отдаёт 200 и контент ПЕРВОЙ страницы (тихая порча данных).
    /// </summary>
    static async Task<(bool ok, JutCatalogPage page)> JutIdxLoadPage(int page)
    {
        var resp = await JutNet.PostForm(JutCatalogUrl(page, null, null, "add"),
            "ajax_load=yes&start_from_page=" + page + "&show_search=&anime_of_user=",
            JutSuParse.ReachedCatalog);
        if (resp == null || !resp.reached) return (false, null);
        return (true, JutSuParse.ParseCatalog(resp.body));
    }

    internal static async Task<JObject> JutCatalogTick(bool manual = false,
                                                      Func<int, Task<(bool ok, JutCatalogPage page)>> loadPage = null)
    {
        loadPage ??= JutIdxLoadPage;

        var res = new JObject { ["ok"] = true };
        if (ModInit.conf?.jutEnable != true) { res["skipped"] = "disabled"; return res; }
        if (!JutIdxOn) { res["skipped"] = "index off"; return res; }

        if (!await _jutIdxGate.WaitAsync(0)) { res["skipped"] = "занят другой проход"; return res; }

        // Фоновый гейт: сид — это десятки запросов подряд, и без него они отбирали бы
        // слоты у карточек и плеера (один запрос к сайту ≈ 1.1 с).
        using var bg = JutNet.BackgroundScope();
        try
        {
            var st = JutIdxLoad();
            int reseedDays = ModInit.conf?.jutCatalogReseedDays ?? 30;
            // Ручной прогон пересобирает всегда: киллсвитч jutCatalogReseedDays=0 выключает
            // ПЛАНОВЫЙ ресид, но не должен блокировать явную кнопку «пересобрать».
            bool needReseed = st.complete
                              && (manual || (reseedDays > 0 && (DateTime.UtcNow - st.seededAt).TotalDays >= reseedDays));

            if (!st.complete) return await JutIdxSeed(st, res, loadPage);
            if (needReseed) return await JutIdxReseed(res, loadPage);
            return await JutIdxHead(st, res, loadPage);
        }
        catch (Exception ex)
        {
            JutNet.Log("catalog", "тик индекса: " + ex.Message);
            res["ok"] = false;
            res["error"] = ex.Message;
            return res;
        }
        finally { _jutIdxGate.Release(); }
    }

    /// <summary>
    /// Медленный проход по всем страницам витрины. Курсор персистится после КАЖДОЙ страницы:
    /// рестарт контейнера дорог (Roslyn), и начинать сид заново после каждого было бы дико.
    /// </summary>
    static async Task<JObject> JutIdxSeed(JutIdxState st, JObject res,
                                          Func<int, Task<(bool ok, JutCatalogPage page)>> loadPage)
    {
        int pace = Math.Max(0, ModInit.conf?.jutCatalogSeedPaceMs ?? 3000);
        int maxPages = Math.Max(1, ModInit.conf?.jutCatalogSeedMaxPages ?? 60);
        int added = 0, pages = 0;
        bool done = false, siteDown = false;

        for (int p = st.cursorPage + 1; p <= maxPages; p++)
        {
            if (ModInit.conf?.jutEnable != true) break;   // выключатель на лету: курсор цел
            var (ok, page) = await loadPage(p);
            if (!ok) { siteDown = true; break; }          // сайт лёг — доберём следующим тиком
            pages++;

            lock (_jutIdxLock)
            {
                foreach (var c in JutCardsJson(page.items).OfType<JObject>())
                    if (st.Add(c)) added++;
                st.cursorPage = p;
                st.updatedAt = DateTime.UtcNow;
                if (!page.hasNext || page.items.Count == 0)
                {
                    st.complete = true;
                    st.seededAt = DateTime.UtcNow;
                    done = true;
                }
            }
            JutIdxSave(st);
            if (done) break;
            if (pace > 0) await Task.Delay(pace);
        }

        // Упор в кап — либо сайт вырос, либо hasNext врёт. Закрываем индекс тем, что набрали:
        // отдавать 46 страниц из кеша полезнее, чем не отдавать ничего, но след в логе нужен.
        if (!done && !siteDown && st.cursorPage >= maxPages)
        {
            lock (_jutIdxLock)
            {
                st.complete = true;
                st.seededAt = DateTime.UtcNow;
            }
            JutIdxSave(st);
            done = true;
            Console.WriteLine("[QbitDownload] jut/catalog: сид упёрся в кап " + maxPages
                              + " страниц — индекс закрыт неполным (jutCatalogSeedMaxPages)");
        }

        Console.WriteLine($"[QbitDownload] jut/catalog: сид — страниц {pages}, добавлено {added}, "
                          + $"всего {st.items.Count}, {(done ? "готов" : "продолжим в следующий тик")}");
        res["mode"] = "seed";
        res["pages"] = pages;
        res["added"] = added;
        res["items"] = st.items.Count;
        res["complete"] = done;
        return res;
    }

    /// <summary>
    /// Полный пересбор в ТЕНЕВОЙ буфер: старый индекс всё это время продолжает отдаваться.
    /// Единственный механизм вычистки тайтлов, удалённых с сайта, — голова про них не узнает.
    /// </summary>
    static async Task<JObject> JutIdxReseed(JObject res,
                                            Func<int, Task<(bool ok, JutCatalogPage page)>> loadPage)
    {
        int pace = Math.Max(0, ModInit.conf?.jutCatalogSeedPaceMs ?? 3000);
        int maxPages = Math.Max(1, ModInit.conf?.jutCatalogSeedMaxPages ?? 60);
        var shadow = new JutIdxState();
        bool done = false;

        for (int p = 1; p <= maxPages; p++)
        {
            if (ModInit.conf?.jutEnable != true) break;
            var (ok, page) = await loadPage(p);
            if (!ok) break;                              // сайт лёг — старый индекс остаётся жить
            foreach (var c in JutCardsJson(page.items).OfType<JObject>()) shadow.Add(c);
            shadow.cursorPage = p;
            if (!page.hasNext || page.items.Count == 0) { done = true; break; }
            if (pace > 0) await Task.Delay(pace);
        }

        res["mode"] = "reseed";
        res["items"] = shadow.items.Count;
        res["complete"] = done;

        // Половинчатый пересбор не подменяет живой индекс: лучше месяц старых данных,
        // чем каталог, обрезанный на середине из-за упавшего сайта.
        if (!done || shadow.items.Count == 0)
        {
            Console.WriteLine("[QbitDownload] jut/catalog: ресид не завершён — оставляем прежний индекс");
            return res;
        }

        shadow.complete = true;
        shadow.seededAt = shadow.updatedAt = DateTime.UtcNow;
        lock (_jutIdxLock) _jutIdx = shadow;
        JutIdxSave(shadow);
        Console.WriteLine("[QbitDownload] jut/catalog: ресид готов — тайтлов " + shadow.items.Count);
        return res;
    }

    /// <summary>
    /// Обычный тик: одна страница. Всё до первого знакомого слага — новинки, они уходят
    /// в голову списка; знакомые карточки заменяем целиком (у онгоингов подрос счётчик серий).
    /// Пересечения нет вовсе → смотрим следующую страницу, до капа.
    /// </summary>
    static async Task<JObject> JutIdxHead(JutIdxState st, JObject res,
                                          Func<int, Task<(bool ok, JutCatalogPage page)>> loadPage)
    {
        int maxPages = Math.Max(1, ModInit.conf?.jutCatalogHeadMaxPages ?? 5);
        int pace = Math.Max(0, ModInit.conf?.jutCatalogSeedPaceMs ?? 3000);
        var fresh = new List<JObject>();
        bool met = false;
        int pages = 0, refreshed = 0;

        for (int p = 1; p <= maxPages && !met; p++)
        {
            if (ModInit.conf?.jutEnable != true) break;
            var (ok, page) = await loadPage(p);
            if (!ok) { res["siteDown"] = true; break; }
            pages++;

            foreach (var c in JutCardsJson(page.items).OfType<JObject>())
            {
                string slug = c.Value<string>("slug");
                if (string.IsNullOrEmpty(slug)) continue;
                bool known;
                lock (_jutIdxLock) known = st.slugs.Contains(slug);
                if (!known) { fresh.Add(c); continue; }

                met = true;
                lock (_jutIdxLock)
                {
                    int i = st.items.FindIndex(x => string.Equals(x.Value<string>("slug"), slug,
                                                                  StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) { st.items[i] = c; refreshed++; }
                }
            }
            if (!met && pages < maxPages && pace > 0) await Task.Delay(pace);
        }

        if (fresh.Count > 0)
        {
            lock (_jutIdxLock)
            {
                // Порядок сайта сохраняем: новинки идут перед старыми, между собой — как пришли
                foreach (var c in Enumerable.Reverse(fresh))
                {
                    string s = c.Value<string>("slug");
                    if (string.IsNullOrEmpty(s) || !st.slugs.Add(s)) continue;
                    st.items.Insert(0, c);
                }
                st.updatedAt = DateTime.UtcNow;
            }
            JutIdxSave(st);
        }
        else if (refreshed > 0)
        {
            lock (_jutIdxLock) st.updatedAt = DateTime.UtcNow;
            JutIdxSave(st);
        }

        // Ни одного знакомого слага за maxPages страниц — витрина разъехалась с индексом
        // (сменилась сортировка сайта, массовое удаление). Полный ресид честнее, чем
        // клеить голову к списку, с которым она больше не стыкуется.
        if (!met && pages >= maxPages)
        {
            lock (_jutIdxLock) st.seededAt = DateTime.MinValue;
            JutIdxSave(st);
            Console.WriteLine("[QbitDownload] jut/catalog: голова не сошлась с индексом за "
                              + maxPages + " страниц — назначен полный ресид");
            res["reseedScheduled"] = true;
        }

        if (fresh.Count > 0)
        {
            Console.WriteLine("[QbitDownload] jut/catalog: новинок " + fresh.Count
                              + ", обновлено карточек " + refreshed + ", всего " + st.items.Count);

            // Новинке постер высокого качества нужен сразу, а не после ближайшего рестарта.
            // ⚠️ Инвариант #3 цел: голова сама апгрейд НЕ зовёт — она зовёт отдельную джобу,
            // у которой свой киллсвитч (jutPosterBackfill) и которая на холостом ходу
            // отсеивает готовое по файлу и решению, без единого запроса наружу.
            try { await JutPosterBackfillAll(); }
            catch (Exception ex) { Console.WriteLine("[QbitDownload] jut poster backfill: " + ex.Message); }
        }

        res["mode"] = "head";
        res["pages"] = pages;
        res["added"] = fresh.Count;
        res["refreshed"] = refreshed;
        res["items"] = st.items.Count;
        return res;
    }

    #endregion

    #region пиггибек и диагностика

    /// <summary>
    /// Счётчик серий у онгоингов — бесплатно: суточный тик слежения и так тянет /anime/ongoing/.
    /// ⚠️ Флаг ongoing у ОТСУТСТВУЮЩИХ в карте не сбрасываем: карта строится по первой странице
    /// онгоингов и может быть неполной — «снял онгоинг у всех, кого не увидел» было бы враньём.
    /// </summary>
    internal static int JutCatalogOngoingUpdate(Dictionary<string, int> counts)
    {
        if (counts == null || counts.Count == 0 || !JutIdxOn) return 0;
        var st = JutIdxLoad();
        int n = 0;
        lock (_jutIdxLock)
        {
            if (!st.complete || st.items.Count == 0) return 0;
            foreach (var c in st.items)
            {
                string slug = c.Value<string>("slug");
                if (string.IsNullOrEmpty(slug) || !counts.TryGetValue(slug, out int eps)) continue;
                if ((c.Value<int?>("episodes") ?? 0) == eps && (c.Value<bool?>("ongoing") ?? false)) continue;
                c["episodes"] = eps;
                c["ongoing"] = true;
                n++;
            }
            if (n > 0) st.updatedAt = DateTime.UtcNow;
        }
        if (n > 0) JutIdxSave(st);
        return n;
    }

    internal static JObject JutIdxDiag()
    {
        var st = JutIdxLoad();
        lock (_jutIdxLock)
            return new JObject
            {
                ["enabled"] = JutIdxOn,
                ["complete"] = st.complete,
                ["items"] = st.items.Count,
                ["pages"] = (st.items.Count + JutPageSize - 1) / JutPageSize,
                ["cursorPage"] = st.cursorPage,
                ["seededAt"] = st.seededAt == DateTime.MinValue ? null : st.seededAt.ToString("o"),
                ["updatedAt"] = st.updatedAt == DateTime.MinValue ? null : st.updatedAt.ToString("o")
            };
    }

    #endregion
}
