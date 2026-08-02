using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ActiveDock;

public sealed record DockProject(
    string Name,
    string Number,
    string Path,
    DateTimeOffset LastActivity,
    bool Pinned);

internal static partial class ActiveDockState
{
    private static readonly object Gate = new();
    private static readonly string AppRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OneHistoryStudio");
    private static readonly string DockRoot = Path.Combine(AppRoot, "ActiveDock");
    private static readonly string StatePath = Path.Combine(DockRoot, "state.json");
    private static readonly string SettingsPath = Path.Combine(AppRoot, "settings.json");
    private static DockPreferences _preferences = LoadPreferences();
    private static IReadOnlyList<DockProject> _projects = [];

    public static event Action? Changed;

    public static IReadOnlyList<DockProject> Projects
    {
        get
        {
            lock (Gate)
                return _projects.ToList();
        }
    }

    public static bool Hidden
    {
        get
        {
            lock (Gate)
                return _preferences.Hidden;
        }
    }

    /// <summary>用户调整后的尺寸；缺省回退到默认值。位置不再持久化，窗口恒定吸附右下角。</summary>
    public static (double Width, double Height) Size
    {
        get
        {
            lock (Gate)
            {
                return (
                    DockLayout.ClampWidth(_preferences.Width ?? DockLayout.DefaultWidth),
                    DockLayout.ClampHeight(_preferences.Height ?? DockLayout.DefaultHeight));
            }
        }
    }

    public static async Task<IReadOnlyList<DockProject>> RefreshAsync()
    {
        var projects = await Task.Run(ScanProjects).ConfigureAwait(false);
        lock (Gate)
            _projects = projects;
        Changed?.Invoke();
        return projects;
    }

    public static bool Pin(string name, bool pinned)
    {
        lock (Gate)
        {
            var project = _projects.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || item.Number.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (project == null)
                return false;
            if (pinned)
                _preferences.Pins.Add(project.Name);
            else
                _preferences.Pins.Remove(project.Name);
            SavePreferences();
            _projects = _projects.Select(item => item with
            {
                Pinned = _preferences.Pins.Contains(item.Name),
            })
                .OrderByDescending(item => item.Pinned)
                .ThenByDescending(item => item.LastActivity)
                .ToList();
        }

        Changed?.Invoke();
        return true;
    }

    public static void SetHidden(bool hidden)
    {
        lock (Gate)
        {
            _preferences.Hidden = hidden;
            SavePreferences();
        }
        Changed?.Invoke();
    }

    public static void SaveSize(double width, double height)
    {
        lock (Gate)
        {
            _preferences.Width = DockLayout.ClampWidth(width);
            _preferences.Height = DockLayout.ClampHeight(height);
            SavePreferences();
        }
    }

    private static IReadOnlyList<DockProject> ScanProjects()
    {
        var root = ReadWorktreeRoot();
        if (!Directory.Exists(root))
            return [];

        HashSet<string> pins;
        lock (Gate)
            pins = new HashSet<string>(_preferences.Pins, StringComparer.OrdinalIgnoreCase);

        var all = new List<DockProject>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            var match = ProjectNumber().Match(name);
            if (!match.Success)
                continue;
            all.Add(new DockProject(
                name,
                match.Value,
                directory,
                ReadLastActivity(directory),
                pins.Contains(name)));
        }

        var pinned = all.Where(item => item.Pinned);
        var recent = all.Where(item => !item.Pinned)
            .OrderByDescending(item => item.LastActivity)
            .Take(12);
        return pinned.Concat(recent)
            .OrderByDescending(item => item.Pinned)
            .ThenByDescending(item => item.LastActivity)
            .ToList();
    }

    private static string ReadWorktreeRoot()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("proj.worktreeroot", out var value)
                    && value.GetString() is { Length: > 0 } configured)
                {
                    return configured;
                }
            }
        }
        catch (JsonException)
        {
        }

        return @"C:\OneHistory\OneHistory-Projects";
    }

    private static DateTimeOffset ReadLastActivity(string directory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "-C", directory, "log", "-1", "--format=%ct" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null)
                return Directory.GetLastWriteTimeUtc(directory);
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            if (process.ExitCode == 0 && long.TryParse(text.Trim(), out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception)
        {
        }

        return Directory.GetLastWriteTimeUtc(directory);
    }

    private static DockPreferences LoadPreferences()
    {
        Directory.CreateDirectory(DockRoot);
        try
        {
            if (File.Exists(StatePath))
            {
                var state = JsonSerializer.Deserialize<DockPreferences>(File.ReadAllText(StatePath));
                if (state != null)
                {
                    state.Pins = new HashSet<string>(state.Pins, StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception)
        {
        }

        return new DockPreferences();
    }

    private static void SavePreferences()
    {
        Directory.CreateDirectory(DockRoot);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(_preferences, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    [GeneratedRegex(@"\d{4}-\d{3}", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNumber();

    /// <summary>
    /// 1.0.0 的 Left/Top 字段已作废：窗口恒定吸附右下角，不再持久化位置。
    /// 反序列化默认忽略未知字段，旧 state.json 可直接读取，固定项不丢。
    /// </summary>
    private sealed class DockPreferences
    {
        public HashSet<string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Hidden { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
    }
}
