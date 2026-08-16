using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Сервер-реплика: отбор по бюджету, выбор пути доставки (swarm/мост) и гарды удаления.
///
/// Почему именно эти три вещи покрыты тестами, а не «цикл целиком»: они единственные, чья
/// ошибка НЕОБРАТИМА. Неверный отбор жжёт домашний аплинк, неверный выбор пути светит passkey
/// на втором адресе, а дырявый гард удаляет фильмы на машине, до которой ещё надо доехать.
/// Всё остальное в цикле — сетевые вызовы, и они лечатся повтором следующего тика.
/// </summary>
public class ReplicaTests
{
    const long GiB = 1024L * 1024 * 1024;
    const long Now = 2_000_000_000;

    static QbitController.ReplicaItem It(string hash, long sizeGb, long activity, long added = 0)
        => new QbitController.ReplicaItem
        {
            hash = hash,
            name = hash,
            size = sizeGb * GiB,
            activity = activity,
            added = added == 0 ? activity : added
        };

    // ── отбор по бюджету ──────────────────────────────────────────────────

    [Fact]
    public void Plan_takes_freshest_first()
    {
        var all = new[] { It("a", 10, 100), It("b", 10, 300), It("c", 10, 200) };
        var plan = QbitController.ReplicaPlan(all, 100 * GiB, 85, 40);

        Assert.Equal(new[] { "b", "c", "a" }, plan.Select(x => x.hash));
    }

    [Fact]
    public void Plan_stops_at_low_watermark_not_at_budget()
    {
        // бюджет 100 ГБ, нижняя ватерлиния 85% → в план входит 8 файлов по 10 ГБ, девятый нет
        var all = Enumerable.Range(0, 12).Select(i => It("h" + i, 10, 1000 - i)).ToArray();
        var plan = QbitController.ReplicaPlan(all, 100 * GiB, 85, 100);

        Assert.Equal(8, plan.Count);
        Assert.Equal(80 * GiB, plan.Sum(x => x.size));
    }

    [Fact]
    public void Plan_skips_oversized_item_and_keeps_going()
    {
        // 🔥 Ради этого случая отбор и вынесен в чистую функцию: жадный обход, который на первом
        // же огромном элементе ОСТАНАВЛИВАЛСЯ БЫ, не набрал бы вообще ничего — реплика стояла бы
        // пустой и молчала. Кап на элемент 40% от 100 ГБ = 40 ГБ.
        var all = new[] { It("huge", 60, 900), It("ok1", 10, 800), It("ok2", 10, 700) };
        var plan = QbitController.ReplicaPlan(all, 100 * GiB, 85, 40);

        Assert.DoesNotContain(plan, x => x.hash == "huge");
        Assert.Equal(new[] { "ok1", "ok2" }, plan.Select(x => x.hash));
    }

    [Fact]
    public void Plan_fills_the_tail_with_smaller_items()
    {
        // не влез — пропускаем и пробуем следующий: остаток добивается мелкими,
        // иначе 85% бюджета простаивали бы из-за одного неудачно вставшего элемента
        var all = new[] { It("big", 80, 900), It("nope", 30, 800), It("small", 5, 700) };
        var plan = QbitController.ReplicaPlan(all, 100 * GiB, 85, 100);

        Assert.Equal(new[] { "big", "small" }, plan.Select(x => x.hash));
    }

    [Fact]
    public void Plan_ignores_zero_sized_items()
    {
        var all = new[] { It("zero", 0, 900), It("ok", 1, 100) };
        var plan = QbitController.ReplicaPlan(all, 100 * GiB, 85, 100);

        Assert.Single(plan);
        Assert.Equal("ok", plan[0].hash);
    }

    // ── swarm или мост ────────────────────────────────────────────────────

    [Fact]
    public void Bridge_always_for_private()
    {
        TestEnv.EnsureConf();
        var it = It("a", 1, 1); it.priv = true; it.numComplete = 50;
        Assert.True(QbitController.ShouldBridge(it));
    }

    [Fact]
    public void Bridge_when_home_is_the_only_seed()
    {
        TestEnv.EnsureConf();
        ModInit.conf.replicaBridgeWhenOnlyHomeSeeds = true;

        // 1 сид = только дом → swarm-загрузка съела бы домашний аплинк мимо шейпера
        var only = It("a", 1, 1); only.priv = false; only.numComplete = 1;
        Assert.True(QbitController.ShouldBridge(only));

        var none = It("b", 1, 1); none.priv = false; none.numComplete = 0;
        Assert.True(QbitController.ShouldBridge(none));

        var many = It("c", 1, 1); many.priv = false; many.numComplete = 7;
        Assert.False(QbitController.ShouldBridge(many));
    }

