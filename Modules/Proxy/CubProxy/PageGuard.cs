using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace CubProxy;

// ── Сторож номера страницы в рядах каталога CUB (qdl 2.112) ─────────────────────────────────
// Продолжение §DI, где этот дефект разобран, но кодом не закрыт («что НЕ сделано, ждёт решения
// владельца: санити-чек рассинхрона body.page != запрошенной → не класть в кеш надолго»).
//
// У CUB перед API стоит свой кеш nginx (в ответе `x-cache-status: HIT`), и он периодически
// отдаёт тело ЧУЖОЙ страницы. Боевые замеры:
//
//   04.09  ключ 192.168.87.24:9118 /cub/tmdb./?sort=now_playing&page=1&email= держал "page": 11,
//          пересечение с живым CUB — 0 карточек из 13
//   05.09  перебор page=1..40 к tmdb.cub.best: page=21 -> отдал 2, page=31 -> отдал 3
//          (дважды подряд, обе на x-cache-status: HIT)
//   05.09  внешний вход tv.d1versy.com:9443, page=1 -> пришло тело page 11
//
// Дефект ПРЕХОДЯЩИЙ: через 20 минут те же адреса отвечают верно. Но наш Staticache примораживает
// пойманное тело на cache_api (3 ч), причём ОТДЕЛЬНО под каждый вход (ключ = схема+хост+путь+
// запрос), поэтому одна неудачная секунда апстрима держит чужую страницу в топе три часа.
//
// 🔴 Ряд ГЛАВНОЙ ходит БЕЗ параметра page (`?sort=now_playing&email=`), а «Ещё» — с `page=1`
// (§DI). Это разные ключи кеша, и жалоба владельца — про ряд главной. Поэтому «параметра нет»
// обязано означать «ждём первую страницу», иначе главный пострадавший остаётся без защиты.
// Проверено на боевом: CUB на адрес без page отвечает {"page":1,…}.
//
// 🔴 Почему функция ЧИСТАЯ и лежит отдельным файлом — ровно та же причина, что у RowFilter.cs:
// её линкуют в Tests/QbitDownload.Tests (Compile Include=… Link=…). Обращений к CubProxy.ModInit
// быть не должно (в тестовой сборке он конфликтует с QbitDownload.ModInit), к BaseController —
// тоже: состояние предохранителя держит контроллер и передаёт сюда значением.
public static class PageGuard
{
    public enum Verdict
    {
        /// <summary>Проверка неприменима: не наша форма тела, нечитаемая страница, законный кламп.</summary>
        Skip,
        /// <summary>Апстрим отдал ту страницу, которую просили.</summary>
        Match,
        /// <summary>🔴 Апстрим отдал ЧУЖУЮ страницу.</summary>
        Mismatch
    }

