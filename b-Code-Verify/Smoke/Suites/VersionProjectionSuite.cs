using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AppShell.Core.Commands;
using AppShell.ServiceHost;
using AppShell.Services;
using AppShell.Shell;
using OneHistoryStudio.Git;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

/// <summary>验证 OHS 自有版本与冻结 AppShell 包版本彼此独立且完整投影。</summary>
internal static class VersionProjectionSuite
{
    private const string FrozenAppShellVersion = "3.0.3";

    public static async Task RunAsync(string[] args)
    {
        var studioRoot = Path.Combine(ParentDir, "b-Code-Studio");
        var studioVersion = ReadSingleVersion(
            Path.Combine(studioRoot, "StudioVersion.props"), "OneHistoryStudioVersion");

        AssertRuntimeAssemblies(FrozenAppShellVersion,
        [
            typeof(CommandBus).Assembly,
            typeof(SettingsService).Assembly,
            typeof(ShellWindow).Assembly,
            typeof(ServiceComposition).Assembly,
        ]);
        AssertRuntimeAssemblies(studioVersion, [typeof(ProjectService).Assembly]);

        const string studioProject = "b-Code-Studio/Studio.csproj";
        var evaluated = await EvaluateAsync(Path.Combine(ParentDir, studioProject));
        AssertEvaluatedVersion(evaluated, studioVersion, studioProject);

        AssertPackageConsumers();
        AssertCurrentSourceAndDocumentation();
        await AssertPublishAreaGovernanceAsync();
        await AssertPublishTransactionAsync(studioRoot);
        Console.WriteLine($"version projection: OHS {studioVersion}, AppShell {FrozenAppShellVersion}");
    }

    private static string ReadSingleVersion(string path, string propertyName)
    {
        var values = XDocument.Load(path).Descendants(propertyName)
            .Select(element => element.Value.Trim())
            .Where(value => value.Length != 0)
            .ToArray();
        Equal(1, values.Length, $"version projection: {propertyName} has one source value");
        True(Version.TryParse(values[0], out var parsed) && parsed.Revision < 0,
            $"version projection: {propertyName} is a three-part version");
        return values[0];
    }

    private static void AssertRuntimeAssemblies(string expected, IEnumerable<Assembly> assemblies)
    {
        var expectedAssemblyVersion = Version.Parse($"{expected}.0");
        foreach (var assembly in assemblies.Distinct())
        {
            var name = assembly.GetName().Name ?? "<unknown>";
            Equal(expectedAssemblyVersion, assembly.GetName().Version,
                $"version projection: {name} assembly version");
            Equal($"{expected}.0",
                assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
                $"version projection: {name} file version");
            Equal(expected,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                $"version projection: {name} informational version");
        }
    }

    private static void AssertEvaluatedVersion(JsonElement evaluation, string expected, string project)
    {
        Equal(expected, Property(evaluation, "OneHistoryStudioVersion"),
            $"version projection: {project} source");
        Equal(expected, Property(evaluation, "VersionPrefix"),
            $"version projection: {project} prefix");
        Equal(expected, Property(evaluation, "Version"),
            $"version projection: {project} version");
        Equal($"{expected}.0", Property(evaluation, "AssemblyVersion"),
            $"version projection: {project} assembly version");
        Equal($"{expected}.0", Property(evaluation, "FileVersion"),
            $"version projection: {project} file version");
        Equal(expected, Property(evaluation, "InformationalVersion"),
            $"version projection: {project} informational version");
    }

    private static string Property(JsonElement evaluation, string name)
        => evaluation.GetProperty("Properties").GetProperty(name).GetString()
           ?? throw new InvalidOperationException($"MSBuild property {name} is null");

    private static async Task<JsonElement> EvaluateAsync(string project)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = ParentDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "msbuild", project, "-nologo",
                     "-getProperty:OneHistoryStudioVersion", "-getProperty:VersionPrefix",
                     "-getProperty:Version", "-getProperty:AssemblyVersion",
                     "-getProperty:FileVersion", "-getProperty:InformationalVersion",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Unable to start dotnet msbuild");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"MSBuild evaluation failed for {project}: {stderr}\n{stdout}");

