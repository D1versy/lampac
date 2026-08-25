using QbitDownload;
using System.Linq;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Скачивание XSMART: чистая кухня — имена файлов, разбор плейлистов, ключи таймлайна.
// Сеть здесь не нужна: всё это функции от строк, и именно в них живут самые дорогие
// ошибки (не то качество, не тот ключ прогресса, не открывшийся вход ffmpeg).
// ─────────────────────────────────────────────────────────────────────────────
public class XsmartGrabTests
{
    static XsmartEp Ep(int sno, int eno, string sid = "32215", string eid = "524438")
        => new XsmartEp { kind = XsmartKind.Episode, seasonNo = sno, epNo = eno, seasonId = sid, epId = eid };

    static XsmartEp Film() => new XsmartEp { kind = XsmartKind.Film, epNo = 1 };

    // ── имена файлов ⇄ разбор ────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 5, 1080, "3-12588511.s01e05.1080p.mp4")]
    [InlineData(2, 12, 720, "3-12588511.s02e12.720p.mp4")]
    [InlineData(1, 100, 0, "3-12588511.s01e100.mp4")]
    public void Имя_серии_несёт_ПОРЯДКОВЫЕ_номера(int sno, int eno, int q, string expect)
        => Assert.Equal(expect, QbitController.XsmartFileName("3-12588511", Ep(sno, eno), q));

    [Fact]
    public void Имя_фильма_несёт_маркер_film()
        => Assert.Equal("6-43970.film.2160p.mp4", QbitController.XsmartFileName("6-43970", Film(), 2160));

    [Theory]
    [InlineData("3-12588511.s01e05.1080p", 1, 5)]
    [InlineData("3-12588511.s01e05", 1, 5)]
    [InlineData("3-12588511.s02e100.720p", 2, 100)]
    public void Разбор_имени_серии_точная_инверсия(string name, int sno, int eno)
    {
        Assert.True(QbitController.TryParseXsmartFileName(name, out var kind, out int s, out int n));
        Assert.Equal(XsmartKind.Episode, kind);
        Assert.Equal(sno, s);
        Assert.Equal(eno, n);
    }

    [Fact]
    public void Разбор_имени_фильма_не_путается_с_серией()
    {
        // 🔴 Общий ParseEp читает «film1» как СЕРИЮ 1 — фильм получал бы ключ таймлайна первой
        // серии вместе с её отметкой просмотра. Свой парсер точен по построению.
        Assert.True(QbitController.TryParseXsmartFileName("6-43970.film.2160p", out var kind, out int s, out int n));
        Assert.Equal(XsmartKind.Film, kind);
        Assert.Equal(0, s);
        Assert.Equal(1, n);
    }

    [Fact]
    public void Разбор_не_цепляется_за_цифровой_префикс_тайтла()
    {
        // Префикс «<cat>-<id>» состоит из цифр. Незаякоренный разбор мог бы принять его за номер.
        Assert.False(QbitController.TryParseXsmartFileName("3-12588511", out _, out _, out _));
        Assert.False(QbitController.TryParseXsmartFileName("3-12588511.trailer", out _, out _, out _));
    }

    [Fact]
    public void Ключ_единицы_из_имени_совпадает_с_ключом_очереди()
    {
        var e = Ep(1, 5);
        string name = QbitController.XsmartFileName("3-12588511", e, 1080);
        string baseNoExt = name.Substring(0, name.Length - 4);
        Assert.Equal(e.epkey, QbitController.XsmartKeyFromName(baseNoExt));
    }

    // ── ключ таймлайна ───────────────────────────────────────────────────────

    [Fact]
    public void Ключ_таймлайна_серии_строится_на_ИДЕНТИФИКАТОРАХ_а_не_на_номерах()
    {
        // 🔴 Плагин раздела строит ровно такой ключ (normalize.timelineKey). Возьми мы номера
        // из имени файла — прогресс скачанной копии разошёлся бы с онлайн-просмотром,
        // и «Продолжить» показывало бы разное на одном и том же тайтле.
        Assert.Equal("xsmart:3:12588511:s32215e524438",
                     QbitController.XsmartTlKey(3, "12588511", Ep(1, 5)));
    }

    [Fact]
    public void Ключ_таймлайна_фильма_без_суффикса_серии()
        => Assert.Equal("xsmart:6:43970", QbitController.XsmartTlKey(6, "43970", Film()));

    // ── идентичность ─────────────────────────────────────────────────────────

