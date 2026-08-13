using QbitDownload;
using System;
using System.IO;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Пережатие постеров jut.su (JutSuPosterOptimize.cs) и разбор WebP (JutSuMatch.ImageSize).
//
// 🔥 Главное, что тут защищается: WebP бывает ТРЁХ форм, и знать только VP8X мало.
// libvips webpsave пишет простой "VP8 " для непрозрачной картинки — а именно такие
// у нас все постеры. Пока ImageSize знал один VP8X, ArtAcceptable отказывал бы почти
// каждому пережатому постеру, и транскод выключился бы сам, без единой ошибки в логе.
// Байты заголовков сверены с реальной выдачей кодировщика (VP8: 460x667, VP8L: 460x667).
// ─────────────────────────────────────────────────────────────────────────────
public class JutPosterEncodeTests
{
    #region фикстуры WebP

    /// <summary>Простой lossy: RIFF/WEBP + "VP8 " + ключевой кадр со стартовым кодом 9D 01 2A.</summary>
    static byte[] WebpLossy(int w, int h)
    {
        var b = new byte[9000];
        Riff(b, "VP8 ");
        b[23] = 0x9D; b[24] = 0x01; b[25] = 0x2A;          // стартовый код ключевого кадра
        b[26] = (byte)(w & 0xFF); b[27] = (byte)((w >> 8) & 0x3F);
        b[28] = (byte)(h & 0xFF); b[29] = (byte)((h >> 8) & 0x3F);
        return b;
    }

    /// <summary>Простой lossless: "VP8L" + сигнатура 0x2F + по 14 бит на сторону, минус единица.</summary>
    static byte[] WebpLossless(int w, int h)
    {
        var b = new byte[9000];
        Riff(b, "VP8L");
        b[20] = 0x2F;
        int bits = ((w - 1) & 0x3FFF) | (((h - 1) & 0x3FFF) << 14);
        b[21] = (byte)bits; b[22] = (byte)(bits >> 8);
        b[23] = (byte)(bits >> 16); b[24] = (byte)(bits >> 24);
        return b;
    }

    /// <summary>Расширенный: "VP8X", размеры тремя байтами каждая, минус единица.</summary>
    static byte[] WebpExtended(int w, int h)
    {
        var b = new byte[9000];
        Riff(b, "VP8X");
        int ww = w - 1, hh = h - 1;
        b[24] = (byte)ww; b[25] = (byte)(ww >> 8); b[26] = (byte)(ww >> 16);
        b[27] = (byte)hh; b[28] = (byte)(hh >> 8); b[29] = (byte)(hh >> 16);
        return b;
    }

    static void Riff(byte[] b, string fourcc)
    {
        b[0] = (byte)'R'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'F';
        b[8] = (byte)'W'; b[9] = (byte)'E'; b[10] = (byte)'B'; b[11] = (byte)'P';
        for (int i = 0; i < 4; i++) b[12 + i] = (byte)fourcc[i];
    }

    #endregion

    #region разбор WebP

    [Fact]
    public void WebP_простой_lossy_разбирается()
        => Assert.Equal((460, 667), JutSuMatch.ImageSize(WebpLossy(460, 667)));

    [Fact]
    public void WebP_lossless_разбирается()
        => Assert.Equal((460, 667), JutSuMatch.ImageSize(WebpLossless(460, 667)));

    [Fact]
    public void WebP_расширенный_разбирается_как_и_раньше()
        => Assert.Equal((460, 690), JutSuMatch.ImageSize(WebpExtended(460, 690)));

    [Fact]
    public void WebP_без_стартового_кода_не_выдумывает_размер()
    {
        var b = WebpLossy(460, 667);
        b[23] = 0x00;                       // стартовый код испорчен
        Assert.Equal((0, 0), JutSuMatch.ImageSize(b));
    }

