using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>
/// 继承树工具窗口内容：
/// 启动自动加载文件缓存(proj.tree cached=true,秒开);
/// 「重新扫描」= proj.tree refresh=true(重扫裸仓库并更新缓存)。
/// 视图只消费 CommandResult.Data,与控制台 proj.tree 同源。
/// </summary>
public partial class BranchTreeView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;

    public BranchTreeView(Func<CommandBus?> busAccessor, ProjectSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        // cached=true 保证无缓存时不会在启动期触发一次全量扫描
        ViewKit.RunOnceOnLoaded(this, () => LoadTreeAsync("proj.tree cached=true"));
    }

    private async void OnRescanClick(object sender, System.Windows.RoutedEventArgs e)
        => await LoadTreeAsync("proj.tree refresh=true");

    private async Task LoadTreeAsync(string command)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;

        RescanButton.IsEnabled = false;
        TreeStatus.Text = "加载中(重扫时进度见控制台)...";
        try
        {
            var result = await bus.ExecuteAsync(command, "UI");
            var validationError = "";
            if (result.Success && ModuleResultData.TryRead(result.Data, out BranchTreeNode? root)
                && root.TryValidate(out var nodeCount, out validationError))
            {
                var displayRoot = BranchTreeItem.FromContract(root);
                BranchTree.ItemsSource = new[] { displayRoot };

                // 默认展开根、一级子分支，以及每支前 5 个二级子分支。
                displayRoot.IsExpanded = true;
                foreach (var child in displayRoot.Children)
                {
                    child.IsExpanded = true;
                    foreach (var grandChild in child.Children.Take(5))
                        grandChild.IsExpanded = true;
                }

                TreeStatus.Text = $"{result.Message.Split('\n')[0]} ({nodeCount} 个节点)";
                ExpandAllButton.IsEnabled = true;
                CollapseAllButton.IsEnabled = true;
            }
            else if (result.Success && ModuleResultData.TryRead(result.Data, out BranchTreeNode? ignoredRoot))
            {
                ClearTree($"继承树数据无效: {validationError}");
            }
            else if (result.Success)
            {
                ClearTree(result.Data == null
                    ? result.Message.Split('\n')[0]
                    : $"继承树数据类型无法识别: {result.Data.GetType().Name}");
            }
            else
            {
                ClearTree("加载失败: " + result.Message.Split('\n')[0]);
            }
        }
        finally
        {
            RescanButton.IsEnabled = true;
        }
    }

    private void OnExpandAllClick(object sender, System.Windows.RoutedEventArgs e)
        => SetExpanded(true);

    private void OnCollapseAllClick(object sender, System.Windows.RoutedEventArgs e)
        => SetExpanded(false);

    private void SetExpanded(bool expanded)
    {
        if (BranchTree.ItemsSource is not IEnumerable<BranchTreeItem> roots)
            return;
        foreach (var root in roots)
            ExpandRecursive(root, expanded);
    }

    private static void ExpandRecursive(BranchTreeItem node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children)
            ExpandRecursive(child, expanded);
    }

    private void OnBranchSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BranchTreeItem node)
            _selection.CurrentProjectName = node.BranchName;
    }

    private void OnTreePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void OnViewHistoryClick(object sender, RoutedEventArgs e)
        => await ShowHistoryAsync("已在分支历史窗口显示所选项目");

    private async void OnChooseRollbackClick(object sender, RoutedEventArgs e)
        => await ShowHistoryAsync("请在分支历史表选择目标节点，再执行恢复或硬重置");

    private async Task ShowHistoryAsync(string status)
    {
        if (BranchTree.SelectedItem is not BranchTreeItem node || _busAccessor() is not { } bus)
            return;
        _selection.CurrentProjectName = node.BranchName;
        var result = await bus.ExecuteAsync("win.show name=history", "UI");
        TreeStatus.Text = result.Success ? status : ViewKit.ResultSummary(result);
    }

    private async void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (BranchTree.SelectedItem is not BranchTreeItem node || _busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync(
            $"proj.open name={CommandParser.QuoteArg(node.BranchName)}", "UI");
        TreeStatus.Text = ViewKit.ResultSummary(result);
    }

    private void OnCopyBranchClick(object sender, RoutedEventArgs e)
    {
        if (BranchTree.SelectedItem is not BranchTreeItem node)
            return;
        Clipboard.SetText(node.BranchName);
        TreeStatus.Text = $"已复制分支名 {node.BranchName}";
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match)
                return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void ClearTree(string status)
    {
        BranchTree.ItemsSource = null;
        ExpandAllButton.IsEnabled = false;
        CollapseAllButton.IsEnabled = false;
        TreeStatus.Text = status;
    }
}
