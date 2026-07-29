using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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

        foreach (var relativePath in new[]
                 {
                     "b-Code-Studio/Studio.csproj",
                     "b-Code-Studio.Service/Studio.Service.csproj",
                 })
        {
            var evaluated = await EvaluateAsync(Path.Combine(ParentDir, relativePath));
            AssertEvaluatedVersion(evaluated, studioVersion, relativePath);
        }

        AssertPackageConsumers();
        await AssertPublishAreaGovernanceAsync();
        await AssertPublishTransactionAsync(studioRoot);
        Console.WriteLine(
            $"VersionProjectionSmoke: PASS (OHS {studioVersion}, AppShell {FrozenAppShellVersion})");
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

        var serviceProject = XDocument.Load(Path.Combine(
            ParentDir, "b-Code-Studio.Service", "Studio.Service.csproj"));
        var serviceHost = serviceProject.Descendants("PackageReference").Single(item =>
            ((string?)item.Attribute("Include"))?.Equals(
                "OneHistory.AppShell.ServiceHost", StringComparison.OrdinalIgnoreCase) == true);
        Equal(FrozenAppShellVersion, (string?)serviceHost.Attribute("Version"),
            "package consumption: service host uses the frozen version");

        foreach (var project in new[] { studioProject, serviceProject })
        {
            True(project.Descendants("ProjectReference").All(item =>
                    !(((string?)item.Attribute("Include")) ?? "")
                        .Contains("b-Code-AppShell", StringComparison.OrdinalIgnoreCase)),
                "package consumption: no AppShell source project reference remains");
        }

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
        Contains(publish, "Invoke-DirectoryPromotion",
            "publish governance: OHS uses the tested promotion transaction");
        Contains(publish, "\"b-Publish\"",
            "publish governance: b-Publish is the local build and history root");
        Contains(publish, "\"current\"",
            "publish governance: b-Publish/current is the replaceable candidate");
        Contains(publish, "history\\OneHistoryStudio",
            "publish governance: prior releases stay under b-Publish history");
        Contains(publish, "quarantine\\OneHistoryStudio",
            "publish governance: failed releases stay under b-Publish quarantine");
        True(!publish.Contains("Join-Path $RepoRoot \"stage\"", StringComparison.Ordinal),
            "publish governance: the removed stage root is not recreated");
        Contains(publish, "\"z-Package\"",
            "publish governance: z-Package is the formal package root");

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
            "version projection: ignored staging has no tracked LFS contract");
        True(Directory.Exists(Path.Combine(ParentDir, "z-Package")),
            "version projection: formal package root exists");

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
