using System.IO;
using System.Text;
using AppShell.Core.Storage;

namespace OneHistoryStudio.Git;

public sealed record WorktreeInfo(string BranchName, string WorktreePath, string LastCommitTime = "");

/// <summary>某项目根下以 z/Z 开头的一级子文件夹(V2.0.1 Meta 文件)。</summary>
public sealed record MetaFolderInfo(
    string ProjectName,
    string MetaName,
    string FullPath,
    string LastWriteTime = "");

public enum CommitOutcome
{
    Success,
    Skipped,
    Rejected,
    Failed,
}

public sealed record CommitReport(
    CommitOutcome Outcome,
    string Message,
    bool HasSizeWarning = false,
    List<LargeFileEntry>? RejectedFiles = null,
    string BeforeSha = "",
    string AfterSha = "",
    IReadOnlyList<SubmoduleOperationEntry>? Submodules = null,
    bool PartialCompletion = false,
    RepositoryTarget Target = RepositoryTarget.Parent,
    bool ParentExecuted = false,
    bool ParentPointerPending = false);

/// <summary>
/// proj.* 指令域的业务实现:OneHistory 项目库(裸仓库 + worktree)管理。
/// 流程移植自 b-Code-OneHistory-V1;确认交互统一走总线确认通道(N-04),
/// 运行参数走配置服务现读现生效(PJ-12)。
/// </summary>
public sealed class ProjectService
{
    // 设置键(PJ-12):首启写入默认值,此后 app.set / app.get 可读写
    public const string KeyBareRepo = "proj.barerepo";
    public const string KeyWorktreeRoot = "proj.worktreeroot";
    public const string KeyBaseBranch = "proj.basebranch";
    public const string KeyWarnMb = "proj.warnmb";
    public const string KeyRejectMb = "proj.rejectmb";
    public const string KeyProtected = "proj.protected";

    private readonly ISettingsService _settings;
    private readonly string _dataDir;
    private readonly GitlinkService _gitlinks = new();

    /// <summary>执行中途的二次确认通道(LFS 启用询问等);由装配点接到总线 Confirmation。</summary>
    private readonly Func<string, bool> _confirm;

    /// <summary>
    /// 分支描述提供者(DT-02,装配点接 HistoryRecorder.AllNotes):
    /// 继承树在展示前用它覆盖节点描述——缓存里存的是构建时的提交信息,
    /// 覆盖在读取时进行,proj.note 后无需重扫即可见。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? NotesProvider { get; set; }

    public ProjectService(ISettingsService settings, Func<string, bool> confirm, string dataDir)
    {
        _settings = settings;
        _confirm = confirm;
        _dataDir = dataDir;
    }

    // ---------------------------------------------------------------- 配置(现读现生效)

    public string BareRepo =>
        _settings.Get(KeyBareRepo) ?? @"C:\OneHistory\OneHistory-Projects\OneHistory-Projects.git";

    public string WorktreeRoot =>
        _settings.Get(KeyWorktreeRoot) ?? @"C:\OneHistory\OneHistory-Projects";

    public string BaseBranch => _settings.Get(KeyBaseBranch) ?? "0000-000-Template";

    public long WarnBytes => _settings.GetInt(KeyWarnMb, 50) * 1024L * 1024;

    public long RejectBytes => _settings.GetInt(KeyRejectMb, 100) * 1024L * 1024;

    public IReadOnlySet<string> ProtectedBranches
    {
        get
        {
            var raw = _settings.Get(KeyProtected) ?? "0000-000-Template,main,master";
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(name);
            set.Add(BaseBranch); // 基础分支永远受保护
            return set;
        }
    }

    /// <summary>首启把默认配置写入 settings.json,使 app.get 可见、app.set 可改。</summary>
    public void EnsureDefaultSettings()
    {
        SetIfMissing(KeyBareRepo, @"C:\OneHistory\OneHistory-Projects\OneHistory-Projects.git");
        SetIfMissing(KeyWorktreeRoot, @"C:\OneHistory\OneHistory-Projects");
        SetIfMissing(KeyBaseBranch, "0000-000-Template");
        SetIfMissing(KeyWarnMb, "50");
        SetIfMissing(KeyRejectMb, "100");
        SetIfMissing(KeyProtected, "0000-000-Template,main,master");
    }

    private void SetIfMissing(string key, string value)
    {
        if (_settings.Get(key) == null)
            _settings.Set(key, value);
    }

    public string DescribeConfig()
    {
        var sb = new StringBuilder();
        sb.AppendLine("proj.* 当前配置(app.set 键=值 可修改,即时生效):");
        sb.AppendLine($"  {KeyBareRepo}     = {BareRepo}");
        sb.AppendLine($"  {KeyWorktreeRoot} = {WorktreeRoot}");
        sb.AppendLine($"  {KeyBaseBranch}   = {BaseBranch}");
        sb.AppendLine($"  {KeyWarnMb}       = {WarnBytes / 1024 / 1024} MB(警告阈值)");
        sb.AppendLine($"  {KeyRejectMb}     = {RejectBytes / 1024 / 1024} MB(LFS 阈值)");
        sb.Append($"  {KeyProtected}    = {string.Join(", ", ProtectedBranches)}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 列表(PJ-01)

    public async Task<(GitResult Git, List<WorktreeInfo> Worktrees)> ListWorktreesAsync()
    {
        var result = await GitRunner.RunAsync(BareRepo, ["worktree", "list", "--porcelain"]);
        if (!result.Success)
            return (result, new List<WorktreeInfo>());

        var bare = BareRepo;
        var list = ParseWorktreeList(result.Output)
            .Where(w => !w.WorktreePath.Equals(bare, StringComparison.OrdinalIgnoreCase)
                        && !w.WorktreePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 最后提交时间:单次 for-each-ref 补全(UI-01 列表列),失败不影响主流程
        var times = await GitRunner.RunAsync(BareRepo,
            ["for-each-ref", "--format=%(refname:short)|%(committerdate:iso-local)", "refs/heads/"]);
        if (times.Success)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in times.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 2);
                if (parts.Length == 2)
                    map[parts[0].Trim()] = parts[1].Trim();
            }

            list = list.Select(w => w with { LastCommitTime = map.GetValueOrDefault(w.BranchName, "") }).ToList();
        }

        return (result, list);
    }

    /// <summary>按登记分支解析工作树，并强制其仍是受管根下的直接、非重解析点子目录。</summary>
    public async Task<(bool Success, string Message, WorktreeInfo? Worktree)> ResolveWorktreeAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return (false, "项目名称不能为空", null);

        var (git, worktrees) = await ListWorktreesAsync();
        if (!git.Success)
            return (false, $"读取登记工作树失败:\n{git.Output}", null);

        var worktree = worktrees.FirstOrDefault(item =>
            item.BranchName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(item.WorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals(name, StringComparison.OrdinalIgnoreCase));
        if (worktree == null)
            return (false, $"未找到已登记项目: {name}", null);
        if (!Directory.Exists(worktree.WorktreePath))
            return (false, $"项目工作树不存在: {worktree.WorktreePath}", null);
        if (!TryValidateManagedDirectChild(worktree.WorktreePath, rejectReparsePoint: true, out var error))
            return (false, $"项目工作树越出受管边界: {error}", null);

        return (true, worktree.WorktreePath, worktree);
    }