    /// <summary>
    /// Параметры, не влияющие на содержимое ряда — выброшены из ключа хранилища копий.
    /// Список повторяет CoreInit.SkipQueryKeys (их же выбрасывает Staticache по skipUids) плюс
    /// наши кеш-бастеры: один ряд просят с трёх входов (LAN, внешний, localhost), и в боевом
    /// реестре прогрева это 86 клиентских адресов на 59 разных рядов.
    /// </summary>
    static readonly HashSet<string> _volatileKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "account_email", "email", "cub_id", "box_mac", "uid", "token", "rchtype", "nws_id",
        "d1v", "zz", "cb"
    };

    /// <summary>Имя параметра-бастера, которым обходим кеш CUB на повторе.</summary>
    public const string BustKey = "d1v";

    /// <summary>Заголовок ответа с исходом проверки: "healed" / "restored" / "mismatch".</summary>
    public const string HeaderName = "X-QDL-Page";

    /// <summary>
    /// Применима ли проверка к ЗАПРОСУ. Решается ДО похода в апстрим — от неё зависит, будем ли
    /// буферизовать тело, поэтому гейт обязан быть узким и дешёвым.
    ///
    /// 🔴 Гейт по ПОДДОМЕНУ, а не по content-type: content-type до похода неизвестен, а
    /// tmdb.cub.* — это чистое API. Картинки живут на imagetmdb/cdn, реакции и лента — на самом
    /// домене (см. GetDomain в контроллере). То есть «картинки и статику не буферизуем»
    /// получается по построению, а не по угадыванию.
    /// </summary>
    public static bool IsCandidate(string subdomain, string uri)
    {
        if (uri == null || !"tmdb".Equals(subdomain, StringComparison.OrdinalIgnoreCase))
            return false;

        // /3/ — passthrough TMDB-API (детали карточки, recommendations, similar, images).
        // Там своя пагинация, к рядам каталога отношения не имеющая, и бывают тяжёлые тела.
        if (uri.StartsWith("3/", StringComparison.Ordinal) || uri.Contains("/3/", StringComparison.Ordinal))
            return false;

        // поиск — одноразовые адреса: ни кешировать, ни подставлять там нечего
        if (uri.Contains("query=", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Какую страницу просили. Параметра нет — первую (§DI: так ходит ряд главной).
    /// Параметр есть, но не число или меньше единицы — null, проверка неприменима.
    /// </summary>
    public static int? RequestedPage(string uri)
    {
        if (uri == null)
            return null;

        int q = uri.IndexOf('?');
        if (q < 0)
            return 1;

        foreach (var pair in uri.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0 || !pair.Substring(0, eq).Equals("page", StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(pair.Substring(eq + 1), out int p) && p >= 1 ? p : (int?)null;
        }

        return 1;
    }

    /// <summary>
    /// Разбор тела. hasResults обязателен: судить можно только о нашей форме, а у /blocked это
    /// вообще МАССИВ — там сторож должен быть строго no-op (тот же инвариант, что у RowFilter).
    /// </summary>
    public static (int? page, int? totalPages, bool hasResults) Shape(string json)
    {
        if (string.IsNullOrEmpty(json))
            return (null, null, false);

        try
        {
            if (JsonConvert.DeserializeObject<JToken>(json) is not JObject o)
                return (null, null, false);

            return (IntOf(o["page"]), IntOf(o["total_pages"]), o["results"] is JArray);
        }
        catch { return (null, null, false); }
    }

    static int? IntOf(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null)
            return null;

        return int.TryParse(t.ToString(), out int v) ? v : (int?)null;
    }

    /// <summary>
    /// Главный вердикт.
    ///
    /// ⚠️ Расхождение засчитываем только при requested ≤ total_pages: за последней страницей
    /// апстрим вправе клампить, и это НЕ отравление. Без этого правила сторож молотил бы повторы
    /// на каждом заходе за край ленты. У живого ряда total_pages ~427, так что боевые случаи
    /// (page=21 → 2, page=31 → 3) под правило попадают честно.
    /// </summary>
    public static Verdict Check(string uri, string json)
    {
        int? wanted = RequestedPage(uri);
        if (!wanted.HasValue)
            return Verdict.Skip;

        var (page, totalPages, hasResults) = Shape(json);

        if (!hasResults || !page.HasValue)
            return Verdict.Skip;

        if (totalPages.HasValue && wanted.Value > totalPages.Value)
            return Verdict.Skip;

        return page.Value == wanted.Value ? Verdict.Match : Verdict.Mismatch;
    }

    /// <summary>
    /// Адрес для повтора мимо кеша CUB: у них кеш на URL, и обойти его можно только уникальным
    /// параметром (§DI, «ловушка диагностики»).
    ///
    /// 🔴 page НЕ трогаем — это ровно тот же самый запрос. Никакого добора соседних страниц:
    /// именно он давал дубли в «Ещё» и отменён в 2.94 (§DA).
    /// </summary>
    public static string BustUrl(string requri, string nonce)
    {
        if (string.IsNullOrEmpty(requri))
            return requri;

        string sep = requri.Contains('?') ? "&" : "?";
        return requri + sep + BustKey + "=" + nonce;
    }

    /// <summary>
    /// Ключ хранилища «последней верной страницы» — АПСТРИМНЫЙ адрес без летучих параметров.
    /// Не наш ключ Staticache: тот включает Host, и три входа держали бы три копии одного ряда.
    /// Порядок параметров нормализуем сортировкой — бандл строит адрес динамически.
    /// </summary>
    public static string StoreKey(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        int q = uri.IndexOf('?');
        if (q < 0)
            return uri;

        string head = uri.Substring(0, q);
        var kept = new List<string>();

        foreach (var pair in uri.Substring(q + 1).Split('&'))
        {
            if (pair.Length == 0)
                continue;

            int eq = pair.IndexOf('=');
            string name = eq > 0 ? pair.Substring(0, eq) : pair;

            if (!_volatileKeys.Contains(name))
                kept.Add(pair);
        }

        kept.Sort(StringComparer.Ordinal);
        return head + "?" + string.Join("&", kept);
    }

    /// <summary>
    /// Годится ли сохранённая копия для подстановки: она есть, лежит под ту же страницу и не
    /// старше keepMinutes. keepMinutes ≤ 0 — подстановка выключена.
    /// </summary>
    public static bool Usable(int? storedPage, DateTimeOffset storedAt, int wanted, DateTimeOffset now, int keepMinutes)
    {
        if (keepMinutes <= 0 || !storedPage.HasValue || storedPage.Value != wanted)
            return false;

        return storedAt > DateTimeOffset.MinValue && now - storedAt <= TimeSpan.FromMinutes(keepMinutes);
    }

    #region предохранитель
    // Состояние держит контроллер (статика в модуле), сюда приезжает значением — иначе файл
    // нельзя было бы линковать в тесты. Образец чистого перехода — CatalogWarmup.RowQuarantine.

    /// <summary>Окно предохранителя, минут. Совпадает по духу с бакетами HealthState.</summary>
    public const int SlotMinutes = 10;

    /// <summary>Состояние окна: номер слота, сколько повторов сделано, сколько расхождений подтверждено.</summary>
    public readonly record struct Fuse(long slot, int retries, int confirmed, bool open);

    public static long SlotOf(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute / SlotMinutes;

    /// <summary>
    /// Можно ли сходить повторно. Предохранитель открыт или выбран потолок повторов — нельзя:
    /// если «врут» ВСЕ ответы, вероятнее ошиблись мы, и превращать весь каталог в
    /// некешируемый хуже, чем показать то, что отдал CUB.
    /// </summary>
    public static bool MayRetry(Fuse f, long nowSlot, int retryCap)
    {
        if (f.slot != nowSlot)
            return true;   // новое окно — счётчики обнулятся в Note

        return !f.open && (retryCap <= 0 || f.retries < retryCap);
    }

    /// <summary>Учесть исход. Со сменой окна счётчики сбрасываются — предохранитель не залипает.</summary>
    public static Fuse Note(Fuse f, long nowSlot, bool retried, bool confirmed, int retryCap, int openAfter)
    {
        if (f.slot != nowSlot)
            f = new Fuse(nowSlot, 0, 0, false);

        int retries = f.retries + (retried ? 1 : 0);
        int conf = f.confirmed + (confirmed ? 1 : 0);
        bool open = f.open || (openAfter > 0 && conf >= openAfter) || (retryCap > 0 && retries >= retryCap);

        return new Fuse(nowSlot, retries, conf, open);
    }
    #endregion
}
