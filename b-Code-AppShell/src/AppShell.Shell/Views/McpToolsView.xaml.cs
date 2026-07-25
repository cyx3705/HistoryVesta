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
            var result = await bus.ExecuteAsync("command.list", "UI");
            if (!result.Success || result.Data is not IReadOnlyList<CommandCatalogRow> rows)
            {
                StatusText.Text = "命令集加载失败，详见控制台";
                return;
            }

            _allRows = rows.ToList();
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
        values.AddRange(_allRows.Select(row => row.Domain).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal));
        DomainFilterBox.ItemsSource = values;
        DomainFilterBox.SelectedItem = values.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? values.First(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase))
            : "全部";
    }

    private void ApplyFilter()
    {
        if (!_initialLoadDone)
            return;
        IEnumerable<CommandCatalogRow> rows = _allRows;
        var keyword = SearchBox.Text.Trim();
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

        var source = SourceFilterBox.SelectedIndex switch
        {
            1 => "framework",
            2 => "app",
            3 => "module",
            _ => null,
        };
        if (source != null)
            rows = rows.Where(row => row.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (CustomizedOnlyCheck.IsChecked == true)
            rows = rows.Where(row => row.Customized);
        if (PendingOnlyCheck.IsChecked == true)
            rows = rows.Where(row => row.OpenProposals > 0);
        if (IncidentOnlyCheck.IsChecked == true)
            rows = rows.Where(row => row.IncidentCount > 0);

        var list = rows.ToList();
        ToolList.ItemsSource = list;
        ToolList.SelectedItem = _selection.CurrentCommandName is { } selected
            ? list.FirstOrDefault(row => row.CommandName.Equals(selected, StringComparison.OrdinalIgnoreCase))
            : null;

        var hardExcluded = _allRows.Count(row => row.McpState == "hidden");
        var readonlyCount = _allRows.Count(row => row.McpState == "readonly");
        var standardCount = _allRows.Count(row => row.McpState == "standard");
        var dangerous = _allRows.Count(row => row.McpState == "dangerous");
        var modules = _allRows.Count(row => row.Source == "module");
        StatusText.Text = $"显示 {list.Count}/{_allRows.Count} 条；硬排除 {hardExcluded}，" +
                          $"readonly {readonlyCount}，standard {standardCount}，危险拒绝 {dangerous}，模块 {modules}";
    }

    private void OnFilterChanged(object sender, EventArgs e) => ApplyFilter();

    private void OnToolSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is CommandCatalogRow row)
            _selection.CurrentCommandName = row.CommandName;
    }
}
