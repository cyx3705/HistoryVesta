using System.IO;
using System.Text;

namespace OneHistoryGitTool.Services;

public record GitCommandResult(int ExitCode, string Output);

public static class WorktreeLfsHelper
{
    private const string LfsPointerVersion = "version https://git-lfs.github.com/spec/v1";

    public static bool IsLfsPointerFile(string absolutePath)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length > 2048)
                return false;

            using var reader = new StreamReader(absolutePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadLine()?.Trim() == LfsPointerVersion;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> IsFileManagedByLfsAsync(
        Func<string, string, Task<GitCommandResult>> runGit,
        string worktreePath,
        string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        string absolutePath = Path.Combine(worktreePath, normalized.Replace('/', Path.DirectorySeparatorChar));

        if (IsLfsPointerFile(absolutePath))
            return true;

        var attrResult = await runGit(worktreePath, $"check-attr filter -- \"{normalized}\"");
        if (attrResult.ExitCode == 0
            && attrResult.Output.Contains("filter: lfs", StringComparison.OrdinalIgnoreCase))
            return true;

        var lfsResult = await runGit(worktreePath, $"lfs ls-files -- \"{normalized}\"");
        if (lfsResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(lfsResult.Output))
            return true;

        return false;
    }

    public static async Task<(bool Success, string Message)> SetupLfsForFilesAsync(
        Func<string, string, Task<GitCommandResult>> runGit,
        string worktreePath,
        IEnumerable<string> relativePaths)
    {
        var installResult = await runGit(worktreePath, "lfs install");
        if (installResult.ExitCode != 0)
            return (false, "git lfs install 失败：\n" + installResult.Output);

        foreach (var relativePath in relativePaths)
        {
            string normalized = relativePath.Replace('\\', '/');
            var trackResult = await runGit(worktreePath, $"lfs track \"{normalized}\"");
            if (trackResult.ExitCode != 0)
                return (false, $"git lfs track 失败（{normalized}）：\n" + trackResult.Output);
        }

        var addAttrResult = await runGit(worktreePath, "add .gitattributes");
        if (addAttrResult.ExitCode != 0)
            return (false, "添加 .gitattributes 失败：\n" + addAttrResult.Output);

        return (true, string.Empty);
    }

    public static async Task<bool> IsGitLfsAvailableAsync(
        Func<string, string, Task<GitCommandResult>> runGit,
        string worktreePath)
    {
        var result = await runGit(worktreePath, "lfs version");
        return result.ExitCode == 0 && result.Output.Contains("git-lfs", StringComparison.OrdinalIgnoreCase);
    }
}