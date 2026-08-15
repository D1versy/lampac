using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Свежесть локальных карточек в /qdl/list (§BM, боевой случай 12.08.2026).
//
// Симптом у владельца: «транскодировал фильм в mp4 — и он просто пропал». Данные были целы
// (mp4 12.99 ГБ в transcoded, маркер и мета на месте, /qdl/files отвечал), невидима была ровно
// карточка в «Загрузках», и возвращал её только рестарт контейнера.
//
// 🔥 Причина: снимок каталога в JsonStore бессрочный (сбрасывается только явным ForgetDir),
// а готовый mp4 кладёт ffmpeg — МИМО стора. FileInDir смотрел в устаревший снимок, отвечал
// «файла нет», у маркера не оставалось ни одного живого файла, и /qdl/list молча его пропускал
// (ни ошибки, ни строки в логе).
//
// Инвариант этих тестов: файл, появившийся на диске мимо JsonStore, ОБЯЗАН быть виден
// «Загрузкам» без рестарта. RunTranscodeItem зовёт ForgetDir сам, но эти тесты его намеренно
// НЕ зовут — проверяется самолечение промаха, то есть путь, который спасает и в гонке
// «список собирался ровно в момент транскода», и при появлении файла мимо нас (docker cp).
// Канон: E:\Media-server\claude\06-fixes-and-gotchas.md §BM
// ─────────────────────────────────────────────────────────────────────────────
public class ListLocalFreshnessTests
{
    const string H = "cccccccccccccccccccccccccccccccccccccccc";
    const string H2 = "dddddddddddddddddddddddddddddddddddddddd";

    static string NewDownloads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qdl-tests", "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Собранный /qdl/list с фейковым qBit (как в ListCacheTests): без кеша ответа.</summary>
    static async Task<JArray> RunList(string torrentsJson = "[]")
    {
        Access.SeedQbitFake(new FakeQbit().Json("/api/v2/torrents/info", torrentsJson).BuildHandler());
        try
        {
            var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var res = await ctrl.List();
            return JArray.Parse(Assert.IsType<ContentResult>(res).Content);
        }
        finally { Access.ResetQbitFake(); }
    }

    static JObject MovieMarker(string name, string path, long size) => new JObject
    {
        ["name"] = name,
        ["path"] = path,
        ["size"] = size,
        ["added"] = 1_700_000_000L
    };

    // ── /qdl/list ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Транскод_положил_mp4_мимо_стора_карточка_видна_без_рестарта()
    {
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 0;
        string prev = ModInit.conf.downloadsPath;
        string tdir = Path.Combine(NewDownloads(), "transcoded");
        Directory.CreateDirectory(tdir);
        try
        {
            ModInit.conf.downloadsPath = Path.GetDirectoryName(tdir);

            // 1) снимок каталога снят ДО транскода — ровно так и живёт работающий контейнер:
            //    любое открытие карточки строит список и кеширует листинг transcoded
            Assert.Empty(JsonStore.List(tdir, "*"));

            // 2) ffmpeg кладёт готовый mp4 мимо JsonStore, следом пишется маркер
            string mp4 = Path.Combine(tdir, "Фильм.mp4");
            File.WriteAllText(mp4, new string('x', 1024));
            Access.SaveLocal(H, MovieMarker("Фильм.mp4", mp4, 1024));

            // 3) карточка обязана быть в «Загрузках» СРАЗУ. До фикса тут было пусто до рестарта.
            var card = (await RunList()).FirstOrDefault(x => x.Value<string>("hash") == H);
            Assert.NotNull(card);
            Assert.Equal("local", card.Value<string>("state"));
            Assert.Equal("Фильм.mp4", card.Value<string>("name"));
            Assert.Equal(1.0, card.Value<double>("progress"));
        }
        finally { ModInit.conf.downloadsPath = prev; }
    }

