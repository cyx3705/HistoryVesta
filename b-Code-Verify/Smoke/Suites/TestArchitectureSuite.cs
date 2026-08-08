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
                $"view uses AppShell dynamic color tokens: {Path.GetFileName(path)}");
        }

        var primaryViews = new[]
        {
            "OverviewView.xaml", "MetaView.xaml", "BranchTreeView.xaml",
            "BranchHistoryView.xaml", "ProjectOperationsView.xaml",
        };
        foreach (var name in primaryViews)
        {
            var source = File.ReadAllText(Path.Combine(viewsRoot, name));
            Contains(source, "Background=\"{DynamicResource Shell.Brush.Surface}\"",
                $"theme governance: {name} uses the AppShell surface token");
            Contains(source, "Shell.Brush.TextPrimary",
                $"theme governance: {name} uses the host primary text token");
        }

        foreach (var name in new[]
                 {
                     "OverviewView.xaml", "MetaView.xaml", "BranchHistoryView.xaml",
                     "BranchTreeView.xaml", "ProjectOperationsView.xaml",
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
                     "Shell.Brush.AccentSoft", "Shell.Brush.Accent", "Shell.Brush.Hairline",
                 })
        {
            Contains(projectOperations, token,
                $"theme governance: project operations uses {token}");
        }
    }
}