    [Fact]
    public void Bridge_not_used_when_seed_count_unknown()
    {
        // трекер не ответил (-1). Отправлять всё неизвестное в мост нельзя — он узкий,
        // и одна молчащая скрейп-статистика утащила бы туда весь поток.
        TestEnv.EnsureConf();
        ModInit.conf.replicaBridgeWhenOnlyHomeSeeds = true;

        var unknown = It("a", 1, 1); unknown.priv = false; unknown.numComplete = -1;
        Assert.False(QbitController.ShouldBridge(unknown));
    }

    [Fact]
    public void Bridge_killswitch_keeps_public_in_swarm()
    {
        TestEnv.EnsureConf();
        ModInit.conf.replicaBridgeWhenOnlyHomeSeeds = false;

        var only = It("a", 1, 1); only.priv = false; only.numComplete = 1;
        Assert.False(QbitController.ShouldBridge(only));
    }

    // ── границы удаления ──────────────────────────────────────────────────

    [Fact]
    public void Inside_downloads_rejects_root_and_escapes()
    {
        TestEnv.EnsureConf();
        string root = Path.Combine(Path.GetTempPath(), "qdl-dl-root");
        ModInit.conf.downloadsPath = root;

        Assert.True(QbitController.ReplicaInsideDownloads(Path.Combine(root, "film.mkv")));
        Assert.True(QbitController.ReplicaInsideDownloads(Path.Combine(root, "sub", "film.mkv")));

        // сам корень — никогда: снести его целиком означало бы снести медиатеку одной строкой
        Assert.False(QbitController.ReplicaInsideDownloads(root));
        Assert.False(QbitController.ReplicaInsideDownloads(Path.Combine(root, "..", "other.mkv")));
        Assert.False(QbitController.ReplicaInsideDownloads(null));
    }

    [Fact]
    public void PathTouches_detects_shared_folders()
    {
        string a = Path.Combine(Path.GetTempPath(), "x", "season1");
        Assert.True(QbitController.PathTouches(a, a));
        Assert.True(QbitController.PathTouches(Path.Combine(Path.GetTempPath(), "x"), a));
        Assert.False(QbitController.PathTouches(a, Path.Combine(Path.GetTempPath(), "x", "season2")));
    }

    // ── гарды ротации ─────────────────────────────────────────────────────

    static JObject Torrent(string cpath, double progress = 1.0, long addedOn = 1, string cat = null)
        => new JObject
        {
            ["name"] = "t",
            ["size"] = 5L * GiB,
            ["progress"] = progress,
            ["added_on"] = addedOn,
            ["category"] = cat ?? ModInit.conf.category,
            ["content_path"] = cpath
        };

    static bool MayEvict(JObject t, JObject played = null, long residence = 3600)
    {
        var mine = new Dictionary<string, JObject> { ["h"] = t };
        return QbitController.ReplicaMayEvict("h", false, mine, new Dictionary<string, JObject>(),
            played ?? new JObject(), Now, residence, 24 * 3600, out _);
    }

