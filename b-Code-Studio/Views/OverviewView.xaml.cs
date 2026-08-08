using System.Windows.Controls;
using System.Windows.Input;
using AppShell.Core.Commands;
using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>项目工作树列表、当前项目选择与批量提交推送。</summary>
public partial class OverviewView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;
    private List<WorktreeRow> _allRows = [];
    private bool _initialLoadDone;
    private bool _suppressSelection;

    public OverviewView(Func<CommandBus?> busAccessor, ProjectSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public sealed record WorktreeRow(int Index, string BranchName, string LastCommitTime, string WorktreePath);

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _selection.Changed -= OnSharedSelectionChanged;
        _selection.Changed += OnSharedSelectionChanged;
        if (!_initialLoadDone)
        {
            _initialLoadDone = true;
            await RefreshAsync();
        }
        else
        {
            ApplySharedSelection();
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        => _selection.Changed -= OnSharedSelectionChanged;

    private async void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e)
        => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busAccessor() is not { } bus)
            return;
        RefreshButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("proj.list", "UI");
            if (!result.Success || !ModuleResultData.TryRead(result.Data, out List<WorktreeInfo>? list))
            {
                StatusText.Text = "项目加载失败，详见控制台";
                return;
            }

            _allRows = list.Select((item, index) => new WorktreeRow(
                index + 1, item.BranchName, item.LastCommitTime, item.WorktreePath)).ToList();
            if (_selection.CurrentProjectName is { } current
                && !_allRows.Any(row => row.BranchName.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                _selection.CurrentProjectName = null;
            }
            ApplyFilter();
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
            : _allRows.Where(row => row.BranchName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        WorktreeList.ItemsSource = rows;
        ApplySharedSelection();
        StatusText.Text = keyword.Length == 0
            ? $"共 {_allRows.Count} 个工作树；双击项目在资源管理器中打开"
            : $"匹配 {rows.Count}/{_allRows.Count} 个工作树";
    }

    private void ApplySharedSelection()
    {
        var rows = WorktreeList.ItemsSource as IEnumerable<WorktreeRow> ?? [];
        _suppressSelection = true;
        WorktreeList.SelectedItem = _selection.CurrentProjectName is { } current
            ? rows.FirstOrDefault(row => row.BranchName.Equals(current, StringComparison.OrdinalIgnoreCase))
            : null;
        _suppressSelection = false;
        if (WorktreeList.SelectedItem != null)
            WorktreeList.ScrollIntoView(WorktreeList.SelectedItem);
    }

    private void OnSharedSelectionChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(ApplySharedSelection);

    private void OnProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelection && WorktreeList.SelectedItem is WorktreeRow row)
            _selection.CurrentProjectName = row.BranchName;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialLoadDone)
            ApplyFilter();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeRow row)
            _ = _busAccessor()?.ExecuteAsync(
                $"proj.open name={CommandParser.QuoteArg(row.BranchName)}", "UI");
    }

    private void OnOpenRootClick(object sender, System.Windows.RoutedEventArgs e)
        => _ = _busAccessor()?.ExecuteAsync("proj.open", "UI");

}
