using System.Windows.Controls;
using System.Windows.Input;
using AppShell.Core.Commands;
using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>
/// Meta 文件工具窗口：汇总各项目根下 z/Z 开头的一级元文件夹。
/// 架构不变量 1:刷新/双击组装指令经总线执行(来源 "UI");
/// 搜索框是纯视图过滤,不改状态、不发指令。
/// </summary>
public partial class MetaView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private List<MetaRow> _allRows = new();
    private bool _initialLoadDone;

    public MetaView(Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;

        Loaded += async (_, _) =>
        {
            if (_initialLoadDone)
                return;
            _initialLoadDone = true;
            await RefreshAsync();
        };
    }

    public sealed record MetaRow(string ProjectName, string MetaName, string LastWriteTime, string FullPath);

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
            var result = await bus.ExecuteAsync("proj.metalist", "UI");
            if (result.Success && ModuleResultData.TryRead(result.Data, out List<MetaFolderInfo>? list))
            {
                _allRows = list
                    .Select(m => new MetaRow(m.ProjectName, m.MetaName, m.LastWriteTime, m.FullPath))
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
            : _allRows.Where(r =>
                r.ProjectName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.MetaName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        MetaList.ItemsSource = rows;
        ListStatus.Text = keyword.Length == 0
            ? $"共 {_allRows.Count} 个元文件夹;双击一行在资源管理器中打开(proj.metaopen)"
            : $"匹配 {rows.Count}/{_allRows.Count} 个元文件夹";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialLoadDone)
            ApplyFilter();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MetaList.SelectedItem is MetaRow row)
        {
            _ = _busAccessor()?.ExecuteAsync(
                $"proj.metaopen name=\"{row.ProjectName}\" meta=\"{row.MetaName}\"", "UI");
        }
    }
}