    [Fact]
    public void Evict_allowed_for_a_settled_finished_torrent()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        Assert.True(MayEvict(Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"))));
    }

    [Fact]
    public void Evict_blocked_while_still_downloading()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        Assert.False(MayEvict(Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"), progress: 0.4)));
    }

    [Fact]
    public void Evict_blocked_for_foreign_category()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        // на машине живут чужие compose-проекты; их раздачи не наши ни при каком бюджете
        Assert.False(MayEvict(Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"), cat: "other")));
    }

    [Fact]
    public void Evict_blocked_outside_downloads_path()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        Assert.False(MayEvict(Torrent(Path.Combine(Path.GetTempPath(), "elsewhere", "film.mkv"))));
        Assert.False(MayEvict(Torrent(null)));
    }

    [Fact]
    public void Evict_blocked_for_fresh_arrival()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        // приехало 10 минут назад при резиденции в час: иначе граничный элемент качался бы
        // и вычищался бесконечно, сжигая домашний аплинк
        var t = Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"), addedOn: Now - 600);
        Assert.False(MayEvict(t, residence: 3600));
    }

    [Fact]
    public void Evict_blocked_for_recently_played()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        // 🔥 Домашний activity про местный просмотр ничего не знает: выдернуть фильм из-под
        // зрителя — худшее, что может сделать бекап-сервер.
        var played = new JObject { ["h"] = Now - 3600 };
        Assert.False(MayEvict(Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv")), played));

        var old = new JObject { ["h"] = Now - 48 * 3600 };
        Assert.True(MayEvict(Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv")), old));
    }

    // ── засев рядов прогрева ──────────────────────────────────────────────

    [Fact]
    public void Warm_rows_are_reseeded_under_the_replica_host()
    {
        TestEnv.FreshCache();

        int n = CatalogWarmup.ImportRowPaths(
            new[] { "/cub/tmdb.red/3/discover/movie?page=1", "/cub/tmdb.red/3/discover/tv?page=1" },
            "https", "tv2.d1versy.com:9443");

        Assert.Equal(2, n);

        // Экспорт отдаёт ПУТИ без хоста — именно они и переносятся: ключ Staticache считается
        // из Scheme+Host+Path+Query, поэтому чужие файлы кеша бесполезны, а список — нет.
        var rows = CatalogWarmup.ExportRowPaths();
        Assert.Contains("/cub/tmdb.red/3/discover/movie?page=1", rows);
        Assert.Contains("/cub/tmdb.red/3/discover/tv?page=1", rows);
    }

    // ── история просмотров: перенос дом → реплика ─────────────────────────

    [Fact]
    public void History_newer_wins_and_local_progress_is_not_clobbered()
    {
        // 🔥 Ради этого правила перенос и односторонний: человек досмотрел серию ЗДЕСЬ, через
        // пять минут пришёл тик с домашней (старой) позицией — кино не должно откатываться.
        Assert.True(QbitController.HistoryNewer("2026-08-16 16:36:16.1795326", "2026-08-16 09:32:18.0987660"));
        Assert.False(QbitController.HistoryNewer("2026-08-16 09:32:18.0987660", "2026-08-16 16:36:16.1795326"));

        // равные — не трогаем (лишняя запись на каждом тике даром)
        Assert.False(QbitController.HistoryNewer("2026-08-16 16:36:16.1795326", "2026-08-16 16:36:16.1795326"));
    }

    [Fact]
    public void History_missing_local_record_is_always_newer()
    {
        Assert.True(QbitController.HistoryNewer("2026-08-16 16:36:16.1795326", null));
        Assert.True(QbitController.HistoryNewer("2026-08-16 16:36:16.1795326", ""));
    }

    [Fact]
    public void History_empty_remote_never_overwrites()
    {
        Assert.False(QbitController.HistoryNewer(null, "2026-08-16 16:36:16.1795326"));
        Assert.False(QbitController.HistoryNewer("", "2026-08-16 16:36:16.1795326"));
    }

    [Fact]
    public void History_unparsable_timestamps_fall_back_to_ordinal()
    {
        // формат мог смениться апстримом; молча «всё свежее» тут было бы худшим исходом
        Assert.True(QbitController.HistoryNewer("zzz", "aaa"));
        Assert.False(QbitController.HistoryNewer("aaa", "zzz"));
    }

    [Fact]
    public void History_blob_path_rejects_traversal()
    {
        // 🔴 rel приходит по сети: единственная защита от «дома», который притворился домом
        Assert.Null(QbitController.HistoryBlobPath("../../init.conf"));
        Assert.Null(QbitController.HistoryBlobPath("..\\..\\init.conf"));
        Assert.Null(QbitController.HistoryBlobPath("syncview/../../../etc/passwd"));
        Assert.Null(QbitController.HistoryBlobPath(""));
        Assert.Null(QbitController.HistoryBlobPath(null));

        var ok = QbitController.HistoryBlobPath("syncview/1d/4a6a0d26033d495ed22adc8ba99962");
        Assert.NotNull(ok);
        Assert.Contains("syncview", ok);
    }

    [Fact]
    public void Warm_rows_ignore_garbage()
    {
        TestEnv.FreshCache();

        // не путь (нет ведущего слэша) — молча пропускаем, а не сеем мусор в прогрев
        int n = CatalogWarmup.ImportRowPaths(new[] { "http://evil/x", "", null, "/cub/ok?page=1" },
            "https", "tv2.d1versy.com:9443");

        Assert.Equal(1, n);
    }
}
