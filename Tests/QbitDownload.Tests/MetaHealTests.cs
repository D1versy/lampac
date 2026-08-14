using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Восстановление карточки загрузки по infohash (MetaHeal.cs): слепок карточки из ответа TMDB,
/// указатель на раздачу и гарды фонового контура.
///
/// Контекст: карточку пишет клиент при «Скачать», а всё, что приехало мимо (login-трекеры до фикса
/// хеша, авто-контуры), оставалось безымянным. Здесь проверяется, что слепок совпадает по форме с
/// клиентским slimCard (его читают и грид, и полная карточка) и что фон не долбит БД/TMDB.
/// </summary>
public class MetaHealTests
{
    const string H = "23e2e66bd6ed853c82c731b1ee4e4a7ca5abf3e6";

    static JObject TvDetails() => JObject.Parse(@"{
        ""id"": 318354,
        ""name"": ""Холод"",
        ""original_name"": ""Холод"",
        ""overview"": ""Женя попадает в тюрьму…"",
        ""first_air_date"": ""2026-07-16"",
        ""poster_path"": ""/abc.jpg"",
        ""backdrop_path"": ""/bd.jpg"",
        ""vote_average"": 7.5,
        ""genres"": [{""id"":18,""name"":""драма""},{""id"":80,""name"":""криминал""}],
        ""episode_run_time"": [52],
        ""origin_country"": [""RU""],
        ""production_countries"": [{""iso_3166_1"":""RU"",""name"":""Россия""}],
        ""status"": ""Returning Series"",
        ""number_of_seasons"": 1,
        ""number_of_episodes"": 10
    }");

    static JObject MovieDetails() => JObject.Parse(@"{
        ""id"": 2787,
        ""title"": ""Чёрная дыра"",
        ""original_title"": ""Pitch Black"",
        ""overview"": ""Космический корабль терпит бедствие…"",
        ""release_date"": ""2000-02-18"",
        ""poster_path"": ""/pb.jpg"",
        ""runtime"": 109,
        ""genres"": [{""id"":878,""name"":""фантастика""}],
        ""production_countries"": [{""iso_3166_1"":""US"",""name"":""США""}],
        ""tagline"": ""Не бойся темноты"",
        ""status"": ""Released""
    }");

    // ── слепок карточки ───────────────────────────────────────────────────

    [Fact]
    public void SlimCard_tv_keeps_the_shape_client_writes()
    {
        var c = Access.BuildSlimCard(TvDetails(), tv: true);

        Assert.Equal(318354, c.Value<int>("id"));
        Assert.Equal("tv", c.Value<string>("media_type"));
        Assert.Equal("Холод", c.Value<string>("title"));            // у сериала TMDB отдаёт name
        Assert.Equal("Холод", c.Value<string>("original_title"));
        Assert.StartsWith("Женя попадает", c.Value<string>("overview"));
        Assert.Equal("2026-07-16", c.Value<string>("release_date"));
        Assert.Equal("2026", c.Value<string>("year"));              // год строкой — как в slimCard
        Assert.Equal("/abc.jpg", c.Value<string>("poster_path"));
        Assert.Equal(52, c.Value<int>("runtime"));                  // episode_run_time[0]
        Assert.Equal(1, c.Value<int>("number_of_seasons"));
        Assert.Equal(10, c.Value<int>("number_of_episodes"));
        Assert.Equal(new[] { "драма", "криминал" }, c["genres"].ToObject<string[]>());
        Assert.Equal(new[] { "Россия", "RU" }, c["countries"].ToObject<string[]>());
        Assert.Equal("index", c.Value<string>("source"));           // метка происхождения
    }

    [Fact]
    public void SlimCard_movie_uses_movie_fields()
    {
        var c = Access.BuildSlimCard(MovieDetails(), tv: false);

        Assert.Equal("movie", c.Value<string>("media_type"));
        Assert.Equal("Чёрная дыра", c.Value<string>("title"));
        Assert.Equal("Pitch Black", c.Value<string>("original_title"));
        Assert.Equal("2000", c.Value<string>("year"));
        Assert.Equal(109, c.Value<int>("runtime"));
        Assert.Equal("Не бойся темноты", c.Value<string>("tagline"));
        Assert.Equal(new[] { "США" }, c["countries"].ToObject<string[]>());
    }

