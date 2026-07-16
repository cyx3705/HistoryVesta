using System.Windows.Controls;
using AppShell.Core.Commands;
using OneHistoryStudio.Mcp;

namespace OneHistoryStudio.Views;

/// <summary>
/// MCP 工具管理页(V2.1.1):观察全部工具形态,查看/编辑/保存提示词。
/// 架构不变量 1:数据经 mcp.schema 消费,保存/恢复经 mcp.desc 指令(控制台可见回显);
/// 提示词覆盖在导出读取侧合成,保存后客户端下次 tools/list 即生效。
/// </summary>
public partial class McpToolsView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private List<ToolRow> _allRows = new();
    private bool _initialLoadDone;

    public McpToolsView(Func<CommandBus?> busAccessor)
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

    public sealed record ToolRow(
        string ToolName, string CommandName, string Tags, string FirstLine,
        string Description, string DefaultDescription, bool Customized);

    private async void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e)
        => await RefreshAsync();

    private void OnStatusClick(object sender, System.Windows.RoutedEventArgs e)
        => _ = _busAccessor()?.ExecuteAsync("mcp.status", "UI");

    private async Task RefreshAsync()
    {
        var bus = _busAccessor();
        if (bus == null)
            return;

        RefreshButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("mcp.schema", "UI");
            if (result.Success && result.Data is IReadOnlyList<McpToolInfo> tools)
            {
                _allRows = tools.Select(t => new ToolRow(
                        t.ToolName,
                        t.CommandName,
                        (t.Dangerous ? "⚠危险拒绝 " : "") + (t.Customized ? "✎已自定义" : ""),
                        t.Description.Split('\n')[0],
                        t.Description,
                        t.DefaultDescription,
                        t.Customized))
                    .ToList();
                ApplyFilter();
            }
            else
            {
                StatusText.Text = "加载失败,详见控制台";
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
            : _allRows.Where(r => r.ToolName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                  || r.CommandName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                  || r.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        ToolList.ItemsSource = rows;
        StatusText.Text = keyword.Length == 0
            ? $"共 {_allRows.Count} 个 MCP 工具({_allRows.Count(r => r.Customized)} 个已自定义提示词);保存后客户端下次 tools/list 即生效"
            : $"匹配 {rows.Count}/{_allRows.Count} 个工具";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialLoadDone)
            ApplyFilter();
    }

    private void OnToolSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolRow row)
        {
            DetailTitle.Text = "(选中左侧工具查看/编辑提示词)";
            DefaultDescBox.Text = "";
            DescBox.Text = "";
            DescBox.IsEnabled = SaveButton.IsEnabled = ResetButton.IsEnabled = false;
            return;
        }

        DetailTitle.Text = $"{row.ToolName} ← {row.CommandName}{(row.Tags.Length > 0 ? $"  [{row.Tags.Trim()}]" : "")}";
        DefaultDescBox.Text = row.DefaultDescription;
        DescBox.Text = row.Description;
        DescBox.IsEnabled = SaveButton.IsEnabled = true;
        ResetButton.IsEnabled = row.Customized;
    }

    private async void OnSaveClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolRow row)
            return;
        var bus = _busAccessor();
        if (bus == null)
            return;

        var text = DescBox.Text.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = "提示词不能为空(要恢复默认请点「恢复默认」)";
            return;
        }

        var command = $"mcp.desc name={row.CommandName} text={CommandParser.QuoteArg(text)}";
        var result = await bus.ExecuteAsync(command, "UI");
        StatusText.Text = result.Success ? $"已保存: {row.CommandName}" : "保存失败,详见控制台";
        if (result.Success)
            await ReselectAsync(row.CommandName);
    }

    private async void OnResetClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolRow row)
            return;
        var bus = _busAccessor();
        if (bus == null)
            return;

        var result = await bus.ExecuteAsync($"mcp.desc name={row.CommandName} reset=true", "UI");
        StatusText.Text = result.Success ? $"已恢复默认: {row.CommandName}" : "恢复失败,详见控制台";
        if (result.Success)
            await ReselectAsync(row.CommandName);
    }

    private async Task ReselectAsync(string commandName)
    {
        await RefreshAsync();
        var again = (ToolList.ItemsSource as IEnumerable<ToolRow>)?
            .FirstOrDefault(r => r.CommandName.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (again != null)
            ToolList.SelectedItem = again;
    }
}
