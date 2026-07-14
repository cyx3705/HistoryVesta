using System.Text;
using System.Windows;
using OneHistoryGitTool.Models;
using OneHistoryGitTool.Services;

namespace OneHistoryGitTool;

public partial class MainWindow
{
    private enum WorktreePushResult
    {
        Success,
        Skipped,
        Rejected,
        Failed
    }

    private enum OversizedFileResolveResult
    {
        Proceed,
        Rejected,
        Failed
    }

    private record WorktreePushOutcome(
        WorktreePushResult Result,
        bool HasSizeWarning = false,
        List<LargeFileEntry>? RejectedFiles = null);

    /// <summary>
    /// 将单个工作树推送到本地裸仓库：文件大小检查 → LFS 处理 → git add &amp; commit
    /// </summary>
    private async Task<WorktreePushOutcome> PushWorktreeToLocalBareRepoAsync(
        WorktreeInfo worktree,
        string commitMessage,
        List<(string BranchName, List<LargeFileEntry> Files)>? rejectedDetails = null)
    {
        rejectedDetails ??= new List<(string BranchName, List<LargeFileEntry> Files)>();

        AppendLog($"路径：{worktree.WorktreePath}");

        var (checkStatus, largeFiles) = await Task.Run(() =>
            WorktreeFileScanner.ScanDirectory(worktree.WorktreePath));

        var oversizedFiles = largeFiles
            .Where(f => f.SizeBytes >= WorktreeFileScanner.RejectSizeBytes)
            .ToList();

        if (oversizedFiles.Count > 0)
        {
            if (!await WorktreeLfsHelper.IsGitLfsAvailableAsync(RunGitForLfsAsync, worktree.WorktreePath))
            {
                AppendLog("❌ 未检测到 Git LFS，无法处理 ≥100MB 文件");
                return new WorktreePushOutcome(WorktreePushResult.Failed);
            }

            var (resolveResult, rejectedFiles) = await ResolveOversizedFilesWithLfsAsync(
                worktree, oversizedFiles, rejectedDetails);

            if (resolveResult == OversizedFileResolveResult.Rejected)
                return new WorktreePushOutcome(WorktreePushResult.Rejected, RejectedFiles: rejectedFiles);

            if (resolveResult == OversizedFileResolveResult.Failed)
                return new WorktreePushOutcome(WorktreePushResult.Failed);
        }

        bool hasSizeWarning = checkStatus == FileSizeCheckStatus.Warning;
        if (hasSizeWarning)
        {
            AppendLog($"⚠ 警告：发现 {largeFiles.Count} 个文件 ≥ 50 MB，仍继续推送");
            foreach (var file in largeFiles)
                AppendLog($"   [警告] {file.RelativePath} ({file.FormattedSize})");
        }
        else if (oversizedFiles.Count == 0)
        {
            AppendLog("✓ 文件大小检查通过");
        }

        var addResult = await RunGitCommandAsync(worktree.WorktreePath, "add .");
        if (addResult.ExitCode != 0)
        {
            AppendLog("❌ git add 失败");
            AppendLog(addResult.Output);
            return new WorktreePushOutcome(WorktreePushResult.Failed);
        }

        var commitResult = await RunGitCommandAsync(worktree.WorktreePath, $"commit -m \"{commitMessage}\"");
        if (commitResult.ExitCode != 0)
        {
            if (commitResult.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                || commitResult.Output.Contains("无文件要提交", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("○ 无变更，已跳过");
                return new WorktreePushOutcome(WorktreePushResult.Skipped, HasSizeWarning: hasSizeWarning);
            }

            AppendLog("❌ git commit 失败");
            AppendLog(commitResult.Output);
            return new WorktreePushOutcome(WorktreePushResult.Failed);
        }

        AppendLog("✓ 已成功推送到本地裸仓库");
        if (!string.IsNullOrWhiteSpace(commitResult.Output))
            AppendLog(commitResult.Output);

        return new WorktreePushOutcome(WorktreePushResult.Success, HasSizeWarning: hasSizeWarning);
    }

    private static void ShowRejectedFilesSummary(string branchName, List<LargeFileEntry> rejectedFiles)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"拒绝推送（≥100MB 且未启用 LFS）：");
        summary.AppendLine();
        summary.AppendLine($"【{branchName}】");
        foreach (var file in rejectedFiles)
            summary.AppendLine($"  • {file.RelativePath}  ({file.FormattedSize})");

