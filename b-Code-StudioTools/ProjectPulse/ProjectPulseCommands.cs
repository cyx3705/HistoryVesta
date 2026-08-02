using System.IO;
using System.Text.Json;

namespace ProjectPulse;

public sealed class ProjectPulseCommands
{
    private static readonly HashSet<string> GeneratedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vs", ".pio", "bin", "obj", "TestResults", "node_modules", "Library", "Temp", "Logs", "UserSettings",
    };

    /// <summary>汇总一个 OHS 工作树的文件数量、体积、格式和一级目录热点</summary>
    /// <param name="name">已登记工作树名称，例如 2026-020-OneHistoryStudio</param>
    /// <param name="includeGenerated">是否包含 bin、obj、.vs、node_modules 等生成目录</param>
    /// <param name="top">最多返回多少项格式和一级目录统计，范围 1~50</param>
    public object Summary(string name, bool includeGenerated = false, int top = 10)
    {
        RequireRange(top, 1, 50, nameof(top));
        var scan = Scan(name, includeGenerated);

        var extensions = scan.Files
            .GroupBy(file => file.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SizeGroup(group.Key, group.Count(), group.Sum(file => file.Length),
                FormatBytes(group.Sum(file => file.Length))))
            .OrderByDescending(group => group.Bytes)
            .ThenByDescending(group => group.Files)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

        var directories = scan.Files
            .GroupBy(file => file.TopDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SizeGroup(group.Key, group.Count(), group.Sum(file => file.Length),
                FormatBytes(group.Sum(file => file.Length))))
            .OrderByDescending(group => group.Bytes)
            .ThenByDescending(group => group.Files)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();

        var totalBytes = scan.Files.Sum(file => file.Length);
        return new
        {
            Project = scan.ProjectName,
            scan.ProjectPath,
            Files = scan.Files.Count,
            Bytes = totalBytes,
            Size = FormatBytes(totalBytes),
            LatestWrite = scan.Files.Count == 0
                ? null
                : scan.Files.Max(file => file.LastWriteUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            scan.SkippedDirectories,
            scan.UnreadableEntries,
            Extensions = extensions,
            Directories = directories,
        };
    }

    /// <summary>列出一个 OHS 工作树最近修改的文件</summary>
    /// <param name="name">已登记工作树名称，例如 2026-020-OneHistoryStudio</param>
    /// <param name="days">回看天数，范围 1~3650</param>
    /// <param name="limit">最多返回文件数，范围 1~200</param>
    /// <param name="includeGenerated">是否包含 bin、obj、.vs、node_modules 等生成目录</param>
    public object Recent(string name, int days = 7, int limit = 30, bool includeGenerated = false)
    {
        RequireRange(days, 1, 3650, nameof(days));
        RequireRange(limit, 1, 200, nameof(limit));
        var scan = Scan(name, includeGenerated);
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var files = scan.Files
            .Where(file => file.LastWriteUtc >= cutoff)
            .OrderByDescending(file => file.LastWriteUtc)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(file => new FileResult(file.RelativePath, file.Length, FormatBytes(file.Length),
                file.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")))
            .ToList();

        return new
        {
            Project = scan.ProjectName,
            Since = cutoff.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            Count = files.Count,
            Files = files,
            scan.SkippedDirectories,
            scan.UnreadableEntries,
        };
    }

    /// <summary>列出一个 OHS 工作树中最大的文件</summary>
    /// <param name="name">已登记工作树名称，例如 2026-020-OneHistoryStudio</param>
    /// <param name="limit">最多返回文件数，范围 1~200</param>
    /// <param name="minMb">最小体积 MB，范围 0~1048576</param>
    /// <param name="includeGenerated">是否包含 bin、obj、.vs、node_modules 等生成目录</param>
    public object Largest(string name, int limit = 20, double minMb = 1, bool includeGenerated = false)
    {
        RequireRange(limit, 1, 200, nameof(limit));
        if (double.IsNaN(minMb) || double.IsInfinity(minMb) || minMb < 0 || minMb > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(minMb), "minMb 必须在 0~1048576 之间");

        var scan = Scan(name, includeGenerated);
        var threshold = (long)(minMb * 1024 * 1024);
        var files = scan.Files
            .Where(file => file.Length >= threshold)
            .OrderByDescending(file => file.Length)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(file => new FileResult(file.RelativePath, file.Length, FormatBytes(file.Length),
                file.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")))
            .ToList();

        return new
        {
            Project = scan.ProjectName,
            MinimumMb = minMb,
            Count = files.Count,
            Files = files,
            scan.SkippedDirectories,
            scan.UnreadableEntries,
        };
    }

    private static ScanResult Scan(string name, bool includeGenerated)
    {
        var projectPath = ResolveProject(name, out var projectName);
        var files = new List<FileSnapshot>();
        var directories = new Stack<string>();
        directories.Push(projectPath);
        var skippedDirectories = 0;
        var unreadableEntries = 0;

        while (directories.Count > 0)
        {
            var current = directories.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> childFiles;
            try
            {
                childDirectories = Directory.EnumerateDirectories(current).ToArray();
                childFiles = Directory.EnumerateFiles(current).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadableEntries++;
                continue;
            }

            foreach (var directory in childDirectories)
            {
                var directoryName = Path.GetFileName(directory);
                if (directoryName.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || (!includeGenerated && GeneratedDirectories.Contains(directoryName)))
                {
                    skippedDirectories++;
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedDirectories++;
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadableEntries++;
                    continue;
                }

                directories.Push(directory);
            }

            foreach (var filePath in childFiles)
            {
                if (Path.GetFileName(filePath).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var info = new FileInfo(filePath);
                    var relative = Path.GetRelativePath(projectPath, filePath);
                    var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                    var topDirectory = separator < 0 ? "(root)" : relative[..separator];
                    var extension = string.IsNullOrEmpty(info.Extension)
                        ? "(none)"
                        : info.Extension.ToLowerInvariant();
                    files.Add(new FileSnapshot(relative, topDirectory, extension, info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadableEntries++;
                }
            }
        }

        return new ScanResult(projectName, projectPath, files, skippedDirectories, unreadableEntries);
    }

    private static string ResolveProject(string name, out string projectName)
    {
        projectName = (name ?? "").Trim();
        if (projectName.Length == 0
            || projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || projectName.Contains(Path.DirectorySeparatorChar)
            || projectName.Contains(Path.AltDirectorySeparatorChar)
            || projectName is "." or "..")
        {
            throw new ArgumentException("name 必须是已登记工作树的单一目录名", nameof(name));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsPath = Path.Combine(appData, "OneHistoryStudio", "settings.json");
        if (!File.Exists(settingsPath))
            throw new InvalidOperationException($"找不到 OHS 配置文件: {settingsPath}");

        string? rootValue;
        string? bareRepoValue;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            rootValue = document.RootElement.TryGetProperty("proj.worktreeroot", out var rootElement)
                ? rootElement.GetString()
                : null;
            bareRepoValue = document.RootElement.TryGetProperty("proj.barerepo", out var bareRepoElement)
                ? bareRepoElement.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"读取 OHS 配置失败: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(rootValue))
            throw new InvalidOperationException("OHS 配置缺少 proj.worktreeroot");
        if (string.IsNullOrWhiteSpace(bareRepoValue))
            throw new InvalidOperationException("OHS 配置缺少 proj.barerepo");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue));
        var candidate = Path.GetFullPath(Path.Combine(root, projectName));
        var parent = Directory.GetParent(candidate)?.FullName;
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标越出 OHS 工作树根，已拒绝");
        if (!Directory.Exists(candidate))
            throw new DirectoryNotFoundException($"工作树不存在: {projectName}");
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"工作树根是重解析点，已拒绝: {projectName}");

        var gitMarker = Path.Combine(candidate, ".git");
        if (!File.Exists(gitMarker))
            throw new InvalidOperationException($"目录不是已登记 Git 工作树: {projectName}");

        var marker = File.ReadAllText(gitMarker).Trim();
        const string prefix = "gitdir:";
        if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"工作树 .git 指针格式无效: {projectName}");

        var gitDirText = marker[prefix.Length..].Trim();
        var gitDir = Path.GetFullPath(Path.IsPathRooted(gitDirText)
            ? gitDirText
            : Path.Combine(candidate, gitDirText));
        var managedWorktrees = Path.GetFullPath(Path.Combine(bareRepoValue, "worktrees"));
        if (!gitDir.StartsWith(managedWorktrees + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(gitDir))
        {
            throw new InvalidOperationException($"目录未登记在 OHS 共享裸仓库中: {projectName}");
        }

        return candidate;
    }

    private static void RequireRange(int value, int minimum, int maximum, string parameter)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameter, $"{parameter} 必须在 {minimum}~{maximum} 之间");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private sealed record FileSnapshot(
        string RelativePath,
        string TopDirectory,
        string Extension,
        long Length,
        DateTime LastWriteUtc);

    private sealed record ScanResult(
        string ProjectName,
        string ProjectPath,
        List<FileSnapshot> Files,
        int SkippedDirectories,
        int UnreadableEntries);

    private sealed record SizeGroup(string Name, int Files, long Bytes, string Size);

    private sealed record FileResult(string Path, long Bytes, string Size, string Modified);
}
