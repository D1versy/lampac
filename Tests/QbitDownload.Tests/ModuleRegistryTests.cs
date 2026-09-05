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

        var missing = disk.Except(tree, StringComparer.Ordinal).ToArray();
        var stale = tree.Except(disk, StringComparer.Ordinal).ToArray();

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
        var ghosts = InCsproj().Except(disk, StringComparer.Ordinal).ToArray();

        Assert.True(ghosts.Length == 0,
            "csproj линкует несуществующие файлы модуля: " + string.Join(", ", ghosts));
    }

    [Fact]
    public void Csproj_is_a_subset_of_the_manifest()
    {
        var extra = InCsproj().Except(InManifest(), StringComparer.Ordinal).ToArray();

        Assert.True(extra.Length == 0,
            "тесты линкуют файлы, которых нет в manifest.json → tree: " + string.Join(", ", extra));
    }

    [Fact]
    public void Unlinked_files_are_declared_explicitly()
    {
        // Файл, выпавший из csproj без правки KnownUnlinked, перестаёт покрываться тестами
        // молча. Список ведётся здесь и требует причины в комментарии.
        var unlinked = InManifest().Except(InCsproj(), StringComparer.Ordinal).ToArray();

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

    // ── чужие модули: та же мина, но сторожа у них не было (qdl 2.112) ─────
    //
    // 🔴 Всё выше смотрит ТОЛЬКО на Modules/QbitDownload (и регексп InCsproj прибит к этому
    // пути). А линкуем мы файлы ещё из JacRed, Online, Core и CubProxy — и у каждого из них
    // ровно та же мина: забытая строка в его manifest.json → tree даёт неполный набор
    // исходников для Roslyn и крашлуп контейнера, который ничем не ловится. Нашлось это при
    // добавлении PageGuard.cs в CubProxy.
    //
    // 🔴 Все сравнения имён — Ordinal. Контейнер — Linux, ext4 регистрозависим: «pageguard.cs» в
    // tree прошёл бы NTFS-тест (File.Exists там регистронезависим) и дал крашлуп в проде.
    // Поэтому существование на диске проверяется посегментно, точным именем (ExistsExact).

    static string RepoRoot => new DirectoryInfo(Resolve("Modules")).Parent.FullName;

    /// <summary>Все ссылки csproj вида ..\..\&lt;путь&gt;\Файл.cs — включая чужие модули.</summary>
    static string[] AllIncludes()
    {
        string xml = File.ReadAllText(CsprojPath);
        var rx = new Regex(@"<Compile\s+Include=""\.\.\\\.\.\\([^""]+)""", RegexOptions.Compiled);
        return rx.Matches(xml).Select(m => m.Groups[1].Value).ToArray();
    }

    /// <summary>Ближайший вверх по дереву каталог с manifest.json; null — модуля нет (Core и т.п.).</summary>
    static string ModuleOf(string includeRelative)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.Combine(RepoRoot, includeRelative)));
        var root = new DirectoryInfo(RepoRoot);

        while (dir != null && dir.FullName.Length >= root.FullName.Length)
        {
            if (File.Exists(Path.Combine(dir.FullName, "manifest.json")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }

    static JArray TreeOf(string moduleDir)
        => JObject.Parse(File.ReadAllText(Path.Combine(moduleDir, "manifest.json")))["tree"] as JArray;

    /// <summary>
    /// Покрыт ли файл записью tree. Запись-КАТАЛОГ покрывает всё под собой — так устроены
    /// JacRed ("Engine", "Controllers") и Online ("SQL"). Пустой или отсутствующий tree — как у
    /// загрузчика (CSharpEval.Compilation): компилируется всё *.cs рекурсивно, забыть нечего.
    /// </summary>
    internal static bool CoveredByTree(JArray tree, string relative)
    {
        if (tree == null || tree.Count == 0)
            return true;

        relative = relative.Replace('\\', '/');

        foreach (var t in tree)
        {
            string e = ((string)t ?? "").Replace('\\', '/').TrimEnd('/');
            if (e.Length == 0)
                continue;

            if (relative.Equals(e, StringComparison.Ordinal) || relative.StartsWith(e + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Существование пути ТОЧНЫМ именем, посегментно — то, что увидит Linux.</summary>
    static bool ExistsExact(string dir, string relative)
    {
        string cur = dir;

        foreach (string seg in relative.Replace('\\', '/').TrimEnd('/').Split('/'))
        {
            if (seg.Length == 0)
                continue;

            string hit = Directory.EnumerateFileSystemEntries(cur)
                                  .Select(Path.GetFileName)
                                  .FirstOrDefault(n => n.Equals(seg, StringComparison.Ordinal));
            if (hit == null)
                return false;

            cur = Path.Combine(cur, hit);
        }

        return true;
    }

    /// <summary>Каталоги, где живут модули с манифестами. Весь репозиторий не обходим: там 200+ манифестов в node_modules и примеры апстрима, которые Roslyn не компилирует.</summary>
    static IEnumerable<string> ModuleManifests()
    {
        foreach (string top in new[] { "Modules", "Online" })
        {
            string dir = Path.Combine(RepoRoot, top);
            if (!Directory.Exists(dir))
                continue;

            foreach (string m in Directory.EnumerateFiles(dir, "manifest.json", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(RepoRoot, m).Replace('\\', '/');
                if (rel.Contains("/node_modules/") || rel.Contains("/bin/") || rel.Contains("/obj/"))
                    continue;

                yield return m;
            }
        }
    }

    [Fact]
    public void Tree_coverage_is_case_sensitive_like_linux()
    {
        // негативный контроль самого сторожа: NTFS простил бы, Linux — нет
        Assert.False(CoveredByTree(new JArray("pageguard.cs"), "PageGuard.cs"));
        Assert.True(CoveredByTree(new JArray("PageGuard.cs"), "PageGuard.cs"));
        Assert.True(CoveredByTree(new JArray("Engine"), "Engine/Sub/X.cs"));
        Assert.False(CoveredByTree(new JArray("Engine"), "EngineX/A.cs"));
        Assert.False(CoveredByTree(new JArray("Other.cs"), "Any.cs"));
        Assert.True(CoveredByTree(null, "Any.cs"));                 // без tree компилируется всё
        Assert.True(CoveredByTree(new JArray(), "Any.cs"));
        Assert.False(ExistsExact(CubProxyDir, "pageguard.cs"));
        Assert.True(ExistsExact(CubProxyDir, "PageGuard.cs"));
    }

    [Fact]
    public void Every_linked_file_of_any_module_is_in_its_manifest()
    {
        var bad = new List<string>();

        foreach (string include in AllIncludes())
        {
            if (include.Contains('*'))
                continue;   // глоб — csproj раскроет сам, файл за файлом тут не проверить

            string moduleDir = ModuleOf(include);
            if (moduleDir == null)
                continue;   // Core и прочее без manifest — Roslyn их не компилирует

            string rel = Path.GetRelativePath(moduleDir, Path.Combine(RepoRoot, include));

            if (!CoveredByTree(TreeOf(moduleDir), rel))
                bad.Add(include);
        }

        Assert.True(bad.Count == 0,
            "тесты линкуют файлы, которых нет в manifest.json → tree их модуля "
            + "(Roslyn не увидит их в проде, контейнер уйдёт в крашлуп): " + string.Join(", ", bad));
    }

    [Fact]
    public void Every_manifest_tree_entry_exists_on_disk()
    {
        var bad = new List<string>();

        foreach (string manifest in ModuleManifests())
        {
            string dir = Path.GetDirectoryName(manifest);

            JObject root;
            try { root = JObject.Parse(File.ReadAllText(manifest)); }
            catch { continue; }   // не наша форма манифеста — не наше дело

            if (root["tree"] is not JArray tree)
                continue;

            foreach (var t in tree)
            {
                string entry = (string)t;
                if (string.IsNullOrEmpty(entry))
                    continue;

                if (!ExistsExact(dir, entry))
                    bad.Add(Path.GetRelativePath(RepoRoot, manifest) + " → " + entry);
            }
        }

        Assert.True(bad.Count == 0, "в manifest.json перечислено несуществующее (или не тем регистром): " + string.Join(", ", bad));
    }

    // ── CubProxy: тот же двойной реестр, что у QbitDownload ───────────────

    static string CubProxyDir => Resolve(Path.Combine("Modules", "Proxy", "CubProxy"));

    /// <summary>
    /// Файлы CubProxy, намеренно не залинкованные в тесты. У каждого — причина, и она же
    /// записана в комментарии над строкой линковки в csproj.
    /// </summary>
    static readonly HashSet<string> CubProxyUnlinked = new(StringComparer.Ordinal)
    {
        "Controller.cs",   // BaseController — в тестовую сборку не тянется
        "ModInit.cs",      // конфликт с QbitDownload.ModInit
        "ModuleConf.cs",   // ходит за CoreInit
        "FilterStore.cs",  // диск + статика, протекала бы между тестами
        "PageStore.cs"     // то же самое: диск + статика
    };

    static string[] CubProxyOnDisk() =>
        Directory.GetFiles(CubProxyDir, "*.cs", SearchOption.AllDirectories)
                 .Select(f => Path.GetRelativePath(CubProxyDir, f).Replace('\\', '/'))
                 .Where(f => !f.StartsWith("bin/") && !f.StartsWith("obj/"))   // артефакты локальной сборки csproj, Roslyn их не видит
                 .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void CubProxy_manifest_tree_covers_every_file_on_disk()
    {
        // tree перечисляет только файлы, поэтому .cs в подпапке был бы невидим и для Roslyn —
        // берём диск рекурсивно и сверяем через CoveredByTree (он понимает и записи-каталоги)
        var tree = TreeOf(CubProxyDir);
        Assert.NotNull(tree);
        Assert.NotEmpty(tree);

        var missing = CubProxyOnDisk().Where(f => !CoveredByTree(tree, f)).ToArray();
        Assert.True(missing.Length == 0,
            "файлы CubProxy есть на диске, но НЕ вписаны в manifest.json → tree (крашлуп): " + string.Join(", ", missing));

        var stale = tree.Select(t => (string)t).Where(e => !ExistsExact(CubProxyDir, e)).ToArray();
        Assert.True(stale.Length == 0,
            "в manifest.json CubProxy перечислены несуществующие файлы (или не тем регистром): " + string.Join(", ", stale));

        // ⚠️ Тест порядка (ModuleConf/ModInit первыми) на CubProxy НЕ переносится: у него в tree
        // первым идёт Controller.cs. Это соглашение QbitDownload, а не требование Roslyn — он
        // получает все синтаксические деревья скопом.
    }

    [Fact]
    public void CubProxy_unlinked_files_are_declared_explicitly()
    {
        string xml = File.ReadAllText(CsprojPath);
        var rx = new Regex(@"<Compile\s+Include=""\.\.\\\.\.\\Modules\\Proxy\\CubProxy\\([^""]+)""", RegexOptions.Compiled);
        var linked = rx.Matches(xml).Select(m => m.Groups[1].Value.Replace('\\', '/')).ToArray();

        var unlinked = CubProxyOnDisk().Except(linked, StringComparer.Ordinal).ToArray();

        Assert.True(CubProxyUnlinked.SetEquals(unlinked),
            "набор незалинкованных файлов CubProxy изменился. Сейчас вне тестов: ["
            + string.Join(", ", unlinked) + "], объявлено: [" + string.Join(", ", CubProxyUnlinked) + "]. "
            + "Либо залинкуй файл, либо впиши его в CubProxyUnlinked с причиной.");
    }
}
