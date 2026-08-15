using QbitDownload;
using System;
using System.IO;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Постер строки ленты уведомлений — QbitController.NotiPosterUrl (qdl 2.46).
//
// 🔥 Что тут защищается. hash у jut-уведомления ПСЕВДО: sha1("jutsu:"+slug). Он проходит
// ValidHash (40 hex), поэтому весь hash-путь считает его торрентным — а файла img/<hash>.jpg
// для НЕ скачанного тайтла не существует НИКОГДА: его пишет только грабер (JutEnsureMeta),
// а в режиме «только уведомления» грабер не запускается. Клиент честно шёл в /qdl/poster?hash=,
// получал 404 и рисовал img_broken.svg на каждой отслеживаемой, но не скачанной серии.
// Замер на живом сервере 15.08.2026: 4 битых строки из 50.
//
// 🔴 Инвариант, который тут же и охраняется: фейковый постер по hash-пути для нескачанного
// НЕ пишется (meta и постер обязаны ездить парой — PurgeCache удаляет их одним набором).
// Поэтому единственный правильный ответ для notify-режима — URL ручки jut.su по слагу.
//
// ⚠️ У каждого кейса СВОЙ слаг: _jutUpCtype — process-wide кеш без инвалидации, и переиспользование
// слага между тестами протекает через границу теста (та же грабля, что в JutPosterEncodeTests).
// ─────────────────────────────────────────────────────────────────────────────
public class NotiPosterTests
{
    const string Hash = "0e6e08e0438c6c10a25415d0828b2a4bbccf830d";
    const string Live = "97f8fe2eec8c0a161c2f926551c8d85fff455a86";

    /// <summary>Минимальный валидный JPEG-заголовок: JutUpPosterGen читает размеры, а не просто факт файла.</summary>
    static byte[] Jpeg(int w = 460, int h = 690)
    {
        var b = new byte[4096];
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xC0;
        b[4] = 0x00; b[5] = 0x11; b[6] = 0x08;
        b[7] = (byte)(h >> 8); b[8] = (byte)(h & 0xFF);
        b[9] = (byte)(w >> 8); b[10] = (byte)(w & 0xFF);
        return b;
    }

    /// <summary>Простой lossy WebP — им JutUpPosterGen отличает ПЕРЕЖАТЫЙ постер (поколение 2).</summary>
    static byte[] Webp(int w = 460, int h = 667)
    {
        var b = new byte[4096];
        b[0] = (byte)'R'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'F';
        b[8] = (byte)'W'; b[9] = (byte)'E'; b[10] = (byte)'B'; b[11] = (byte)'P';
        b[12] = (byte)'V'; b[13] = (byte)'P'; b[14] = (byte)'8'; b[15] = (byte)' ';
        b[23] = 0x9D; b[24] = 0x01; b[25] = 0x2A;
        b[26] = (byte)(w & 0xFF); b[27] = (byte)((w >> 8) & 0x3F);
        b[28] = (byte)(h & 0xFF); b[29] = (byte)((h >> 8) & 0x3F);
        return b;
    }

    /// <summary>Разворачивает временный cachePath и гоняет тело под ним, возвращая конфиг на место.</summary>
    static void WithCache(Action<string> body, bool jutEnable = true, bool posterUpgrade = true)
    {
        var saved = ModInit.conf;
        string dir = Path.Combine(Path.GetTempPath(), "notipost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "img"));
        Directory.CreateDirectory(Path.Combine(dir, "jut", "img"));
        try
        {
            ModInit.conf = new ModuleConf { cachePath = dir, jutEnable = jutEnable, jutPosterUpgrade = posterUpgrade };
            body(dir);
        }
        finally
        {
            ModInit.conf = saved;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    #region скачанное — прежний путь не тронут

    [Fact]
    public void Скачанное_отдаёт_свой_файл_по_hash()
        => WithCache(dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "img", Hash + ".jpg"), Jpeg());
            Assert.Equal("/qdl/poster?hash=" + Hash, QbitController.NotiPosterUrl(Hash, null));
        });

    [Fact]
    public void Скачанный_jut_открывает_ЛОКАЛЬНЫЙ_файл_а_не_ручку_сайта()
        => WithCache(dir =>
        {
            // У скачанного jut-тайтла есть и то, и другое. Приоритет у своего файла: именно его
            // синхронизирует JutPosterSyncDownloads, и он же переживает недоступность jut.su.
            File.WriteAllBytes(Path.Combine(dir, "img", Hash + ".jpg"), Jpeg());
            File.WriteAllBytes(Path.Combine(dir, "jut", "img", "downloaded-one.up.jpg"), Webp());
            Assert.Equal("/qdl/poster?hash=" + Hash, QbitController.NotiPosterUrl(Hash, "downloaded-one"));
        });

    #endregion

    #region jut без скачивания — сама жалоба владельца

    [Fact]
    public void Отслеживаемый_но_не_скачанный_идёт_на_ручку_jut_по_слагу()
        => WithCache(dir =>
        {
            // Ровно боевой кейс: файла img/<псевдо-hash>.jpg нет и не будет.
            string url = QbitController.NotiPosterUrl(Hash, "clevatess");
            Assert.Equal("/qdl/jut/poster?slug=clevatess", url);
            Assert.DoesNotContain("/qdl/poster?hash=", url);
        });

