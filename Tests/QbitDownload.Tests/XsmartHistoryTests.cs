using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Память экрана поиска XSMART (qdl 2.114, XsmartHistory.cs): «Недавнее» = просмотренное +
// найденное, раздельно по устройствам, карточка хранится в самой записи.
// Паритет с jut (JutSuAutopilotTests, регион «память экрана поиска») + своё: санация карточки
// (постер только наш путь, мусорные cat/id не пишутся), группа даёт общий бакет, песочница
// чистит бакет устройства.
// ─────────────────────────────────────────────────────────────────────────────
public class XsmartHistoryTests
{
    const string DevA = "xa11111";
    const string DevB = "xb22222";

    static void Fresh()
    {
        TestEnv.FreshCache();
        ModInit.conf.xsmartEnable = true;
        ModInit.conf.replicaRole = "";
        QbitController.XsmartHistoryResetForTests();
    }

    static JObject Card(string id, string title = null, int cat = 6, string poster = null) => new JObject
    {
        ["cat"] = cat, ["id"] = id, ["type"] = cat == 3 ? "series" : "movie",
        ["title"] = title ?? ("Тайтл " + id), ["year"] = 2025, ["rating"] = 7.1,
        ["poster"] = poster ?? ("/xsmart/img/360/" + id + ".jpeg"),
        ["description"] = "лишнее поле — в запись не идёт"
    };

    static JArray Recent(string uid, int limit = 50)
        => (JArray)QbitController.XsmartRecentPayload(limit, uid)["items"];

    static string Ref(JToken c) => c.Value<int>("cat") + "-" + c.Value<string>("id");

    [Fact]
    public void Просмотренное_идёт_впереди_искомого()
    {
        Fresh();
        QbitController.XsmartHistoryRecordSearch(new JArray(Card("1"), Card("2")), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("3"), DevA);

        var items = Recent(DevA);
        Assert.Equal(3, items.Count);
        Assert.Equal("6-3", Ref(items[0]));
        Assert.Equal("watch", items[0].Value<string>("src"));
        Assert.Contains(items, x => Ref(x) == "6-1" && x.Value<string>("src") == "search");
    }

    [Fact]
    public void Один_тайтл_не_дублируется_между_секциями()
    {
        Fresh();
        QbitController.XsmartHistoryRecordSearch(new JArray(Card("5")), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("5"), DevA);

        var items = Recent(DevA);
        Assert.Single(items);
        Assert.Equal("watch", items[0].Value<string>("src"));
    }

    [Fact]
    public void Свежий_просмотр_поднимается_наверх()
    {
        Fresh();
        QbitController.XsmartHistoryTouchWatch(Card("10"), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("11"), DevA);

        var items = Recent(DevA);
        Assert.Equal("6-11", Ref(items[0]));
        Assert.Equal("6-10", Ref(items[1]));
    }

    [Fact]
    public void Кап_ограничивает_выдачу()
    {
        Fresh();
        for (int i = 0; i < 60; i++) QbitController.XsmartHistoryTouchWatch(Card((100 + i).ToString()), DevA);
        Assert.Equal(50, Recent(DevA).Count);
    }

    [Fact]
    public void Повторный_старт_серии_не_плодит_записей()
    {
        // Одна серия за вечер стартует не раз (перемотка, смена качества, следующая серия того же
        // тайтла) — это один просмотр тайтла.
        Fresh();
        QbitController.XsmartHistoryTouchWatch(Card("7"), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("7"), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("7"), DevA);

        var jo = JsonStore.ReadObject(Path.Combine(XsmartNet.DataDir(), "history", DevA + ".json"));
        Assert.Equal(1, jo["watched"]["6-7"]["count"].Value<int>());
    }

    [Fact]
    public void Карточка_хранится_в_записи_и_отдаётся_slim()
    {
        // У lampac нет каталога XSMART — карточка обязана лежать в самой записи, а лишние поля
        // (description и прочая простыня) в неё не идут.
        Fresh();
        QbitController.XsmartHistoryTouchWatch(Card("8", "Вскрытие демона"), DevA);

        var c = Recent(DevA)[0];
        Assert.Equal("Вскрытие демона", c.Value<string>("title"));
        Assert.Equal("/xsmart/img/360/8.jpeg", c.Value<string>("poster"));
        Assert.Equal(2025, c.Value<int>("year"));
        Assert.Null(c["description"]);
    }

