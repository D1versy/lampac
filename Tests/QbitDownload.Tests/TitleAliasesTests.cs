using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Services.Utilities;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// TitleAliases (qdl 2.118) — чистые функции резолвера псевдонимов. Фикстуры — с живых ответов TMDB и
/// Shikimori по «Истребителю демонов: Бесконечный замок» (tmdb 1311031, 2026-09-14): именно там
/// русское имя TMDB разошлось с трекерным и карточка осталась с одним bitmagnet.
/// </summary>
public class TitleAliasesTests
{
    const string CardTitle = "Истребитель демонов: Бесконечный замок";
    const string CardOriginal = "劇場版「鬼滅の刃」無限城編 第一章 猗窩座再来";

    static readonly (string iso, string title, string type)[] LiveAlts =
    {
        ("US", "Demon Slayer -Kimetsu no Yaiba- the Movie: Infinity Castle", ""),
        ("JP", "Gekijouban Kimetsu no Yaiba Mugenjou-hen", "romaji"),
        ("JP", "Kimetsu no Yaiba: Mugenjou-hen", "romaji"),
        ("RU", "Истребитель демонов: Бесконечная крепость", ""),
        ("RU", "Клинок, рассекающий демонов: Бесконечный замок", ""),
        ("RU", "Клинок, рассекающий демонов: Бесконечный замок — Возвращение Акадзы", ""),
        ("UA", "Вбивця Демонів: Замок Нескінченності", ""),
        ("JP", "Gekijo-ban Kimetsu No Yaiba Mugen Jo-hen", "romanized"),
    };

    static JObject Tmdb(string title, string original, string lang, bool tv, params (string iso, string title, string type)[] alts)
    {
        var arr = new JArray(alts.Select(a => new JObject { ["iso_3166_1"] = a.iso, ["title"] = a.title, ["type"] = a.type }));
        var o = new JObject { ["id"] = 1311031, ["original_language"] = lang };
        if (tv) { o["name"] = title; o["original_name"] = original; o["alternative_titles"] = new JObject { ["results"] = arr }; }
        else { o["title"] = title; o["original_title"] = original; o["alternative_titles"] = new JObject { ["titles"] = arr }; }
        return o;
    }

    // ── TMDB ────────────────────────────────────────────────────────────────
    [Fact]
    public void FromTmdb_RuFirst_ThenEn_ThenRomaji_ForJapaneseOriginal()
    {
        var list = QbitController.AliasesFromTmdb(Tmdb(CardTitle, CardOriginal, "ja", tv: false, LiveAlts),
                                                  "Demon Slayer: Kimetsu no Yaiba Infinity Castle", CardTitle, CardOriginal);
        var names = list.Select(a => a.alias).ToList();

        // русские записи — первыми, среди них ТО САМОЕ трекерное имя
        Assert.Equal("Истребитель демонов: Бесконечная крепость", names[0]);
        Assert.Contains("Клинок, рассекающий демонов: Бесконечный замок", names);
        // затем русский title TMDB и английское
        Assert.True(names.IndexOf("Demon Slayer: Kimetsu no Yaiba Infinity Castle") > names.IndexOf("Клинок, рассекающий демонов: Бесконечный замок"));
        // romaji есть (оригинал иероглифами), romanized — тоже, US/UA записи — нет
        Assert.Contains("Gekijouban Kimetsu no Yaiba Mugenjou-hen", names);
        Assert.Contains("Gekijo-ban Kimetsu No Yaiba Mugen Jo-hen", names);
        Assert.DoesNotContain("Вбивця Демонів: Замок Нескінченності", names);
        Assert.DoesNotContain("Demon Slayer -Kimetsu no Yaiba- the Movie: Infinity Castle", names);
        Assert.All(list, a => Assert.Equal(TitleAlias.SrcTmdb, a.source));
    }

    [Fact]
    public void FromTmdb_LatinOriginal_NoRomaji()
    {
        var alts = new[] { ("RU", "Дюна: Часть вторая", ""), ("JP", "Dyun Paato Tsuu", "romaji") };
        var list = QbitController.AliasesFromTmdb(Tmdb("Дюна: Часть вторая", "Dune: Part Two", "en", tv: false, alts), "Dune: Part Two", "Дюна: Часть вторая", "Dune: Part Two");
        Assert.DoesNotContain(list, a => a.alias == "Dyun Paato Tsuu");
    }

