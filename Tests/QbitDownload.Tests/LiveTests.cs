using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared;
using Shared.Models.AppConf;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// D1versy Live/Rec — прокси домашнего видеорегистратора (Modules/QbitDownload/Live.cs, 1453 строки).
///
/// Файл был единственным в модуле вне сборки тестов: csproj утверждал, что он «тянет за собой
/// контроллер». Проверка показала обратное — линкуется чисто, вся сьюта осталась зелёной.
///
/// Таймзоны строим программно (CreateCustomTimeZone), а не берём из базы ОС: тест обязан давать
/// один и тот же вердикт на любой машине, включая контейнер сборки.
/// </summary>
public class LiveTests
{
    /// <summary>Фиксированный +03:00 без перевода часов — «домашняя» зона регистратора.</summary>
    static readonly TimeZoneInfo Msk =
        TimeZoneInfo.CreateCustomTimeZone("D1V-Test-Plus3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    /// <summary>
    /// Зона, где переход на летнее время происходит В ПОЛНОЧЬ, — ровно тот случай, ради которого
    /// в LiveToUtc стоит гард IsInvalidTime: локальной полуночи в этот день не существует.
    /// </summary>
    static readonly TimeZoneInfo MidnightDst = BuildMidnightDstZone();

    static TimeZoneInfo BuildMidnightDstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 29);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 25);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);

        return TimeZoneInfo.CreateCustomTimeZone(
            "D1V-Test-MidnightDST", TimeSpan.FromHours(1), "M-DST", "M-DST-STD", "M-DST-DST", new[] { rule });
    }

    // ══ наивный UTC регистратора ══════════════════════════════════════════

    [Fact]
    public void Naive_timestamp_is_read_as_UTC_not_as_local_time()
    {
        // 🔴 Сердце раздела: регистратор пишет время БЕЗ зоны, и это UTC.
        // Прочитать его как локальное — сдвиг всей ленты на смещение зоны.
        Assert.True(LiveAccess.TryLiveUtc("2026-07-26T16:39:09.563288", out var utc));

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 7, 26, 16, 39, 9, DateTimeKind.Utc), utc.AddTicks(-utc.Ticks % TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void Timestamp_WITH_an_offset_is_converted_to_UTC()
    {
        Assert.True(LiveAccess.TryLiveUtc("2026-07-26T19:39:09+03:00", out var utc));

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 7, 26, 16, 39, 9, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Trailing_Z_is_UTC_too()
    {
        Assert.True(LiveAccess.TryLiveUtc("2026-07-26T16:39:09Z", out var utc));
        Assert.Equal(new DateTime(2026, 7, 26, 16, 39, 9, DateTimeKind.Utc), utc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не дата")]
    [InlineData("2026-13-45T99:99:99")]
    public void Garbage_timestamps_are_rejected_rather_than_defaulted(string s)
    {
        Assert.False(LiveAccess.TryLiveUtc(s, out _));
    }

    // ══ локальные сутки → UTC-окно ════════════════════════════════════════

    [Fact]
    public void A_local_day_spans_TWO_utc_dates()
    {
        // Именно из-за этого нельзя фильтровать записи по подстроке даты в UTC-строке:
        // вечер 20-го UTC — это уже 21-е по локальному времени.
        var (from, to) = LiveAccess.LiveDayWindow(new DateTime(2026, 8, 21), Msk);

        Assert.Equal(new DateTime(2026, 8, 20, 21, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 8, 21, 21, 0, 0, DateTimeKind.Utc), to);
        Assert.NotEqual(from.Date, to.Date);
    }

    [Fact]
    public void Day_window_is_exactly_24_hours_outside_dst()
    {
        var (from, to) = LiveAccess.LiveDayWindow(new DateTime(2026, 8, 21), Msk);
        Assert.Equal(TimeSpan.FromHours(24), to - from);
    }

    [Fact]
    public void Midnight_that_does_not_exist_does_not_throw()
    {
        // 🔴 В зонах с переходом в полночь ConvertTimeToUtc бросает ArgumentException.
        // Гард IsInvalidTime сдвигает на час — иначе раздел падал бы раз в год.
        var day = new DateTime(2026, 3, 29);
        Assert.True(MidnightDst.IsInvalidTime(day), "проверочная зона обязана иметь дыру в полночь");

        var utc = LiveAccess.LiveToUtc(day, MidnightDst);

        // Сдвиг ровно на час вперёд: 00:00 не существует → берём 01:00.
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 3, 29, 1, 0, 0), MidnightDst), utc);
    }

    [Fact]
    public void Day_window_survives_the_dst_gap()
    {
        var ex = Record.Exception(() => LiveAccess.LiveDayWindow(new DateTime(2026, 3, 29), MidnightDst));
        Assert.Null(ex);
    }

    [Fact]
    public void Kind_of_the_incoming_local_date_is_ignored()
    {
        // day приходит из TryLiveDay/LiveToday с разным Kind; SpecifyKind(Unspecified) обязан
        // защитить от «локальное время уже локальное» — иначе двойная конвертация.
        var asUtc = DateTime.SpecifyKind(new DateTime(2026, 8, 21), DateTimeKind.Utc);
        var asLocal = DateTime.SpecifyKind(new DateTime(2026, 8, 21), DateTimeKind.Local);

        Assert.Equal(LiveAccess.LiveToUtc(asUtc, Msk), LiveAccess.LiveToUtc(asLocal, Msk));
    }

    // ══ разбор параметра date ═════════════════════════════════════════════

    [Fact]
    public void Empty_date_means_today_in_the_recorder_zone()
    {
        Assert.True(LiveAccess.TryLiveDay("", Msk, out var day));
        Assert.Equal(LiveAccess.LiveToday(Msk), day);
    }

    [Fact]
    public void Explicit_date_is_parsed_as_a_local_calendar_day()
    {
        Assert.True(LiveAccess.TryLiveDay("2026-08-21", Msk, out var day));
        Assert.Equal(new DateTime(2026, 8, 21), day);
    }

    [Theory]
    [InlineData("21.08.2026")]
    [InlineData("2026/08/21")]
    [InlineData("2026-8-21")]
    [InlineData("вчера")]
    public void Only_the_strict_ISO_form_is_accepted(string s)
    {
        // Свободный парс пустил бы сюда локаль машины и разъехался бы между хостом и контейнером.
        Assert.False(LiveAccess.TryLiveDay(s, Msk, out _));
    }

    [Fact]
    public void DayKey_is_stable_and_culture_independent()
    {
        Assert.Equal("2026-08-21", LiveAccess.LiveDayKey(new DateTime(2026, 8, 21)));
    }

    // ══ подписи дней ══════════════════════════════════════════════════════

    [Fact]
    public void Recent_days_get_words_instead_of_dates()
    {
        var today = new DateTime(2026, 8, 21);

        Assert.Equal("Сегодня", LiveAccess.LiveDayLabel(today, today));
        Assert.Equal("Вчера", LiveAccess.LiveDayLabel(today.AddDays(-1), today));
        Assert.Equal("Позавчера", LiveAccess.LiveDayLabel(today.AddDays(-2), today));
    }

    [Fact]
    public void Older_days_get_a_russian_date_with_a_weekday()
    {
        // 18 августа 2026 — вторник.
        Assert.Equal("18 августа, вт", LiveAccess.LiveDayLabel(new DateTime(2026, 8, 18), new DateTime(2026, 8, 21)));
    }

    [Fact]
    public void Day_label_ignores_the_time_component()
    {
        var today = new DateTime(2026, 8, 21, 23, 59, 0);
        var day = new DateTime(2026, 8, 21, 0, 0, 1);
        Assert.Equal("Сегодня", LiveAccess.LiveDayLabel(day, today));
    }

    [Fact]
    public void All_twelve_months_and_seven_weekdays_are_covered()
    {
        // Канарейка на массивы-словари: обрезанный массив дал бы IndexOutOfRange в проде.
        var today = new DateTime(2027, 1, 1);
        for (int m = 1; m <= 12; m++)
        {
            var ex = Record.Exception(() => LiveAccess.LiveDayLabel(new DateTime(2026, m, 15), today));
            Assert.Null(ex);
        }
        for (int d = 0; d < 7; d++)
        {
            var ex = Record.Exception(() => LiveAccess.LiveDayLabel(new DateTime(2026, 8, 16).AddDays(d), today));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Time_is_rendered_in_the_recorder_zone_not_in_UTC()
    {
        string s = LiveAccess.LiveTime(new DateTime(2026, 8, 21, 16, 39, 0, DateTimeKind.Utc), Msk);
        Assert.Equal("19:39", s);
    }

    // ══ имена сегментов: анти-traversal ═══════════════════════════════════

    [Theory]
    [InlineData("seg_0.ts")]
    [InlineData("seg_1.ts")]
    [InlineData("seg_123456.ts")]
    public void Valid_segment_names_pass(string name)
    {
        Assert.Matches(LiveAccess.SegRx, name);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../seg_1.ts")]
    [InlineData("seg_1.ts/../..")]
    [InlineData("seg_.ts")]
    [InlineData("seg_1234567.ts")]      // семь цифр — за пределами {1,6}
    [InlineData("seg_1.mp4")]
    [InlineData("index.m3u8")]
    [InlineData("seg_1.ts\n")]
    [InlineData("")]
    public void Hostile_or_unexpected_segment_names_are_rejected(string name)
    {
        Assert.DoesNotMatch(LiveAccess.SegRx, name);
    }

    [Theory]
    [InlineData("seg_0.ts", true)]
    [InlineData("seg_1234567890.m4s", true)]
    [InlineData("init.mp4", true)]
    [InlineData("../init.mp4", false)]
    [InlineData("seg_1.mkv", false)]
    [InlineData("init.mp4.exe", false)]
    public void Watch_segment_names_allow_fmp4_but_not_traversal(string name, bool ok)
    {
        Assert.Equal(ok, LiveAccess.WatchSegRx.IsMatch(name));
    }

    [Theory]
    [InlineData("/hls/_vod/12/seg_3.ts", "seg_3.ts")]
    [InlineData("seg_3.ts", "seg_3.ts")]
    [InlineData("http://host/a/b/seg_9.ts", "seg_9.ts")]
    [InlineData("/", "")]
    public void Segment_name_is_taken_after_the_last_slash(string line, string expected)
    {
        Assert.Equal(expected, LiveAccess.LiveSegName(line));
    }

    // ══ разбор записи регистратора ════════════════════════════════════════

    [Fact]
    public void Recording_is_parsed_into_our_normalised_shape()
    {
        var r = LiveAccess.ParseLiveRec(@"{
            ""id"": 4211, ""camera_id"": 6,
            ""start_time"": ""2026-08-21T10:00:00"",
            ""duration_seconds"": 600,
            ""file_size_bytes"": 123456789,
            ""trigger_type"": ""motion""
        }");

        Assert.Equal(4211, r.Id);
        Assert.Equal(6, r.Camera);
        Assert.Equal(new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc), r.StartUtc);
        Assert.Equal(600, r.Seconds);
        Assert.Equal(123456789L, r.Size);
        Assert.Equal("motion", r.Trigger);
    }

    [Fact]
    public void Missing_duration_is_computed_from_end_time()
    {
        // duration_seconds заполняется только при закрытии сегмента — у последней записи
        // за сутки его штатно нет, и без этой ветки день обрывался бы раньше времени.
        var r = LiveAccess.ParseLiveRec(@"{
            ""id"": 1, ""camera_id"": 6,
            ""start_time"": ""2026-08-21T10:00:00"",
            ""end_time"":   ""2026-08-21T10:05:30""
        }");

        Assert.Equal(330, r.Seconds);
    }

    [Fact]
    public void End_time_before_start_time_does_not_produce_a_negative_duration()
    {
        var r = LiveAccess.ParseLiveRec(@"{
            ""id"": 1, ""start_time"": ""2026-08-21T10:00:00"", ""end_time"": ""2026-08-21T09:00:00""
        }");

        Assert.Equal(0, r.Seconds);
    }

    [Fact]
    public void Duration_unknown_stays_zero_rather_than_guessing()
    {
        var r = LiveAccess.ParseLiveRec(@"{ ""id"": 1, ""start_time"": ""2026-08-21T10:00:00"" }");
        Assert.Equal(0, r.Seconds);
    }

    [Fact]
    public void Trigger_defaults_to_continuous()
    {
        var r = LiveAccess.ParseLiveRec(@"{ ""id"": 1, ""start_time"": ""2026-08-21T10:00:00"" }");
        Assert.Equal("continuous", r.Trigger);
    }

    [Theory]
    [InlineData(@"{ ""id"": 0, ""start_time"": ""2026-08-21T10:00:00"" }")]
    [InlineData(@"{ ""id"": -5, ""start_time"": ""2026-08-21T10:00:00"" }")]
    [InlineData(@"{ ""id"": 1 }")]
    [InlineData(@"{ ""id"": 1, ""start_time"": ""мусор"" }")]
    [InlineData(@"[]")]
    [InlineData(@"""строка""")]
    public void Unusable_recordings_are_dropped_not_half_parsed(string json)
    {
        // Ветвление UI по остатку — известный класс багов: половинчатая запись доехала бы
        // до клиента и нарисовалась пустой карточкой.
        Assert.True(LiveAccess.ParseLiveRec(json).IsNull);
    }

    [Fact]
    public void Null_token_is_dropped()
    {
        Assert.True(LiveAccess.ParseLiveRec((JToken)null).IsNull);
    }

    // ══ подпись сегментных строк ключом периметра ═════════════════════════

    sealed class Conf : IDisposable
    {
        readonly D1vConf _prevD1v;
        readonly bool? _prevPerms;

        public Conf(string cookieName = "d1v", bool? permsEnabled = null)
        {
            TestEnv.EnsureConf();
            _prevD1v = CoreInit.conf.d1v;
            _prevPerms = ModInit.conf.permsEnabled;
            CoreInit.conf.d1v = new D1vConf { enable = true, cookieName = cookieName };
            if (permsEnabled != null) ModInit.conf.permsEnabled = permsEnabled.Value;
        }

        public void Dispose()
        {
            CoreInit.conf.d1v = _prevD1v;
            if (_prevPerms != null) ModInit.conf.permsEnabled = _prevPerms.Value;
        }
    }

    const string Key = "aaaabbbbccccdddd1111222233334444aaaabbbbccccdddd1111222233334444";

    /// <summary>Плейлист ровно той формы, что строит LiveDayBuild: смешанные плейсхолдеры.</summary>
    const string Playlist =
        "#EXTM3U\n" +
        "#EXT-X-PLAYLIST-TYPE:VOD\n" +
        "#EXTINF:4.0,\n/qdl/live/seg/11/seg_0.ts?o=0{&d1v}\n" +
        "#EXT-X-DISCONTINUITY\n" +
        "#EXTINF:4.0,\n/qdl/live/seg/12/seg_0.ts{?d1v}\n" +
        "#EXT-X-ENDLIST\n";

    [Fact]
    public void Shifted_segments_get_an_AMPERSAND_not_a_second_question_mark()
    {
        // 🔴 Тот самый баг: «?o=123?d1v=…» — второй знак вопроса уходит в ИМЯ параметра,
        // ключ не распознаётся, и снаружи каждый сегмент ловит 404. В LAN при этом всё работает,
        // поэтому дефект незаметен до первого просмотра из интернета.
        using var _ = new Conf();
        var c = LiveAccess.Controller(query: "?d1v=" + Key);

        string signed = LiveAccess.LiveSignDay(c, Playlist);

        Assert.Contains("/qdl/live/seg/11/seg_0.ts?o=0&d1v=" + Key, signed);
        Assert.Contains("/qdl/live/seg/12/seg_0.ts?d1v=" + Key, signed);
        Assert.DoesNotContain("?o=0?d1v=", signed);
    }

    [Fact]
    public void Both_placeholders_disappear_completely_after_signing()
    {
        // Утёкший плейсхолдер плеер отправит на сервер как есть — 404 на каждом сегменте.
        using var _ = new Conf();
        var c = LiveAccess.Controller(query: "?d1v=" + Key, uid: "device-1");

        string signed = LiveAccess.LiveSignDay(c, Playlist);

        Assert.DoesNotContain("{?d1v}", signed);
        Assert.DoesNotContain("{&d1v}", signed);
    }

    [Fact]
    public void In_LAN_without_a_key_placeholders_are_removed_cleanly()
    {
        // Дома ключа нет вовсе — сегментные строки обязаны остаться валидными URI,
        // а не получить висящий «?» или «&».
        using var _ = new Conf();
        var c = LiveAccess.Controller();

        string signed = LiveAccess.LiveSignDay(c, Playlist);

        Assert.Contains("/qdl/live/seg/11/seg_0.ts?o=0\n", signed);
        Assert.Contains("/qdl/live/seg/12/seg_0.ts\n", signed);
        Assert.DoesNotContain("{", signed);
        Assert.DoesNotContain("?o=0&\n", signed);
    }

    [Fact]
    public void Signing_is_idempotent_in_shape_for_repeated_deliveries()
    {
        // Плейлист лежит в кэше С плейсхолдерами и подписывается на КАЖДОЙ отдаче —
        // подпись не должна накапливаться в кэшированной строке.
        using var _ = new Conf();
        var c = LiveAccess.Controller(query: "?d1v=" + Key);

        Assert.Equal(LiveAccess.LiveSignDay(c, Playlist), LiveAccess.LiveSignDay(c, Playlist));
    }

    [Fact]
    public void Device_uid_travels_in_the_segment_line_too()
    {
        // Без uid в самой строке гейт прав отдавал бы 404 на каждом сегменте уже начатого
        // просмотра: нативные плееры не несут ни заголовков, ни cookie на относительные URI.
        using var _ = new Conf();
        var c = LiveAccess.Controller(query: "?d1v=" + Key, uid: "mac-abc");

        string q = LiveAccess.LiveSegQuery(c);

        Assert.Contains("d1v=" + Key, q);
        Assert.Contains("uid=mac-abc", q);
        Assert.StartsWith("?", q);
    }

    [Fact]
    public void Segment_query_is_empty_when_there_is_neither_key_nor_uid()
    {
        using var _ = new Conf();
        Assert.Equal("", LiveAccess.LiveSegQuery(LiveAccess.Controller()));
    }

    [Fact]
    public void Uid_alone_still_produces_a_valid_query()
    {
        using var _ = new Conf();
        string q = LiveAccess.LiveSegQuery(LiveAccess.Controller(uid: "mac-abc"));

        Assert.Equal("?uid=mac-abc", q);
        Assert.DoesNotContain("&", q);
    }

    [Fact]
    public void Key_and_uid_are_percent_encoded()
    {
        // Ключ из init.conf теоретически может содержать что угодно; неэкранированный «&»
        // расщепил бы query и увёл бы половину ключа в чужой параметр.
        using var _ = new Conf();
        var c = LiveAccess.Controller(query: "?d1v=a%26b%3Dc", uid: "dev%20one");

        string q = LiveAccess.LiveSegQuery(c);

        Assert.Contains("d1v=a%26b%3Dc", q);
        Assert.DoesNotContain("d1v=a&b=c", q);
    }

    [Fact]
    public void Key_is_taken_from_the_query_first()
    {
        using var _ = new Conf();
        var c = LiveAccess.Controller(query: "?d1v=" + Key, cookie: "d1v=stale");

        Assert.Equal(Key, LiveAccess.LiveD1vKey(c));
    }

    [Fact]
    public void Key_falls_back_to_the_perimeter_cookie()
    {
        // Внутри WebView ключ живёт именно в cookie: сервер пересадил его при первой навигации.
        using var _ = new Conf();
        var c = LiveAccess.Controller(cookie: "d1v=" + Key);

        Assert.Equal(Key, LiveAccess.LiveD1vKey(c));
    }

    [Fact]
    public void Cookie_name_comes_from_conf_not_from_a_literal()
    {
        using var _ = new Conf(cookieName: "custom_perimeter");
        var c = LiveAccess.Controller(cookie: "custom_perimeter=" + Key);

        Assert.Equal(Key, LiveAccess.LiveD1vKey(c));
    }

    [Fact]
    public void No_key_anywhere_yields_null_not_an_empty_string()
    {
        using var _ = new Conf();
        Assert.Null(LiveAccess.LiveD1vKey(LiveAccess.Controller()));
    }

    [Fact]
    public void Empty_query_key_does_not_shadow_the_cookie()
    {
        // «?d1v=» (пустое значение) — не предъявление ключа; иначе клиент с пустым параметром
        // терял бы рабочую cookie.
        using var _ = new Conf();
        var c = LiveAccess.Controller(query: "?d1v=", cookie: "d1v=" + Key);

        Assert.Equal(Key, LiveAccess.LiveD1vKey(c));
    }

    // ══ гейт прав раздела ═════════════════════════════════════════════════

    [Theory]
    [InlineData(Perms.FeatureLive)]
    [InlineData(Perms.FeatureRec)]
    public void Killswitch_permsEnabled_false_opens_both_sections(string feature)
    {
        using var _ = new Conf(permsEnabled: false);
        Assert.False(LiveAccess.LiveDenied(LiveAccess.Controller(uid: "whoever"), feature));
    }

    [Theory]
    [InlineData(Perms.FeatureLive)]
    [InlineData(Perms.FeatureRec)]
    public void With_perms_on_an_unknown_device_is_denied(string feature)
    {
        // default deny: незнакомое устройство не видит разделы, даже из LAN.
        TestEnv.FreshCache();
        using var _ = new Conf(permsEnabled: true);

        Assert.True(LiveAccess.LiveDenied(LiveAccess.Controller(uid: "never-seen-device"), feature));
    }

    [Fact]
    public void A_request_without_uid_is_denied_when_perms_are_on()
    {
        TestEnv.FreshCache();
        using var _ = new Conf(permsEnabled: true);

        Assert.True(LiveAccess.LiveDenied(LiveAccess.Controller(), Perms.FeatureLive));
    }

    [Fact]
    public void Live_and_Rec_are_separate_features()
    {
        // Права выдаются по разделам отдельно — «эфир» не открывает «записи».
        Assert.NotEqual(Perms.FeatureLive, Perms.FeatureRec);
    }
}
