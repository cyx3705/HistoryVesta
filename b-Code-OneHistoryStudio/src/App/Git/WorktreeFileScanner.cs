using System.IO;

namespace OneHistoryStudio.Git;

// 移植自 b-Code-OneHistory-V1(OneHistoryGitTool/Services/WorktreeFileScanner.cs),
// 阈值由调用方传入(V2 中来自 proj.warnmb / proj.rejectmb 配置)。

public enum FileSizeCheckStatus
{
    Ok,
    Warning,
    Rejected,
}

public record LargeFileEntry(string RelativePath, long SizeBytes)
{
    public string FormattedSize => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F2} KB";
        return $"{bytes} B";
    }
}

public static class WorktreeFileScanner
{
    public static (FileSizeCheckStatus Status, List<LargeFileEntry> LargeFiles) ScanDirectory(
        string rootPath, long warnBytes, long rejectBytes)
    {
        var largeFiles = new List<LargeFileEntry>();
        var status = FileSizeCheckStatus.Ok;

        if (!Directory.Exists(rootPath))
            return (FileSizeCheckStatus.Rejected, largeFiles);

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            if (ShouldSkipGitPath(file))
                continue;

            var info = new FileInfo(file);
            if (info.Length >= rejectBytes)
            {
                status = FileSizeCheckStatus.Rejected;
                largeFiles.Add(new LargeFileEntry(GetRelativePath(rootPath, file), info.Length));
            }
            else if (info.Length >= warnBytes)
            {
                if (status != FileSizeCheckStatus.Rejected)
                    status = FileSizeCheckStatus.Warning;
                largeFiles.Add(new LargeFileEntry(GetRelativePath(rootPath, file), info.Length));
            }
        }

        return (status, largeFiles.OrderByDescending(f => f.SizeBytes).ToList());
    }

    private static bool ShouldSkipGitPath(string filePath)
    {
        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetRelativePath(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        return relative.Replace('\\', '/');
    }
}
