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

    // ── зеркалирование удалений: отбор сирот ──────────────────────────────
    //
    // 🔴 Самая дорогая ошибка в этом контуре — спутать targetSet (бюджетный план) с known
    // (всё, что есть у дома). Первое меньше второго втрое, и подмена превратила бы «не влезло
    // в 240 ГБ» в «удалить с диска». Поэтому отбор вынесен в чистую функцию и закрыт тестами.

    const string H1 = "1111111111111111111111111111111111111111";
    const string H2 = "2222222222222222222222222222222222222222";
    const string H3 = "3333333333333333333333333333333333333333";

    static Dictionary<string, JObject> Mine(params string[] hashes)
    {
        var d = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hashes) d[h] = new JObject { ["name"] = h, ["size"] = 5L * GiB };
        return d;
    }

    static HashSet<string> Known(params string[] hashes)
        => new HashSet<string>(hashes, StringComparer.OrdinalIgnoreCase);

    static List<(string hash, bool jut)> Orphans(Dictionary<string, JObject> mine, HashSet<string> known)
        => QbitController.ReplicaOrphanCandidates(mine, new Dictionary<string, JObject>(), known);

    [Fact]
    public void Hash_in_manifest_but_not_in_plan_is_not_an_orphan()
    {
        // 🔥 Регресс. У дома 598 ГБ, в план реплики влезает ~204 ГБ. Всё, что не влезло, ЖИВО
        // у дома и обязано остаться на диске до давления бюджета — сиротой оно не является.
        var mine = Mine(H1, H2);
        var orphans = Orphans(mine, Known(H1, H2));   // known накрывает оба, план тут ни при чём

        Assert.Empty(orphans);
    }

    [Fact]
    public void Transcoded_card_is_known_but_not_planned_and_is_not_an_orphan()
    {
        // 🔥 Боевой случай. Дом дотранскодил карточку: торрент удалён ВМЕСТЕ С ФАЙЛАМИ, а
        // local-маркер остался под тем же хешем — и в manifest.local не попал, потому что у
        // маркера транскода нет объекта jut. В torrents его тоже нет. Единственное, что держит
        // его живым для реплики, — присутствие в known.
        var mine = Mine(H1);
        Assert.Empty(Orphans(mine, Known(H1)));

        // а без known-покрытия он стал бы сиротой — цена ошибки: безвозвратная потеря копии,
        // потому что .torrent дом уже не отдаст и моста для файлов нет
        Assert.Single(Orphans(mine, Known(H2)));
    }

    [Fact]
    public void Orphan_is_a_hash_home_does_not_have_at_all()
    {
        var mine = Mine(H1, H2, H3);
        var orphans = Orphans(mine, Known(H1, H3));

        Assert.Single(orphans);
        Assert.Equal(H2, orphans[0].hash);
        Assert.False(orphans[0].jut);
    }

    [Fact]
    public void Empty_home_set_is_not_a_command_to_delete_everything()
    {
        // Дом с пустой библиотекой и дом, отдавший мусор, снаружи неразличимы,
        // а цена ошибки различается на три порядка.
        Assert.Empty(Orphans(Mine(H1, H2), Known()));
        Assert.Empty(Orphans(Mine(H1, H2), null));
    }

    [Fact]
    public void Missing_known_disables_mirroring()
    {
        var set = QbitController.ReplicaKnownSet(null, new[] { H1 }, out string why);

        Assert.Null(set);
        Assert.Contains("не отдаёт known", why);
    }

    [Fact]
    public void Known_that_does_not_cover_the_plan_is_rejected()
    {
        // Поле есть, но собрано не тем кодом: дом сам прислал H2 к репликации и сам же не
        // упомянул его в known. Доверять такому нельзя — пробел читался бы как «удалено».
        var set = QbitController.ReplicaKnownSet(new JArray(H1), new[] { H1, H2 }, out string why);

        Assert.Null(set);
        Assert.Contains("не покрывает", why);
    }

    [Fact]
    public void Known_is_parsed_case_insensitively_and_garbage_is_dropped()
    {
        var set = QbitController.ReplicaKnownSet(
            new JArray(H1.ToUpperInvariant(), "не-хеш", "", H2), new[] { H1 }, out string why);

        Assert.Null(why);
        Assert.Equal(2, set.Count);
        Assert.Contains(H1, set);
    }

    // ── тормоз массовости ─────────────────────────────────────────────────

    [Fact]
    public void Mass_brake_does_not_fire_on_a_small_set()
    {
        // 🔥 Боевой случай 21.08.2026: реплика держала 19 позиций, 6 из них — законные сироты
        // (4 старых хеша после перекачки дома + 2 удалённых руками). 31% при пороге 25%
        // блокировали проход целиком, и дубли не уходили. Один процент на десятках позиций
        // не работает: нужен ещё и абсолютный пол.
        Assert.False(QbitController.ReplicaOrphanBrake(6, 19, 25, 10, out _));
    }

    [Fact]
    public void Mass_brake_still_catches_a_wrong_snapshot()
    {
        // а вот когда осиротел почти весь набор — это уже не хвост, а не тот снимок дома
        Assert.True(QbitController.ReplicaOrphanBrake(19, 19, 25, 10, out string why));
        Assert.Contains("остановлено", why);

        Assert.True(QbitController.ReplicaOrphanBrake(30, 47, 25, 10, out _));
    }

    [Fact]
    public void Mass_brake_needs_both_conditions()
    {
        Assert.False(QbitController.ReplicaOrphanBrake(9, 10, 25, 10, out _));   // доля есть, числа нет
        Assert.False(QbitController.ReplicaOrphanBrake(10, 100, 25, 10, out _)); // число есть, доли нет
        Assert.True(QbitController.ReplicaOrphanBrake(10, 20, 25, 10, out _));   // оба
    }

    [Fact]
    public void Mass_brake_is_silent_when_there_is_nothing_to_delete()
    {
        Assert.False(QbitController.ReplicaOrphanBrake(0, 19, 25, 10, out _));
        Assert.False(QbitController.ReplicaOrphanBrake(0, 0, 25, 10, out _));
    }

    // ── подтверждение пропажи ─────────────────────────────────────────────

    static (List<string> ready, int pending) Confirm(JObject state, long now, params string[] missing)
        => QbitController.ReplicaOrphanConfirm(state, missing, now, 3, 15, out _);

    [Fact]
    public void Orphan_needs_both_ticks_and_minutes()
    {
        var state = new JObject();

        // три тика подряд, но прошло всего 10 минут — окно по стенным часам не закрыто
        Assert.Empty(Confirm(state, Now, H1).ready);
        Assert.Empty(Confirm(state, Now + 300, H1).ready);
        var third = Confirm(state, Now + 600, H1);
        Assert.Empty(third.ready);
        Assert.Equal(1, third.pending);

        // четвёртый тик — 15 минут набрались
        Assert.Equal(new[] { H1 }, Confirm(state, Now + 900, H1).ready);
    }

    [Fact]
    public void Orphan_needs_ticks_even_if_time_has_passed()
    {
        // 🔴 Одного времени мало: после долгого простоя (тик занят мостом, дом лежал) первый же
        // здоровый снимок иначе удалил бы всё разом.
        var state = new JObject();

        Assert.Empty(Confirm(state, Now, H1).ready);
        Assert.Empty(Confirm(state, Now + 100000, H1).ready);          // misses=2, времени вагон
        Assert.Equal(new[] { H1 }, Confirm(state, Now + 100001, H1).ready);
    }

    [Fact]
    public void Orphan_confirmation_resets_when_hash_returns()
    {
        var state = new JObject();
        Confirm(state, Now, H1);
        Confirm(state, Now + 300, H1);

        // хеш снова есть у дома → запись снимается целиком
        var back = QbitController.ReplicaOrphanConfirm(state, Array.Empty<string>(), Now + 600, 3, 15, out bool changed);
        Assert.Empty(back.ready);
        Assert.True(changed);
        Assert.Null(state[H1]);

        // пропал снова — счётчик и окно стартуют заново, а не досчитывают старое
        Confirm(state, Now + 900, H1);
        Assert.Equal(1, state[H1].Value<int>("misses"));
        Assert.Equal(Now + 900, state[H1].Value<long>("since"));
    }

    [Fact]
    public void Orphan_since_survives_restart()
    {
        // состояние поднимается с диска: второй тик не имеет права сдвигать точку отсчёта,
        // иначе окно подтверждения не закрылось бы никогда
        var state = new JObject { [H1] = new JObject { ["since"] = Now, ["misses"] = 2 } };

        Confirm(state, Now + 1200, H1);

        Assert.Equal(Now, state[H1].Value<long>("since"));
        Assert.Equal(3, state[H1].Value<int>("misses"));
    }

    [Fact]
    public void Orphan_state_prunes_hashes_we_no_longer_have()
    {
        // удалённые и снятые вручную записи не должны копиться вечно
        var state = new JObject { [H1] = new JObject { ["since"] = Now, ["misses"] = 9 } };

        QbitController.ReplicaOrphanConfirm(state, new[] { H2 }, Now + 60, 3, 15, out _);

        Assert.Null(state[H1]);
        Assert.NotNull(state[H2]);
    }

    // ── гарды сироты ──────────────────────────────────────────────────────

    static bool MayEvictOrphan(JObject t, out bool filesOk, JObject played = null, long grace = 1800)
    {
        var mine = new Dictionary<string, JObject> { ["h"] = t };
        return QbitController.ReplicaMayEvictOrphan("h", false, mine, new Dictionary<string, JObject>(),
            played ?? new JObject(), Now, grace, out filesOk, out _);
    }

    [Fact]
    public void Orphan_is_evicted_even_if_unfinished()
    {
        // 🔴 Контраст с бюджетным гардом: там незавершённое не трогаем, здесь недокачанный
        // огрызок удалённого не просто не нужен — он ещё и занимает канал.
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");
        var t = Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"), progress: 0.3);

        Assert.False(MayEvict(t));                       // бюджет: блокирует
        Assert.True(MayEvictOrphan(t, out bool filesOk)); // зеркало: пропускает
        Assert.True(filesOk);
    }

    [Fact]
    public void Orphan_is_evicted_even_if_fresh()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");
        var t = Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"), addedOn: Now - 60);

        Assert.False(MayEvict(t, residence: 24 * 3600));
        Assert.True(MayEvictOrphan(t, out _));
    }

    [Fact]
    public void Orphan_keeps_the_category_guard()
    {
        // чужие раздачи на той же машине не наши ни при каком основании
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        Assert.False(MayEvictOrphan(
            Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"), cat: "other"), out _));
    }

    [Fact]
    public void Orphan_outside_downloads_is_removed_without_files()
    {
        // 🔴 Отличие от бюджетного гарда: раздачу снять НАДО (иначе она вечно стучится в
        // трекер уже после удаления дома), но файлы за пределами downloadsPath — никогда.
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");
        var t = Torrent(Path.Combine(Path.GetTempPath(), "elsewhere", "film.mkv"));

        Assert.False(MayEvict(t));                        // бюджет: не трогаем вовсе
        Assert.True(MayEvictOrphan(t, out bool filesOk));  // зеркало: снимаем раздачу
        Assert.False(filesOk);                             // …но файлы оставляем
    }

    [Fact]
    public void Orphan_without_metadata_is_removed_without_files()
    {
        // магнет, у которого метаданные так и не приехали: content_path пуст или равен корню
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        Assert.True(MayEvictOrphan(Torrent(""), out bool f1));
        Assert.False(f1);

        Assert.True(MayEvictOrphan(Torrent(ModInit.conf.downloadsPath), out bool f2));
        Assert.False(f2);   // сам корень удалять нельзя никогда
    }

    [Fact]
    public void Orphan_recently_played_is_deferred_not_vetoed()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");
        var t = Torrent(Path.Combine(ModInit.conf.downloadsPath, "film.mkv"));

        // смотрят прямо сейчас — не выдёргиваем из-под зрителя
        Assert.False(MayEvictOrphan(t, out _, new JObject { ["h"] = Now - 720 }));

        // грейс истёк — уходит; счётчик подтверждений при этом не сбрасывался
        Assert.True(MayEvictOrphan(t, out _, new JObject { ["h"] = Now - 2400 }));

        // грейс 0 — гард выключен
        Assert.True(MayEvictOrphan(t, out _, new JObject { ["h"] = Now - 10 }, grace: 0));
    }

    [Fact]
    public void Orphan_local_marker_pointing_outside_keeps_files()
    {
        TestEnv.EnsureConf();
        ModInit.conf.downloadsPath = Path.Combine(Path.GetTempPath(), "qdl-dl-root");

        var loc = new JObject
        {
            ["name"] = "anime",
            ["size"] = 3L * GiB,
            ["files"] = new JArray(new JObject
            {
                ["index"] = 0,
                ["name"] = "ep1.mkv",
                ["path"] = Path.Combine(Path.GetTempPath(), "elsewhere", "ep1.mkv"),
                ["size"] = 3L * GiB
            })
        };

        bool ok = QbitController.ReplicaMayEvictOrphan("h", true,
            new Dictionary<string, JObject>(), new Dictionary<string, JObject> { ["h"] = loc },
            new JObject(), Now, 1800, out bool filesOk, out _);

        Assert.True(ok);
        Assert.False(filesOk);
    }

    // ── разбор ответа qBittorrent на добавление ───────────────────────────

    [Fact]
    public void Qbit_add_fails_body_is_not_a_success()
    {
        // 🔥 Ровно этим реплика и болела: `IsSuccessStatusCode || 409` читал 200 «Fails.» как
        // успех, печатал «поставлено в закачку» и молча ретраил добавление каждые пять минут.
        Assert.Equal(QbitAddStatus.Failed, QbitController.QbitAddOutcome(200, "Fails."));
    }

    [Fact]
    public void Qbit_add_outcome_matrix()
    {
        Assert.Equal(QbitAddStatus.Added, QbitController.QbitAddOutcome(200, "Ok."));
        Assert.Equal(QbitAddStatus.Added, QbitController.QbitAddOutcome(200, ""));
        Assert.Equal(QbitAddStatus.Added, QbitController.QbitAddOutcome(200, null));

        Assert.Equal(QbitAddStatus.Duplicate, QbitController.QbitAddOutcome(409, ""));
        Assert.Equal(QbitAddStatus.Duplicate, QbitController.QbitAddOutcome(200, "Conflict"));

        // qBit v5 отвечает JSON со счётчиками
        Assert.Equal(QbitAddStatus.Added, QbitController.QbitAddOutcome(200, "{\"success_count\":1}"));
        Assert.Equal(QbitAddStatus.Added, QbitController.QbitAddOutcome(200, "{\"pending_count\":1}"));
        Assert.Equal(QbitAddStatus.Duplicate, QbitController.QbitAddOutcome(200, "{\"duplicate_count\":1}"));
        Assert.Equal(QbitAddStatus.Failed, QbitController.QbitAddOutcome(200, "{\"failed_count\":1}"));

        Assert.Equal(QbitAddStatus.Failed, QbitController.QbitAddOutcome(403, "Ok."));
        Assert.Equal(QbitAddStatus.Failed, QbitController.QbitAddOutcome(500, ""));
    }

    // ── порядок бюджетного выселения ──────────────────────────────────────

    [Fact]
    public void Evict_order_prefers_activity_then_added_then_oldest()
    {
        // 🔴 Прежний код давал записи БЕЗ штампа активности long.MaxValue — она выселялась
        // последней, ровно наоборот канону. Но и «без штампа = самое старое» неточно:
        // собственная дата появления у кандидата почти всегда есть, и терять её незачем.
        Assert.Equal(500, QbitController.ReplicaEvictOrder(500, 100));   // активность важнее
        Assert.Equal(100, QbitController.ReplicaEvictOrder(0, 100));     // нет активности → дата
        Assert.Equal(0, QbitController.ReplicaEvictOrder(0, 0));         // нет ничего → первым
    }

    // ── применение закладок дома (qdl 2.61) ───────────────────────────────
    //
    // До 2.61 таблица bookmarks была пуста, и замена строки целиком была безобидной. С живой
    // историей она означала бы: всё, что посмотрели через tv2, стирается первым же домашним
    // обновлением. Инвариант «реплика не пишет в дом» при слиянии цел — поток остаётся односторонним.

    [Fact]
    public void Bookmarks_merge_keeps_what_was_watched_on_replica()
    {
        string home = @"{""history"":[1,2],""card"":[{""id"":1},{""id"":2}]}";
        string local = @"{""history"":[9],""card"":[{""id"":9}]}";

        var merged = JObject.Parse(QbitController.HistoryMergeBookmarks(local, home));

        // порядок ведёт дом (источник правды), местный хвост дописывается следом
        Assert.Equal(new[] { "1", "2", "9" }, merged["history"].Select(t => t.ToString()));
        Assert.Equal(3, ((JArray)merged["card"]).Count);
    }

    [Fact]
    public void Bookmarks_merge_prefers_home_card_object()
    {
        string home = @"{""history"":[1],""card"":[{""id"":1,""title"":""дом""}]}";
        string local = @"{""history"":[1],""card"":[{""id"":1,""title"":""реплика""}]}";

        var merged = JObject.Parse(QbitController.HistoryMergeBookmarks(local, home));

        Assert.Single((JArray)merged["card"]);
        Assert.Equal("дом", merged["card"][0].Value<string>("title"));
    }

    [Fact]
    public void Bookmarks_merge_keeps_local_only_categories()
    {
        string home = @"{""history"":[1]}";
        string local = @"{""like"":[42],""history"":[]}";

        var merged = JObject.Parse(QbitController.HistoryMergeBookmarks(local, home));

        Assert.Equal(new[] { "42" }, merged["like"].Select(t => t.ToString()));
        Assert.Equal(new[] { "1" }, merged["history"].Select(t => t.ToString()));
    }

    [Fact]
    public void Bookmarks_merge_falls_back_to_home_on_empty_or_broken_local()
    {
        string home = @"{""history"":[1]}";

        Assert.Equal(home, QbitController.HistoryMergeBookmarks(null, home));
        Assert.Equal(home, QbitController.HistoryMergeBookmarks("", home));
        Assert.Equal(home, QbitController.HistoryMergeBookmarks("не json", home));
    }

    [Fact]
    public void Bookmarks_merge_never_loses_local_when_home_is_empty()
    {
        // домашняя строка пустая/битая — местную не трогаем вовсе
        string local = @"{""history"":[9]}";
        Assert.Equal(local, QbitController.HistoryMergeBookmarks(local, null));
        Assert.Equal(local, QbitController.HistoryMergeBookmarks(local, ""));
    }
}
