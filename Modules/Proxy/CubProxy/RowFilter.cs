using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace CubProxy;

// ── Фильтр рядов каталога CUB по году выпуска (qdl 2.89) ────────────────────────────────────
// Жалоба владельца: в верхнем ряду главной «Сейчас смотрят» мусорная выдача. Разбор: ряд
// строится из ?sort=now_playing — это ЖИВОЙ поток CUB «что прямо сейчас смотрят пользователи»,
// и мусор в нём штатный. Замер по 100 карточкам: 20% с popularity<10 («Ле-Ман» 1971,
// «Преступление страсти» 1956, «Потроха девственницы» 1986). Наш прокси отдавал это байт-в-байт.
//
// Решение владельца — резать по ГОДУ, порог руками, отдельно для кино и сериалов.
//
// 🔴 qdl 2.94: наша страница N — это РОВНО апстримная страница N, один к одному. До 2.94 здесь
// был добор соседних страниц (N, N+1, N+2) до целевых 20 карточек, и он давал дубли: хвост
// нашей страницы N брался с апстримной N+1, а наша N+1 начиналась с той же N+1 с нуля. Замер
// боевого сервера: перекрытие 4 / 8 / 5 карточек из 20 между соседними страницами — владелец
// видел это в «Ещё» как «каждый фильм двумя строчками по 6 карточек». Короткую страницу лечит
// КЛИЕНТ (патчи grid-dedup-build/grid-dedup-next + насос gridPump в qdl.js), возвращать добор
// сюда нельзя.
//
// 🔴 Почему функция ЧИСТАЯ и лежит отдельным файлом: её линкуют в Tests/QbitDownload.Tests
// (Compile Include=… Link=…, образец JacRed\ProxyFallback.cs). Обращений к CubProxy.ModInit
// быть не должно — в тестовой сборке он конфликтует с QbitDownload.ModInit. Конфиг приходит
// параметром.
public static class RowFilter
{
    /// <summary>Порог. enabled=false — фильтр не применяется вовсе.</summary>
    public readonly record struct Conf(bool enabled, int movieYear, int tvYear);

    /// <summary>
    /// Ниже этого — отдаём ИСХОДНОЕ тело нетронутым (со старьём внутри: это меньшее зло).
    /// 🔴 Api.partNext выбрасывает ряд с пустым results СОВСЕМ — «Сейчас смотрят» просто
    /// исчезает с главной, и это выглядит не как поломка, а как «нет такого ряда». Прямой
    /// прыжок на страницу через Pagination строит грид через Items.onBuild, а там
    /// пустой results даёт экран «Пусто».
    ///
    /// ⚠️ Второе прежнее обоснование («короткая страница = экран, который не догрузится»)
    /// с qdl 2.94 живёт НА КЛИЕНТЕ: патчи grid-dedup-build / grid-dedup-next + насос gridPump
    /// в qdl.js. Лечить его добором соседних страниц НЕЛЬЗЯ — именно добор давал перекрытие
    /// 8 карточек из 20.
    /// </summary>
    public const int Floor = 5;

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
    /// Итоговое тело ОДНОЙ страницы. null — «оставить исходное нетронутым» (не наша форма,
    /// фильтр выключен, резать нечего, или после фильтра осталось меньше Floor).
    ///
    /// 🔴 Кап на количество отдаваемых карточек здесь ЗАПРЕЩЁН (qdl 2.94). Наша страница N —
    /// это ровно апстримная N, и всё, что мы обрежем, не появится больше НИГДЕ: следующий
    /// запрос клиента уйдёт на N+1. Прежний Target=20 был безобиден только в паре с добором,
    /// где обрезанный хвост показывался на следующей нашей странице — ценой перекрытия.
    /// </summary>
    public static string Build(string json, Conf conf)
    {
        if (!conf.enabled)
            return null;

        var results = ResultsOf(json, out JObject root);
        if (results == null)
            return null;

        var kept = new List<JToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in results)
        {
            if (!Keep(card, conf))
                continue;

            // Страховка: CUB иногда повторяет карточку внутри одной страницы. Дедуп МЕЖДУ
            // страницами живёт на клиенте (gridNext в qdl.js) — здесь состояния нет и не будет.
            string id = (card as JObject)?["id"]?.ToString();
            if (id != null && !seen.Add(id))
                continue;

            kept.Add(card);
        }

        // Ничего не вырезалось — не переписываем тело зря (и не трогаем Content-Length).
        if (kept.Count == results.Count)
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