    [Fact]
    public void Чужой_постер_и_мусорные_cat_id_в_историю_не_идут()
    {
        Fresh();
        // постер не с нашего пути — отбрасывается, карточка остаётся
        QbitController.XsmartHistoryTouchWatch(Card("9", poster: "https://cdn.evil/x.jpg"), DevA);
        Assert.Null(Recent(DevA)[0]["poster"]);

        // мусорные cat/id — записи нет вовсе
        Assert.False(QbitController.XsmartHistoryTouchWatch(Card("../../etc", cat: 6), DevA));
        Assert.False(QbitController.XsmartHistoryTouchWatch(Card("12", cat: 999), DevA));
        Assert.Equal(0, QbitController.XsmartHistoryRecordSearch(new JArray(new JObject { ["cat"] = 6 }), DevA));
        Assert.Single(Recent(DevA));
    }

    [Fact]
    public void Устройства_не_видят_историю_друг_друга()
    {
        Fresh();
        QbitController.XsmartHistoryTouchWatch(Card("21"), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("22"), DevB);

        Assert.Single(Recent(DevA));
        Assert.Equal("6-21", Ref(Recent(DevA)[0]));
        Assert.Single(Recent(DevB));
        Assert.Equal("6-22", Ref(Recent(DevB)[0]));
    }

    [Fact]
    public void Дедуп_одного_устройства_не_глушит_другое()
    {
        Fresh();
        QbitController.XsmartHistoryTouchWatch(Card("31"), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("31"), DevB);   // тот же тайтл на другом устройстве в то же окно
        Assert.Single(Recent(DevB));
    }

    [Fact]
    public void Общая_история_добирается_новому_устройству()
    {
        // Безымянный клиент (без uid) пишет в _shared; новое устройство видит это добором.
        Fresh();
        QbitController.XsmartHistoryTouchWatch(Card("41"), null);
        QbitController.XsmartHistoryTouchWatch(Card("42"), DevA);

        var items = Recent(DevA);
        Assert.Equal("6-42", Ref(items[0]));
        Assert.Equal("6-41", Ref(items[1]));
    }

    [Fact]
    public void Группа_устройств_даёт_общий_бакет()
    {
        // Связанные устройства (qdl 2.81) делят историю: бакет — группа, а не устройство.
        Fresh();
        ModInit.conf.groupsEnabled = true;
        string gid = Groups.Create("Семья");
        Assert.Null(Groups.Join(gid, DevA, apply: true)["error"]);
        Assert.Null(Groups.Join(gid, DevB, apply: true)["error"]);

        Assert.Equal(QbitController.XsmartHistoryBucketFor(DevA), QbitController.XsmartHistoryBucketFor(DevB));
        QbitController.XsmartHistoryTouchWatch(Card("51"), DevA);
        Assert.Single(Recent(DevB));
    }

    [Fact]
    public void Реплика_в_историю_дома_не_пишет()
    {
        Fresh();
        var prev = ModInit.conf.replicaRole;
        try
        {
            ModInit.conf.replicaRole = "replica";
            Assert.False(QbitController.XsmartHistoryTouchWatch(Card("61"), DevA));
            Assert.Equal(0, QbitController.XsmartHistoryRecordSearch(new JArray(Card("62")), DevA));
            Assert.Empty(Recent(DevA));
        }
        finally { ModInit.conf.replicaRole = prev; }
    }

    [Fact]
    public void Песочница_чистит_бакет_устройства_но_не_общий()
    {
        Fresh();
        QbitController.XsmartHistoryTouchWatch(Card("71"), DevA);
        QbitController.XsmartHistoryTouchWatch(Card("72"), null);   // _shared
        var errors = new JArray();

        Assert.Equal(1, (int)Access.Call("PurgeXsmartHistory", DevA, false, errors));   // сухой прогон видит
        Assert.Equal(1, (int)Access.Call("PurgeXsmartHistory", DevA, true, errors));
        Assert.Equal(0, (int)Access.Call("PurgeXsmartHistory", "", true, errors));      // общий — никогда
        Assert.Empty(errors);

        var items = Recent(DevA);
        Assert.Single(items);                                   // своё стёрто, общий добор остался
        Assert.Equal("6-72", Ref(items[0]));
    }
}
