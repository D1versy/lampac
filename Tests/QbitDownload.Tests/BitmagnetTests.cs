using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Bitmagnet — источник раздач из локального индекса DHT-краулера (qdl 2.107). Всё без Postgres:
/// чистый маппинг строки выборки в элемент выдачи (BitmagnetItem), сторожа текста SQL
/// (строго по TMDB id, никакого поиска по имени — §AZ) и пост-фильтр /qdl/search, который
/// сворачивает иностранные поштучные серии, когда в выдаче есть русские (HideForeignSingles).
/// </summary>
public class BitmagnetTests
{
    const string Btih = "521fff292d6a3e1f636f3445aabd22f0a1bee9dc";
    const string ColdFilm = "Silo.S03E10.1080p.ColdFilm.mkv";

    public BitmagnetTests() => TestEnv.EnsureConf();

    // строка выборки «как из БД»: по умолчанию single-торрент без разрешения/кодека/источника, привязка к «Silo»
    static QbitController.BitmagnetRow Row(string name, string episodes = null, int filesCount = 1, string res = null, string codec = null,
                                           string source = null, string modifier = null, string filesStatus = "single",
                                           List<(string name, long size)> files = null, bool langRu = false,
                                           int seeders = 2, int leechers = 0, long size = 2352351365)
        => new QbitController.BitmagnetRow
        {
            name = name, btih = Btih, size = size, seeders = seeders, leechers = leechers,
            res = res, codec = codec, source = source, modifier = modifier,
            episodesJson = episodes, filesCount = filesCount, filesStatus = filesStatus, langRu = langRu,
            contentTitle = "Silo", contentOriginal = "Silo", files = files
        };

    static int[] Eps(JObject it) => it["bm_eps"] is JArray a ? a.Values<int>().ToArray() : null;

    // элемент выдачи /qdl/search, как он приходит в HideForeignSingles (bm_* только у строк bitmagnet)
    static JObject Tor(string title, string tracker, int[] eps = null, bool? pack = null, int? season = null,
                       bool? langRu = null, string filesStatus = null, int? filesCount = null)
    {
        var t = new JObject { ["title"] = title, ["tracker"] = tracker, ["sid"] = 5, ["pir"] = 1 };
        if (eps != null) t["bm_eps"] = new JArray(eps);
        if (pack != null) t["bm_pack"] = pack.Value;
        if (season != null) t["bm_season"] = season.Value;
        if (langRu != null) t["lang_ru"] = langRu.Value;
        if (filesStatus != null) t["files_status"] = filesStatus;
        if (filesCount != null) t["files_count"] = filesCount.Value;
        return t;
    }

    static string[] Titles(JArray a) => a.Select(x => x.Value<string>("title")).ToArray();

    // ── BitmagnetItem: маппинг строки в элемент выдачи ────────────────────
    [Fact]
    public void BitmagnetItem_ColdFilmE10_ПолныйМаппинг()
    {
        // реальная строка из базы: русская одиночка S03E10 «Укрытия», в БД стояло 2 сида (реально 35 — потому sid_hint)
        var r = Row(ColdFilm, episodes: "{\"3\": {\"10\": {}}}", res: "V1080p",
                    files: new List<(string, long)> { (ColdFilm, 2352351365) });
        var it = QbitController.BitmagnetItem(r, 3);

        Assert.Equal(ColdFilm, it.Value<string>("title"));
        Assert.Equal(1080, it.Value<int>("quality"));
        Assert.Equal(2, it.Value<int>("sid"));
        Assert.Equal(0, it.Value<int>("pir"));
        Assert.True(it.Value<bool>("sid_hint"));
        Assert.True(it.Value<bool>("id_match"));
        Assert.Equal("bitmagnet", it.Value<string>("tracker"));
        Assert.Null(it.Value<string>("parselink"));                       // трекера нет — качаем по DHT
        Assert.Contains("urn:btih:" + Btih, it.Value<string>("magnet"));
        Assert.StartsWith("magnet:?xt=", it.Value<string>("magnet"));
        Assert.Equal(2352351365L, it.Value<long>("sizeBytes"));
        Assert.EndsWith(" GB", it.Value<string>("size"));                 // разделитель дроби зависит от культуры — не сверяем
        Assert.Equal(3, it.Value<int>("bm_season"));
        Assert.Equal(new[] { 10 }, Eps(it));
        Assert.False(it.Value<bool>("bm_pack"));
        Assert.Equal("Silo", it.Value<string>("id_title"));
        Assert.Equal("Silo", it.Value<string>("id_title_original"));
        Assert.Single((JArray)it["bm_files"]);
        Assert.Equal(ColdFilm, it["bm_files"][0].Value<string>("name"));
        Assert.Equal(2352351365L, it["bm_files"][0].Value<long>("size"));
        Assert.Null(it["bm_legacy"]);                                     // флаги пишутся только когда истинны
        Assert.Null(it["bm_screener"]);
        Assert.False(it.Value<bool>("lang_ru"));
        Assert.Equal(1, it.Value<int>("files_count"));
        Assert.Equal("single", it.Value<string>("files_status"));
    }

