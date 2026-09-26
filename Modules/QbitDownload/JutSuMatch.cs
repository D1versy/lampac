using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace QbitDownload;

// ─────────────────────────────────────────────────────────────────────────────
// Сопоставление тайтла jut.su с базой аниме — ЧИСТАЯ логика: ноль HTTP, ноль ModInit,
// ноль BaseController. Файл линкуется в тестовый проект одной строкой (как JutSuParse.cs),
// потому что здесь живёт единственное, что реально может сделать больно: неверный матч.
//
// Зачем это вообще: постер jut.su — квадрат 186×186 (10–25 КБ), карточка Lampa ждёт
// портрет 2:3. Лучшего варианта на самом сайте НЕТ (страница тайтла и карточка каталога
// ссылаются на один файл), внешних id (MAL/AniList/IMDb) на страницах тоже нет —
// остаётся сопоставление по названию.
//
// 🔥 Требование владельца: ПРАВИЛЬНОСТЬ важнее качества. Чужой постер хуже плохого.
//    Поэтому отказ — это штатный исход, а не ошибка: не уверены → остаётся постер jut.su.
//
// Почему матчер — Shikimori, а картинку берём у AniList (JutSuPoster.cs):
//    у Shikimori романдзи совпадает с jut.su символ-в-символ (замер: 15/15 на реальной
//    выдаче каталога), но постеры мелкие (240×360); у AniList постеры 460×690, но своё
//    стилизованное написание («SPY×FAMILY», «ONE PIECE», «Shite mo» вместо «shitemo») —
//    по названию он матчится плохо. Отсюда: Shikimori ищет → отдаёт MAL id → AniList
//    забирает обложку ПО ID, то есть уже без всякого угадывания.
//
// Документация: E:\Media-server\claude\jut\02-architecture.md
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Промежуток лет, в котором у тайтла jut.su есть сезоны. Один тайтл несёт несколько.
///
/// 🔥 Зачем не просто int. Карточка каталога jut.su помечена классами `anime_year_*`, и для
/// всего, что старше 2016-го, это НЕ годы, а бакеты фильтра сайта: `2000-2007`, `2008-2014`,
/// `2015-2023`, `before2000`. Пока парсер вынимал из них все четырёхзначные числа как точные
/// годы, «Durarara!!» (2010) превращалась в «2008, 2014», «Hunter x Hunter» (2011) — тоже
/// в «2008, 2014», и вето «±1 год» резало ПРАВИЛЬНОГО кандидата. Боевой замер 26.09.2026:
/// 457 отказов `year_mismatch` из 1357 тайтлов каталога — треть каталога без обложки из-за
/// одного неверно понятого класса. Точный год страницы тайтла — вырожденный промежуток (y, y).
/// </summary>
public readonly record struct JutYearSpan(int from, int to)
{
    /// <summary>Открыт снизу («before2000» → to = 1999): нижней границы у сайта нет.</summary>
    public bool Open => from <= 0;

    /// <summary>Ровно один год (со страницы тайтла или из класса вида `anime_year_2026`).</summary>
    public bool IsExact => !Open && from == to;

    /// <summary>Год попадает в промежуток с допуском slack по обеим сторонам.</summary>
    public bool Covers(int year, int slack)
        => year <= to + slack && (Open || year >= from - slack);

    public static JutYearSpan Exact(int year) => new(year, year);

    public static List<JutYearSpan> Exact(IEnumerable<int> years)
    {
        var list = new List<JutYearSpan>();
        if (years == null) return list;
        foreach (int y in years) if (y > 0) list.Add(Exact(y));
        return list;
    }

    /// <summary>
    /// Значение класса `anime_year_*` карточки каталога → промежуток. Ровно четыре формы,
    /// увиденные на живой витрине 26.09.2026: `2026` · `2015-2023` · `before2000` · `ongoing`
    /// (последняя — не год, null). Незнакомое — тоже null: лучше без вето, чем с выдуманным.
    /// </summary>
    public static JutYearSpan? ParseClass(string v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        v = v.Trim().ToLowerInvariant();

        if (v.Length == 4 && int.TryParse(v, out int y) && y >= 1900) return Exact(y);

        if (v.Length == 9 && v[4] == '-'
            && int.TryParse(v.AsSpan(0, 4), out int a) && int.TryParse(v.AsSpan(5, 4), out int b)
            && a >= 1900 && b >= a)
            return new JutYearSpan(a, b);

        if (v.StartsWith("before", StringComparison.Ordinal)
            && int.TryParse(v.AsSpan(6), out int lim) && lim >= 1900)
            return new JutYearSpan(0, lim - 1);

        return null;
    }

    /// <summary>JSON-форма: `[from, to]`. Компактно и без имён — карточек в снапшоте 1357.</summary>
    public JArray ToJson() => new JArray(from, to);

    /// <summary>
    /// Обратно из `[[from,to], …]`. null — поля нет вовсе (старый снапшот, JSON тайтла до 2.122):
    /// вызывающий тогда берёт `years` как точные годы. Мусорные элементы пропускаются.
    /// </summary>
    public static List<JutYearSpan> FromJson(JToken t)
    {
        if (t is not JArray arr) return null;
        var list = new List<JutYearSpan>();
        foreach (var it in arr)
        {
            if (it is not JArray pair || pair.Count != 2) continue;
            int a = pair[0]?.Value<int?>() ?? -1, b = pair[1]?.Value<int?>() ?? -1;
            if (b <= 0 || (a > 0 && a > b)) continue;
            list.Add(new JutYearSpan(Math.Max(0, a), b));
        }
        return list;
    }

    public override string ToString() => Open ? "<" + (to + 1) : IsExact ? from.ToString() : from + "-" + to;
}

