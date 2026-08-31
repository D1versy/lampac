using System.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты локальной витрины расширений CUB (<c>CubExtensions.cs</c>): валидация id элемента
/// (qdl 2.17) и запрет редиректа наружу на промахе вендора (qdl 2.88). Сама отдача файлов и
/// дотяжка с upstream проверяются вживую после деплоя (curl по /cub/red/extensions/&lt;id&gt;
/// и /cub/red/api/extensions/list).
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

    /// <summary>
    /// 🔴 qdl 2.88: промах вендора НЕ смеет отвечать редиректом. До 2.88 он отдавал
    /// 302 на cub.best — и это было невидимо: тема работала, экран выглядел нормально, а
    /// устройство при каждой загрузке ходило на третью сторону, унося в URL ещё и ?token=
    /// аккаунта (его дописывает Theme.set). Теперь за файлом идёт сервер (FetchAndCache).
    ///
    /// Тест по исходнику, а не по поведению, сознательно: чтобы проверить ветку вживую,
    /// нужен элемент витрины, которого нет в вендоре, — то есть состояние, которое сам же
    /// прогон и лечит, положив файл в кеш. Второй запуск был бы уже зелёным на любом коде.
    /// Здесь же ловится именно тихий возврат Redirect при ребейзе или «упрощении».
    /// </summary>
    [Fact]
    public void CubExtensions_NoRedirectAnywhere()
    {
        string src = System.IO.File.ReadAllText(SourcePath());

        // Отбрасываем комментарии: слово Redirect в объяснении «почему его тут нет» — не код.
        string code = string.Join('\n', src.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//")));

        Assert.DoesNotContain("Redirect(", code);
    }

    static string SourcePath()
    {
        // bin/Debug/net10.0 → корень репозитория
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Modules")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir.FullName, "Modules", "QbitDownload", "CubExtensions.cs");
    }
}
