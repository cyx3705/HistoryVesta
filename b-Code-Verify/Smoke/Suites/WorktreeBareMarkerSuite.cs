using System.IO;
using HistoryJanus.Git;

namespace HistoryJanus.Smoke.Suites;

/// <summary>
/// 裸标记覆盖：裸仓开 <c>extensions.worktreeConfig</c> 时，工作树必须自带
/// <c>config.worktree</c> 的 <c>bare = false</c>，否则 git 把它当裸仓，
/// <c>status</c> 与 <c>ls-files --others</c> 一律报 "must be run in a work tree"。
/// </summary>
internal static class WorktreeBareMarkerSuite
{
    public static async Task RunAsync(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "HistoryJanus.BareMarker", Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "Fixture.git");
        var worktree = Path.Combine(root, "sample-branch");
        Directory.CreateDirectory(root);

        try
        {
            // 复刻 HistoryVesta 的配置组合：裸仓 + 每工作树配置。
            SmokeKit.Ensure(await GitRunner.RunAsync(root, ["init", "--bare", bare]), "init bare");
            SmokeKit.Ensure(await GitRunner.RunAsync(bare, ["config", "extensions.worktreeConfig", "true"]), "enable worktreeConfig");

            var seed = Path.Combine(root, "seed");
            Directory.CreateDirectory(seed);
            SmokeKit.Ensure(await GitRunner.RunAsync(root, ["clone", bare, seed]), "clone seed");
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
            SmokeKit.Ensure(await GitRunner.RunAsync(seed, ["add", "--", "README.md"]), "add seed");
            SmokeKit.Ensure(await GitRunner.RunAsync(seed, ["commit", "-m", "seed"]), "commit seed");
            SmokeKit.Ensure(await GitRunner.RunAsync(seed, ["push", "origin", "HEAD:refs/heads/sample-branch"]), "push seed");

            SmokeKit.Ensure(
                await GitRunner.RunAsync(bare, ["worktree", "add", worktree, "sample-branch"]),
                "worktree add");

            // 复刻故障：删掉 git 可能已写好的覆盖，工作树立刻被当成裸仓。
            var marker = Path.Combine(bare, "worktrees", "sample-branch", "config.worktree");
            if (File.Exists(marker))
                File.Delete(marker);

            var brokenStatus = await GitRunner.RunAsync(worktree, ["status", "--porcelain"]);
            if (brokenStatus.Success)
                throw new InvalidOperationException("缺少裸标记覆盖时 git status 竟然成功，夹具没有复现故障");
            if (!brokenStatus.Output.Contains("must be run in a work tree", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"复现的失败不是预期原因: {brokenStatus.Output}");

            // 修复：Ensure 应写入覆盖并让 git 重新认得工作树。
            var first = WorktreeBareMarker.Ensure(worktree);
            if (!first.Success || !first.Written)
                throw new InvalidOperationException($"Ensure 未写入覆盖: {first.Message}");
            if (!File.Exists(marker))
                throw new InvalidOperationException($"覆盖文件未落盘: {marker}");

            SmokeKit.Ensure(await GitRunner.RunAsync(worktree, ["status", "--porcelain"]), "status after repair");
            SmokeKit.Ensure(
                await GitRunner.RunAsync(worktree, ["ls-files", "--others", "--exclude-standard"]),
                "ls-files --others after repair");

            // 幂等：已正确时不得重复写盘。
            var second = WorktreeBareMarker.Ensure(worktree);
            if (!second.Success || second.Written)
                throw new InvalidOperationException("Ensure 不幂等：覆盖已存在时仍然写盘");

            // 不是工作树时必须明确失败，而不是静默返回成功。
            var notAWorktree = WorktreeBareMarker.Ensure(seed);
            if (notAWorktree.Success)
                throw new InvalidOperationException("对独立仓库应报告无需覆盖，而不是返回成功");
        }
        finally
        {
            SmokeKit.DeleteTree(root);
        }
    }
}
