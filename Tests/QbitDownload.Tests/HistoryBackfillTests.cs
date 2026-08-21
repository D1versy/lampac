using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Разовый бэкфилл «Истории просмотров» (qdl 2.61).
///
/// Покрываем ровно то, чья ошибка НЕОБРАТИМА или невидима глазом: разбор ведра (промах = чужой
/// тайтл в истории), нормализацию сериала (без неё вход из истории открывает объект TMDB из
/// другого пространства идентификаторов) и слияние (без идемпотентности повтор ручки размножил бы
/// записи, а без «только history/card» разовая операция стала бы способом потерять закладки).
/// </summary>
public class HistoryBackfillTests
{
    static JObject Card(object id, string title = "t") => new JObject { ["id"] = JToken.FromObject(id), ["title"] = title };

    // ── разбор ведра ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("270603_tv", "tv", "270603")]
    [InlineData("1315772_movie", "movie", "1315772")]
    [InlineData("qdl_t270603", "tmdb", "270603")]
    [InlineData("qdl_jut:joutai-ijou-skill", "jut", "joutai-ijou-skill")]
    public void Bucket_parsed(string bucket, string kind, string value)
    {
        var r = QbitController.ParseHistoryBucket(bucket);
        Assert.Equal(kind, r.kind);
        Assert.Equal(value, r.value);
    }

    [Fact]
    public void Bucket_infohash_parsed()
    {
        var r = QbitController.ParseHistoryBucket("qdl_" + new string('A', 40));
        Assert.Equal("hash", r.kind);
        Assert.Equal(new string('a', 40), r.value);   // приводим к нижнему регистру: имя файла меты
    }

    [Theory]
    [InlineData("0_movie")]          // вырожденное ведро: карточки в activity не было
    [InlineData("0_tv")]
    [InlineData("qdl_t0")]
    [InlineData("qdl_l1a2b3c4d")]    // ключ по ссылке раздачи — обратно не разворачивается
    [InlineData("qdl_jut:")]
    [InlineData("qdl_jut:../etc")]   // слаг обязан пройти валидатор jut
    [InlineData("qdl_short")]
    [InlineData("")]
    [InlineData(null)]
    public void Bucket_rejected(string bucket)
    {
        var r = QbitController.ParseHistoryBucket(bucket);
        Assert.Null(r.kind);
    }

    // ── карточка jut ──────────────────────────────────────────────────────

    [Fact]
    public void Jut_card_carries_slug_in_id_and_is_not_tmdb()
    {
        var c = QbitController.HistoryJutCard("joutai-ijou-skill", "Статус-скилл");

        Assert.Equal("jut:joutai-ijou-skill", c.Value<string>("id"));
        // 🔴 source ≠ cub/tmdb — иначе сканер рекомендаций Lampa пошёл бы в TMDB по мёртвому id
        Assert.Equal("jutsu", c.Value<string>("source"));
        Assert.Equal("Статус-скилл", c.Value<string>("title"));
        // корневой относительный путь: одна строка обязана работать на LAN, на tv и на реплике
        Assert.StartsWith("/qdl/jut/poster?slug=", c.Value<string>("img"));
    }

    [Fact]
    public void Jut_card_falls_back_to_slug_as_title()
        => Assert.Equal("some-slug", QbitController.HistoryJutCard("some-slug", null).Value<string>("title"));

    // ── нормализация сериала ──────────────────────────────────────────────

    [Fact]
    public void Tv_card_gets_name_fields()
    {
        // slim-card «Загрузок» полей сериала не несёт вовсе — только media_type
        var c = QbitController.HistoryNormalizeCard(new JObject
        {
            ["id"] = 270603,
            ["media_type"] = "tv",
            ["title"] = "Укрытие",
            ["original_title"] = "Silo",
            ["release_date"] = "2023-05-05"
        });

        // без name/original_name роутер Lampa открыл бы сериал как ФИЛЬМ (чужой объект TMDB)
        Assert.Equal("Укрытие", c.Value<string>("name"));
        Assert.Equal("Silo", c.Value<string>("original_name"));
        // first_air_date нужен Favorite.continues, чтобы отличить сериал в ряду «Продолжить»
        Assert.Equal("2023-05-05", c.Value<string>("first_air_date"));
    }

    [Fact]
    public void Movie_card_stays_movie()
    {
        var c = QbitController.HistoryNormalizeCard(new JObject
        {
            ["id"] = 1315772,
            ["media_type"] = "movie",
            ["title"] = "Фильм",
            ["release_date"] = "2025-01-01"
        });

        Assert.Null(c.Value<string>("name"));            // иначе роутер решил бы, что это сериал
        Assert.Null(c.Value<string>("first_air_date"));
    }

    [Fact]
    public void Normalize_does_not_mutate_source()
    {
        var src = new JObject { ["id"] = 1, ["media_type"] = "tv", ["title"] = "X" };
        QbitController.HistoryNormalizeCard(src);
        Assert.Null(src.Value<string>("name"));          // карточку нам дают из активности — не портим
    }

    // ── слияние ───────────────────────────────────────────────────────────

