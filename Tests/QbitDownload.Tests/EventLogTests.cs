using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Журнал событий владельца (qdl 2.111): кольцо на томе вместо новой таблицы в SQLite.
/// Таблицу завести нельзя — EnsureCreated() не добавляет её в существующую БД, и она молча
/// не создалась бы (та же грабля, что у SearchMonitor).
/// </summary>
public class EventLogTests
{
    [Fact]
    public void Пишет_и_отдаёт_свежие_сверху()
    {
        TestEnv.FreshCache();
        QdlEvents.Log(QdlEvents.CatHunt, "Укрытие", "донор взят");
        QdlEvents.Log(QdlEvents.CatRelease, "Фонари", "раздача обновилась");

        var (items, total) = QdlEvents.Read(10);
        Assert.Equal(2, total);
        Assert.Equal("раздача обновилась", items[0].Value<string>("text"));   // свежее — первым
        Assert.Equal("донор взят", items[1].Value<string>("text"));
        Assert.Equal(QdlEvents.CatRelease, items[0].Value<string>("cat"));
    }

    [Fact]
    public void Кольцо_не_растёт_выше_кэпа()
    {
        TestEnv.FreshCache();
        ModInit.conf.adminEventsKeep = 100;          // клампится снизу сотней
        try
        {
            for (int i = 0; i < 150; i++) QdlEvents.Log(QdlEvents.CatDiag, "t", "строка " + i);

            var (items, total) = QdlEvents.Read(1000);
            Assert.Equal(100, total);
            Assert.Equal("строка 149", items[0].Value<string>("text"));       // хвост сохранён
            Assert.Equal("строка 50", items[99].Value<string>("text"));       // голова вытеснена
        }
        finally { ModInit.conf.adminEventsKeep = 2000; }
    }

    [Fact]
    public void Read_режет_выдачу_но_total_честный()
    {
        TestEnv.FreshCache();
        for (int i = 0; i < 20; i++) QdlEvents.Log(QdlEvents.CatDownload, "t", "s" + i);

        var (items, total) = QdlEvents.Read(5);
        Assert.Equal(5, items.Count);
        Assert.Equal(20, total);                     // «показаны последние N из M» — не врём
    }

    [Fact]
    public void Recent_дедупит_по_ключу_и_окну()
    {
        TestEnv.FreshCache();
        QdlEvents.Log(QdlEvents.CatDiag, "Поиск раздач", "трекер лёг", key: "tracker-down");

        Assert.True(QdlEvents.Recent(QdlEvents.CatDiag, "tracker-down", TimeSpan.FromHours(12)));
        Assert.False(QdlEvents.Recent(QdlEvents.CatDiag, "другое", TimeSpan.FromHours(12)));
        Assert.False(QdlEvents.Recent(QdlEvents.CatHunt, "tracker-down", TimeSpan.FromHours(12)));
        Assert.False(QdlEvents.Recent(QdlEvents.CatDiag, "tracker-down", TimeSpan.Zero));
        Assert.False(QdlEvents.Recent(QdlEvents.CatDiag, null, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void Киллсвитч_не_пишет_ничего()
    {
        TestEnv.FreshCache();
        ModInit.conf.adminEvents = false;
        try
        {
            QdlEvents.Log(QdlEvents.CatHunt, "t", "строка");
            Assert.Equal(0, QdlEvents.Read(10).total);
        }
        finally { ModInit.conf.adminEvents = true; }
    }

    [Fact]
    public void Пустой_текст_строки_не_создаёт()
    {
        TestEnv.FreshCache();
        QdlEvents.Log(QdlEvents.CatHunt, "t", null);
        QdlEvents.Log(QdlEvents.CatHunt, "t", "");
        Assert.Equal(0, QdlEvents.Read(10).total);
    }

    [Fact]
    public void Clear_очищает()
    {
        TestEnv.FreshCache();
        QdlEvents.Log(QdlEvents.CatHunt, "t", "строка");
        QdlEvents.Clear();
        Assert.Equal(0, QdlEvents.Read(10).total);
    }

    [Fact]
    public void Необязательные_поля_не_занимают_места()
    {
        TestEnv.FreshCache();
        QdlEvents.Log(QdlEvents.CatWatch, "t", "строка");
        var o = QdlEvents.Read(1).items[0] as JObject;
        Assert.False(o.ContainsKey("hash"));
        Assert.False(o.ContainsKey("act"));
        Assert.False(o.ContainsKey("key"));
        Assert.NotNull(o.Value<string>("at"));
    }
}
