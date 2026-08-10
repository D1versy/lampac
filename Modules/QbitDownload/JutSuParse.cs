using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Чистый парсер jut.su: НОЛЬ HTTP, ноль ModInit, ноль BaseController.
// Файл сознательно объявляет usings явно и ни к чему не обращается — так он
// линкуется в тестовый проект одной строкой (образец: TorrentScoring.cs).
//
// Вся разведка сайта (почему именно такие маркеры) — E:\Media-server\claude\jut\01-recon.md
// Карта «поле ↔ маркер ↔ фикстура ↔ тест»                — E:\Media-server\claude\jut\04-parser-map.md
//
// Три вещи, без которых парсер ломается молча:
//  1. Strip() <script>/<style> ДО любого regex: инлайн-CSS >100 КБ содержит слова
//     films_title / short-btn.black / all_anime_ongoing → ложные срабатывания.
//  2. StripItalics() — скрытый SEO-текст <i>Аниме </i> внутри жанров, описания и
//     кнопок серий (CSS .the_hildi i{display:none}). Без него жанр = «Аниме боевик».
//  3. Сезон берётся ТОЛЬКО из URL. <h2 class="the-anime-season"> — это АРКА:
//     у One Piece 54 арки при НУЛЕ season-N в URL (проверено фикстурой).
// ─────────────────────────────────────────────────────────────────────────────

#region DTO

/// <summary>Карточка каталога или поиска (разметка у них байт в байт одинаковая).</summary>
public sealed class JutCard
{
    public string slug;
    public int id;
    public string titleRu;
    public string titleOrig;
    public string poster;          // ⚠️ только из HTML: имя файла ≠ слаг
    public string descr;
    public int episodes;           // из aailines
    public int seasons;
    public int films;
    public bool ongoing;
    public bool viewed;            // залогинен: this_anime_is_viewed
    public int rate;               // своя оценка 1..5; 0 = нет
    public List<string> genres = new();
    public List<string> types = new();
    public List<int> years = new();
}

public sealed class JutCatalogPage
{
    public List<JutCard> items = new();
    public bool hasNext;
}

public enum JutEpKind { Episode, Film, Ova, GameOva, Special }

public sealed class JutEp
{
    public string url;             // нормализован до относительного: /slug/season-1/episode-1.html
    public string slug;
    public JutEpKind kind = JutEpKind.Episode;
    public int season = 1;         // ⚠️ ТОЛЬКО из URL; нет season-N → 1
    public int num;
    public string name;            // название серии, если сайт его даёт (чаще нет)
    public string arcRu;           // ближайший предшествующий <h2> — АРКА, не сезон
    public string arcEn;           // его title=""
    public bool seasonBoundary;    // у h2 был need_bold_season
    public bool watched;
    public int percent;            // a_dur_line

    /// <summary>Ключ серии: s1e7 / film1 / ova2 / gameova3. Совместим с ParseEp сериалов.</summary>
    public string epkey => kind switch
    {
        JutEpKind.Episode => "s" + season + "e" + num,
        JutEpKind.Film => "film" + num,
        JutEpKind.Ova => "ova" + num,
        JutEpKind.GameOva => "gameova" + num,
        _ => "sp" + num
    };
}

public sealed class JutSeasonRef
{
    public int season;
    public string url;
    public string label;
}

public sealed class JutTitle
{
    public string slug;
    public int id;
    public string titleRu;
    public string titleOrig;
    public string poster;
    public string descr;
    public double rating;
    public int ratingCount;
    public int ageRating;
    public bool ongoing;

    /// <summary>Хаб-вёрстка (Наруто): страница = каталог разделов, ни одной short-btn.</summary>
    public bool isHub;
    public List<string> hubSections = new();

    public List<string> genres = new();
    public List<string> themes = new();
    public List<int> years = new();
    public List<JutSeasonRef> seasonRefs = new();
    public List<JutEp> items = new();
}

public sealed class JutVideo
{
    public int res;
    public string url;
}

public sealed class JutEpisodePage
{
    /// <summary>null | NOT_AUTHORIZED | PARSE</summary>
    public string error;
    public List<JutVideo> videos = new();   // отсортированы по res по убыванию
    public int voiceCount;                  // сколько блоков wap_player (мультиозвучка)
    public double duration;                 // сек, 0 = неизвестно
    public double outro;                    // начало эндинга, сек; 0 = нет
    public string poster;