    [Fact]
    public void Merge_puts_fresh_first_and_keeps_existing_tail()
    {
        var data = new JObject { ["history"] = new JArray(7L, 8L), ["card"] = new JArray(Card(7), Card(8)) };
        int added = QbitController.MergeHistoryInto(data, new List<JObject> { Card(1), Card(7) }, 100);

        Assert.Equal(1, added);                          // 7 уже была
        Assert.Equal(new[] { "1", "7", "8" }, data["history"].Select(t => t.ToString()));
    }

    [Fact]
    public void Merge_is_idempotent()
    {
        var data = new JObject();
        var cards = new List<JObject> { Card(1), Card(2) };

        Assert.Equal(2, QbitController.MergeHistoryInto(data, cards, 100));
        string first = data.ToString();

        Assert.Equal(0, QbitController.MergeHistoryInto(data, cards, 100));
        Assert.Equal(first, data.ToString());
    }

    [Fact]
    public void Merge_touches_only_history_and_card()
    {
        var data = new JObject
        {
            ["like"] = new JArray(42L),
            ["book"] = new JArray(43L),
            ["history"] = new JArray()
        };

        QbitController.MergeHistoryInto(data, new List<JObject> { Card(1) }, 100);

        // разовая операция не должна становиться способом потерять закладки
        Assert.Equal(new[] { "42" }, data["like"].Select(t => t.ToString()));
        Assert.Equal(new[] { "43" }, data["book"].Select(t => t.ToString()));
    }

    [Fact]
    public void Merge_respects_cap()
    {
        var data = new JObject();
        var cards = Enumerable.Range(1, 10).Select(i => Card(i)).ToList();

        QbitController.MergeHistoryInto(data, cards, 4);

        Assert.Equal(4, ((JArray)data["history"]).Count);
        Assert.Equal(new[] { "1", "2", "3", "4" }, data["history"].Select(t => t.ToString()));
    }

    [Fact]
    public void Merge_keeps_numeric_ids_numeric_and_jut_ids_string()
    {
        var data = new JObject();
        QbitController.MergeHistoryInto(data, new List<JObject> { Card(270603), Card("jut:one-piece") }, 100);

        var hist = (JArray)data["history"];
        // тот же формат, что кладёт BookmarkController.AddToCategory — иначе id разъедутся при сравнении
        Assert.Equal(JTokenType.Integer, hist[0].Type);
        Assert.Equal(JTokenType.String, hist[1].Type);
    }

    [Fact]
    public void Merge_prefers_fresh_card_object_but_keeps_local_only_ones()
    {
        var data = new JObject
        {
            ["history"] = new JArray(1L, 9L),
            ["card"] = new JArray(Card(1, "старое"), Card(9, "чужое"))
        };

        QbitController.MergeHistoryInto(data, new List<JObject> { Card(1, "свежее") }, 100);

        var cards = ((JArray)data["card"]).OfType<JObject>().ToList();
        Assert.Equal("свежее", cards.First(c => c.Value<string>("id") == "1").Value<string>("title"));
        Assert.Contains(cards, c => c.Value<string>("id") == "9");   // местные карточки не выкидываем
    }

    [Fact]
    public void Merge_survives_garbage_shapes()
    {
        // строка вместо массива у истории и мусор в card — на боевом это чужой формат, не повод падать
        var data = new JObject { ["history"] = "not-an-array", ["card"] = 5 };
        QbitController.MergeHistoryInto(data, new List<JObject> { Card(1) }, 100);

        Assert.Equal(new[] { "1" }, data["history"].Select(t => t.ToString()));
    }

    // ── время ─────────────────────────────────────────────────────────────

    [Fact]
    public void Time_unparsable_is_oldest()
        => Assert.Equal(System.DateTime.MinValue, QbitController.HistoryParseTime("не дата"));

    [Fact]
    public void Time_ef_format_parsed()
        => Assert.Equal(2026, QbitController.HistoryParseTime("2026-08-16 16:36:16.1795326").Year);

    [Fact]
    public void Jut_watch_time_is_iso_string_on_disk()
    {
        // 🔴 На боевом это ISO-8601, а не unix: попытка прочесть его как long роняла разбор ФАЙЛА
        // ЦЕЛИКОМ, и устройство, смотревшее только аниме, оставалось с пустой историей.
        var jo = JObject.Parse(@"{""at"":""2026-08-20T08:56:52.7622051Z""}");
        Assert.Equal(2026, QbitController.HistoryJutAt(jo["at"]).Year);
        Assert.Equal(8, QbitController.HistoryJutAt(jo["at"]).Month);
    }

    [Fact]
    public void Jut_watch_time_accepts_unix_seconds_too()
        => Assert.Equal(2020, QbitController.HistoryJutAt(JToken.FromObject(1600000000L)).Year);

    [Fact]
    public void Jut_watch_time_garbage_is_oldest()
    {
        Assert.Equal(System.DateTime.MinValue, QbitController.HistoryJutAt(null));
        Assert.Equal(System.DateTime.MinValue, QbitController.HistoryJutAt(JToken.FromObject("мусор")));
    }
}
