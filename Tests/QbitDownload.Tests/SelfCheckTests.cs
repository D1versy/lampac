using System;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Канарейка гейта.
///
/// Прогон `scripts\test-all.ps1 -SelfCheck` выставляет D1V_SELFCHECK=1, и этот тест
/// намеренно падает. Смысл — доказать, что гейт вообще СПОСОБЕН покраснеть: молча
/// «зелёная» сьюта, которая на самом деле не запустилась или потеряла ассерты,
/// хуже отсутствия тестов, потому что создаёт ложную уверенность.
///
/// Такая же канарейка есть в каждой ноге гейта.
/// </summary>
public class SelfCheckTests
{
    [Fact]
    public void The_suite_is_able_to_fail()
    {
        Assert.True(Environment.GetEnvironmentVariable("D1V_SELFCHECK") != "1",
            "канарейка -SelfCheck: сьюта форка (C#) умеет краснеть");
    }
}