    [Fact]
    public async Task Сериал_в_новой_папке_виден_целиком()
    {
        // Папки transcoded/<name>.<hash8> на момент снимка не было вовсе — закешировался
        // пустой массив, и «нет файла» получали ВСЕ серии сразу.
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 0;
        string prev = ModInit.conf.downloadsPath;
        string tdir = Path.Combine(NewDownloads(), "transcoded");
        Directory.CreateDirectory(tdir);
        try
        {
            ModInit.conf.downloadsPath = Path.GetDirectoryName(tdir);
            string sdir = Path.Combine(tdir, "Сериал.dddddddd");
            Assert.Empty(JsonStore.List(sdir, "*"));       // снимок несуществующего каталога

            Directory.CreateDirectory(sdir);
            string e1 = Path.Combine(sdir, "Ep01.mp4"), e2 = Path.Combine(sdir, "Ep02.mp4");
            File.WriteAllText(e1, new string('x', 100));
            File.WriteAllText(e2, new string('x', 200));
            Access.SaveLocal(H2, new JObject
            {
                ["name"] = "Сериал",
                ["dir"] = sdir,
                ["size"] = 300,
                ["added"] = 1_700_000_000L,
                ["overlay"] = false,
                ["files"] = new JArray
                {
                    new JObject { ["index"] = 0, ["name"] = "Ep01.mp4", ["path"] = e1, ["size"] = 100 },
                    new JObject { ["index"] = 1, ["name"] = "Ep02.mp4", ["path"] = e2, ["size"] = 200 }
                }
            });

            var card = (await RunList()).FirstOrDefault(x => x.Value<string>("hash") == H2);
            Assert.NotNull(card);
            Assert.Equal("local", card.Value<string>("state"));
            Assert.Equal(300, card.Value<long>("size"));
            Assert.Equal(sdir, card.Value<string>("content_path"));
        }
        finally { ModInit.conf.downloadsPath = prev; }
    }

    [Fact]
    public async Task Маркер_без_единого_живого_файла_карточку_не_даёт()
    {
        // Обратный инвариант: самолечение НЕ имеет права воскрешать удалённые файлы.
        TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 0;
        string prev = ModInit.conf.downloadsPath;
        string tdir = Path.Combine(NewDownloads(), "transcoded");
        Directory.CreateDirectory(tdir);
        try
        {
            ModInit.conf.downloadsPath = Path.GetDirectoryName(tdir);
            Access.SaveLocal(H, MovieMarker("Нет.mp4", Path.Combine(tdir, "Нет.mp4"), 1024));
            Assert.DoesNotContain((await RunList()).Select(x => x.Value<string>("hash")), h => h == H);
        }
        finally { ModInit.conf.downloadsPath = prev; }
    }

    // ── маркер и write-behind ─────────────────────────────────────────────

    [Fact]
    public void Первая_запись_маркера_видна_в_листинге_сразу()
    {
        // Список локальных карточек строится ПЕРЕЧИСЛЕНИЕМ каталога, а не чтением РАМ.
        // Пока write-behind не доехал (дебаунс 200 мс), карточки в листинге нет — а если ровно
        // в этом окне кто-то откроет карточку, снимок закешируется без неё навсегда.
        string cache = TestEnv.FreshCache();
        string local = Path.Combine(cache, "local");

        Access.SaveLocal(H, MovieMarker("Фильм.mp4", Path.Combine(cache, "Фильм.mp4"), 10));

        Assert.True(File.Exists(Path.Combine(local, H + ".json")), "первая запись маркера обязана быть на диске сразу");
        Assert.Single(JsonStore.List(local, "*.json"));
    }

    [Fact]
    public void Правки_существующего_маркера_остаются_коалесящимися()
    {
        // Обратная сторона: маркер сериала переписывается ПОСЛЕ КАЖДОЙ серии (60 серий = 60 правок).
        // Инвариант 4 стора обязан выжить — на диск это по-прежнему не 60 записей.
        TestEnv.FreshCache();
        var before = JsonStore.Stats();

        Access.SaveLocal(H2, MovieMarker("a.mp4", "/x/a.mp4", 1));      // первая — сквозная
        for (int i = 0; i < 30; i++) Access.SaveLocal(H2, MovieMarker("a.mp4", "/x/a.mp4", i));
        JsonStore.Flush();

        long written = JsonStore.Stats().diskWrites - before.diskWrites;
        Assert.True(written <= 3, "ожидали коалесинг правок, а записей на диск было " + written);
    }

    // ── FileInDir ─────────────────────────────────────────────────────────

    [Fact]
    public void Файл_созданный_после_снимка_виден_и_снимок_обновляется()
    {
        JsonStore.ForgetAllDirs();
        string dir = NewDownloads();
        string p = Path.Combine(dir, "a.mp4");

        Assert.Empty(JsonStore.List(dir, "*"));        // снимок взведён на пустом каталоге
        File.WriteAllText(p, "x");                     // файл появился мимо стора

        Assert.True(Access.FileInDir(p), "промах обязан перепроверяться File.Exists");
        // и снимок обязан быть сброшен: соседний файл того же каталога уже не платит за промах
        Assert.Single(JsonStore.List(dir, "*"));
    }