        using var json = JsonDocument.Parse(stdout);
        return json.RootElement.Clone();
    }

    private static void AssertPackageConsumers()
    {
        var expectedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OneHistory.AppShell.Core",
            "OneHistory.AppShell.Services",
            "OneHistory.AppShell.Shell",
            "OneHistory.AppShell.ServiceHost",
        };
        var studioProject = XDocument.Load(Path.Combine(ParentDir, "b-Code-Studio", "Studio.csproj"));
        var studioPackages = studioProject.Descendants("PackageReference")
            .Where(item => expectedPackages.Contains((string?)item.Attribute("Include") ?? ""))
            .ToArray();
        Equal(4, studioPackages.Length, "package consumption: Studio references all four AppShell packages");
        True(studioPackages.All(item => (string?)item.Attribute("Version") == FrozenAppShellVersion),
            "package consumption: Studio AppShell references use the frozen version");

        True(studioProject.Descendants("ProjectReference").All(item =>
                !(((string?)item.Attribute("Include")) ?? "")
                    .Contains("b-Code-AppShell", StringComparison.OrdinalIgnoreCase)),
            "package consumption: no AppShell source project reference remains");
        True(!Directory.Exists(Path.Combine(ParentDir, "b-Code-Studio.Service")),
            "single entry: legacy Service project directory is removed");

        var nuget = XDocument.Load(Path.Combine(ParentDir, "nuget.config"));
        True(nuget.Descendants("add").Any(item =>
                ((string?)item.Attribute("value"))?.Replace('/', '\\')
                    .Equals("..\\2026-023-AppShell\\z-Package-AppShell\\feed",
                        StringComparison.OrdinalIgnoreCase) == true),
            "package consumption: NuGet source points to the independent AppShell project");

        True(!Directory.Exists(Path.Combine(ParentDir, "b-Code-AppShell")),
            "repository boundary: AppShell source is not embedded in OHS");
        True(!Directory.Exists(Path.Combine(ParentDir, "z-Package-AppShell")),
            "repository boundary: AppShell package repository is not duplicated in OHS");

        var publish = File.ReadAllText(Path.Combine(
            ParentDir, "b-Code-Studio", "eng", "Publish-Studio.ps1"));
        True(!publish.Contains("Publish-AppShell", StringComparison.OrdinalIgnoreCase)
             && !publish.Contains("b-Code-AppShell", StringComparison.OrdinalIgnoreCase),
            "publish boundary: OHS publish does not build or publish AppShell");
        Contains(publish, "sourceDirty", "publish governance: OHS records source state");
        Contains(publish, "\"b-Code-Studio\", \"b-Code-Verify\", \"b-Office\"",
            "publish governance: product, verification, and documentation must all be clean");
        Contains(publish, "Invoke-DirectoryPromotion",
            "publish governance: OHS uses the tested promotion transaction");
        Contains(publish, "\"b-Publish\"",
            "publish governance: b-Publish is the local build and history root");
        Contains(publish, "\"candidate\"",
            "publish governance: b-Publish/candidate is the replaceable release candidate");
        Contains(publish, "Removed legacy b-Publish/current staging slot",
            "publish governance: the retired current slot is cleaned during migration");
        Contains(publish, "Join-Path $PublishRoot \"history\"",
            "publish governance: formal package history is flat under b-Publish/history");
        True(!publish.Contains("history/candidate", StringComparison.OrdinalIgnoreCase)
             && !publish.Contains("Join-Path $HistoryRoot \"candidate", StringComparison.Ordinal),
            "publish governance: release candidates are never archived as history");
        Contains(publish, "Previous candidate discarded after successful replacement",
            "publish governance: only the latest release candidate is retained");
        True(!publish.Contains("Join-Path $RepoRoot \"stage\"", StringComparison.Ordinal),
            "publish governance: the removed stage root is not recreated");
        Contains(publish, "\"z-Package\"",
            "publish governance: z-Package is the formal package root");

        var developmentDeployPath = Path.Combine(
            ParentDir, "b-Code-Studio", "eng", "Test-Deploy-Studio.ps1");
        True(File.Exists(developmentDeployPath),
            "development governance: lightweight test deployment entry exists");
        var developmentDeploy = File.ReadAllText(developmentDeployPath);
        Contains(developmentDeploy, "\"candidate\"",
            "development governance: lightweight verification writes the shared candidate slot");
        True(!developmentDeploy.Contains("dev-current", StringComparison.OrdinalIgnoreCase),
            "development governance: no extra development delivery layer exists");
        Contains(developmentDeploy, "--suite",
            "development governance: targeted Smoke is required");
        True(!developmentDeploy.Contains("-c\", \"Release", StringComparison.Ordinal)
             && !developmentDeploy.Contains("--generate-manual", StringComparison.Ordinal)
             && !developmentDeploy.Contains("\"z-Package\"", StringComparison.Ordinal),
            "development governance: lightweight verification does not cross release gates");

        var deployPath = Path.Combine(ParentDir, "b-Code-Studio", "eng", "Deploy-Studio.ps1");
        True(File.Exists(deployPath), "deployment governance: deployment entry exists");
        var deploy = File.ReadAllText(deployPath);
        Contains(deploy, "\"z-Package\"",
            "deployment governance: deployment consumes the formal package root");
        Contains(deploy, "C:\\OneHistory\\OneHistory-Push",
            "deployment governance: deployment target remains OneHistory-Push");
        Contains(deploy, "Join-Path $applicationDataRoot \"data\"",
            "deployment governance: deployment backs up the current data/main.db location");
    }

    private static void AssertCurrentSourceAndDocumentation()
    {
        var studioRoot = Path.Combine(ParentDir, "b-Code-Studio");
        var historicalToken = new Regex(
            @"\bV\d+\.\d+(?:\.\d+)?\b|\bV\d+-[A-Z0-9]+\b|\bM\d+(?:\.\d+)?\b|\b(?!SHA-)[A-Z]{1,5}-\d{2,}(?:-\d+)?\b|\bQ\d{3,}(?:-\d+)?\b|\b[AD]\d{3,}(?:-\d+)?\b|移植自|原样迁入|今后每个版本|历史清理",
            RegexOptions.CultureInvariant);
        var productionFiles = Directory.EnumerateFiles(studioRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".cs", ".xaml", ".props", ".csproj" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                               StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                               StringComparison.OrdinalIgnoreCase));
        foreach (var path in productionFiles)
        {
            True(!historicalToken.IsMatch(File.ReadAllText(path)),
                $"current source contains no release-history narration: {Path.GetRelativePath(ParentDir, path)}");
        }

        var officeRoot = Path.Combine(ParentDir, "b-Office");
        var currentRoot = Path.Combine(officeRoot, "current");
        var packageRoot = Path.Combine(officeRoot, "package");
        var historyRoot = Path.Combine(officeRoot, "history");
        True(Directory.Exists(currentRoot) && Directory.Exists(packageRoot) && Directory.Exists(historyRoot),
            "documentation follows the current/package/history contract");
        True(!Directory.Exists(Path.Combine(officeRoot, "meta"))
             && !Directory.Exists(Path.Combine(officeRoot, "versions"))
             && !Directory.Exists(Path.Combine(officeRoot, "evidence")),
            "retired documentation directories are absent");
        var currentDocuments = Directory.EnumerateFiles(currentRoot, "*.md")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        var packageDocuments = Directory.EnumerateFiles(packageRoot, "*.md")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        True(currentDocuments.SetEquals(["使用说明.md", "项目库与备份.md", "发布与升级.md"]),
            "current contains the reviewed OHS-owned contracts");
        True(packageDocuments.SetEquals(["命令手册.md", "模块开发手册.md", "MCP接入与安全.md"]),
            "package contains the reviewed consumer contracts");
        var documentationCenter = Path.Combine(officeRoot, "文档中心.md");
        True(File.Exists(documentationCenter), "b-Office has a root documentation center");
        Equal(0, Directory.EnumerateFiles(officeRoot, "README.md", SearchOption.AllDirectories).Count(),
            "b-Office subdirectories contain no independent README");

        foreach (var path in Directory.EnumerateFiles(currentRoot, "*.md")
                     .Concat(Directory.EnumerateFiles(packageRoot, "*.md")))
        {
            True(!File.ReadAllLines(path).Any(line =>
                    line.StartsWith("> 适用版本：", StringComparison.Ordinal)
                    || line.StartsWith("> 当前版本：", StringComparison.Ordinal)),
                $"current manual has no hand-synchronized applicability version: {Path.GetFileName(path)}");
        }
        var officeNavigation = File.ReadAllText(documentationCenter);
        True(!officeNavigation.Contains("> 当前版本：", StringComparison.Ordinal),
            "documentation center has no hand-synchronized current version");

        var moduleManual = File.ReadAllText(Path.Combine(packageRoot, "模块开发手册.md"));
        foreach (var forbidden in new[]
                 {
                     "计划为 4.0",
                     "下一代合同",
                     "下一条公共契约线",
                     "多来源/外部只读模块目录装载合同",
                 })
        {
            True(!moduleManual.Contains(forbidden, StringComparison.Ordinal),
                $"module manual does not predeclare AppShell roadmap: {forbidden}");
        }
    }

    private static async Task AssertPublishAreaGovernanceAsync()
    {
        var gitIgnore = File.ReadAllLines(Path.Combine(ParentDir, ".gitignore"));
        True(!gitIgnore.Any(line => line.Trim().Equals("stage/", StringComparison.Ordinal)),
            "version projection: the retired stage directory is not part of current governance");
        True(gitIgnore.Any(line => line.Trim().Equals("b-Publish/", StringComparison.Ordinal)),
            "version projection: local b-Publish build and history data is ignored");

        var gitAttributes = File.ReadAllLines(Path.Combine(ParentDir, ".gitattributes"));
        True(gitAttributes.Any(line => line.StartsWith("z-Package/**/*.dll ", StringComparison.Ordinal)),
            "version projection: formal package binaries use Git LFS");
        True(!gitAttributes.Any(line => line.StartsWith("b-Publish/**/*.dll ", StringComparison.Ordinal)),
            "version projection: ignored local publish area has no tracked LFS contract");
        True(Directory.Exists(Path.Combine(ParentDir, "z-Package")),
            "version projection: formal package root exists");
        var formalPackageFiles = Directory.EnumerateFiles(
                Path.Combine(ParentDir, "z-Package"), "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToArray();
        True(formalPackageFiles.All(name =>
                name is not null
                && !name.StartsWith("OneHistoryStudio.Service.", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("OneHistoryStudio.LegacyServiceHost.", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("LegacyServiceHost.", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("LegacyServiceProgram.", StringComparison.OrdinalIgnoreCase)),
            "version projection: formal package contains no retired Service artifacts");

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = ParentDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("b-Publish");
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Unable to inspect tracked b-Publish files");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        True(process.ExitCode == 0,
            $"version projection: git ls-files b-Publish succeeds: {stderr}");
        True(string.IsNullOrWhiteSpace(stdout),
            "version projection: b-Publish contains no tracked files");
    }

    private static async Task AssertPublishTransactionAsync(string studioRoot)
    {
        var script = Path.Combine(studioRoot, "eng", "tests", "Publish-Transaction.Smoke.ps1");
        True(File.Exists(script), "version projection: publish transaction smoke exists");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("Unable to start publish transaction smoke");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        True(process.ExitCode == 0,
            $"version projection: publish transaction smoke exits successfully: {stderr}\n{stdout}");
        Contains(stdout, "PublishTransactionSmoke: PASS",
            "version projection: publish transaction state machine passes");
    }
}
