using System;
using System.Collections.Generic;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// D1versy Live 2.95: видимость плиток сетки эфира и лента Detection
/// (Modules/QbitDownload/Live.cs + LiveDetect.cs).
///
/// Зоны строим программно (CreateCustomTimeZone), а не берём из базы ОС — тест обязан давать
/// один и тот же вердикт на любой машине, включая контейнер сборки.
/// </summary>
public class LiveDetectTests
{
    /// <summary>Фиксированный +03:00 без перевода часов — «домашняя» зона регистратора.</summary>
    static readonly TimeZoneInfo Msk =
        TimeZoneInfo.CreateCustomTimeZone("D1V-Test-Detect-Plus3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    // ── Глобальный тумблер «видео в плитках» (qdl 2.96) ──────────────────────
    //
    // Владелец: «настройки глобальны для всех — я у себя включил, и оно на все девайсы».
    // Поэтому значение серверное, как фильтр каталога, а не Lampa.Storage у устройства.
    // 🔴 Плитка «6 превью → 4» из 2.95 откачена в ту же ревизию: неактивные камеры снова едут
    // клиенту (он показывает их отдельным блоком снизу), поэтому серверного правила видимости
    // больше нет — и тестов на него тоже.

    [Fact]
    public void Без_файла_эфир_в_плитках_включён()
    {
        TestEnv.FreshCache();
        Assert.True(QbitController.LiveVideoConf.Enabled);
    }

    [Fact]
    public void Выключение_переживает_перечитывание()
    {
        TestEnv.FreshCache();
        QbitController.LiveVideoConf.Save(false);

        // 🔴 Именно false: значение по умолчанию — true, и «потерянная» запись читалась бы как
        // включённый эфир, то есть тумблер молча не работал бы ровно в одну сторону.
        Assert.False(QbitController.LiveVideoConf.Enabled);

        QbitController.LiveVideoConf.Save(true);
        Assert.True(QbitController.LiveVideoConf.Enabled);
    }

    [Fact]
    public void Значение_лежит_на_томе_отдельным_файлом()
    {
        TestEnv.FreshCache();
        QbitController.LiveVideoConf.Save(false);

        string file = System.IO.Path.Combine(ModInit.conf.cachePath, "live-video.json");
        Assert.True(System.IO.File.Exists(file), "ожидали live-video.json в cachePath");

        var root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(file));
        Assert.False((bool)root["video"]);
    }

    // ── Разбор события детектора ─────────────────────────────────────────────

    [Fact]
    public void Событие_разбирается_целиком()
    {
        var e = LiveAccess.ParseLiveEvt(@"{
            ""id"": 453801,
            ""camera_id"": 1,
            ""event_type"": ""human"",
            ""confidence"": 0.792550265789032,
            ""thumbnail_path"": ""/recordings/_thumbnails/1/human_2026-09-01_15-36-24.jpg"",
            ""recording_id"": 8812,
            ""timestamp"": ""2026-09-01T15:36:24.562593""
        }");

        Assert.False(e.IsNull);
        Assert.Equal(453801, e.Id);
        Assert.Equal(1, e.Camera);
        Assert.Equal("human", e.Kind);
        Assert.Equal(8812, e.Recording);
        Assert.True(e.HasThumb);
        Assert.InRange(e.Confidence, 0.79, 0.80);

        // Метка регистратора — наивный UTC (его контейнер поднят с TZ=UTC).
        Assert.Equal(DateTimeKind.Utc, e.StartUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 15, 36, 24, DateTimeKind.Utc), e.StartUtc.AddTicks(-e.StartUtc.Ticks % TimeSpan.TicksPerSecond));

