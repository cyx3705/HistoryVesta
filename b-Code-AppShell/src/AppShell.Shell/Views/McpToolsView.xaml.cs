using AppShell.Core;
using AppShell.Core.Mcp;
using AppShell.Shell.Mcp;
using System.Windows;
using System.Windows.Controls;
using AppShell.Core.Commands;

namespace AppShell.Shell.Views;

/// <summary>全部注册指令及其 MCP 投影的可筛选列表。</summary>
public partial class McpToolsView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly CommandSelectionState _selection;
    private List<CommandCatalogRow> _allRows = [];
    private IReadOnlyList<string> _domains = [];
    private string _consoleQuery = "";
    private bool _initialLoadDone;
    private bool _registryRefreshPending;
    private CommandRegistry? _observedRegistry;

    public McpToolsView(Func<CommandBus?> busAccessor, CommandSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        Loaded += async (_, _) =>
        {
            ObserveRegistry();
            if (!_initialLoadDone)
            {
                _initialLoadDone = true;
                await RefreshAsync();
            }
        };
        Unloaded += (_, _) => StopObservingRegistry();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnStatusClick(object sender, RoutedEventArgs e)
        => _ = _busAccessor()?.ExecuteAsync("mcp.status", "UI");

    private void ObserveRegistry()
    {
        var registry = _busAccessor()?.Registry;
        if (ReferenceEquals(registry, _observedRegistry))
            return;
        StopObservingRegistry();
        _observedRegistry = registry;
        if (_observedRegistry != null)
            _observedRegistry.Changed += OnRegistryChanged;
    }

    private void StopObservingRegistry()
    {
        if (_observedRegistry != null)
            _observedRegistry.Changed -= OnRegistryChanged;
        _observedRegistry = null;
    }

    private void OnRegistryChanged()
    {
        if (_registryRefreshPending)
            return;
        _registryRefreshPending = true;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(200);
            _registryRefreshPending = false;
            if (IsLoaded)
                await RefreshAsync();
        });
    }

    private async Task RefreshAsync()
    {
        if (_busAccessor() is not { } bus)
            return;

        RefreshButton.IsEnabled = false;
        try
        {
            var mcpEnabled = bus.Registry.TryGet("mcp.status", out _);
            var governanceEnabled = bus.Registry.TryGet("prompt.get", out _);
            McpStatusButton.IsEnabled = mcpEnabled;
            CustomizedOnlyCheck.IsEnabled = governanceEnabled;
            PendingOnlyCheck.IsEnabled = governanceEnabled;
            IncidentOnlyCheck.IsEnabled = governanceEnabled;
            if (!governanceEnabled)
            {
                CustomizedOnlyCheck.IsChecked = false;
                PendingOnlyCheck.IsChecked = false;
                IncidentOnlyCheck.IsChecked = false;
            }

            var result = await bus.ExecuteAsync("command.list", "UI");
            if (!result.Success
                || !CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(result.Data, out var rows))
            {
                StatusText.Text = "命令集加载失败，详见控制台";
                return;
            }

            _allRows = rows.ToList();
            var domainsResult = await bus.ExecuteAsync("command.domains", "UI");
            if (!domainsResult.Success
                || !CommandResultData.TryRead<IReadOnlyList<CommandDomainInfo>>(
                    domainsResult.Data, out var domainRows))
            {
                StatusText.Text = "命令域加载失败，详见控制台";
                return;
            }
            _domains = domainRows.Select(row => row.Domain).ToList();
            if (_selection.CurrentCommandName is { } current
                && !_allRows.Any(row => row.CommandName.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                _selection.CurrentCommandName = null;
            }

            RefreshDomainFilter();
            ApplyFilter();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void RefreshDomainFilter()
    {
        var selected = DomainFilterBox.SelectedItem?.ToString() ?? "全部";
        var values = new List<string> { "全部" };
        values.AddRange(_domains);
        DomainFilterBox.ItemsSource = values;
        DomainFilterBox.SelectedItem = values.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? values.First(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase))
            : "全部";
    }

    /// <summary>由控制台输入驱动命令集检索，不持有第二个文本输入状态。</summary>
    internal void SetConsoleQuery(string query)
    {
        _consoleQuery = query ?? "";
        ApplyFilter();
    }

    /// <summary>在当前可见结果中循环选择，选择继续通过共享状态驱动指令详情。</summary>
    internal bool MoveConsoleSelection(int direction)
    {
        var count = ToolList.Items.Count;
        if (count == 0)
            return false;

        var index = ToolList.SelectedIndex;
        if (index < 0)
            index = direction < 0 ? 0 : -1;
        index = (index + direction) % count;
        if (index < 0)
            index += count;

        ToolList.SelectedIndex = index;
        ToolList.ScrollIntoView(ToolList.SelectedItem);
        return true;
    }

    internal string? SelectedCommandName
        => (ToolList.SelectedItem as CommandCatalogRow)?.CommandName;

    private void ApplyFilter()
    {
        if (!_initialLoadDone)
            return;
        IEnumerable<CommandCatalogRow> rows = _allRows;
        var keyword = _consoleQuery.Trim();
        if (keyword.Length > 0)
        {
            rows = rows.Where(row =>
                row.CommandName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (row.Example?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var domain = DomainFilterBox.SelectedItem?.ToString();
        if (!string.IsNullOrWhiteSpace(domain) && domain != "全部")
            rows = rows.Where(row => row.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));

        rows = McpFilterBox.SelectedIndex switch
        {
            1 => rows.Where(row => row.PolicyVisible),
            2 => rows.Where(row => !row.PolicyVisible),
            3 => rows.Where(row => row.McpState == "hidden"),
            4 => rows.Where(row => row.McpState == "dangerous"),
            _ => rows,
        };

        if (CustomizedOnlyCheck.IsChecked == true)
            rows = rows.Where(row => row.Customized);
        if (PendingOnlyCheck.IsChecked == true)
            rows = rows.Where(row => row.OpenProposals > 0);
        if (IncidentOnlyCheck.IsChecked == true)
            rows = rows.Where(row => row.IncidentCount > 0);

        var selectedName = (ToolList.SelectedItem as CommandCatalogRow)?.CommandName
                           ?? _selection.CurrentCommandName;
        var list = rows.ToList();
        ToolList.ItemsSource = list;
        var selected = selectedName == null
            ? null
            : list.FirstOrDefault(row => row.CommandName.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        if (selected == null && keyword.Length > 0)
            selected = list.FirstOrDefault();
        ToolList.SelectedItem = selected;
        _selection.CurrentCommandName = selected?.CommandName;

        var hardExcluded = _allRows.Count(row => row.McpState == "hidden");
        var readonlyCount = _allRows.Count(row => row.McpState == "readonly");
        var standardCount = _allRows.Count(row => row.McpState == "standard");
        var dangerous = _allRows.Count(row => row.McpState == "dangerous");
        var modules = _allRows.Count(row => row.Source == "module");
        var serviceState = McpStatusButton.IsEnabled ? "MCP 已装配" : "MCP 未启用";
        StatusText.Text = $"显示 {list.Count}/{_allRows.Count} 条；{serviceState}；硬排除 {hardExcluded}，" +
                          $"readonly {readonlyCount}，standard {standardCount}，危险拒绝 {dangerous}，模块 {modules}";
    }

    private void OnFilterChanged(object sender, EventArgs e) => ApplyFilter();

    private void OnToolSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is CommandCatalogRow row)
            _selection.CurrentCommandName = row.CommandName;
    }
}
