using System.Text.RegularExpressions;
using System.Xml.Linq;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>Smoke 套件的功能命名、文件体积、运行隔离与单宿主结构。</summary>
internal static class TestArchitectureSuite
{
    private const int MaximumProductFileLines = 700;
    private const int MaximumSuiteFileLines = 550;

    public static Task RunAsync(string[] args)
    {
        True(Directory.Exists(VerifyRoot), "verification has a root-level b-Code component");
        True(!Directory.Exists(Path.Combine(RepoRoot, "tests")),
            "product component contains no nested test tree");
        var separator = Path.DirectorySeparatorChar;
        var productFiles = Directory.EnumerateFiles(RepoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        True(productFiles.Length > 0, "test architecture discovers product sources");
        foreach (var path in productFiles)
        {
            var lines = File.ReadLines(path).Count();
            True(lines <= MaximumProductFileLines,
                $"product source stays within {MaximumProductFileLines} lines: " +
                $"{Path.GetRelativePath(RepoRoot, path)} ({lines})");
        }

        VerifyGitHubModuleBoundary(separator);
        VerifyV3HostBoundary();
        VerifyMergedOverviewBoundary(separator);
        VerifyEmbeddedHistoryBoundary(separator);
        VerifyThemeBoundary();

        var smokeRoot = Path.Combine(VerifyRoot, "Smoke");
        var suitesRoot = Path.Combine(smokeRoot, "Suites");
        var suiteFiles = Directory.EnumerateFiles(suitesRoot, "*.cs").ToArray();
        True(suiteFiles.Length > 0, "test architecture discovers suite sources");

        foreach (var path in suiteFiles)
        {
            var name = Path.GetFileName(path);
            var source = File.ReadAllText(path);
            var lines = File.ReadLines(path).Count();
            True(!Regex.IsMatch(name, @"^V\d", RegexOptions.CultureInvariant),
                $"test suite uses a functional filename: {name}");
            True(lines <= MaximumSuiteFileLines,
                $"test suite source stays within {MaximumSuiteFileLines} lines: {name} ({lines})");
            True(!source.Contains("Environment.Current" + "Directory =", StringComparison.Ordinal),
                $"test suite does not mutate process working directory: {name}");
            True(!source.Contains(".tmp-" + "v", StringComparison.OrdinalIgnoreCase),
                $"test suite does not create version-named source-tree temp data: {name}");
            True(!Regex.IsMatch(source,
                    @"Console\.WriteLine\([^\r\n]*Smoke:\s*PASS",
                    RegexOptions.CultureInvariant),
                $"test suite leaves PASS output to the runner: {name}");
        }

        var program = File.ReadAllText(Path.Combine(smokeRoot, "Program.cs"));
        var registered = Regex.Matches(program,
                "\\(\\\"(?<name>[A-Za-z][A-Za-z0-9]+)\\\",",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        string[] expected =
        [
            "VersionProjection", "TestArchitecture", "GitRules", "BranchHistory",
            "SubmoduleSafety", "RepositoryTargets", "ProjectOperations",
        ];
        True(registered.SequenceEqual(expected),
            "test runner registers the reviewed functional suite order");
        Equal(registered.Length, registered.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "test runner suite names are unique");
        True(!Regex.IsMatch(program, "\\\"V\\d", RegexOptions.CultureInvariant),
            "test runner exposes no version-named suite");

        var project = XDocument.Load(Path.Combine(smokeRoot, "Smoke.csproj"));
        Equal(1, project.Descendants("ProjectReference").Count(),
            "test architecture uses the single module product reference");
        Contains(project.Descendants("ProjectReference").Single().Attribute("Include")?.Value ?? "",
            "HistoryJanus.Module.csproj",
            "test architecture references the module project");
        var kit = File.ReadAllText(Path.Combine(smokeRoot, "SmokeKit.cs"));
        Contains(kit, "Path.GetTempPath()",
            "test architecture centralizes temporary data outside the source tree");

        return Task.CompletedTask;
    }

    private static void VerifyV3HostBoundary()
    {
        var businessPath = Path.Combine(RepoRoot, "StudioBusinessComposition.cs");
        var business = File.ReadAllText(businessPath);
        foreach (var prohibited in new[]
                 {
                     "ServiceHost", "ModuleHost", "McpGateway", "WebGateway",
                     "ShellServiceClient", "ShellWindow",
                 })
        {
            True(!business.Contains(prohibited, StringComparison.Ordinal),
                $"V3 business composition does not own host capability: {prohibited}");
        }

        foreach (var injected in new[]
                 {
                     "CommandRegistry registry", "CommandBus bus", "ISettingsService settings",
                     "IShellLog log", "string dataDirectory", "string commandSource",
                 })
        {
            Contains(business, injected,
                $"V3 business composition receives host dependency: {injected}");
        }

        True(!File.Exists(Path.Combine(RepoRoot, "Studio.csproj")),
            "module boundary: legacy Studio project is absent");
        True(!File.Exists(Path.Combine(RepoRoot, "Program.cs")),
            "module boundary: legacy process entry is absent");
        True(!Directory.Exists(Path.Combine(RepoRoot, "Connection")),
            "module boundary: legacy connection tree is absent");
        True(!Directory.Exists(Path.Combine(RepoRoot, "Service")),
            "module boundary: legacy service tree is absent");
    }

    private static void VerifyGitHubModuleBoundary(char separator)
    {
        True(!File.Exists(Path.Combine(VerifyRoot, "Smoke", "Suites", "GitHubAccountSuite.cs")),
            "GitHub-specific Smoke moved to its owning module");

        string[] removedFiles =
        [
            "Git/GitHubAccountModels.cs",
            "Git/GitHubAccountService.cs",
            "Git/GitHubAccountCommands.cs",
            "Git/GitHubRedactor.cs",
            "Git/ToolProcessRunner.cs",
            "Views/GitHubAccountView.xaml",
            "Views/GitHubAccountView.xaml.cs",
        ];
        foreach (var relativePath in removedFiles)
            True(!File.Exists(Path.Combine(RepoRoot, relativePath.Replace('/', separator))),
                $"GitHub-specific host source is removed: {relativePath}");

        string[] prohibitedIdentifiers =
        [
            "GitHubAccount",
            "GitHubRedactor",
            "github.account",
            "github.status",
            "github.accounts",
            "github.test",
            "github.login",
            "github.logout",
            "github.identity",
            "github.remote",
        ];
        var productionFiles = Directory.EnumerateFiles(RepoRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".cs", ".xaml", ".csproj" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase));
        foreach (var path in productionFiles)
        {
            var source = File.ReadAllText(path);
            foreach (var identifier in prohibitedIdentifiers)
                True(!source.Contains(identifier, StringComparison.OrdinalIgnoreCase),
                    $"Janus host does not contain GitHub module identifier {identifier}: " +
                    Path.GetRelativePath(RepoRoot, path));
        }
    }

    private static void VerifyMergedOverviewBoundary(char separator)
    {
        foreach (var removed in new[]
                 {
                     "Views/MetaView.xaml", "Views/MetaView.xaml.cs",
                     "Views/BranchTreeView.xaml", "Views/BranchTreeView.xaml.cs",
                     "Views/BranchTreeItem.cs",
                 })
        {
            True(!File.Exists(Path.Combine(RepoRoot, removed.Replace('/', separator))),
                $"merged overview: standalone view is removed: {removed}");
        }

        foreach (var retained in new[]
                 {
                     "Git/BranchTreeService.cs", "Git/BranchTreeNode.cs", "Git/ProjectService.Meta.cs",
                 })
        {
            True(File.Exists(Path.Combine(RepoRoot, retained.Replace('/', separator))),
                $"merged overview: background service stays: {retained}");
        }

        var module = File.ReadAllText(Path.Combine(RepoRoot, "Module", "HistoryJanusUiModule.cs"));
        var windowIds = Regex.Matches(module, @"Id = ""(?<id>[a-z]+)""", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["id"].Value)
            .ToArray();
        True(windowIds.SequenceEqual(new[] { "overview", "projops" }),
            "page consolidation: module registers exactly overview and projops");

        var commands = File.ReadAllText(Path.Combine(RepoRoot, "Git", "ProjectCommands.cs"));
        foreach (var retained in new[] { "\"proj.tree\"", "\"proj.metalist\"", "\"proj.metaopen\"" })
        {
            Contains(commands, retained,
                $"merged overview: background command stays registered: {retained}");
        }
    }

    private static void VerifyEmbeddedHistoryBoundary(char separator)
    {
        foreach (var retained in new[]
                 {
                     "Views/BranchHistoryView.xaml", "Views/BranchHistoryView.xaml.cs",
                 })
        {
            True(File.Exists(Path.Combine(RepoRoot, retained.Replace('/', separator))),
                $"embedded history: view component stays: {retained}");
        }

        var projectOperations = File.ReadAllText(
            Path.Combine(RepoRoot, "Views", "ProjectOperationsView.xaml.cs"));
        Contains(projectOperations, "new BranchHistoryView(",
            "embedded history: project operations hosts the branch history component");

        var commands = File.ReadAllText(Path.Combine(RepoRoot, "Git", "BranchHistoryCommands.cs"));
        foreach (var retained in new[]
                 {
                     "\"proj.history\"", "\"proj.history.show\"", "\"proj.history.diff\"",
                     "\"proj.rollback\"", "\"proj.reset\"", "\"proj.forcepush\"",
                 })
        {
            Contains(commands, retained,
                $"embedded history: history command stays registered: {retained}");
        }
    }

    private static void VerifyThemeBoundary()
    {
        var viewsRoot = Path.Combine(RepoRoot, "Views");
        var literalColor = new Regex(
            "(?:Background|Foreground|BorderBrush|Fill|Stroke|Value)\\s*=\\s*\\\"(?:White|Black|Gray|#[0-9A-Fa-f]+)\\\"",
            RegexOptions.CultureInvariant);
        var views = Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.TopDirectoryOnly)
            .ToArray();
        True(views.Length > 0, "theme governance discovers module XAML views");
        foreach (var path in views)
        {
            var source = File.ReadAllText(path);
            True(!literalColor.IsMatch(source),
                $"view uses HistoryVulcan dynamic color tokens: {Path.GetFileName(path)}");
        }

        var primaryViews = new[]
        {
            "OverviewView.xaml", "BranchHistoryView.xaml", "ProjectOperationsView.xaml",
        };
        foreach (var name in primaryViews)
        {
            var source = File.ReadAllText(Path.Combine(viewsRoot, name));
            Contains(source, "Background=\"{DynamicResource Shell.Brush.Surface}\"",
                $"theme governance: {name} uses the HistoryVulcan surface token");
            Contains(source, "Shell.Brush.TextPrimary",
                $"theme governance: {name} uses the host primary text token");
        }

        foreach (var name in new[]
                 {
                     "OverviewView.xaml", "BranchHistoryView.xaml", "ProjectOperationsView.xaml",
                 })
        {
            var source = File.ReadAllText(Path.Combine(viewsRoot, name));
            True(source.Contains("Shell.Brush.Surface}", StringComparison.Ordinal)
                 && source.Contains("Shell.Brush.Hairline}", StringComparison.Ordinal),
                $"theme governance: {name} content controls use surface and hairline tokens");
        }

        var projectOperations = File.ReadAllText(Path.Combine(viewsRoot, "ProjectOperationsView.xaml"));
        foreach (var token in new[]
                  {
                      "Shell.Brush.SurfaceAlt", "Shell.Brush.ControlBorder",
                      "Shell.Brush.SurfaceHover", "Shell.Brush.AccentSoft", "Shell.Brush.Accent",
                      "Shell.Brush.TextDisabled", "Shell.Brush.Hairline",
                  })
        {
            Contains(projectOperations, token,
                $"theme governance: project operations uses {token}");
        }
        Contains(projectOperations,
            "Foreground=\"{TemplateBinding Foreground}\"",
            "theme governance: operation segment text inherits the host foreground");
    }
}