    [Fact]
    public void Пережатый_постер_добавляет_поколение_в_URL()
        => WithCache(dir =>
        {
            // pv — ПОКОЛЕНИЕ кодировки, а не «апгрейд есть»: на ?v= стоит immutable на год.
            File.WriteAllBytes(Path.Combine(dir, "jut", "img", "gen-webp.up.jpg"), Webp());
            Assert.Equal("/qdl/jut/poster?slug=gen-webp&v=2", QbitController.NotiPosterUrl(Hash, "gen-webp"));
        });

    [Fact]
    public void Постер_приехавший_как_есть_даёт_первое_поколение()
        => WithCache(dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "jut", "img", "gen-jpeg.up.jpg"), Jpeg());
            Assert.Equal("/qdl/jut/poster?slug=gen-jpeg&v=1", QbitController.NotiPosterUrl(Hash, "gen-jpeg"));
        });

    [Fact]
    public void Выключённый_апгрейд_убирает_версию_но_не_ломает_URL()
        => WithCache(dir =>
        {
            // jutPosterUpgrade:false — полный откат апгрейда, файлы при этом остаются на диске.
            // URL обязан остаться рабочим (ручка сама отдаст сырой постер), но без ?v=.
            File.WriteAllBytes(Path.Combine(dir, "jut", "img", "rollback.up.jpg"), Webp());
            Assert.Equal("/qdl/jut/poster?slug=rollback", QbitController.NotiPosterUrl(Hash, "rollback"));
        }, posterUpgrade: false);

    #endregion

    #region гейты и мусор на входе

    [Fact]
    public void При_jutEnable_false_jut_ветка_молчит()
        => WithCache(dir =>
        {
            // /qdl/jut/poster при выключенном модуле отвечает 404 — гнать туда клиента нельзя.
            Assert.Null(QbitController.NotiPosterUrl(Hash, "clevatess"));
        }, jutEnable: false);

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("Clevatess")]
    [InlineData("сериал")]
    [InlineData("")]
    [InlineData(null)]
    public void Невалидный_слаг_не_попадает_в_URL(string slug)
        => WithCache(dir => Assert.Null(QbitController.NotiPosterUrl(Hash, slug)));

    [Fact]
    public void Строка_DIAG_без_hash_и_слага_даёт_null()
        => WithCache(dir =>
        {
            // «Поиск раздач» из SearchMonitor: hash пустой. Затычку рисует клиент — это ШТАТНО.
            Assert.Null(QbitController.NotiPosterUrl("", null));
            Assert.Null(QbitController.NotiPosterUrl(null, null));
            Assert.Null(QbitController.NotiPosterUrl("не-хеш", null));
        });

    [Fact]
    public void Вычищенная_раздача_без_слага_даёт_null_а_не_мёртвый_URL()
        => WithCache(dir => Assert.Null(QbitController.NotiPosterUrl(Hash, null)));

    #endregion

    #region SWITCH/re-grab — постер уехал на новый хеш

    [Fact]
    public void Мёртвый_hash_лечится_живым_хешем_серии()
        => WithCache(dir =>
        {
            // MigrateCache при SWITCH/re-grab ПЕРЕНОСИТ постер на новый хеш, а исторические строки
            // noti навсегда остаются со старым. Боевой случай: «Изгнанный реинкарнированный
            // тяжёлый рыцарь…», строки 262/263 ленты от 14.08.2026.
            File.WriteAllBytes(Path.Combine(dir, "img", Live + ".jpg"), Jpeg());
            Assert.Equal("/qdl/poster?hash=" + Live, QbitController.NotiPosterUrl(Hash, null, () => Live));
        });

    [Fact]
    public void Живой_хеш_без_файла_не_спасает_и_не_врёт()
        => WithCache(dir => Assert.Null(QbitController.NotiPosterUrl(Hash, null, () => Live)));

    [Fact]
    public void Живой_хеш_не_трогают_если_свой_файл_на_месте()
        => WithCache(dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "img", Hash + ".jpg"), Jpeg());
            File.WriteAllBytes(Path.Combine(dir, "img", Live + ".jpg"), Jpeg());
            bool asked = false;
            var url = QbitController.NotiPosterUrl(Hash, null, () => { asked = true; return Live; });
            Assert.Equal("/qdl/poster?hash=" + Hash, url);
            Assert.False(asked, "watch.json прочитан впустую — резолвер обязан быть ленивым");
        });

    [Fact]
    public void Jut_слаг_имеет_приоритет_над_живым_хешем()
        => WithCache(dir =>
        {
            // У jut-тайтла торрентного «живого хеша» быть не может, но если фолбэк когда-нибудь
            // получит мусор — своя ручка всё равно вернее.
            File.WriteAllBytes(Path.Combine(dir, "img", Live + ".jpg"), Jpeg());
            Assert.Equal("/qdl/jut/poster?slug=prio-jut", QbitController.NotiPosterUrl(Hash, "prio-jut", () => Live));
        });

    [Fact]
    public void Падение_фолбэка_не_роняет_резолвер()
        => WithCache(dir =>
            Assert.Null(QbitController.NotiPosterUrl(Hash, null, () => throw new InvalidOperationException("watch.json битый"))));

    #endregion
}