/// <summary>Кандидат из базы аниме (Shikimori). Ровно те поля, что нужны для решения.</summary>
public sealed class JutAnimeCandidate
{
    public int id;              // ⚠️ id Shikimori == id MyAnimeList (проверено: One Piece=21, Naruto=20)
    public string name;         // романдзи
    public string russian;
    public string kind;         // tv / movie / ova / ona / special / cm / pv / music
    public string airedOn;      // "2022-04-09"; может отсутствовать у анонсов
    public string image;        // относительный путь постера Shikimori (запасной источник)

    /// <summary>Год выхода из airedOn. 0 = неизвестен (анонс без даты).</summary>
    public int year
    {
        get
        {
            if (string.IsNullOrEmpty(airedOn) || airedOn.Length < 4) return 0;
            return int.TryParse(airedOn.Substring(0, 4), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int y) ? y : 0;
        }
    }
}

public enum JutMatchVerdict { Accepted, NoMatch, Ambiguous }

public sealed class JutMatchResult
{
    public JutAnimeCandidate pick;
    public JutMatchVerdict verdict = JutMatchVerdict.NoMatch;
    public string reason = "no_candidates";
    public bool ok => verdict == JutMatchVerdict.Accepted && pick != null;
}

public static class JutSuMatch
{
    // Мусорные типы: реклама, промо-ролики, клипы. jut.su их тайтлами не показывает,
    // а в выдаче Shikimori они шумят (на «Spy x Family» приезжает кроссовер-реклама
    // Street Fighter с kind=cm). Отсечь их — не догадка, а чистка выдачи.
    static readonly HashSet<string> _junkKinds =
        new(StringComparer.OrdinalIgnoreCase) { "cm", "pv", "music" };

    // Короткое название матчить нельзя: «Ai», «Ao» и подобное совпадёт с чем угодно.
    const int MinNormLength = 3;

    // Порог для сопоставления по префиксу (jut.su обрезает длинные названия). 25 нормализованных
    // символов — это 4–6 слов: случайное совпадение такой длины практически невозможно.
    const int MinPrefixLength = 25;