    [Fact]
    public void Пак_сезона_без_разбивки_bm_pack_только_при_files_count_от_двух()
    {
        var pack = QbitController.BitmagnetItem(Row("Silo.S02.1080p.WEB.H264-ETHEL", episodes: "{\"2\": {}}", filesCount: 41, filesStatus: "multi"), 2);
        Assert.Equal(2, pack.Value<int>("bm_season"));
        Assert.Empty(Eps(pack));
        Assert.True(pack.Value<bool>("bm_pack"));

        // ошибка парсера краулера: «Silo.S02S10…» → {"2":{}} при ОДНОМ файле — это не пак и не серия
        var broken = QbitController.BitmagnetItem(Row("Silo.S02S10.1080p.WEB.mkv", episodes: "{\"2\": {}}", filesCount: 1), 2);
        Assert.Equal(2, broken.Value<int>("bm_season"));
        Assert.Empty(Eps(broken));
        Assert.False(broken.Value<bool>("bm_pack"));
    }

    [Fact]
    public void Мультисерийный_bm_eps_отсортирован()
    {
        // ключи jsonb приходят в произвольном порядке — на выходе список по возрастанию
        var it = QbitController.BitmagnetItem(Row("Show.S01.1080p.WEB", episodes: "{\"1\": {\"10\": {}, \"1\": {}, \"2\": {}}}", filesCount: 3, filesStatus: "multi"), 1);
        Assert.Equal(1, it.Value<int>("bm_season"));
        Assert.Equal(new[] { 1, 2, 10 }, Eps(it));
        Assert.False(it.Value<bool>("bm_pack"));
    }

    [Fact]
    public void Мультисезонный_пак_bm_multi_без_bm_eps()
    {
        var it = QbitController.BitmagnetItem(Row("Silo.S01-S03.1080p.WEB", episodes: "{\"1\": {}, \"2\": {}, \"3\": {}}", filesCount: 30, filesStatus: "multi"), 0);
        Assert.True(it.Value<bool>("bm_multi"));
        Assert.True(it.Value<bool>("bm_pack"));
        Assert.Null(it["bm_eps"]);      // серии внутри сезона считает FindEpFiles после метаданных
        Assert.Null(it["bm_season"]);
    }

    [Fact]
    public void Мультисезонный_в_сезонном_скоупе_берёт_свой_сезон()
    {
        // охота за 3-м сезоном: из трёх ключей выбирается запрошенный, а не «многосезонный пак»
        var it = QbitController.BitmagnetItem(Row("Silo.S01-S03.1080p.WEB", episodes: "{\"1\": {}, \"2\": {}, \"3\": {\"10\": {}}}", filesCount: 30, filesStatus: "multi"), 3);
        Assert.Equal(3, it.Value<int>("bm_season"));
        Assert.Equal(new[] { 10 }, Eps(it));
        Assert.Null(it["bm_multi"]);
    }

    [Fact]
    public void Без_скоупа_один_сезон_в_episodes_всё_равно_даёт_bm_season_и_bm_eps()
    {
        // интерактив (/qdl/search, scopeSeason 0): bm_eps нужен HideForeignSingles, чтобы отличить одиночку от пака
        var it = QbitController.BitmagnetItem(Row(ColdFilm, episodes: "{\"3\": {\"10\": {}}}"), 0);
        Assert.Equal(3, it.Value<int>("bm_season"));
        Assert.Equal(new[] { 10 }, Eps(it));
        Assert.False(it.Value<bool>("bm_pack"));
    }