    public bool ok => error == null && videos.Count > 0;
}

#endregion

public static class JutSuParse
{
    #region предобработка

    static readonly RegexOptions RO = RegexOptions.Compiled | RegexOptions.IgnoreCase;
    static readonly RegexOptions ROS = RO | RegexOptions.Singleline;

    static readonly Regex _rxScript = new(@"<script\b[^>]*>.*?</script>", ROS);
    static readonly Regex _rxStyle = new(@"<style\b[^>]*>.*?</style>", ROS);
    static readonly Regex _rxItalic = new(@"<i\b[^>]*>.*?</i>", ROS);
    static readonly Regex _rxTag = new(@"<[^>]+>", RO);

    /// <summary>
    /// ⚠️ ОБЯЗАТЕЛЬНО перед любым regex по странице. Инлайн-CSS больше 100 КБ и содержит
    /// films_title / short-btn.black / all_anime_ongoing / .green — без вырезания эти слова
    /// дают ложные срабатывания где угодно.
    /// </summary>
    public static string Strip(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return _rxStyle.Replace(_rxScript.Replace(html, " "), " ");
    }

    /// <summary>Скрытый SEO-текст: &lt;i&gt;Аниме &lt;/i&gt;боевик → «боевик».</summary>
    public static string StripItalics(string s)
        => string.IsNullOrEmpty(s) ? string.Empty : _rxItalic.Replace(s, string.Empty);

