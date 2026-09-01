using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CubProxy;

// ── Фильтр рядов каталога CUB по году выпуска (qdl 2.89) ────────────────────────────────────
// Жалоба владельца: в верхнем ряду главной «Сейчас смотрят» мусорная выдача. Разбор: ряд
// строится из ?sort=now_playing — это ЖИВОЙ поток CUB «что прямо сейчас смотрят пользователи»,
// и мусор в нём штатный. Замер по 100 карточкам: 20% с popularity<10 («Ле-Ман» 1971,
// «Преступление страсти» 1956, «Потроха девственницы» 1986). Наш прокси отдавал это байт-в-байт.
//
// Решение владельца — резать по ГОДУ, порог руками, отдельно для кино и сериалов.
//
// 🔴 Почему функция ЧИСТАЯ и лежит отдельным файлом: её линкуют в Tests/QbitDownload.Tests
// (Compile Include=… Link=…, образец JacRed\ProxyFallback.cs). Обращений к CubProxy.ModInit
// быть не должно — в тестовой сборке он конфликтует с QbitDownload.ModInit. Конфиг приходит
// параметром.
public static class RowFilter
{
    /// <summary>Порог. enabled=false — фильтр не применяется вовсе.</summary>
    public readonly record struct Conf(bool enabled, int movieYear, int tvYear);

    /// <summary>Сколько отфильтрованных карточек стараемся набрать на одну страницу ответа.</summary>
    public const int Target = 20;

    /// <summary>
    /// Ниже этого — отдаём ИСХОДНОЕ тело нетронутым.
    /// 🔴 Не косметика, а защита от двух молчаливых поломок:
    ///  • Api.partNext (app.min.js) выбрасывает ряд с пустым results СОВСЕМ — ряд исчезает с главной;
    ///  • экран «Дальше» (category_full) грузит следующую страницу только от scroll.onEnd, а
    ///    Scroll.isEnd() на незаполненном гриде даёт false — короткая первая страница = экран,
    ///    который НИКОГДА не догрузит вторую.
    /// </summary>
    public const int Floor = 5;

    /// <summary>Максимум страниц апстрима на одну нашу (замер: при 2020/2010 выживает 60%, т.е. ~1.7).</summary>
    public const int MaxPages = 3;