    [Fact]
    public void Без_episodes_нет_ни_bm_season_ни_bm_pack()
    {
        var it = QbitController.BitmagnetItem(Row("Movie.2024.1080p.WEB-DL.mkv"), 0);
        Assert.Null(it["bm_season"]);
        Assert.Null(it["bm_eps"]);
        Assert.Null(it["bm_pack"]);
        Assert.Null(it["bm_multi"]);
        Assert.Null(it["bm_files"]);    // файлы не запрашивали (интерактив) → поля нет, «гейт файлов молчит»
    }

    // ── legacy-кодек: XviD/DivX/MPEG — старьё, которому без разрешения ставим 480 ──
    [Theory]
    [InlineData("XviD")]
    [InlineData("DivX")]
    [InlineData("MPEG4")]
    [InlineData("MPEG2")]
    [InlineData("mpeg-4")]
    public void Legacy_кодек_из_БД(string codec)
    {
        var it = QbitController.BitmagnetItem(Row("Show.S01E01.HDTV-LOL.avi", codec: codec), 0);
        Assert.True(it.Value<bool>("bm_legacy"));
    }

    [Fact]
    public void Legacy_без_разрешения_качество_480()
    {
        // res null, в имени высоты нет → QualityFromTitle даёт 0 → legacy подставляет 480
        var it = QbitController.BitmagnetItem(Row("Show.S01E01.HDTV-LOL.avi", codec: "XviD"), 0);
        Assert.True(it.Value<bool>("bm_legacy"));
        Assert.Equal(480, it.Value<int>("quality"));
    }

    [Fact]
    public void X265_не_legacy_кодек_нормализуется_в_hevc()
    {
        var it = QbitController.BitmagnetItem(Row("Show.S01E01.1080p.WEB-GRP.mkv", codec: "x265"), 0);
        Assert.Null(it["bm_legacy"]);
        Assert.Equal("hevc", it.Value<string>("codec"));
        Assert.Equal(1080, it.Value<int>("quality"));
    }

    [Fact]
    public void Legacy_по_имени_при_пустом_codec()
    {
        var it = QbitController.BitmagnetItem(Row("Show.S01E01.HDTV.XviD-LOL.avi"), 0);
        Assert.True(it.Value<bool>("bm_legacy"));
        Assert.Equal(480, it.Value<int>("quality"));
    }

    // ── экранка: CAM/TS/TC/WORKPRINT и модификатор SCREENER ──────────────
    [Theory]
    [InlineData("CAM")]
    [InlineData("TELESYNC")]
    [InlineData("TELECINE")]
    [InlineData("WORKPRINT")]
    [InlineData("cam")]      // HashSet без учёта регистра
    public void Screener_по_video_source(string source)
    {
        var it = QbitController.BitmagnetItem(Row("Movie.2024.1080p.WEB-DL.mkv", source: source), 0);
        Assert.True(it.Value<bool>("bm_screener"));
        Assert.Equal(source, it.Value<string>("bm_src"));
    }

    [Fact]
    public void Screener_по_video_modifier()
    {
        var it = QbitController.BitmagnetItem(Row("Movie.2024.1080p.WEB-DL.mkv", modifier: "SCREENER"), 0);
        Assert.True(it.Value<bool>("bm_screener"));
    }

    [Fact]
    public void WEBRip_не_экранка()
    {
        var it = QbitController.BitmagnetItem(Row("Movie.2024.1080p.WEB-DL.mkv", source: "WEBRip"), 0);
        Assert.Null(it["bm_screener"]);
    }

    [Fact]
    public void Screener_по_имени_при_пустом_source()
    {
        var it = QbitController.BitmagnetItem(Row("Movie.2024.HDCAM.x264.mkv"), 0);
        Assert.True(it.Value<bool>("bm_screener"));
    }

    // ── качество из файлов пака: у пака без токена разрешения в имени ─────
    [Fact]
    public void Качество_из_файлов_пака_по_большинству()
    {
        var files = new List<(string, long)> { ("Show.S01E01.1080p.mkv", 1), ("Show.S01E02.1080p.mkv", 1), ("Show.S01E03.720p.mkv", 1) };
        var withFiles = QbitController.BitmagnetItem(Row("Show.S01.WEB.H264-GRP", episodes: "{\"1\": {}}", filesCount: 3, filesStatus: "multi", files: files), 1);
        Assert.Equal(1080, withFiles.Value<int>("quality"));
        Assert.Equal(3, ((JArray)withFiles["bm_files"]).Count);

        // без списка файлов узнать нечего — 0, а не выдумка
        var noFiles = QbitController.BitmagnetItem(Row("Show.S01.WEB.H264-GRP", episodes: "{\"1\": {}}", filesCount: 3, filesStatus: "multi"), 1);
        Assert.Equal(0, noFiles.Value<int>("quality"));
    }

