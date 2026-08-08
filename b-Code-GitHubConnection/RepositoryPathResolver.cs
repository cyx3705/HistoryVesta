using System.IO;
using System.Text.Json;

namespace GitHubConnection;

public sealed class RepositoryPathResolver
{
    public const string DefaultRepository = @"C:\OneHistory\HistoryVesta\HistoryVesta.git";

    private readonly string _settingsPath;
    private readonly string _defaultRepository;

    public RepositoryPathResolver(string? settingsPath = null, string? defaultRepository = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OneHistoryStudio", "settings.json");
        _defaultRepository = Path.GetFullPath(defaultRepository ?? DefaultRepository);
    }

    public string Resolve()
    {
        try
        {
            using var stream = new FileStream(
                _settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("proj.barerepo", out var property)
                && property.ValueKind == JsonValueKind.String
                && property.GetString() is { } configured
                && !string.IsNullOrWhiteSpace(configured)
                && Path.IsPathFullyQualified(configured))
            {
                return Path.GetFullPath(configured);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }

        return _defaultRepository;
    }
}