    [Fact]
    public void Псевдо_infohash_сорок_hex_и_не_совпадает_с_jut()
    {
        string h = XsmartNet.Hash(3, "12588511");
        Assert.Equal(40, h.Length);
        Assert.Matches("^[0-9a-f]{40}$", h);
        Assert.NotEqual(h, XsmartNet.Hash(6, "12588511"));   // cat входит в соль
    }

    [Fact]
    public void Гейт_идентификаторов_режет_мусор()
    {
        Assert.True(XsmartNet.Valid(6, "43970"));
        Assert.False(XsmartNet.Valid(999, "43970"));        // категории вне таксономии нет
        Assert.False(XsmartNet.Valid(6, "../etc"));         // id уходит в ИМЯ ПАПКИ
        Assert.False(XsmartNet.Valid(6, ""));
    }

    // ── мастер-плейлист: выбор варианта ──────────────────────────────────────

    const string MASTER =
        "#EXTM3U\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n" +
        "/xsmart/stream/tok/360.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=5200000,RESOLUTION=1920x1080\n" +
        "/xsmart/stream/tok/1080.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=2400000,RESOLUTION=1280x720\n" +
        "/xsmart/stream/tok/720.m3u8\n";

    [Fact]
    public void Из_мастера_берём_МАКСИМАЛЬНЫЙ_битрейт_а_не_первый()
    {
        // 🔴 Ровно тут ffmpeg и подводит: сам он взял бы ПЕРВЫЙ вариант мастера, то есть 360p.
        // Дорожку «Авто» резолв отдаёт как максимум качества — значит выбирать обязаны мы.
        var (uri, h) = QbitController.XsmartPickMasterVariant(MASTER);
        Assert.Equal("/xsmart/stream/tok/1080.m3u8", uri);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void Без_BANDWIDTH_ранжируем_по_высоте_картинки()
    {
        string m = "#EXTM3U\n" +
                   "#EXT-X-STREAM-INF:RESOLUTION=640x360\n/xsmart/stream/tok/a.m3u8\n" +
                   "#EXT-X-STREAM-INF:RESOLUTION=1280x720\n/xsmart/stream/tok/b.m3u8\n";
        var (uri, h) = QbitController.XsmartPickMasterVariant(m);
        Assert.Equal("/xsmart/stream/tok/b.m3u8", uri);
        Assert.Equal(720, h);
    }

    [Fact]
    public void Обычный_медиа_плейлист_мастером_не_считается()
    {
        string media = "#EXTM3U\n#EXTINF:6.0,\n/xsmart/stream/tok/seg-1.ts\n#EXT-X-ENDLIST\n";
        var (uri, _) = QbitController.XsmartPickMasterVariant(media);
        Assert.Null(uri);
    }

    // ── медиа-плейлист: разбор ───────────────────────────────────────────────

    [Fact]
    public void Разбор_медиа_собирает_сегменты_и_init()
    {
        string media =
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n" +
            "#EXT-X-MAP:URI=\"/xsmart/stream/tok/init.mp4\"\n" +
            "#EXTINF:6.0,\n/xsmart/stream/tok/seg-1.m4s\n" +
            "#EXT-X-DISCONTINUITY\n" +
            "#EXTINF:6.0,\n/xsmart/stream/tok/seg-2.m4s\n#EXT-X-ENDLIST\n";

        var (segs, map, enc) = QbitController.XsmartParseMedia(media);
        Assert.Equal(2, segs.Count);
        Assert.Equal("/xsmart/stream/tok/init.mp4", map);
        Assert.Null(enc);
    }

    [Fact]
    public void Шифрованный_поток_опознаётся_а_не_качается_молча()
    {
        // Скачать зашифрованные сегменты «как есть» = получить файл, который не играет нигде.
        // Честный отказ лучше молчаливого мусора на диске.
        string media = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"/xsmart/stream/tok/k\"\n" +
                       "#EXTINF:6.0,\n/xsmart/stream/tok/seg-1.ts\n";
        var (_, _, enc) = QbitController.XsmartParseMedia(media);
        Assert.Equal("AES-128", enc);
    }

    [Fact]
    public void METHOD_NONE_шифрованием_не_считается()
    {
        string media = "#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:6.0,\n/xsmart/stream/tok/seg-1.ts\n";
        var (_, _, enc) = QbitController.XsmartParseMedia(media);
        Assert.Null(enc);
    }

    [Fact]
    public void Чужой_origin_в_плейлисте_не_наш()
    {
        // Инвариант №1 раздела: клиент и сервер ходят ТОЛЬКО в свой прокси.
        Assert.True(QbitController.XsmartOwnUri("/xsmart/stream/tok/seg-1.ts"));
        Assert.False(QbitController.XsmartOwnUri("https://rescdn17.daycamp.cc/seg-1.ts"));
        Assert.False(QbitController.XsmartOwnUri("/qdl/stream?hash=deadbeef"));
    }

    // ── локальный плейлист для ремукса ───────────────────────────────────────

    [Fact]
    public void Имя_сегмента_наше_порядковое_без_двоеточий()
    {
        // 🔴 У XSMART сегменты называются «720.mp4:hls:seg-1-v1-a1.ts». Такое имя ffmpeg
        // разбирает как ПРОТОКОЛ «720.mp4» — вход не открылся бы вовсе.
        string n = QbitController.XsmartSegName(0, "/xsmart/stream/tok/720.mp4:hls:seg-1-v1-a1.ts");
        Assert.Equal("s00000.ts", n);
        Assert.DoesNotContain(":", n);
    }

    [Fact]
    public void Расширение_сегмента_сохраняется()
    {
        Assert.Equal("s00007.m4s", QbitController.XsmartSegName(7, "/xsmart/stream/tok/seg-7.m4s"));
        Assert.Equal("s00001.ts", QbitController.XsmartSegName(1, "/xsmart/stream/tok/segment-no-ext"));
    }

    [Fact]
    public void Локальный_плейлист_сохраняет_теги_и_меняет_только_адреса()
    {
        // ⚠️ Собранный «по-своему» плейлист терял бы #EXTINF и #EXT-X-DISCONTINUITY —
        // склейка поехала бы по таймингам, а увидели бы мы это уже в плеере.
        string media =
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n" +
            "#EXT-X-MAP:URI=\"/xsmart/stream/tok/init.mp4\"\n" +
            "#EXTINF:6.0,\n/xsmart/stream/tok/a:hls:seg-1.ts\n" +
            "#EXT-X-DISCONTINUITY\n" +
            "#EXTINF:5.5,\n/xsmart/stream/tok/a:hls:seg-2.ts\n#EXT-X-ENDLIST\n";

        string local = QbitController.XsmartLocalPlaylist(
            media, new[] { "s00000.ts", "s00001.ts" }, "init.mp4");

        Assert.Contains("#EXT-X-TARGETDURATION:6", local);
        Assert.Contains("#EXTINF:6.0,", local);
        Assert.Contains("#EXT-X-DISCONTINUITY", local);
        Assert.Contains("#EXT-X-ENDLIST", local);
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4\"", local);
        Assert.Contains("s00000.ts", local);
        Assert.Contains("s00001.ts", local);
        // ни одного адреса нашего прокси в локальном плейлисте не остаётся
        Assert.DoesNotContain("/xsmart/stream/", local);
    }

