using Newtonsoft.Json.Linq;
using QbitDownload;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace QbitDownload.Tests;

// Тесты гейта уверенности при апгрейде постеров jut.su. Сети нет: ответы Shikimori записаны
// в fixtures/shiki/catalog15.json ровно для тех 15 тайтлов, что лежат в фикстуре каталога.
//
// 🔥 Зачем этот файл вообще. Постеры jut.su — квадраты 186×186, и мы подменяем их обложками
// из внешней базы. Единственный способ сделать это плохо — подставить ЧУЖУЮ картинку.
// Требование владельца: не уверены — оставляем как было. Здесь это требование и живёт.
//
// Устройство цепочки — E:\Media-server\claude\jut\02-architecture.md
public class JutSuMatchTests
{
    static string Fx(string sub, string name)
    {
        string[] probe =
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", sub, name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", sub, name)
        };
        foreach (string p in probe)
            if (File.Exists(p)) return File.ReadAllText(p);
        throw new FileNotFoundException("нет фикстуры " + sub + "/" + name);
    }

    static JObject Recorded() => JObject.Parse(Fx("shiki", "catalog-page1.json"));

    static List<JutAnimeCandidate> CandsFor(JObject rec, string orig)
        => JutSuMatch.ParseCandidates(rec["responses"]?[orig] as JArray);

    static JutAnimeCandidate C(int id, string name, string ru = null, string aired = null, string kind = "tv")
        => new() { id = id, name = name, russian = ru, airedOn = aired, kind = kind };

    #region нормализация

    [Theory]
    // Ровно те расхождения, что встречаются в живых данных трёх источников
    [InlineData("SPY×FAMILY", "Spy x Family")]        // × (U+00D7) у AniList против латинской x у jut
    [InlineData("ONE PIECE", "One Piece")]            // регистр
    [InlineData("Natteita.", "Natteita")]             // хвостовая точка у Shikimori
    [InlineData("Sekai Saikyou no Kouei: Meikyuukoku", "Sekai Saikyou no Kouei Meikyuukoku")]
    [InlineData("\"Kimi wo Aisuru Ki wa Nai\"", "Kimi wo Aisuru Ki wa Nai")]
    [InlineData("Ёлка", "Елка")]                      // ё/е расходятся непредсказуемо
    public void NormTitle_гасит_реальные_расхождения(string a, string b)
        => Assert.Equal(JutSuMatch.NormTitle(a), JutSuMatch.NormTitle(b));

    [Fact]
    public void NormTitle_не_склеивает_разные_тайтлы()
    {
        Assert.NotEqual(JutSuMatch.NormTitle("Fate/Zero"), JutSuMatch.NormTitle("Fate/Apocrypha"));
        Assert.NotEqual(JutSuMatch.NormTitle("Mebius Dust"), JutSuMatch.NormTitle("Mebius Rust"));
    }

    #endregion

    #region главный замер: реальный каталог против реальной выдачи Shikimori

    /// <summary>
    /// 🔥 Несущий тест всей фичи: ЦЕЛАЯ страница каталога (30 тайтлов) против записанной живой
    /// выдачи Shikimori. Ожидание — все сопоставились и ни один не сопоставился с чужим.
    /// Если этот тест поедет, значит поехала нормализация или гейт, и постеры начнут ставиться
    /// чужие — а это ровно то, что владелец просил не допустить.
    /// </summary>
    [Fact]
    public void Целая_страница_каталога_сопоставляется_без_единого_промаха()
    {
        var rec = Recorded();
        var cards = JutSuParse.ParseCatalog(Fx("jut", "catalog-ajax.html")).items
                              .Where(c => !string.IsNullOrWhiteSpace(c.titleOrig)).ToList();
        Assert.Equal(30, cards.Count);

        var refused = new List<string>();
        foreach (var c in cards)
        {
            // spans, а не years — ровно тот путь, которым карточка идёт в очередь постеров
            var m = JutSuMatch.Pick(c.titleOrig, c.titleRu, c.spans, CandsFor(rec, c.titleOrig));
            if (!m.ok) { refused.Add(c.titleOrig + " → " + m.reason); continue; }

            // Принятое обязано совпадать по названию — иначе это и есть «чужой постер».
            // Префикс допустим в обе стороны: jut.su обрезает длинные названия.
            string a = JutSuMatch.NormTitle(c.titleOrig), b = JutSuMatch.NormTitle(m.pick.name);
            Assert.True(a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal),
                        $"выбран чужой тайтл: «{c.titleOrig}» → «{m.pick.name}» ({m.reason})");
            Assert.True(m.pick.id > 0);
        }

        Assert.True(refused.Count == 0, "не сопоставились: " + string.Join(" | ", refused));
    }

    [Fact]
    public void Сокращённое_русское_название_не_мешает_романдзи()
    {
        // jut.su режет длинные русские названия («Самый сильный в мире заступник» против
        // «…: Страна лабиринта и искатели приключений»). Романдзи при этом совпадает точно —
        // именно поэтому основной ключ он, а русский только запасной.
        var rec = Recorded();
        const string orig = "Sekai Saikyou no Kouei: Meikyuukoku no Shinjin Tansakusha";

        var m = JutSuMatch.Pick(orig, "Самый сильный в мире заступник", new[] { 2026 }, CandsFor(rec, orig));

        Assert.True(m.ok);
        Assert.Equal("romaji", m.reason);
        Assert.Equal(62435, m.pick.id);
    }

    [Fact]
    public void Найденный_id_совпадает_с_id_MyAnimeList()
    {
        // На этом равенстве держится второй шаг цепочки: AniList ищется по idMal.
        var rec = Recorded();
        var m = JutSuMatch.Pick("Kore Kaite Shine", "Нарисуй это, потом умри",
                                new[] { 2026 }, CandsFor(rec, "Kore Kaite Shine"));
        Assert.True(m.ok);
        Assert.Equal(61280, m.pick.id);
    }

    #endregion

    #region отказы — то, ради чего всё затевалось

    [Fact]
    public void Нет_точного_романдзи_и_русское_другое_отказ()
    {
        var cands = new[] { C(1, "Mebius Rust", "Ржавчина Мёбиуса", "2026-01-01") };
        var m = JutSuMatch.Pick("Mebius Dust", "Пыль Мёбиуса", new[] { 2026 }, cands);

        Assert.False(m.ok);
        Assert.Equal("no_match", m.reason);
    }

    [Fact]
    public void Год_противоречит_отказ_даже_при_точном_романдзи()
    {
        // Название совпало символ в символ, но тайтл другой: разница в годах больше ±1.
        var cands = new[] { C(1, "Hunter x Hunter", "Охотник х Охотник", "1999-10-16") };
        var m = JutSuMatch.Pick("Hunter x Hunter", "Охотник х Охотник", new[] { 2011 }, cands);

        Assert.False(m.ok);
        Assert.Equal("year_mismatch", m.reason);
    }

    [Fact]
    public void Расхождение_в_один_год_допустимо()
    {
        // Декабрьская премьера в Японии против январского показа у нас — не повод отказывать.
        var cands = new[] { C(7, "Sayonara Lara", "Прощай, Лара", "2025-12-28") };
        var m = JutSuMatch.Pick("Sayonara Lara", "Прощай, Лара", new[] { 2026 }, cands);

        Assert.True(m.ok);
    }

    [Fact]
    public void Год_неизвестен_сильного_романдзи_достаточно()
    {
        // Год — вето, а не требование: jut.su его указывает не всегда.
        var cands = new[] { C(9, "Mebius Dust", "Пыль Мёбиуса", null) };
        var m = JutSuMatch.Pick("Mebius Dust", "Пыль Мёбиуса", Array.Empty<int>(), cands);

        Assert.True(m.ok);
        Assert.Equal("romaji", m.reason);
    }

    [Fact]
    public void Два_одинаковых_романдзи_разводятся_годом()
    {
        var cands = new[]
        {
            C(1, "Trigun", "Триган", "1998-04-01"),
            C(2, "Trigun", "Триган", "2023-01-07")
        };
        var m = JutSuMatch.Pick("Trigun", "Триган", new[] { 2023 }, cands);

        Assert.True(m.ok);
        Assert.Equal(2, m.pick.id);
    }

    [Fact]
    public void Два_одинаковых_романдзи_и_ничего_не_развело_отказ()
    {
        // 🔥 Догадка тут была бы дешевле отказа — и именно поэтому её нет: ни год, ни русское
        // название не различают кандидатов, значит выбирать не из чего.
        var cands = new[]
        {
            C(1, "Trigun", "Триган", "2023-01-07"),
            C(2, "Trigun", "Триган", "2023-04-01")
        };
        var m = JutSuMatch.Pick("Trigun", "Триган", Array.Empty<int>(), cands);

        Assert.False(m.ok);
        Assert.Equal(JutMatchVerdict.Ambiguous, m.verdict);
    }

    [Fact]
    public void Два_одинаковых_романдзи_разводятся_русским_названием()
    {
        // Год не помог, но русское название совпало ровно с одним — это проверяемый признак,
        // а не догадка, поэтому принимаем.
        var cands = new[]
        {
            C(1, "Trigun", "Триган", "2023-01-07"),
            C(2, "Trigun", "Триган: Возвращение", "2023-04-01")
        };
        var m = JutSuMatch.Pick("Trigun", "Триган", Array.Empty<int>(), cands);

        Assert.True(m.ok);
        Assert.Equal(1, m.pick.id);
    }

    [Fact]
    public void Короткий_романдзи_не_матчится()
    {
        var cands = new[] { C(1, "Ai", "Аи", "2026-01-01") };
        var m = JutSuMatch.Pick("Ai", "Что-то другое", Array.Empty<int>(), cands);
        Assert.False(m.ok);
    }

    [Fact]
    public void Реклама_и_клипы_выбрасываются_до_сопоставления()
    {
        // На «Spy x Family» Shikimori приносит кроссовер-рекламу Street Fighter (kind=cm).
        var cands = new[]
        {
            C(1, "Spy x Family", "Семья шпиона", "2022-04-09", "cm"),
            C(2, "Spy x Family", "Семья шпиона", "2022-04-09", "tv")
        };
        var m = JutSuMatch.Pick("Spy x Family", "Семья шпиона", new[] { 2022 }, cands);

        Assert.True(m.ok);
        Assert.Equal(2, m.pick.id);
    }

    [Fact]
    public void Русский_ключ_работает_только_вместе_с_годом()
    {
        // Романдзи не совпал, русское совпало. Без года — отказ, с годом — принимаем.
        var cands = new[] { C(5, "Yani Neko", "Табакошка", "2026-07-03") };

        Assert.False(JutSuMatch.Pick("Smoking Cat", "Табакошка", Array.Empty<int>(), cands).ok);

        var m = JutSuMatch.Pick("Smoking Cat", "Табакошка", new[] { 2026 }, cands);
        Assert.True(m.ok);
        Assert.Equal("russian_year", m.reason);
    }

    [Fact]
    public void Обрезанное_длинное_название_сопоставляется_по_префиксу()
    {
        // Реальный случай со страницы каталога: jut.su обрезал название, Shikimori держит полное
        // («…wo Kagenagara Osewa suru» против «…wo Kagenagara Osewa suru Koto ni Narimashita»).
        var rec = Recorded();
        const string orig = "Saijo no Osewa: Takane no Hanadarake na Meimonkou de, Gakuin Ichi no Ojousama " +
                            "(Seikatsu Nouryoku Kaimu) wo Kagenagara Osewa suru";

        var m = JutSuMatch.Pick(orig, "Забота об одарённой девушке", new[] { 2026 }, CandsFor(rec, orig));

        Assert.True(m.ok);
        Assert.Equal("romaji_prefix", m.reason);
        Assert.Equal(62876, m.pick.id);
    }

    [Fact]
    public void Короткий_префикс_не_считается_совпадением()
    {
        // «Naruto» — префикс «Naruto Shippuuden», но это РАЗНЫЕ тайтлы. Порог длины ровно за этим.
        var cands = new[] { C(1, "Naruto Shippuuden", "Наруто: Ураганные хроники", "2007-02-15") };
        var m = JutSuMatch.Pick("Naruto", "Наруто", new[] { 2007 }, cands);

        Assert.False(m.ok);
    }

    [Fact]
    public void Пустая_выдача_отказ()
        => Assert.False(JutSuMatch.Pick("Mebius Dust", "Пыль Мёбиуса", new[] { 2026 },
                                        Array.Empty<JutAnimeCandidate>()).ok);

    #endregion

    #region промежутки лет (бакеты каталога) и уточнения «(TV)» / «(год)» — qdl 2.122

    // Боевой замер 26.09.2026: 457 отказов year_mismatch из 1357 тайтлов. Причина одна на всех:
    // класс anime_year_2008-2014 на карточке каталога читался как «2008, 2014», и вето «±1 год»
    // резало кандидата с годом ВНУТРИ бакета. Второй, независимый случай — жалоба владельца на
    // размытый постер «ДжоДжо»: точное романдзи в Shikimori — OVA 1993, сериал — «… (TV)».

    static JutYearSpan Span(int a, int b) => new(a, b);

    [Fact]
    public void Бакет_каталога_покрывает_год_внутри_а_не_только_края()
    {
        // «Durarara!!» (2010): карточка помечена anime_year_2008-2014 anime_year_2015-2023
        var cands = new[] { C(6746, "Durarara!!", "Дюрарара!!", "2010-01-08") };
        var m = JutSuMatch.Pick("Durarara!!", "Дюрарара!!", new[] { Span(2008, 2014), Span(2015, 2023) }, cands);

        Assert.True(m.ok);
        Assert.Equal("romaji", m.reason);
    }

    [Fact]
    public void Бакет_before2000_открыт_снизу()
    {
        var cands = new[] { C(45, "Rurouni Kenshin: Meiji Kenkaku Romantan", "Бродяга Кэнсин", "1996-01-10") };
        var m = JutSuMatch.Pick("Rurouni Kenshin: Meiji Kenkaku Romantan", "Бродяга Кэнсин",
                                new[] { JutYearSpan.ParseClass("before2000").Value, Span(2015, 2023) }, cands);

        Assert.True(m.ok);
        Assert.Equal(45, m.pick.id);
    }

    [Fact]
    public void Год_вне_бакета_по_прежнему_вето()
    {
        // 1999 против бакета 2008-2014 — другая экранизация, допуск ±1 не спасает
        var cands = new[] { C(136, "Hunter x Hunter", "Охотник х Охотник", "1999-10-16") };
        var m = JutSuMatch.Pick("Hunter x Hunter", "Хантер х Хантер", new[] { Span(2008, 2014) }, cands);

        Assert.False(m.ok);
        Assert.Equal("year_mismatch", m.reason);
    }

    [Fact]
    public void Точное_имя_не_по_году_а_вариант_с_уточнением_подходит_берём_вариант()
    {
        // 🔥 Жалоба владельца 26.09.2026: у «ДжоДжо» размытый постер. Хаб jut.su зовётся
        // «JoJo no Kimyou na Bouken» и несёт годы сериала (2012…), а точное имя в Shikimori —
        // OVA 1993 года; сериал там — «JoJo no Kimyou na Bouken (TV)», 2012. Живая выдача дословно.
        var cands = new[]
        {
            C(666,   "JoJo no Kimyou na Bouken", "Невероятное приключение ДжоДжо OVA", "1993-11-19", "ova"),
            C(37991, "JoJo no Kimyou na Bouken Part 5: Ougon no Kaze", "Золотой ветер", "2018-10-06"),
            C(38972, "JoJo no Kimyou na Bouken Part 5: Ougon no Kaze Recaps", "Рекапы", "2019-01-05", "tv_special"),
            C(14719, "JoJo no Kimyou na Bouken (TV)", "Невероятное приключение ДжоДжо", "2012-10-06"),
            C(665,   "JoJo no Kimyou na Bouken: Adventure", "OVA (2000)", "2000-05-25", "ova"),
            C(3603,  "JoJo no Kimyou na Bouken: Phantom Blood", "Призрачная кровь", "2007-02-17", "movie"),
            C(41306, "JoJo no Kimyou na Bouken: Phantom Blood Pilot", "Пилотный выпуск", "2004-03-28", "special"),
            C(61469, "Steel Ball Run: JoJo no Kimyou na Bouken", "Гонка «Стальной шар»", "2026-03-19", "ona")
        };
        var m = JutSuMatch.Pick("JoJo no Kimyou na Bouken", "Невероятные приключения ДжоДжо",
                                new[] { 2012, 2014, 2015, 2016, 2018, 2021, 2026 }, cands);

        Assert.True(m.ok);
        Assert.Equal("romaji_variant", m.reason);
        Assert.Equal(14719, m.pick.id);
    }

    [Fact]
    public void Уточнение_годом_подходит_под_бакет_каталога()
    {
        // «Hunter x Hunter» на карточке — бакет 2008-2014; в Shikimori 1999-й и «(2011)»
        var cands = new[]
        {
            C(136,   "Hunter x Hunter", "Охотник х Охотник", "1999-10-16"),
            C(11061, "Hunter x Hunter (2011)", "Охотник х Охотник (2011)", "2011-10-02")
        };
        var m = JutSuMatch.Pick("Hunter x Hunter", "Хантер х Хантер", new[] { Span(2008, 2014) }, cands);

        Assert.True(m.ok);
        Assert.Equal("romaji_variant", m.reason);
        Assert.Equal(11061, m.pick.id);
    }

    [Fact]
    public void Точное_имя_подходит_по_году_вариант_не_рассматривается()
    {
        // Хаб содержит и 1999-й, и 2011-й: корень франшизы — честный выбор, как и раньше
        var cands = new[]
        {
            C(136,   "Hunter x Hunter", "Охотник х Охотник", "1999-10-16"),
            C(11061, "Hunter x Hunter (2011)", "Охотник х Охотник (2011)", "2011-10-02")
        };
        var m = JutSuMatch.Pick("Hunter x Hunter", "Хантер х Хантер",
                                new[] { JutYearSpan.ParseClass("before2000").Value, Span(2008, 2014) }, cands);

        Assert.True(m.ok);
        Assert.Equal("romaji", m.reason);
        Assert.Equal(136, m.pick.id);
    }

    [Fact]
    public void Вариант_с_уточнением_вне_года_не_спасает()
    {
        var cands = new[]
        {
            C(1, "Trigun", "Триган", "1998-04-01"),
            C(2, "Trigun (2030)", "Триган", "2030-01-01")
        };
        var m = JutSuMatch.Pick("Trigun", "Триган", new[] { 2023 }, cands);

        Assert.False(m.ok);
        Assert.Equal("year_mismatch", m.reason);
    }

    [Fact]
    public void Два_варианта_под_год_отказ()
    {
        // Догадка и здесь дешевле отказа — и её здесь нет
        var cands = new[]
        {
            C(1, "Grappler Baki", "Боец Баки", "1994-08-21", "ova"),
            C(2, "Grappler Baki (TV)", "Боец Баки", "2001-01-08"),
            C(3, "Grappler Baki (2001)", "Боец Баки", "2001-07-23")
        };
        var m = JutSuMatch.Pick("Grappler Baki", "Боец Баки", new[] { Span(2000, 2007) }, cands);

        Assert.False(m.ok);
        Assert.Equal(JutMatchVerdict.Ambiguous, m.verdict);
    }

    [Fact]
    public void Уточнением_считаются_только_TV_и_год()
    {
        Assert.Equal("JoJo no Kimyou na Bouken", JutSuMatch.VariantBase("JoJo no Kimyou na Bouken (TV)"));
        Assert.Equal("Hunter x Hunter", JutSuMatch.VariantBase("Hunter x Hunter (2011)"));
        Assert.Null(JutSuMatch.VariantBase("JoJo no Kimyou na Bouken"));
        Assert.Null(JutSuMatch.VariantBase("Fullmetal Alchemist: Brotherhood"));
        Assert.Null(JutSuMatch.VariantBase("Kimi no Na wa. (Movie)"));
        Assert.Null(JutSuMatch.VariantBase(null));
    }

    [Fact]
    public void Русский_ключ_не_принимает_бакет_за_год()
    {
        // Русское имя плюс семилетнее окно — слишком слабо для чужого постера
        var cands = new[] { C(5, "Yani Neko", "Табакошка", "2010-07-03") };

        Assert.False(JutSuMatch.Pick("Smoking Cat", "Табакошка", new[] { Span(2008, 2014) }, cands).ok);
        Assert.True(JutSuMatch.Pick("Smoking Cat", "Табакошка", new[] { Span(2010, 2010) }, cands).ok);
    }

    [Theory]
    [InlineData("2026", 2026, 2026)]
    [InlineData("2015-2023", 2015, 2023)]
    [InlineData("before2000", 0, 1999)]
    public void Класс_года_каталога_разбирается_в_промежуток(string cls, int from, int to)
    {
        var sp = JutYearSpan.ParseClass(cls);
        Assert.True(sp.HasValue);
        Assert.Equal((from, to), (sp.Value.from, sp.Value.to));
    }

    [Fact]
    public void Служебный_и_незнакомый_класс_года_не_промежуток()
    {
        Assert.Null(JutYearSpan.ParseClass("ongoing"));
        Assert.Null(JutYearSpan.ParseClass("2023-2015"));
        Assert.Null(JutYearSpan.ParseClass(""));
    }

    [Fact]
    public void Промежутки_переживают_JSON_а_без_поля_null()
    {
        var spans = new List<JutYearSpan> { Span(0, 1999), Span(2008, 2014), Span(2026, 2026) };
        var back = JutYearSpan.FromJson(new JArray(spans.Select(s => s.ToJson())));

        Assert.Equal(spans, back);
        Assert.Null(JutYearSpan.FromJson(null));
        Assert.Null(JutYearSpan.FromJson(new JObject()));
        Assert.Equal("<2000,2008-2014,2026", string.Join(",", spans));
    }

    #endregion

    #region санити картинки

    static byte[] Jpeg(int w, int h)
    {
        // FFD8 + SOF0: длина(2) точность(1) высота(2) ширина(2)
        var b = new byte[9000];
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xC0;
        b[4] = 0x00; b[5] = 0x11; b[6] = 0x08;
        b[7] = (byte)(h >> 8); b[8] = (byte)(h & 0xFF);
        b[9] = (byte)(w >> 8); b[10] = (byte)(w & 0xFF);
        return b;
    }

    static byte[] Png(int w, int h)
    {
        var b = new byte[9000];
        b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    [Fact]
    public void Размеры_читаются_из_заголовка()
    {
        Assert.Equal((460, 690), JutSuMatch.ImageSize(Jpeg(460, 690)));
        Assert.Equal((460, 650), JutSuMatch.ImageSize(Png(460, 650)));
    }

    [Fact]
    public void Обложка_AniList_проходит_санити()
        => Assert.True(JutSuMatch.ArtAcceptable(Jpeg(460, 690), 300, out _, out _, out string mime)
                       && mime == "image/jpeg");

    [Fact]
    public void Квадрат_186_на_186_не_проходит()
    {
        // Ровно нынешний постер jut.su: заменять его такой же мелочью бессмысленно.
        // Проверяем на боевом пороге (200), а не на произвольном.
        Assert.False(JutSuMatch.ArtAcceptable(Jpeg(186, 186), 200, out _, out _, out _));
    }

    [Fact]
    public void Запасной_источник_Shikimori_проходит_порог()
    {
        // 🔥 Порог обязан пропускать 225–240 px: это размер обложек Shikimori — запасного
        // источника, когда AniList недоступен. Подними порог выше — и лестница фолбэка умрёт
        // молча (ровно это и случилось на первом прогоне с порогом 300).
        Assert.True(JutSuMatch.ArtAcceptable(Jpeg(225, 350), 200, out _, out _, out _));
        Assert.True(JutSuMatch.ArtAcceptable(Jpeg(240, 360), 200, out _, out _, out _));
    }

    [Fact]
    public void Пейзаж_и_узкая_картинка_не_проходят()
    {
        Assert.False(JutSuMatch.ArtAcceptable(Jpeg(1920, 1080), 200, out _, out _, out _)); // фон, а не постер
        Assert.False(JutSuMatch.ArtAcceptable(Jpeg(150, 300), 200, out _, out _, out _));   // уже минимума
    }

    [Fact]
    public void Не_картинка_не_проходит()
    {
        var html = System.Text.Encoding.UTF8.GetBytes(new string('<', 9000));
        Assert.False(JutSuMatch.ArtAcceptable(html, 300, out _, out _, out _));
        Assert.Null(JutSuMatch.SniffMime(html));
    }

    #endregion
}