    /// <summary>
    /// Нормализация названия под сравнение. Гасит РОВНО те различия, что видны в реальных
    /// данных: «×» против « x », хвостовую точку, двоеточия, кавычки, дефисы, ё/е, диакритику.
    /// Всё остальное (пробелы, пунктуация) просто выкидывается.
    /// </summary>
    public static string NormTitle(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        // ⚠️ «×» (U+00D7) — это НЕ латинская x. У AniList «SPY×FAMILY», у jut.su «Spy x Family»:
        // без этой замены нормализация даёт spyfamily против spyxfamily.
        s = s.Replace('\u00D7', 'x').Replace('\u0451', '\u0435').Replace('\u0401', '\u0435');

        // Диакритика: Pokémon → pokemon. Раскладываем и выкидываем комбинирующие знаки.
        // Кириллица при этом схлопывает й→и — это одинаково с обеих сторон сравнения,
        // а второй ключ и так требует совпадения года, так что послаблением не пахнет.
        string d = s.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char lc = char.ToLowerInvariant(c);
            if ((lc >= 'a' && lc <= 'z') || (lc >= '0' && lc <= '9') || (lc >= '\u0430' && lc <= '\u044F'))
                sb.Append(lc);
        }
        return sb.ToString();
    }

    /// <summary>Разбор выдачи Shikimori /api/animes в кандидатов. Чистая функция — тестируется фикстурой.</summary>
    public static List<JutAnimeCandidate> ParseCandidates(JArray arr)
    {
        var list = new List<JutAnimeCandidate>();
        if (arr == null) return list;

        foreach (var t in arr)
        {
            if (t is not JObject o) continue;
            int id = o["id"]?.Value<int?>() ?? 0;
            if (id <= 0) continue;

            list.Add(new JutAnimeCandidate
            {
                id = id,
                name = o["name"]?.Value<string>(),
                russian = o["russian"]?.Value<string>(),
                kind = o["kind"]?.Value<string>(),
                airedOn = o["aired_on"]?.Value<string>(),
                image = o["image"]?["original"]?.Value<string>()
            });
        }
        return list;
    }

    /// <summary>
    /// Выбор кандидата. Порядок ключей принципиален и отражает замеры на реальной выдаче:
    /// романдзи совпал точно у 15 из 15 тайтлов, русское название — только у 8 из 15
    /// (jut.su сокращает длинные названия), поэтому русский — ТОЛЬКО запасной ключ и
    /// ТОЛЬКО вместе с годом. Годы — точные (страница тайтла); для карточки каталога есть
    /// перегрузка с промежутками, см. JutYearSpan.
    /// </summary>
    public static JutMatchResult Pick(string romaji, string ru, IReadOnlyList<int> years,
                                      IReadOnlyList<JutAnimeCandidate> cands)
        => Pick(romaji, ru, JutYearSpan.Exact(years), cands);

    /// <summary>
    /// То же, но годы — промежутками (карточка каталога jut.su знает про старые тайтлы только
    /// бакет вида 2008-2014). Точные годы страницы тайтла — вырожденные промежутки.
    /// </summary>
    public static JutMatchResult Pick(string romaji, string ru, IReadOnlyList<JutYearSpan> years,
                                      IReadOnlyList<JutAnimeCandidate> cands)
    {
        if (cands == null || cands.Count == 0) return Refuse(JutMatchVerdict.NoMatch, "no_candidates");

        var pool = cands.Where(c => c != null && !_junkKinds.Contains(c.kind ?? "")).ToList();
        if (pool.Count == 0) return Refuse(JutMatchVerdict.NoMatch, "no_candidates");

        // ── Ключ 1: точное совпадение романдзи ────────────────────────────────
        string nr = NormTitle(romaji);
        if (nr.Length >= MinNormLength)
        {
            bool vetoed = false;
            var exact = pool.Where(c => NormTitle(c.name) == nr).ToList();
            if (exact.Count == 1)
            {
                if (!YearContradicts(exact[0], years)) return Accept(exact[0], "romaji");
                // Имя совпало, год — нет: это ДРУГАЯ экранизация того же названия
                // (OVA 1993 против сериала 2012). Прежде чем отказать — ключ 1a.
                vetoed = true;
            }
            if (exact.Count > 1)
            {
                var tie = TieBreak(exact, ru, years);
                return tie != null ? Accept(tie, "romaji_tie")
                                   : Refuse(JutMatchVerdict.Ambiguous, "ambiguous");
            }

            // ── Ключ 1a: то же имя с уточнением MAL «(TV)» / «(2011)» ─────────────
            // Так MyAnimeList (а за ним Shikimori) различает переэкранизации с одинаковым
            // названием: «JoJo no Kimyou na Bouken» — OVA 1993, «JoJo no Kimyou na Bouken (TV)» —
            // сериал 2012; «Hunter x Hunter» — 1999, «Hunter x Hunter (2011)». У jut.su хаб
            // на всю франшизу называется без уточнения, и годы у него — от сериала. Уточнение —
            // не догадка: имя равно символ-в-символ, год подходит, и такой кандидат ровно один.
            // Два подошедших — отказ (Ambiguous), как и везде.
            var variants = pool.Where(c => VariantBase(c.name) is string b && NormTitle(b) == nr
                                           && !YearContradicts(c, years)).ToList();
            if (variants.Count == 1) return Accept(variants[0], "romaji_variant");
            if (variants.Count > 1) return Refuse(JutMatchVerdict.Ambiguous, "ambiguous_variant");
            if (vetoed) return Refuse(JutMatchVerdict.NoMatch, "year_mismatch");
        }

        // ── Ключ 1b: длинный префикс романдзи ─────────────────────────────────
        // jut.su ОБРЕЗАЕТ очень длинные названия, которых у нынешних ранобэ-экранизаций
        // большинство: «Saijo no Osewa: …(Seikatsu Nouryoku Kaimu) wo Kagenagara Osewa suru»
        // против «…Osewa suru Koto ni Narimashita». Точное равенство тут не сработает никогда.
        // Совпадение на 25+ символах при ЕДИНСТВЕННОМ кандидате — свидетельство более сильное,
        // чем точное равенство короткого названия, а год всё равно остаётся вето.
        if (nr.Length >= MinPrefixLength)
        {
            var pref = pool.Where(c =>
            {
                string cn = NormTitle(c.name);
                return cn.Length >= MinPrefixLength &&
                       (cn.StartsWith(nr, StringComparison.Ordinal) || nr.StartsWith(cn, StringComparison.Ordinal));
            }).ToList();

            if (pref.Count == 1)
                return YearContradicts(pref[0], years)
                    ? Refuse(JutMatchVerdict.NoMatch, "year_mismatch")
                    : Accept(pref[0], "romaji_prefix");
            if (pref.Count > 1) return Refuse(JutMatchVerdict.Ambiguous, "ambiguous_prefix");
        }

        // ── Ключ 2: точное совпадение русского названия ПЛЮС совпадение года ──
        // Без года не пускаем: русские названия у разных сезонов часто одинаковы.
        // ⚠️ Год здесь — только ТОЧНЫЙ (страница тайтла или класс вида anime_year_2026).
        // Бакет «2008-2014» с карточки каталога годом не считается: русское имя плюс
        // семилетнее окно — слишком слабое свидетельство для чужого постера.
        string nu = NormTitle(ru);
        if (nu.Length >= MinNormLength && years != null && years.Any(s => s.IsExact))
        {
            var byRu = pool.Where(c => NormTitle(c.russian) == nu && c.year > 0
                                       && years.Any(s => s.IsExact && s.from == c.year)).ToList();
            if (byRu.Count == 1) return Accept(byRu[0], "russian_year");
            if (byRu.Count > 1) return Refuse(JutMatchVerdict.Ambiguous, "ambiguous_ru");
        }

        return Refuse(JutMatchVerdict.NoMatch, nr.Length < MinNormLength ? "romaji_too_short" : "no_match");
    }

    /// <summary>
    /// Несколько кандидатов с одинаковым романдзи. Сужаем только ПРОВЕРЯЕМЫМИ признаками;
    /// если после них всё ещё больше одного — возвращаем null (честный отказ, а не догадка).
    /// </summary>
    static JutAnimeCandidate TieBreak(List<JutAnimeCandidate> list, string ru, IReadOnlyList<JutYearSpan> years)
    {
        if (years != null && years.Count > 0)
        {
            var byYear = list.Where(c => c.year > 0 && years.Any(s => s.Covers(c.year, 0))).ToList();
            if (byYear.Count == 1) return byYear[0];
            if (byYear.Count > 1) list = byYear;
        }

        string nu = NormTitle(ru);
        if (nu.Length >= MinNormLength)
        {
            var byRu = list.Where(c => NormTitle(c.russian) == nu).ToList();
            if (byRu.Count == 1) return byRu[0];
            if (byRu.Count > 1) list = byRu;
        }

        return null;
    }

    /// <summary>
    /// Год ПРОТИВОРЕЧИТ кандидату? Это вето, а не требование: год у jut.su бывает не указан,
    /// и тогда сильного романдзи достаточно. Но если обе стороны год назвали и они разошлись
    /// больше чем на ±1 — совпало название, а тайтл другой. ±1 закрывает декабрь/январь и
    /// расхождение «премьера в Японии / показ у нас».
    /// </summary>
    static bool YearContradicts(JutAnimeCandidate c, IReadOnlyList<JutYearSpan> years)
    {
        if (c == null || c.year <= 0 || years == null || years.Count == 0) return false;
        return !years.Any(s => s.Covers(c.year, 1));
    }

    // «JoJo no Kimyou na Bouken (TV)» → «JoJo no Kimyou na Bouken»; «Hunter x Hunter (2011)» →
    // «Hunter x Hunter». Только эти две формы уточнения — ровно те, которыми MAL разводит
    // переэкранизации. «(Movie)», «: Part 2» и прочее — другие тайтлы, а не варианты.
    static readonly System.Text.RegularExpressions.Regex _rxVariant =
        new(@"^(?<b>.*\S)\s*\((?:TV|(?:19|20)\d{2})\)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Имя без уточнения «(TV)»/«(год)», либо null, если уточнения нет.</summary>
    public static string VariantBase(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var m = _rxVariant.Match(name);
        return m.Success ? m.Groups["b"].Value : null;
    }

    static JutMatchResult Accept(JutAnimeCandidate c, string reason)
        => new() { pick = c, verdict = JutMatchVerdict.Accepted, reason = reason };

    static JutMatchResult Refuse(JutMatchVerdict v, string reason)
        => new() { verdict = v, reason = reason };

    // ── Санити картинки ──────────────────────────────────────────────────────
    // Апгрейд имеет право заменить постер, ТОЛЬКО если он объективно лучше. Иначе смысл
    // всей затеи теряется: подсунуть вместо квадрата 186×186 другой мелкий квадрат — регресс.

    /// <summary>MIME по сигнатуре, а не по расширению. null = это не картинка (например HTML-заглушка).</summary>
    public static string SniffMime(byte[] b)
    {
        if (b == null || b.Length < 64) return null;
        if (b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
            b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    /// <summary>Размеры из ЗАГОЛОВКА файла (JPEG SOF / PNG IHDR / WebP). (0,0) — не разобрали.</summary>
    public static (int w, int h) ImageSize(byte[] b)
    {
        string mime = SniffMime(b);
        if (mime == null) return (0, 0);

        try
        {
            if (mime == "image/png")
                return (ReadBe32(b, 16), ReadBe32(b, 20));      // IHDR: width, height

            // 🔥 У WebP ТРИ формы, и знать только VP8X мало: libvips webpsave пишет простой
            // "VP8 " для непрозрачной картинки, а VP8X появляется лишь при альфе/метаданных.
            // Пока разбирался один VP8X, ArtAcceptable молча отказывал почти всем нашим
            // пережатым постерам — то есть транскод выключался бы сам, без единой ошибки.
            if (mime == "image/webp")
            {
                // Расширенный: VP8X, размеры лежат минус единица тремя байтами каждый
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
                    return (ReadLe24(b, 24) + 1, ReadLe24(b, 27) + 1);

                // Простой lossy: "VP8 " + ключевой кадр со стартовым кодом 9D 01 2A,
                // дальше по 14 бит на сторону (старшие два бита — масштаб, он нам не нужен)
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ' &&
                    b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
                    return (ReadLe16(b, 26) & 0x3FFF, ReadLe16(b, 28) & 0x3FFF);

                // Простой lossless: "VP8L" + сигнатура 0x2F, затем 14+14 бит минус единица
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L' && b[20] == 0x2F)
                {
                    int bits = ReadLe32(b, 21);
                    return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
                }

                return (0, 0);
            }

            if (mime == "image/jpeg")
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    int m = b[i + 1];
                    // SOF0..SOF3, SOF5..SOF7, SOF9..SOF11, SOF13..SOF15 — в них лежат размеры.
                    // DHT(C4)/JPG(C8)/DAC(CC) — НЕ SOF, их надо пропустить как обычные сегменты.
                    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                        return (ReadBe16(b, i + 7), ReadBe16(b, i + 5));    // height идёт ПЕРЕД width
                    if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                    if (m == 0xD9 || m == 0xDA) break;                     // конец / начало сжатых данных
                    i += 2 + ReadBe16(b, i + 2);
                }
            }
        }
        catch { }
        return (0, 0);
    }

    /// <summary>
    /// Годится ли скачанное в постеры: разобрали размеры, это ПОРТРЕТ и он шире minWidth.
    /// Не разобрали размеры — отказ: подтвердить, что картинка лучше нынешней, мы не можем.
    /// </summary>
    public static bool ArtAcceptable(byte[] bytes, int minWidth, out int w, out int h, out string mime)
    {
        w = h = 0;
        mime = SniffMime(bytes);
        if (mime == null || bytes.Length < 8192) return false;

        (w, h) = ImageSize(bytes);
        return w >= Math.Max(1, minWidth) && h > w;
    }

    static int ReadBe16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    static int ReadBe32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    static int ReadLe16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
    static int ReadLe24(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
    static int ReadLe32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
}