        MessageBox.Show(summary.ToString(), "文件大小检查摘要", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 检查 ≥100MB 文件是否已使用 LFS；未使用时弹窗询问是否启用 LFS 指针
    /// </summary>
    private async Task<(OversizedFileResolveResult Result, List<LargeFileEntry>? RejectedFiles)> ResolveOversizedFilesWithLfsAsync(
        WorktreeInfo worktree,
        List<LargeFileEntry> oversizedFiles,
        List<(string BranchName, List<LargeFileEntry> Files)> rejectedDetails)
    {
        var lfsManaged = new List<LargeFileEntry>();
        var needsLfs = new List<LargeFileEntry>();

        AppendLog($"发现 {oversizedFiles.Count} 个文件 ≥ 100 MB，正在检查 LFS 状态...");

        foreach (var file in oversizedFiles)
        {
            bool isLfs = await WorktreeLfsHelper.IsFileManagedByLfsAsync(
                RunGitForLfsAsync, worktree.WorktreePath, file.RelativePath);

            if (isLfs)
                lfsManaged.Add(file);
            else
                needsLfs.Add(file);
        }

        if (lfsManaged.Count > 0)
        {
            AppendLog($"✓ {lfsManaged.Count} 个大文件已使用 LFS 指针，放行");
            foreach (var file in lfsManaged)
                AppendLog($"   [LFS] {file.RelativePath} ({file.FormattedSize})");
        }

        if (needsLfs.Count == 0)
            return (OversizedFileResolveResult.Proceed, null);

        var prompt = new StringBuilder();
        prompt.AppendLine($"项目【{worktree.BranchName}】发现 {needsLfs.Count} 个文件超过 100 MB，且未使用 Git LFS 指针：");
        prompt.AppendLine();
        foreach (var file in needsLfs)
            prompt.AppendLine($"  • {file.RelativePath}  ({file.FormattedSize})");
        prompt.AppendLine();
        prompt.AppendLine("是否对这些文件启用 Git LFS 指针后继续推送？");
        prompt.AppendLine("选择「否」将跳过该项目的推送。");

        var choice = MessageBox.Show(
            prompt.ToString(),
            "大文件 Git LFS 确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (choice != MessageBoxResult.Yes)
        {
            rejectedDetails.Add((worktree.BranchName, needsLfs));
            AppendLog($"❌ 用户拒绝启用 LFS，跳过项目 [{worktree.BranchName}]");
            foreach (var file in needsLfs)
                AppendLog($"   [拒绝] {file.RelativePath} ({file.FormattedSize})");
            return (OversizedFileResolveResult.Rejected, needsLfs);
        }

        AppendLog($"正在为项目 [{worktree.BranchName}] 配置 Git LFS...");

        var (success, message) = await WorktreeLfsHelper.SetupLfsForFilesAsync(
            RunGitForLfsAsync,
            worktree.WorktreePath,
            needsLfs.Select(f => f.RelativePath));

        if (!success)
        {
            AppendLog("❌ Git LFS 配置失败");
            AppendLog(message);
            return (OversizedFileResolveResult.Failed, null);
        }

        AppendLog("✓ Git LFS 已配置，以下大文件将使用指针存储");
        foreach (var file in needsLfs)
            AppendLog($"   [LFS 新启用] {file.RelativePath} ({file.FormattedSize})");

        return (OversizedFileResolveResult.Proceed, null);
    }
}