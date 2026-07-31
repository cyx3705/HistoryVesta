using System.Text.RegularExpressions;
using System.Xml.Linq;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

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
            "Wiring", "VersionProjection", "TestArchitecture", "GitRules",
            "PromptGovernance", "BranchHistory", "SubmoduleSafety", "RepositoryTargets",
            "ServiceWeb", "Docking", "GitHubAccount", "LanSingleExe",
        ];
        True(registered.SequenceEqual(expected),
            "test runner registers the reviewed functional suite order");
        Equal(registered.Length, registered.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "test runner suite names are unique");
        True(!Regex.IsMatch(program, "\\\"V\\d", RegexOptions.CultureInvariant),
            "test runner exposes no version-named suite");

        var project = XDocument.Load(Path.Combine(smokeRoot, "Smoke.csproj"));
        Equal(1, project.Descendants("ProjectReference").Count(),
            "test architecture uses one product reference and one compiled host");
        var kit = File.ReadAllText(Path.Combine(smokeRoot, "SmokeKit.cs"));
        Contains(kit, "Path.GetTempPath()",
            "test architecture centralizes temporary data outside the source tree");

        return Task.CompletedTask;
    }
}