    // Ряды «что сейчас / новинки». Всё остальное не трогаем:
    //  • sort=top — под ним ходят ВСЕ жанровые ряды и аниме-топы;
    //  • top/hundred/*, top/fire/*, movie/popular, collections/* — исторические подборки,
    //    у top/hundred/movie фильтр съедал 15 карточек из 20;
    //  • у них у всех нет параметра sort вовсе, поэтому белого списка достаточно.
    static readonly HashSet<string> _sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "now_playing", "latest", "now", "airing"
    };

    /// <summary>
    /// Кандидат ли запрос на фильтрацию. uri — путь ПОСЛЕ домена вместе с query,
    /// ровно в том виде, в каком его собирает CubProxyController ("?sort=now_playing&amp;page=1&amp;email=").
    /// </summary>
    public static bool IsCandidate(string subdomain, string uri)
    {
        if (uri == null || !"tmdb".Equals(subdomain, StringComparison.OrdinalIgnoreCase))
            return false;

        // /3/ — passthrough TMDB-API (детали карточки). Там тоже бывает results
        // (recommendations/similar), и фильтрация сломала бы содержимое карточки.
        if (uri.StartsWith("3/", StringComparison.Ordinal) || uri.Contains("/3/", StringComparison.Ordinal))
            return false;

        // поиск — одноразовые URL, резать выдачу поиска владелец не просил
        if (uri.Contains("query=", StringComparison.OrdinalIgnoreCase))
            return false;

        return _sorts.Contains(SortOf(uri));
    }

    /// <summary>Значение параметра sort= (пустая строка, если его нет).</summary>
    public static string SortOf(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        int q = uri.IndexOf('?');
        if (q < 0)
            return string.Empty;

        foreach (var pair in uri.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair.Substring(0, eq).Equals("sort", StringComparison.OrdinalIgnoreCase))
                return pair.Substring(eq + 1);
        }

        return string.Empty;
    }

    /// <summary>
    /// Адрес соседней страницы апстрима для добора: page текущего запроса + offset.
    /// Параметра page может не быть вовсе (первая страница ряда) — тогда база 1.
    /// Кандидаты всегда несут sort=, то есть "?" в адресе уже есть, и дописываем через "&amp;".
    /// </summary>
    public static string NextPageUrl(string url, int offset)
    {
        if (string.IsNullOrEmpty(url) || offset <= 0)
            return url;

        int page = 1;
        int at = url.IndexOf("page=", StringComparison.OrdinalIgnoreCase);

        if (at >= 0)
        {
            // именно параметр, а не хвост чужого имени вроде "?mypage=2"
            char before = at == 0 ? '?' : url[at - 1];
            if (before == '?' || before == '&')
            {
                int end = at + 5;
                while (end < url.Length && char.IsDigit(url[end]))
                    end++;

                if (end > at + 5 && int.TryParse(url.Substring(at + 5, end - at - 5), out int cur) && cur > 0)
                    page = cur;

                return url.Substring(0, at + 5) + (page + offset) + url.Substring(end);
            }
        }

        return url + (url.Contains('?') ? "&" : "?") + "page=" + (page + offset);
    }

    /// <summary>
    /// Оставляем ли карточку.
    /// 🔴 Признак сериала — наличие first_air_date, а НЕ last_air_date: последний есть у всех
    /// 100 карточек ряда, включая фильмы, и дискриминатором не годится (проверено на боевом).
    /// Ровно одно из полей присутствует всегда (51 фильм / 49 сериалов), обоих сразу не бывает.
    /// Нет ни одного (в ?sort=latest такие встречаются) — ОСТАВЛЯЕМ: молча резать живую новинку
    /// из-за отсутствующей даты хуже, чем пропустить одну старую.
    /// </summary>
    public static bool Keep(JToken card, Conf conf)
    {
        if (card is not JObject o)
            return true;

        int? tv = YearOf((string)o["first_air_date"]);
        if (tv.HasValue)
            return tv.Value >= conf.tvYear;

        int? mv = YearOf((string)o["release_date"]);
        if (mv.HasValue)
            return mv.Value >= conf.movieYear;

        return true;
    }

    static int? YearOf(string date)
        => !string.IsNullOrEmpty(date) && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out int y) ? y : null;

    /// <summary>
    /// Сколько карточек страницы переживёт фильтр. Нужен контроллеру, чтобы решить,
    /// добирать ли следующую страницу апстрима, ещё до сборки итогового тела.
    /// -1 — тело не наша форма (фильтровать нечего).
    /// </summary>
    public static int CountKept(string json, Conf conf)
    {
        var results = ResultsOf(json, out _);
        return results == null ? -1 : results.Count(c => Keep(c, conf));
    }

    /// <summary>
    /// Итоговое тело. null — «оставить исходное нетронутым» (не наша форма, фильтр выключен,
    /// резать нечего, или после фильтра осталось меньше Floor).
    /// pages — страницы апстрима по порядку, первая обязательна.
    /// </summary>
    public static string Build(IReadOnlyList<string> pages, Conf conf)
    {
        if (!conf.enabled || pages == null || pages.Count == 0)
            return null;

        var first = ResultsOf(pages[0], out JObject root);
        if (first == null)
            return null;

        var kept = new List<JToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int firstCount = first.Count;

        foreach (string page in pages)
        {
            var results = ResultsOf(page, out _);
            if (results == null)
                continue;

            foreach (var card in results)
            {
                if (kept.Count >= Target)
                    break;
                if (!Keep(card, conf))
                    continue;

                // Дедуп обязателен: добор тянет соседние страницы апстрима, а CUB между
                // вызовами переставляет выдачу — одна карточка легко попадает на обе.
                string id = (card as JObject)?["id"]?.ToString();
                if (id != null && !seen.Add(id))
                    continue;

                kept.Add(card);
            }

            if (kept.Count >= Target)
                break;
        }

        // Ничего не вырезалось — не переписываем тело зря (и не трогаем Content-Length).
        if (pages.Count == 1 && kept.Count == firstCount)
            return null;

        if (kept.Count < Floor)
            return null;

        // 🔴 page / total_pages / total_results оставляем КАК ПРИШЛИ. На них висит кнопка
        // «Дальше» (гейт pages > 1) и весь цикл пагинации; пересчёт под фильтр сломал бы
        // догрузку. CUB и сам отдаёт их нестабильно — у одного ряда 15 на page=1 и 21 на page=2.
        root["results"] = new JArray(kept);
        return JsonConvert.SerializeObject(root);
    }

    /// <summary>
    /// results[] тела, если это наша форма { "results": [...] }.
    /// null — что угодно другое: у /blocked это МАССИВ, а не объект, и фильтр обязан быть
    /// на нём строго no-op.
    /// </summary>
    static JArray ResultsOf(string json, out JObject root)
    {
        root = null;
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            if (JsonConvert.DeserializeObject<JToken>(json) is not JObject o)
                return null;

            root = o;
            return o["results"] as JArray;
        }
        catch { return null; }
    }
}