    /// <summary>解析 git worktree list --porcelain 输出(移植自 V1)。</summary>
    public static List<WorktreeInfo> ParseWorktreeList(string output)
    {
        var result = new List<WorktreeInfo>();
        string? currentPath = null;
        string? currentBranch = null;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(currentPath) && !string.IsNullOrEmpty(currentBranch))
                    result.Add(new WorktreeInfo(currentBranch, currentPath));
                currentPath = line["worktree ".Length..].Trim();
                currentBranch = null;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var refName = line["branch ".Length..].Trim();
                currentBranch = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? refName["refs/heads/".Length..]
                    : refName;
            }
        }

        if (!string.IsNullOrEmpty(currentPath) && !string.IsNullOrEmpty(currentBranch))
            result.Add(new WorktreeInfo(currentBranch, currentPath));

        return result;
    }

    // ---------------------------------------------------------------- 创建(PJ-02)

    public async Task<(bool Success, string Message)> CreateAsync(
        string name, string? baseBranch, IProgress<string>? progress)
    {
        baseBranch = string.IsNullOrWhiteSpace(baseBranch) ? BaseBranch : baseBranch.Trim();
        name = name.Trim();

        if (name.Length == 0)
            return (false, "项目名称不能为空");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return (false, "项目名称包含非法字符(\\ / : * ? \" < > | 等)");
        if (!Directory.Exists(BareRepo))
            return (false, $"裸仓库路径不存在: {BareRepo}(检查 {KeyBareRepo} 配置)");

        var targetPath = Path.Combine(WorktreeRoot, name);
        if (!TryValidateManagedDirectChild(targetPath, rejectReparsePoint: true, out var pathError))
            return (false, $"目标工作树路径不安全: {pathError}");
        if (Directory.Exists(targetPath))
            return (false, $"目标工作树目录已存在: {targetPath}\n请更换项目名称或手动清理该目录");

        var branchCheck = await GitRunner.RunAsync(BareRepo, ["branch", "--list", name]);
        if (branchCheck.Success && branchCheck.Output.Contains(name, StringComparison.Ordinal))
            return (false, $"分支 \"{name}\" 已存在于裸仓库中");

        progress?.Report($"创建分支 {name}(基于 {baseBranch})...");
        var createBranch = await GitRunner.RunAsync(BareRepo, ["branch", name, baseBranch]);
        if (!createBranch.Success)
            return (false, $"创建分支失败:\n{createBranch.Output}");

        progress?.Report($"创建工作树 {targetPath} ...");
        var addWorktree = await GitRunner.RunAsync(BareRepo, ["worktree", "add", targetPath, name]);
        if (!addWorktree.Success)
        {
            return (false,
                $"工作树创建失败(分支 {name} 已创建,可能残留,请手动清理):\n{addWorktree.Output}");
        }

        return (true, $"项目已就绪: {targetPath}(分支 {name},基于 {baseBranch})");
    }

    // ---------------------------------------------------------------- 删除(PJ-03)

    public bool IsProtected(string branchName) => ProtectedBranches.Contains(branchName.Trim());

    public async Task<(bool Success, string Message)> DeleteAsync(string name, IProgress<string>? progress)
    {
        name = name.Trim();
        if (IsProtected(name))
            return (false, $"\"{name}\" 是受保护分支({string.Join("/", ProtectedBranches)}),拒绝删除");

        var (listResult, worktrees) = await ListWorktreesAsync();
        if (!listResult.Success)
            return (false, $"获取工作树列表失败:\n{listResult.Output}");

        var target = worktrees.FirstOrDefault(
            w => w.BranchName.Equals(name, StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        if (target != null)
        {
            if (!TryValidateManagedDirectChild(
                    target.WorktreePath,
                    rejectReparsePoint: true,
                    out var pathError))
            {
                return (false, $"工作树路径不在受管边界内，拒绝删除: {pathError}\n{target.WorktreePath}");
            }

            progress?.Report($"移除工作树 {target.WorktreePath} ...");
            var removeWt = await GitRunner.RunAsync(BareRepo,
                ["worktree", "remove", target.WorktreePath, "--force"]);
            if (removeWt.Success)
            {
                sb.AppendLine($"工作树已移除: {target.WorktreePath}");
            }
            else
            {
                // 与 V1 一致:worktree 可能已损坏,移除失败仍继续删分支
                sb.AppendLine($"移除工作树失败(继续尝试删除分支): {removeWt.Output}");
            }
        }
        else
        {
            sb.AppendLine($"分支 {name} 没有对应工作树(或已断链),直接删除分支");
        }

        progress?.Report($"强制删除分支 {name} ...");
        var deleteBranch = await GitRunner.RunAsync(BareRepo, ["branch", "-D", name]);
        if (!deleteBranch.Success)
            return (false, sb + $"删除分支失败:\n{deleteBranch.Output}");

        sb.Append($"分支 {name} 已删除");
        return (true, sb.ToString());
    }

    // ---------------------------------------------------------------- 提交(PJ-05)

    public async Task<CommitReport> CommitAsync(
        string name,
        string message,
        IProgress<string>? progress,
        bool includeSubmodules = false,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
        => await CommitAsync(name, message, progress,
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent,
            submoduleMessage, cancellation);

    public async Task<CommitReport> CommitAsync(
        string name,
        string message,
        IProgress<string>? progress,
        RepositoryTarget target,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
    {
        name = name.Trim();
        var worktreePath = Path.Combine(WorktreeRoot, name);
        if (!Directory.Exists(worktreePath))
            return new CommitReport(CommitOutcome.Failed, $"工作树目录不存在: {worktreePath}",
                Target: target);
        return await CommitWorktreeAsync(new WorktreeInfo(name, worktreePath), message, progress,
            target, submoduleMessage, cancellation);
    }

    private sealed record RepositoryCommitPlan(
        WorktreeInfo Worktree,
        string Message,
        FileSizeCheckStatus CheckStatus,
        List<LargeFileEntry> LargeFiles,
        List<LargeFileEntry> NeedsLfs,
        string BeforeSha,
        bool AddAll);

    private async Task<CommitReport> CommitWorktreeAsync(
        WorktreeInfo worktree,
        string commitMessage,
        IProgress<string>? progress,
        RepositoryTarget target,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(commitMessage))
            return new CommitReport(CommitOutcome.Failed, "提交描述不能为空", Target: target);

        List<GitlinkDescriptor> links = [];
        if (target != RepositoryTarget.Parent)
        {
            var discovered = await _gitlinks.DiscoverAsync(worktree.WorktreePath, cancellation);
            if (!discovered.Success)
                return new CommitReport(CommitOutcome.Failed, discovered.Message, Target: target);
            links = discovered.Items;
            var invalid = links.FirstOrDefault(link => string.IsNullOrWhiteSpace(link.Branch)
                                                      || !link.HasIdentity || link.NestedGitlinkDirty);
            if (invalid != null)
            {
                var reason = string.IsNullOrWhiteSpace(invalid.Branch)
                    ? "处于 detached HEAD"
                    : !invalid.HasIdentity
                        ? "缺少 user.name 或 user.email"
                        : "存在发生变化的第二层 gitlink";
                return new CommitReport(CommitOutcome.Failed,
                    $"子模块预检失败 [{invalid.RelativePath}]: {reason}", Target: target);
            }
        }

        var childMessage = string.IsNullOrWhiteSpace(submoduleMessage)
            ? commitMessage.Trim()
            : submoduleMessage.Trim();
        var childPlans = new List<(GitlinkDescriptor Link, RepositoryCommitPlan Plan)>();
        foreach (var link in links.Where(link => link.IsDirty))
        {
            var prepared = await PrepareCommitPlanAsync(
                new WorktreeInfo(link.RelativePath, link.FullPath), childMessage, progress,
                excludedDirectories: [], addAll: true, cancellation);
            if (prepared.Error != null)
                return prepared.Error with { Submodules = BuildSkippedEntries(links), Target = target };
            childPlans.Add((link, prepared.Plan!));
        }

        RepositoryCommitPlan? parentPlan = null;
        if (target != RepositoryTarget.Submodules)
        {
            var parentPrepared = await PrepareCommitPlanAsync(worktree, commitMessage.Trim(), progress,
                links.Select(link => link.FullPath), addAll: false, cancellation);
            if (parentPrepared.Error != null)
                return parentPrepared.Error with
                {
                    Submodules = BuildSkippedEntries(links),
                    Target = target,
                    ParentExecuted = true,
                };
            parentPlan = parentPrepared.Plan!;
        }

        var parentBefore = parentPlan?.BeforeSha
                           ?? await ReadHeadShaAsync(worktree.WorktreePath, cancellation);

        if (childPlans.Count > 0
            && !_confirm(BuildSubmoduleCommitPrompt(worktree.BranchName, childPlans, target)))
        {
            return new CommitReport(CommitOutcome.Rejected, "用户取消子模块联动提交",
                BeforeSha: parentBefore, AfterSha: parentBefore,
                Submodules: BuildSkippedEntries(links), Target: target);
        }

        var entries = BuildSkippedEntries(links).ToList();
        foreach (var (link, plan) in childPlans)
        {
            progress?.Report($"[{worktree.BranchName}/{link.RelativePath}] 提交子模块...");
            var child = await ExecuteCommitPlanAsync(plan, progress, cancellation);
            var entry = new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                child.BeforeSha, child.AfterSha, ToSubmoduleOutcome(child.Outcome), child.Message, link.Kind);
            ReplaceEntry(entries, entry);
            if (child.Outcome is not CommitOutcome.Success and not CommitOutcome.Skipped)
            {
                var pending = await HasPendingParentPointerAsync(
                    worktree.WorktreePath, links, cancellation);
                return new CommitReport(child.Outcome,
                    $"子模块提交失败 [{link.RelativePath}]: {child.Message}",
                    child.HasSizeWarning, child.RejectedFiles, parentBefore,
                    parentBefore, entries,
                    entries.Any(item => item.Outcome == SubmoduleOperationOutcome.Success),
                    target, ParentExecuted: false, ParentPointerPending: pending);
            }
        }

        if (target == RepositoryTarget.Submodules)
        {
            var anyCommitted = entries.Any(item => item.Outcome == SubmoduleOperationOutcome.Success);
            var pending = await HasPendingParentPointerAsync(
                worktree.WorktreePath, links, cancellation);
            var message = anyCommitted
                ? $"已提交 {entries.Count(item => item.Outcome == SubmoduleOperationOutcome.Success)} 个子模块；" +
                  "父分支尚未记录新 gitlink，请执行“分支及子模块”完成收口"
                : links.Count == 0
                    ? "未发现直属子模块，已跳过"
                    : pending
                        ? "子模块没有新变更；父分支仍有待收口 gitlink"
                        : "子模块均无变更，已跳过";
            return new CommitReport(
                anyCommitted ? CommitOutcome.Success : CommitOutcome.Skipped,
                message,
                BeforeSha: parentBefore,
                AfterSha: parentBefore,
                Submodules: entries,
                Target: target,
                ParentExecuted: false,
                ParentPointerPending: pending);
        }

        var parent = await ExecuteCommitPlanAsync(parentPlan!, progress, cancellation);
        var parentPending = parent.Outcome is not CommitOutcome.Success
                            && await HasPendingParentPointerAsync(
                                worktree.WorktreePath, links, cancellation);
        return parent with
        {
            Submodules = entries,
            PartialCompletion = parent.Outcome is not CommitOutcome.Success and not CommitOutcome.Skipped
                                && entries.Any(item => item.Outcome == SubmoduleOperationOutcome.Success),
            Target = target,
            ParentExecuted = true,
            ParentPointerPending = parentPending,
        };
    }

    private async Task<bool> HasPendingParentPointerAsync(
        string parentPath,
        IEnumerable<GitlinkDescriptor> links,
        CancellationToken cancellation)
    {
        foreach (var link in links)
        {
            var childHead = await GitRunner.RunAsync(link.FullPath, ["rev-parse", "HEAD"],
                cancellation: cancellation);
            if (!childHead.Success)
                return true;
            var recorded = await _gitlinks.GetHeadGitlinkShaAsync(
                parentPath, link.RelativePath, cancellation);
            if (!recorded.Success || !recorded.Sha.Equals(
                    FirstLine(childHead.Output), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<string> ReadHeadShaAsync(
        string repository, CancellationToken cancellation)
    {
        var result = await GitRunner.RunAsync(repository, ["rev-parse", "HEAD"],
            cancellation: cancellation);
        return result.Success ? FirstLine(result.Output) : string.Empty;
    }

    private async Task<(RepositoryCommitPlan? Plan, CommitReport? Error)> PrepareCommitPlanAsync(
        WorktreeInfo worktree,
        string message,
        IProgress<string>? progress,
        IEnumerable<string> excludedDirectories,
        bool addAll,
        CancellationToken cancellation)
    {
        var name = await GitRunner.RunAsync(worktree.WorktreePath, ["config", "--get", "user.name"],
            cancellation: cancellation);
        var email = await GitRunner.RunAsync(worktree.WorktreePath, ["config", "--get", "user.email"],
            cancellation: cancellation);
        if (!name.Success || string.IsNullOrWhiteSpace(FirstLine(name.Output))
                          || !email.Success || string.IsNullOrWhiteSpace(FirstLine(email.Output)))
            return (null, new CommitReport(CommitOutcome.Failed,
                $"仓库缺少提交身份 user.name/user.email: {worktree.BranchName}"));

        var head = await GitRunner.RunAsync(worktree.WorktreePath, ["rev-parse", "HEAD"],
            cancellation: cancellation);
        if (!head.Success)
            return (null, new CommitReport(CommitOutcome.Failed,
                $"无法读取仓库 HEAD [{worktree.BranchName}]:\n{head.Output}"));

        progress?.Report($"[{worktree.BranchName}] 扫描文件大小...");
        var warnBytes = WarnBytes;
        var rejectBytes = RejectBytes;
        var (checkStatus, largeFiles) = await Task.Run(() =>
            WorktreeFileScanner.ScanDirectory(worktree.WorktreePath, warnBytes, rejectBytes,
                excludedDirectories), cancellation);
        var oversized = largeFiles.Where(file => file.SizeBytes >= rejectBytes).ToList();
        var needsLfs = new List<LargeFileEntry>();
        if (oversized.Count > 0)
        {
            if (!await WorktreeLfsHelper.IsGitLfsAvailableAsync(worktree.WorktreePath))
                return (null, new CommitReport(CommitOutcome.Failed,
                    $"发现 {oversized.Count} 个 ≥{rejectBytes / 1024 / 1024}MB 文件,但未检测到 Git LFS,无法处理"));
            foreach (var file in oversized)
            {
                if (!await WorktreeLfsHelper.IsFileManagedByLfsAsync(worktree.WorktreePath, file.RelativePath))
                    needsLfs.Add(file);
                else
                    progress?.Report($"   [LFS] {file.RelativePath}({file.FormattedSize})已用指针,放行");
            }
        }

        if (needsLfs.Count > 0 && !_confirm(BuildLfsPrompt(worktree.BranchName, needsLfs)))
        {
            return (null, new CommitReport(CommitOutcome.Rejected,
                $"用户拒绝启用 LFS,已跳过项目 [{worktree.BranchName}]", RejectedFiles: needsLfs));
        }

        if (checkStatus == FileSizeCheckStatus.Warning)
        {
            progress?.Report($"[{worktree.BranchName}] ⚠ {largeFiles.Count} 个文件 ≥ {warnBytes / 1024 / 1024}MB,仍继续提交:");
            foreach (var file in largeFiles)
                progress?.Report($"   [警告] {file.RelativePath}({file.FormattedSize})");
        }

        return (new RepositoryCommitPlan(worktree, message, checkStatus, largeFiles, needsLfs,
            FirstLine(head.Output), addAll), null);
    }

    private async Task<CommitReport> ExecuteCommitPlanAsync(
        RepositoryCommitPlan plan, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (plan.NeedsLfs.Count > 0)
        {
            progress?.Report($"[{plan.Worktree.BranchName}] 配置 Git LFS...");
            var setup = await WorktreeLfsHelper.SetupLfsForFilesAsync(plan.Worktree.WorktreePath,
                plan.NeedsLfs.Select(file => file.RelativePath));
            if (!setup.Success)
                return new CommitReport(CommitOutcome.Failed, $"Git LFS 配置失败:\n{setup.Message}",
                    BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
        }

        var addResult = await GitRunner.RunAsync(plan.Worktree.WorktreePath,
            plan.AddAll ? ["add", "-A"] : ["add", "."], cancellation: cancellation);
        if (!addResult.Success)
            return new CommitReport(CommitOutcome.Failed, $"git add 失败:\n{addResult.Output}",
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);

        var staged = await GitRunner.RunAsync(plan.Worktree.WorktreePath,
            ["diff", "--cached", "--quiet"], cancellation: cancellation);
        if (staged.ExitCode == 0)
            return new CommitReport(CommitOutcome.Skipped, "无变更,已跳过",
                plan.CheckStatus == FileSizeCheckStatus.Warning,
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
        if (staged.ExitCode != 1)
            return new CommitReport(CommitOutcome.Failed, $"检查暂存区失败:\n{staged.Output}",
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);

        var commitResult = await GitRunner.RunAsync(plan.Worktree.WorktreePath,
            ["commit", "-m", plan.Message], cancellation: cancellation);
        if (!commitResult.Success)
        {
            if (commitResult.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                || commitResult.Output.Contains("无文件要提交", StringComparison.OrdinalIgnoreCase))
                return new CommitReport(CommitOutcome.Skipped, "无变更,已跳过",
                    plan.CheckStatus == FileSizeCheckStatus.Warning,
                    BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
            return new CommitReport(CommitOutcome.Failed, $"git commit 失败:\n{commitResult.Output}",
                BeforeSha: plan.BeforeSha, AfterSha: plan.BeforeSha);
        }

        var after = await GitRunner.RunAsync(plan.Worktree.WorktreePath, ["rev-parse", "HEAD"],
            cancellation: cancellation);
        var afterSha = after.Success ? FirstLine(after.Output) : string.Empty;
        var summary = FirstLine(commitResult.Output);
        return new CommitReport(CommitOutcome.Success, $"已提交到本地仓库 {summary}".Trim(),
            plan.CheckStatus == FileSizeCheckStatus.Warning,
            BeforeSha: plan.BeforeSha, AfterSha: afterSha);
    }

    // ---------------------------------------------------------------- 推送(PJ-06 / PJ-08)

    public async Task<PushReport> PushAsync(
        string name, bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAsync(name,
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent, cancellation);

    public async Task<PushReport> PushAsync(
        string name, RepositoryTarget target, CancellationToken cancellation = default)
    {
        name = name.Trim();
        var worktreePath = Path.Combine(WorktreeRoot, name);
        if (!Directory.Exists(worktreePath))
            return new PushReport(false, $"工作树目录不存在: {worktreePath}", Target: target);

        var entries = new List<SubmoduleOperationEntry>();
        var pendingParentCount = 0;
        if (target != RepositoryTarget.Parent)
        {
            var prepared = await PrepareSubmodulePushesAsync(
                [(worktreePath, worktreePath)], target == RepositoryTarget.Both, cancellation);
            pendingParentCount = prepared.ParentPointerPendingCount;
            if (!prepared.Success)
                return new PushReport(false, prepared.Message, Submodules: prepared.Entries,
                    Target: target, ParentPointerPending: pendingParentCount > 0);
            var pushed = await PushSubmodulesAsync(prepared.Items, cancellation);
            entries = pushed.Entries;
            if (!pushed.Success)
                return new PushReport(false, pushed.Message, Submodules: entries,
                    PartialCompletion: entries.Any(item => item.Pushed), Target: target,
                    ParentPointerPending: pendingParentCount > 0);

            if (target == RepositoryTarget.Submodules)
            {
                var message = entries.Count == 0
                    ? "未发现直属子模块，已跳过"
                    : $"已推送 {entries.Count} 个子模块，父分支未推送" +
                      (pendingParentCount > 0 ? "；父 gitlink 尚待收口" : string.Empty);
                return new PushReport(true, message, ParentPushed: false, Submodules: entries,
                    Target: target, ParentPointerPending: pendingParentCount > 0);
            }
        }

        var result = await GitRunner.RunAsync(worktreePath, ["push", "origin", name],
            timeoutSeconds: 600, cancellation: cancellation);
        return result.Success
            ? new PushReport(true, $"已推送到 GitHub(origin/{name})\n{result.Output}".Trim(),
                true, entries, Target: target)
            : new PushReport(false, $"父仓库推送失败(退出码 {result.ExitCode}):\n{result.Output}",
                Submodules: entries, PartialCompletion: entries.Any(item => item.Pushed),
                Target: target, ParentPointerPending: pendingParentCount > 0);
    }

    public async Task<BatchPushReport> PushAllAsync(
        bool includeSubmodules = false, CancellationToken cancellation = default)
        => await PushAllAsync(
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent, cancellation);

    public async Task<BatchPushReport> PushAllAsync(
        RepositoryTarget target, CancellationToken cancellation = default)
    {
        var entries = new List<SubmoduleOperationEntry>();
        var pendingParentCount = 0;
        if (target != RepositoryTarget.Parent)
        {
            var (listResult, worktrees) = await ListWorktreesAsync();
            if (!listResult.Success)
                return new BatchPushReport(false, $"获取工作树列表失败:\n{listResult.Output}",
                    false, [], Target: target);
            var parents = worktrees.Where(item => Directory.Exists(item.WorktreePath))
                .Select(item => (item.WorktreePath, item.WorktreePath));
            var prepared = await PrepareSubmodulePushesAsync(
                parents, target == RepositoryTarget.Both, cancellation);
            pendingParentCount = prepared.ParentPointerPendingCount;
            if (!prepared.Success)
                return new BatchPushReport(false, prepared.Message, false, prepared.Entries,
                    Target: target, ParentPointerPendingCount: pendingParentCount);
            var pushed = await PushSubmodulesAsync(prepared.Items, cancellation);
            entries = pushed.Entries;
            if (!pushed.Success)
                return new BatchPushReport(false, pushed.Message, false, entries,
                    entries.Any(item => item.Pushed), target, pendingParentCount);

            if (target == RepositoryTarget.Submodules)
            {
                var message = entries.Count == 0
                    ? "全部工作树均未发现直属子模块，已跳过"
                    : $"已推送 {entries.Count} 个子模块，全部父分支未推送" +
                      (pendingParentCount > 0 ? $"；{pendingParentCount} 个父项目 gitlink 尚待收口" : string.Empty);
                return new BatchPushReport(true, message, false, entries,
                    Target: target, ParentPointerPendingCount: pendingParentCount);
            }
        }

        var result = await GitRunner.RunAsync(BareRepo, ["push", "--all", "origin"],
            timeoutSeconds: 1800, cancellation: cancellation);
        return result.Success
            ? new BatchPushReport(true, $"已推送全部分支到 GitHub\n{result.Output}".Trim(),
                true, entries, Target: target)
            : new BatchPushReport(false, $"一键全推失败(退出码 {result.ExitCode}):\n{result.Output}",
                false, entries, entries.Any(item => item.Pushed), target, pendingParentCount);
    }

    private async Task<(bool Success, string Message,
        List<(string ParentPath, GitlinkDescriptor Link)> Items,
        List<SubmoduleOperationEntry> Entries,
        int ParentPointerPendingCount)> PrepareSubmodulePushesAsync(
        IEnumerable<(string ParentPath, string Identity)> parents,
        bool requireParentPointerMatch,
        CancellationToken cancellation)
    {
        var items = new List<(string ParentPath, GitlinkDescriptor Link)>();
        var entries = new List<SubmoduleOperationEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parentPath, _) in parents)
        {
            var discovered = await _gitlinks.DiscoverAsync(parentPath, cancellation);
            if (!discovered.Success)
                return (false, discovered.Message, [], entries, pendingParents.Count);
            foreach (var link in discovered.Items)
            {
                if (!seen.Add(Path.GetFullPath(link.FullPath)))
                    continue;
                var failure = string.IsNullOrWhiteSpace(link.Branch)
                    ? "处于 detached HEAD"
                    : link.IsDirty
                        ? "工作树不干净，请先提交"
                        : !link.HasOrigin
                            ? "不存在 origin"
                            : null;
                if (failure != null)
                {
                    entries.Add(new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                        link.HeadSha, link.HeadSha, SubmoduleOperationOutcome.Rejected, failure, link.Kind));
                    return (false, $"子模块推送预检失败 [{link.RelativePath}]: {failure}",
                        [], entries, pendingParents.Count);
                }

                var recorded = await _gitlinks.GetHeadGitlinkShaAsync(parentPath, link.RelativePath,
                    cancellation);
                var pointerMatches = recorded.Success
                                     && recorded.Sha.Equals(link.HeadSha, StringComparison.OrdinalIgnoreCase);
                if (!pointerMatches)
                {
                    pendingParents.Add(parentPath);
                    var message = recorded.Success
                        ? $"父 HEAD 记录 {recorded.Sha[..Math.Min(12, recorded.Sha.Length)]}，子 HEAD 为 {link.HeadSha[..Math.Min(12, link.HeadSha.Length)]}"
                        : recorded.Message;
                    if (requireParentPointerMatch)
                    {
                        entries.Add(new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                            link.HeadSha, link.HeadSha, SubmoduleOperationOutcome.Rejected, message, link.Kind));
                        return (false, $"子模块指针尚未由父项目提交 [{link.RelativePath}]: {message}",
                            [], entries, pendingParents.Count);
                    }
                }
                items.Add((parentPath, link));
            }
        }
        return (true, string.Empty,
            items.OrderBy(item => item.Link.FullPath, StringComparer.OrdinalIgnoreCase).ToList(),
            entries, pendingParents.Count);
    }

    private static async Task<(bool Success, string Message, List<SubmoduleOperationEntry> Entries)>
        PushSubmodulesAsync(
            IEnumerable<(string ParentPath, GitlinkDescriptor Link)> items,
            CancellationToken cancellation)
    {
        var entries = new List<SubmoduleOperationEntry>();
        foreach (var (_, link) in items)
        {
            var result = await GitRunner.RunAsync(link.FullPath,
                ["push", "origin", link.Branch], timeoutSeconds: 600, cancellation: cancellation);
            var entry = new SubmoduleOperationEntry(link.RelativePath, link.Branch,
                link.HeadSha, link.HeadSha,
                result.Success ? SubmoduleOperationOutcome.Success : SubmoduleOperationOutcome.Failed,
                result.Success ? result.Output : $"退出码 {result.ExitCode}: {result.Output}",
                link.Kind, result.Success);
            entries.Add(entry);
            if (!result.Success)
                return (false, $"子模块推送失败 [{link.RelativePath}]:\n{result.Output}", entries);
        }
        return (true, entries.Count == 0 ? "没有子模块需要推送" : $"已推送 {entries.Count} 个子模块", entries);
    }

    // ---------------------------------------------------------------- 批量提交(PJ-07)

    public async Task<BatchCommitReport> CommitAllAsync(
        string message,
        IProgress<string>? progress,
        Action<string, CommitReport>? onProjectDone = null,
        bool includeSubmodules = false,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
        => await CommitAllAsync(message, progress, onProjectDone,
            includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent,
            submoduleMessage, cancellation);

    public async Task<BatchCommitReport> CommitAllAsync(
        string message,
        IProgress<string>? progress,
        Action<string, CommitReport>? onProjectDone,
        RepositoryTarget target,
        string? submoduleMessage = null,
        CancellationToken cancellation = default)
    {
        var (listResult, worktrees) = await ListWorktreesAsync();
        if (!listResult.Success)
            return new BatchCommitReport(false, $"获取工作树列表失败:\n{listResult.Output}", [],
                Target: target);
        worktrees = worktrees.Where(worktree => Directory.Exists(worktree.WorktreePath)).ToList();
        if (worktrees.Count == 0)
            return new BatchCommitReport(false, "未发现可提交的工作树", [], Target: target);

        var results = new List<ProjectCommitResult>();
        for (var i = 0; i < worktrees.Count; i++)
        {
            var worktree = worktrees[i];
            progress?.Report($"[{i + 1}/{worktrees.Count}] {worktree.BranchName} ...");
            var report = await CommitWorktreeAsync(worktree, message, progress,
                target, submoduleMessage, cancellation);
            results.Add(new ProjectCommitResult(worktree.BranchName, report));
            onProjectDone?.Invoke(worktree.BranchName, report);
            progress?.Report($"[{i + 1}/{worktrees.Count}] {worktree.BranchName} " +
                             (report.Outcome is CommitOutcome.Success or CommitOutcome.Skipped ? "✓ " : "✗ ") +
                             report.Message);
        }

        var success = results.Count(item => item.Report.Outcome == CommitOutcome.Success);
        var skipped = results.Count(item => item.Report.Outcome == CommitOutcome.Skipped);
        var rejected = results.Count(item => item.Report.Outcome == CommitOutcome.Rejected);
        var failed = results.Count(item => item.Report.Outcome == CommitOutcome.Failed);
        var summary = $"批量提交完成: 成功 {success} | 无变更跳过 {skipped} | 拒绝 {rejected} | 失败 {failed}";
        var failedNames = results.Where(item => item.Report.Outcome is CommitOutcome.Failed or CommitOutcome.Rejected)
            .Select(item => item.Project).ToList();
        if (failedNames.Count > 0)
            summary += $"\n失败/拒绝项目: {string.Join(", ", failedNames)}";
        var pendingCount = results.Count(item => item.Report.ParentPointerPending);
        if (pendingCount > 0)
            summary += $"\n待收口父项目: {pendingCount}";
        return new BatchCommitReport(failed == 0 && rejected == 0, summary, results,
            results.Any(item => item.Report.PartialCompletion)
            || success + skipped > 0 && failed + rejected > 0,
            target, pendingCount);
    }

    private static string BuildLfsPrompt(string name, IEnumerable<LargeFileEntry> files)
    {
        var list = files.ToList();
        var prompt = new StringBuilder();
        prompt.AppendLine($"项目【{name}】发现 {list.Count} 个超限文件尚未使用 Git LFS 指针:");
        foreach (var file in list)
            prompt.AppendLine($"  • {file.RelativePath}({file.FormattedSize})");
        prompt.Append("是否启用 Git LFS 后继续?选择「否」将中止该项目且不创建提交。");
        return prompt.ToString();
    }

    private static string BuildSubmoduleCommitPrompt(
        string parent,
        IEnumerable<(GitlinkDescriptor Link, RepositoryCommitPlan Plan)> plans,
        RepositoryTarget target)
    {
        var list = plans.ToList();
        var prompt = new StringBuilder();
        prompt.AppendLine($"项目【{parent}】将联动提交 {list.Count} 个子模块:");
        foreach (var (link, plan) in list)
            prompt.AppendLine($"  • {link.RelativePath} [{link.Branch}] 修改 {link.ModifiedCount} / 删除 {link.DeletedCount} / 未跟踪 {link.UntrackedCount}\n    描述: {plan.Message}");
        prompt.AppendLine();
        prompt.Append(target == RepositoryTarget.Submodules
            ? "本次只提交子模块，父分支将保留待收口 gitlink。多仓库无法原子回退，是否继续?"
            : "执行顺序固定为子模块先提交、父项目后提交。多仓库无法原子回退，是否继续?");
        return prompt.ToString();
    }

    private static IReadOnlyList<SubmoduleOperationEntry> BuildSkippedEntries(
        IEnumerable<GitlinkDescriptor> links)
        => links.Select(link => new SubmoduleOperationEntry(link.RelativePath, link.Branch,
            link.HeadSha, link.HeadSha, SubmoduleOperationOutcome.Skipped,
            link.IsDirty ? "尚未执行" : "工作树干净，已跳过", link.Kind)).ToList();

    private static void ReplaceEntry(
        List<SubmoduleOperationEntry> entries, SubmoduleOperationEntry replacement)
    {
        var index = entries.FindIndex(item => item.RelativePath.Equals(
            replacement.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            entries[index] = replacement;
        else
            entries.Add(replacement);
    }

    private static SubmoduleOperationOutcome ToSubmoduleOutcome(CommitOutcome outcome) => outcome switch
    {
        CommitOutcome.Success => SubmoduleOperationOutcome.Success,
        CommitOutcome.Skipped => SubmoduleOperationOutcome.Skipped,
        CommitOutcome.Rejected => SubmoduleOperationOutcome.Rejected,
        _ => SubmoduleOperationOutcome.Failed,
    };

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    // ---------------------------------------------------------------- 扫描(PJ-10)

    public async Task<(bool Success, string Message)> ScanAsync(string name)
    {
        name = name.Trim();
        var worktreePath = Path.Combine(WorktreeRoot, name);
        if (!Directory.Exists(worktreePath))
            return (false, $"工作树目录不存在: {worktreePath}");

        var warnBytes = WarnBytes;
        var rejectBytes = RejectBytes;
        var (status, largeFiles) = await Task.Run(() =>
            WorktreeFileScanner.ScanDirectory(worktreePath, warnBytes, rejectBytes));

        if (largeFiles.Count == 0)
            return (true, $"[{name}] 文件大小检查通过,无 ≥{warnBytes / 1024 / 1024}MB 文件");

        var sb = new StringBuilder();
        sb.Append($"[{name}] 状态: {status switch { FileSizeCheckStatus.Rejected => $"含 ≥{rejectBytes / 1024 / 1024}MB 文件(提交时需 LFS)", FileSizeCheckStatus.Warning => "仅警告级大文件", _ => "正常" }},共 {largeFiles.Count} 个大文件:");
        foreach (var file in largeFiles)
        {
            var tag = file.SizeBytes >= rejectBytes ? "LFS" : "警告";
            sb.Append($"\n  [{tag}] {file.RelativePath}({file.FormattedSize})");
        }

        return (true, sb.ToString());
    }

    // ---------------------------------------------------------------- 继承树(PJ-04,移植 V1 merge-base 算法)

    /// <summary>
    /// 继承树入口(PJ-04):默认读文件缓存秒开;refresh=true 重扫并更新缓存;
    /// cachedOnly=true 无缓存时不触发扫描(视图启动自动加载用)。
    /// </summary>
    public async Task<(bool Success, string Message, BranchNode? Root)> BuildTreeAsync(
        IProgress<string>? progress, bool refresh = false, bool cachedOnly = false)
    {
        if (!refresh)
        {
            var cached = LoadTreeCache();
            if (cached != null)
            {
                var (builtAt, count, cachedRoot) = cached.Value;
                ApplyNotes(cachedRoot);
                var sbc = new StringBuilder();
                sbc.Append($"分支继承树(共 {count} 个分支,根 = {BaseBranch};缓存于 {builtAt:yyyy-MM-dd HH:mm},proj.tree refresh=true 重新扫描):");
                RenderTree(cachedRoot, sbc, "", isRoot: true);
                return (true, sbc.ToString(), cachedRoot);
            }

            if (cachedOnly)
                return (true, "尚无继承树缓存,点击「重新扫描」或执行 proj.tree refresh=true 构建", null);
        }

        if (!Directory.Exists(BareRepo))
            return (false, $"裸仓库路径不存在: {BareRepo}", null);

        var branches = await GetAllBranchesInfoAsync(progress);
        if (branches == null)
            return (false, "无法获取分支列表", null);
        if (branches.Count == 0)
            return (false, "未发现任何分支", null);

        var root = branches.FirstOrDefault(
            b => b.Name.Equals(BaseBranch, StringComparison.OrdinalIgnoreCase));
        if (root == null)
            return (false, $"未找到根模板分支: {BaseBranch}", null);

        var rootNode = await BuildInheritanceTreeAsync(branches, root, progress);
        SaveTreeCache(rootNode, branches.Count, progress); // 缓存存原始提交信息,描述覆盖只在读取侧

        ApplyNotes(rootNode);
        var sb = new StringBuilder();
        sb.Append($"分支继承树(共 {branches.Count} 个分支,根 = {BaseBranch};已缓存):");
        RenderTree(rootNode, sb, "", isRoot: true);
        return (true, sb.ToString(), rootNode);
    }

    /// <summary>用 branch_notes 的项目描述覆盖节点默认描述(DT-02)。</summary>
    private void ApplyNotes(BranchNode root)
    {
        var notes = NotesProvider?.Invoke();
        if (notes == null || notes.Count == 0)
            return;
        Overlay(root);

        void Overlay(BranchNode node)
        {
            if (notes.TryGetValue(node.BranchName, out var note) && note.Length > 0)
                node.Description = note;
            foreach (var child in node.Children)
                Overlay(child);
        }
    }

    public sealed class BranchInfo
    {
        public required string Name { get; init; }
        public string TipSha { get; set; } = "";
        public string LastCommitTime { get; set; } = "";
        public string LastCommitMessage { get; set; } = "";
    }

    public sealed class BranchNode : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isExpanded;

        public required string BranchName { get; init; }

        /// <summary>展示描述:默认最后提交信息,读取时被 branch_notes 覆盖(DT-02)。</summary>
        public string Description { get; set; } = "";

        public string LastPushTime { get; init; } = "";
        public List<BranchNode> Children { get; } = new();

        /// <summary>继承树视图的展开状态(UI-02;TreeViewItem 双向绑定)。</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public async Task<List<BranchInfo>?> GetAllBranchesInfoAsync(IProgress<string>? progress)
    {
        var shaTime = await GitRunner.RunAsync(BareRepo,
            ["for-each-ref", "--sort=committerdate",
                "--format=%(refname:short)|%(objectname)|%(committerdate:iso-local)", "refs/heads/"]);
        if (!shaTime.Success)
            return null;

        var list = new List<BranchInfo>();
        foreach (var line in shaTime.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 3)
                continue;
            list.Add(new BranchInfo
            {
                Name = parts[0].Trim(),
                TipSha = parts[1].Trim(),
                LastCommitTime = parts[2].Trim(),
            });
        }

        // 提交说明逐分支单独取,避免 subject 中的 | 破坏解析(V1 同款)
        foreach (var b in list)
        {
            var msg = await GitRunner.RunAsync(BareRepo, ["log", "-1", "--pretty=%s", b.Name]);
            b.LastCommitMessage = msg.Success ? msg.Output.Trim() : "(无法获取提交信息)";
        }

        return list;
    }

    /// <summary>
    /// 继承树算法 v2(修正 V1 缺陷):
    /// 一次性取每个分支的完整提交链,比对全部在内存完成(git 调用从 O(n²) 降到 O(n))。
    /// 判定规则——候选 P 是 child 的父分支候选,当且仅当 child 提交链上第一个
    /// 属于 P 的提交(分叉点)是 P 的**自有提交**(不在根模板分支历史内):
    /// 分叉点若落在模板历史里,说明两者只是共同的模板血统,不构成父子证据
    /// (V1 用 merge-base 最短距离,同点分叉的兄弟分支会被随机挂成父子)。
    /// 多个候选同时成立时取分叉点最深(离 child tip 最近)者;仍并列按名称升序
    /// (编号约定 = 创建顺序)。无任何候选 → 挂根模板。
    /// </summary>
    public async Task<BranchNode> BuildInheritanceTreeAsync(
        List<BranchInfo> allBranches, BranchInfo rootInfo, IProgress<string>? progress)
    {
        // 1. 读取每个分支的完整提交链(tip → root 有序)
        var historyOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var setOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < allBranches.Count; i++)
        {
            var b = allBranches[i];
            progress?.Report($"读取提交历史 {i + 1}/{allBranches.Count}: {b.Name}");
            var rev = await GitRunner.RunAsync(BareRepo, ["rev-list", b.Name]);
            var shas = rev.Success
                ? rev.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList()
                : new List<string>();
            historyOf[b.Name] = shas;
            setOf[b.Name] = new HashSet<string>(shas, StringComparer.Ordinal);
        }

        var rootSet = setOf[rootInfo.Name];

        var nodeMap = allBranches.ToDictionary(
            b => b.Name,
            b => new BranchNode
            {
                BranchName = b.Name,
                Description = string.IsNullOrWhiteSpace(b.LastCommitMessage) ? "(无提交信息)" : b.LastCommitMessage,
                LastPushTime = b.LastCommitTime,
            },
            StringComparer.OrdinalIgnoreCase);

        var rootNode = nodeMap[rootInfo.Name];

        // 2. 内存内为每个分支找父
        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in allBranches)
        {
            if (child.Name.Equals(rootInfo.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            string? best = null;
            var bestDepth = int.MaxValue;
            var childHistory = historyOf[child.Name];

            foreach (var candidate in allBranches)
            {
                if (candidate.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.Equals(rootInfo.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 工作流不变量:分支只能从已存在的分支创建,而项目编号(yyyy-nnn)即创建顺序,
                // 父分支名必然序在子分支之前。排除反向候选,父子互认的环从结构上不可能出现
                // (否则 A 派生 B 后 A 继续提交,fork 点是 A 的自有提交,B 会反过来成为 A 的"父")。
                if (string.Compare(candidate.Name, child.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                var candidateSet = setOf[candidate.Name];

                // 分叉点 = child 链上(从 tip 往下)第一个也属于 candidate 的提交
                string? fork = null;
                var depth = -1;
                for (var d = 0; d < childHistory.Count; d++)
                {
                    if (candidateSet.Contains(childHistory[d]))
                    {
                        fork = childHistory[d];
                        depth = d;
                        break;
                    }
                }

                if (fork == null || rootSet.Contains(fork))
                    continue; // 无交点,或只共享模板血统

                if (depth < bestDepth
                    || (depth == bestDepth && best != null
                        && string.Compare(candidate.Name, best, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    bestDepth = depth;
                    best = candidate.Name;
                }
            }

            parentOf[child.Name] = best ?? rootInfo.Name;
        }

        // 3. 环修复(backstop):tip 相同的分支对(创建后均无新提交)会互认父子形成
        // 从根不可达的环——把每个不可达分量中名称最小者(创建更早)改挂到根。
        while (true)
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootInfo.Name };
            bool grew;
            do
            {
                grew = false;
                foreach (var (child, parent) in parentOf)
                {
                    if (!reachable.Contains(child) && reachable.Contains(parent))
                    {
                        reachable.Add(child);
                        grew = true;
                    }
                }
            }
            while (grew);

            var unreachable = parentOf.Keys.Where(n => !reachable.Contains(n)).ToList();
            if (unreachable.Count == 0)
                break;

            parentOf[unreachable.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First()] = rootInfo.Name;
        }

        foreach (var (child, parent) in parentOf)
            nodeMap[parent].Children.Add(nodeMap[child]);

        // 子节点按名称排序,输出稳定
        SortChildren(rootNode);
        return rootNode;
    }

    private static void SortChildren(BranchNode node)
    {
        node.Children.Sort((a, b) => string.Compare(a.BranchName, b.BranchName, StringComparison.OrdinalIgnoreCase));
        foreach (var child in node.Children)
            SortChildren(child);
    }

    private static void RenderTree(BranchNode node, StringBuilder sb, string prefix, bool isRoot)
    {
        if (isRoot)
        {
            sb.Append($"\n{node.BranchName}  [{node.LastPushTime}]  {node.Description}");
        }

        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var isLast = i == children.Count - 1;
            sb.Append($"\n{prefix}{(isLast ? "└─ " : "├─ ")}{child.BranchName}  [{child.LastPushTime}]  {child.Description}");
            RenderTree(child, sb, prefix + (isLast ? "   " : "│  "), isRoot: false);
        }
    }

    // ---------------------------------------------------------------- 继承树文件缓存

    private string TreeCachePath => Path.Combine(_dataDir, "data", "branch-tree.json");

    private sealed record TreeCacheNode(string Name, string Time, string Desc, List<TreeCacheNode> Children);

    private sealed record TreeCacheFile(DateTime BuiltAt, int BranchCount, string BareRepo, TreeCacheNode Root);

    private void SaveTreeCache(BranchNode root, int branchCount, IProgress<string>? progress)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TreeCachePath)!);
            var file = new TreeCacheFile(DateTime.Now, branchCount, BareRepo, ToCacheNode(root));
            File.WriteAllText(TreeCachePath, System.Text.Json.JsonSerializer.Serialize(
                file, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            progress?.Report($"继承树已缓存: {TreeCachePath}");
        }
        catch (Exception ex)
        {
            progress?.Report($"继承树缓存写入失败(不影响本次结果): {ex.Message}");
        }
    }

    private (DateTime BuiltAt, int Count, BranchNode Root)? LoadTreeCache()
    {
        try
        {
            if (!File.Exists(TreeCachePath))
                return null;
            var file = System.Text.Json.JsonSerializer.Deserialize<TreeCacheFile>(
                File.ReadAllText(TreeCachePath));
            if (file?.Root == null)
                return null;
            // 缓存与当前裸仓库不匹配(如切换过 proj.barerepo)视同无缓存
            if (!string.Equals(file.BareRepo, BareRepo, StringComparison.OrdinalIgnoreCase))
                return null;
            return (file.BuiltAt, file.BranchCount, FromCacheNode(file.Root));
        }
        catch
        {
            return null; // 缓存损坏视同无缓存,重扫即可恢复
        }
    }

    private static TreeCacheNode ToCacheNode(BranchNode node) => new(
        node.BranchName, node.LastPushTime, node.Description,
        node.Children.Select(ToCacheNode).ToList());

    private static BranchNode FromCacheNode(TreeCacheNode dto)
    {
        var node = new BranchNode
        {
            BranchName = dto.Name,
            LastPushTime = dto.Time,
            Description = dto.Desc,
        };
        foreach (var child in dto.Children)
            node.Children.Add(FromCacheNode(child));
        return node;
    }

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

    // ---------------------------------------------------------------- 打开(PJ-09)

    public (bool Success, string Message) OpenFolder(string? name)
    {
        var path = string.IsNullOrWhiteSpace(name)
            ? WorktreeRoot
            : Path.Combine(WorktreeRoot, name.Trim());
        if (!Directory.Exists(path))
            return (false, $"目录不存在: {path}");

        System.Diagnostics.Process.Start("explorer.exe", path);
        return (true, $"已在资源管理器中打开: {path}");
    }

    // ---------------------------------------------------------------- Meta 文件夹(V2.0.1 MF-10/12)

    /// <summary>
    /// 扫描全部 worktree 根下以 z/Z 开头的一级子文件夹。
    /// 单项目枚举失败只计入警告并跳过,不拖垮整次扫描。
    /// </summary>
    public async Task<(GitResult Git, List<MetaFolderInfo> Metas, List<string> Warnings)> ListMetaFoldersAsync()
    {
        var (git, worktrees) = await ListWorktreesAsync();
        if (!git.Success)
            return (git, new List<MetaFolderInfo>(), new List<string>());

        var metas = new List<MetaFolderInfo>();
        var warnings = new List<string>();

        foreach (var w in worktrees)
        {
            if (!Directory.Exists(w.WorktreePath))
                continue;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(w.WorktreePath))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Length == 0 || (name[0] != 'z' && name[0] != 'Z'))
                        continue;

                    var time = "";
                    try
                    {
                        time = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm");
                    }
                    catch
                    {
                        // 取时间失败不影响列入
                    }

                    metas.Add(new MetaFolderInfo(w.BranchName, name, dir, time));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"{w.BranchName}: {ex.Message}");
            }
        }

        metas.Sort((a, b) =>
        {
            var c = string.Compare(a.ProjectName, b.ProjectName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.MetaName, b.MetaName, StringComparison.OrdinalIgnoreCase);
        });

        return (git, metas, warnings);
    }

    public async Task<(bool Success, string Message)> OpenMetaFolderAsync(
        string? path,
        string? projectName,
        string? metaName)
    {
        string target;
        if (!string.IsNullOrWhiteSpace(path))
        {
            target = path.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(metaName))
        {
            target = Path.Combine(WorktreeRoot, projectName.Trim(), metaName.Trim());
        }
        else
        {
            return (false, "请提供 path= 或同时提供 name= 与 meta=");
        }

        try
        {
            target = NormalizePath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, $"元文件夹路径无效: {ex.Message}");
        }

        if (!Directory.Exists(target))
            return (false, $"目录不存在: {target}");

        var folderName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (folderName.Length == 0 || (folderName[0] != 'z' && folderName[0] != 'Z'))
            return (false, $"不是 Z 级元文件夹(名称须以 z/Z 开头): {target}");

        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            return (false, $"元文件夹不能是符号链接或目录联接: {target}");

        var parent = Directory.GetParent(target)?.FullName;
        var (git, worktrees) = await ListWorktreesAsync();
        if (!git.Success)
            return (false, $"无法验证元文件夹所属工作树:\n{git.Output}");
        if (parent == null || !worktrees.Any(worktree => PathsEqual(parent, worktree.WorktreePath)))
            return (false, $"目录不是已登记 worktree 的一级元文件夹: {target}");

        System.Diagnostics.Process.Start("explorer.exe", target);
        return (true, $"已在资源管理器中打开: {target}");
    }

    private bool TryValidateManagedDirectChild(
        string path,
        bool rejectReparsePoint,
        out string error)
    {
        try
        {
            if (!TryValidateWorktreeRoot(out error))
                return false;

            var root = NormalizePath(WorktreeRoot);
            var candidate = NormalizePath(path);

            if (PathsEqual(candidate, BareRepo))
            {
                error = "目标是裸仓库";
                return false;
            }

            var parent = Directory.GetParent(candidate)?.FullName;
            if (parent == null || !PathsEqual(parent, root))
            {
                error = $"目标必须是工作树根目录的直接子目录({root})";
                return false;
            }

            if (rejectReparsePoint && Directory.Exists(candidate)
                && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                error = "目标是符号链接或目录联接";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryValidateWorktreeRoot(out string error)
    {
        try
        {
            var root = NormalizePath(WorktreeRoot);
            if (!Directory.Exists(root))
            {
                error = $"目录不存在: {root}";
                return false;
            }

            var volumeRoot = Path.GetPathRoot(root);
            if (volumeRoot != null && PathsEqual(root, volumeRoot))
            {
                error = "不能把磁盘根目录配置为工作树根目录";
                return false;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                error = "工作树根目录不能是符号链接或目录联接";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (pathRoot != null && fullPath.Equals(pathRoot, StringComparison.OrdinalIgnoreCase))
            return pathRoot;

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
