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
}