    [Fact]
    public void FromTmdb_TvShape_ResultsArray_AndNameField()
    {
        var alts = new[] { ("RU", "Клинок, рассекающий демонов", ""), ("JP", "Kimetsu no Yaiba", "romaji") };
        var list = QbitController.AliasesFromTmdb(Tmdb("Истребитель демонов", "鬼滅の刃", "ja", tv: true, alts), "Demon Slayer: Kimetsu no Yaiba", "Истребитель демонов", "鬼滅の刃");
        var names = list.Select(a => a.alias).ToList();
        Assert.Contains("Клинок, рассекающий демонов", names);
        Assert.Contains("Истребитель демонов", names);   // name (tv) как ru title
        Assert.Contains("Kimetsu no Yaiba", names);
    }

    [Fact]
    public void FromTmdb_Null_Empty()
        => Assert.Empty(QbitController.AliasesFromTmdb(null, "x", "y", "z"));

    // ── Shikimori ───────────────────────────────────────────────────────────
    [Fact]
    public void FromShikimori_CollectsRussianLicenseRomajiEnglishSynonyms()
    {
        var pick = new JutAnimeCandidate { id = 62546, name = "Kimetsu no Yaiba Movie: Mugenjou-hen", russian = "Клинок, рассекающий демонов: Бесконечный замок", kind = "movie" };
        var detail = new JObject
        {
            ["english"] = new JArray("Demon Slayer: Kimetsu no Yaiba Infinity Castle"),
            ["synonyms"] = new JArray("Demon Slayer: Kimetsu no Yaiba - The Movie: Infinity Castle - Part 1"),
            ["license_name_ru"] = "Клинок, рассекающий демонов. Бесконечный замок"
        };
        var list = QbitController.AliasesFromShikimori(pick, detail);
        Assert.Equal("Клинок, рассекающий демонов: Бесконечный замок", list[0].alias);
        Assert.Equal("Клинок, рассекающий демонов. Бесконечный замок", list[1].alias);   // лицензионное — сразу за russian
        Assert.Contains(list, a => a.alias == "Kimetsu no Yaiba Movie: Mugenjou-hen");
        Assert.Contains(list, a => a.alias == "Demon Slayer: Kimetsu no Yaiba Infinity Castle");
        Assert.Contains(list, a => a.alias.EndsWith("Part 1"));
        Assert.All(list, a => Assert.Equal(TitleAlias.SrcShikimori, a.source));
    }

    [Fact]
    public void FromShikimori_NoDetail_StillRussianAndRomaji()
    {
        var list = QbitController.AliasesFromShikimori(new JutAnimeCandidate { id = 1, name = "Naruto", russian = "Наруто" }, null);
        Assert.Equal(new[] { "Наруто", "Naruto" }, list.Select(a => a.alias).ToArray());
    }

    [Theory]
    [InlineData("movie", false, true)]
    [InlineData("tv", false, false)]      // фильм ищем среди фильмов: сериал того же имени — не он
    [InlineData("tv", true, true)]
    [InlineData("ona", true, true)]
    [InlineData("movie", true, false)]
    [InlineData("", true, true)]          // тип не заявлен — не отсекаем
    public void ShikiKindOk_MovieVsTv(string kind, bool tv, bool ok)
        => Assert.Equal(ok, QbitController.ShikiKindOk(kind, tv));

    // ── слияние и выбор для поиска ──────────────────────────────────────────
    [Fact]
    public void Merge_Dedupes_SkipsCardNames_Short_AndCaps()
    {
        var src = new List<TitleAlias>
        {
            TitleAlias.Make("Клинок, рассекающий демонов", "tmdb"),
            TitleAlias.Make("Клинок рассекающий демонов", "shikimori"),   // та же норма → дубль
            TitleAlias.Make(CardTitle, "tmdb"),                           // имя карточки → мимо
            TitleAlias.Make("Ай", "tmdb"),                                // норма < 3 → мимо
            TitleAlias.Make("Demon Slayer", "tmdb"),
            TitleAlias.Make("Kimetsu no Yaiba", "tmdb"),
        };
        var merged = QbitController.AliasesMerge(src, SearchNameTo.Convert(CardTitle), SearchNameTo.Convert(CardOriginal), cap: 2);
        Assert.Equal(new[] { "Клинок, рассекающий демонов", "Demon Slayer" }, merged.Select(a => a.alias).ToArray());
        Assert.Equal("tmdb", merged[0].source);   // первое вхождение побеждает
    }

