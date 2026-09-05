using Newtonsoft.Json.Linq;
using Shared.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace QbitDownload;

// Контекст запроса для скоринга (собирается из TMDB-контекста /qdl/search).
public sealed class ScoreCtx
{
    public string titleNorm;        // SearchNameTo.Convert(русское название)
    public string originalNorm;     // SearchNameTo.Convert(оригинальное название)
    public int year;                // 0 = неизвестен
    public bool isSerial;
    public int wantSeason;          // number_of_seasons с карточки; 0 = неизвестен
    public int preferredQuality = 2160;
    public DateTime now = DateTime.UtcNow;   // инъецируется в тестах
}

// «N из M» из названия раздачи: сколько серий выложено / сколько всего.
public sealed class EpCoverage
{
    public int have;
    public int total;               // 0 = неизвестно («5 из ??»)
    public bool ongoing => total > 0 && have < total;
}

public sealed class ScoreResult
{
    public double score;
    public bool nameMiss;           // название карточки в title не найдено — не может быть ⭐
    public bool nameGateOff;        // имя карточки нормализовалось в пустоту — сверять НЕЧЕМ, ⭐ запрещена
    public bool typeMiss;           // категория/вид явно не совпали с запросом
    public EpCoverage ep;           // распарсенное «N из M» (null если нет)
    public List<string> why = new List<string>();
}

// Чистый скоринг раздач: релевантность (имя/год/тип/сезон/полнота) доминирует над сидами.
// Без HTTP/IO — тестируется напрямую. Graceful degradation: любые отсутствующие поля
// (date/cats/quality; в режиме typesearch=jackett блока Info нет) дают нейтральный балл,
// и порядок деградирует к «по сидам».
public static class TorrentScoring
{
    #region парсеры названия
    // «1-8 из 12» / «8 из 12» / «[09 из 24]» / «Серии: 1-16 (16)»; «из XX» (неизвестный итог) → total=0.
    // Гард: «Сезон 1 из 3» — это сезоны, не серии (смотрим слово перед числом).
    // Формат kinozal: «(3 сезон: 1-5 серии из 10)». Между диапазоном и «из» стоит слово,
    // поэтому _covRangeRx его не видел, а стоящее ПЕРЕД «сезон:» ещё и включало season-гард —
    // полнота серий у kinozal терялась целиком. Явное «серии» двусмысленность снимает,
    // поэтому пробуем этот шаблон первым и без гарда.
    static readonly Regex _covSeriesWordRx = new Regex(@"(?i)(?:\d{1,3}\s*-\s*)?(\d{1,3})\s*сери[ияйе]\w*\s*из\s*(\d{1,4}|[?xXхХ]+)", RegexOptions.Compiled);
    static readonly Regex _covRangeRx = new Regex(@"(\d{1,3})\s*-\s*(\d{1,3})\s*из\s*(\d{1,4}|[?xXхХ]+)", RegexOptions.Compiled);
    static readonly Regex _covPlainRx = new Regex(@"(?<![-\d])(\d{1,3})\s*из\s*(\d{1,4}|[?xXхХ]+)", RegexOptions.Compiled);
    static readonly Regex _covSeriesParenRx = new Regex(@"(?i)сери[ия][:\s]*\s*(?:\d{1,3}\s*-\s*)?(\d{1,3})\s*\(\s*(\d{1,4})\s*\)", RegexOptions.Compiled);
    static readonly Regex _seasonWordBeforeRx = new Regex(@"(?i)сезон\w*\s*[:№#]?\s*$", RegexOptions.Compiled);
    // симметрия: «сезон» может стоять ПОСЛЕ чисел («10 из 10 сезонов», «2 из 5 сезонов») —
    // тогда «N из M» это сезоны, а не серии (as «Сезон 1 из 3» уже ловит before-guard)
    static readonly Regex _seasonWordAfterRx = new Regex(@"(?i)^\s*(из\s*(\d{1,4}|[?xXхХ]+)\s*)?сезон", RegexOptions.Compiled);

    static bool SeasonContext(string title, int matchIndex, int matchEnd)
    {
        int from = Math.Max(0, matchIndex - 12);
        if (_seasonWordBeforeRx.IsMatch(title.Substring(from, matchIndex - from))) return true;
        int len = Math.Min(14, title.Length - matchEnd);
        return len > 0 && _seasonWordAfterRx.IsMatch(title.Substring(matchEnd, len));
    }

