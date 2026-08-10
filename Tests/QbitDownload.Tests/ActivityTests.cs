using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// «Актуальность» карточки (сортировка «Загрузок» по последнему событию загрузки):
/// чистая CardActivity + персистентное хранилище activity.json (Touch/Migrate/Remove/Prune).
/// </summary>
public class ActivityTests
{
    const string H1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string H2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const long Now = 2_000_000_000;

    static string ActFile() => Path.Combine(ModInit.conf.cachePath, "activity.json");

    // ── CardActivity: чистая функция ──────────────────────────────────────

    [Fact]
    public void CardActivity_without_events_falls_back_to_added()
    {
        Assert.Equal(300, Access.CardActivity(300, 0, 0.5, 0, Now));
    }

    [Theory]
    [InlineData(0.5, 4294967295L)]   // качается: u32-мусор qBit
    [InlineData(1.0, -1)]            // «докачан», но completion_on битый
    [InlineData(1.0, 0)]
    [InlineData(1.0, Now + 86401)]   // из будущего дальше суток — битое значение
    public void CardActivity_ignores_garbage_completion(double progress, long completionOn)
    {
        Assert.Equal(300, Access.CardActivity(300, completionOn, progress, 0, Now));
    }

    [Fact]
    public void CardActivity_completion_lifts_finished_download()
    {
        // раздача добавлена в 300, докачалась в 500 → актуальность 500 («аниме докачалось» всплывает)
        Assert.Equal(500, Access.CardActivity(300, 500, 1.0, 0, Now));
    }

    [Fact]
    public void CardActivity_stored_touch_beats_completion_and_added()
    {
        Assert.Equal(700, Access.CardActivity(300, 500, 1.0, 700, Now));
        // и наоборот: старый touch не тянет вниз
        Assert.Equal(500, Access.CardActivity(300, 500, 1.0, 100, Now));
    }

    // ── Touch: персист + монотонность ─────────────────────────────────────

    [Fact]
    public void Touch_persists_and_is_monotonic()
    {
        TestEnv.FreshCache();
        Access.ActivityTouch(H1, 100);
        Assert.Equal(100, Access.ActivityLoad().Value<long?>(H1));

        Access.ActivityTouch(H1, 50);    // запоздавший штамп не откатывает
        Assert.Equal(100, Access.ActivityLoad().Value<long?>(H1));

        Access.ActivityTouch(H1, 200);
        Assert.Equal(200, Access.ActivityLoad().Value<long?>(H1));
    }

    [Fact]
    public void Touch_uppercase_hash_lands_lowercase()
    {
        TestEnv.FreshCache();
        Access.ActivityTouch(H1.ToUpperInvariant(), 100);
        Assert.Equal(100, Access.ActivityLoad().Value<long?>(H1));
    }

    [Fact]
    public void Touch_invalid_hash_is_noop()
    {
        TestEnv.FreshCache();
        Access.ActivityTouch("not-a-hash", 100);
        Access.ActivityTouch("", 100);
        Access.ActivityTouch(null, 100);
        Assert.False(File.Exists(ActFile()));
    }

    [Fact]
    public void Broken_activity_json_does_not_throw_and_gets_rewritten()
    {
        TestEnv.FreshCache();
        File.WriteAllText(ActFile(), "{broken json!!");
        Assert.Empty(Access.ActivityLoad());       // битый файл → пустой объект

        Access.ActivityTouch(H1, 100);             // и штатно перезаписывается
        Assert.Equal(100, Access.ActivityLoad().Value<long?>(H1));
    }

    // ── Migrate / Remove ──────────────────────────────────────────────────

    [Fact]
    public void Migrate_moves_stamp_with_max_and_drops_old()
    {
        TestEnv.FreshCache();
        Access.ActivityTouch(H1, 100);
        Access.ActivityTouch(H2, 200);
        Access.ActivityMigrate(H1, H2);            // у нового штамп свежее → остаётся 200
        var a = Access.ActivityLoad();
        Assert.Null(a[H1]);
        Assert.Equal(200, a.Value<long?>(H2));

        TestEnv.FreshCache();
        Access.ActivityTouch(H1, 300);
        Access.ActivityTouch(H2, 200);
        Access.ActivityMigrate(H1, H2);            // у старого свежее → переезжает 300
        a = Access.ActivityLoad();
        Assert.Null(a[H1]);
        Assert.Equal(300, a.Value<long?>(H2));
    }

    [Fact]
    public void MigrateCache_carries_activity()     // регрессия: re-grab/switch не теряет штамп
    {
        TestEnv.FreshCache();
        Access.ActivityTouch(H1, 400);
        Access.MigrateCache(H1, H2);
        var a = Access.ActivityLoad();
        Assert.Null(a[H1]);
        Assert.Equal(400, a.Value<long?>(H2));
    }

    [Fact]
    public void PurgeCache_removes_key()
    {
        TestEnv.FreshCache();
        Access.ActivityTouch(H1, 100);
        Access.ActivityTouch(H2, 200);
        Access.PurgeCache(H1);
        var a = Access.ActivityLoad();
        Assert.Null(a[H1]);
        Assert.Equal(200, a.Value<long?>(H2));     // чужой ключ не задет
    }

    // ── Prune ─────────────────────────────────────────────────────────────

    [Fact]
    public void Prune_drops_stale_orphans_and_keeps_fresh_and_live()
    {
        TestEnv.FreshCache();
        const string H3 = "cccccccccccccccccccccccccccccccccccccccc";
        Access.ActivityTouch(H1, Now - 8 * 86400);   // сирота старше грейса (7 суток) → удалить
        Access.ActivityTouch(H2, Now - 3600);        // сирота, но свежая → оставить (грейс)
        Access.ActivityTouch(H3, Now - 30 * 86400);  // старый штамп, но карточка ЖИВА → оставить

        Access.ActivityPrune(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { H3 }, Now);

        var a = Access.ActivityLoad();
        Assert.Null(a[H1]);
        Assert.Equal(Now - 3600, a.Value<long?>(H2));
        Assert.Equal(Now - 30 * 86400, a.Value<long?>(H3));
    }
}
