using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Services.Utilities;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.107 — охота за сериями видит bitmagnet и берёт лучшее качество без мусора. Инцидент «Укрытие»
/// 2026-09-04: донором стал русский XviD-пак 720×400 («…/ WEBRip» без цифры разрешения): quality:0
/// проходил гейт качества, а порядок проб решал score, где «пак 10 из 10» весит больше, чем 1080p.
/// Здесь: новые гейты (язык, качество/кодек/экранка, своя раздача по файлам, сезон по классификатору),
/// гейт имени по TMDB id, порядок проб по рангу качества, вердикт покрытия по episodes bitmagnet,
/// fail-closed одиночного фолбэка FindEpFiles, замещение и показ по рангу, оффлайн-реплей инцидента на
/// боевых фикстурах, локальный тик, текстовые сторожа исходника и сухой прогон без побочек.
/// </summary>
public class HuntBitmagnetTests
{
    // боевые значения из инцидента: основная — rutracker-пак 3-го сезона, донор — одиночка ColdFilm
    const string MainHash = "546025a3a74001b9fee95dc4e7e30a0c28927d00";
    const string ColdFilmHash = "521fff292d6a3e1f636f3445aabd22f0a1bee9dc";
    const string ColdFilm1080 = "Silo.S03E10.1080p.ColdFilm.mkv";
    const string MainName = "Silo (Season 3) WEB-DL 1080p";
    const string RuDubPack = "Укрытие (Бункер) (3 сезон: 1-10 серии из 10) / Silo / 2026 / ПМ (RuDub) / WEBRip";
    static readonly DateTime Now = new DateTime(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

    public HuntBitmagnetTests() => TestEnv.EnsureConf();

    // ── билдеры ──────────────────────────────────────────────────────────
    static string Hex(char c) => new string(c, 40);
    static string Magnet(string btih) => "magnet:?xt=urn:btih:" + btih;

    static JToken Vid(int index, string name, double progress = 0, long size = 1_500_000_000)
        => new JObject { ["index"] = index, ["name"] = name, ["progress"] = progress, ["size"] = size };

    // трекерная строка выдачи: parselink есть, магнета нет (как Cand в EpisodeHunterTests)
    static JObject Cand(string title, int sid, string magnet = null, string parselink = "http://t/parsemagnet?id=1",
                        int quality = 1080, long sizeBytes = 12_000_000_000, double score = 50)
        => new JObject { ["title"] = title, ["sid"] = sid, ["magnet"] = magnet, ["parselink"] = parselink, ["quality"] = quality, ["sizeBytes"] = sizeBytes, ["score"] = score };

    // строка bitmagnet «как из BitmagnetItem»: магнет по btih, parselink нет, сиды — подсказка (sid_hint),
    // id_match по TMDB id, эталон имени id_title, серии/сезон/пак — из episodes классификатора
    static JObject Bm(string title, string btih, int quality = 1080, int sid = 5, double score = 50, long sizeBytes = 2_352_351_365,
                      int[] eps = null, int season = 0, bool? pack = null, string idTitle = "Silo", bool langRu = false, int pir = 0, bool idMatch = true)
    {
        var t = new JObject
        {
            ["title"] = title, ["magnet"] = Magnet(btih), ["parselink"] = null, ["tracker"] = "bitmagnet",
            ["sid"] = sid, ["pir"] = pir, ["sid_hint"] = true, ["quality"] = quality, ["sizeBytes"] = sizeBytes, ["score"] = score,
            ["lang_ru"] = langRu, ["id_match"] = idMatch
        };
        if (idTitle != null) { t["id_title"] = idTitle; t["id_title_original"] = idTitle; }
        if (eps != null) t["bm_eps"] = new JArray(eps);
        if (season > 0) t["bm_season"] = season;
        if (pack != null) t["bm_pack"] = pack.Value;
        return t;
    }

    static JObject Donor(string hash, double score, int quality, int season, params int[] eps)
    {
        var arr = new JArray();
        foreach (int e in eps)
            arr.Add(new JObject { ["epkey"] = "s" + season + "e" + e, ["season"] = season, ["ep"] = e, ["fileIndex"] = e, ["status"] = "hunted" });
        return new JObject { ["hash"] = hash, ["link"] = Magnet(hash), ["score"] = score, ["quality"] = quality, ["eps"] = arr };
    }

    // контекст охоты «Укрытие» S3: карточка укрытие/silo, известна только основная; новые гейты — явно флагами
    // (MakeHuntCtx по умолчанию держит их выключенными ради старых фикстур)
    static object Ctx(bool requireRussian = false, bool rejectUnknownQuality = false, bool rejectLegacy = true, bool rejectScreener = true,
                      int minSeeds = 3, int minQuality = 720, int season = 3,
                      IEnumerable<(string name, long size)> mainSig = null, string mainName = null, IEnumerable<string> known = null)
        => HunterAccess.MakeHuntCtx(MainHash, season, known ?? new[] { MainHash }, null, minSeeds, minQuality, 150, 8,
                                    "укрытие", "silo", null, requireRussian, rejectUnknownQuality, 1080, mainSig, mainName, rejectLegacy, rejectScreener);

    static string Why(JObject cand, object h) => HunterAccess.DropReason(cand, h);

    // ── A1. язык: донор только с русской дорожкой ────────────────────────
    [Fact]
    public void Gate_Language_RequiresRussianMarkers()
    {
        var h = Ctx(requireRussian: true);

        // английская одиночка PSA — имя проходит по id, языка нет
        Assert.Equal("язык", Why(Bm("Silo.S03E10.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA.mkv", Hex('1'), sid: 71, eps: new[] { 10 }, season: 3), h));
        // ColdFilm — русская студия в имени: краулер пишет ["en"], русскость видна только по маркеру
        Assert.Null(Why(Bm(ColdFilm1080, ColdFilmHash, sid: 2, eps: new[] { 10 }, season: 3), h));
        // «[English Dub]»: голое dub из маркеров убрано (44 строки Гносии в DHT делались «русскими»)
        var gnosia = HunterAccess.MakeHuntCtx(MainHash, 1, new[] { MainHash }, null, 3, 720, 150, 8, "гносия", null, requireRussian: true);
        Assert.Equal("язык", Why(Bm("[Yameii] GNOSIA - S01E01 [English Dub] [CR WEB-DL 1080p]", Hex('2'), eps: new[] { 1 }, season: 1, idTitle: "GNOSIA"), gnosia));
        // украинское вето: студия HDRezka рядом с «ukr» языка не доказывает
        Assert.Equal("язык", Why(Bm("Silo.S01E10.WEBRip.1080p.ukr.5.1.HDREZKA.STUDIO.mkv", Hex('3'), eps: new[] { 10 }, season: 1), h));
        // кириллический трекерный тайтл проходит язык автоматически (quality задан — остальные гейты тоже)
        var cyr = Cand(RuDubPack, 7, quality: 1080);
        Assert.NotEqual("язык", Why(cyr, h));
        Assert.Null(Why(cyr, h));
        // подсказка языка из БД при английском имени — достаточно
        Assert.Null(Why(Bm("Silo.S03E10.1080p.WEB-DL.mkv", Hex('4'), eps: new[] { 10 }, season: 3, langRu: true), h));
    }

    // ── A2. сиды: у bitmagnet — подсказка, не гейт ───────────────────────
    [Fact]
    public void Gate_Seeds_HintIsNotAGate()
    {
        var h = Ctx();   // minSeeds 3
        // в БД у ColdFilm 1080p стояло 2 сида (реально 35) — с sid_hint проходит
        Assert.Null(Why(Bm(ColdFilm1080, ColdFilmHash, sid: 2, eps: new[] { 10 }, season: 3), h));
        // те же 2 сида как измерение трекера — отсев
        var measured = Bm(ColdFilm1080, Hex('5'), sid: 2, eps: new[] { 10 }, season: 3);
        measured["sid_hint"] = false;
        Assert.Equal("сиды", Why(measured, h));
    }

    // ── A3. качество: неизвестное = ниже порога; кодек и экранка — независимо от порога ──
    [Fact]
    public void Gate_Quality_UnknownLegacyScreener()
    {
        var strict = Ctx(rejectUnknownQuality: true);   // minQuality 720

        Assert.Equal("качество не распознано", Why(Bm("Silo.S03E10.WEBRip.ColdFilm.avi", Hex('6'), quality: 0, eps: new[] { 10 }, season: 3), strict));
        Assert.Equal("качество", Why(Bm("Silo.S03E10.480p.ColdFilm.avi", Hex('7'), quality: 480, eps: new[] { 10 }, season: 3), strict));
        Assert.Null(Why(Bm("Silo.S03E10.720p.ColdFilm.mkv", Hex('8'), quality: 720, eps: new[] { 10 }, season: 3), strict));   // ровно порог — проходит

        var legacy = Bm(ColdFilm1080, Hex('9'), eps: new[] { 10 }, season: 3);
        legacy["bm_legacy"] = true;                                                     // кодек из БД классификатора
        Assert.Equal("кодек", Why(legacy, strict));
        const string xvid = "Silo.S03E10.1080p.WEB-DL.XviD.ColdFilm.avi";
        Assert.Equal("кодек", Why(Bm(xvid, Hex('a'), eps: new[] { 10 }, season: 3), strict));   // кодек из имени — даже при 1080

        var screener = Bm(ColdFilm1080, Hex('b'), eps: new[] { 10 }, season: 3);
        screener["bm_screener"] = true;
        Assert.Equal("экранка", Why(screener, strict));
        Assert.Equal("экранка", Why(Bm("Silo.S03E10.1080p.CAMRip.ColdFilm.mkv", Hex('c'), eps: new[] { 10 }, season: 3), strict));

        // ручка выключена — XviD по кодеку не отсекается
        var lenient = Ctx(rejectUnknownQuality: true, rejectLegacy: false);
        Assert.Null(Why(Bm(xvid, Hex('a'), eps: new[] { 10 }, season: 3), lenient));
    }

    // ── A4. своя раздача по файлам/имени (замена шлюза §AK-1 для DHT-строк без parselink) ──
    static readonly List<(string name, long size)> MainSig9 = Enumerable.Range(1, 9)
        .Select(n => ($"{MainName}/Silo.S03E0{n}.1080p.WEB-DL.RGzsRutracker.mkv", 4_000_000_000L + n)).ToList();

    static JArray Files9() => new JArray(Enumerable.Range(1, 9)
        .Select(n => new JObject { ["name"] = $"Silo.S03E0{n}.1080p.WEB-DL.RGzsRutracker.mkv", ["size"] = 4_000_000_000L + n }));

    [Fact]
    public void Gate_OwnRelease_ByFilesOrName()
    {
        var h = Ctx(mainSig: MainSig9, mainName: MainName);

        // перезалив нашей же раздачи под другим btih: те же 9 файлов байт-в-байт (имена без папки)
        var reupload = Bm(MainName, Hex('d'), sizeBytes: 36_000_000_000, pack: true);
        reupload["files_count"] = 9; reupload["bm_files"] = Files9();
        Assert.Equal("своя раздача (файлы)", Why(reupload, h));

        // одиночка с ОДНИМ совпавшим файлом — не своя: русский сезонный пак несёт тот же файл, что уже
        // стоит донором, и по одному совпадению вылетал бы из кандидатов на все серии
        var single = Bm("Silo.S03E05.1080p.WEB-DL.RGzsRutracker.mkv", Hex('e'), eps: new[] { 5 }, season: 3);
        single["bm_files"] = new JArray(new JObject { ["name"] = "Silo.S03E05.1080p.WEB-DL.RGzsRutracker.mkv", ["size"] = 4_000_000_005L });
        Assert.NotEqual("своя раздача (файлы)", Why(single, h));
        Assert.Null(Why(single, h));

        // over_threshold-строка без списка файлов — узнаём по имени торрента
        var byName = Bm(MainName, Hex('f'), sizeBytes: 36_000_000_000, pack: true);
        byName["files_count"] = 9;
        Assert.Equal("своя раздача (файлы)", Why(byName, h));

        // другое имя, файлов нет — не своя
        var other = Bm("Silo.S03.1080p.WEB.H264-ETHEL", Hex('0'), sizeBytes: 20_000_000_000, pack: true);
        other["files_count"] = 10;
        Assert.NotEqual("своя раздача (файлы)", Why(other, h));
        Assert.Null(Why(other, h));

        // порядок: собственный btih с теми же файлами — «уже есть», а не «своя»
        var known = Bm(MainName, MainHash, sizeBytes: 36_000_000_000, pack: true);
        known["files_count"] = 9; known["bm_files"] = Files9();
        Assert.Equal("уже есть", Why(known, h));
    }

    // ── A5. сезон по элементу: имя без сезона спасает только bm_season ───
    [Fact]
    public void SeasonOkItem_ClassifierSeasonFillsSilentName()
    {
        var h = Ctx();   // season 3
        Assert.True(HunterAccess.SeasonOkItem(Bm(ColdFilm1080, Hex('1'), eps: new[] { 10 }, season: 3), h));
        Assert.True(HunterAccess.SeasonOkItem(Bm("Silo 3x01 JP.mkv", Hex('2'), eps: new[] { 1 }, season: 3), h));   // по bm_season
        Assert.False(HunterAccess.SeasonOkItem(Bm("Silo.Ep10.mkv", Hex('3'), eps: new[] { 10 }), h));                  // ни имя, ни классификатор
        Assert.False(HunterAccess.SeasonOkItem(Bm("Silo.S02E10.1080p.mkv", Hex('4'), eps: new[] { 10 }, season: 3), h));   // имя противоречит — отсев

        // подписи тех же вердиктов в DropReason
        var silent = Bm("Silo 1080p WEB-DL", Hex('5'), sizeBytes: 20_000_000_000, pack: true);
        silent["files_count"] = 10;
        Assert.Equal("сезон не заявлен", Why(silent, h));
        Assert.Equal("сезон", Why(Bm("Silo.S02E10.1080p.mkv", Hex('4'), eps: new[] { 10 }, season: 3), h));
    }

    // ── A6. гейт имени по TMDB id: голова scene-имени до первого маркера ──
    [Theory]
    [InlineData("Silo.S03E10.1080p.ColdFilm.mkv", true, true)]
    [InlineData("Silo.2023.S03e10.Final.Troy.Ad.Atv.Web.H264.mp4", true, true)]        // год — тоже маркер
    [InlineData("Silo (Season 2) WEB-DL 1080p", true, true)]                             // строгий путь: сегмент до «(»
    [InlineData("Silo - 2x02.mp4", true, true)]
    [InlineData("Silo S03 (720p)", true, true)]
    [InlineData("Silo Season 3 Mp4 1080p", true, true)]
    [InlineData("末日地堡.Silo.S03E10.1080p.WEB-DL.mkv", true, true)]                    // CJK нормализуется в пустоту, остаётся «silo»; язык его срежет отдельно
    [InlineData("www.UIndex.org - Silo S03E10 Troy 1080p", true, false)]                // домен в голове
    [InlineData("The.House.That.Dragons.Built.S01E10.1080p", true, false)]
    [InlineData("Silo.Killer.2019.mkv", true, false)]                                    // однофамилец
    [InlineData("[ Torrent911.lol ] Silo.S03E10.FRENCH", true, false)]                  // ведущая «[группа]» похожа на домен — не срезается, голова пустая
    [InlineData("Silo.S03E10.1080p.ColdFilm.mkv", false, false)]                        // без id_match — только строгий путь, он scene-имя не пропускает
    public void NameMatchesSeriesOrId_Silo(string title, bool idMatch, bool expected)
        => Assert.Equal(expected, HunterAccess.NameMatchesSeriesOrId(Bm(title, Hex('1'), idMatch: idMatch), Ctx()));

    [Fact]
    public void NameMatchesSeriesOrId_Gnosia_JapaneseOriginal_NullGuard()
    {
        // карточка: русское «Гносия», оригинал — японский, который нормализуется в null → сверять можно только с эталоном bitmagnet
        Assert.Null(SearchNameTo.Convert("グノーシア"));
        var h = HunterAccess.MakeHuntCtx(MainHash, 1, new[] { MainHash }, null, 3, 720, 150, 8, "гносия", null);

        Assert.True(HunterAccess.NameMatchesSeriesOrId(Bm("[AniDub]_Gnosia.s01", Hex('1'), idTitle: "GNOSIA"), h));
        // «【1080P】GNOSIA»: «【» — не «[», ведущая группа не срезается; первый маркер — «1080P», голова «【» → пусто → отказ.
        // Зафиксировано как есть: цена — потеря такого релиза, а не ложный донор.
        Assert.False(HunterAccess.NameMatchesSeriesOrId(Bm("【1080P】GNOSIA.mkv", Hex('2'), idTitle: "GNOSIA"), h));
        // 🔴 гард на null: голова из CJK без эталона имени НЕ должна совпасть с originalNorm == null
        Assert.False(HunterAccess.NameMatchesSeriesOrId(Bm("【剧集】.S01E01.mkv", Hex('3'), idTitle: null), h));
    }

    [Theory]
    [InlineData("Silo.S03E10.1080p.ColdFilm.mkv", "Silo.")]
    [InlineData("[AniDub]_Gnosia.s01", "Gnosia.")]
    [InlineData("[ Torrent911.lol ] Dune.Prophecy.S01", "")]
    public void TitleHeadBeforeMarker_Cases(string title, string expected)
        => Assert.Equal(expected, HunterAccess.TitleHeadBeforeMarker(title));

    // ── B. порядок проб: ранг качества → живые → бакет байт/серия → score ──
    [Fact]
    public void OrderByCover_RankThenLiveThenBucketThenScore()
    {
        // ровно расклад инцидента: XviD-пак с лучшим score раньше шёл первым
        var xvid = Cand(RuDubPack, 7, quality: 0, score: 122, sizeBytes: 7_000_000_000);
        var cold720 = Bm("Silo.S03E10.720p.ColdFilm.mkv", Hex('1'), quality: 720, sid: 5, score: 105, sizeBytes: 1_475_343_428, eps: new[] { 10 }, season: 3);
        var cold1080 = Bm(ColdFilm1080, ColdFilmHash, quality: 1080, sid: 2, score: 107, sizeBytes: 2_352_351_365, eps: new[] { 10 }, season: 3);
        var psa = Bm("Silo.S03E10.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA.mkv", Hex('2'), quality: 1080, sid: 71, score: 78, sizeBytes: 732_477_250, eps: new[] { 10 }, season: 3);
        var webdl = Bm("Silo.S03E10.1080p.ATVP.WEB-DL.DDP5.1.H.264-NTb.mkv", Hex('3'), quality: 1080, sid: 14, score: 60, sizeBytes: 4_600_000_000, eps: new[] { 10 }, season: 3);

        var res = HunterAccess.OrderByCover(new List<JObject> { xvid, cold720, cold1080, psa, webdl }, 3, new List<int> { 10 }, 1080);
        Assert.Equal(
            new[] { webdl, cold1080, psa, cold720, xvid }.Select(t => t.Value<string>("title")).ToArray(),   // ранг 0: бакеты 2,1,0; затем ранг 460; затем 1000
            res.Select(t => t.Value<string>("title")).ToArray());

        // живые вперёд: равный ранг и бакет, 0/0 против 20 сидов — второй раньше, несмотря на score
        var dead = Bm("Silo.S03E10.1080p.WEB-DL.A.mkv", Hex('4'), sid: 0, pir: 0, score: 90, eps: new[] { 10 }, season: 3);
        var live = Bm("Silo.S03E10.1080p.WEB-DL.B.mkv", Hex('5'), sid: 20, score: 50, eps: new[] { 10 }, season: 3);
        var res2 = HunterAccess.OrderByCover(new List<JObject> { dead, live }, 3, new List<int> { 10 }, 1080);
        Assert.Same(live, res2[0]);
        Assert.Same(dead, res2[1]);
    }

    // ── C. вердикт покрытия по элементу: episodes классификатора сильнее имени ──
    [Fact]
    public void TitleCoversEpItem_ByClassifier()
    {
        var one = Bm(ColdFilm1080, Hex('1'), eps: new[] { 10 }, season: 3);
        Assert.Equal(DonorCover.Yes, HunterAccess.TitleCoversEpItem(one, 3, 10));
        Assert.Equal(DonorCover.No, HunterAccess.TitleCoversEpItem(one, 3, 9));

        // пак {"3":{}} с files_count ≥ 2 — вслепую, проверит FindEpFiles
        var pack = Bm("Silo.S03.1080p.WEB.H264-ETHEL", Hex('2'), eps: new int[0], season: 3, pack: true);
        pack["files_count"] = 10;
        Assert.Equal(DonorCover.Maybe, HunterAccess.TitleCoversEpItem(pack, 3, 10));

        // ошибка парсера («Silo.S02S10…» → {"2":{}} при одном файле): не пак → строковый вердикт, не исключение
        var broken = Bm("Silo.S02S10.1080p.WEB.H264-kovalski.mkv", Hex('3'), eps: new int[0], pack: false);
        broken["files_count"] = 1;
        var v = HunterAccess.TitleCoversEpItem(broken, 3, 10);
        Assert.True(Enum.IsDefined(typeof(DonorCover), v));
        Assert.Equal(HunterAccess.TitleCoversEp(broken.Value<string>("title"), 3, 10), v);
        var brokenS2 = Bm("Silo.S02S10.1080p.WEB.H264-kovalski.mkv", Hex('3'), eps: new int[0], season: 2, pack: false);
        Assert.Equal(DonorCover.No, HunterAccess.TitleCoversEpItem(brokenS2, 3, 10));   // сезон классификатора 2 ≠ 3

        // мультисерийный {"1":{"1":…"10":{}}} при ОДНОМ файле — episodes на весь сезон врут, решает имя
        var all10 = Enumerable.Range(1, 10).ToArray();
        var wrong = Bm("House.of.the.Dragon.S01E01.1080p.WEB.h264-ETHEL.mkv", Hex('4'), eps: all10, season: 1, idTitle: "House of the Dragon");
        wrong["files_count"] = 1;
        Assert.Equal(DonorCover.No, HunterAccess.TitleCoversEpItem(wrong, 1, 10));
        var real = Bm("House.of.the.Dragon.S01.1080p.WEB.h264-ETHEL", Hex('5'), eps: all10, season: 1, idTitle: "House of the Dragon");
        real["files_count"] = 10;
        Assert.Equal(DonorCover.Yes, HunterAccess.TitleCoversEpItem(real, 1, 10));

        // многосезонный пак — Maybe (серии внутри сезона посчитает FindEpFiles)
        var multi = Bm("Silo.S01-S03.Complete.1080p.WEB-DL", Hex('6'), pack: true);
        multi["bm_multi"] = true;
        Assert.Equal(DonorCover.Maybe, HunterAccess.TitleCoversEpItem(multi, 3, 10));

        // чужой сезон по классификатору — No сразу
        Assert.Equal(DonorCover.No, HunterAccess.TitleCoversEpItem(Bm("Silo.S02E10.1080p.mkv", Hex('7'), eps: new[] { 10 }, season: 2), 3, 10));
    }

    // ── D. одиночный фолбэк FindEpFiles: сезон охоты вслепую не подставляем ──
    [Fact]
    public void FindEpFiles_SingleFallback_FailClosedAboveSeason1()
    {
        var files = new JArray(Vid(0, "Show.mkv"));
        var wanted = new List<int> { 9 };
        const string title = "Сериал (Серия 9) 1080p";   // сезона в названии нет

        // не первый сезон, сезон донора неизвестен — не берём (раньше «s <= 0 → s = season» делал проверку тождеством)
        Assert.Empty(HunterAccess.FindEpFiles(files, 3, wanted, title, 0));
        // сезон донора подтверждён — берём и проставляем его
        var got = HunterAccess.FindEpFiles(files, 3, wanted, title, 3);
        Assert.Single(got);
        Assert.Equal(3, got[0].season);
        Assert.Equal("s3e9", got[0].epkey);
        // первый сезон — как раньше (аниме/односезонники без маркеров)
        Assert.Single(HunterAccess.FindEpFiles(files, 1, wanted, title, 0));
    }

    // ── E. замещение и показ по рангу качества, а не по score ────────────
    [Fact]
    public void PlanReplacements_UpgradeDecidedByRank_NotScore()
    {
        var xvidDonor = Donor(Hex('a'), 122, 0, 3, 5);      // XviD-пак: score выше, качество не распознано (ранг 1000)
        var coldDonor = Donor(Hex('b'), 107, 1080, 3, 5);   // 1080p-одиночка: score ниже, ранг 0
        var item = new JObject { ["hash"] = MainHash, ["donors"] = new JArray(xvidDonor, coldDonor) };
        var main = new JArray(Vid(0, $"{MainName}/Silo.S03E01.1080p.WEB-DL.mkv", 1));

        var files = new Dictionary<string, JArray>
        {
            [Hex('a')] = new JArray(Vid(5, "Silo.S03/Silo.s03e05.WEBRip.XviD.avi", 1, 700_000_000)),
            [Hex('b')] = new JArray(Vid(5, "Silo.S03E05.1080p.ColdFilm.mkv", 0.4, 2_100_000_000))
        };
        // лучший по рангу ещё качается — защищён; худший докачанный не трогаем, зритель без серии не остаётся
        Assert.DoesNotContain(HunterAccess.PlanReplacements(main, item, files, Now, 7), a => a.kind == "upgraded");

        files[Hex('b')] = new JArray(Vid(5, "Silo.S03E05.1080p.ColdFilm.mkv", 1, 2_100_000_000));
        var acts = HunterAccess.PlanReplacements(main, item, files, Now, 7);
        Assert.Contains(acts, a => a.kind == "upgraded" && a.donorHash == Hex('a') && a.fileIndex == 5);   // ранг 1000 проигрывает 0 при score 122 > 107
        Assert.DoesNotContain(acts, a => a.kind == "upgraded" && a.donorHash == Hex('b'));
    }

    [Fact]
    public void MergeEpisodeFiles_TwoDoneCopies_ShowsBetterRank()
    {
        var xvidDonor = Donor(Hex('a'), 122, 0, 3, 5);
        var coldDonor = Donor(Hex('b'), 107, 1080, 3, 5);
        var xvidFiles = new JArray(Vid(5, "Silo.S03/Silo.s03e05.WEBRip.XviD.avi", 1, 700_000_000));
        var coldFiles = new JArray(Vid(5, "Silo.S03E05.1080p.ColdFilm.mkv", 1, 2_100_000_000));
        var main = new JArray(Vid(0, $"{MainName}/Silo.S03E01.1080p.WEB-DL.mkv", 1));

        // порядок доноров не должен влиять: показывается копия с лучшим рангом, не с большим score
        foreach (var order in new[]
        {
            new List<(JObject, JArray)> { (xvidDonor, xvidFiles), (coldDonor, coldFiles) },
            new List<(JObject, JArray)> { (coldDonor, coldFiles), (xvidDonor, xvidFiles) }
        })
        {
            var merged = HunterAccess.MergeEpisodeFiles(MainHash, main, order, "t125988", 3);
            var e5 = merged.Where(x => x.Value<int?>("episode") == 5).ToList();
            Assert.Single(e5);
            Assert.Equal(Hex('b'), e5[0].Value<string>("hash"));
            Assert.Equal("donor", e5[0].Value<string>("source"));
            Assert.Null(e5[0]["dquality"]);   // служебные поля наружу не отдаём
        }
    }

    // ── F. оффлайн-реплей инцидента на боевых фикстурах Media-server ─────
    static string FixtureDir()
    {
        var probe = new List<string>();
        string env = Environment.GetEnvironmentVariable("MEDIA_SERVER_ROOT");
        if (!string.IsNullOrEmpty(env)) probe.Add(Path.Combine(env, "tests", "fixtures", "hunter"));
        // bin/Debug/net10.0 → Tests/QbitDownload.Tests → E:\lampac → соседний репозиторий оркестрации
        probe.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Media-server", "tests", "fixtures", "hunter"));
        probe.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "Media-server", "tests", "fixtures", "hunter"));
        probe.Add(@"E:\Media-server\tests\fixtures\hunter");
        foreach (string p in probe) if (Directory.Exists(p)) return Path.GetFullPath(p);
        throw new DirectoryNotFoundException("фикстуры охоты не найдены (Media-server/tests/fixtures/hunter; MEDIA_SERVER_ROOT)");
    }

    static JArray Fixture(string name) => JArray.Parse(File.ReadAllText(Path.Combine(FixtureDir(), name)));

    // postgres отдаёт «2026-08-28 09:52:40.631765+03» — смещение без минут .NET не читает
    static DateTime? PgDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s.Trim(), @"([+-]\d{2})$", "$1:00");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.UtcDateTime : null;
    }

    // сырая строка скоупа (как из БД, см. README фикстур) → BitmagnetRow; seeders = greatest(tc, dht), как в SQL
    static QbitController.BitmagnetRow Row(JObject r) => new QbitController.BitmagnetRow
    {
        name = r.Value<string>("name"), btih = r.Value<string>("btih"), size = r.Value<long?>("size") ?? 0,
        seeders = Math.Max(r.Value<int?>("seeders_tc") ?? 0, r.Value<int?>("seeders_dht") ?? 0),
        leechers = r.Value<int?>("leechers") ?? 0,
        res = r.Value<string>("video_resolution"), codec = r.Value<string>("video_codec"),
        source = r.Value<string>("video_source"), modifier = r.Value<string>("video_modifier"),
        episodesJson = r["episodes"] is JObject eo ? eo.ToString(Formatting.None) : r.Value<string>("episodes"),
        filesCount = r.Value<int?>("files_count") ?? 0, filesStatus = r.Value<string>("files_status"),
        published = PgDate(r.Value<string>("published_at")), created = PgDate(r.Value<string>("created_at")), updated = PgDate(r.Value<string>("updated_at")),
        langRu = r.Value<bool?>("lang_ru") ?? false,
        contentTitle = r.Value<string>("content_title"), contentOriginal = r.Value<string>("content_original_title"),
        files = null   // hunt-bitmagnet-scoped-125988-s3-files.json пуст: все 356 строк скоупа single
    };

    // выдача «как в SearchScored»: трекеры + скоуп bitmagnet, дедуп по btih/parselink, затем скоринг (он мутирует)
    static JArray Scored(JArray huntInput, JArray scope)
    {
        var items = new JArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(JObject t)
        {
            string mag = t.Value<string>("magnet");
            string key = !string.IsNullOrWhiteSpace(mag) ? Access.MagnetHash(mag) : t.Value<string>("parselink");
            if (!string.IsNullOrEmpty(key) && !seen.Add(key)) return;
            items.Add(t);
        }
        foreach (var t in huntInput.OfType<JObject>()) Add((JObject)t.DeepClone());
        if (scope != null)
            foreach (var r in scope.OfType<JObject>()) Add(QbitController.BitmagnetItem(Row(r), 3));

        var ctx = new ScoreCtx
        {
            titleNorm = SearchNameTo.Convert("Укрытие"), originalNorm = SearchNameTo.Convert("Silo"),
            year = 2023, isSerial = true, wantSeason = 3, preferredQuality = 2160
        };
        return TorrentScoring.SortAndMark(items, ctx, 5);
    }

    static JObject MainRecord() => new JObject
    {
        ["hash"] = MainHash,
        ["link"] = "http://127.0.0.1:9118/rutracker/parsemagnet?id=6878482&apikey=1",
        ["id"] = 125988, ["title"] = "Укрытие",
        ["ctx"] = new JObject { ["title"] = "Укрытие", ["title_original"] = "Silo", ["year"] = 2023, ["is_serial"] = 2, ["season"] = 3 }
    };

    static JArray MainFiles() => new JArray(Enumerable.Range(1, 9)
        .Select(n => Vid(n - 1, $"{MainName}/Silo.S03E0{n}.1080p.WEB-DL.RGzsRutracker.mkv", 1, 4_270_000_000L + n)));

    static object Plan(JArray scored, ModuleConf conf, JArray donors = null)
        => HunterAccess.BuildHuntPlan(MainRecord(), MainFiles(), MainName, donors, null, scored, new[] { MainHash },
                                      "Укрытие", "Silo", 3, 10, DateTime.UtcNow, conf, false);

    static JObject Find(JArray scored, Func<string, bool> byTitle)
        => scored.OfType<JObject>().FirstOrDefault(t => byTitle(t.Value<string>("title") ?? ""));

    [Fact]
    public void Replay_NewPolicy_ColdFilm1080First_NoForeignNoUnknown()
    {
        var scored = Scored(Fixture("hunt-input-tmdb.json"), Fixture("hunt-bitmagnet-scoped-125988-s3.json"));
        var plan = Plan(scored, new ModuleConf());   // дефолты 2.107: minQuality 720, rejectUnknownQuality, requireRussian
        var h = HunterAccess.PlanField<object>(plan, "h");

        Assert.True(HunterAccess.PlanField<int>(plan, "bitmagnet") > 0);
        Assert.Equal(new List<int> { 10 }, HunterAccess.PlanField<List<int>>(plan, "wanted"));

        var probes = HunterAccess.PlanField<List<JObject>>(plan, "probes");
        Assert.NotEmpty(probes);
        Assert.Equal(ColdFilm1080, probes[0].Value<string>("title"));
        Assert.All(probes, p => Assert.True(TorrentScoring.IsRussian(p.Value<string>("title"), p.Value<bool?>("lang_ru")), "не русская проба: " + p.Value<string>("title")));

        // XviD-пак инцидента: русский, сидов хватает, но качество не распознано → ниже порога
        var xvid = Find(scored, t => t.Contains("RuDub"));
        Assert.NotNull(xvid);
        Assert.Equal("качество не распознано", HunterAccess.DropReason(xvid, h));

        // английские одиночки той же серии — «язык», а не «сиды»/«качество»
        var psa = Find(scored, t => t.StartsWith("Silo.S03E10.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA", StringComparison.Ordinal));
        Assert.NotNull(psa);
        Assert.Equal("язык", HunterAccess.DropReason(psa, h));
        var troy = Find(scored, t => t.Contains("S03E10.Troy.2160p"));
        Assert.NotNull(troy);
        Assert.Equal("язык", HunterAccess.DropReason(troy, h));

        // мультисезонный пак — только Maybe (сквозной счётчик серий ничего не доказывает про E10 внутри S3)
        var multi = Find(scored, t => t.Contains("1-27 серии из 30"));
        if (multi != null) Assert.Equal(DonorCover.Maybe, HunterAccess.TitleCoversEpItem(multi, 3, 10));

        var drops = HunterAccess.PlanField<Dictionary<string, int>>(plan, "drops");
        Assert.True(drops.ContainsKey("язык"));
        Assert.True(drops.ContainsKey("качество не распознано"));
    }

    [Fact]
    public void Replay_OldPolicy_ReproducesIncident_RuDubFirst()
    {
        // как до 2.107: без bitmagnet-скоупа (выдача без tmdb_id), неизвестное качество проходит, язык не гейтится
        var conf = new ModuleConf { donorRejectUnknownQuality = false, donorRequireRussian = false, donorMinQuality = 1080, huntBitmagnet = false };
        var scored = Scored(Fixture("hunt-input-no-tmdb.json"), null);
        var plan = Plan(scored, conf);

        Assert.Equal(new List<int> { 10 }, HunterAccess.PlanField<List<int>>(plan, "wanted"));
        var probes = HunterAccess.PlanField<List<JObject>>(plan, "probes");
        Assert.NotEmpty(probes);
        Assert.Contains("RuDub", probes[0].Value<string>("title"));
    }

    [Fact]
    public void Replay_AfterManualFix_OwnDonorIsKnown_NoRegrab()
    {
        var scored = Scored(Fixture("hunt-input-tmdb.json"), Fixture("hunt-bitmagnet-scoped-125988-s3.json"));
        var donors = new JArray(new JObject
        {
            ["hash"] = ColdFilmHash, ["link"] = Magnet(ColdFilmHash), ["title"] = ColdFilm1080, ["tracker"] = "bitmagnet",
            ["sid"] = 35, ["score"] = 107.1, ["quality"] = 1080,
            ["eps"] = new JArray(new JObject { ["epkey"] = "s3e10", ["season"] = 3, ["ep"] = 10, ["fileIndex"] = 0, ["status"] = "hunted" })
        });
        var plan = Plan(scored, new ModuleConf(), donors);
        var h = HunterAccess.PlanField<object>(plan, "h");

        Assert.Empty(HunterAccess.PlanField<List<int>>(plan, "wanted"));
        Assert.Empty(HunterAccess.PlanField<List<int>>(plan, "upgrades"));
        Assert.Equal(0, HunterAccess.PlanField<int>(plan, "selfClaim"));   // собственный донор не взводит re-grab

        var own = scored.OfType<JObject>().FirstOrDefault(t => ColdFilmHash.Equals(Access.MagnetHash(t.Value<string>("magnet")), StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(own);
        Assert.Equal("уже есть", HunterAccess.DropReason(own, h));
    }

    // ── G. локальный тик: есть ли чего ждать ─────────────────────────────
    [Fact]
    public void LocalTickWaiting_ByLocalWanted_ThenAiredCache()
    {
        var mainFiles = MainFiles();
        // без hunt — по кешу эфира TMDB; кеша нет → ждать нечего (ни одного SELECT)
        var fresh = new JObject { ["hash"] = MainHash, ["id"] = 999001 };
        Assert.False(HunterAccess.LocalTickWaiting(fresh, mainFiles, null, 3));

        var waiting = new JObject { ["hash"] = MainHash, ["id"] = 999001, ["hunt"] = new JObject { ["localWanted"] = new JArray(10) } };
        Assert.True(HunterAccess.LocalTickWaiting(waiting, mainFiles, null, 3));

        var closed = new JObject { ["hash"] = MainHash, ["id"] = 999001, ["hunt"] = new JObject { ["localWanted"] = new JArray() } };
        Assert.False(HunterAccess.LocalTickWaiting(closed, mainFiles, null, 3));

        // SetLocalWanted пишет ровно этот список
        var m = new JObject { ["hash"] = MainHash };
        HunterAccess.SetLocalWanted(m, new List<int> { 10 });
        Assert.True(HunterAccess.LocalTickWaiting(m, mainFiles, null, 3));
        HunterAccess.SetLocalWanted(m, new List<int>());
        Assert.False(HunterAccess.LocalTickWaiting(m, mainFiles, null, 3));
    }

    // ── H. текстовые сторожа исходника ──────────────────────────────────
    static string ModuleFile(string name)
    {
        string[] probe =
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Modules", "QbitDownload", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Modules", "QbitDownload", name)
        };
        foreach (string p in probe) if (File.Exists(p)) return p;
        throw new FileNotFoundException("не найден " + name);
    }

    static string Strip(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join("\n", src.Split('\n')
            .Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l.Substring(0, i) : l; }));
    }

    static string Between(string src, string start, string end)
    {
        int i = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, "в исходнике не найдено: " + start);
        int j = src.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        Assert.True(j > i, "в исходнике не найден конец: " + end);
        return src.Substring(i, j - i);
    }

    [Fact]
    public void Source_HuntPrepare_SearchesWithTmdbAndSeasonScope()
    {
        var src = Strip(File.ReadAllText(ModuleFile("EpisodeHunter.cs")));
        string prep = Between(src, "static async Task<HuntPrep> HuntPrepare(", "static async Task<HuntOneResult> HuntOne(");
        // полный проход: tmdb_id восьмым, сезонный скоуп bitmagnet девятым — только при tmdb_id
        Assert.Contains("SearchScored(ctitle, ctitle, titleOriginal, year, 2, season, null, tmdbId, tmdbId != null ? season : 0)", prep);
    }

    [Fact]
    public void Source_ConsiderSwitch_SearchesWithoutTmdb()
    {
        var src = Strip(File.ReadAllText(ModuleFile("EpisodeHunter.cs")));
        string cs = Between(src, "static async Task ConsiderSwitch(JObject m)", "#endregion");
        int i = cs.IndexOf("SearchScored(", StringComparison.Ordinal);
        Assert.True(i >= 0, "в ConsiderSwitch нет вызова SearchScored");
        int j = cs.IndexOf(");", i, StringComparison.Ordinal);
        string call = Regex.Replace(cs.Substring(i, j - i + 2), @"\s+", " ");
        // переключение основной — пользовательская выдача: без tmdb_id и без сезонного скоупа (кеш/индекс пишутся)
        Assert.StartsWith("SearchScored(ctitle, ctitle, ctx?.Value<string>(\"title_original\"),", call, StringComparison.Ordinal);
        Assert.EndsWith("2, season, null);", call, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb", call, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_SearchScored_ScopeWritesNeitherCacheNorIndex()
    {
        var src = Strip(File.ReadAllText(ModuleFile("Controller.cs")));
        string ss = Between(src, "static async Task<JArray> SearchScored(", "static JArray ScoreResult(");
        int gate = ss.IndexOf("bool userPath = bmScopeSeason <= 0;", StringComparison.Ordinal);
        int write = ss.IndexOf("SearchCache.Write(", StringComparison.Ordinal);
        int store = ss.IndexOf("store: userPath", StringComparison.Ordinal);
        Assert.True(gate >= 0, "нет гейта userPath");
        Assert.True(write > gate, "SearchCache.Write стоит не под гейтом userPath");
        Assert.True(store > write, "store: userPath стоит не под гейтом userPath");
        Assert.Single(Regex.Matches(ss, Regex.Escape("SearchCache.Write(")));   // единственная запись кеша — под гейтом
    }

    [Fact]
    public void Source_LocalFetch_NeverCallsTrackers()
    {
        var src = Strip(File.ReadAllText(ModuleFile("EpisodeHunter.cs")));
        string lf = Between(src, "LocalFetch(string ctitle, int year, int season, string tmdbId)", "public static async Task HuntLocalTick()");
        Assert.DoesNotContain("SearchScored(", lf);
        Assert.Contains("FetchBitmagnet(", lf);
    }

    [Fact]
    public void Source_NoEpisodeBlacklist_KeyIsBtihOnly()
    {
        var src = Strip(File.ReadAllText(ModuleFile("EpisodeHunter.cs")));
        // у обновляемого топика parselink стабилен — бан по нему выключал лучший пак на 30 дней
        Assert.Contains("BlacklistAdd(m, btih, null, \"no-episode\"", src);
        Assert.DoesNotContain("BlacklistAdd(m, btih, parselink, \"no-episode\"", src);
    }

    // ── I. сухой прогон: только чтение, ни одной записи ──────────────────
    [Fact]
    public async Task HuntDry_LocalOnly_ReadsOnly_NoWrites()
    {
        string dir = TestEnv.FreshCache();
        TestEnv.SetListen(1, "127.0.0.1");
        var conf = ModInit.conf;
        string prevBm = conf.bitmagnetConnection, prevLi = conf.localIndexConnection;
        bool prevAired = conf.tmdbAiredCap;
        conf.bitmagnetConnection = "";      // локальные базы недоступны — выборка пустая, SELECT нет
        conf.localIndexConnection = "";
        conf.tmdbAiredCap = false;          // без похода за эфиром TMDB: тест герметичен

        var rec = MainRecord();
        rec["hunt"] = new JObject { ["localWanted"] = new JArray(10) };
        HunterAccess.SaveWatch(new JArray(rec));
        string watchPath = Path.Combine(dir, "watch.json");
        byte[] before = File.ReadAllBytes(watchPath);

        var fake = new FakeQbit()
            .Json("/torrents/files?hash=" + MainHash, MainFiles().ToString(Formatting.None))
            .Json("/torrents/info", new JArray(new JObject { ["hash"] = MainHash, ["name"] = MainName, ["category"] = "lampa" }).ToString(Formatting.None));
        Access.SeedQbitFake(fake.BuildHandler());
        try
        {
            var items = await HunterAccess.HuntDry(MainHash, true);

            var rep = items.OfType<JObject>().FirstOrDefault(x => MainHash.Equals(x.Value<string>("hash"), StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(rep);
            Assert.Null(rep.Value<string>("error"));
            Assert.Equal(3, rep.Value<int>("season"));
            Assert.True(rep.Value<bool>("waiting"));                  // localWanted [10] — есть чего ждать
            Assert.NotNull(rep["wanted"]);                            // план построен и при пустой выборке…
            Assert.Empty(rep["wanted"] as JArray);                    // …но заявлять серии некому — wanted пуст, проб нет
            Assert.Empty(rep["wouldProbe"] as JArray);

            // qBit только читали: ни add/delete/filePrio/start
            Assert.NotEmpty(fake.Requests);
            Assert.All(fake.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
            Assert.DoesNotContain(fake.Requests, r => Regex.IsMatch(r.RequestUri?.ToString() ?? "", "/torrents/(add|delete|filePrio|start|resume|pause)"));
            Assert.Contains(fake.Requests, r => (r.RequestUri?.ToString() ?? "").Contains("/torrents/files?hash=" + MainHash));

            // watch.json не тронут байт в байт
            Assert.Equal(before, File.ReadAllBytes(watchPath));
        }
        finally
        {
            Access.ResetQbitFake();
            conf.bitmagnetConnection = prevBm; conf.localIndexConnection = prevLi; conf.tmdbAiredCap = prevAired;
        }
    }
}