    static int ParseTotal(string g) => int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : 0;

    public static EpCoverage ParseEpCoverage(string title)
    {
        string t = title ?? "";

        var sw = _covSeriesWordRx.Match(t);   // «1-5 серии из 10» (kinozal)
        if (sw.Success)
        {
            int have = int.Parse(sw.Groups[1].Value), total = ParseTotal(sw.Groups[2].Value);
            if (have <= 999 && (total == 0 || have <= total))
                return new EpCoverage { have = have, total = total };
        }

        foreach (Match m in _covRangeRx.Matches(t))
        {
            if (SeasonContext(t, m.Index, m.Index + m.Length)) continue;
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value), total = ParseTotal(m.Groups[3].Value);
            if (b < a || (total > 0 && b > total) || b > 999) continue;
            return new EpCoverage { have = b, total = total };
        }
        foreach (Match m in _covPlainRx.Matches(t))
        {
            if (SeasonContext(t, m.Index, m.Index + m.Length)) continue;
            int a = int.Parse(m.Groups[1].Value), total = ParseTotal(m.Groups[2].Value);
            if ((total > 0 && a > total) || a > 999) continue;
            return new EpCoverage { have = a, total = total };
        }
        var sp = _covSeriesParenRx.Match(t);   // «Серии: 1-16 (16)»
        if (sp.Success)
        {
            int a = int.Parse(sp.Groups[1].Value), total = ParseTotal(sp.Groups[2].Value);
            if (a <= total && a <= 999) return new EpCoverage { have = a, total = total };
        }
        return null;
    }

    // «2 сезон» / «Сезон: 1-3» / «Сезоны 1-3» / «S02» / «S01-S03» / «Season 2» → множество номеров сезонов.
    // Индексы 4 и 6 («сезон[:] A-B» / «season A-B») ДВУСМЫСЛЕННЫ и прикрыты гардом RangeIsEpisodes —
    // см. комментарий там.
    static readonly Regex[] _seasonRx =
    {
        new Regex(@"(?i)(?<![A-Za-z0-9])S(\d{1,2})\s*-\s*S?(\d{1,2})(?![0-9])", RegexOptions.Compiled),
        new Regex(@"(?i)(?<![A-Za-z0-9])S(\d{1,2})(?![0-9\-])", RegexOptions.Compiled),
        new Regex(@"(?i)(\d{1,2})\s*-\s*(\d{1,2})\s*сезон", RegexOptions.Compiled),
        new Regex(@"(?i)(?<![-\d])(\d{1,2})\s*сезон", RegexOptions.Compiled),
        new Regex(@"(?i)сезон\w*\s*[:№#]?\s*(\d{1,2})\s*-\s*(\d{1,2})(?![0-9])", RegexOptions.Compiled),
        new Regex(@"(?i)сезон\w*\s*[:№#]?\s*(\d{1,2})(?![0-9\-])", RegexOptions.Compiled),
        new Regex(@"(?i)season\s*(\d{1,2})\s*-\s*(\d{1,2})(?![0-9])", RegexOptions.Compiled),
        new Regex(@"(?i)season\s*(\d{1,2})(?![0-9\-])", RegexOptions.Compiled),
    };

    // Формат kinozal «(2 сезон: 1-10 серии из 10)»: диапазон после слова «сезон» — это СЕРИИ, а не
    // сезоны. Зеркало гарда _covSeriesWordRx выше (там та же двусмысленность ломала полноту серий,
    // claude/06 §AZ), только в другую сторону. Без гарда раздача ВТОРОГО сезона отдавала {1..10},
    // множество накрывало любой охотимый сезон — сезонный гейт охоты (FilterDonorCandidates)
    // пропускал её донором, скоринг давал +12 «сезон совпал», и в 3-й сезон «Укрытия» приехали
    // серии 2-го (инцидент 2026-08-09).
    // Два независимых признака «это серии»:
    //   • номер сезона уже назван ПЕРЕД словом («2 сезон: 1-10») — второй раз его не объявляют;
    //   • сразу после диапазона стоит слово серий («1-10 серии»).
    // Слово «из» признаком НЕ является: «Сезон 1-3 из 5» — легитимные сезоны.
    static readonly Regex _numBeforeSeasonRx = new Regex(@"(?<!\d)\d{1,2}\s*$", RegexOptions.Compiled);
    static readonly Regex _epWordAfterRangeRx = new Regex(@"(?i)^\s*(?:сери[ияйе]\w*|серий|эпизод\w*|episodes?\b)", RegexOptions.Compiled);

    static bool RangeIsEpisodes(string t, Match m)
    {
        int from = Math.Max(0, m.Index - 6);
        if (_numBeforeSeasonRx.IsMatch(t.Substring(from, m.Index - from))) return true;
        int end = m.Index + m.Length, len = Math.Min(12, t.Length - end);
        return len > 0 && _epWordAfterRangeRx.IsMatch(t.Substring(end, len));
    }

    public static List<int> ParseSeasons(string title)
    {
        var set = new SortedSet<int>();
        string t = title ?? "";
        for (int i = 0; i < _seasonRx.Length; i++)
        {
            foreach (Match m in _seasonRx[i].Matches(t))
            {
                if ((i == 4 || i == 6) && RangeIsEpisodes(t, m)) continue;   // «N сезон: A-B серии» — это серии
                int a = int.Parse(m.Groups[1].Value);
                int b = m.Groups.Count > 2 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
                if (b < a || b - a > 30 || a <= 0) continue;
                for (int s = a; s <= b; s++) set.Add(s);
            }
        }
        return set.ToList();
    }

    // ── отсев «левых» раздач ────────────────────────────────────────────────
    // Скоринг даёт +25 за одно лишь УПОМИНАНИЕ названия в строке, поэтому аудиокниги
    // «Фрэнк Герберт - Дюна 4 (2021) MP3» и саундтреки набирали 42+ и лезли в середину
    // выдачи фильма. Категории тут не спасают: у 790 из 913 раздач cats пустые.
    static readonly Regex _nonVideoRx = new Regex(
        // «МР3» здесь дважды: латиницей и КИРИЛЛИЦЕЙ (М U+041C, Р U+0420) — аудиокниги пишут его
        // именно так, и латинский паттерн их не видел. В снимках поиска таких строк набралось 125;
        // на карточках с коротким названием они доезжали до списка (в «Холоде» — 7 аудиокниг).
        @"(?i)(?<![a-z0-9])(mp3|МР3|flac|ape\b|wav\b|m4b|epub|fb2|mobi|djvu|pdf|аудиокниг\w*|дискограф\w*|саундтрек\w*|ost)(?![a-z0-9])",
        RegexOptions.Compiled);

    // Видео-признак. Проверяем его ОБЯЗАТЕЛЬНО: иначе «Дюна (2021) BDRemux + OST» —
    // нормальный релиз с бонусной дорожкой — улетел бы в мусор из-за одного слова.
    static readonly Regex _videoMarkRx = new Regex(
        @"(?i)(?<![a-z0-9])(bd\s?rip|bd\s?remux|blu-?ray|web-?dl|web-?rip|web-?dlrip|hd\s?rip|dvd\s?rip|dvd\d?|hdtv\w*|tvrip|camrip|ts\b|satrip|remux|[xh]\.?\s?26[45]|hevc|avc|av1|xvid|divx|\d{3,4}p|2160|1080|720|480)(?![a-z0-9])",
        RegexOptions.Compiled);

    /// <summary>
    /// «Левая» раздача — то, что не может быть искомым фильмом/сериалом в принципе:
    /// не-видео контент (музыка, книги, аудиокниги, саундтреки) без единого видео-признака.
    /// </summary>
    public static bool IsNonVideo(string title)
        => !string.IsNullOrEmpty(title) && _nonVideoRx.IsMatch(title) && !_videoMarkRx.IsMatch(title);

    /// <summary>
    /// Другой фильм той же франшизы: в названии ЗАЯВЛЕН год, и ни один из заявленных не сходится
    /// с годом карточки. «Дюна: Часть вторая (2024)» в выдаче «Дюны (2021)» — именно этот случай.
    ///
    /// Только для ФИЛЬМОВ: у сериала раздачи законно датируются годами позже старта (сезоны).
    /// Год в названии не заявлен → НЕ режем: отсутствие данных не улика.
    /// Достаточно одного совпадения из всех найденных — «Бегущий по лезвию 2049 (2017)» даёт
    /// годы {2049, 2017}, и 2017 спасает раздачу от ложного отсева.
    /// </summary>
    public static bool IsOtherMovieYear(string title, int wantYear, bool isSerial)
    {
        if (isSerial || wantYear <= 0 || string.IsNullOrEmpty(title)) return false;
        var years = ParseYears(title);
        if (years.Count == 0) return false;
        return !years.Any(y => Math.Abs(y - wantYear) <= 1);
    }

    // ── язык раздачи ────────────────────────────────────────────────────────
    // Кириллица в названии — сама по себе надёжный признак русского релиза
    // (так распознаются почти все раздачи русских трекеров).
    static readonly Regex _cyrillicRx = new Regex(@"[А-Яа-яЁё]", RegexOptions.Compiled);

    // Латиничные имена (в основном из bitmagnet) — по маркерам озвучки и русским
    // релиз-группам: краулер помечает такие раздачи как ["en"], хотя дубляж русский.
    // ⚠️ Голого «dub» здесь НЕТ (qdl 2.107): он делал русскими «[English Dub]» (44 строки Гносии в
    // DHT), «Hindi (HQ-Dub)». Русский дубляж без студии пишут как rus.dub/dub.rus/rudub — они есть.
    // ⚠️ Голого «ru» тоже нет: с ASCII-границами он ловит «Ruža pre nevestu», «To Love-Ru», пиньинь
    // и «ru.subs» (русские СУБТИТРЫ при английском звуке) — 189 строк по базе. Допускается только в
    // паре с другим языком (Ru.Eng / Eng.Ru): так обозначают многодорожечные русские релизы.
    static readonly Regex _ruMarkRx = new Regex(
        @"(?i)(?<![a-z0-9])(rus|rux|rus\.?dub|dub\.?rus|rudub|mvo|dvo|avo|kp|hdclub|hdt|new-?team|sofcj|kinoreys|lostfilm|newstudio|hdrezka|kubik|jaskier|tvshows|baibako|coldfilm|amedia|selezen|elektri4ka|kerob|scarabey|hellywood|exkinoray|ultradox|newcomers|rhs|red[\s._-]?head[\s._-]?sound|anilibria|anidub|animedia|shiza|dreamcast|studio[\s._-]?band|ru[._-](eng|en|jp|ja)|(eng|en)[._-]ru|(?<=[\s._-])(?:dexter|domino)(?=[\s._-]*(?:studio|&|$|[\]\)]|\.(?:mkv|avi|mp4|ts)$))|[\[(](?:dexter|domino)[\])])(?![a-z0-9])",
        RegexOptions.Compiled);
    // Студии-омонимы (Dexter, DoMiNo): голое слово — это ещё и тайтлы «Dexter: Resurrection», «Domino»
    // (2019), где маркер делал русскими ВСЕ scene-строки, а с 2.107 IsRussian — гейт донора и признак
    // «русские есть» для свёртки. Поэтому только в позиции релиз-группы: после разделителя и перед
    // концом имени/расширением/«& селезень»/«studio»/скобкой либо в скобках.

    // Украинское вето (qdl 2.107). Имя студии — не доказательство языка: HDRezka Studio и BaibaKo
    // выпускают и украинские дорожки («Silo.S01E10.WEBRip.1080p.ukr.5.1.HDREZKA.STUDIO.mkv»,
    // «Silo.s02…Ukr.Eng.BaibaKo.tv»), а кириллица сама по себе не значит «по-русски» (toloka:
    // «Бункер (Сезон 3, Серії 1-9)»). Явный украинский токен при ОТСУТСТВИИ явного русского
    // языкового токена → не русская, какая бы студия ни стояла рядом. Граница слева включает
    // «_»: «[Ukr_Eng]» иначе не матчился бы. 🔴 Границы ОБЯЗАНЫ включать кириллицу: без «а-яё» в
    // лукахеде «укр» матчился внутри «Укрытие», и все русские паки самого «Укрытия» без явного
    // «rus/дубляж» становились «нерусскими» (поймано на живой выдаче при раскатке 2.107).
    // «2xUkr/Eng» (rutor: две украинские дорожки) — токен допускается и сразу после счётчика «Nx».
    // Полные слова тоже: «Ukrainian», «UA», «Українською» (студийный токен HDRezka рядом ничего не доказывает).
    static readonly Regex _ukrMarkRx = new Regex(@"(?i)(?:(?<![a-z0-9_а-яё])|(?<=\dx))(ukr|укр|ua)(?![a-z0-9а-яё])|(?<![a-z0-9])ukrain|(?<![а-яё])серії|(?<![а-яё])україн|(?<![a-z0-9])toloka(?![a-z0-9])", RegexOptions.Compiled);
    // Явные русские языковые токены, снимающие вето. Кроме слов озвучки — легенды трекеров: rutor/torrent.by
    // кодируют русскую дорожку буквами «| P, L |» (P профессиональный, L любительский, D дубляж, A авторский;
    // UKR там — ДОПОЛНИТЕЛЬНАЯ украинская дорожка), kinozal — «ПМ/ЛМ/ДБ/АП», scene — MVO/DVO/AVO.
    static readonly Regex _ruLangTokenRx = new Regex(
        @"(?i)(?<![a-z0-9])(rus|rux|rus\.?dub|dub\.?rus|rudub|mvo|dvo|avo)(?![a-z0-9])|рус\w*|дубляж|многоголос|двухголос|одноголос|закадров"
        + @"|\|\s*[PLDA](?:\s*,\s*[PLDA])*\s*\||(?<![а-яёa-z0-9])(?:ПМ|ЛМ|ДБ|АП|МВО|ДВО|АВО)(?![а-яёa-z0-9])",
        RegexOptions.Compiled);

    /// <summary>
    /// Русская ли раздача: подсказка источника (может отсутствовать) + кириллица + маркеры озвучки,
    /// минус украинское вето (явный ukr/укр/серії без явного русского языкового токена).
    /// </summary>
    public static bool IsRussian(string title, bool? langRuHint = null)
    {
        if (langRuHint == true) return true;
        if (string.IsNullOrEmpty(title)) return false;
        if (_ukrMarkRx.IsMatch(title) && !_ruLangTokenRx.IsMatch(title)) return false;
        return _cyrillicRx.IsMatch(title) || _ruMarkRx.IsMatch(title);
    }

    static readonly Regex _yearRx = new Regex(@"(?<!\d)(19|20)\d{2}(?!\d)", RegexOptions.Compiled);
    public static List<int> ParseYears(string title)
    {
        var res = new List<int>();
        foreach (Match m in _yearRx.Matches(title ?? "")) res.Add(int.Parse(m.Value));
        return res;
    }
    #endregion

    #region скоринг
    // маркер «это сериал» в названии (как отсечка сериалов из фильмовой выдачи в JacRed RedApi)
    static readonly Regex _serialMarkRx = new Regex(@"(?i)(сезон|сери(и|я|й)|(?<![A-Za-z0-9])S\d{1,2}(?![0-9]))", RegexOptions.Compiled);

    static readonly HashSet<int> _serialCats = new HashSet<int> { 5000, 5020, 5070, 5080, 2010 };

    // голова названия до первого разделителя «/ ( [» — на трекерах это русское название
    static string TitleHead(string title)
    {
        string t = title ?? "";
        int cut = t.Length;
        foreach (char c in new[] { '/', '(', '[' })
        {
            int i = t.IndexOf(c);
            if (i >= 0 && i < cut) cut = i;
        }
        return t.Substring(0, cut);
    }

    static DateTime? ParseDate(JToken t, DateTime now)
    {
        string raw = t?.Value<string>("date");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return null;
        // битые createTime (DateTime.MinValue у неудачного парса, «сейчас»-дефолт TorrentDetails
        // ловим только снизу): вне разумного диапазона → неизвестно
        if (d < new DateTime(2000, 1, 1) || d > now.AddDays(1)) return null;
        return d;
    }

    public static ScoreResult Score(JObject t, ScoreCtx ctx)
    {
        var r = new ScoreResult();
        string title = t.Value<string>("title") ?? "";
        int sid = t.Value<int?>("sid") ?? 0;
        int pir = t.Value<int?>("pir") ?? 0;
        int quality = t.Value<int?>("quality") ?? 0;
        string parselink = t.Value<string>("parselink");
        var cats = (t["cats"] as JArray)?.Select(x => x.Value<int?>() ?? 0).Where(x => x > 0).ToList() ?? new List<int>();

        double score = 0;

        // ── имя (0..40): голова title против нормализованных названий карточки ──
        // Источник, нашедший раздачу по TMDB id (bitmagnet), уже гарантировал тайтл —
        // сверять имя не нужно и вредно: имена там латиницей и против русской карточки
        // всегда давали бы nameMiss.
        bool idMatch = t.Value<bool?>("id_match") == true;
        bool haveNames = !string.IsNullOrEmpty(ctx.titleNorm) || !string.IsNullOrEmpty(ctx.originalNorm);
        if (idMatch) { score += 40; r.why.Add("точное совпадение по TMDB"); }
        else if (!haveNames)
        {
            // 🔥 Сюда попадают карточки, чьё название после нормализации пусто: SearchNameTo
            // оставляет только латиницу, кириллицу и цифры, поэтому иероглифы и бенгали дают
            // пустую строку И в title, и в original. Гейт имени тогда не работает НИ ДЛЯ КОГО:
            // nameMiss не выставляется никому, все получают одинаковые нейтральные +20, и ⭐
            // достаётся тому, кто набрал больше по сидам и свежести, — то есть произвольному
            // чужому тайтлу. Живой пример: карточка 英雄傳 (tmdb 1440599) рекомендовала «Как
            // раздражать людей (1969)». Список показать можно, но ручаться за него нельзя —
            // ⭐ запрещаем. Левая раздача под звездой хуже, чем её отсутствие.
            // ⚠️ Раздачи, найденные по TMDB id (id_match), сюда не попадают — они уходят веткой
            // выше и ⭐ получить могут: там тайтл гарантирован источником, а не именем.
            score += 20;   // без контекста имён скорить нечего — нейтрально
            r.nameGateOff = true;
        }
        else
        {
            string headNorm = SearchNameTo.Convert(TitleHead(title)) ?? "";
            bool exact = (!string.IsNullOrEmpty(ctx.titleNorm) && headNorm == ctx.titleNorm)
                      || (!string.IsNullOrEmpty(ctx.originalNorm) && headNorm == ctx.originalNorm);
            if (exact) { score += 40; r.why.Add("точное имя"); }
            else
            {
                // Contains по ПОЛНОМУ нормализованному title; короткие имена (<4) после нормализации
                // (пробелы выкинуты) дают ложные вхождения — для них только точное равенство головы
                bool contains =
                    (!string.IsNullOrEmpty(ctx.titleNorm) && ctx.titleNorm.Length >= 4 && SearchNameTo.Contains(title, ctx.titleNorm))
                 || (!string.IsNullOrEmpty(ctx.originalNorm) && ctx.originalNorm.Length >= 4 && SearchNameTo.Contains(title, ctx.originalNorm));
                if (contains) { score += 25; r.why.Add("имя в названии"); }
                else r.nameMiss = true;
            }
        }

        // ── язык (−18..+18): русское — в топ ──
        // Вес подобран так, чтобы перевесить сиды (−20..+15) и качество (1..8): иностранная
        // раздача с 300 сидами обязана оказаться НИЖЕ русской. Внутри каждой группы порядок
        // по-прежнему решают сиды/качество/свежесть. Особенно важно для bitmagnet — там
        // подавляющее большинство раздач нерусские.
        if (IsRussian(title, t.Value<bool?>("lang_ru")))
        {
            score += 18;
            r.why.Add("русская");
        }
        else score -= 18;

        // ── год (−15..10) + отсечение сериалов из фильмовой выдачи ──
        var years = ParseYears(title);
        if (!ctx.isSerial && _serialMarkRx.IsMatch(title))
            score -= 15;   // фильм искали, а это сериальная раздача
        if (ctx.year <= 0) score += 4;
        else if (ctx.isSerial)
        {
            if (years.Count == 0) score += 4;
            else if (years.Any(y => y >= ctx.year - 1)) { score += 10; r.why.Add("год " + ctx.year); }
        }
        else
        {
            if (years.Count == 0) score += 4;
            else if (years.Any(y => Math.Abs(y - ctx.year) <= 1)) { score += 10; r.why.Add("год " + ctx.year); }
        }

        // ── тип (0..8): jackett-категории (2000=movie; 5000/5070/5080/5020/2010=serial-подобные) ──
        if (cats.Count > 0)
        {
            bool catSerial = cats.Any(c => _serialCats.Contains(c));
            bool catMovie = cats.Contains(2000);
            if (ctx.isSerial ? catSerial : catMovie) score += 8;
            else if (ctx.isSerial ? catMovie : catSerial) r.typeMiss = true;   // явный мисматч → 0
            else score += 4;   // экзотика — нейтрально
        }
        else
        {
            bool looksSerial = _serialMarkRx.IsMatch(title);
            if (looksSerial && ctx.isSerial) score += 8;
            else if (looksSerial && !ctx.isSerial) r.typeMiss = true;
            else score += 4;   // не похоже на сериал — не значит фильм; нейтрально
        }

        // ── сезон (0..12, только сериал) ──
        if (ctx.isSerial)
        {
            var seasons = ParseSeasons(title);
            var infoSeasons = (t["info"]?["seasons"] as JArray)?.Select(x => x.Value<int?>() ?? 0).Where(x => x > 0);
            if (infoSeasons != null) seasons = seasons.Union(infoSeasons).ToList();
            if (ctx.wantSeason <= 0 || seasons.Count == 0) score += 6;
            else if (seasons.Contains(ctx.wantSeason)) { score += 12; r.why.Add("сезон " + ctx.wantSeason); }
            else score += 3;   // только другие сезоны
        }

        var date = ParseDate(t, ctx.now);

        // ── серии «N из M» (0..10, только сериал) ──
        if (ctx.isSerial)
        {
            r.ep = ParseEpCoverage(title);
            if (r.ep == null || r.ep.total <= 0) score += 5;
            else
            {
                score += 8.0 * r.ep.have / r.ep.total;
                r.why.Add("серии " + r.ep.have + " из " + r.ep.total);
                if (r.ep.ongoing && date != null && (ctx.now - date.Value).TotalDays <= 14)
                { score += 2; r.why.Add("обновляется"); }
            }
        }

        // ── свежесть (0..10) ──
        if (date == null) score += 3;   // неизвестная дата — нейтрально
        else
        {
            double days = (ctx.now - date.Value).TotalDays;
            if (ctx.isSerial)
            {
                if (days <= 7) score += 10;
                else if (days <= 30) score += 7;
                else if (days <= 90) score += 4;
                else if (days <= 365) score += 2;
                if (days <= 30) r.why.Add(days < 1 ? "обновлена сегодня" : "обновлена " + (int)days + " дн назад");
            }
            else if (days <= 365) score += 2;
        }

        // ── сиды (−20..15, лог-шкала с капом ~300: 500 vs 700 неразличимы) ──
        // sid_hint (bitmagnet, qdl 2.107): число из краулера — замер через минуту после появления
        // раздачи в DHT либо копия, обнуляемая при переклассификации; на выборке 2725 раздач оно почти
        // не несёт информации о живости (ColdFilm 1080p: в базе 2, реально 35). Ни штрафа «мёртвая»,
        // ни бонуса — нейтрально.
        if (t.Value<bool?>("sid_hint") == true) score += 3;
        else if (sid <= 0 && pir <= 0) score -= 20;        // мёртвая
        else if (sid <= 0) score -= 6;
        else
        {
            score += 15 * Math.Min(1.0, Math.Log10(1 + sid) / Math.Log10(301));
            r.why.Add(sid + " сид" + SidSuffix(sid));
        }

        // ── качество (0..8) ──
        if (quality == ctx.preferredQuality) score += 8;
        else if (quality >= 1080) score += 6;
        else if (quality == 720) score += 3;
        else score += 1;

        // ── следибельность (0..6, только сериал): parselink = login-трекер ⇒ работает re-grab ──
        if (ctx.isSerial && !string.IsNullOrWhiteSpace(parselink)) score += 6;

        r.score = score;
        return r;
    }

    static string SidSuffix(int n)
    {
        int d10 = n % 10, d100 = n % 100;
        if (d100 >= 11 && d100 <= 14) return "ов";
        if (d10 == 1) return "";
        if (d10 >= 2 && d10 <= 4) return "а";
        return "ов";
    }

    // Скоринг всего списка: дописывает score/watchable/ep в каждый JObject, сортирует
    // score→sid→date, помечает ⭐ (rec=true + why) у первого прошедшего гейты.
    public static JArray SortAndMark(JArray items, ScoreCtx ctx, int recommendMinSeeds, bool dropIrrelevant = true)
    {
        var scored = new List<(JObject t, ScoreResult r)>();
        var nameMissed = new List<(JObject t, ScoreResult r)>();   // отложены, а не выброшены (см. предохранитель ниже)
        int dropNonVideo = 0, dropOtherYear = 0;

        foreach (var tok in items)
        {
            if (tok is not JObject t) continue;
            var r = Score(t, ctx);

            t["score"] = Math.Round(r.score, 1);
            t["watchable"] = ctx.isSerial && !string.IsNullOrWhiteSpace(t.Value<string>("parselink"));
            if (r.ep != null && r.ep.total > 0)
                t["ep"] = new JObject { ["have"] = r.ep.have, ["total"] = r.ep.total, ["ongoing"] = r.ep.ongoing };

            // Отсев, а не понижение: такие раздачи в списке фильма не нужны ВООБЩЕ —
            // именно они раньше «попадали не в тот фильм». Действует и на фоновые
            // контуры (охота за сериями), потому что те ходят через тот же SortAndMark.
            if (dropIrrelevant)
            {
                if (IsNonVideo(t.Value<string>("title"))) { dropNonVideo++; continue; }
                // Другой фильм франшизы (сиквел/приквел): широкий проход намеренно ходит без
                // года, поэтому отсекать «Часть вторую» из выдачи первой части приходится здесь.
                // ⚠️ id_match не трогаем: там совпадение по TMDB id, оно сильнее любого года.
                if (t.Value<bool?>("id_match") != true && IsOtherMovieYear(t.Value<string>("title"), ctx.year, ctx.isSerial))
                { dropOtherYear++; continue; }
                // nameMiss = названия карточки нет в строке НИ в русском, ни в оригинальном
                // виде → это просто другой тайтл (так в выдаче «Дюны» жил «Отдел С.С.С.Р»).
                if (r.nameMiss) { nameMissed.Add((t, r)); continue; }
            }

            scored.Add((t, r));
        }

        // Предохранитель: если по именам не прошло НИЧЕГО, а раздачи были — это скорее
        // угол нормализации имени, чем «все чужие». Пустой экран тут хуже лишних строк:
        // с него и начиналась вся история с «Раздачи не найдены».
        if (scored.Count == 0 && nameMissed.Count > 0)
        {
            Console.WriteLine($"[QbitDownload] скоринг «{ctx.titleNorm}»: по имени не прошла ни одна из {nameMissed.Count} раздач — показываю как есть (проверь нормализацию имени)");
            scored.AddRange(nameMissed);
            nameMissed.Clear();
        }

        if (dropNonVideo > 0 || dropOtherYear > 0 || nameMissed.Count > 0)
            Console.WriteLine($"[QbitDownload] скоринг «{ctx.titleNorm}»: отсев {dropNonVideo + dropOtherYear + nameMissed.Count} (не видео {dropNonVideo}, другой год {dropOtherYear}, чужое имя {nameMissed.Count}), осталось {scored.Count}");

        var ordered = scored
            .OrderByDescending(x => x.r.score)
            .ThenByDescending(x => x.t.Value<int?>("sid") ?? 0)
            .ThenByDescending(x => ParseDate(x.t, ctx.now) ?? DateTime.MinValue)
            .ToList();

        foreach (var (t, r) in ordered)
        {
            if (r.nameMiss || r.nameGateOff || r.typeMiss || (t.Value<int?>("sid") ?? 0) < recommendMinSeeds) continue;
            // ⭐ не bitmagnet-строкам: parselink нет (re-grab не работает), сиды — подсказка, а с честным
            // greatest(tc, dht) английская одиночка PSA с 84 сидами иначе забирала бы звезду у русского пака.
            if (t.Value<bool?>("sid_hint") == true || t.Value<string>("tracker") == "bitmagnet") continue;
            t["rec"] = true;
            t["why"] = string.Join(" · ", r.why);
            break;   // ⭐ только у одной; никто не прошёл — списка без ⭐ достаточно
        }

        return new JArray(ordered.Select(x => x.t));
    }
    #endregion
}
