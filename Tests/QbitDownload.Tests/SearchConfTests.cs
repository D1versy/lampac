using Newtonsoft.Json.Linq;
using QbitDownload;
using Xunit;

namespace QbitDownload.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Киллсвитч экрана поиска (qdl 2.121). Флаг searchScreen из init.conf едет клиенту ключом
// «search» в /qdl/features — отдельным от features (тот объект клиент читает как булеву карту
// прав). Тестируем статический билдер, как ProgressClientConf: экшен требует requestInfo,
// а контракт с клиентом — ровно форма этого объекта.
// ─────────────────────────────────────────────────────────────────────────────
public class SearchConfTests
{
    [Fact]
    public void По_умолчанию_экран_поиска_включён()
    {
        TestEnv.EnsureConf();
        Assert.True(new ModuleConf().searchScreen, "дефолт конфига — включено");
        ModInit.conf.searchScreen = true;
        JObject conf = QbitController.SearchClientConf();
        Assert.True(conf.Value<bool>("screen"));
        Assert.Single(conf);   // клиент ждёт ровно {screen}: лишний ключ — повод перечитать setSearchConf в qdl.js
    }

    [Fact]
    public void Киллсвитч_выключает_экран_на_лету()
    {
        TestEnv.EnsureConf();
        ModInit.conf.searchScreen = false;
        try
        {
            Assert.False(QbitController.SearchClientConf().Value<bool>("screen"));
        }
        finally
        {
            ModInit.conf.searchScreen = true;
        }
    }
}
