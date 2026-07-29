using System.IO;
using System.Text;
using AppShell.Services;

namespace OneHistoryStudio.Git;

/// <summary>
/// 分支继承树的构建、渲染与文件缓存。
/// 分支树构建服务只依赖裸仓库路径、根分支名、
/// 数据目录和描述提供者,与提交/推送/修复互不相干,是最干净的一刀。
/// ProjectService 保留同名转调方法,调用方签名一字未改。
///
/// 节点类型仍是 ProjectService.BranchNode / BranchInfo:它们是嵌套公共类型,
/// 移出会改变 11 处调用点的类型名。本版铁律是对外行为不变,不为整理而制造调用面变更。
///
/// 配置以委托方式注入而非快照：proj.barerepo / proj.basebranch 现读现生效，
/// 构造时取值会让 app.set 改配置后本服务仍用旧路径。
/// </summary>
public sealed class BranchTreeService
{
    private readonly Func<string> _bareRepo;
    private readonly Func<string> _baseBranch;
    private readonly string _dataDir;

    public BranchTreeService(Func<string> bareRepo, Func<string> baseBranch, string dataDir)
    {
        _bareRepo = bareRepo;
        _baseBranch = baseBranch;
        _dataDir = dataDir;
    }

    private string BareRepo => _bareRepo();

    private string BaseBranch => _baseBranch();

    /// <summary>
    /// 分支描述提供者，由装配点连接 HistoryRecorder.AllNotes：
    /// 继承树在展示前用它覆盖节点描述——缓存里存的是构建时的提交信息,
    /// 覆盖在读取时进行,proj.note 后无需重扫即可见。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? NotesProvider { get; set; }

    // ---------------------------------------------------------------- 继承树

    /// <summary>
    /// 继承树入口：默认读取文件缓存；refresh=true 时重扫并更新缓存；
    /// cachedOnly=true 无缓存时不触发扫描(视图启动自动加载用)。
    /// </summary>
    public async Task<(bool Success, string Message, ProjectService.BranchNode? Root)> BuildTreeAsync(
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

    /// <summary>用 branch_notes 的项目描述覆盖节点默认描述。</summary>
    private void ApplyNotes(ProjectService.BranchNode root)
    {
        var notes = NotesProvider?.Invoke();
        if (notes == null || notes.Count == 0)
            return;
        Overlay(root);

        void Overlay(ProjectService.BranchNode node)
        {
            if (notes.TryGetValue(node.BranchName, out var note) && note.Length > 0)
                node.Description = note;
            foreach (var child in node.Children)
                Overlay(child);
        }
    }

    public async Task<List<ProjectService.BranchInfo>?> GetAllBranchesInfoAsync(IProgress<string>? progress)
    {
        var shaTime = await GitRunner.RunAsync(BareRepo,
            ["for-each-ref", "--sort=committerdate",
                "--format=%(refname:short)|%(objectname)|%(committerdate:iso-local)", "refs/heads/"]);
        if (!shaTime.Success)
            return null;

        var list = new List<ProjectService.BranchInfo>();
        foreach (var line in shaTime.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 3)
                continue;
            list.Add(new ProjectService.BranchInfo
            {
                Name = parts[0].Trim(),
                TipSha = parts[1].Trim(),
                LastCommitTime = parts[2].Trim(),
            });
        }

        // 提交说明逐分支单独读取，避免 subject 中的 | 破坏解析。
        foreach (var b in list)
        {
            var msg = await GitRunner.RunAsync(BareRepo, ["log", "-1", "--pretty=%s", b.Name]);
            b.LastCommitMessage = msg.Success ? msg.Output.Trim() : "(无法获取提交信息)";
        }

        return list;
    }

    /// <summary>
    /// 继承树算法一次性读取每个分支的完整提交链，比对全部在内存完成，
    /// 从而把 git 调用从 O(n²) 降到 O(n)。
    /// 判定规则——候选 P 是 child 的父分支候选,当且仅当 child 提交链上第一个
    /// 属于 P 的提交(分叉点)是 P 的**自有提交**(不在根模板分支历史内):
    /// 分叉点若落在模板历史里，说明两者只是共同的模板血统，不构成父子证据；
    /// 不能只按 merge-base 最短距离判断，否则同点分叉的兄弟分支可能被错误挂成父子。
    /// 多个候选同时成立时取分叉点最深(离 child tip 最近)者;仍并列按名称升序
    /// (编号约定 = 创建顺序)。无任何候选 → 挂根模板。
    /// </summary>
    public async Task<ProjectService.BranchNode> BuildInheritanceTreeAsync(
        List<ProjectService.BranchInfo> allBranches,
        ProjectService.BranchInfo rootInfo,
        IProgress<string>? progress)
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
            b => new ProjectService.BranchNode
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

    private static void SortChildren(ProjectService.BranchNode node)
    {
        node.Children.Sort((a, b) => string.Compare(a.BranchName, b.BranchName, StringComparison.OrdinalIgnoreCase));
        foreach (var child in node.Children)
            SortChildren(child);
    }

    private static void RenderTree(
        ProjectService.BranchNode node, StringBuilder sb, string prefix, bool isRoot)
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

    private string TreeCachePath => Path.Combine(AppPaths.GetDataDir(_dataDir), "branch-tree.json");

    private sealed record TreeCacheNode(string Name, string Time, string Desc, List<TreeCacheNode> Children);

    private sealed record TreeCacheFile(DateTime BuiltAt, int BranchCount, string BareRepo, TreeCacheNode Root);

    private void SaveTreeCache(ProjectService.BranchNode root, int branchCount, IProgress<string>? progress)
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

    private (DateTime BuiltAt, int Count, ProjectService.BranchNode Root)? LoadTreeCache()
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

    private static TreeCacheNode ToCacheNode(ProjectService.BranchNode node) => new(
        node.BranchName, node.LastPushTime, node.Description,
        node.Children.Select(ToCacheNode).ToList());

    private static ProjectService.BranchNode FromCacheNode(TreeCacheNode dto)
    {
        var node = new ProjectService.BranchNode
        {
            BranchName = dto.Name,
            LastPushTime = dto.Time,
            Description = dto.Desc,
        };
        foreach (var child in dto.Children)
            node.Children.Add(FromCacheNode(child));
        return node;
    }
}
