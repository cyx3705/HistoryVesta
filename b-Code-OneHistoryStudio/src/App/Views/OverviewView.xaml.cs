using System.Windows.Controls;
using System.Windows.Input;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Views;

/// <summary>
/// 项目总览工具窗口内容(UI-01/03):worktree 列表 + 搜索过滤。
/// 架构不变量 1:按钮动作组装指令文本经总线执行(来源 "UI"),
/// 视图只消费 CommandResult.Data;搜索框是纯视图过滤,不改状态、不发指令。
/// </summary>
public partial class OverviewView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private List<WorktreeRow> _allRows = new();
    private bool _initialLoadDone;

    public OverviewView(Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;

        // Loaded 在停靠重排时会反复触发,首次加载只做一次
        Loaded += async (_, _) =>
        {
            if (_initialLoadDone)
                return;
            _initialLoadDone = true;
            await RefreshAsync();
        };
    }

    public sealed record WorktreeRow(int Index, string BranchName, string LastCommitTime, string WorktreePath);

    private async void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e)
        => await RefreshAsync();

    private async Task RefreshAsync()
    {
        var bus = _busAccessor();
        if (bus == null)
            return;

        RefreshButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("proj.list", "UI");
            if (result.Success && result.Data is List<WorktreeInfo> list)
            {
                _allRows = list
                    .Select((w, i) => new WorktreeRow(i + 1, w.BranchName, w.LastCommitTime, w.WorktreePath))
                    .ToList();
                ApplyFilter();
            }
            else
            {
                ListStatus.Text = "加载失败,详见控制台";
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        var rows = keyword.Length == 0
            ? _allRows
            : _allRows.Where(r => r.BranchName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        WorktreeList.ItemsSource = rows;
        ListStatus.Text = keyword.Length == 0
            ? $"共 {_allRows.Count} 个工作树;双击一行在资源管理器中打开(proj.open)"
            : $"匹配 {rows.Count}/{_allRows.Count} 个工作树";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialLoadDone)
            ApplyFilter();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeRow row)
            _ = _busAccessor()?.ExecuteAsync($"proj.open name=\"{row.BranchName}\"", "UI");
    }

    private void OnOpenRootClick(object sender, System.Windows.RoutedEventArgs e)
        => _ = _busAccessor()?.ExecuteAsync("proj.open", "UI");
}
