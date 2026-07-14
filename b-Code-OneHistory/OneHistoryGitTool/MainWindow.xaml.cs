using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OneHistoryGitTool.Models;
using OneHistoryGitTool.Services;

namespace OneHistoryGitTool;

/// <summary>
/// OneHistory 新建分支 + Worktree 工具
/// </summary>
public partial class MainWindow : Window
{
    private string? _lastCreatedWorktreePath;
    private readonly ObservableCollection<WorktreeInfo> _worktrees = new();           // 完整列表
    private readonly ObservableCollection<WorktreeInfo> _filteredWorktrees = new();   // 搜索过滤后的列表（绑定到ListBox）

    // Git 操作继承树相关
    private readonly ObservableCollection<BranchNode> _treeRootNodes = new();
    private const string ProductionBareRepoPath = @"C:\OneHistory\OneHistory-Projects\OneHistory-Projects.git";
    private const string RootTemplateBranch = "0000-000-Template";
    private const string WorktreeRootPath = @"C:\OneHistory\OneHistory-Projects";

    // 受保护的分支，删除时需要额外警告
    private static readonly HashSet<string> ProtectedBranches = new(StringComparer.OrdinalIgnoreCase)
    {
        "0000-000-Template",
        "main",
        "master"
    };

    public MainWindow()
    {
        InitializeComponent();
        
        // 初始预览
        UpdatePreview();
        
        // 绑定删除列表（使用过滤后的集合，支持搜索）
        WorktreesListBox.ItemsSource = _filteredWorktrees;

        // 绑定继承树
        BranchTreeView.ItemsSource = _treeRootNodes;
        
        // 状态提示
        AppendLog("程序启动成功。Git 命令将使用 -C 参数直接操作裸仓库。");
        AppendLog("提示：项目名称请使用合法文件夹字符，避免使用 \\ / : * ? \" < > | 等符号。");
        AppendLog("提示：删除操作会先移除 worktree，再强制删除分支，操作不可撤销！");
    }

