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
    static readonly Regex[] _seasonRx =
    {
        new Regex(@"(?i)(?<![A-Za-z0-9])S(\d{1,2})\s*-\s*S?(\d{1,2})(?![0-9])", RegexOptions.Compiled),
        new Regex(@"(?i)(?<![A-Za-z0-9])S(\d{1,2})(?![0-9\-])", RegexOptions.Compiled),
        new Regex(@"(?i)(\d{1,2})\s*-\s*(\d{1,2})\s*сезон", RegexOptions.Compiled),
        new Regex(@"(?i)(?<![-\d])(\d{1,2})\s*сезон", RegexOptions.Compiled),
        new Regex(@"(?i)сезон\w*\s*[:№#]?\s*(\d{1,2})\s*-\s*(\d{1,2})", RegexOptions.Compiled),
        new Regex(@"(?i)сезон\w*\s*[:№#]?\s*(\d{1,2})(?![0-9\-])", RegexOptions.Compiled),
        new Regex(@"(?i)season\s*(\d{1,2})\s*-\s*(\d{1,2})", RegexOptions.Compiled),
        new Regex(@"(?i)season\s*(\d{1,2})(?![0-9\-])", RegexOptions.Compiled),
    };

    public static List<int> ParseSeasons(string title)
    {
        var set = new SortedSet<int>();
        string t = title ?? "";
        foreach (var rx in _seasonRx)
        {
            foreach (Match m in rx.Matches(t))
            {
                int a = int.Parse(m.Groups[1].Value);
                int b = m.Groups.Count > 2 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
                if (b < a || b - a > 30 || a <= 0) continue;
                for (int s = a; s <= b; s++) set.Add(s);
            }
        }
        return set.ToList();
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
        bool haveNames = !string.IsNullOrEmpty(ctx.titleNorm) || !string.IsNullOrEmpty(ctx.originalNorm);
        if (!haveNames)
            score += 20;   // без контекста имён скорить нечего — нейтрально
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
        if (sid <= 0 && pir <= 0) score -= 20;        // мёртвая
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
    public static JArray SortAndMark(JArray items, ScoreCtx ctx, int recommendMinSeeds)
    {
        var scored = new List<(JObject t, ScoreResult r)>();
        foreach (var tok in items)
        {
            if (tok is not JObject t) continue;
            var r = Score(t, ctx);
            t["score"] = Math.Round(r.score, 1);
            t["watchable"] = ctx.isSerial && !string.IsNullOrWhiteSpace(t.Value<string>("parselink"));
            if (r.ep != null && r.ep.total > 0)
                t["ep"] = new JObject { ["have"] = r.ep.have, ["total"] = r.ep.total, ["ongoing"] = r.ep.ongoing };
            scored.Add((t, r));
        }

        var ordered = scored
            .OrderByDescending(x => x.r.score)
            .ThenByDescending(x => x.t.Value<int?>("sid") ?? 0)
            .ThenByDescending(x => ParseDate(x.t, ctx.now) ?? DateTime.MinValue)
            .ToList();

        foreach (var (t, r) in ordered)
        {
            if (r.nameMiss || r.typeMiss || (t.Value<int?>("sid") ?? 0) < recommendMinSeeds) continue;
            t["rec"] = true;
            t["why"] = string.Join(" · ", r.why);
            break;   // ⭐ только у одной; никто не прошёл — списка без ⭐ достаточно
        }

        return new JArray(ordered.Select(x => x.t));
    }
    #endregion
}
