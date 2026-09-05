using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Services.Utilities;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>TorrentScoring — чистый скоринг выдачи /qdl/search (класс публичный, без reflection).</summary>
public class TorrentScoringTests
{
    static readonly DateTime Now = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    static ScoreCtx SerialCtx(string title = "Тестовый сериал", string original = "Test Serial", int year = 2024, int season = 2)
        => new ScoreCtx { titleNorm = SearchNameTo.Convert(title), originalNorm = SearchNameTo.Convert(original), year = year, isSerial = true, wantSeason = season, preferredQuality = 2160, now = Now };

    static ScoreCtx MovieCtx(string title = "Фильм", int year = 2020)
        => new ScoreCtx { titleNorm = SearchNameTo.Convert(title), originalNorm = null, year = year, isSerial = false, wantSeason = 0, preferredQuality = 2160, now = Now };

    static JObject T(string title, int sid, int pir = 1, string parselink = null, string date = null, long sizeBytes = 0)
        => new JObject { ["title"] = title, ["sid"] = sid, ["pir"] = pir, ["parselink"] = parselink, ["date"] = date, ["sizeBytes"] = sizeBytes, ["quality"] = QualityFrom(title) };

    static int QualityFrom(string t)
    {
        var m = System.Text.RegularExpressions.Regex.Match(t ?? "", "(2160|1080|720|480)p?");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    // ── ParseEpCoverage ──────────────────────────────────────────────────
    [Theory]
    [InlineData("Сериал [1-8 из 12]", 8, 12)]
    [InlineData("Сериал (8 из 12)", 8, 12)]
    [InlineData("Тайтл [09 из 24] WEB-DL", 9, 24)]
    [InlineData("Серии: 1-16 (16)", 16, 16)]
    // формат kinozal: слово «серии» между диапазоном и «из», плюс «сезон:» перед ним
    [InlineData("Укрытие (Бункер) (3 сезон: 1-5 серии из 10) / Silo / 2026", 5, 10)]
    [InlineData("Аутсорс (1 сезон: 1-8 серии из 8) / 2024 / РУ, СТ", 8, 8)]
    [InlineData("Сериал (2 сезон: 3 серия из 12)", 3, 12)]
    public void ParseEpCoverage_Formats(string title, int have, int total)
    {
        var c = TorrentScoring.ParseEpCoverage(title);
        Assert.NotNull(c);
        Assert.Equal(have, c.have);
        Assert.Equal(total, c.total);
    }

    [Fact]
    public void ParseEpCoverage_UnknownTotal()
    {
        var c = TorrentScoring.ParseEpCoverage("Сериал [5 из ??]");
        Assert.NotNull(c);
        Assert.Equal(5, c.have);
        Assert.Equal(0, c.total);
    }

    [Fact]
    public void ParseEpCoverage_SeasonGuard_NotEpisodes()
        => Assert.Null(TorrentScoring.ParseEpCoverage("Сериал [Сезон 1 из 3] WEB-DL"));

    [Theory]
    [InlineData("Друзья [10 из 10 сезонов]")]   // «сезон» ПОСЛЕ чисел — это сезоны, не серии
    [InlineData("Сериал 2 из 5 сезонов")]
    [InlineData("Сериал [1-3 из 5 сезонов]")]
    public void ParseEpCoverage_SeasonGuard_After_NotEpisodes(string title)
        => Assert.Null(TorrentScoring.ParseEpCoverage(title));

    [Fact]
    public void ParseEpCoverage_Garbage_Null()
        => Assert.Null(TorrentScoring.ParseEpCoverage("Просто фильм (2024) BDRip 1080p"));

    // ── IsNonVideo: «левые» раздачи, которые не могут быть фильмом ────────
    [Theory]
    [InlineData("Фрэнк Герберт - Дюна 4, Бог-император Дюны (2021) MP3")]
    [InlineData("OST - Minions / Миньоны - Саундтрек [Score] (2015) MP3")]
    [InlineData("Электроклуб - Дискография (11 Альбомов + 1 Миньон) (1987-2008) MP3")]
    [InlineData("Татьяна Коростышевская | Цикл: «Миньон» [3 книги] (2018-2020) [EPUB]")]
    [InlineData("Елизавета Павлова | Монстры с человеческим лицом (2025) [FB2, MOBI]")]
    public void IsNonVideo_Junk(string title) => Assert.True(TorrentScoring.IsNonVideo(title));

    // Видео-признак обязателен: релиз с бонусной звуковой дорожкой — не мусор
    [Theory]
    [InlineData("Дюна / Dune (2021) BDRemux 1080p + OST")]
    [InlineData("Миньоны / Minions (2015) BDRip 720p | MP3 5.1")]
    [InlineData("Дюна / Dune (2021) WEB-DL 2160p")]
    [InlineData("Укрытие (Бункер) (3 сезон: 1-6 серии из 10) / Silo / 2026")]
    [InlineData("Ghost in the Shell (1995) BDRip")]          // «ost» внутри слова — не маркер
    public void IsNonVideo_RealRelease_False(string title) => Assert.False(TorrentScoring.IsNonVideo(title));

    // ── IsOtherMovieYear: сиквел франшизы — это другой фильм ──────────────
    [Theory]
    // «Дюна (2021)» ищем — «Часть вторая (2024)» не она
    [InlineData("Дюна: Часть вторая / Dune: Part Two (2024) UHD BDRemux", 2021, false, true)]
    [InlineData("Миньоны: Грювитация / Minions: The Rise of Gru (2022) BDRip", 2015, false, true)]
    public void IsOtherMovieYear_Sequel(string title, int year, bool serial, bool expected)
        => Assert.Equal(expected, TorrentScoring.IsOtherMovieYear(title, year, serial));

    [Theory]
    // сам фильм — год сходится (в т.ч. ±1: релиз мог выйти на границе года)
    [InlineData("Дюна / Dune (2021) BDRemux 1080p", 2021, false)]
    [InlineData("Дюна / Dune (2022) WEB-DL", 2021, false)]
    // ⚠️ год в САМОМ названии: «2049» — часть имени, спасает второй найденный год 2017
    [InlineData("Бегущий по лезвию 2049 / Blade Runner 2049 (2017) BDRip", 2017, false)]
    // года в названии нет вовсе → отсутствие данных не улика, не режем
    [InlineData("Дюна / Dune BDRemux 2160p", 2021, false)]
    // сериал: раздачи законно датируются годами позже старта (сезоны)
    [InlineData("Игра престолов / Game of Thrones (2019) 8 сезон", 2011, true)]
    public void IsOtherMovieYear_НеРежет(string title, int year, bool serial)
        => Assert.False(TorrentScoring.IsOtherMovieYear(title, year, serial));

    [Fact]
    public void Год_карточки_неизвестен_фильтр_молчит()
        => Assert.False(TorrentScoring.IsOtherMovieYear("Дюна: Часть вторая (2024)", 0, false));

    [Fact]
    public void SortAndMark_ВырезаетСиквел_НоНеТронетIdMatch()
    {
        var right = Tor("Дюна / Dune (2021) BDRemux 1080p", 20);
        var sequel = Tor("Дюна: Часть вторая / Dune: Part Two (2024) UHD BDRemux", 90);
        // найдено по TMDB id — совпадение точное, год ему не указ
        var byId = new JObject { ["title"] = "Dune.2024.Part.Two.2160p", ["sid"] = 5, ["id_match"] = true };

        var res = TorrentScoring.SortAndMark(new JArray(right, sequel, byId), Ctx("Дюна", "Dune", 2021, false), 5);

        var titles = res.Select(x => x.Value<string>("title")).ToList();
        Assert.DoesNotContain(titles, t => t.Contains("Часть вторая"));
        Assert.Contains(titles, t => t.Contains("(2021)"));
        Assert.Contains(titles, t => t.Contains("Dune.2024.Part.Two"));   // id_match уцелел
    }

    // ── SortAndMark: отсев чужих тайтлов ──────────────────────────────────
    [Fact]
    public void SortAndMark_DropsForeignTitleAndNonVideo()
    {
        var items = new JArray(
            Tor("Дюна / Dune (2021) BDRemux 1080p", 50),
            Tor("Фрэнк Герберт - Дюна 4, Бог-император Дюны (2021) MP3", 90),
            Tor("Отдел «С.С.С.Р» / Серии 1-8 из 8 (2012) HDTVRip", 90));

        var res = TorrentScoring.SortAndMark(items, Ctx("Дюна", "Dune", 2021, false), 5);

        Assert.Single(res);
        Assert.Equal("Дюна / Dune (2021) BDRemux 1080p", res[0].Value<string>("title"));
    }

    // Предохранитель: пустой экран хуже лишних строк
    [Fact]
    public void SortAndMark_AllNameMissed_KeepsList()
    {
        var items = new JArray(Tor("Совсем другой фильм (2021) BDRip", 10));
        var res = TorrentScoring.SortAndMark(items, Ctx("Дюна", "Dune", 2021, false), 5);
        Assert.Single(res);
    }

    // ── язык: русское в топ ───────────────────────────────────────────────
    [Theory]
    [InlineData("Миньоны / Minions (2015) BDRip 1080p", null)]              // кириллица
    [InlineData("Minions.2015.BDRip-AVC.Dub.stalkerok.new-team.mkv", null)] // рус. дубляж, БД врёт «en»
    [InlineData("Minions.2015.WEB-DL.KP.1080p-SOFCJ.mkv", null)]            // KP = Кинопоиск
    [InlineData("Minions.2015.720p.WEB-DL.Rus.HDCLUB.mkv", null)]
    [InlineData("Minions.2015.2160p.BluRay.REMUX.HEVC", true)]              // подсказка из БД
    // qdl 2.107: русские студии/маркеры из DHT (у bitmagnet все они помечены ["en"])
    [InlineData("Silo.S03E10.1080p.ColdFilm.mkv", null)]
    [InlineData("Silo.S03.720p.Ru.Ultradox", null)]
    [InlineData("[AniDub]_Gnosia.s01", null)]
    [InlineData("Dune.Prophecy.2024.S01.WEB-DL.1080p.AMZN.RHS", null)]
    [InlineData("Show.S01E01.RUS.ENG.WEB-DL.1080p", null)]
    [InlineData("Silo.S03E09.rus.LostFilm.TV.avi", null)]
    [InlineData("Silo.s03.WEBRip.XviD.Rus.RuDub.tv", null)]
    // 🔴 «укр» внутри слова — не украинское вето: «Укрытие», «Укрощение», «укрепления» (поймано при раскатке)
    [InlineData("Укрытие (Бункер) (3 сезон: 1-9 серии из 10) / Silo / 2026 / ПМ (HDrezka Studio), СТ / WEB-DL (1080p)", null)]
    [InlineData("Укрощение строптивого / Il bisbetico domato (1980) BDRip 1080p", null)]
    [InlineData("Укрытие / Silo / Сезон: 3 / Серии: 1-9 из 10 [2026, WEB-DL 1080p] MVO", null)]
    public void IsRussian_True(string title, bool? hint) => Assert.True(TorrentScoring.IsRussian(title, hint));

    // 05.09: русская раздача с ДОПОЛНИТЕЛЬНОЙ украинской дорожкой — язык закодирован легендой трекера
    // (rutor «| P, L |», kinozal «ПМ»), и вето её терять не должно; студия-омоним в позиции релиз-группы
    [Theory]
    [InlineData("Дюна: Пророчество / Дюна. Пророцтво / Dune: Prophecy [S01] (2024) WEB-DL 1080p | P, L | UKR")]
    [InlineData("Дом Дракона / Дім Дракона / House of the Dragon [S01-03] (2022-2026) WEB-DLRip-AVC | КПК | P | UKR")]
    [InlineData("Дюна: Пророчество / Dune: Prophecy (1 сезон: 1-6 серии из 6) / 2024 / ПМ (HDrezka Studio), UKR")]
    [InlineData("Dune.Prophecy.S01E03.1080p.WEB-DL.MVO.Ukr.Eng")]
    [InlineData("Silo.S03E01.1080p.WEB-DL.DoMiNo.mkv")]
    [InlineData("[Dexter] Silo.S03E01.1080p.mkv")]
    public void IsRussian_True_LegendAndGroupContext(string title) => Assert.True(TorrentScoring.IsRussian(title));

    [Theory]
    [InlineData("Dexter.Resurrection.S01E01.1080p.WEB.H264-SuccessfulCrab.mkv")]
    [InlineData("Dexter.Original.Sin.S01E01.1080p.WEB.H264-GRP.mkv")]
    [InlineData("Domino.2019.1080p.BluRay.x264-GRP.mkv")]
    [InlineData("The.Domino.Effect.2020.720p.WEB.x264")]
    [InlineData("Дім Дракона / House of the Dragon (2022) WEB-DL 1080p | 2xUkr/Eng")]
    [InlineData("Silo.S03E10.1080p.WEB-DL.Ukrainian.Dub.HDRezka.Studio.mkv")]
    [InlineData("Silo.S03E10.1080p.WEB-DL.UA.HDREZKA.STUDIO.mkv")]
    [InlineData("Бункер / Silo (Сезон 3) WEB-DL 1080p [Українською] HDRezka")]
    public void IsRussian_False_TitleHomonymsAndUkr(string title) => Assert.False(TorrentScoring.IsRussian(title));

    // Эхо DHT-строки из нашего индекса (tracker=bitmagnet, sid_hint нет — старые записи) ⭐ тоже не получает
    [Fact]
    public void BitmagnetEcho_WithoutSidHint_NeverStar()
    {
        var list = new JArray(new JObject
        {
            ["title"] = "Silo.S03E10.1080p.ATVP.WEB-DL.DDP5.1.H.264-NTb.mkv", ["tracker"] = "bitmagnet",
            ["magnet"] = "magnet:?xt=urn:btih:" + new string('c', 40), ["sid"] = 84, ["pir"] = 3, ["quality"] = 1080, ["id_match"] = true
        });
        var sorted = TorrentScoring.SortAndMark(list, Ctx("Укрытие", "Silo", 2026, true), 5);
        Assert.Single(sorted);
        Assert.Null(sorted[0]["rec"]);
    }

    [Theory]
    [InlineData("Minions.2015.UHD.BluRay.2160p.TrueHD.Atmos.7.1.HEVC.REMUX-FraMeSToR")]
    [InlineData("[ OxTorrent.com ] Minions.2015.TRUEFRENCH.BDRiP.XViD-AViTECH.avi")]
    [InlineData("Minions (2015) 2160p H265 10 bit ita eng AC3 5.1 sub ita eng Licdom")]
    // qdl 2.107: голый «dub» больше не русский — «[English Dub]» (44 строки Гносии в DHT), «Hindi (HQ-Dub)»
    [InlineData("[Yameii] GNOSIA - S01E01 [English Dub] [CR WEB-DL 1080p]")]
    [InlineData("House of the Dragon (2022) S01-E05 1080p [Proper Hindi (HQ-Dub) + English]")]
    [InlineData("Silo.S03E10.FINAL.MULTI.1080p.WEB.H264-HiggsBoson")]
    [InlineData("Silo.S03E10.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA.mkv")]
    // украинское вето: студия ≠ язык
    [InlineData("Silo.S01E10.WEBRip.1080p.ukr.5.1.HDREZKA.STUDIO.mkv")]
    [InlineData("Silo.s02.720p.ATVP.WEB-DL.DDP5.1.Atmos.H.264.Ukr.Eng.BaibaKo.tv")]
    [InlineData("Silo.(Season 1).(2023).WEBDLRip.[Ukr_Eng]")]
    [InlineData("Бункер (Сезон 3, Серії 1-9) / Silo (Season 3, Episodes 1-9) (2026) WEB-DL 1080p H.265 Ukr/Eng")]
    // голый «ru» — не маркер (славянское слово, японский тайтл, русские СУБТИТРЫ)
    [InlineData("Ruža pre nevestu - 04x21.mp4")]
    [InlineData("To Love-Ru S01E03 1080p")]
    [InlineData("Lou Grant.S03E11.ru.subs.mp4")]
    public void IsRussian_False(string title) => Assert.False(TorrentScoring.IsRussian(title));

    // qdl 2.107: сиды bitmagnet — подсказка (sid_hint): ни «мёртвая» −20, ни бонус; ⭐ такой строке не даём
    [Fact]
    public void SidHint_IsNeutral_AndNeverStar()
    {
        var hint0 = new JObject { ["title"] = "Silo.S03E10.1080p.ColdFilm.mkv", ["sid"] = 0, ["pir"] = 0, ["id_match"] = true, ["sid_hint"] = true };
        var dead = new JObject { ["title"] = "Silo.S03E10.1080p.ColdFilm.mkv", ["sid"] = 0, ["pir"] = 0, ["id_match"] = true };
        var ctx = Ctx("Укрытие", "Silo", 2023, true);
        Assert.True(TorrentScoring.Score(hint0, ctx).score > TorrentScoring.Score(dead, ctx).score + 20);

        var hint84 = new JObject { ["title"] = "Silo.S03E10.720p.10bit.WEBRip.2CH.x265.HEVC-PSA.mkv", ["sid"] = 84, ["id_match"] = true, ["sid_hint"] = true };
        var res = TorrentScoring.SortAndMark(new JArray(hint84), ctx, 5);
        Assert.Single(res);
        Assert.Null(res[0]["rec"]);   // 84 «сида» из краулера звезду не дают
    }

    // Русская раздача обязана быть выше иностранной, даже если у той кратно больше сидов
    [Fact]
    public void Russian_Beats_Foreign_EvenWithFarMoreSeeds()
    {
        var ru = Tor("Миньоны / Minions (2015) BDRip 1080p", 5);
        var foreign = Tor("Minions.2015.UHD.BluRay.2160p.TrueHD.Atmos.HEVC.REMUX-FraMeSToR", 300);

        var res = TorrentScoring.SortAndMark(new JArray(foreign, ru), Ctx("Миньоны", "Minions", 2015, false), 5);

        Assert.Equal(2, res.Count);
        Assert.Equal(5, res[0].Value<int>("sid"));   // русская первой, несмотря на 5 против 300
    }

    // Раздача, найденная по TMDB id, не проверяется по имени и не режется как «чужой тайтл»
    [Fact]
    public void IdMatch_SurvivesNameFilter()
    {
        var byId = new JObject { ["title"] = "Dune.2021.BDREMUX.2160p.HDR.mkv", ["sid"] = 3, ["id_match"] = true };
        var res = TorrentScoring.SortAndMark(new JArray(byId), Ctx("Дюна", null, 2021, false), 5);
        Assert.Single(res);   // без id_match латиница против русской карточки = nameMiss = вылет
    }

    static JObject Tor(string title, int sid) => new JObject { ["title"] = title, ["sid"] = sid };

    static ScoreCtx Ctx(string ru, string en, int year, bool serial) => new ScoreCtx
    {
        titleNorm = Shared.Services.Utilities.SearchNameTo.Convert(ru),
        originalNorm = Shared.Services.Utilities.SearchNameTo.Convert(en),
        year = year,
        isSerial = serial,
        preferredQuality = 2160
    };

    [Fact]
    public void ParseEpCoverage_Ongoing()
    {
        Assert.True(TorrentScoring.ParseEpCoverage("Сериал [1-8 из 12]").ongoing);
        Assert.False(TorrentScoring.ParseEpCoverage("Сериал [12 из 12]").ongoing);
    }

    // ── ParseSeasons ─────────────────────────────────────────────────────
    [Theory]
    [InlineData("Сериал (2 сезон)", new[] { 2 })]
    [InlineData("Сериал [Сезон: 1-3]", new[] { 1, 2, 3 })]
    [InlineData("Show S02E05 WEB-DL", new[] { 2 })]
    [InlineData("Show S01-S03 Complete", new[] { 1, 2, 3 })]
    [InlineData("Show Season 2", new[] { 2 })]
    [InlineData("Сериал [1-2 сезоны]", new[] { 1, 2 })]
    // Формат kinozal «N сезон: A-B серии»: A-B — это СЕРИИ. Раньше отдавалось {1..10}, множество
    // накрывало любой охотимый сезон, и раздача 2-го сезона уезжала донором в 3-й («Укрытие»).
    [InlineData("Укрытие (Бункер) (2 сезон: 1-10 серии из 10) / Silo / 2024 / ПМ (NewComers), СТ / WEB-DLRip | NewComers", new[] { 2 })]
    [InlineData("Укрытие (Бункер) (3 сезон: 1-6 серии из 10) / Silo / 2026 / ПМ (HDrezka Studio), СТ", new[] { 3 })]
    [InlineData("Укрытие (Бункер) (1-3 сезоны: 1-23 серии из 30) / Silo / 2023-2026", new[] { 1, 2, 3 })]
    [InlineData("Сериал (12 сезон: 1-10 серии)", new[] { 12 })]
    // легитимные диапазоны сезонов — не сломать
    [InlineData("Сериал (Сезон 1-3 из 5)", new[] { 1, 2, 3 })]
    [InlineData("Сериал 2024 Сезон: 1-3", new[] { 1, 2, 3 })]
    [InlineData("Show Season 1-3", new[] { 1, 2, 3 })]
    [InlineData("Укрытие / Silo / Сезон: 3 / Серии: 1-6 из 10 [2026]", new[] { 3 })]
    public void ParseSeasons_Formats(string title, int[] expected)
        => Assert.Equal(expected, TorrentScoring.ParseSeasons(title));

    [Fact]
    public void ParseSeasons_NoFalsePositives_OnQuality()
        => Assert.Empty(TorrentScoring.ParseSeasons("Фильм (2024) WEB-DL 1080p x264"));

    // Гард ParseSeasons не должен задеть фикс §AZ: полнота серий у того же kinozal-формата цела.
    [Fact]
    public void ParseEpCoverage_KinozalFormat_StillParsed()
    {
        var cov = TorrentScoring.ParseEpCoverage("Укрытие (Бункер) (2 сезон: 1-10 серии из 10) / Silo / 2024");
        Assert.Equal(10, cov.have);
        Assert.Equal(10, cov.total);
    }

    // ── Score / SortAndMark ──────────────────────────────────────────────
    [Fact]
    public void RelevantLowSeeds_Beats_IrrelevantHighSeeds()
    {
        var relevant = T("Тестовый сериал / Test Serial [2 сезон, 1-8 из 12] (2024) WEB-DL 1080p", 30);
        var alien = T("Совсем другое кино (2018) BDRip 2160p", 700, 100);
        var sorted = TorrentScoring.SortAndMark(new JArray(alien, relevant), SerialCtx(), 5);
        Assert.Equal(30, sorted[0].Value<int>("sid"));   // релевантная — первой
        Assert.True(sorted[0].Value<bool?>("rec") == true);
        // чужой тайтл теперь не просто лишён ⭐, а вырезан из списка (раньше оставался ниже)
        Assert.Single(sorted);

        // без отсева поведение прежнее: чужая остаётся, но ⭐ не получает
        var kept = TorrentScoring.SortAndMark(new JArray(alien, relevant), SerialCtx(), 5, dropIrrelevant: false);
        Assert.Equal(2, kept.Count);
        Assert.False(kept[1].Value<bool?>("rec") == true);
    }

    [Fact]
    public void MovieQuery_SerialRelease_Sinks()
    {
        var movie = T("Фильм (2020) BDRip", 10);
        var serial = T("Фильм (3 сезон) WEB-DL", 500);
        var sorted = TorrentScoring.SortAndMark(new JArray(serial, movie), MovieCtx(), 5);
        Assert.Equal(10, sorted[0].Value<int>("sid"));
        Assert.False(sorted.Any(x => x.Value<bool?>("rec") == true && x.Value<int>("sid") == 500));   // typeMiss — не ⭐
    }

    [Fact]
    public void DeadTorrent_Sinks()
    {
        var dead = T("Тестовый сериал (2024) 1080p", 0, 0);
        var alive = T("Тестовый сериал (2024) 1080p", 5);
        var sorted = TorrentScoring.SortAndMark(new JArray(dead, alive), SerialCtx(), 5);
        Assert.Equal(5, sorted[0].Value<int>("sid"));
        Assert.True(sorted[0].Value<double>("score") - sorted[1].Value<double>("score") > 20);
    }

    [Fact]
    public void LogScaleSeeds_500vs700_AlmostEqual()
    {
        var a = TorrentScoring.Score(T("Тестовый сериал (2024) 1080p", 500), SerialCtx());
        var b = TorrentScoring.Score(T("Тестовый сериал (2024) 1080p", 700), SerialCtx());
        Assert.True(Math.Abs(a.score - b.score) < 0.5);   // кап лог-шкалы: неразличимы
    }

    [Fact]
    public void Freshness_BadDate_Neutral()
    {
        // TorrentDetails.createTime дефолтится в UtcNow при неудачном парсе — даты из будущего/древние игнорим
        var bad = TorrentScoring.Score(T("Тестовый сериал (2024)", 10, date: "1997-05-01T00:00:00Z"), SerialCtx());
        var none = TorrentScoring.Score(T("Тестовый сериал (2024)", 10), SerialCtx());
        Assert.Equal(none.score, bad.score, 3);
    }

    [Fact]
    public void Freshness_RecentSerial_Boost()
    {
        var fresh = TorrentScoring.Score(T("Тестовый сериал (2024)", 10, date: Now.AddDays(-3).ToString("o")), SerialCtx());
        var old = TorrentScoring.Score(T("Тестовый сериал (2024)", 10, date: Now.AddDays(-400).ToString("o")), SerialCtx());
        Assert.True(fresh.score > old.score + 5);
        Assert.Contains(fresh.why, w => w.StartsWith("обновлена"));
    }

    [Fact]
    public void OngoingFresh_GetsBonus_AndEpField()
    {
        var t = T("Тестовый сериал [1-8 из 12] (2024)", 10, date: Now.AddDays(-2).ToString("o"));
        var r = TorrentScoring.Score(t, SerialCtx());
        Assert.NotNull(r.ep);
        Assert.True(r.ep.ongoing);
        Assert.Contains("обновляется", r.why);
    }

    [Fact]
    public void GracefulDegradation_MinimalFields_NoThrow()
    {
        var minimal = new JObject { ["title"] = "Тестовый сериал", ["sid"] = 7 };
        var sorted = TorrentScoring.SortAndMark(new JArray(minimal), SerialCtx(), 5);
        Assert.NotNull(sorted[0]["score"]);
        Assert.Equal("Тестовый сериал", sorted[0].Value<string>("title"));   // старые поля не тронуты
    }

    [Fact]
    public void Rec_RequiresMinSeeds()
    {
        var lowSeeds = T("Тестовый сериал (2024) 1080p", 3);
        var sorted = TorrentScoring.SortAndMark(new JArray(lowSeeds), SerialCtx(), 5);
        Assert.False(sorted.Any(x => x.Value<bool?>("rec") == true));
    }

    [Fact]
    public void Rec_OnlyOne_WithWhy()
    {
        var a = T("Тестовый сериал [2 сезон, 1-8 из 12] (2024) 1080p", 40, parselink: "http://127.0.0.1:9118/rutracker/parsemagnet?id=1");
        var b = T("Тестовый сериал (2024) 1080p", 30);
        var sorted = TorrentScoring.SortAndMark(new JArray(b, a), SerialCtx(), 5);
        Assert.Equal(1, sorted.Count(x => x.Value<bool?>("rec") == true));
        var rec = sorted.First(x => x.Value<bool?>("rec") == true);
        Assert.False(string.IsNullOrWhiteSpace(rec.Value<string>("why")));
        Assert.True(rec.Value<bool?>("watchable") == true);   // parselink у сериала → 🔔
    }

    [Fact]
    public void KillSwitchFields_EpAndWatchable_Serialized()
    {
        var t = T("Тестовый сериал [1-8 из 12] (2024)", 10, parselink: "http://x/parsemagnet");
        var sorted = TorrentScoring.SortAndMark(new JArray(t), SerialCtx(), 5);
        Assert.Equal(8, sorted[0]["ep"].Value<int>("have"));
        Assert.Equal(12, sorted[0]["ep"].Value<int>("total"));
        Assert.True(sorted[0].Value<bool>("watchable"));
    }

    // ── ⭐ и карточки, чьё имя нормализуется в пустоту ─────────────────────
    // Боевой случай 25.08.2026: у карточки 英雄傳 (tmdb 1440599) и titleNorm, и originalNorm
    // пусты, гейт имени выключается для ВСЕХ раздач сразу — и ⭐ уехала на «Как раздражать
    // людей (1969)». Список показывать можно, ручаться за него нельзя.

    [Fact]
    public void NoStar_When_CardName_Normalizes_To_Nothing()
    {
        var ctx = new ScoreCtx { titleNorm = "", originalNorm = "", year = 2026, isSerial = false, now = new DateTime(2026, 8, 25) };

        var res = TorrentScoring.SortAndMark(new JArray(
            Tor("Как раздражать людей (1969) BDRip 1080p", 40),
            Tor("Совсем другое кино (2024) WEB-DL 1080p", 30)), ctx, 5);

        Assert.DoesNotContain(res.Children<JObject>(), t => t.Value<bool?>("rec") == true);
    }

    /// <summary>
    /// А вот раздача, найденная ПО TMDB id, звезду получить обязана и на такой карточке:
    /// за её тайтл ручается источник, а не совпадение имени.
    /// </summary>
    [Fact]
    public void Star_Still_Allowed_For_IdMatch_On_Nameless_Card()
    {
        var ctx = new ScoreCtx { titleNorm = "", originalNorm = "", year = 2026, isSerial = false, now = new DateTime(2026, 8, 25) };

        var byId = Tor("B.King.2026.1080p.WEB-DL.mkv", 12);
        byId["id_match"] = true;

        var res = TorrentScoring.SortAndMark(new JArray(
            Tor("Как раздражать людей (1969) BDRip 1080p", 40), byId), ctx, 5);

        var star = res.Children<JObject>().FirstOrDefault(t => t.Value<bool?>("rec") == true);
        Assert.NotNull(star);
        Assert.Equal("B.King.2026.1080p.WEB-DL.mkv", star.Value<string>("title"));
    }

    /// <summary>«МР3» кириллицей — так подписаны аудиокниги; латинский паттерн их не ловил.</summary>
    [Theory]
    [InlineData("Дмитрий Крам - S-T-I-K-S. Холод (2023) МР3")]
    [InlineData("Сборник - Холод / Аудиокнига / МР3")]
    public void IsNonVideo_Catches_Cyrillic_Mp3(string title)
        => Assert.True(TorrentScoring.IsNonVideo(title));

    /// <summary>А нормальный релиз с «МР3» в описании дорожки — всё ещё видео.</summary>
    [Fact]
    public void IsNonVideo_Cyrillic_Mp3_With_Video_Mark_Is_Video()
        => Assert.False(TorrentScoring.IsNonVideo("Холод (2024) WEB-DL 1080p + МР3 дорожка"));
}