    [Fact]
    public void Отсутствующий_файл_остаётся_отсутствующим()
    {
        JsonStore.ForgetAllDirs();
        string dir = NewDownloads();
        string p = Path.Combine(dir, "нет.mp4");

        Assert.False(Access.FileInDir(p));
        Assert.False(Access.FileInDir(p));   // повтор ничего не меняет (ложных срабатываний нет)
    }

    [Fact]
    public void Живой_файл_обслуживается_из_снимка_без_перепроверок()
    {
        // Горячий путь не должен дорожать: пока файлы на месте, промахов нет вовсе.
        JsonStore.ForgetAllDirs();
        string dir = NewDownloads();
        string p = Path.Combine(dir, "b.mp4");
        File.WriteAllText(p, "x");

        Assert.True(Access.FileInDir(p));
        File.Delete(p);                       // удалили мимо стора — снимок ещё помнит файл
        Assert.True(Access.FileInDir(p));      // ответ из снимка, лишнего File.Exists не было
    }

    // ── HasPoster: та же болезнь, вторая жертва (§BV, 15.08.2026) ─────────
    //
    // 🔥 Боевой случай: /qdl/poster?hash=e571585d… отдавал 200 и 102 526 байт, а /qdl/list про ту
    // же строку говорил has_poster:false — и так до рестарта контейнера. Постер тайтла jut.su
    // положил догоняющий апгрейд (JutPosterSyncDownloads) уже ПОСЛЕ того, как снимок каталога img/
    // был снят, а сбрасывала снимок ровно одна точка записи из пяти (MetaHeal).
    // Клиент верил списку: и плитка грида, и экран qdl_card рисовали рваную заглушку.

    const string PH = "e571585d03c87fd61ae33eff7b2c1f2ee4cbcf23";   // «Провальный навык», боевой хеш
    const string Slug = "joutai-ijou-skill";

    /// <summary>Минимальный JPEG-заголовок: содержимое неважно, важен сам факт файла.</summary>
    static byte[] Jpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

    static string ImgDir(string cache)
    {
        string img = Path.Combine(cache, "img");
        Directory.CreateDirectory(img);
        return img;
    }

    [Fact]
    public void Постер_появившийся_мимо_стора_виден_без_рестарта()
    {
        string cache = TestEnv.FreshCache();
        string img = ImgDir(cache);

        Assert.Empty(JsonStore.List(img, "*.jpg"));                        // снимок взведён на пустом каталоге
        File.WriteAllBytes(Path.Combine(img, PH + ".jpg"), Jpeg());        // копия апгрейда мимо стора

        Assert.True(Access.HasPoster(PH), "промах обязан перепроверяться File.Exists");
        // и снимок обязан быть сброшен: соседняя карточка того же списка за промах уже не платит
        Assert.Single(JsonStore.List(img, "*.jpg"));
    }

    [Fact]
    public void Отсутствующий_постер_остаётся_отсутствующим()
    {
        // Обратный инвариант: самолечение не имеет права выдумывать постер, которого нет.
        string cache = TestEnv.FreshCache();
        ImgDir(cache);

        Assert.False(Access.HasPoster(PH));
        Assert.False(Access.HasPoster(PH));
    }