    [Fact]
    public void ForSearch_ManualFirst_ThenCyrillic_ThenLatin_Capped()
    {
        var src = new List<TitleAlias>
        {
            TitleAlias.Make("Demon Slayer", "tmdb"),
            TitleAlias.Make("Клинок, рассекающий демонов", "tmdb"),
            TitleAlias.Make("Kimetsu no Yaiba", "shikimori"),
            TitleAlias.Make("Истребитель демонов (Клинок)", "claude"),
            TitleAlias.Make("Бесконечный замок", "manual"),
        };
        var pick = QbitController.AliasesForSearch(src, 3).Select(a => a.alias).ToArray();
        Assert.Equal(new[] { "Истребитель демонов (Клинок)", "Бесконечный замок", "Клинок, рассекающий демонов" }, pick);
        Assert.Empty(QbitController.AliasesForSearch(src, 0));
    }

    [Fact]
    public void Make_NormalizesAndRejectsEmpty()
    {
        var a = TitleAlias.Make("  Клинок, рассекающий демонов  ", null);
        Assert.Equal("Клинок, рассекающий демонов", a.alias);
        Assert.Equal(SearchNameTo.Convert("Клинок, рассекающий демонов"), a.norm);
        Assert.Equal(TitleAlias.SrcManual, a.source);
        Assert.Null(TitleAlias.Make("   ", "tmdb"));
        Assert.Null(TitleAlias.Make("『』", "tmdb"));   // одни иероглифы/скобки → норма пуста → нет псевдонима
    }

    // ── промпт кнопки «Фикс» ────────────────────────────────────────────────
    [Fact]
    public void FixPrompt_ListsCardsWithKindTriedAndInstructions()
    {
        var misses = new JArray(
            new JObject { ["tmdb_id"] = 1311031, ["is_tv"] = false, ["title"] = CardTitle, ["title_original"] = CardOriginal, ["year"] = 2025, ["count"] = 7, ["last_at"] = "2026-09-14T10:00:00Z", ["kind"] = "zero" },
            new JObject { ["tmdb_id"] = 270603, ["is_tv"] = true, ["title"] = "Изгнанный рыцарь", ["year"] = 2026, ["count"] = 2, ["kind"] = "noru", ["note"] = "2026-09-01: пробовал X" });
        var tried = new Dictionary<string, List<TitleAlias>>
        {
            ["m1311031"] = new List<TitleAlias> { TitleAlias.Make("Клинок, рассекающий демонов: Бесконечный замок", "tmdb") }
        };
        string p = QbitController.AliasesFixPrompt(misses, tried, new System.DateTime(2026, 9, 14, 12, 0, 0, System.DateTimeKind.Utc));

        Assert.Contains("/title-aliases", p);
        Assert.Contains("открытых промахов 2", p);
        Assert.Contains("tmdb_id=1311031, is_tv=false", p);
        Assert.Contains("оригинал: " + CardOriginal, p);
        Assert.Contains("трекеры дали 0 строк", p);
        Assert.Contains("Клинок, рассекающий демонов: Бесконечный замок [tmdb]", p);
        Assert.Contains("tmdb_id=270603, is_tv=true", p);
        Assert.Contains("трекеры отвечают, но русских строк нет", p);
        Assert.Contains("ничего не нашлось", p);          // у второй карточки автомат пуст
        Assert.Contains("пометка прошлого прогона: 2026-09-01: пробовал X", p);
        Assert.Contains("/admin/d1v/api/aliases/check", p);
        Assert.Contains("source:\"claude\"", p);
        // порядок сохранён: первая карточка раньше второй
        Assert.True(p.IndexOf("tmdb_id=1311031") < p.IndexOf("tmdb_id=270603"));
    }

    [Fact]
    public void FixPrompt_Empty_SaysNothingToDo()
    {
        string p = QbitController.AliasesFixPrompt(new JArray(), new Dictionary<string, List<TitleAlias>>(), System.DateTime.UtcNow);
        Assert.Contains("Открытых промахов нет", p);
    }

    [Theory]
    [InlineData("ja", true)]
    [InlineData("zh", true)]
    [InlineData("ko", true)]
    [InlineData("en", false)]
    [InlineData("ru", false)]
    [InlineData(null, false)]
    public void LooksAsian(string lang, bool expected)
        => Assert.Equal(expected, QbitController.AliasLooksAsian(lang));
}