    /// <summary>Теги долой, сущности раскрыть, пробелы схлопнуть.</summary>
    public static string Text(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        string s = WebUtility.HtmlDecode(_rxTag.Replace(html, " "));
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    #endregion

    #region маркеры «дошли до сайта»

    // ⚠️ ИНВАРИАНТ: Reached* отвечает «дошли ли мы до jut.su», а НЕ «залогинены ли мы».
    // Страница серии с pixel.png — это reached=true, authorized=false. Смешаешь их —
    // протухшие куки будут выглядеть как отказ прокси и сожгут бюджет выходов за один тик.

    public static bool ReachedCatalog(string html)
        => !string.IsNullOrEmpty(html) && html.Contains("all_anime_global", StringComparison.Ordinal);

    public static bool ReachedTitle(string html)
        => !string.IsNullOrEmpty(html)
           && html.Contains("itemprop=\"name\"", StringComparison.Ordinal)
           && (html.Contains("anime_fs_", StringComparison.Ordinal)
               || html.Contains("all_anime_title", StringComparison.Ordinal));

    /// <summary>Хаб-раздел (у него нет itemprop=name, зато есть разделы или кнопки серий).</summary>
    public static bool ReachedSection(string html)
        => !string.IsNullOrEmpty(html)
           && (html.Contains("mail_h", StringComparison.Ordinal)
               || html.Contains("short-btn", StringComparison.Ordinal)
               || html.Contains("all_anime_global", StringComparison.Ordinal));

    public static bool ReachedEpisode(string html)
        => !string.IsNullOrEmpty(html)
           && (html.Contains("wap_player", StringComparison.Ordinal)
               || html.Contains("id=\"my-player\"", StringComparison.Ordinal));

    #endregion

    #region каталог и поиск

    // Разбиение по ОТКРЫВАЮЩЕМУ тегу: вложенность </div> регуляркой не считается.
    static readonly Regex _rxCard = new(
        @"<div class=""all_anime_global\s*(?<cls>[^""]*)""\s+id=""anime_fs_(?<id>\d+)"">(?<body>.*?)<span class=""the_invis""", ROS);

    static readonly Regex _rxCardSlug = new(@"<a href=""/(?<slug>[^""/]+)/""", RO);
    // ⚠️ Постер — CSS background, НЕ <img>. И имя файла ≠ слаг → конструировать нельзя.
    static readonly Regex _rxCardPoster = new(@"class=""all_anime_image""\s+style=""background:\s*url\('(?<v>[^']+)'\)", RO);
    static readonly Regex _rxCardName = new(@"<div class=""aaname"">(?<v>.*?)</div>", ROS);
    static readonly Regex _rxCardLines = new(@"<div class=""aailines"">(?<v>.*?)</div>", ROS);
    static readonly Regex _rxCardTip = new(@"<div class=""tooltip_of_the_anime"" content='(?<v>.*?)'\s*>", ROS);
    static readonly Regex _rxCardRate = new(@"<span class=""av_active"">(?<v>\d)</span>", RO);

    static readonly Regex _rxTipOrig = new(@"class=""tooltip_title_in_anime"">.*?</a><br\s*/?>(?<v>[^<]+)</span>", ROS);
    static readonly Regex _rxTipDescr = new(@"</span>\s*<br\s*/?>(?<v>.*)$", ROS);

    // ⚠️ ganre — опечатка САЙТА, не наша.
    static readonly Regex _rxClsGenre = new(@"anime_ganre_(?<v>[a-z0-9-]+)", RO);
    static readonly Regex _rxClsType = new(@"anime_type_(?<v>[a-z0-9-]+)", RO);
    static readonly Regex _rxClsYear = new(@"anime_year_(?<v>[a-z0-9-]+)", RO);

    // Склонения: «12 серий» / «2 сезона» / «1173 серии» / «14 фильмов» / «1 фильм»
    static readonly Regex _rxLines = new(@"(?<n>\d+)\s*(?<w>сезон\w*|сери[йияе]\w*|фильм\w*|OVA)", RO);

    static readonly Regex _rxNextFlag = new(@"anime_page_next\s*=\s*(?<v>true|false)", RO);
    static readonly Regex _rxNextHref = new(@"<a class=""vnright"" href=""(?<v>[^""]*)""", RO);

    /// <summary>Одна карточка. Используется и каталогом, и поиском — разметка идентична.</summary>
    public static JutCard ParseCard(string cls, int id, string body)
    {
        var c = new JutCard { id = id };

        var m = _rxCardSlug.Match(body);
        if (m.Success) c.slug = m.Groups["slug"].Value;

        m = _rxCardPoster.Match(body);
        if (m.Success) c.poster = WebUtility.HtmlDecode(m.Groups["v"].Value);

        m = _rxCardName.Match(body);
        if (m.Success) c.titleRu = Text(m.Groups["v"].Value);

        m = _rxCardLines.Match(body);
        if (m.Success)
        {
            foreach (Match lm in _rxLines.Matches(Text(m.Groups["v"].Value)))
            {
                if (!int.TryParse(lm.Groups["n"].Value, out int n)) continue;
                string w = lm.Groups["w"].Value.ToLowerInvariant();
                if (w.StartsWith("сезон")) c.seasons = n;
                else if (w.StartsWith("сери")) c.episodes = n;
                else if (w.StartsWith("фильм")) c.films = n;
            }
        }

        // Онгоинг — два независимых признака, достаточно любого.
        c.ongoing = body.Contains("all_anime_ongoing", StringComparison.Ordinal)
                    || cls.Contains("anime_year_ongoing", StringComparison.Ordinal);
        c.viewed = cls.Contains("this_anime_is_viewed", StringComparison.Ordinal);

        m = _rxCardRate.Match(body);
        if (m.Success && int.TryParse(m.Groups["v"].Value, out int r)) c.rate = r;

        foreach (Match g in _rxClsGenre.Matches(cls)) c.genres.Add(g.Groups["v"].Value);
        foreach (Match t in _rxClsType.Matches(cls)) c.types.Add(t.Groups["v"].Value);
        foreach (Match y in _rxClsYear.Matches(cls))
        {
            // anime_year_* смешивает точные годы, диапазоны (2015-2023) и служебные (ongoing/before2000)
            foreach (Match yy in Regex.Matches(y.Groups["v"].Value, @"(19|20)\d{2}"))
                if (int.TryParse(yy.Value, out int yv) && !c.years.Contains(yv)) c.years.Add(yv);
        }

        m = _rxCardTip.Match(body);
        if (m.Success)
        {
            string tip = WebUtility.HtmlDecode(m.Groups["v"].Value);
            var om = _rxTipOrig.Match(tip);
            if (om.Success) c.titleOrig = Text(om.Groups["v"].Value);
            var dm = _rxTipDescr.Match(tip);
            if (dm.Success) c.descr = Text(StripItalics(dm.Groups["v"].Value));
        }

        c.years.Sort();
        return c;
    }

    /// <summary>
    /// Каталог или выдача поиска. Принимает и полную страницу, и AJAX-ответ
    /// (POST ajax_load=yes — только карточки, вдвое легче).
    /// </summary>
    public static JutCatalogPage ParseCatalog(string html)
    {
        var page = new JutCatalogPage();
        if (string.IsNullOrEmpty(html)) return page;

        // ⚠️ Флаг hasNext читаем ДО Strip(): он живёт в <script>, который Strip вырезает.
        var fm = _rxNextFlag.Match(html);
        bool? flag = fm.Success ? fm.Groups["v"].Value.Equals("true", StringComparison.OrdinalIgnoreCase) : null;
        var hm = _rxNextHref.Match(html);
        bool? href = hm.Success ? hm.Groups["v"].Value is not ("#" or "") : null;

        string clean = Strip(html);
        foreach (Match m in _rxCard.Matches(clean))
        {
            int.TryParse(m.Groups["id"].Value, out int id);
            var card = ParseCard(m.Groups["cls"].Value, id, m.Groups["body"].Value);
            if (!string.IsNullOrEmpty(card.slug)) page.items.Add(card);
        }

        // Три независимых признака конца; страница всегда по 30 карточек.
        page.hasNext = flag ?? href ?? (page.items.Count >= 30);
        if (page.items.Count == 0) page.hasNext = false;
        return page;
    }

    #endregion

    #region страница тайтла

    static readonly Regex _rxCanonical = new(@"<link rel=""canonical"" href=""https?://jut\.su/(?<slug>[^/""]+)/""", RO);
    static readonly Regex _rxAnimeId = new(@"id=""anime_fs_(?<v>\d+)""", RO);
    static readonly Regex _rxItemName = new(@"<meta itemprop=""name"" content=""(?<v>[^""]*)""", RO);
    static readonly Regex _rxItemAlt = new(@"<meta itemprop=""alternateName"" content=""(?<v>[^""]*)""", RO);
    static readonly Regex _rxRating = new(@"<span itemprop=""ratingValue"">(?<v>[^<]+)</span>", RO);
    static readonly Regex _rxRatingCnt = new(@"<span itemprop=""ratingCount"">(?<v>\d+)</span>", RO);
    static readonly Regex _rxPosterMeta = new(@"<meta property=""yandex_recommendations_image"" content=""(?<v>[^""]*)""", RO);
    static readonly Regex _rxPosterCss = new(@"class=""all_anime_title""\s+style=""background:\s*url\('(?<v>[^']+)'\)", RO);
    static readonly Regex _rxAge = new(@"age_rating_all age_rating_(?<v>\d+)", RO);
    static readonly Regex _rxOngoing = new(@"<a href=""/anime/ongoing/"">\s*<b>\s*онгоинг", RO);
    static readonly Regex _rxDescr = new(@"<p class=""under_video[^""]*""[^>]*>\s*<span>(?<v>.*?)</span>\s*</p>", ROS);
    static readonly Regex _rxFacts = new(@"<div class=""under_video_additional[^""]*""[^>]*>(?<v>.*?)</div>", ROS);

    // ⚠️ Единственное/множественное меняется: Жанр:/Жанры:, Тема:/Темы:, Год выпуска:/Годы выпуска:
    static readonly Regex _rxFactGenres = new(@"Жанр(?:ы)?:\s*(?<v>.*?)\.\s*<br", ROS);
    static readonly Regex _rxFactThemes = new(@"Тем[аы]:\s*(?<v>.*?)\.\s*<br", ROS);
    static readonly Regex _rxFactYears = new(@"Год(?:ы)? выпуска:\s*(?<v>.*?)\.\s*<br", ROS);
    static readonly Regex _rxFactLink = new(@"<a href=""/anime/(?<slug>[^/""]+)/"">(?<label>[^<]+)</a>", RO);

    static readonly Regex _rxHubH1 = new(@"<h1 class=""mail_h", RO);
    static readonly Regex _rxSeasonRef = new(@"<div class=""the_invis""><a href=""(?<url>[^""]*?/season-(?<n>\d+)/)"">(?<label>[^<]*)</a></div>", RO);
    static readonly Regex _rxHubLink = new(@"<div class=""all_anime_global[^""]*""[^>]*>\s*<a href=""(?<url>[^""]+)""", ROS);

    static readonly Regex _rxH2 = new(
        @"<h2 class=""(?<cls>[^""]*the-anime-season[^""]*)""(?:\s+title=""(?<en>[^""]*)"")?\s*>(?<ru>.*?)</h2>", ROS);

    static readonly Regex _rxAnchor = new(@"<a\s(?<attrs>[^>]*)>(?<inner>.*?)</a>", ROS);
    static readonly Regex _rxAttr = new(@"(?<k>[a-zA-Z0-9_:-]+)\s*=\s*""(?<v>[^""]*)""", RO);
    static readonly Regex _rxPercent = new(@"<span class=""a_dur_line""><span style=""width:\s*(?<v>\d+)%", RO);

    // Пять реальных шаблонов + опциональный special. href бывают и относительные, и АБСОЛЮТНЫЕ.
    static readonly Regex _rxEpUrl = new(
        @"^(?:https?://jut\.su)?/(?<slug>[^/]+)/(?:season-(?<season>\d+)/)?(?<kind>episode|film|ova|game-ova|special)-(?<num>\d+)\.html$", RO);

    static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in _rxAttr.Matches(s)) d[m.Groups["k"].Value] = m.Groups["v"].Value;
        return d;
    }

    /// <summary>Разбор ссылки на серию. null, если это не серия (напр. ссылка на страницу сезона).</summary>
    public static JutEp ParseEpUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = _rxEpUrl.Match(url.Trim());
        if (!m.Success) return null;

        var ep = new JutEp
        {
            slug = m.Groups["slug"].Value,
            num = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture),
            // ⚠️ Сезон ТОЛЬКО из URL. Нет season-N → 1 (One Piece, Hellsing, Violet Evergarden).
            season = m.Groups["season"].Success
                ? int.Parse(m.Groups["season"].Value, CultureInfo.InvariantCulture) : 1,
            kind = m.Groups["kind"].Value.ToLowerInvariant() switch
            {
                "film" => JutEpKind.Film,
                "ova" => JutEpKind.Ova,
                "game-ova" => JutEpKind.GameOva,
                "special" => JutEpKind.Special,
                _ => JutEpKind.Episode
            }
        };
        // нормализуем к относительному виду
        ep.url = "/" + ep.slug + "/"
                 + (m.Groups["season"].Success ? "season-" + ep.season + "/" : "")
                 + m.Groups["kind"].Value + "-" + ep.num + ".html";
        return ep;
    }

    /// <summary>
    /// Страница тайтла или раздела хаба. Собирает мету и плоский список серий.
    /// При isHub список серий пуст — вызывающий обязан обойти hubSections.
    /// </summary>
    public static JutTitle ParseTitle(string html, string slugFallback = null)
    {
        var t = new JutTitle { slug = slugFallback };
        if (string.IsNullOrEmpty(html)) return t;

        string clean = Strip(html);
        string noItalic = StripItalics(clean);

        var m = _rxCanonical.Match(clean);
        if (m.Success) t.slug = m.Groups["slug"].Value;

        m = _rxAnimeId.Match(clean);
        if (m.Success && int.TryParse(m.Groups["v"].Value, out int id)) t.id = id;

        // Microdata — самый чистый источник названий (og:* и JSON-LD на сайте ОТСУТСТВУЮТ).
        m = _rxItemName.Match(clean);
        if (m.Success) t.titleRu = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
        m = _rxItemAlt.Match(clean);
        if (m.Success) t.titleOrig = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();

        m = _rxRating.Match(clean);
        if (m.Success && double.TryParse(m.Groups["v"].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double rv)) t.rating = rv;
        m = _rxRatingCnt.Match(clean);
        if (m.Success && int.TryParse(m.Groups["v"].Value, out int rc)) t.ratingCount = rc;

        m = _rxPosterMeta.Match(clean);
        if (!m.Success) m = _rxPosterCss.Match(clean);
        if (m.Success) t.poster = WebUtility.HtmlDecode(m.Groups["v"].Value);

        m = _rxAge.Match(clean);
        if (m.Success && int.TryParse(m.Groups["v"].Value, out int age)) t.ageRating = age;

        t.ongoing = _rxOngoing.IsMatch(noItalic);

        m = _rxDescr.Match(clean);
        if (m.Success) t.descr = Text(StripItalics(m.Groups["v"].Value));

        // Блок фактов: обязательно по noItalic, иначе жанр = «Аниме боевик».
        var fm = _rxFacts.Match(noItalic);
        if (fm.Success)
        {
            string facts = fm.Groups["v"].Value;
            foreach (var (rx, sink) in new (Regex, List<string>)[]
                     { (_rxFactGenres, t.genres), (_rxFactThemes, t.themes) })
            {
                var g = rx.Match(facts);
                if (!g.Success) continue;
                foreach (Match lm in _rxFactLink.Matches(g.Groups["v"].Value))
                    sink.Add(Text(lm.Groups["label"].Value));
            }
            var ym = _rxFactYears.Match(facts);
            if (ym.Success)
            {
                // ⚠️ Брать ТЕКСТ ссылки, а не href: href — диапазон (/anime/2015-2023/), текст — год.
                foreach (Match lm in _rxFactLink.Matches(ym.Groups["v"].Value))
                    if (int.TryParse(Text(lm.Groups["label"].Value), out int yv) && !t.years.Contains(yv))
                        t.years.Add(yv);
            }
            t.years.Sort();
        }

        foreach (Match sm in _rxSeasonRef.Matches(noItalic))
            t.seasonRefs.Add(new JutSeasonRef
            {
                season = int.Parse(sm.Groups["n"].Value, CultureInfo.InvariantCulture),
                url = sm.Groups["url"].Value,
                label = Text(sm.Groups["label"].Value)
            });

        t.items = ParseEpisodeList(clean, noItalic);

        // Хаб (Наруто): h1.mail_h и/или ни одной кнопки серии при наличии карточек разделов.
        if (t.items.Count == 0
            && (_rxHubH1.IsMatch(clean) || clean.Contains("all_anime_global", StringComparison.Ordinal)))
        {
            foreach (Match hm in _rxHubLink.Matches(clean))
            {
                string u = hm.Groups["url"].Value;
                if (string.IsNullOrEmpty(u) || t.hubSections.Contains(u)) continue;
                t.hubSections.Add(u);
            }
            t.isHub = t.hubSections.Count > 0;
        }

        return t;
    }

    /// <summary>
    /// Плоский список серий. Два layout сразу: кнопки a.short-btn.video и список
    /// div.watch_list_item li a.pos_rel (там названия серий лежат текстом ссылки).
    /// </summary>
    public static List<JutEp> ParseEpisodeList(string clean, string noItalic = null)
    {
        var list = new List<JutEp>();
        if (string.IsNullOrEmpty(clean)) return list;
        noItalic ??= StripItalics(clean);

        // Заголовки арок с позициями — чтобы привязать каждую серию к ближайшему предыдущему.
        var arcs = new List<(int pos, string ru, string en, bool bold)>();
        foreach (Match h in _rxH2.Matches(noItalic))
            arcs.Add((h.Index, Text(h.Groups["ru"].Value),
                      h.Groups["en"].Success ? WebUtility.HtmlDecode(WebUtility.HtmlDecode(h.Groups["en"].Value)) : null,
                      h.Groups["cls"].Value.Contains("need_bold_season", StringComparison.Ordinal)));

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match a in _rxAnchor.Matches(noItalic))
        {
            var at = Attrs(a.Groups["attrs"].Value);
            if (!at.TryGetValue("href", out string href) || string.IsNullOrEmpty(href)) continue;
            at.TryGetValue("class", out string cls);
            cls ??= string.Empty;

            bool isBtn = cls.Contains("short-btn", StringComparison.Ordinal)
                         && cls.Contains("video", StringComparison.Ordinal);
            bool isList = cls.Contains("pos_rel", StringComparison.Ordinal);
            if (!isBtn && !isList) continue;

            var ep = ParseEpUrl(href);
            if (ep == null) continue;                       // ссылка на страницу сезона и т.п.
            if (!seen.Add(ep.url)) continue;                // дубли (SEO-блоки)

            // ⚠️ green/black — ЧИСТЫЙ ДЕКОР, старт чередования нестабилен. Семантики в них нет.
            ep.watched = cls.Contains("this_anime_was_watched", StringComparison.Ordinal);

            var pm = _rxPercent.Match(a.Groups["inner"].Value);
            if (pm.Success && int.TryParse(pm.Groups["v"].Value, out int pct)) ep.percent = pct;

            if (at.TryGetValue("title", out string tt) && !string.IsNullOrWhiteSpace(tt))
                ep.name = WebUtility.HtmlDecode(tt).Trim();
            else if (isList)
            {
                string inner = Text(a.Groups["inner"].Value);
                if (!string.IsNullOrWhiteSpace(inner)) ep.name = inner;
            }

            for (int i = arcs.Count - 1; i >= 0; i--)
            {
                if (arcs[i].pos >= a.Index) continue;
                ep.arcRu = arcs[i].ru;
                ep.arcEn = arcs[i].en;
                ep.seasonBoundary = arcs[i].bold;
                break;
            }

            list.Add(ep);
        }

        list.Sort((x, y) => x.kind != y.kind ? x.kind.CompareTo(y.kind)
                          : x.season != y.season ? x.season.CompareTo(y.season)
                          : x.num.CompareTo(y.num));
        return list;
    }

    #endregion

    #region страница серии

    static readonly Regex _rxWap = new(@"<span class=""[^""]*wap_player[^""]*""(?<attrs>[^>]*)>", RO);
    static readonly Regex _rxWapData = new(@"data-player-(?<res>\d+)=""(?<url>[^""]+)""", RO);
    static readonly Regex _rxSource = new(@"<source\s(?<attrs>[^>]*?)/?>", RO);
    static readonly Regex _rxVideoPoster = new(@"<video[^>]*\sposter=""(?<v>[^""]*)""", RO);
    static readonly Regex _rxDurationMeta = new(@"itemprop=""duration""\s+content=""(?<v>[^""]*)""", RO);
    static readonly Regex _rxIsoDur = new(@"^P?T?(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?$", RO);
    // ⚠️ после Base64.decode( стоит ПРОБЕЛ — без \s* regex не сработает (проверено на фикстуре)
    static readonly Regex _rxB64 = new(@"Base64\.decode\(\s*""(?<v>[A-Za-z0-9+/=]+)""", RO);
    static readonly Regex _rxOutro = new(@"video_outro_start\s*=\s*(?<v>\d+)", RO);

    static readonly Regex _rxDerou = new(@"[?&]derou=[^&]*", RO);
    static readonly Regex _rxHash2 = new(@"[?&]hash2=[^&]*", RO);

    /// <summary>
    /// Из query нужен ТОЛЬКО hash (проверено: без derou и hash2 CDN отдаёт 206).
    /// derou = наш dle_user_id — в клиентских URL и логах ему делать нечего.
    /// </summary>
    public static string CleanCdnUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        string s = _rxDerou.Replace(_rxHash2.Replace(url, string.Empty), string.Empty);
        // первый оставшийся параметр мог потерять '?'
        int q = s.IndexOf('?');
        if (q < 0)
        {
            int amp = s.IndexOf('&');
            if (amp >= 0) s = s.Substring(0, amp) + "?" + s.Substring(amp + 1);
        }
        return s;
    }

    /// <summary>Страница серии: ссылки на все качества, длительность, метка эндинга.</summary>
    public static JutEpisodePage ParseEpisode(string html)
    {
        var r = new JutEpisodePage();
        if (string.IsNullOrEmpty(html)) { r.error = "PARSE"; return r; }

        // outro лежит в base64 внутри <script> — достаём ДО Strip()
        foreach (Match b in _rxB64.Matches(html))
        {
            string dec;
            try { dec = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(b.Groups["v"].Value))); }
            catch { continue; }
            var om = _rxOutro.Match(dec);
            if (om.Success && double.TryParse(om.Groups["v"].Value, out double ov)) { r.outro = ov; break; }
        }

        string clean = Strip(html);

        var dm = _rxDurationMeta.Match(clean);
        if (dm.Success) r.duration = IsoSeconds(dm.Groups["v"].Value);

        var pm = _rxVideoPoster.Match(clean);
        if (pm.Success) r.poster = WebUtility.HtmlDecode(pm.Groups["v"].Value);

        bool pixel = false;
        var byRes = new Dictionary<int, string>();

        // Основной источник — wap_player: это НАДМНОЖЕСТВО (механика мультиозвучек).
        // При нескольких блоках берём ПЕРВЫЙ как основной (правило зафиксировано в 04-parser-map).
        var wap = _rxWap.Matches(clean);
        r.voiceCount = wap.Count;
        if (wap.Count > 0)
        {
            foreach (Match d in _rxWapData.Matches(wap[0].Groups["attrs"].Value))
            {
                string u = WebUtility.HtmlDecode(d.Groups["url"].Value);
                if (u.Contains("pixel.png", StringComparison.OrdinalIgnoreCase)) { pixel = true; continue; }
                if (int.TryParse(d.Groups["res"].Value, out int res)) byRes[res] = u;
            }
        }

        // Фолбэк — <source>: в <video> попадает только активная озвучка.
        if (byRes.Count == 0)
        {
            foreach (Match s in _rxSource.Matches(clean))
            {
                var at = Attrs(s.Groups["attrs"].Value);
                if (!at.TryGetValue("src", out string u) || string.IsNullOrEmpty(u)) continue;
                u = WebUtility.HtmlDecode(u);
                if (u.Contains("pixel.png", StringComparison.OrdinalIgnoreCase)) { pixel = true; continue; }
                int res = 0;
                if (at.TryGetValue("res", out string rs)) int.TryParse(rs, out res);
                if (res == 0 && at.TryGetValue("label", out string lb))
                    int.TryParse(new string(lb.TakeWhile(char.IsDigit).ToArray()), out res);
                if (res > 0) byRes[res] = u;
            }
        }

        if (byRes.Count == 0)
        {
            // 🔥 Заглушка pixel.png = протухли/отсутствуют куки. Разметка при этом ЦЕЛАЯ,
            //    поэтому без явного детекта это выглядело бы как «успешный парсинг».
            r.error = pixel ? "NOT_AUTHORIZED" : "PARSE";
            return r;
        }

        r.videos = byRes.OrderByDescending(k => k.Key)
                        .Select(k => new JutVideo { res = k.Key, url = CleanCdnUrl(k.Value) })
                        .ToList();
        return r;
    }

    static string Pad(string b64) => b64 + new string('=', (4 - b64.Length % 4) % 4);

    static double IsoSeconds(string v)
    {
        if (string.IsNullOrEmpty(v)) return 0;
        var m = _rxIsoDur.Match(v.Trim());
        if (!m.Success) return 0;
        double s = 0;
        if (m.Groups["h"].Success) s += int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600;
        if (m.Groups["m"].Success) s += int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60;
        if (m.Groups["s"].Success) s += int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        return s;
    }

    /// <summary>
    /// Выбор качества. preferred=0 → всегда максимум (требование владельца).
    /// Иначе preferred — ПОТОЛОК: берём наибольшее не выше него, а если все выше — наименьшее.
    /// </summary>
    public static JutVideo PickQuality(List<JutVideo> videos, int preferred)
    {
        if (videos == null || videos.Count == 0) return null;
        if (preferred <= 0) return videos.OrderByDescending(v => v.res).First();
        var fit = videos.Where(v => v.res <= preferred).OrderByDescending(v => v.res).FirstOrDefault();
        return fit ?? videos.OrderBy(v => v.res).First();
    }

    #endregion

    #region утилиты

    static readonly Regex _rxSlug = new(@"^[a-z0-9][a-z0-9-]{0,99}$", RegexOptions.Compiled);

    /// <summary>
    /// Гейт на входе КАЖДОГО роута: slug идёт в пути на диске (/qdl-data/jut/…,
    /// /downloads/jutsu/&lt;slug&gt;/) и в URL к сайту. Прецеденты traversal — §L, §AX.
    /// </summary>
    public static bool IsValidSlug(string slug) => !string.IsNullOrEmpty(slug) && _rxSlug.IsMatch(slug);

    public static string CanonicalSlug(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = _rxCanonical.Match(html);
        return m.Success ? m.Groups["slug"].Value : null;
    }

    #endregion
}
