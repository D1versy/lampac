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
    /// ТОЛЬКО вместе с годом.
    /// </summary>
    public static JutMatchResult Pick(string romaji, string ru, IReadOnlyList<int> years,
                                      IReadOnlyList<JutAnimeCandidate> cands)
    {
        if (cands == null || cands.Count == 0) return Refuse(JutMatchVerdict.NoMatch, "no_candidates");

        var pool = cands.Where(c => c != null && !_junkKinds.Contains(c.kind ?? "")).ToList();
        if (pool.Count == 0) return Refuse(JutMatchVerdict.NoMatch, "no_candidates");

        // ── Ключ 1: точное совпадение романдзи ────────────────────────────────
        string nr = NormTitle(romaji);
        if (nr.Length >= MinNormLength)
        {
            var exact = pool.Where(c => NormTitle(c.name) == nr).ToList();
            if (exact.Count == 1)
                return YearContradicts(exact[0], years)
                    ? Refuse(JutMatchVerdict.NoMatch, "year_mismatch")
                    : Accept(exact[0], "romaji");
            if (exact.Count > 1)
            {
                var tie = TieBreak(exact, ru, years);
                return tie != null ? Accept(tie, "romaji_tie")
                                   : Refuse(JutMatchVerdict.Ambiguous, "ambiguous");
            }
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
        string nu = NormTitle(ru);
        if (nu.Length >= MinNormLength && years != null && years.Count > 0)
        {
            var byRu = pool.Where(c => NormTitle(c.russian) == nu && c.year > 0 && years.Contains(c.year)).ToList();
            if (byRu.Count == 1) return Accept(byRu[0], "russian_year");
            if (byRu.Count > 1) return Refuse(JutMatchVerdict.Ambiguous, "ambiguous_ru");
        }

        return Refuse(JutMatchVerdict.NoMatch, nr.Length < MinNormLength ? "romaji_too_short" : "no_match");
    }

    /// <summary>
    /// Несколько кандидатов с одинаковым романдзи. Сужаем только ПРОВЕРЯЕМЫМИ признаками;
    /// если после них всё ещё больше одного — возвращаем null (честный отказ, а не догадка).
    /// </summary>
    static JutAnimeCandidate TieBreak(List<JutAnimeCandidate> list, string ru, IReadOnlyList<int> years)
    {
        if (years != null && years.Count > 0)
        {
            var byYear = list.Where(c => c.year > 0 && years.Contains(c.year)).ToList();
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
    static bool YearContradicts(JutAnimeCandidate c, IReadOnlyList<int> years)
    {
        if (c == null || c.year <= 0 || years == null || years.Count == 0) return false;
        return !years.Any(y => Math.Abs(y - c.year) <= 1);
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

    /// <summary>Размеры из ЗАГОЛОВКА файла (JPEG SOF / PNG IHDR / WebP VP8X). (0,0) — не разобрали.</summary>
    public static (int w, int h) ImageSize(byte[] b)
    {
        string mime = SniffMime(b);
        if (mime == null) return (0, 0);

        try
        {
            if (mime == "image/png")
                return (ReadBe32(b, 16), ReadBe32(b, 20));      // IHDR: width, height

            if (mime == "image/webp" && b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
                return (ReadLe24(b, 24) + 1, ReadLe24(b, 27) + 1);

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
    static int ReadLe24(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
}
