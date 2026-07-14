using System.Windows.Controls;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Views;

/// <summary>
/// 继承树工具窗口内容(UI-02/03):
/// 启动自动加载文件缓存(proj.tree cached=true,秒开);
/// 「重新扫描」= proj.tree refresh=true(重扫裸仓库并更新缓存)。
/// 视图只消费 CommandResult.Data,与控制台 proj.tree 同源。
/// </summary>
public partial class BranchTreeView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private bool _initialLoadDone;

    public BranchTreeView(Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;

        // Loaded 在停靠重排时会反复触发,首次加载只做一次;
        // cached=true 保证无缓存时不会在启动期触发一次全量扫描
        Loaded += async (_, _) =>
        {
            if (_initialLoadDone)
                return;
            _initialLoadDone = true;
            await LoadTreeAsync("proj.tree cached=true");
        };
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
            if (result.Success && result.Data is ProjectService.BranchNode root)
            {
                BranchTree.ItemsSource = new[] { root };

                // Q3:沿用 V1 默认展开——根 + 一级子分支 + 每支前 5 个二级子分支
                root.IsExpanded = true;
                foreach (var child in root.Children)
                {
                    child.IsExpanded = true;
                    foreach (var grandChild in child.Children.Take(5))
                        grandChild.IsExpanded = true;
                }

                TreeStatus.Text = result.Message.Split('\n')[0];
                ExpandAllButton.IsEnabled = true;
                CollapseAllButton.IsEnabled = true;
            }
            else if (result.Success)
            {
                TreeStatus.Text = result.Message.Split('\n')[0]; // 无缓存提示
            }
            else
            {
                TreeStatus.Text = "加载失败,详见控制台";
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
        if (BranchTree.ItemsSource is not IEnumerable<ProjectService.BranchNode> roots)
            return;
        foreach (var root in roots)
            ExpandRecursive(root, expanded);
    }

    private static void ExpandRecursive(ProjectService.BranchNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children)
            ExpandRecursive(child, expanded);
    }
}