    [Fact]
    public void Разрешение_из_БД_сильнее_файлов_и_имени()
    {
        var files = new List<(string, long)> { ("Show.S01E01.720p.mkv", 1) };
        var it = QbitController.BitmagnetItem(Row("Show.S01.1080p.WEB", episodes: "{\"1\": {}}", filesCount: 2, filesStatus: "multi", res: "V2160p", files: files), 1);
        Assert.Equal(2160, it.Value<int>("quality"));
    }

    // ── QualityFromResolution / NormalizeCodec (private static → рефлексия) ──
    [Theory]
    [InlineData("V540P", 540)]
    [InlineData("V4320P", 4320)]
    [InlineData("V2160p", 2160)]
    [InlineData("V1080p", 1080)]
    [InlineData(" v720p ", 720)]     // trim + регистр
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("V999p", null)]      // незнакомое значение — не гадаем
    public void QualityFromResolution_Таблица(string res, int? expected)
        => Assert.Equal(expected, (int?)Access.Call("QualityFromResolution", res));

    [Theory]
    [InlineData("x265", "hevc")]
    [InlineData("HEVC", "hevc")]
    [InlineData("H.264", "h264")]
    [InlineData("AVC", "h264")]
    [InlineData("AV1", "av1")]
    [InlineData("XviD", null)]       // legacy — в поле codec не попадает, его несёт bm_legacy
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeCodec_Таблица(string codec, string expected)
        => Assert.Equal(expected, (string)Access.Call("NormalizeCodec", new object[] { codec }));

    // ── сторожа SQL: строго по TMDB id, с типом контента, без текстового поиска (§AZ) ──
    [Fact]
    public void SQL_Select_привязка_по_id_и_типу_публичные_сиды_из_dht()
    {
        string sql = QbitController.BmSqlSelect;
        Assert.Contains("tc.content_type = @ctype", sql);            // id фильмов и сериалов в разных пространствах
        Assert.Contains("content_source = 'tmdb'", sql);
        Assert.Contains("tc.content_id = @id", sql);
        Assert.Contains("torrents_torrent_sources", sql);            // честные сиды dht, а не обнуляемая копия
        Assert.Contains("greatest(coalesce(tc.seeders", sql);
        Assert.Contains("coalesce(t.private, false) = false", sql);  // приватные трекеры по DHT не качаются
    }

    [Fact]
    public void SQL_Scoped_сезон_по_jsonb_сортировка_по_сидам()
    {
        string sql = QbitController.BmSqlScoped;
        Assert.Contains("episodes @> @seasonJson", sql);
        Assert.Contains("order by seeders desc", sql);
        Assert.Contains("limit @huntLim", sql);
        Assert.StartsWith(QbitController.BmSqlSelect, sql);
    }

    [Fact]
    public void SQL_Top_объединение_по_сидам_и_по_свежести()
    {
        string sql = QbitController.BmSqlTop;
        Assert.Contains("union", sql);
        Assert.Contains("order by seeders desc", sql);
        Assert.Contains("limit @lim", sql);
        Assert.Contains("order by t.created_at desc", sql);
        Assert.Contains("limit @fresh", sql);

        string seedsOnly = QbitController.BmSqlTopSeedsOnly;
        Assert.DoesNotContain("union", seedsOnly);
        Assert.Contains("limit @lim", seedsOnly);
        Assert.DoesNotContain("@fresh", seedsOnly);
    }

    [Fact]
    public void SQL_Files_по_массиву_хешей()
    {
        string sql = QbitController.BmSqlFiles;
        Assert.Contains("torrent_files", sql);
        Assert.Contains("info_hash = any(@hashes)", sql);
    }

    [Theory]
    [InlineData("ilike")]
    [InlineData("tsv @@")]
    [InlineData("name like")]
    public void SQL_Никакого_поиска_по_имени(string forbidden)
    {
        // 11.7 млн раздач без метаданных и 696 тыс. xxx — текстовый поиск по базе и был бы мусором
        foreach (var sql in new[] { QbitController.BmSqlSelect, QbitController.BmSqlScoped, QbitController.BmSqlTop, QbitController.BmSqlTopSeedsOnly, QbitController.BmSqlFiles })
            Assert.DoesNotContain(forbidden, sql.ToLowerInvariant());
    }

    // ── HideForeignSingles: иностранные поштучные серии видны только без русских ──
    // ⚠️ Этот заголовок kinozal сам IsRussian НЕ считает русским: «Укр» в начале «Укрытие» ловит
    // украинское вето _ukrMarkRx (хвостовой lookahead не исключает кириллицу), а русского языкового
    // токена в строке нет. «Русскость» выдачи здесь обеспечивает ColdFilm; пак остаётся потому, что он не
    // из bitmagnet. Заголовок с явным токеном — RuKinozal ниже.
    const string Kinozal = "Укрытие (Бункер) (3 сезон: 1-9 серии из 10) / Silo / WEB-DL (1080p)";
    const string RuKinozal = "Укрытие (Бункер) (3 сезон: 1-9 серии из 10) / Silo / 2026 / ПМ (RuDub) / WEBRip";

    static JArray SiloS03()
        => new JArray(
            Tor(Kinozal, "kinozal.guru"),                                                                     // русский пак с трекера
            Tor("Silo.S03E10.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA.mkv", "bitmagnet", eps: new[] { 10 }, season: 3),   // английская одиночка
            Tor("Silo S03E10 Troy 720p ATVP WEB-DL", "bitmagnet"),                                          // эхо нашего индекса: bm_* нет — по имени
            Tor("Silo.S03.1080p.WEB.H264-ETHEL", "bitmagnet", eps: new int[0], pack: true, season: 3),      // английский ПАК — не трогаем
            Tor(ColdFilm, "bitmagnet", eps: new[] { 10 }, season: 3));                                       // русская одиночка (ColdFilm)

    [Fact]
    public void HideForeignSingles_СкрываетТолькоИностранныеОдиночки()
    {
        var (list, hidden) = QbitController.HideForeignSingles(SiloS03());
        Assert.Equal(2, hidden);
        Assert.Equal(new[] { Kinozal, "Silo.S03.1080p.WEB.H264-ETHEL", ColdFilm }, Titles(list));   // порядок SortAndMark сохранён
    }

    [Fact]
    public void HideForeignSingles_Одиночка_определяется_по_bm_eps_а_не_по_files_status()
    {
        // иностранная серия часто упакована multi (видео + nfo/sample) — это всё равно одиночка
        var sorted = new JArray(
            Tor(RuKinozal, "kinozal.guru"),
            Tor("Silo.S03E07.720p.WEB.h264-ETHEL", "bitmagnet", eps: new[] { 7 }, season: 3, filesStatus: "multi", filesCount: 2));
        var (list, hidden) = QbitController.HideForeignSingles(sorted);
        Assert.Equal(1, hidden);
        Assert.Single(list);
        Assert.StartsWith("Укрытие", list[0].Value<string>("title"));
    }

    [Fact]
    public void HideForeignSingles_БезРусских_НичегоНеСкрывает_ТотЖеОбъект()
    {
        // ни одной русской → иностранные одиночки и есть вся выдача, показываем как есть
        var sorted = new JArray(
            Tor("Silo.S03E10.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA.mkv", "bitmagnet", eps: new[] { 10 }, season: 3),
            Tor("Silo S03E10 Troy 720p ATVP WEB-DL", "bitmagnet"),
            Tor("Silo.S03.1080p.WEB.H264-ETHEL", "bitmagnet", eps: new int[0], pack: true, season: 3));
        var (list, hidden) = QbitController.HideForeignSingles(sorted);
        Assert.Equal(0, hidden);
        Assert.Same(sorted, list);
    }

    [Fact]
    public void HideForeignSingles_Предохранитель_РусскаяСТрекера_ОстаётсяОдна()
    {
        // единственная строка — русская НЕ-bitmagnet: свёртка включена (русская есть), но скрывать нечего —
        // выдача не обнуляется. Заголовок с RuDub, чтобы русскость была настоящей, а не ранним выходом.
        var sorted = new JArray(Tor(RuKinozal, "kinozal.guru"));
        var (list, hidden) = QbitController.HideForeignSingles(sorted);
        Assert.Equal(0, hidden);
        Assert.Single(list);
        Assert.StartsWith("Укрытие", list[0].Value<string>("title"));
    }

    [Fact]
    public void HideForeignSingles_Русская_по_lang_ru_держит_выдачу()
    {
        // русскость — и по подсказке БД: латинское имя с lang_ru=true считается русским и включает свёртку
        var sorted = new JArray(
            Tor("Silo.S03E10.1080p.WEB.h264-GRP.mkv", "bitmagnet", eps: new[] { 10 }, season: 3, langRu: true),
            Tor("Silo S03E10 Troy 720p ATVP WEB-DL", "bitmagnet"));
        var (list, hidden) = QbitController.HideForeignSingles(sorted);
        Assert.Equal(1, hidden);
        Assert.Single(list);
        Assert.True(list[0].Value<bool>("lang_ru"));
    }

    [Fact]
    public void HideForeignSingles_ПустойИNull_КакЕсть()
    {
        var (nl, nh) = QbitController.HideForeignSingles(null);
        Assert.Null(nl);
        Assert.Equal(0, nh);

        var empty = new JArray();
        var (el, eh) = QbitController.HideForeignSingles(empty);
        Assert.Same(empty, el);
        Assert.Equal(0, eh);
    }

    // ── IsForeignSingle ──────────────────────────────────────────────────
    [Fact]
    public void IsForeignSingle_ТолькоBitmagnet()
    {
        Assert.False(QbitController.IsForeignSingle(Tor("Silo.S03E10.1080p.WEB.h264-GRP.mkv", "rutracker.org", eps: new[] { 10 })));
        Assert.False(QbitController.IsForeignSingle(Tor("Silo S03E10 Troy 720p ATVP WEB-DL", "kinozal.guru")));
        Assert.True(QbitController.IsForeignSingle(Tor("Silo.S03E10.1080p.WEB.h264-GRP.mkv", "bitmagnet", eps: new[] { 10 })));
    }

    [Fact]
    public void IsForeignSingle_РусскаяПоПодсказкеБД_НеИностранная()
        => Assert.False(QbitController.IsForeignSingle(Tor("Silo.S03E10.1080p.WEB.h264-GRP.mkv", "bitmagnet", eps: new[] { 10 }, langRu: true)));

    [Fact]
    public void IsForeignSingle_РусскаяПоИмени_НеИностранная()
    {
        Assert.False(QbitController.IsForeignSingle(Tor(ColdFilm, "bitmagnet", eps: new[] { 10 })));                              // русская студия
        Assert.False(QbitController.IsForeignSingle(Tor("Silo.S03E10.1080p.WEB-DL.Rus.Eng.mkv", "bitmagnet", eps: new[] { 10 })));  // языковой токен
        Assert.False(QbitController.IsForeignSingle(Tor("Бункер S03E10 WEB-DL 1080p", "bitmagnet", eps: new[] { 10 })));            // кириллица
        Assert.False(QbitController.IsForeignSingle(Tor(RuKinozal, "bitmagnet", eps: new[] { 10 })));
    }

    [Fact]
    public void IsForeignSingle_ПакиИМультисезонники_НеОдиночки()
    {
        Assert.False(QbitController.IsForeignSingle(Tor("Silo.S03.1080p.WEB.H264-ETHEL", "bitmagnet", eps: new int[0], pack: true)));
        Assert.False(QbitController.IsForeignSingle(Tor("Silo.S03E01-E05.1080p.WEB", "bitmagnet", eps: new[] { 1, 2, 3, 4, 5 })));   // несколько серий — не одиночка
        var multi = Tor("Silo.S01-S03.1080p.WEB", "bitmagnet");
        multi["bm_multi"] = true;
        Assert.False(QbitController.IsForeignSingle(multi));
    }

    [Fact]
    public void IsForeignSingle_БезBm_ПоИмени()
    {
        // эхо нашего индекса приходит без bm_* — одиночку читаем из S03E10 в названии
        Assert.True(QbitController.IsForeignSingle(Tor("Silo S03E10 Troy 720p ATVP WEB-DL", "bitmagnet")));
        Assert.True(QbitController.IsForeignSingle(Tor("Silo.S03E10.1080p.WEB.h264-GRP.mkv", "bitmagnet")));
        // пак по имени: сезон без серии — не одиночка
        Assert.False(QbitController.IsForeignSingle(Tor("Silo.S03.1080p.WEB.H264-ETHEL", "bitmagnet")));
        Assert.False(QbitController.IsForeignSingle(Tor("Silo Season 3 1080p WEB-DL", "bitmagnet")));
    }
}