        // …и клиенту уходит уже локальное время: 15:36 UTC = 18:36 по дому.
        var local = TimeZoneInfo.ConvertTimeFromUtc(e.StartUtc, Msk);
        Assert.Equal(18, local.Hour);
        Assert.Equal(36, local.Minute);
    }

    [Fact]
    public void Тип_события_нормализуется()
    {
        Assert.Equal("human", LiveAccess.ParseLiveEvt(@"{ ""id"":1, ""event_type"":""HUMAN"", ""timestamp"":""2026-09-01T10:00:00"" }").Kind);
        Assert.Equal("motion", LiveAccess.ParseLiveEvt(@"{ ""id"":1, ""event_type"":""motion"", ""timestamp"":""2026-09-01T10:00:00"" }").Kind);
        // незнакомый тип не должен утечь в CSS-класс плитки как есть
        Assert.Equal("motion", LiveAccess.ParseLiveEvt(@"{ ""id"":1, ""event_type"":""cat"", ""timestamp"":""2026-09-01T10:00:00"" }").Kind);
        Assert.Equal("motion", LiveAccess.ParseLiveEvt(@"{ ""id"":1, ""timestamp"":""2026-09-01T10:00:00"" }").Kind);
    }

    [Fact]
    public void Пустые_поля_не_роняют_разбор()
    {
        var e = LiveAccess.ParseLiveEvt(@"{ ""id"":7, ""camera_id"":3, ""event_type"":""motion"",
                                            ""confidence"":null, ""recording_id"":null,
                                            ""thumbnail_path"":null, ""timestamp"":""2026-09-01T10:00:00"" }");
        Assert.False(e.IsNull);
        Assert.Equal(0d, e.Confidence);
        Assert.Equal(0, e.Recording);
        Assert.False(e.HasThumb);
    }

    [Fact]
    public void Событие_без_id_или_с_битой_меткой_отбрасывается()
    {
        Assert.True(LiveAccess.ParseLiveEvt(@"{ ""camera_id"":1, ""timestamp"":""2026-09-01T10:00:00"" }").IsNull);
        Assert.True(LiveAccess.ParseLiveEvt(@"{ ""id"":0, ""timestamp"":""2026-09-01T10:00:00"" }").IsNull);
        Assert.True(LiveAccess.ParseLiveEvt(@"{ ""id"":5, ""timestamp"":""позавчера"" }").IsNull);
        Assert.True(LiveAccess.ParseLiveEvt(@"{ ""id"":5 }").IsNull);
        Assert.True(LiveAccess.ParseLiveEvt("[]").IsNull);
    }

    // ── Локальные сутки против UTC-суток регистратора ────────────────────────

    [Fact]
    public void Локальный_день_задевает_две_UTC_даты_регистратора()
    {
        // Фильтр `date` у /api/detection/events режет UTC-сутки, а день у нас локальный:
        // спрашивать одну дату — значит потерять события после 21:00 UTC (полночь по дому).
        var (from, to) = LiveAccess.LiveDayWindow(new DateTime(2026, 9, 1), Msk);

        var dates = new List<string>();
        for (var d = from.Date; d <= to.AddTicks(-1).Date; d = d.AddDays(1))
            dates.Add(LiveAccess.LiveDayKey(d));

        Assert.Equal(new[] { "2026-08-31", "2026-09-01" }, dates);

        // Граница окна: 21:00 UTC 31 августа — это уже 1 сентября по дому.
        Assert.Equal(new DateTime(2026, 8, 31, 21, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 9, 1, 21, 0, 0, DateTimeKind.Utc), to);
    }

    [Fact]
    public void Событие_попадает_в_локальный_день_а_не_в_UTC_день()
    {
        var (from, to) = LiveAccess.LiveDayWindow(new DateTime(2026, 9, 1), Msk);

        // 22:30 UTC 31 августа = 01:30 1 сентября по дому → это событие ДНЯ 1 сентября,
        // хотя у регистратора оно лежит под датой 2026-08-31.
        var e = LiveAccess.ParseLiveEvt(@"{ ""id"":11, ""camera_id"":1, ""event_type"":""human"",
                                            ""timestamp"":""2026-08-31T22:30:00"" }");
        Assert.InRange(e.StartUtc, from, to.AddTicks(-1));
        Assert.Equal(1, TimeZoneInfo.ConvertTimeFromUtc(e.StartUtc, Msk).Day);

        // А 20:30 UTC того же числа — ещё 31 августа по дому, в окно не входит.
        var before = LiveAccess.ParseLiveEvt(@"{ ""id"":12, ""timestamp"":""2026-08-31T20:30:00"" }");
        Assert.True(before.StartUtc < from);
    }

    // ── Курсор подгрузки ─────────────────────────────────────────────────────
    //
    // 🔥 Поймано живой проверкой на боевых данных, а не на глаз: в режиме дня спрашиваются ДВЕ
    // UTC-даты, и у «вчерашней» ленты id заведомо меньше. Курсор по сырому минимуму ответа
    // перепрыгивал через всё, что между: отдали 453934…453933, а курсор уехал на 451618 —
    // дыра в 2300 непоказанных событий.

    [Fact]
    public void Курсор_идёт_по_отданному_хвосту_а_не_по_сырому_минимуму()
    {
        // страница = два свежих события Сегодня, сырой минимум — из вчерашней UTC-даты
        Assert.Equal(453933, LiveAccess.LiveDetectCursor(new[] { 453934, 453933 }, rawMin: 451618));
    }

    [Fact]
    public void Пустая_страница_перешагивает_по_сырому_минимуму()
    {
        // Вся страница легла за окно локальных суток: отдавать нечего, но всё сырое уже
        // рассмотрено и отвергнуто — перешагнуть через него безопасно. Без этой ветки
        // лента вставала бы намертво.
        Assert.Equal(451618, LiveAccess.LiveDetectCursor(new int[0], rawMin: 451618));
    }

    [Fact]
    public void Совсем_пустой_ответ_даёт_нулевой_курсор_и_останавливает_ленту()
    {
        Assert.Equal(0, LiveAccess.LiveDetectCursor(new int[0], rawMin: 0));
    }

    // ── Уменьшёнка кадра ─────────────────────────────────────────────────────
    //
    // Контракт один: сбой обязан выродиться в «отдай оригинал» (null), а не в исключение —
    // иначе грид молча зарос бы битыми плитками там, где раньше просто работал.

    [Fact]
    public void Пустой_вход_просит_отдать_оригинал()
    {
        Assert.Null(LiveAccess.LiveThumbResize(null, 640));
        Assert.Null(LiveAccess.LiveThumbResize(new byte[0], 640));
    }

    [Fact]
    public void Мусор_вместо_картинки_не_бросает_исключение()
    {
        var junk = new byte[4096];
        for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i % 251);

        var ex = Record.Exception(() => LiveAccess.LiveThumbResize(junk, 640));
        Assert.Null(ex);
        Assert.Null(LiveAccess.LiveThumbResize(junk, 640));
    }
}
