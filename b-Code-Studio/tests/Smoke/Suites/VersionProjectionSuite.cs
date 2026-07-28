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

/// <summary>R275-03:版本真值必须投影到所有程序集、包、发布入口和消费示例。</summary>
internal static class VersionProjectionSuite
{
    public static async Task RunAsync(string[] args)
    {
        var appShellRoot = Path.Combine(ParentDir, "b-Code-AppShell");
        var studioRoot = Path.Combine(ParentDir, "b-Code-Studio");
        var appShellVersion = ReadSingleVersion(
            Path.Combine(appShellRoot, "AppShellVersion.props"), "AppShellVersion");
        var studioVersion = ReadSingleVersion(
            Path.Combine(studioRoot, "StudioVersion.props"), "OneHistoryStudioVersion");

        Equal(appShellVersion, studioVersion, "version projection: AppShell and OHS share the release train");

        AssertRuntimeAssemblies(appShellVersion, new[]
        {
            typeof(CommandBus).Assembly,
            typeof(SettingsService).Assembly,
            typeof(ShellWindow).Assembly,
            typeof(ServiceComposition).Assembly,
        });
        AssertRuntimeAssemblies(studioVersion, new[] { typeof(ProjectService).Assembly });

        var appShellProjects = new Dictionary<string, string?>
        {
            ["src/AppShell.Core/AppShell.Core.csproj"] = "OneHistory.AppShell.Core",
            ["src/AppShell.Services/AppShell.Services.csproj"] = "OneHistory.AppShell.Services",
            ["src/AppShell.Shell/AppShell.Shell.csproj"] = "OneHistory.AppShell.Shell",
            ["src/AppShell.ServiceHost/AppShell.ServiceHost.csproj"] = "OneHistory.AppShell.ServiceHost",
            ["src/App/App.csproj"] = null,
            ["tests/PackageSmoke/PackageSmoke.csproj"] = null,
        };
        foreach (var (relativePath, packageId) in appShellProjects)
        {
            var evaluated = await EvaluateAsync(Path.Combine(appShellRoot, relativePath), includePackageVersions: false);
            AssertEvaluatedVersion(evaluated, "AppShellVersion", appShellVersion, relativePath);
            if (packageId != null)
                Equal(packageId, Property(evaluated, "PackageId"), $"version projection: {relativePath} package id");
        }

        foreach (var relativePath in new[]
                 {
                     "b-Code-Studio/Studio.csproj",
                     "b-Code-Studio.Service/Studio.Service.csproj",
                 })
        {
            var evaluated = await EvaluateAsync(Path.Combine(ParentDir, relativePath), includePackageVersions: false);
            AssertEvaluatedVersion(evaluated, "OneHistoryStudioVersion", studioVersion, relativePath);
        }

        var packageSmoke = await EvaluateAsync(
            Path.Combine(appShellRoot, "tests", "PackageSmoke", "PackageSmoke.csproj"),
            includePackageVersions: true);
        var centralVersions = packageSmoke.GetProperty("Items").GetProperty("PackageVersion")
            .EnumerateArray()
            .Where(item => item.GetProperty("Identity").GetString() is
                "OneHistory.AppShell.Shell" or "OneHistory.AppShell.ServiceHost")
            .ToArray();
        Equal(2, centralVersions.Length, "version projection: two central AppShell package references");
        True(centralVersions.All(item => item.GetProperty("Version").GetString() == appShellVersion),
            "version projection: central AppShell package references use the single source");

        AssertMachineConsumers(appShellRoot, appShellVersion);
        await AssertPublishTransactionAsync(studioRoot);
        AssertCurrentMarkdownLinks(appShellRoot, studioRoot);
        Console.WriteLine($"VersionProjectionSmoke: PASS (AppShell/OHS {appShellVersion})");
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

    private static void AssertEvaluatedVersion(
        JsonElement evaluation,
        string sourceProperty,
        string expected,
        string project)
    {
        Equal(expected, Property(evaluation, sourceProperty), $"version projection: {project} source");
        Equal(expected, Property(evaluation, "VersionPrefix"), $"version projection: {project} prefix");
        Equal(expected, Property(evaluation, "Version"), $"version projection: {project} version");
        Equal(expected, Property(evaluation, "PackageVersion"), $"version projection: {project} package version");
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

    private static async Task<JsonElement> EvaluateAsync(string project, bool includePackageVersions)
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
                     "-getProperty:AppShellVersion", "-getProperty:OneHistoryStudioVersion",
                     "-getProperty:VersionPrefix", "-getProperty:Version", "-getProperty:PackageVersion",
                     "-getProperty:AssemblyVersion", "-getProperty:FileVersion",
                     "-getProperty:InformationalVersion", "-getProperty:PackageId",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        if (includePackageVersions)
            start.ArgumentList.Add("-getItem:PackageVersion");

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

    private static void AssertMachineConsumers(string appShellRoot, string expected)
    {
        var studioRoot = Path.Combine(ParentDir, "b-Code-Studio");
        var buildProps = File.ReadAllText(Path.Combine(appShellRoot, "Directory.Build.props"));
        Contains(buildProps, "<Version>$(AppShellVersion)</Version>",
            "version projection: build props derives package version");
        Contains(buildProps, "<FileVersion>$(AppShellVersion).0</FileVersion>",
            "version projection: build props derives file version");

        var publish = File.ReadAllText(Path.Combine(appShellRoot, "eng", "Publish-AppShell.ps1"));
        Contains(publish, "-getProperty:AppShellVersion", "version projection: publish reads MSBuild source");
        Contains(publish, "$demoVersion -ne $ExpectedFileVersion",
            "version projection: publish validates derived demo identity");
        Contains(publish, "version = $Version", "version projection: manifest uses evaluated version");
        Contains(publish, "Threading.Mutex", "version projection: publish serializes staging writers");
        Contains(publish, "dotnet restore AppShell.sln --locked-mode --force-evaluate",
            "version projection: isolated publish repairs the workspace asset graph");
        True(!Regex.IsMatch(publish, @"\[string\]\$Version\s*=\s*\""\d"),
            "version projection: publish has no independent default version");

        var studioPublishPath = Path.Combine(studioRoot, "eng", "Publish-Studio.ps1");
        True(File.Exists(studioPublishPath), "version projection: OHS publish entry exists");
        var studioPublish = File.ReadAllText(studioPublishPath);
        Contains(studioPublish, "-getProperty:OneHistoryStudioVersion",
            "version projection: OHS publish reads MSBuild source");
        Contains(studioPublish, "--generate-manual",
            "version projection: OHS publish regenerates the command manual");
        Contains(studioPublish, "sourceDirty",
            "version projection: OHS publish records and guards source state");
        Contains(studioPublish, "b-Publish-pre-$Version",
            "version projection: OHS formal publish keeps a rollback snapshot");
        Contains(studioPublish, "Invoke-DirectoryPromotion",
            "version projection: OHS formal publish uses the tested promotion transaction");
        Contains(studioPublish, "b-Publish-failed-$Version",
            "version projection: OHS formal publish quarantines a failed candidate");
        True(!Regex.IsMatch(studioPublish, @"\[string\]\$Version\s*=\s*\""\d"),
            "version projection: OHS publish has no independent default version");

        var packageSmoke = File.ReadAllText(Path.Combine(appShellRoot, "tests", "PackageSmoke", "Program.cs"));
        True(!Regex.IsMatch(packageSmoke, @"new Version\s*\(\s*\d"),
            "version projection: PackageSmoke has no independent assembly version");
        True(!Regex.IsMatch(packageSmoke, @"AppVersion\s*=\s*\""\d"),
            "version projection: PackageSmoke app identity is projected");

        foreach (var relativePath in new[] { "README.md", "PACKAGE.md" })
        {
            var content = File.ReadAllText(Path.Combine(appShellRoot, relativePath));
            var references = Regex.Matches(content,
                @"<PackageReference Include=\""OneHistory\.AppShell\.(?:Shell|ServiceHost)\"" Version=\""(?<version>[^\""\r\n]+)\"" />");
            Equal(2, references.Count, $"version projection: {relativePath} has two install references");
            True(references.Cast<Match>().All(match => match.Groups["version"].Value == expected),
                $"version projection: {relativePath} install references match source");
        }

        var readme = File.ReadAllText(Path.Combine(appShellRoot, "README.md"));
        var publishExamples = Regex.Matches(readme,
            @"Publish-AppShell\.ps1 -Version (?<version>\d+\.\d+\.\d+)");
        Equal(2, publishExamples.Count, "version projection: README has staging and publish examples");
        True(publishExamples.Cast<Match>().All(match => match.Groups["version"].Value == expected),
            "version projection: README publish examples match source");

        AssertCurrentDocumentation(appShellRoot, studioRoot, expected);
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

    private static void AssertCurrentDocumentation(
        string appShellRoot,
        string studioRoot,
        string expected)
    {
        var package = File.ReadAllText(Path.Combine(appShellRoot, "PACKAGE.md"));
        foreach (var packageId in new[]
                 {
                     "OneHistory.AppShell.Core",
                     "OneHistory.AppShell.Services",
                     "OneHistory.AppShell.Shell",
                     "OneHistory.AppShell.ServiceHost",
                 })
        {
            Contains(package, $"`{packageId}`", $"documentation projection: package matrix {packageId}");
        }
        Contains(package, $"AppShell {expected} targets .NET 8",
            "documentation projection: package target framework and version");
        Contains(package, "Shell and ServiceHost packages require Windows and WPF",
            "documentation projection: package Windows/WPF boundary");

        var appShellReadme = File.ReadAllText(Path.Combine(appShellRoot, "README.md"));
        Contains(appShellReadme, $"## {expected} ",
            "documentation projection: AppShell current-version heading");
        Contains(appShellReadme, "z-Package-AppShell/feed",
            "documentation projection: AppShell local delivery channel");
        True(!appShellReadme.Contains("待实施", StringComparison.Ordinal),
            "documentation projection: AppShell README has no stale pending status");

        var studioReadme = File.ReadAllText(Path.Combine(studioRoot, "README.md"));
        AssertPublishExamples(studioReadme, "Publish-Studio.ps1", expected, 2,
            "documentation projection: OHS README publish examples");

        var releaseGuide = File.ReadAllText(Path.Combine(
            ParentDir, "b-Office", "meta", "发布与升级.md"));
        Contains(releaseGuide, $"OneHistoryStudio V{expected}",
            "documentation projection: release guide current version");
        AssertPublishExamples(releaseGuide, "Publish-Studio.ps1", expected, 2,
            "documentation projection: release guide OHS examples");
        AssertPublishExamples(releaseGuide, "Publish-AppShell.ps1", expected, 2,
            "documentation projection: release guide AppShell examples");

        var mcpGuide = File.ReadAllText(Path.Combine(
            ParentDir, "b-Office", "meta", "MCP接入与安全.md"));
        True(!Regex.IsMatch(mcpGuide, @"当前全仓共\s*\d+\s*条核心只读命令"),
            "documentation projection: MCP guide has no live readonly-count mirror");
    }

    private static void AssertPublishExamples(
        string content,
        string scriptName,
        string expected,
        int expectedCount,
        string scope)
    {
        var matches = Regex.Matches(content,
            $@"{Regex.Escape(scriptName)} -Version (?<version>\d+\.\d+\.\d+)");
        Equal(expectedCount, matches.Count, $"{scope}: staging and formal entries");
        True(matches.Cast<Match>().All(match => match.Groups["version"].Value == expected),
            $"{scope}: versions match source");
    }

    private static void AssertCurrentMarkdownLinks(string appShellRoot, string studioRoot)
    {
        var officeRoot = Path.Combine(ParentDir, "b-Office");
        var metaRoot = Path.Combine(officeRoot, "meta");
        var versionsRoot = Path.Combine(officeRoot, "versions");
        var evidenceRoot = Path.Combine(officeRoot, "evidence");
        var files = new[]
            {
                Path.Combine(ParentDir, "README.md"),
                Path.Combine(appShellRoot, "README.md"),
                Path.Combine(appShellRoot, "PACKAGE.md"),
                Path.Combine(studioRoot, "README.md"),
                Path.Combine(officeRoot, "README.md"),
            }
            .Concat(Directory.GetFiles(metaRoot, "*.md", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var repositoryPrefix = DirectoryPrefix(ParentDir);
        var metaPrefix = DirectoryPrefix(metaRoot);
        var versionsPrefix = DirectoryPrefix(versionsRoot);
        var evidencePrefix = DirectoryPrefix(evidenceRoot);
        var versionsReadme = Path.GetFullPath(Path.Combine(versionsRoot, "README.md"));
        var localLinkCount = 0;

        foreach (var file in files)
        {
            True(File.Exists(file), $"documentation links: current document exists {file}");
            var content = File.ReadAllText(file);
            foreach (var destination in MarkdownDestinations(content))
            {
                if (destination.StartsWith('#') ||
                    Regex.IsMatch(destination, @"^[a-zA-Z][a-zA-Z0-9+.-]*:"))
                {
                    continue;
                }

                var separator = destination.IndexOfAny(['#', '?']);
                var pathPart = separator >= 0 ? destination[..separator] : destination;
                if (string.IsNullOrWhiteSpace(pathPart))
                    continue;

                True(!Path.IsPathRooted(pathPart),
                    $"documentation links: local link must be repository-relative: {file} -> {destination}");
                var decoded = Uri.UnescapeDataString(pathPart)
                    .Replace('/', Path.DirectorySeparatorChar);
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, decoded));
                localLinkCount++;

                True(resolved.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase),
                    $"documentation links: target stays inside repository: {file} -> {destination}");
                True(File.Exists(resolved) || Directory.Exists(resolved),
                    $"documentation links: target exists: {file} -> {destination}");
                True(!resolved.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase) ||
                     resolved.Equals(versionsReadme, StringComparison.OrdinalIgnoreCase),
                    $"documentation links: current docs do not depend on a V-version record: {file} -> {destination}");

                if (file.StartsWith(metaPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    True(!resolved.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase) &&
                         !resolved.StartsWith(evidencePrefix, StringComparison.OrdinalIgnoreCase),
                        $"documentation links: meta does not depend on versions/evidence: {file} -> {destination}");
                }
            }
        }

        True(localLinkCount > 0, "documentation links: current document set contains local links");
    }

    private static IEnumerable<string> MarkdownDestinations(string content)
    {
        foreach (Match match in Regex.Matches(
                     content,
                     @"!?\[[^\]\r\n]*\]\((?<destination><[^>\r\n]+>|[^\s\)\r\n]+)(?:\s+[^\)]*)?\)"))
        {
            yield return match.Groups["destination"].Value.Trim('<', '>');
        }

        foreach (Match match in Regex.Matches(
                     content,
                     @"(?m)^\s*\[[^\]\r\n]+\]:\s*(?<destination><[^>\r\n]+>|\S+)"))
        {
            yield return match.Groups["destination"].Value.Trim('<', '>');
        }
    }

    private static string DirectoryPrefix(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