    private void ProjectNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void ProjectNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CreateButton.IsEnabled)
        {
            CreateButton_Click(sender, new RoutedEventArgs());
        }
    }

    private void UpdatePreview()
    {
        string name = ProjectNameBox.Text.Trim();
        string barePath = BareRepoPathBox.Text.Trim();
        string rootPath = WorktreeRootBox.Text.Trim();
        string baseBranch = BaseBranchBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            PreviewPathText.Text = "（请输入项目名称）";
            CommandPreviewText.Text = "";
            CreateButton.IsEnabled = false;
            return;
        }

        string targetPath = Path.Combine(rootPath, name);
        PreviewPathText.Text = targetPath;

        CommandPreviewText.Text =
            $"git -C \"{barePath}\" branch \"{name}\" \"{baseBranch}\"\n" +
            $"git -C \"{barePath}\" worktree add \"{targetPath}\" \"{name}\"";

        CreateButton.IsEnabled = true;
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        string projectName = ProjectNameBox.Text.Trim();
        string bareRepoPath = BareRepoPathBox.Text.Trim();
        string worktreeRoot = WorktreeRootBox.Text.Trim();
        string baseBranch = BaseBranchBox.Text.Trim();

        // 1. 基础校验
        if (string.IsNullOrWhiteSpace(projectName))
        {
            MessageBox.Show("项目名称不能为空！", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("项目名称包含非法字符（\\ / : * ? \" < > | 等）！", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(bareRepoPath) || !Directory.Exists(bareRepoPath))
        {
            MessageBox.Show("裸仓库路径不存在，请检查配置！", "路径错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string targetWorktreePath = Path.Combine(worktreeRoot, projectName);
        if (Directory.Exists(targetWorktreePath))
        {
            MessageBox.Show($"目标工作树目录已存在：\n{targetWorktreePath}\n\n请更换项目名称或手动删除该目录。", 
                            "目录冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 2. 检查分支是否已存在
        CreateButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        StatusText.Text = "正在检查分支是否已存在...";
        StatusText.Foreground = Brushes.OrangeRed;

        var branchCheck = await RunGitCommandAsync(bareRepoPath, $"branch --list \"{projectName}\"");
        if (branchCheck.ExitCode == 0 && branchCheck.Output.Contains(projectName))
        {
            AppendLog($"⚠ 分支已存在：{projectName}");
            MessageBox.Show($"分支 \"{projectName}\" 已经存在于裸仓库中！", "分支冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            CreateButton.IsEnabled = true;
            StatusText.Text = "就绪 - 请修改项目名称";
            return;
        }

        // 3. 执行创建流程
        AppendLog("");
        AppendLog($"========== 开始创建项目 [{projectName}] ==========");
        AppendLog($"裸仓库：{bareRepoPath}");
        AppendLog($"目标工作树：{targetWorktreePath}");
        AppendLog($"基于分支：{baseBranch}");

        StatusText.Text = "正在创建分支...";
        StatusText.Foreground = Brushes.DarkOrange;

        // 第1步：创建分支
        var createBranchResult = await RunGitCommandAsync(
            bareRepoPath, 
            $"branch \"{projectName}\" \"{baseBranch}\"");

        if (createBranchResult.ExitCode != 0)
        {
            AppendLog("❌ 创建分支失败！");
            AppendLog(createBranchResult.Output);
            FinishWithError("创建分支失败，请查看日志。");
            return;
        }

        AppendLog("✓ 分支创建成功");
        if (!string.IsNullOrWhiteSpace(createBranchResult.Output))
            AppendLog(createBranchResult.Output);

        StatusText.Text = "正在创建工作树...";
        StatusText.Foreground = Brushes.DarkOrange;

        // 第2步：创建 worktree
        var worktreeResult = await RunGitCommandAsync(
            bareRepoPath,
            $"worktree add \"{targetWorktreePath}\" \"{projectName}\"");

        if (worktreeResult.ExitCode != 0)
        {
            AppendLog("❌ 创建工作树失败！（分支已创建，但工作树创建失败）");
            AppendLog(worktreeResult.Output);
            FinishWithError("工作树创建失败，分支可能已残留，请手动清理。");
            return;
        }

        AppendLog("✓ 工作树创建成功！");
        if (!string.IsNullOrWhiteSpace(worktreeResult.Output))
            AppendLog(worktreeResult.Output);

        // 成功
        _lastCreatedWorktreePath = targetWorktreePath;
        OpenFolderButton.IsEnabled = true;

        AppendLog("========== 创建完成 ==========");
        AppendLog($"新项目已就绪：{targetWorktreePath}");
        AppendLog("你现在可以进入该目录开始工作，并使用 git add/commit/push。");

        StatusText.Text = "✅ 创建成功！";
        StatusText.Foreground = Brushes.DarkGreen;

        // 可选：自动打开文件夹（用户体验好）
        // System.Diagnostics.Process.Start("explorer.exe", targetWorktreePath);

        CreateButton.IsEnabled = true;
    }

    private void FinishWithError(string message)
    {
        StatusText.Text = "❌ " + message;
        StatusText.Foreground = Brushes.DarkRed;
        CreateButton.IsEnabled = true;
        MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastCreatedWorktreePath) && Directory.Exists(_lastCreatedWorktreePath))
        {
            Process.Start("explorer.exe", _lastCreatedWorktreePath);
        }
        else
        {
            MessageBox.Show("工作树目录不存在或尚未创建。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    #region ==================== 页面1：分支与工作树管理（创建/删除/搜索） ====================

    // ==================== 删除功能相关 ====================

    private async void RefreshWorktreesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWorktreesAsync();
    }

    private void WorktreesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorktreesListBox.SelectedItem is WorktreeInfo info)
        {
            SelectedBranchBox.Text = info.BranchName;
            DeleteButton.IsEnabled = true;
        }
        else
        {
            SelectedBranchBox.Clear();
            DeleteButton.IsEnabled = false;
        }
    }

    private async Task RefreshWorktreesAsync()
    {
        string bareRepoPath = BareRepoPathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(bareRepoPath) || !Directory.Exists(bareRepoPath))
        {
            MessageBox.Show("裸仓库路径无效，无法刷新工作树列表。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RefreshWorktreesButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        _worktrees.Clear();
        SelectedBranchBox.Clear();

        StatusText.Text = "正在刷新工作树列表...";
        StatusText.Foreground = Brushes.DarkOrange;

        AppendLog("");
        AppendLog("========== 刷新工作树列表 ==========");

        var result = await RunGitCommandAsync(bareRepoPath, "worktree list --porcelain");

        if (result.ExitCode != 0)
        {
            AppendLog("❌ 获取工作树列表失败");
            AppendLog(result.Output);
            StatusText.Text = "刷新失败";
            StatusText.Foreground = Brushes.DarkRed;
            RefreshWorktreesButton.IsEnabled = true;
            return;
        }

        var list = ParseWorktreeList(result.Output);
        
        foreach (var item in list)
        {
            _worktrees.Add(item);
        }

        AppendLog($"✓ 共发现 {_worktrees.Count} 个工作树");
        
        StatusText.Text = $"就绪 - 已加载 {_worktrees.Count} 个工作树";
        StatusText.Foreground = Brushes.DarkGreen;

        RefreshWorktreesButton.IsEnabled = true;

        // 应用当前搜索过滤
        ApplyDeleteFilter();
    }

    /// <summary>
    /// 非常简单的搜索过滤：只按分支名称前缀匹配（例如输入 2026）
    /// </summary>
    private void ApplyDeleteFilter()
    {
        string keyword = DeleteSearchBox?.Text?.Trim() ?? "";

        _filteredWorktrees.Clear();

        IEnumerable<WorktreeInfo> source = _worktrees;

        if (!string.IsNullOrEmpty(keyword))
        {
            source = _worktrees.Where(w => 
                w.BranchName.StartsWith(keyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in source)
        {
            _filteredWorktrees.Add(item);
        }

        // 过滤后清除当前选中，避免选中一个被过滤掉的项目
        WorktreesListBox.SelectedItem = null;
        SelectedBranchBox.Clear();
        DeleteButton.IsEnabled = false;
    }

    private void DeleteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyDeleteFilter();
    }

    /// <summary>
    /// 解析 git worktree list --porcelain 输出
    /// </summary>
    private List<WorktreeInfo> ParseWorktreeList(string output)
    {
        var result = new List<WorktreeInfo>();
        string? currentPath = null;
        string? currentBranch = null;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("worktree "))
            {
                // 保存上一个
                if (!string.IsNullOrEmpty(currentPath) && !string.IsNullOrEmpty(currentBranch))
                {
                    result.Add(new WorktreeInfo(currentBranch, currentPath));
                }
                currentPath = line.Substring("worktree ".Length).Trim();
                currentBranch = null;
            }
            else if (line.StartsWith("branch "))
            {
                string refName = line.Substring("branch ".Length).Trim();
                // refs/heads/xxx → xxx
                currentBranch = refName.StartsWith("refs/heads/") 
                    ? refName.Substring("refs/heads/".Length) 
                    : refName;
            }
        }

        // 最后一个
        if (!string.IsNullOrEmpty(currentPath) && !string.IsNullOrEmpty(currentBranch))
        {
            result.Add(new WorktreeInfo(currentBranch, currentPath));
        }

        return result;
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreesListBox.SelectedItem is not WorktreeInfo info)
            return;

        string bareRepoPath = BareRepoPathBox.Text.Trim();
        string branchName = info.BranchName;
        string worktreePath = info.WorktreePath;

        // 保护检查
        bool isProtected = ProtectedBranches.Contains(branchName);

        // 构建强警告消息
        string warningTitle = isProtected ? "【极度危险】正在尝试删除受保护分支！" : "确认删除分支与工作树";

        string warningText =
            $"你即将执行以下【不可撤销】的操作：\n\n" +
            $"• 删除 Git 分支：{branchName}\n" +
            $"• 删除工作树目录：{worktreePath}\n\n" +
            $"工作树目录内所有未提交的修改、未跟踪文件都将被永久删除！\n\n";

        if (isProtected)
        {
            warningText += "⚠⚠⚠ 这是受保护的分支（模板或其他重要分支），强烈不建议删除！\n\n";
        }

        warningText += "确定要继续吗？";

        var result = MessageBox.Show(
            warningText,
            warningTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);   // 默认选中 No，更安全

        if (result != MessageBoxResult.Yes)
        {
            AppendLog("用户取消了删除操作");
            return;
        }

        // 二次确认（尤其是保护分支）
        if (isProtected)
        {
            var second = MessageBox.Show(
                $"最后一次确认：你真的要删除受保护分支 \"{branchName}\" 吗？\n\n此操作将导致所有关联的工作树数据丢失，且无法通过此工具恢复！",
                "最终确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop,
                MessageBoxResult.No);

            if (second != MessageBoxResult.Yes)
            {
                AppendLog("用户在二次确认中取消");
                return;
            }
        }

        // 执行删除
        DeleteButton.IsEnabled = false;
        RefreshWorktreesButton.IsEnabled = false;

        AppendLog("");
        AppendLog($"========== 开始删除 [{branchName}] ==========");
        AppendLog($"工作树路径：{worktreePath}");

        StatusText.Text = "正在删除工作树...";
        StatusText.Foreground = Brushes.DarkRed;

        // 1. 先移除 worktree（--force 允许脏状态）
        var removeWt = await RunGitCommandAsync(bareRepoPath, $"worktree remove \"{worktreePath}\" --force");

        if (removeWt.ExitCode != 0)
        {
            AppendLog("❌ 移除工作树失败！");
            AppendLog(removeWt.Output);
            // 即使失败也尝试删分支（有些情况下 worktree 已经损坏）
        }
        else
        {
            AppendLog("✓ 工作树已移除");
        }

        StatusText.Text = "正在删除分支...";
        StatusText.Foreground = Brushes.DarkRed;

        // 2. 删除分支（-D 强制）
        var deleteBranch = await RunGitCommandAsync(bareRepoPath, $"branch -D \"{branchName}\"");

        if (deleteBranch.ExitCode != 0)
        {
            AppendLog("❌ 删除分支失败！");
            AppendLog(deleteBranch.Output);
            StatusText.Text = "删除失败（部分操作可能已执行）";
            StatusText.Foreground = Brushes.DarkRed;
        }
        else
        {
            AppendLog("✓ 分支已删除");
            AppendLog($"========== 删除完成 ==========");

            StatusText.Text = "✅ 删除成功！";
            StatusText.Foreground = Brushes.DarkGreen;

            // 刷新列表
            await RefreshWorktreesAsync();
        }

        DeleteButton.IsEnabled = false;
        SelectedBranchBox.Clear();
        RefreshWorktreesButton.IsEnabled = true;
    }

    #endregion

    #region ==================== 页面2：Git 操作继承树 ====================

    // ==================== Git 操作继承树（新核心功能） ====================

    private async void LoadTreeButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadGitInheritanceTreeAsync();
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _treeRootNodes)
        {
            ExpandAll(root, true);
        }
    }

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _treeRootNodes)
        {
            ExpandAll(root, false);
        }
    }

    private static void ExpandAll(BranchNode node, bool expand)
    {
        node.IsExpanded = expand;
        foreach (var child in node.Children)
        {
            ExpandAll(child, expand);
        }
    }

    /// <summary>
    /// 核心：从真实生产裸仓库加载完整分支继承树
    /// </summary>
    private async Task LoadGitInheritanceTreeAsync()
    {
        string barePath = ProductionBareRepoPath;

        if (!Directory.Exists(barePath))
        {
            MessageBox.Show($"真实生产裸仓库不存在：\n{barePath}\n\n请确认路径是否正确。", "路径错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadTreeButton.IsEnabled = false;
        ExpandAllButton.IsEnabled = false;
        CollapseAllButton.IsEnabled = false;
        _treeRootNodes.Clear();
        TreeStatusText.Text = "正在从裸仓库加载分支信息...";

        AppendLog("");
        AppendLog("========== 开始加载 Git 操作继承树 ==========");
        AppendLog($"裸仓库：{barePath}");

        try
        {
            // 1. 获取所有分支 + 最新提交信息
            var branches = await GetAllBranchesInfoAsync(barePath);
            AppendLog($"✓ 共发现 {branches.Count} 个分支");

            if (branches.Count == 0)
            {
                TreeStatusText.Text = "未发现任何分支";
                return;
            }

            // 3. 找到根模板分支
            var rootBranch = branches.FirstOrDefault(b => b.Name.Equals(RootTemplateBranch, StringComparison.OrdinalIgnoreCase));
            if (rootBranch == null)
            {
                MessageBox.Show($"未找到根模板分支：{RootTemplateBranch}", "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 4. 构建继承树（使用 merge-base + 距离计算的可靠算法）
            TreeStatusText.Text = "正在重建分支继承关系...";
            var rootNode = await BuildInheritanceTreeAsync(barePath, branches, rootBranch);

            _treeRootNodes.Add(rootNode);

            // 展开前两层，方便查看
            rootNode.IsExpanded = true;
            int expandedCount = 0;
            foreach (var child in rootNode.Children)
            {
                child.IsExpanded = true;
                expandedCount++;
                foreach (var grandChild in child.Children.Take(5))   // 最多再展开一层
                {
                    grandChild.IsExpanded = true;
                }
            }

            TreeStatusText.Text = $"加载完成，共 {branches.Count} 个分支 | 继承树已构建";
            AppendLog($"✓ 继承树构建完成，根节点：{rootNode.BranchName}，直接子节点数：{rootNode.Children.Count}");
            AppendLog($"✓ 已自动展开前两层（共展开 {expandedCount} 个一级子分支）");
            AppendLog("提示：鼠标悬停在分支名称上可查看完整描述。");
            AppendLog("========== 加载完成 ==========");
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 加载失败：{ex.Message}");
            TreeStatusText.Text = "加载失败，请查看日志";
            MessageBox.Show($"加载继承树时出错：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadTreeButton.IsEnabled = true;
            ExpandAllButton.IsEnabled = true;
            CollapseAllButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 获取裸仓库中所有分支的最新提交信息（更鲁棒的版本）
    /// </summary>
    private async Task<List<BranchInfo>> GetAllBranchesInfoAsync(string barePath)
    {
        // 第一步：安全获取分支名 + 时间 + SHA（避免 subject 里的特殊字符破坏解析）
        var shaTimeResult = await RunGitCommandAsync(barePath,
            "for-each-ref --sort=committerdate --format=\"%(refname:short)|%(objectname)|%(committerdate:iso-local)\" refs/heads/");

        if (shaTimeResult.ExitCode != 0)
            throw new Exception("无法获取分支列表：" + shaTimeResult.Output);

        var list = new List<BranchInfo>();

        foreach (var line in shaTimeResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 3) continue;

            var info = new BranchInfo
            {
                Name = parts[0].Trim(),
                TipSha = parts[1].Trim(),
                LastCommitTime = parts[2].Trim()
            };
            list.Add(info);
        }

        // 第二步：单独为每个分支获取最新的 commit message（避免 | 等特殊字符问题）
        foreach (var b in list)
        {
            var msgResult = await RunGitCommandAsync(barePath, $"log -1 --pretty=%s {b.Name}");
            b.LastCommitMessage = msgResult.ExitCode == 0 
                ? msgResult.Output.Trim() 
                : "(无法获取提交信息)";
        }

        return list;
    }

    /// <summary>
    /// 使用 merge-base + 距离计算的可靠方法构建分支继承树
    /// 这是目前最稳健的做法（尤其适合父分支持续演进的场景）
    /// </summary>
    private async Task<BranchNode> BuildInheritanceTreeAsync(string barePath, List<BranchInfo> allBranches, BranchInfo rootInfo)
    {
        // 创建所有节点的字典
        var nodeMap = allBranches.ToDictionary(
            b => b.Name,
            b => new BranchNode
            {
                BranchName = b.Name,
                Description = string.IsNullOrWhiteSpace(b.LastCommitMessage) ? "(无提交信息)" : b.LastCommitMessage,
                LastPushTime = b.LastCommitTime
            },
            StringComparer.OrdinalIgnoreCase);

        var rootNode = nodeMap[rootInfo.Name];

        // 给每个非根分支找最佳父分支
        foreach (var branch in allBranches)
        {
            if (branch.Name.Equals(rootInfo.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            string? bestParent = await FindBestParentBranchUsingMergeBaseAsync(barePath, branch, allBranches, rootInfo.Name);

            if (bestParent != null && nodeMap.TryGetValue(bestParent, out var parentNode))
            {
                parentNode.Children.Add(nodeMap[branch.Name]);
            }
            else
            {
                // 实在找不到，就挂在根模板下
                rootNode.Children.Add(nodeMap[branch.Name]);
            }
        }

        return rootNode;
    }

    /// <summary>
    /// 使用 merge-base 找到“最近分叉”的父分支（推荐算法）
    /// </summary>
    private async Task<string?> FindBestParentBranchUsingMergeBaseAsync(
        string barePath, 
        BranchInfo childBranch, 
        List<BranchInfo> allBranches,
        string rootBranchName)
    {
        string bestParent = rootBranchName;
        int minDistance = int.MaxValue;

        foreach (var candidate in allBranches)
        {
            if (candidate.Name.Equals(childBranch.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            // 计算 merge-base
            var mbResult = await RunGitCommandAsync(barePath, $"merge-base \"{candidate.Name}\" \"{childBranch.Name}\"");
            if (mbResult.ExitCode != 0 || string.IsNullOrWhiteSpace(mbResult.Output))
                continue;

            string mergeBaseSha = mbResult.Output.Trim().Split('\n')[0].Trim();

            // 计算从 merge-base 到 child 的距离（越小说明分叉越近）
            var distResult = await RunGitCommandAsync(barePath, $"rev-list --count \"{mergeBaseSha}\"..\"{childBranch.Name}\"");
            if (distResult.ExitCode != 0) continue;

            if (int.TryParse(distResult.Output.Trim(), out int distance))
            {
                if (distance < minDistance)
                {
                    minDistance = distance;
                    bestParent = candidate.Name;
                }
            }
        }

        return bestParent;
    }

    #endregion

    #region ==================== 页面3：推送操作 ====================

    // ==================== 推送操作（第三页面） ====================

    /// <summary>
    /// 1. 提交到本地仓库（含文件大小检查与 LFS 处理，与一键批量推送逻辑一致）
    /// </summary>
    private async void CommitLocalButton_Click(object sender, RoutedEventArgs e)
    {
        string branchName = CommitBranchBox.Text.Trim();
        string message = CommitMessageBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(branchName))
        {
            MessageBox.Show("请输入分支名称！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            MessageBox.Show("请输入提交描述！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string worktreePath = Path.Combine(WorktreeRootPath, branchName);

        if (!Directory.Exists(worktreePath))
        {
            MessageBox.Show($"工作树目录不存在：\n{worktreePath}\n\n请确认分支名称是否正确。", "路径错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        CommitLocalButton.IsEnabled = false;
        PushPageStatusText.Text = "正在检查并提交到本地...";
        PushPageStatusText.Foreground = Brushes.DarkOrange;

        AppendLog("");
        AppendLog($"========== 提交到本地仓库 [{branchName}] ==========");

        var worktree = new WorktreeInfo(branchName, worktreePath);
        var rejectedDetails = new List<(string BranchName, List<LargeFileEntry> Files)>();
        var outcome = await PushWorktreeToLocalBareRepoAsync(worktree, message, rejectedDetails);

        switch (outcome.Result)
        {
            case WorktreePushResult.Success:
                PushPageStatusText.Text = outcome.HasSizeWarning
                    ? "✅ 已提交（含大文件警告，请查看日志）"
                    : "✅ 已成功提交到本地仓库";
                PushPageStatusText.Foreground = outcome.HasSizeWarning ? Brushes.DarkOrange : Brushes.DarkGreen;
                PushBranchBox.Text = branchName;
                break;

            case WorktreePushResult.Skipped:
                PushPageStatusText.Text = "无变更需要提交";
                PushPageStatusText.Foreground = Brushes.DarkOrange;
                break;

            case WorktreePushResult.Rejected:
                PushPageStatusText.Text = "已拒绝推送（未启用 LFS）";
                PushPageStatusText.Foreground = Brushes.DarkRed;
                if (outcome.RejectedFiles?.Count > 0)
                    ShowRejectedFilesSummary(branchName, outcome.RejectedFiles);
                break;

            case WorktreePushResult.Failed:
                PushPageStatusText.Text = "提交失败，请查看日志";
                PushPageStatusText.Foreground = Brushes.DarkRed;
                break;
        }

        CommitLocalButton.IsEnabled = true;
    }

    /// <summary>
    /// 2. 推送单个分支到 GitHub
    /// </summary>
    private async void PushToGithubButton_Click(object sender, RoutedEventArgs e)
    {
        string branchName = PushBranchBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(branchName))
        {
            MessageBox.Show("请输入要推送的分支名称！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string worktreePath = Path.Combine(WorktreeRootPath, branchName);

        if (!Directory.Exists(worktreePath))
        {
            MessageBox.Show($"工作树目录不存在：\n{worktreePath}", "路径错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        PushToGithubButton.IsEnabled = false;
        PushPageStatusText.Text = "正在推送到 GitHub...";
        PushPageStatusText.Foreground = Brushes.DarkOrange;

        AppendLog("");
        AppendLog($"========== 推送到 GitHub [{branchName}] ==========");

        var pushResult = await RunGitCommandAsync(worktreePath, $"push origin {branchName}");

        if (pushResult.ExitCode != 0)
        {
            AppendLog("❌ 推送失败");
            AppendLog(pushResult.Output);
            PushPageStatusText.Text = "推送失败，请查看日志";
            PushPageStatusText.Foreground = Brushes.DarkRed;
        }
        else
        {
            AppendLog("✓ 成功推送到 GitHub！");
            AppendLog(pushResult.Output);
            PushPageStatusText.Text = "✅ 已成功推送到 GitHub";
            PushPageStatusText.Foreground = Brushes.DarkGreen;
        }

        PushToGithubButton.IsEnabled = true;
    }

    #endregion

    #region ==================== 页面4：一键操作 ====================

    /// <summary>
    /// 一键推送全部工作树到本地裸仓库（含文件大小检查）
    /// </summary>
    private async void PushAllWorktreesButton_Click(object sender, RoutedEventArgs e)
    {
        string commitMessage = BatchCommitMessageBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            MessageBox.Show("请输入批量提交描述！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string bareRepoPath = BareRepoPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(bareRepoPath) || !Directory.Exists(bareRepoPath))
        {
            MessageBox.Show("裸仓库路径无效，请检查配置。", "路径错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var confirm = MessageBox.Show(
            "确定要对全部工作树执行 git add . & git commit 吗？\n\n" +
            "推送前将检查每个项目的文件大小：\n" +
            "• ≥ 50 MB 的文件会警告\n" +
            "• ≥ 100 MB 的文件会检查 Git LFS 状态\n" +
            "  - 已使用 LFS 指针 → 放行\n" +
            "  - 未使用 LFS → 弹窗询问是否启用指针",
            "确认一键推送到本地裸仓库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        PushAllWorktreesButton.IsEnabled = false;
        PushAllButton.IsEnabled = false;
        OneClickPageStatusText.Text = "正在获取工作树列表...";
        OneClickPageStatusText.Foreground = Brushes.DarkOrange;

        AppendLog("");
        AppendLog("========== 一键推送全部工作树到本地裸仓库 ==========");

        var listResult = await RunGitCommandAsync(bareRepoPath, "worktree list --porcelain");
        if (listResult.ExitCode != 0)
        {
            AppendLog("❌ 获取工作树列表失败");
            AppendLog(listResult.Output);
            OneClickPageStatusText.Text = "获取工作树列表失败";
            OneClickPageStatusText.Foreground = Brushes.DarkRed;
            PushAllWorktreesButton.IsEnabled = true;
            PushAllButton.IsEnabled = true;
            return;
        }

        var worktrees = ParseWorktreeList(listResult.Output)
            .Where(w => !w.WorktreePath.Equals(bareRepoPath, StringComparison.OrdinalIgnoreCase)
                     && !w.WorktreePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                     && Directory.Exists(w.WorktreePath))
            .ToList();

        if (worktrees.Count == 0)
        {
            AppendLog("⚠ 未发现可推送的工作树");
            OneClickPageStatusText.Text = "未发现可推送的工作树";
            OneClickPageStatusText.Foreground = Brushes.DarkOrange;
            PushAllWorktreesButton.IsEnabled = true;
            PushAllButton.IsEnabled = true;
            return;
        }

        AppendLog($"共 {worktrees.Count} 个工作树待处理");

        if (!await WorktreeLfsHelper.IsGitLfsAvailableAsync(RunGitForLfsAsync, worktrees[0].WorktreePath))
        {
            AppendLog("❌ 未检测到 Git LFS，无法处理 ≥100MB 文件");
            MessageBox.Show(
                "未检测到 Git LFS（git lfs）。\n请先安装 Git LFS 后再执行批量推送。",
                "缺少 Git LFS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            OneClickPageStatusText.Text = "缺少 Git LFS";
            OneClickPageStatusText.Foreground = Brushes.DarkRed;
            PushAllWorktreesButton.IsEnabled = true;
            PushAllButton.IsEnabled = true;
            return;
        }

        int successCount = 0;
        int skippedCount = 0;
        int rejectedCount = 0;
        int failedCount = 0;
        var warnedProjects = new List<string>();
        var rejectedDetails = new List<(string BranchName, List<LargeFileEntry> Files)>();

        foreach (var worktree in worktrees)
        {
            AppendLog("");
            AppendLog($"----- 处理 [{worktree.BranchName}] -----");

            OneClickPageStatusText.Text = $"正在处理：{worktree.BranchName}...";
            OneClickPageStatusText.Foreground = Brushes.DarkOrange;

            var outcome = await PushWorktreeToLocalBareRepoAsync(worktree, commitMessage, rejectedDetails);

            switch (outcome.Result)
            {
                case WorktreePushResult.Success:
                    successCount++;
                    if (outcome.HasSizeWarning)
                        warnedProjects.Add(worktree.BranchName);
                    break;
                case WorktreePushResult.Skipped:
                    skippedCount++;
                    break;
                case WorktreePushResult.Rejected:
                    rejectedCount++;
                    break;
                case WorktreePushResult.Failed:
                    failedCount++;
                    break;
            }
        }

        AppendLog("");
        AppendLog("========== 批量推送完成 ==========");
        AppendLog($"成功：{successCount} | 无变更跳过：{skippedCount} | 拒绝（未启用LFS）：{rejectedCount} | 失败：{failedCount}");
        if (warnedProjects.Count > 0)
            AppendLog($"⚠ 含大文件警告的项目：{string.Join(", ", warnedProjects)}");

        if (rejectedCount > 0 || failedCount > 0)
        {
            OneClickPageStatusText.Text = $"完成：成功 {successCount}，拒绝 {rejectedCount}，失败 {failedCount}";
            OneClickPageStatusText.Foreground = Brushes.DarkRed;
        }
        else if (warnedProjects.Count > 0)
        {
            OneClickPageStatusText.Text = $"✅ 完成（{warnedProjects.Count} 个项目有大文件警告）";
            OneClickPageStatusText.Foreground = Brushes.DarkOrange;
        }
        else
        {
            OneClickPageStatusText.Text = $"✅ 全部完成：成功 {successCount}，跳过 {skippedCount}";
            OneClickPageStatusText.Foreground = Brushes.DarkGreen;
        }

        if (warnedProjects.Count > 0 || rejectedCount > 0)
        {
            var summary = new StringBuilder();
            if (warnedProjects.Count > 0)
                summary.AppendLine($"警告（≥50MB）：{warnedProjects.Count} 个项目（{string.Join(", ", warnedProjects)}）");

            if (rejectedDetails.Count > 0)
            {
                if (summary.Length > 0)
                    summary.AppendLine();
                summary.AppendLine($"拒绝（≥100MB 且未启用 LFS）：{rejectedCount} 个项目");
                foreach (var (branchName, files) in rejectedDetails)
                {
                    summary.AppendLine();
                    summary.AppendLine($"【{branchName}】");
                    foreach (var file in files)
                        summary.AppendLine($"  • {file.RelativePath}  ({file.FormattedSize})");
                }
            }

            MessageBox.Show(summary.ToString(), "文件大小检查摘要", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        PushAllWorktreesButton.IsEnabled = true;
        PushAllButton.IsEnabled = true;
    }

    /// <summary>
    /// 一键推送全部分支到 GitHub
    /// </summary>
    private async void PushAllButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "确定要执行 git push --all origin 吗？\n\n此操作会推送当前裸仓库中的所有本地分支到 GitHub。",
            "确认一键全推",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        PushAllButton.IsEnabled = false;
        PushAllWorktreesButton.IsEnabled = false;
        OneClickPageStatusText.Text = "正在推送全部分支到 GitHub...";
        OneClickPageStatusText.Foreground = Brushes.DarkRed;

        AppendLog("");
        AppendLog("========== 一键推送全部分支到 GitHub ==========");

        var pushAllResult = await RunGitCommandAsync(ProductionBareRepoPath, "push --all origin");

        if (pushAllResult.ExitCode != 0)
        {
            AppendLog("❌ 一键全推失败");
            AppendLog(pushAllResult.Output);
            OneClickPageStatusText.Text = "推送失败，请查看日志";
            OneClickPageStatusText.Foreground = Brushes.DarkRed;
        }
        else
        {
            AppendLog("✓ 成功推送全部分支到 GitHub！");
            AppendLog(pushAllResult.Output);
            OneClickPageStatusText.Text = "✅ 已完成一键全推";
            OneClickPageStatusText.Foreground = Brushes.DarkGreen;
        }

        PushAllButton.IsEnabled = true;
        PushAllWorktreesButton.IsEnabled = true;
    }

    #endregion

}