    [Fact]
    public void SlimCard_survives_empty_details()
    {
        var c = Access.BuildSlimCard(new JObject { ["id"] = 1 }, tv: false);

        Assert.Equal(1, c.Value<int>("id"));
        Assert.Equal("", c.Value<string>("year"));
        Assert.Equal(0, c.Value<int>("runtime"));
        Assert.Empty(c["genres"]);
        Assert.Empty(c["countries"]);
    }

    // ── links/<hash>.json ─────────────────────────────────────────────────

    [Fact]
    public void HealLink_writes_pointer_with_tmdb_context()
    {
        TestEnv.FreshCache();
        var card = Access.BuildSlimCard(TvDetails(), tv: true);

        Access.HealLink(H, "http://127.0.0.1:9118/kinozal/parsemagnet?id=42", "холод", 2026, 2, card);

        string p = Path.Combine(ModInit.conf.cachePath, "links", H + ".json");
        Assert.True(File.Exists(p));

        var lj = JObject.Parse(File.ReadAllText(p));
        Assert.Equal("http://127.0.0.1:9118/kinozal/parsemagnet?id=42", lj.Value<string>("link"));
        Assert.Equal("Холод", lj.Value<string>("query"));
        Assert.Equal(2026, lj["ctx"].Value<int>("year"));
        Assert.Equal(2, lj["ctx"].Value<int>("is_serial"));
        Assert.Equal("Холод", lj["ctx"].Value<string>("title_original"));
    }

    [Fact]
    public void HealLink_never_overwrites_an_existing_pointer()
    {
        TestEnv.FreshCache();
        string dir = Path.Combine(ModInit.conf.cachePath, "links");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, H + ".json");
        File.WriteAllText(p, "{\"link\":\"свой\"}");

        Access.HealLink(H, "magnet:?xt=urn:btih:" + H, "холод", 2026, 2, null);

        Assert.Equal("свой", JObject.Parse(File.ReadAllText(p)).Value<string>("link"));
    }

    [Fact]
    public void HealLink_without_link_writes_nothing()
    {
        TestEnv.FreshCache();

        Access.HealLink(H, "", "холод", 2026, 2, null);

        Assert.False(File.Exists(Path.Combine(ModInit.conf.cachePath, "links", H + ".json")));
    }

    // ── гарды фонового контура ────────────────────────────────────────────

    [Fact]
    public async Task Heal_does_nothing_when_meta_already_there()
    {
        TestEnv.FreshCache();
        Access.HealTried().Clear();
        Directory.CreateDirectory(Path.Combine(ModInit.conf.cachePath, "meta"));
        File.WriteAllText(Path.Combine(ModInit.conf.cachePath, "meta", H + ".json"), "{\"id\":318354}");

        Assert.False(await Access.MetaHealAsync(H));
        Assert.False(Access.HealTried().Contains(H));   // до негативного кеша дело не дошло
    }

    [Fact]
    public async Task Heal_without_index_marks_negative_cache()
    {
        TestEnv.FreshCache();
        Access.HealTried().Clear();
        ModInit.conf.localIndexConnection = "";
        ModInit.conf.bitmagnetConnection = "";

        Assert.False(await Access.MetaHealAsync(H));
        // повторный проход по тому же хешу не должен снова ходить в БД: /qdl/list зовётся
        // на каждое открытие карточки
        Assert.True(Access.HealTried().Contains(H));
        Assert.False(await Access.MetaHealAsync(H));
    }

    [Fact]
    public async Task Heal_respects_killswitch_and_bad_hash()
    {
        TestEnv.FreshCache();
        Access.HealTried().Clear();
        bool saved = ModInit.conf.metaHealEnabled;
        try
        {
            ModInit.conf.metaHealEnabled = false;
            Assert.False(await Access.MetaHealAsync(H));
            Assert.False(Access.HealTried().Contains(H));
        }
        finally { ModInit.conf.metaHealEnabled = saved; }

        Assert.False(await Access.MetaHealAsync("не хеш"));
    }
}
