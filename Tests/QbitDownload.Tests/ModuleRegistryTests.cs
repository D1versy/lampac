using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Двойной реестр файлов модуля: новый .cs обязан быть вписан И в manifest.json → tree
/// (по нему хост компилирует модуль Roslyn'ом в рантайме), И в csproj тестов (по нему
/// компилируются тесты).
///
/// 🔴 Зачем это тест. Расхождение НЕ ловится ни сборкой образа, ни сборкой тестов — оно
/// проявляется только в проде крашлупом контейнера, потому что Roslyn получает неполный
/// набор исходников. Файл, забытый в tree, до сих пор находили глазами.
/// </summary>
public class ModuleRegistryTests
{
    /// <summary>Файлы, намеренно не залинкованные в тесты. Пусто — и это правильное состояние.</summary>
    /// <remarks>
    /// Историческая справка: до 2026-08-23 здесь лежал Live.cs — csproj утверждал, что он
    /// «тянет за собой контроллер». Проверка сборкой показала, что это неверно (Controller.cs
    /// линкуется с первого тестового коммита), и файл уехал под тесты.
    /// Добавлять что-то сюда можно ТОЛЬКО с причиной в комментарии.
    /// </remarks>
    static readonly HashSet<string> KnownUnlinked = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    static string ModuleDir => Resolve(Path.Combine("Modules", "QbitDownload"));
    static string CsprojPath => Resolve(Path.Combine("Tests", "QbitDownload.Tests", "QbitDownload.Tests.csproj"));

    /// <summary>Корень репозитория от папки сборки — тем же многокандидатным пробингом, что у фикстур.</summary>
    static string Resolve(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(p) || File.Exists(p)) return p;
        }
        throw new DirectoryNotFoundException("не нашёл " + relative + " от " + AppContext.BaseDirectory);
    }

    static string[] OnDisk() =>
        Directory.GetFiles(ModuleDir, "*.cs", SearchOption.TopDirectoryOnly)
                 .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    static string[] InManifest()
    {
        var tree = JObject.Parse(File.ReadAllText(Path.Combine(ModuleDir, "manifest.json")))["tree"] as JArray;
        Assert.NotNull(tree);
        return tree.Select(t => (string)t).ToArray();
    }

    static string[] InCsproj()
    {
        string xml = File.ReadAllText(CsprojPath);
        var rx = new Regex(@"<Compile\s+Include=""\.\.\\\.\.\\Modules\\QbitDownload\\([^""]+)""", RegexOptions.Compiled);
        return rx.Matches(xml).Select(m => m.Groups[1].Value).ToArray();
    }

    // ── главный инвариант ─────────────────────────────────────────────────

    [Fact]
    public void Manifest_tree_matches_the_files_on_disk_exactly()
    {
        // Именно это расхождение роняет контейнер крашлупом.
        var disk = OnDisk();
        var tree = InManifest();

        var missing = disk.Except(tree, StringComparer.OrdinalIgnoreCase).ToArray();
        var stale = tree.Except(disk, StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.True(missing.Length == 0,
            "файлы есть на диске, но НЕ вписаны в manifest.json → tree (Roslyn их не увидит, контейнер уйдёт в крашлуп): "
            + string.Join(", ", missing));
        Assert.True(stale.Length == 0,
            "в manifest.json → tree перечислены несуществующие файлы: " + string.Join(", ", stale));
    }

    [Fact]
    public void Manifest_tree_has_no_duplicates()
    {
        var tree = InManifest();
        var dupes = tree.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

        Assert.True(dupes.Length == 0, "дубли в tree: " + string.Join(", ", dupes));
    }

    // ── порядок компиляции ────────────────────────────────────────────────

    [Fact]
    public void Config_and_init_come_first_and_the_controller_last()
    {
        // Порядок в tree — это порядок передачи Roslyn'у. ModuleConf/ModInit объявляют то,
        // на что опираются остальные; Controller.cs замыкает partial-класс.
        var tree = InManifest();

        Assert.Equal("ModuleConf.cs", tree[0]);
        Assert.Equal("ModInit.cs", tree[1]);
        Assert.Contains("Controller.cs", tree);
    }

    // ── связь с тестовым проектом ─────────────────────────────────────────

    [Fact]
    public void Every_Compile_Include_points_at_a_file_that_exists()
    {
        var disk = OnDisk();
        var ghosts = InCsproj().Except(disk, StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.True(ghosts.Length == 0,
            "csproj линкует несуществующие файлы модуля: " + string.Join(", ", ghosts));
    }

    [Fact]
    public void Csproj_is_a_subset_of_the_manifest()
    {
        var extra = InCsproj().Except(InManifest(), StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.True(extra.Length == 0,
            "тесты линкуют файлы, которых нет в manifest.json → tree: " + string.Join(", ", extra));
    }

    [Fact]
    public void Unlinked_files_are_declared_explicitly()
    {
        // Файл, выпавший из csproj без правки KnownUnlinked, перестаёт покрываться тестами
        // молча. Список ведётся здесь и требует причины в комментарии.
        var unlinked = InManifest().Except(InCsproj(), StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.True(KnownUnlinked.SetEquals(unlinked),
            "набор незалинкованных файлов изменился. Сейчас вне тестов: ["
            + string.Join(", ", unlinked) + "], объявлено: [" + string.Join(", ", KnownUnlinked) + "]. "
            + "Либо залинкуй файл, либо впиши его в KnownUnlinked с причиной.");
    }

    [Fact]
    public void The_whole_module_is_currently_under_test()
    {
        // Канарейка достигнутого состояния: с 2026-08-23 незалинкованных файлов нет.
        Assert.Empty(KnownUnlinked);
    }
}