    [Fact]
    public void Подпись_набора_меняется_при_смене_сегментов()
    {
        // На ней держится докачка: тот же набор — продолжаем, другой — качаем заново.
        string a = QbitController.XsmartSegSig(new[] { "/xsmart/stream/t/1.ts", "/xsmart/stream/t/2.ts" });
        string b = QbitController.XsmartSegSig(new[] { "/xsmart/stream/t/1.ts", "/xsmart/stream/t/2.ts" });
        string c = QbitController.XsmartSegSig(new[] { "/xsmart/stream/t/1.ts", "/xsmart/stream/t/3.ts" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ── тексты для клиента ───────────────────────────────────────────────────

    [Fact]
    public void Три_исхода_постановки_различимы_текстом()
    {
        // 🔥 На jut все три давали «В очереди: 0», и повторное нажатие выглядело так,
        // будто ничего не произошло.
        Assert.Contains("Поставлено в очередь: 2", QbitController.XsmartQueueMessage(2, 0, 0, 0, 2));
        Assert.Contains("Всё уже скачано", QbitController.XsmartQueueMessage(0, 5, 0, 0, 0));
        Assert.Contains("Уже в очереди", QbitController.XsmartQueueMessage(0, 0, 3, 0, 3));
        Assert.Contains("нет играбельного источника", QbitController.XsmartQueueMessage(0, 0, 0, 4, 0));
        Assert.Equal("Нечего скачивать", QbitController.XsmartQueueMessage(0, 0, 0, 0, 0));
    }

    [Fact]
    public void Агрегат_уведомлений_включается_на_пачке_и_на_доборе()
    {
        Assert.False(QbitController.XsmartAggFor(freshBatch: true, queued: 1));   // одна серия — своя строка
        Assert.True(QbitController.XsmartAggFor(freshBatch: true, queued: 12));   // пачка — одна строка
        Assert.True(QbitController.XsmartAggFor(freshBatch: false, queued: 1));   // добор к идущей пачке
    }
}
