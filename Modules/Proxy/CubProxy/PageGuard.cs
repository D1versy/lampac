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
// 🔴 Логика намеренно ПРОДУБЛИРОВАНА в Modules/QbitDownload/CatalogWarmup.cs (аудит по реальным
// клиентским ключам — он видит HIT-ы, которых контроллер не видит). Модули компилируются в
// разные сборки и типами связаться не могут. Обе копии обязаны судить одинаково — это стережёт
// тест Сторож_страницы_в_двух_модулях_судит_одинаково на общем корпусе сырых тел; правишь здесь —
// правь и там.
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

    /// <summary>Что делать с ответом — решение вынесено в чистую функцию Decide, чтобы его можно было тестировать.</summary>
    public enum Action
    {
        /// <summary>Расхождения нет — отдаём и кешируем как обычно.</summary>
        PassThrough,
        /// <summary>Расхождение, но предохранитель открыт: отдаём то, что дал CUB, с КОРОТКИМ TTL, копию не подставляем.</summary>
        Fuse,
        /// <summary>Повтор мимо кеша CUB вернул верную страницу — отдаём её, кешируем коротко.</summary>
        Healed,
        /// <summary>Повтор не помог, есть свежая копия — отдаём её, в кеш не кладём.</summary>
        Restored,
        /// <summary>Повтор не помог, копии нет — отдаём чужую страницу как есть, в кеш не кладём.</summary>
        MismatchNoCache
    }

    /// <summary>
    /// Потолок тела, с которым сторож вообще работает. Ряд каталога — 7–22 КБ по замеру боевого;
    /// всё, что крупнее, у tmdb.cub.* не наше. Один и тот же потолок — и для буферизации в
    /// контроллере (дальше тело уходит потоком), и для копий PageStore.
    /// </summary>
    public const int MaxBodyBytes = 2 * 1024 * 1024;

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

    /// <summary>Заголовок ответа с исходом проверки: match / healed / restored / mismatch / fuse.</summary>
    public const string HeaderName = "X-QDL-Page";

    /// <summary>
    /// Применима ли проверка к ЗАПРОСУ. Решается ДО похода в апстрим — от неё зависит, будем ли
    /// буферизовать тело, поэтому гейт обязан быть узким и дешёвым. Это то же самое «ряд ли это
    /// каталога», что и у фильтра по году — одно правило, один источник (RowFilter.IsCatalogApi):
    /// картинки живут на imagetmdb/cdn, детали карточки — под /3/, поиск — одноразовый.
    /// </summary>
    public static bool IsCandidate(string subdomain, string uri) => RowFilter.IsCatalogApi(subdomain, uri);

    /// <summary>
    /// Какую страницу просили. Параметра нет — первую (§DI: так ходит ряд главной).
    /// Параметр есть, но не число, меньше единицы или встречается дважды с РАЗНЫМИ значениями —
    /// null: у фреймворков «выигрывает первый» и «выигрывает последний» встречаются оба, и судить
    /// на таком адресе значит выдумывать ожидание.
    /// </summary>
    public static int? RequestedPage(string uri)
    {
        if (uri == null)
            return null;

        int q = uri.IndexOf('?');
        if (q < 0)
            return 1;

        int? found = null;

        foreach (var pair in uri.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0 || !pair.Substring(0, eq).Equals("page", StringComparison.OrdinalIgnoreCase))
                continue;

            int? p = int.TryParse(pair.Substring(eq + 1), out int v) && v >= 1 ? v : (int?)null;

            if (found.HasValue && found != p)
                return null;

            if (!p.HasValue)
                return null;

            found = p;
        }

        return found ?? 1;
    }

    /// <summary>
    /// Разбор тела. results = -1, если это не наша форма (у /blocked это вообще МАССИВ — там сторож
    /// обязан быть строго no-op, тот же инвариант, что у RowFilter); иначе число карточек.
    ///
    /// ⚠️ Числа принимаем ТОЛЬКО целые (JSON integer) и строки с целым — не 1.0, не 1e0. Правило
    /// общее с копией в CatalogWarmup: на дробных числах копии иначе расходились (Newtonsoft
    /// печатает 1.0 как "1", System.Text.Json TryGetInt32 на 1.0 падает).
    /// </summary>
    public static (int? page, int? totalPages, int results) Shape(string json)
    {
        if (string.IsNullOrEmpty(json))
            return (null, null, -1);

        try
        {
            if (JsonConvert.DeserializeObject<JToken>(json) is not JObject o)
                return (null, null, -1);

            int results = o["results"] is JArray arr ? arr.Count : -1;
            return (IntOf(o["page"]), IntOf(o["total_pages"]), results);
        }
        catch { return (null, null, -1); }
    }

    static int? IntOf(JToken t)
        => t is JValue v && (v.Type is JTokenType.Integer or JTokenType.String)
           && int.TryParse(v.ToString(), out int n) ? n : (int?)null;

    /// <summary>
    /// Главный вердикт. Расхождение засчитываем только при requested ≤ total_pages — за последней
    /// страницей апстрим вправе клампить, и это НЕ отравление.
    ///
    /// 🔴 Но total_pages берётся из ПОДОЗРИТЕЛЬНОГО тела, а у одного ряда он нестабилен (15 на
    /// page=1 и 21 на page=2 — §CW). Чужая страница 2 с total_pages=15 на запрос page=18 прошла
    /// бы как «кламп» и примёрзла на 3 часа. Поэтому кламп признаём только когда тело на него
    /// ПОХОЖЕ: пришла первая или последняя страница. total_pages=0 — пустая лента, судить нечего.
    /// </summary>
    public static Verdict Check(string uri, string json) => Judge(uri, json).verdict;

    /// <summary>То же, что Check, но с цифрами — чтобы контроллер и аудит не разбирали тело дважды.</summary>
    public static (Verdict verdict, int? wanted, int? got, int results) Judge(string uri, string json)
    {
        int? wanted = RequestedPage(uri);
        var (page, totalPages, results) = Shape(json);

        if (!wanted.HasValue || results < 0 || !page.HasValue)
            return (Verdict.Skip, wanted, page, results);

        // Кламп признаём, только если тело на него похоже (пришла первая или последняя страница).
        // total_pages ≤ 0 — мусор, судим по page; ноль при пустых results — пустая лента, судить нечего.
        if (totalPages is >= 1 && wanted.Value > totalPages.Value && (page.Value == 1 || page.Value == totalPages.Value))
            return (Verdict.Skip, wanted, page, results);

        if (totalPages == 0 && results == 0)
            return (Verdict.Skip, wanted, page, results);

        return (page.Value == wanted.Value ? Verdict.Match : Verdict.Mismatch, wanted, page, results);
    }

    /// <summary>
    /// Адрес для повтора мимо кеша CUB: у них кеш на URL, и обойти его можно только уникальным
    /// параметром (§DI, «ловушка диагностики»).
    ///
    /// 🔴 page НЕ трогаем — это ровно тот же самый запрос. Никакого добора соседних страниц:
    /// именно он давал дубли в «Ещё» и отменён в 2.94 (§DA). Тест держит равенство
    /// `requri + sep + "d1v=" + nonce` дословно.
    /// </summary>
    public static string BustUrl(string requri, string nonce)
    {
        if (string.IsNullOrEmpty(requri))
            return requri;

        string sep = requri.Contains('?') ? "&" : "?";
        return requri + sep + BustKey + "=" + nonce;
    }

    /// <summary>
    /// Ключ хранилища «последней верной страницы» — АПСТРИМНЫЙ адрес без летучих параметров,
    /// с нормализованным номером страницы. Не наш ключ Staticache: тот включает Host, и три входа
    /// держали бы три копии одного ряда.
    ///
    /// Ряд главной (без page) и «Ещё» стр. 1 (page=1) — одна и та же апстримная страница, поэтому
    /// page дописывается всегда: копия, снятая с любого из них, годится обоим — а главный
    /// пострадавший как раз ряд главной. Порядок параметров нормализуем сортировкой.
    /// </summary>
    public static string StoreKey(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        int q = uri.IndexOf('?');
        string head = q < 0 ? uri : uri.Substring(0, q);
        var kept = new List<string>();

        if (q >= 0)
        {
            foreach (var pair in uri.Substring(q + 1).Split('&'))
            {
                if (pair.Length == 0)
                    continue;

                int eq = pair.IndexOf('=');
                string name = eq > 0 ? pair.Substring(0, eq) : pair;

                if (name.Equals("page", StringComparison.OrdinalIgnoreCase) || _volatileKeys.Contains(name))
                    continue;

                kept.Add(pair);
            }
        }

        int? page = RequestedPage(uri);
        if (page.HasValue)
            kept.Add("page=" + page.Value);

        kept.Sort(StringComparer.Ordinal);
        return kept.Count == 0 ? head : head + "?" + string.Join("&", kept);
    }

    /// <summary>
    /// Годится ли сохранённая копия для подстановки: она есть, лежит под ту же страницу и не
    /// старше keepMinutes. keepMinutes ≤ 0 — подстановка выключена. Возраст «в будущем» (часы
    /// уехали после падения по питанию) не выбрасываем.
    /// </summary>
    public static bool Usable(int? storedPage, DateTimeOffset storedAt, int wanted, DateTimeOffset now, int keepMinutes)
    {
        if (keepMinutes <= 0 || !storedPage.HasValue || storedPage.Value != wanted)
            return false;

        return storedAt > DateTimeOffset.MinValue && now - storedAt <= TimeSpan.FromMinutes(keepMinutes);
    }

    /// <summary>
    /// Что делать с ответом. Единственное место, где сходятся вердикт, предохранитель, исход
    /// повтора и наличие копии — и оно чистое, чтобы ветка «предохранитель открыт» была под тестом,
    /// а не жила только в контроллере.
    ///
    /// Открытый предохранитель означает «врут ВСЕ ответы — вероятнее ошиблись мы»: сторож только
    /// наблюдает. Ни копии, ни выключенного кеша — иначе весь каталог стал бы некешируемым, а
    /// каждый клиентский запрос — походом в CUB. Только короткий TTL, чтобы окно ущерба было конечным.
    /// </summary>
    public static Action Decide(Verdict verdict, bool fuseOpen, bool healed, bool hasCopy)
    {
        if (verdict != Verdict.Mismatch)
            return Action.PassThrough;

        if (healed)
            return Action.Healed;

        if (fuseOpen)
            return Action.Fuse;

        return hasCopy ? Action.Restored : Action.MismatchNoCache;
    }

    #region предохранитель
    // Состояние держит контроллер (статика в модуле), сюда приезжает значением — иначе файл
    // нельзя было бы линковать в тесты. Образец чистого перехода — CatalogWarmup.RowQuarantine.
    //
    // Два независимых ограничителя:
    //   • потолок ПОВТОРОВ за окно (retryCap) — защита CUB и себя от шторма лишних походов;
    //   • предохранитель (openAfter) — по числу ПОДТВЕРЖДЁННЫХ расхождений: повтор мимо кеша
    //     вернул тело, и номер страницы всё равно чужой. Значит врёт не кеш, а сам ответ — и
    //     вероятнее, что ошиблись мы (CUB сменил семантику page). Тогда сторож только наблюдает.
    // ⚠️ Без повторов (pageGuardRetry=false) подтверждать нечем, и предохранитель не взводится —
    // это осознанно: выключенный повтор = «ловить и подставлять, наружу не ходить».

    /// <summary>Окно предохранителя, минут. Совпадает по духу с бакетами HealthState.</summary>
    public const int SlotMinutes = 10;

    /// <summary>Состояние окна: номер слота, сколько повторов сделано, сколько расхождений подтверждено.</summary>
    public readonly record struct Fuse(long slot, int retries, int confirmed);

    public static long SlotOf(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute / SlotMinutes;

    /// <summary>Открыт ли предохранитель в этом окне. Выводится из счётчиков — липкого флага нет: счётчики в окне монотонны.</summary>
    public static bool Open(Fuse f, long nowSlot, int openAfter)
        => f.slot == nowSlot && openAfter > 0 && f.confirmed >= openAfter;

    /// <summary>
    /// Можно ли сходить повторно. retryCap ≤ 0 — повторов НЕТ (ноль везде в этой секции значит
    /// «выключено»: keepMinutes 0 — без подстановки, suspectMinutes 0 — не кешировать).
    /// </summary>
    public static bool MayRetry(Fuse f, long nowSlot, int retryCap, int openAfter)
    {
        if (retryCap <= 0)
            return false;

        if (f.slot != nowSlot)
            return true;   // новое окно — счётчики обнулятся в Note

        return !Open(f, nowSlot, openAfter) && f.retries < retryCap;
    }

    /// <summary>
    /// Учесть событие. Повтор резервируется ДО похода (retried:true), исход учитывается ПОСЛЕ
    /// (confirmed) — иначе N параллельных расхождений перебирали бы потолок на величину параллелизма.
    /// Со сменой окна счётчики сбрасываются; назад окно не откатывается (запрос, начатый в прошлом
    /// слоте, не должен обнулять уже начатый новый).
    /// </summary>
    public static Fuse Note(Fuse f, long nowSlot, bool retried, bool confirmed)
    {
        if (nowSlot > f.slot)
            f = new Fuse(nowSlot, 0, 0);

        return new Fuse(f.slot, f.retries + (retried ? 1 : 0), f.confirmed + (confirmed ? 1 : 0));
    }
    #endregion
}
