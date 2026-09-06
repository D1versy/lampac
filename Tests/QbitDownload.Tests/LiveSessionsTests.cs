using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.113 — склеенные сессии upload-камер (merge_service регистратора IPCamLive).
///
/// Что изменилось у регистратора: через ~5 мин после конца сессии Mac-рекордера все её чанки
/// склеиваются в ОДИН mp4 под id первого чанка (compression_preset 'original' → 'merged',
/// duration/end/size суммарные), строки и файлы остальных чанков УДАЛЯЮТСЯ (их id → 404),
/// каталоги /hls/_vod/ всех задетых id снесены. Файл до 6 ч и может пересекать полночь.
///
/// Что держат эти тесты:
///  • разбор preset и вычисление конца записи;
///  • два режима попадания в окно дня: старт-в-окне (дневной HLS RTSP — без дублей на стыке суток)
///    и пересечение (сессии — запись через полночь видна в обоих днях, секунды делятся);
///  • выбор режима дня: sessions только у upload-камеры, у которой ВСЕ записи склеены;
///  • сброс кэшей по 404 одного id — и когда камера известна, и когда нет.
/// </summary>
[Collection("LiveCaches")]
public class LiveSessionsTests : IDisposable
{
    static readonly TimeZoneInfo Msk =
        TimeZoneInfo.CreateCustomTimeZone("D1V-Test-Plus3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    // Локальные сутки 2026-09-06 (Msk) = [2026-09-05T21:00Z, 2026-09-06T21:00Z)
    static readonly (DateTime from, DateTime to) Day6 = LiveAccess.LiveDayWindow(new DateTime(2026, 9, 6), Msk);
    static readonly (DateTime from, DateTime to) Day5 = LiveAccess.LiveDayWindow(new DateTime(2026, 9, 5), Msk);

    public LiveSessionsTests() { LiveSessions.ClearAll(); }
    public void Dispose() { LiveSessions.ClearAll(); }

    // ══ разбор ═══════════════════════════════════════════════════════════

    [Fact]
    public void Merged_session_is_parsed_with_its_preset_and_end()
    {
        // Боевая строка камеры 6 за 2026-09-06 после склейки (ids 97, 99, 101, 103 удалены).
        var r = LiveAccess.ParseLiveRec(@"{""camera_id"":6,""filename"":""2026-09-06_10-27-01.mp4"",
            ""start_time"":""2026-09-06T10:27:01.820554"",""end_time"":""2026-09-06T11:39:00.820554"",
            ""duration_seconds"":4319,""file_size_bytes"":2229571692,""compression_preset"":""merged"",
            ""trigger_type"":""continuous"",""id"":96}");

        Assert.Equal(96, r.Id);
        Assert.Equal("merged", r.Preset);
        Assert.True(r.IsMerged);
        Assert.Equal(4319, r.Seconds);
        Assert.Equal(new DateTime(2026, 9, 6, 11, 39, 0, DateTimeKind.Utc).AddMilliseconds(820.554).Date, r.EndUtc.Date);
        Assert.Equal(r.StartUtc.AddSeconds(4319), r.EndUtc);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_but_merged_is_a_plain_chunk(string preset)
    {
        var j = new JObject { ["id"] = 1, ["camera_id"] = 6, ["start_time"] = "2026-09-06T10:00:00", ["duration_seconds"] = 600 };
        if (preset != null) j["compression_preset"] = preset;

        Assert.False(LiveAccess.ParseLiveRec(j).IsMerged);
    }

    [Fact]
    public void Preset_comparison_ignores_case()
    {
        Assert.True(LiveSessions.Rec(1, "2026-09-06T10:00:00", 60, "Merged").IsMerged);
    }

    // ══ окно дня ═════════════════════════════════════════════════════════

    // Сессия 2026-09-05 23:00 (Msk) → 04:00 06.09: 5 часов, через полночь.
    static LiveAccess.RecView CrossMidnight() => LiveSessions.Rec(96, "2026-09-05T20:00:00", 5 * 3600, "merged");

    [Fact]
    public void Start_in_window_puts_a_cross_midnight_session_only_into_the_day_it_started()
    {
        // Режим дневного HLS RTSP-камер: кусок принадлежит ровно одному дню, иначе на стыке суток
        // он звучал бы дважды (прежнее поведение — не менялось).
        var r = CrossMidnight();

        Assert.True(LiveSessions.LiveInWindow(r, Day5.from, Day5.to, overlap: false));
        Assert.False(LiveSessions.LiveInWindow(r, Day6.from, Day6.to, overlap: false));
    }

    [Fact]
    public void Overlap_shows_a_cross_midnight_session_in_BOTH_days()
    {
        // Режим сессий: зритель, открывший «сегодня», обязан увидеть запись, которая шла ночью.
        var r = CrossMidnight();

        Assert.True(LiveSessions.LiveInWindow(r, Day5.from, Day5.to, overlap: true));
        Assert.True(LiveSessions.LiveInWindow(r, Day6.from, Day6.to, overlap: true));
    }

    [Fact]
    public void Overlap_does_not_leak_into_a_day_the_session_never_touched()
    {
        var r = CrossMidnight();
        var (from, to) = LiveAccess.LiveDayWindow(new DateTime(2026, 9, 7), Msk);

        Assert.False(LiveSessions.LiveInWindow(r, from, to, overlap: true));
        Assert.False(LiveSessions.LiveInWindow(r, Day5.from.AddDays(-1), Day5.from, overlap: true));
    }

    [Fact]
    public void Session_ending_exactly_at_midnight_belongs_only_to_the_day_before()
    {
        // Полуинтервал [start, end): конец в 21:00Z — это ещё 5-е, 6-го из неё нет ни секунды.
        var r = LiveSessions.Rec(1, "2026-09-05T19:00:00", 2 * 3600, "merged");

        Assert.True(LiveSessions.LiveInWindow(r, Day5.from, Day5.to, overlap: true));
        Assert.False(LiveSessions.LiveInWindow(r, Day6.from, Day6.to, overlap: true));
    }

    [Fact]
    public void A_recording_still_in_progress_counts_by_its_start_in_both_modes()
    {
        // duration=0 — запись ещё пишется; конца нет, но старт в окне — значит она дня.
        var r = LiveSessions.Rec(1, "2026-09-05T21:00:00", 0);   // ровно на границе from

        Assert.True(LiveSessions.LiveInWindow(r, Day6.from, Day6.to, overlap: false));
        Assert.True(LiveSessions.LiveInWindow(r, Day6.from, Day6.to, overlap: true));
        Assert.False(LiveSessions.LiveInWindow(r, Day5.from, Day5.to, overlap: true));
    }

    [Fact]
    public void Null_record_is_never_in_a_window()
    {
        Assert.False(LiveSessions.LiveInWindow(null, Day6.from, Day6.to, overlap: true));
        Assert.Equal(0, LiveSessions.LiveSecondsIn(null, Day6.from, Day6.to));
    }

    [Fact]
    public void Seconds_of_a_cross_midnight_session_are_split_between_the_two_days()
    {
        var r = CrossMidnight();   // 23:00 → 04:00 Msk: 1 ч пятого, 4 ч шестого

        Assert.Equal(1 * 3600, LiveSessions.LiveSecondsIn(r, Day5.from, Day5.to));
        Assert.Equal(4 * 3600, LiveSessions.LiveSecondsIn(r, Day6.from, Day6.to));
        Assert.Equal(r.Seconds, LiveSessions.LiveSecondsIn(r, Day5.from, Day5.to) + LiveSessions.LiveSecondsIn(r, Day6.from, Day6.to));
    }

    [Fact]
    public void Seconds_inside_the_day_are_the_whole_duration()
    {
        var r = LiveSessions.Rec(96, "2026-09-06T10:27:01", 4319, "merged");
        Assert.Equal(4319, LiveSessions.LiveSecondsIn(r, Day6.from, Day6.to));
        Assert.Equal(0, LiveSessions.LiveSecondsIn(r, Day5.from, Day5.to));
    }

    [Fact]
    public void Local_day_asks_the_recorder_for_two_utc_dates()
    {
        // Регистратор фильтрует by-date по СВОЕЙ (UTC) дате с 6-часовым lookback (recordings.py:
        // _MAX_SEGMENT_LOOKBACK = max(2×segment, MERGE_MAX_DURATION_S)), то есть отдаёт и записи,
        // начавшиеся до полуночи и залезшие в дату. Наше окно лежит внутри объединения двух UTC-дат,
        // поэтому любая запись, пересекающая окно, придёт хотя бы в одном из двух ответов.
        var dates = LiveSessions.LiveUtcDates(Day6.from, Day6.to);

        Assert.Equal(new[] { new DateTime(2026, 9, 5), new DateTime(2026, 9, 6) }, dates);
    }

    [Fact]
    public void Utc_zone_day_is_a_single_utc_date()
    {
        var (from, to) = LiveAccess.LiveDayWindow(new DateTime(2026, 9, 6), TimeZoneInfo.Utc);
        Assert.Equal(new[] { new DateTime(2026, 9, 6) }, LiveSessions.LiveUtcDates(from, to));
    }

    // ══ JSON записи для клиента ══════════════════════════════════════════

    [Fact]
    public void Record_json_marks_a_session_that_started_yesterday_and_ends_tomorrow()
    {
        var r = CrossMidnight();

        var yesterday = LiveSessions.LiveRecJson(r, Day5.from, Day5.to, Msk);
        Assert.Null(yesterday["prevDay"]);
        Assert.True((bool)yesterday["nextDay"]);
        Assert.Equal("23:00", (string)yesterday["start"]);
        Assert.Equal("04:00", (string)yesterday["end"]);

        var today = LiveSessions.LiveRecJson(r, Day6.from, Day6.to, Msk);
        Assert.True((bool)today["prevDay"]);
        Assert.Null(today["nextDay"]);
        Assert.Equal("merged", (string)today["preset"]);
        Assert.True((bool)today["merged"]);
    }

    [Fact]
    public void Record_json_inside_the_day_carries_no_day_markers_and_a_default_preset()
    {
        var r = LiveSessions.Rec(5, "2026-09-06T10:00:00", 600, "");
        var j = LiveSessions.LiveRecJson(r, Day6.from, Day6.to, Msk);

        Assert.Null(j["prevDay"]);
        Assert.Null(j["nextDay"]);
        Assert.Equal("original", (string)j["preset"]);
        Assert.False((bool)j["merged"]);
        Assert.Equal(5, (int)j["id"]);
    }

    // ══ режим дня ════════════════════════════════════════════════════════

    [Fact]
    public void Upload_camera_with_every_record_merged_plays_sessions_directly()
    {
        Assert.Equal("sessions", LiveSessions.LiveDayMode("upload",
            LiveSessions.Rec(96, "2026-09-06T10:27:01", 4319, "merged")));
    }

    [Fact]
    public void One_unmerged_chunk_keeps_the_whole_day_on_HLS()
    {
        // Сессия ещё пишется (или воркер склейки до неё не дошёл): 'original' чанки рядом со
        // склеенной утренней — фолбек на прежний сшитый день, как и просил регистратор.
        Assert.Equal("day", LiveSessions.LiveDayMode("upload",
            LiveSessions.Rec(96, "2026-09-06T10:27:01", 4319, "merged"),
            LiveSessions.Rec(105, "2026-09-06T15:00:00", 1000, "original")));
    }

    [Theory]
    [InlineData("rtsp")]
    [InlineData("onvif")]
    [InlineData("")]
    public void RTSP_cameras_stay_on_HLS_even_if_a_record_says_merged(string protocol)
    {
        // Никаких изменений для RTSP-камер — это требование задачи, а не побочный эффект.
        Assert.Equal("day", LiveSessions.LiveDayMode(protocol,
            LiveSessions.Rec(1, "2026-09-06T10:00:00", 600, "merged")));
    }

    [Fact]
    public void Empty_day_or_unknown_camera_is_day_mode()
    {
        Assert.Equal("day", LiveSessions.LiveDayMode("upload"));
        Assert.Equal("day", LiveSessions.LiveDayMode(null, LiveSessions.Rec(1, "2026-09-06T10:00:00", 600, "merged")));
    }

    // ══ сброс кэшей по 404 ═══════════════════════════════════════════════

    static readonly DateTime U5 = new DateTime(2026, 9, 5), U6 = new DateTime(2026, 9, 6);

    void SeedTwoCameras()
    {
        // Камера 6 (upload): день 06.09 из чанков 96+97 (ещё не склеенных), камера 3 (rtsp) — своя.
        LiveSessions.SeedByDate(6, U5, LiveSessions.Rec(90, "2026-09-05T18:00:00", 1000));
        LiveSessions.SeedByDate(6, U6, LiveSessions.Rec(96, "2026-09-06T10:27:01", 1000), LiveSessions.Rec(97, "2026-09-06T10:43:41", 1000));
        LiveSessions.SeedByDate(3, U6, LiveSessions.Rec(500, "2026-09-06T10:00:00", 600, camera: 3));
        LiveSessions.SeedDayBuild(6, "2026-09-06", 96, 97);
        LiveSessions.SeedDayBuild(6, "2026-09-05", 90);
        LiveSessions.SeedDayBuild(3, "2026-09-06", 500);
        LiveSessions.SeedPts(97, 126000);
        LiveSessions.SeedPts(500, 90000);
        LiveSessions.SeedFeed("0|30");
    }

    [Fact]
    public void Forgetting_a_known_record_drops_every_cache_of_ITS_camera_and_nothing_of_others()
    {
        SeedTwoCameras();
        LiveSessions.SeedRecCam(97, 6);

        LiveSessions.LiveForgetRec(97);

        // камера 6 — всё: и обе UTC-даты, и обе дневные сборки (по камере, не только по id)
        Assert.False(LiveSessions.HasByDate(6, U5));
        Assert.False(LiveSessions.HasByDate(6, U6));
        Assert.False(LiveSessions.HasDayBuild(6, "2026-09-06"));
        Assert.False(LiveSessions.HasDayBuild(6, "2026-09-05"));
        Assert.False(LiveSessions.HasPts(97));
        // камера 3 не трогается
        Assert.True(LiveSessions.HasByDate(3, U6));
        Assert.True(LiveSessions.HasDayBuild(3, "2026-09-06"));
        Assert.True(LiveSessions.HasPts(500));
        // лента хранит id всех камер — сбрасывается целиком
        Assert.False(LiveSessions.HasFeed("0|30"));
    }

    [Fact]
    public void Forgetting_an_UNKNOWN_record_still_drops_the_caches_that_contain_it()
    {
        // Камера неизвестна (реестр rec→camera пуст после рестарта): ищем id по содержимому.
        SeedTwoCameras();

        LiveSessions.LiveForgetRec(97);

        Assert.False(LiveSessions.HasByDate(6, U6), "список by-date с этим id");
        Assert.False(LiveSessions.HasDayBuild(6, "2026-09-06"), "дневная сборка с этим id");
        Assert.True(LiveSessions.HasByDate(6, U5), "соседняя дата той же камеры без id — остаётся");
        Assert.True(LiveSessions.HasDayBuild(6, "2026-09-05"));
        Assert.True(LiveSessions.HasByDate(3, U6));
        Assert.True(LiveSessions.HasDayBuild(3, "2026-09-06"));
        Assert.False(LiveSessions.HasPts(97));
        Assert.True(LiveSessions.HasPts(500));
    }

    [Fact]
    public void Forgetting_zero_or_a_never_seen_id_is_harmless()
    {
        SeedTwoCameras();

        LiveSessions.LiveForgetRec(0);
        Assert.True(LiveSessions.HasByDate(6, U6));
        Assert.True(LiveSessions.HasFeed("0|30"));

        LiveSessions.LiveForgetRec(424242);
        Assert.True(LiveSessions.HasByDate(6, U6));
        Assert.True(LiveSessions.HasDayBuild(6, "2026-09-06"));
        Assert.True(LiveSessions.HasPts(97));
        Assert.False(LiveSessions.HasFeed("0|30"), "лента сбрасывается на любой 404 — она дешёвая и хранит чужие id");
    }

    [Fact]
    public void ByDate_cache_key_is_camera_plus_utc_date()
    {
        Assert.Equal("6|2026-09-06", LiveSessions.ByDateKey(6, new DateTime(2026, 9, 6, 15, 0, 0)));
    }
}
