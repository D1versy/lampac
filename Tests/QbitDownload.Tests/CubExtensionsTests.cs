using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты локальной витрины расширений CUB (<c>CubExtensions.cs</c>, qdl 2.17): валидация id
/// элемента. Отдача файлов и фолбэк на upstream проверяются вживую после деплоя (curl по
/// /cub/red/extensions/&lt;id&gt; и /cub/red/api/extensions/list).
/// </summary>
public class CubExtensionsTests
{
    [Theory]
    [InlineData("212")]   // Neon
    [InlineData("196")]   // Gold (был премиум)
    [InlineData("183")]   // Galaxy (скринсейвер, был премиум)
    [InlineData("1")]
    public void IsExtensionId_Numeric_True(string id)
        => Assert.True(CubExtensions_IsId(id));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("212.css")]          // с расширением к нам не приходит
    [InlineData("../../etc/passwd")] // traversal не должен дойти до ФС
    [InlineData("theme/212")]
    [InlineData("212 ")]
    [InlineData("abc")]
    [InlineData("999999999999999")]  // абсурдная длина
    public void IsExtensionId_Foreign_False(string id)
        => Assert.False(CubExtensions_IsId(id));

    static bool CubExtensions_IsId(string id) => QbitController.IsExtensionId(id);
}
