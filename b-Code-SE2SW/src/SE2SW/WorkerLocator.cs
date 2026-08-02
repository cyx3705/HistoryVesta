using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SE2SW;

public static class WorkerLocator
{
    private const string WorkerFileName = "SE2SW.Worker.exe";

    public static string Locate()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return Candidates().First();
    }

    public static IReadOnlyList<string> Candidates()
    {
        var candidates = new List<string>();
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
            candidates.Add(Path.Combine(Path.GetDirectoryName(assemblyLocation)!, WorkerFileName));

        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OneHistoryStudio");
        var configuredModules = ReadConfiguredModulesDirectory(Path.Combine(appDataRoot, "settings.json"));
        if (!string.IsNullOrWhiteSpace(configuredModules) && Path.IsPathFullyQualified(configuredModules))
        {
            candidates.Add(Path.Combine(configuredModules, "SE2SW", WorkerFileName));
            candidates.Add(Path.Combine(configuredModules, WorkerFileName));
        }

        var defaultModules = Path.Combine(appDataRoot, "Modules");
        candidates.Add(Path.Combine(defaultModules, "SE2SW", WorkerFileName));
        candidates.Add(Path.Combine(defaultModules, WorkerFileName));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ReadConfiguredModulesDirectory(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "module.dir", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        return null;
    }
}