    [Fact]
    public void Санити_пропускает_пережатый_постер()
    {
        // Ровно тот случай, ради которого правился ImageSize: портрет 460×667 в простом VP8.
        Assert.True(JutSuMatch.ArtAcceptable(WebpLossy(460, 667), 200, out int w, out int h, out string mime));
        Assert.Equal(460, w);
        Assert.Equal(667, h);
        Assert.Equal("image/webp", mime);
    }

    [Fact]
    public void Санити_отвергает_пейзаж_и_мелочь()
    {
        Assert.False(JutSuMatch.ArtAcceptable(WebpLossy(690, 460), 200, out _, out _, out _));  // не портрет
        Assert.False(JutSuMatch.ArtAcceptable(WebpLossy(150, 220), 200, out _, out _, out _));  // уже минимума
    }

    #endregion

    #region настройки кодека

    [Fact]
    public void Формат_нормализуется_кривой_конфиг_не_ломает_кодек()
    {
        var saved = ModInit.conf;
        try
        {
            ModInit.conf = new ModuleConf { jutPosterFormat = "  WEBP " };
            Assert.Equal("webp", QbitController.JutPosterFormat());

            ModInit.conf = new ModuleConf { jutPosterFormat = "мусор" };
            Assert.Equal("webp", QbitController.JutPosterFormat());

            ModInit.conf = new ModuleConf { jutPosterFormat = "JPEG" };
            Assert.Equal("jpeg", QbitController.JutPosterFormat());

            ModInit.conf = new ModuleConf { jutPosterFormat = "none" };
            Assert.Equal("none", QbitController.JutPosterFormat());
        }
        finally { ModInit.conf = saved; }
    }

    [Fact]
    public void Киллсвитч_none_гасит_пережатие()
    {
        var saved = ModInit.conf;
        try
        {
            ModInit.conf = new ModuleConf { jutPosterFormat = "none" };
            Assert.Null(QbitController.JutPosterEncode(new byte[9000]));
        }
        finally { ModInit.conf = saved; }
    }

    [Fact]
    public void Мусор_на_входе_не_бросает_а_возвращает_null()
    {
        // 🔥 «Постер не теряется никогда»: любой сбой кодека обязан выродиться в null,
        // чтобы вызывающий сохранил ОРИГИНАЛ, а не упал.
        var saved = ModInit.conf;
        try
        {
            ModInit.conf = new ModuleConf { jutPosterFormat = "webp" };
            Assert.Null(QbitController.JutPosterEncode(null));
            Assert.Null(QbitController.JutPosterEncode(Array.Empty<byte>()));
            Assert.Null(QbitController.JutPosterEncode(new byte[] { 1, 2, 3, 4, 5 }));

            var html = System.Text.Encoding.ASCII.GetBytes(new string('<', 9000));
            Assert.Null(QbitController.JutPosterEncode(html));
        }
        finally { ModInit.conf = saved; }
    }

    #endregion

    #region поколение постера (pv)

    [Fact]
    public void Поколение_различает_пережатый_и_приехавший_как_есть()
    {
        var saved = ModInit.conf;
        string dir = Path.Combine(Path.GetTempPath(), "jutgen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "jut", "img"));
        try
        {
            ModInit.conf = new ModuleConf { cachePath = dir, jutEnable = true };

            // WebP → поколение 2: именно им бампается pv, иначе клиент с immutable на год
            // пережатый файл не запросит никогда.
            File.WriteAllBytes(Path.Combine(dir, "jut", "img", "a.up.jpg"), WebpLossy(460, 667));
            Assert.Equal(2, QbitController.JutUpPosterGen("a"));

            // Лежит как приехал → поколение 1, URL прежний, перекачки не будет.
            File.WriteAllBytes(Path.Combine(dir, "jut", "img", "b.up.jpg"), Jpeg(460, 690));
            Assert.Equal(1, QbitController.JutUpPosterGen("b"));

            File.WriteAllBytes(Path.Combine(dir, "jut", "img", "c.up.jpg"), Png(460, 650));
            Assert.Equal(1, QbitController.JutUpPosterGen("c"));
        }
        finally
        {
            ModInit.conf = saved;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static byte[] Jpeg(int w, int h)
    {
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

    #endregion
}
