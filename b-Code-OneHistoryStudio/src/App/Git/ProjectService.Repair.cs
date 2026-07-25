using System.IO;
using System.Text;

namespace OneHistoryStudio.Git;

/// <summary>
/// ProjectService 的仓库修复切面(PJ-11)。
/// V2.3.3 QC-06 分文件;路径守卫(TryValidateManagedDirectChild / TryValidateWorktreeRoot)
/// 留在主文件,本块只调用,不改其行为——先生成完整计划、任何越界都在零删除状态下拒绝,
/// 该安全次序一行未动。
/// </summary>
public sealed partial class ProjectService
{
    // ---------------------------------------------------------------- 修复(PJ-11,按主项目 Note.txt 方案)

    public async Task<(bool Success, string Message)> RepairAsync(IProgress<string>? progress)
    {
        if (!Directory.Exists(BareRepo))
            return (false, $"裸仓库路径不存在: {BareRepo}");
        if (!TryValidateWorktreeRoot(out var rootError))
            return (false, $"工作树根目录不安全: {rootError}");

        // 先生成并验证完整计划。任何路径越界时必须在零删除状态下拒绝。
        var listResult = await GitRunner.RunAsync(BareRepo, ["worktree", "list", "--porcelain"]);
        if (!listResult.Success)
            return (false, $"获取工作树列表失败:\n{listResult.Output}");

        var branchesResult = await GitRunner.RunAsync(BareRepo,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads/"]);
        if (!branchesResult.Success)
            return (false, $"获取分支列表失败:\n{branchesResult.Output}");

        var paths = listResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line["worktree ".Length..].Trim())
            .Where(path => !PathsEqual(path, BareRepo))
            .ToList();

        var branches = branchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(branch => branch.Trim())
            .Where(branch => branch.Length > 0)
            .ToList();

        var invalidPaths = new List<string>();
        foreach (var path in paths)
        {
            if (!TryValidateManagedDirectChild(path, rejectReparsePoint: true, out var error))
                invalidPaths.Add($"现有 worktree {path}: {error}");
        }

        foreach (var branch in branches)
        {
            var target = Path.Combine(WorktreeRoot, branch);
            if (!TryValidateManagedDirectChild(target, rejectReparsePoint: true, out var error))
                invalidPaths.Add($"分支 {branch} 的重建目标 {target}: {error}");
        }

        if (invalidPaths.Count > 0)
        {
            return (false,
                "修复计划包含越界或高风险路径，已在删除任何目录前拒绝执行:\n  - " +
                string.Join("\n  - ", invalidPaths));
        }

        // 1. 删除已通过边界验证的 worktree 目录(代码数据都在裸仓库,删的只是视图)
        var removed = 0;
        foreach (var path in paths)
        {
            progress?.Report($"删除工作树目录: {path}");
            try
            {
                if (Directory.Exists(path))
                    await Task.Run(() => Directory.Delete(path, recursive: true));
                removed++;
            }
            catch (Exception ex)
            {
                progress?.Report($"   删除失败(跳过): {ex.Message}");
            }
        }

        // 2. 清理 Git 残留 worktree 记录
        progress?.Report("git worktree prune ...");
        var prune = await GitRunner.RunAsync(BareRepo, ["worktree", "prune"]);
        if (!prune.Success)
            return (false, $"worktree prune 失败:\n{prune.Output}");

        // 3. 按已验证的分支清单重建(目录名 = 分支名)
        var rebuilt = 0;
        var failedList = new List<string>();
        for (var i = 0; i < branches.Count; i++)
        {
            var branch = branches[i];
            var targetDir = Path.Combine(WorktreeRoot, branch);
            progress?.Report($"[{i + 1}/{branches.Count}] 重建 worktree: {branch}");
            var add = await GitRunner.RunAsync(BareRepo, ["worktree", "add", targetDir, branch]);
            if (add.Success)
            {
                rebuilt++;
            }
            else
            {
                failedList.Add(branch);
                progress?.Report($"   重建失败: {add.Output}");
            }
        }

        // 4. 全量复测 git status
        var broken = new List<string>();
        var (verifyResult, verifyList) = await ListWorktreesAsync();
        if (verifyResult.Success)
        {
            foreach (var wt in verifyList)
            {
                var status = await GitRunner.RunAsync(wt.WorktreePath, ["status", "--porcelain"]);
                if (!status.Success)
                    broken.Add(wt.BranchName);
            }
        }

        var sb = new StringBuilder();
        sb.Append($"修复完成: 移除 {removed} 个旧目录,重建 {rebuilt}/{branches.Count} 个 worktree");
        if (failedList.Count > 0)
            sb.Append($"\n✗ 重建失败: {string.Join(", ", failedList)}");
        sb.Append(broken.Count == 0
            ? "\n✓ 全量 git status 复测通过"
            : $"\n✗ 复测仍异常: {string.Join(", ", broken)}");

        return (failedList.Count == 0 && broken.Count == 0, sb.ToString());
    }
}