    [Fact]
    public void Живой_постер_обслуживается_из_снимка_без_перепроверок()
    {
        // Горячий путь не дорожает: пока постеры на месте, промахов нет вовсе.
        string cache = TestEnv.FreshCache();
        string img = ImgDir(cache);
        File.WriteAllBytes(Path.Combine(img, PH + ".jpg"), Jpeg());

        Assert.True(Access.HasPoster(PH));
        File.Delete(Path.Combine(img, PH + ".jpg"));   // удалили мимо стора — снимок ещё помнит
        Assert.True(Access.HasPoster(PH));             // ответ из снимка, лишнего File.Exists не было
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не-хеш")]
    [InlineData("../../etc/passwd")]
    public void Мусорный_хеш_до_диска_не_доходит(string hash)
    {
        // ValidHash обязан стоять ПЕРВЫМ: перепроверка промаха не должна открыть путь к ФС.
        TestEnv.FreshCache();
        Assert.False(Access.HasPoster(hash));
    }

    [Fact]
    public async Task Запись_постера_сбрасывает_и_снимок_и_кеш_ответа()
    {
        // Единственный тест, отличающий точечную инвалидацию от самолечения: без PosterWritten
        // готовый постер ждал бы истечения TTL ответа (30 с), а не появлялся сразу.
        string cache = TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 30;
        string prev = ModInit.conf.downloadsPath;
        string tdir = Path.Combine(NewDownloads(), "transcoded");
        Directory.CreateDirectory(tdir);
        try
        {
            ModInit.conf.downloadsPath = Path.GetDirectoryName(tdir);
            string mp4 = Path.Combine(tdir, "Фильм.mp4");
            File.WriteAllText(mp4, "x");
            Access.SaveLocal(PH, MovieMarker("Фильм.mp4", mp4, 1));

            var before = (await RunList()).First(x => x.Value<string>("hash") == PH);
            Assert.False(before.Value<bool>("has_poster"));

            File.WriteAllBytes(Path.Combine(ImgDir(cache), PH + ".jpg"), Jpeg());
            QbitController.PosterWritten();

            var after = (await RunList()).First(x => x.Value<string>("hash") == PH);
            Assert.True(after.Value<bool>("has_poster"), "внутри TTL ответ обязан пересобраться");
            Assert.Equal("/qdl/poster?hash=" + PH, after.Value<string>("posterUrl"));
        }
        finally { ModInit.conf.downloadsPath = prev; ModInit.conf.listCacheSeconds = 0; }
    }

    // ── /qdl/list: URL постера решает СЕРВЕР (§BV, тот же принцип, что §BU) ──

    [Fact]
    public void Jut_строка_без_своего_файла_получает_обложку_по_слагу()
    {
        // Ровно боевой случай: файла img/<hash>.jpg ещё нет (грабер не дошёл или запись сорвалась),
        // а каталожная обложка jut/img/<slug>.jpg лежит у нас с первого открытия тайтла.
        TestEnv.FreshCache();
        ModInit.conf.jutEnable = true;
        var item = new JObject { ["hash"] = PH, ["jut"] = new JObject { ["slug"] = Slug } };

        QbitController.DecorateListPoster(item);

        Assert.Equal("/qdl/jut/poster?slug=" + Slug, item.Value<string>("posterUrl"));
        Assert.DoesNotContain("/qdl/poster?hash=", item.Value<string>("posterUrl"));
    }

    [Fact]
    public void Скачанная_строка_отдаёт_свой_файл_а_не_ручку_сайта()
    {
        // Приоритет тот же, что в ленте: свой файл важнее ручки jut.su — он же получает апгрейд.
        string cache = TestEnv.FreshCache();
        ModInit.conf.jutEnable = true;
        File.WriteAllBytes(Path.Combine(ImgDir(cache), PH + ".jpg"), Jpeg());
        var item = new JObject { ["hash"] = PH, ["jut"] = new JObject { ["slug"] = Slug } };

        QbitController.DecorateListPoster(item);

        Assert.Equal("/qdl/poster?hash=" + PH, item.Value<string>("posterUrl"));
    }

    [Fact]
    public void Строка_без_постера_поля_не_несёт()
    {
        // null в ответ не кладём: клиент проверяет наличие, а лишний ключ едет на КАЖДОЙ строке.
        TestEnv.FreshCache();
        var item = new JObject { ["hash"] = PH };

        QbitController.DecorateListPoster(item);

        Assert.Null(item["posterUrl"]);
    }

    [Fact]
    public async Task Список_jut_строки_несёт_posterUrl_после_декорации()
    {
        // 🔴 Страж порядка: DecorateListPoster обязан стоять ПОСЛЕ JutDecorateListItem — слаг
        // кладёт она. Перенос строки выше красит этот тест (posterUrl уедет в торрентную ветку).
        string cache = TestEnv.FreshCache();
        ModInit.conf.listCacheSeconds = 0;
        ModInit.conf.jutEnable = true;
        string prev = ModInit.conf.downloadsPath;
        string tdir = Path.Combine(NewDownloads(), "jutsu");
        Directory.CreateDirectory(tdir);
        try
        {
            ModInit.conf.downloadsPath = Path.GetDirectoryName(tdir);
            string mp4 = Path.Combine(tdir, "Серия 1.mp4");
            File.WriteAllText(mp4, "x");
            var marker = MovieMarker("Провальный навык", mp4, 1);
            marker["jut"] = new JObject { ["slug"] = Slug };
            Access.SaveLocal(PH, marker);

            var row = (await RunList()).First(x => x.Value<string>("hash") == PH);

            Assert.Equal(Slug, row["jut"]?.Value<string>("slug"));
            Assert.Equal("/qdl/jut/poster?slug=" + Slug, row.Value<string>("posterUrl"));
        }
        finally { ModInit.conf.downloadsPath = prev; }
    }
}
