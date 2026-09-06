using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// /qdl/progress — лёгкий живой прогресс загрузок (qdl 2.93, Progress.cs).
//
// Ручка существует ровно затем, чтобы клиент мог опрашивать состояние раз в 5 секунд,
// не платя за это 45 КБ и секунду пересборки /qdl/list. Здесь под тестами три её контракта:
//
//   1. `items` содержит ТОЛЬКО недокачанное — отсутствие хеша при ok:true клиент читает
//      как «готово». Попади сюда докачанная раздача, гейт запер бы её навсегда.
//   2. `ok:false` — это «не знаю», а не «всё скачано». Лёгший qBit, киллсвитч и реплика
//      обязаны отвечать 200 с ok:false, а не 500 и не пустым списком с ok:true.
//   3. В теле нет времени → одинаковое состояние даёт одинаковый ETag → 304.
//
// Плюс сторож общего порога 0.999: он живёт в ЧЕТЫРЁХ местах модуля, и разъехавшись,
// вернул бы «дождитесь загрузки» на полностью скачанном сериале.
// ─────────────────────────────────────────────────────────────────────────────
public class ProgressTests
{
    const string HA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string HB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string HC = "cccccccccccccccccccccccccccccccccccccccc";
    const string HD = "dddddddddddddddddddddddddddddddddddddddd";

    static string T(string hash, double progress, string state, long dlspeed = 0)
        => $"{{\"hash\":\"{hash}\",\"name\":\"t\",\"progress\":{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
         + $"\"state\":\"{state}\",\"dlspeed\":{dlspeed},\"size\":100,\"added_on\":1}}";

    /// <summary>Прогон ручки на фейковом qBit. Возвращает разобранное тело (или null для 304/ошибки).</summary>
    static async Task<(JObject body, ActionResult raw, System.Collections.Generic.IReadOnlyList<System.Net.Http.HttpRequestMessage> reqs)>
        Run(FakeQbit fake, string hash = null, string ifNoneMatch = null, HttpContext reuse = null)
    {
        Access.SeedQbitFake(fake.BuildHandler());
        try
        {
            var http = reuse ?? new DefaultHttpContext();
            if (ifNoneMatch != null) http.Request.Headers["If-None-Match"] = ifNoneMatch;
            var ctrl = new QbitController { ControllerContext = new ControllerContext { HttpContext = http } };
            var res = await ctrl.Progress(hash);
            var body = res is ContentResult cr ? JObject.Parse(cr.Content) : null;
            return (body, res, fake.Requests);
        }
        finally { Access.ResetQbitFake(); }
    }

    static FakeQbit Qbit(string mainInfo, string donorInfo = "[]", string files = null)
    {
        var f = new FakeQbit();
        if (files != null) f.Json("/torrents/files", files);
        // порядок важен: первым матчится более узкий роут донорской категории
        f.Route("/torrents/info", req =>
        {
            string url = req.RequestUri!.ToString();
            bool donor = url.Contains("-donor", System.StringComparison.OrdinalIgnoreCase);
            return FakeHttpMessageHandler.Json(donor ? donorInfo : mainInfo);
        });
        return f;
    }

    static void Conf()
    {
        TestEnv.FreshCache();
        ModInit.conf.progressPollSeconds = 5;
        ModInit.conf.progressSnapshotSeconds = 0;   // по умолчанию без снимка — снимок тестируется отдельно
        ModInit.conf.progressIdlePollSeconds = 30;
        ModInit.conf.progressIdleBudgetMinutes = 10;
        ModInit.conf.partialPlayBlock = true;
        ModInit.conf.replicaRole = "";
    }

    // ─────────────────────── сводка ───────────────────────

