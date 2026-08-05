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
    bool Pinned,
    double Weight = 0,
    double Clicks = 0,
    DateTimeOffset? LastOpened = null,
    bool Excluded = false);

internal static partial class ActiveDockState
{
    private static readonly object Gate = new();
    private static FileSystemWatcher? _watcher;
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

    public static DockPolicy Policy
    {
        get
        {
            lock (Gate)
                return _preferences.Policy.Normalized();
        }
    }

    internal static string WorktreeRoot => ReadWorktreeRoot();

    /// <summary>
    /// 监视 state.json。活动坞在服务进程、管理页面在桌面 Shell 进程，两者不共享内存，
    /// 靠这个文件完成跨进程同步：任一侧写入后，另一侧重载偏好并刷新界面。
    /// </summary>
    public static void StartWatching()
    {
        lock (Gate)
        {
            if (_watcher != null)
                return;
            try
            {
                Directory.CreateDirectory(DockRoot);
                _watcher = new FileSystemWatcher(DockRoot, "state.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, _) => Reload();
                _watcher.Created += (_, _) => Reload();
            }
            catch (Exception)
            {
                _watcher = null;
            }
        }
    }

    /// <summary>重新读入偏好并通知界面。写入方自己触发的事件同样走这里，重载幂等，不会形成回环。</summary>
    private static void Reload()
    {
        try
        {
            var loaded = LoadPreferences();
            lock (Gate)
                _preferences = loaded;
        }
        catch (Exception)
        {
            return;
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    /// <summary>记一次通过活动坞的打开：先衰减到当前时刻，再累加一次点击。</summary>
    public static void RecordOpen(string name)
    {
        lock (Gate)
        {
            var halfLife = _preferences.Policy.Normalized().HalfLifeDays;
            var now = DateTimeOffset.UtcNow;
            if (!_preferences.Usage.TryGetValue(name, out var entry))
                entry = new UsageEntry();
            entry.Score = DockWeight.Accumulate(
                entry.Score, entry.Updated, now, DockWeight.ClickWeight, halfLife);
            entry.Updated = now;
            _preferences.Usage[name] = entry;
            SavePreferences();
        }
    }

    /// <summary>手动排除或恢复收录。</summary>
    public static void Exclude(string name, bool excluded)
    {
        lock (Gate)
        {
            if (excluded)
                _preferences.Excluded.Add(name);
            else
                _preferences.Excluded.Remove(name);
            SavePreferences();
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    /// <summary>清除使用记录；name 为空表示全部。</summary>
    public static void Forget(string? name)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(name))
                _preferences.Usage.Clear();
            else
                _preferences.Usage.Remove(name);
            SavePreferences();
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    public static void SetPolicy(int? minItems, int? maxItems, double? halfLifeDays)
    {
        lock (Gate)
        {
            var policy = _preferences.Policy;
            policy.MinItems = minItems ?? policy.MinItems;
            policy.MaxItems = maxItems ?? policy.MaxItems;
            policy.HalfLifeDays = halfLifeDays ?? policy.HalfLifeDays;
            _preferences.Policy = policy.Normalized();
            SavePreferences();
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    public static async Task<IReadOnlyList<DockProject>> RefreshAsync()
    {
        var projects = await Task.Run(ScanProjects).ConfigureAwait(false);
        lock (Gate)
            _projects = projects;
        if (!DockShortcutFolder.IsExplorerRegistrationDisabled)
        {
            DockShortcutFolder.Synchronize(projects);
            _ = ExplorerNamespaceRegistration.RegisterOrUpdate(DockShortcutFolder.Path);
        }
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
                .ThenByDescending(item => item.Weight)
                .ThenByDescending(item => item.LastActivity)
                .ToList();
        }

        if (!DockShortcutFolder.IsExplorerRegistrationDisabled)
            DockShortcutFolder.Synchronize(Projects);
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
        HashSet<string> excluded;
        Dictionary<string, UsageEntry> usage;
        DockPolicy policy;
        lock (Gate)
        {
            pins = new HashSet<string>(_preferences.Pins, StringComparer.OrdinalIgnoreCase);
            excluded = new HashSet<string>(_preferences.Excluded, StringComparer.OrdinalIgnoreCase);
            usage = new Dictionary<string, UsageEntry>(_preferences.Usage, StringComparer.OrdinalIgnoreCase);
            policy = _preferences.Policy.Normalized();
        }

        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (ProjectNumber().IsMatch(name))
                directories[name] = directory;
        }

        // 只统计项目主文件夹；解析失败时该信号整体缺席，不影响其余排序。
        IReadOnlyDictionary<string, DateTimeOffset> explorerOpens;
        try
        {
            explorerOpens = RecentFolders.Scan(directories);
        }
        catch (Exception)
        {
            explorerOpens = new Dictionary<string, DateTimeOffset>();
        }

        var now = DateTimeOffset.UtcNow;
        var all = new List<DockProject>();
        foreach (var (name, directory) in directories)
        {
            var match = ProjectNumber().Match(name);
            usage.TryGetValue(name, out var entry);
            entry ??= new UsageEntry();
            explorerOpens.TryGetValue(name, out var opened);
            DateTimeOffset? lastOpened = opened == default ? null : opened;
            all.Add(new DockProject(
                name,
                match.Value,
                directory,
                ReadLastActivity(directory),
                pins.Contains(name),
                DockWeight.Combine(entry.Score, entry.Updated, lastOpened, now, policy.HalfLifeDays),
                DockWeight.Decay(entry.Score, entry.Updated, now, policy.HalfLifeDays),
                lastOpened,
                excluded.Contains(name)));
        }

        // 固定项无视权重恒在最前；其余按加权频率降序，同分回退到 git 活动时间。
        var pinned = all.Where(item => item.Pinned && !item.Excluded)
            .OrderByDescending(item => item.Weight)
            .ThenByDescending(item => item.LastActivity)
            .ToList();
        var rest = all.Where(item => !item.Pinned && !item.Excluded)
            .OrderByDescending(item => item.Weight)
            .ThenByDescending(item => item.LastActivity)
            .ToList();

        // 条数：先按上限截断，再用排序结果补足到下限，避免长期不用后列表变空。
        var quota = Math.Max(policy.MaxItems - pinned.Count, 0);
        var selected = pinned.Concat(rest.Take(quota)).ToList();
        if (selected.Count < policy.MinItems)
        {
            selected = pinned
                .Concat(rest.Take(Math.Max(policy.MinItems - pinned.Count, 0)))
                .ToList();
        }

        return selected;
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
                    state.Excluded = new HashSet<string>(state.Excluded, StringComparer.OrdinalIgnoreCase);
                    state.Usage = new Dictionary<string, UsageEntry>(state.Usage, StringComparer.OrdinalIgnoreCase);
                    state.Policy = state.Policy.Normalized();
                    return state;
                }
            }
        }
        catch (Exception)
        {
        }

        return new DockPreferences();
    }

    /// <summary>
    /// 先写临时文件再替换。另一侧进程被 FileSystemWatcher 唤醒时读到的一定是完整内容，
    /// 不会撞上写到一半的 JSON。
    /// </summary>
    private static void SavePreferences()
    {
        Directory.CreateDirectory(DockRoot);
        var payload = JsonSerializer.Serialize(_preferences, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var temporary = StatePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, payload);
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.WriteAllText(StatePath, payload);
            }
            catch (Exception)
            {
                // 偏好写入失败不得影响界面。
            }
        }
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
        public Dictionary<string, UsageEntry> Usage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Excluded { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DockPolicy Policy { get; set; } = new();
    }

    /// <summary>已衰减到 <see cref="Updated"/> 时刻的点击分数，而不是原始次数。</summary>
    internal sealed class UsageEntry
    {
        public double Score { get; set; }
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.UnixEpoch;
    }
}
