using System.IO;
using System.Text;
using HistoryVulcan.Core.Storage;

namespace HistoryJanus.Git;

public sealed record WorktreeInfo(
    string BranchName,
    string WorktreePath,
    string LastCommitTime = "",
    string LastCommitMessage = "",
    bool? IsClean = null,
    string WorktreeStatusMessage = "")
{
    /// <summary>工作树目录名（路径权威侧的文件夹名，可能与分支名不一致）。</summary>
    public string FolderName
    {
        get
        {
            var trimmed = WorktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed);
        }
    }

    /// <summary>分支名是唯一身份权威；目录名必须与之相同，否则视为不合规则。</summary>
    public bool HasNameMismatch
        => FolderName.Length > 0
           && !FolderName.Equals(BranchName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>某项目根下以 z/Z 开头的一级 Meta 文件夹。</summary>
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
/// 确认交互统一走总线确认通道，运行参数通过配置服务现读现生效。
///
/// ProjectService 按职责拆分为多个 partial 文件：
/// - 本文件:配置、工作树增删改查、扫描、打开、继承树转调、**路径守卫**;
/// - ProjectService.Commit.cs：提交与推送；
/// - ProjectService.Repair.cs：仓库修复；
/// - ProjectService.Meta.cs：Meta 文件夹；
/// - BranchTreeService.cs:继承树构建与缓存(已抽为独立类,本类保留转调)。
///
/// 路径守卫 TryValidateManagedDirectChild / TryValidateWorktreeRoot 刻意留在本文件:
/// 它是四个切面共用的安全底座,不随任一职责外迁。
/// </summary>
public sealed partial class ProjectService
{
    // 设置键：首启写入默认值，此后 app.set / app.get 可读写。
    public const string KeyBareRepo = "proj.barerepo";
    public const string KeyWorktreeRoot = "proj.worktreeroot";
    public const string KeyBaseBranch = "proj.basebranch";
    public const string KeyWarnMb = "proj.warnmb";
    public const string KeyRejectMb = "proj.rejectmb";
    public const string KeyProtected = "proj.protected";

    private readonly ISettingsService _settings;
    private readonly string _dataDir;
    private readonly GitlinkService _gitlinks = new();

    /// <summary>继承树构建与缓存由 BranchTreeService 实现；本类保留同名转调方法。</summary>
    private readonly BranchTreeService _tree;

    /// <summary>执行中途的二次确认通道(LFS 启用询问等);由装配点接到总线 Confirmation。</summary>
    private readonly Func<string, bool> _confirm;

    /// <summary>
    /// 分支描述提供者，由装配点连接 HistoryRecorder.AllNotes：
    /// 继承树在展示前用它覆盖节点描述——缓存里存的是构建时的提交信息,
    /// 覆盖在读取时进行,janus.proj.note 后无需重扫即可见。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? NotesProvider
    {
        get => _tree.NotesProvider;
        set => _tree.NotesProvider = value;
    }

    public ProjectService(ISettingsService settings, Func<string, bool> confirm, string dataDir)
    {
        _settings = settings;
        _confirm = confirm;
        _dataDir = dataDir;
        // 配置以委托传入：proj.barerepo / proj.basebranch 现读现生效，不能取构造时的快照。
        _tree = new BranchTreeService(() => BareRepo, () => BaseBranch, dataDir);
    }

    // ---------------------------------------------------------------- 配置(现读现生效)

    public string BareRepo =>
        _settings.Get(KeyBareRepo) ?? @"C:\OneHistory\HistoryVesta\HistoryVesta.git";

    public string WorktreeRoot =>
        _settings.Get(KeyWorktreeRoot) ?? @"C:\OneHistory\HistoryVesta";

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
        SetIfMissing(KeyBareRepo, @"C:\OneHistory\HistoryVesta\HistoryVesta.git");
        SetIfMissing(KeyWorktreeRoot, @"C:\OneHistory\HistoryVesta");
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

    // ---------------------------------------------------------------- 列表

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

        // 最后提交时间与主题由单次 for-each-ref 补全；失败不影响主流程。
        var times = await GitRunner.RunAsync(BareRepo,
            ["for-each-ref", "--format=%(refname:short)|%(committerdate:iso-local)|%(subject)", "refs/heads/"]);
        if (times.Success)
        {
            var map = new Dictionary<string, (string Time, string Subject)>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in times.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 3);
                if (parts.Length >= 2)
                {
                    map[parts[0].Trim()] = (
                        parts[1].Trim(),
                        parts.Length >= 3 ? parts[2].Trim() : "");
                }
            }

            list = list.Select(w =>
            {
                if (!map.TryGetValue(w.BranchName, out var tip))
                    return w;
                return w with
                {
                    LastCommitTime = tip.Time,
                    LastCommitMessage = tip.Subject,
                };
            }).ToList();
        }

        return (result, list);
    }

    /// <summary>并行读取项目总览所需的工作树状态；单个仓库失败时保留该行并标记为未知。</summary>
    public async Task<List<WorktreeInfo>> ReadWorktreeStatusesAsync(
        IReadOnlyList<WorktreeInfo> worktrees,
        CancellationToken cancellation = default)
    {
        var tasks = worktrees.Select(async worktree =>
        {
            if (!Directory.Exists(worktree.WorktreePath))
            {
                return worktree with
                {
                    IsClean = null,
                    WorktreeStatusMessage = "工作树目录不存在",
                };
            }

            var status = await GitRunner.RunAsync(
                worktree.WorktreePath,
                ["status", "--porcelain=v1", "--untracked-files=all"],
                cancellation).ConfigureAwait(false);
            if (!status.Success)
            {
                return worktree with
                {
                    IsClean = null,
                    WorktreeStatusMessage = string.IsNullOrWhiteSpace(status.Output)
                        ? "工作树状态检查失败"
                        : status.Output,
                };
            }

            var isClean = string.IsNullOrWhiteSpace(status.Output);
            return worktree with
            {
                IsClean = isClean,
                WorktreeStatusMessage = isClean ? "工作树干净" : "工作树有未提交或未跟踪的文件",
            };
        });

        return [.. await Task.WhenAll(tasks).ConfigureAwait(false)];
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

    /// <summary>解析 git worktree list --porcelain 输出。</summary>
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

    // ---------------------------------------------------------------- 创建

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

    // ---------------------------------------------------------------- 删除

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
                // worktree 可能已损坏；移除失败时仍继续尝试删除分支。
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

    // ---------------------------------------------------------------- 扫描

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

    // ---------------------------------------------------------------- 继承树
    // 构建、渲染与文件缓存由 BranchTreeService 负责；此处只保留转调，
    // ProjectCommands / BranchHistoryService 的调用签名一字未改。

    /// <summary>
    /// 继承树入口：默认读取文件缓存；refresh=true 时重扫并更新缓存；
    /// cachedOnly=true 无缓存时不触发扫描(视图启动自动加载用)。
    /// </summary>
    public Task<(bool Success, string Message, BranchNode? Root)> BuildTreeAsync(
        IProgress<string>? progress, bool refresh = false, bool cachedOnly = false)
        => _tree.BuildTreeAsync(progress, refresh, cachedOnly);

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

        /// <summary>展示描述默认使用最后提交信息，读取时可被 branch_notes 覆盖。</summary>
        public string Description { get; set; } = "";

        public string LastPushTime { get; init; } = "";
        public List<BranchNode> Children { get; } = new();

        /// <summary>继承树视图的展开状态，与 TreeViewItem 双向绑定。</summary>
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

    public Task<List<BranchInfo>?> GetAllBranchesInfoAsync(IProgress<string>? progress)
        => _tree.GetAllBranchesInfoAsync(progress);

    public Task<BranchNode> BuildInheritanceTreeAsync(
        List<BranchInfo> allBranches, BranchInfo rootInfo, IProgress<string>? progress)
        => _tree.BuildInheritanceTreeAsync(allBranches, rootInfo, progress);


    // ---------------------------------------------------------------- 打开

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