    [Fact]
    public async Task Докачанное_в_items_не_попадает_а_active_и_pending_разведены()
    {
        Conf();
        var (body, _, _) = await Run(Qbit("[" +
            T(HA, 0.42, "downloading", 500_000) + "," +      // движется
            T(HB, 0.10, "stalledDL") + "," +                 // недокачано, но стоит
            T(HC, 1.0, "uploading") + "]"));                 // готово — в ответе его быть не должно

        Assert.True(body.Value<bool>("ok"));
        var items = (JArray)body["items"];
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, x => x.Value<string>("h") == HC);
        Assert.Equal(1, body.Value<int>("active"));
        Assert.Equal(1, body.Value<int>("pending"));
    }

    [Fact]
    public async Task StalledDL_со_скоростью_считается_активным()
    {
        Conf();
        // qBit ставит stalledDL и когда прямо в эту секунду никто не отдаёт — флаг мерцает,
        // и без поправки на скорость опрос уходил бы в медленный режим посреди работы.
        var (body, _, _) = await Run(Qbit("[" + T(HA, 0.3, "stalledDL", 1_200_000) + "]"));
        Assert.Equal(1, body.Value<int>("active"));
        Assert.Equal(0, body.Value<int>("pending"));
    }

    [Theory]
    [InlineData("pausedDL")]    // qBit 4.x
    [InlineData("stoppedDL")]   // qBit 5.x — то же самое под новым именем
    [InlineData("error")]
    [InlineData("чего-то-новенькое")]
    public async Task Стоящие_и_неизвестные_состояния_идут_в_pending(string state)
    {
        Conf();
        var (body, _, _) = await Run(Qbit("[" + T(HA, 0.3, state) + "]"));
        Assert.Equal(0, body.Value<int>("active"));
        Assert.Equal(1, body.Value<int>("pending"));
    }

    [Fact]
    public async Task Прогресс_0_9995_считается_готовым_и_в_items_не_едет()
    {
        Conf();
        // 🔴 Порог 0.999, а не 1.0. Взвешенный прогресс полностью скачанной группы сезонов
        // на double даёт 0.9999999 — на строгом сравнении «дождитесь загрузки» вылезало бы
        // на готовом сериале.
        var (body, _, _) = await Run(Qbit("[" + T(HA, 0.9995, "downloading", 100) + "]"));
        Assert.Empty((JArray)body["items"]);
        Assert.Equal(0, body.Value<int>("active"));
        Assert.Equal(0, body.Value<int>("pending"));
    }

    [Fact]
    public async Task Доноры_охоты_попадают_в_сводку()
    {
        Conf();
        // Доноров нет в гриде «Загрузок», но их докачка разблокирует серию на экране серий.
        var (body, _, _) = await Run(Qbit("[]", "[" + T(HD, 0.2, "downloading", 900) + "]"));
        Assert.Single((JArray)body["items"]);
        Assert.Equal(HD, ((JArray)body["items"])[0].Value<string>("h"));
        Assert.Equal(1, body.Value<int>("active"));
    }

    [Fact]
    public async Task Падение_донорской_категории_не_роняет_сводку()
    {
        Conf();
        var fake = new FakeQbit();
        fake.Route("/torrents/info", req =>
            req.RequestUri!.ToString().Contains("-donor", System.StringComparison.OrdinalIgnoreCase)
                ? FakeHttpMessageHandler.Text("boom", System.Net.HttpStatusCode.InternalServerError)
                : FakeHttpMessageHandler.Json("[" + T(HA, 0.5, "downloading", 10) + "]"));

        var (body, _, _) = await Run(fake);
        Assert.True(body.Value<bool>("ok"));
        Assert.Single((JArray)body["items"]);
    }

    // ─────────────────────── per-file ───────────────────────

    [Fact]
    public async Task Без_hash_файлы_не_запрашиваются_вовсе()
    {
        Conf();
        // ⚠️ 65 загрузок × torrents/files на КАЖДЫЙ тик КАЖДОГО устройства — ровно то,
        // ради чего ручка и заводилась отдельно от /qdl/list.
        var (body, _, reqs) = await Run(Qbit("[" + T(HA, 0.4, "downloading", 1) + "]",
                                             files: "[{\"index\":0,\"progress\":0.5}]"));
        Assert.Null(body["files"]);
        Assert.DoesNotContain(reqs, r => r.RequestUri!.ToString().Contains("/torrents/files"));
    }

    [Fact]
    public async Task С_hash_приезжает_per_file_прогресс()
    {
        Conf();
        var (body, _, _) = await Run(Qbit("[" + T(HA, 0.4, "downloading", 1) + "]",
                                          files: "[{\"index\":0,\"progress\":1},{\"index\":3,\"progress\":0.6218}]"),
                                     hash: HA);
        var arr = (JArray)body["files"][HA];
        Assert.Equal(2, arr.Count);
        Assert.Equal(0, arr[0][0].Value<int>());
        Assert.Equal(1.0, arr[0][1].Value<double>());
        Assert.Equal(3, arr[1][0].Value<int>());
        Assert.Equal(0.6218, arr[1][1].Value<double>(), 4);
    }

    [Fact]
    public async Task С_hash_покрываются_доноры_раздачи()
    {
        Conf();
        // То же множество хешей, что обходит EpisodesJson: иначе строки донорских серий на
        // экране остались бы без живых данных и залипли на снимке /qdl/episodes.
        Access.SaveWatch(new JArray(new JObject
        {
            ["hash"] = HA,
            ["donors"] = new JArray(new JObject { ["hash"] = HD })
        }));

        var (body, _, _) = await Run(Qbit("[" + T(HA, 0.4, "downloading", 1) + "]",
                                          files: "[{\"index\":0,\"progress\":0.5}]"),
                                     hash: HA);
        Assert.NotNull(body["files"][HA]);
        Assert.NotNull(body["files"][HD]);
    }

    [Fact]
    public async Task Невалидный_hash_отвергается()
    {
        Conf();
        var (_, raw, _) = await Run(Qbit("[]"), hash: "не-хеш");
        Assert.IsType<BadRequestObjectResult>(raw);
    }

    // ─────────────────────── снимок, ETag, деградация ───────────────────────

    [Fact]
    public async Task Снимок_схлопывает_два_запроса_в_один_поход_к_qBit()
    {
        Conf();
        ModInit.conf.progressSnapshotSeconds = 30;
        QbitController.DropProgressCache();

        var fake = Qbit("[" + T(HA, 0.4, "downloading", 1) + "]");
        Access.SeedQbitFake(fake.BuildHandler());
        try
        {
            var mk = () => new QbitController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            await mk().Progress();
            int after1 = fake.Requests.Count(r => r.RequestUri!.ToString().Contains("/torrents/info"));
            await mk().Progress();
            int after2 = fake.Requests.Count(r => r.RequestUri!.ToString().Contains("/torrents/info"));
            Assert.Equal(after1, after2);
        }
        finally { Access.ResetQbitFake(); QbitController.DropProgressCache(); }
    }

    [Fact]
    public async Task Одинаковое_состояние_даёт_304_потому_что_в_теле_нет_времени()
    {
        Conf();
        var (body, raw, _) = await Run(Qbit("[" + T(HA, 0.4, "downloading", 1) + "]"));
        Assert.IsType<ContentResult>(raw);

        // ETag берём из ответа первого прогона — он посчитан по телу
        var http = new DefaultHttpContext();
        Access.SeedQbitFake(Qbit("[" + T(HA, 0.4, "downloading", 1) + "]").BuildHandler());
        string etag;
        try
        {
            var c1 = new QbitController { ControllerContext = new ControllerContext { HttpContext = http } };
            await c1.Progress();
            etag = http.Response.Headers["ETag"];
        }
        finally { Access.ResetQbitFake(); }

        Assert.False(string.IsNullOrEmpty(etag));
        var (_, second, _) = await Run(Qbit("[" + T(HA, 0.4, "downloading", 1) + "]"), ifNoneMatch: etag);
        Assert.Equal(304, Assert.IsType<StatusCodeResult>(second).StatusCode);
    }

    [Fact]
    public async Task Киллсвитч_гасит_ручку_и_не_трогает_qBit()
    {
        Conf();
        ModInit.conf.progressPollSeconds = 0;

        var (body, _, reqs) = await Run(Qbit("[" + T(HA, 0.4, "downloading", 1) + "]"));
        Assert.False(body.Value<bool>("ok"));
        Assert.Equal(0, body.Value<int>("poll"));
        Assert.Empty(reqs);   // ноль обращений к qBittorrent
    }

    [Fact]
    public async Task Лёгший_qBittorrent_даёт_ok_false_и_200_а_не_500()
    {
        Conf();
        // 🔴 Клиент обязан отличить «не знаю» от «всё скачано»: на ok:false он не делает
        // выводов вообще. 500 он проглотил бы как сетевую ошибку — то же самое, но шумнее.
        var fake = new FakeQbit().Text("/torrents/info", "down", System.Net.HttpStatusCode.InternalServerError);
        var (body, raw, _) = await Run(fake);
        Assert.IsType<ContentResult>(raw);
        Assert.False(body.Value<bool>("ok"));
        Assert.Empty((JArray)body["items"]);
    }

    [Fact]
    public async Task Настройки_поллера_едут_в_теле_и_в_features()
    {
        Conf();
        ModInit.conf.progressIdlePollSeconds = 45;
        ModInit.conf.progressIdleBudgetMinutes = 3;
        ModInit.conf.partialPlayBlock = false;

        var (body, _, _) = await Run(Qbit("[]"));
        Assert.Equal(5, body.Value<int>("poll"));
        Assert.Equal(45, body.Value<int>("idle"));
        Assert.Equal(3, body.Value<int>("budget"));
        Assert.False(body.Value<bool>("block"));

        // тот же блок клиент получает из /qdl/features (loadFeatures ходит каждые 60 с)
        var conf = QbitController.ProgressClientConf();
        Assert.Equal(5, conf.Value<int>("poll"));
        Assert.Equal(45, conf.Value<int>("idle"));
        Assert.Equal(3, conf.Value<int>("budget"));
        Assert.False(conf.Value<bool>("block"));
    }

    // ─────────────────────── сторож общего порога ───────────────────────

    // ─────────────────── закачки XSMART «в полёте» (qdl 2.114) ───────────────────
    // Карточка XSMART/jut до первого готового файла живёт в /qdl/list, а живой процент ей даёт
    // этот же поллер под псевдо-infohash. Контракт тот же: в items только недокачанное,
    // «качается» → active (быстрый опрос), «в очереди» → pending (медленный пульс).

    const string SREF = "6-10425171";
    static string XsHash => XsmartNet.Hash(6, "10425171");

    /// <summary>Окружение XSMART + настройки поллера. ⚠️ Не вместе с Conf(): оба зовут FreshCache.</summary>
    static void ConfXs()
    {
        XsAccess.Env();
        ModInit.conf.progressPollSeconds = 5;
        ModInit.conf.progressSnapshotSeconds = 0;
        ModInit.conf.progressIdlePollSeconds = 30;
        ModInit.conf.progressIdleBudgetMinutes = 10;
        ModInit.conf.partialPlayBlock = true;
        ModInit.conf.replicaRole = "";
    }

    [Fact]
    public async Task Xsmart_в_полёте_едет_в_items_и_считается_active()
    {
        ConfXs();
        using var pin = XsAccess.PinWorker();
        WantsAccess.CommitXs(SREF, 6, "10425171", WantsAccess.Film());
        XsAccess.JobSet(SREF, "running", seg: 40, segTotal: 100);

        var (body, _, _) = await Run(Qbit("[]"));
        Assert.True(body.Value<bool>("ok"));
        var it = ((JArray)body["items"]).OfType<JObject>().FirstOrDefault(x => x.Value<string>("h") == XsHash);
        Assert.NotNull(it);
        Assert.Equal(0.4, it.Value<double>("p"), 3);
        Assert.Equal("downloading", it.Value<string>("s"));
        Assert.Equal(1, body.Value<int>("active"));
        Assert.Equal(0, body.Value<int>("pending"));
    }

    [Fact]
    public async Task Xsmart_queued_считается_pending()
    {
        ConfXs();
        using var pin = XsAccess.PinWorker();
        WantsAccess.CommitXs(SREF, 6, "10425171", WantsAccess.Film());
        XsAccess.JobSet(SREF, "queued");

        var (body, _, _) = await Run(Qbit("[]"));
        var it = ((JArray)body["items"]).OfType<JObject>().FirstOrDefault(x => x.Value<string>("h") == XsHash);
        Assert.NotNull(it);
        Assert.Equal("queued", it.Value<string>("s"));
        Assert.Equal(0, body.Value<int>("active"));
        Assert.Equal(1, body.Value<int>("pending"));
    }

    [Fact]
    public async Task Xsmart_на_ремуксе_не_читается_как_готовое()
    {
        // 🔴 seg == segTotal наступает ДО ремукса и ДО маркера. Без капа хеш пропал бы из items,
        // клиент прочитал бы «готово», снял гейт и открыл файл, которого ещё нет.
        ConfXs();
        using var pin = XsAccess.PinWorker();
        WantsAccess.CommitXs(SREF, 6, "10425171", WantsAccess.Film());
        XsAccess.JobSet(SREF, "running", seg: 911, segTotal: 911);

        var (body, _, _) = await Run(Qbit("[]"));
        var it = ((JArray)body["items"]).OfType<JObject>().FirstOrDefault(x => x.Value<string>("h") == XsHash);
        Assert.NotNull(it);
        Assert.True(it.Value<double>("p") < QbitController.ProgressDone);
        Assert.Equal(0.99, it.Value<double>("p"), 3);
    }

    [Fact]
    public async Task Xsmart_завершённая_пачка_в_items_не_едет()
    {
        // Долга нет, очередь пуста, job лежит «done» до уборки — это не полёт: хеша в items нет,
        // и клиент честно читает «готово».
        ConfXs();
        using var pin = XsAccess.PinWorker();
        XsAccess.JobSet(SREF, "done", fileDone: 1, filesTotal: 1);

        var (body, _, _) = await Run(Qbit("[]"));
        Assert.True(body.Value<bool>("ok"));
        Assert.DoesNotContain(((JArray)body["items"]).OfType<JObject>(), x => x.Value<string>("h") == XsHash);
        Assert.Equal(0, body.Value<int>("active"));
    }

    [Fact]
    public async Task Лёгший_qBittorrent_с_xsmart_в_полёте_всё_равно_ok_false()
    {
        // Иначе недокачанные торренты прочитались бы как готовые (нет хеша при ok:true = готово).
        ConfXs();
        using var pin = XsAccess.PinWorker();
        WantsAccess.CommitXs(SREF, 6, "10425171", WantsAccess.Film());
        XsAccess.JobSet(SREF, "running", seg: 10, segTotal: 100);

        var fake = new FakeQbit().Text("/torrents/info", "down", System.Net.HttpStatusCode.InternalServerError);
        var (body, _, _) = await Run(fake);
        Assert.False(body.Value<bool>("ok"));
        Assert.Empty((JArray)body["items"]);
    }

    [Fact]
    public void Порог_готовности_один_на_весь_модуль()
    {
        // 🔴 0.999 живёт в ЧЕТЫРЁХ местах: здесь, в MergeEpisodeFiles («основная готова —
        // донор не нужен»), в MergeGroupEpisodes (выбор копии) и в гейте транскода.
        // Разъехавшись, они дали бы разный ответ на вопрос «эта серия скачана?» —
        // строка на экране разблокирована, а транскод отказывает, и наоборот.
        Assert.Equal(0.999, QbitController.ProgressDone);

        // Сканируем ВЕСЬ модуль: любой литерал вида 0.99… обязан быть ровно порогом.
        // Исключение ровно одно и оно про другое — Math.Min(0.99, …) в отчёте транскода
        // (там 0.99 не «готово», а «не показывай 100% до финала»).
        var rx = new System.Text.RegularExpressions.Regex(@"\b0\.99\d*\b");
        int seen = 0;
        foreach (string path in System.IO.Directory.GetFiles(ModuleDir(), "*.cs"))
        {
            string name = System.IO.Path.GetFileName(path);
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                if (line.Contains("Math.Min(0.99,")) continue;
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(line))
                {
                    seen++;
                    Assert.True(m.Value == "0.999",
                        name + ": порог " + m.Value + " разошёлся с ProgressDone — «эта серия скачана?» станет вопросом с двумя ответами");
                }
            }
        }
        // сторож не должен стать пустым, если файлы переименуют или литерал вынесут в константу
        Assert.True(seen >= 15, "порог перестал встречаться в модуле (" + seen + ") — сторож ослеп");
    }

    static string ModuleDir()
    {
        // тесты бегут из bin/... — поднимаемся к корню репозитория
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Modules", "QbitDownload")))
            d = d.Parent;
        Assert.NotNull(d);
        return System.IO.Path.Combine(d.FullName, "Modules", "QbitDownload");
    }
}
