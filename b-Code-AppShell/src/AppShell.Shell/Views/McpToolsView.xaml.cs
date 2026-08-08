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
    private CommandCatalogSession? _catalogSession;
    private bool _initialLoadDone;
    private bool _updatingList;

    public McpToolsView(Func<CommandBus?> busAccessor, CommandSelectionState selection)
        : this(busAccessor, selection, null)
    {
    }

    internal McpToolsView(
        Func<CommandBus?> busAccessor,
        CommandSelectionState selection,
        CommandCatalogSession? catalogSession)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        _catalogSession = catalogSession;
        Loaded += async (_, _) =>
        {
            var session = EnsureSession();
            if (session == null)
                return;
            session.Changed -= OnCatalogChanged;
            session.Changed += OnCatalogChanged;
            if (!_initialLoadDone)
            {
                _initialLoadDone = true;
                await RefreshAsync();
            }
        };
        Unloaded += (_, _) =>
        {
            if (_catalogSession != null)
                _catalogSession.Changed -= OnCatalogChanged;
        };
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync(force: true);

    private void OnStatusClick(object sender, RoutedEventArgs e)
        => _ = _busAccessor()?.ExecuteAsync("mcp.status", "UI");

    private CommandCatalogSession? EnsureSession()
    {
        if (_catalogSession != null)
            return _catalogSession;
        if (_busAccessor() is not { } bus)
            return null;
        _catalogSession = new CommandCatalogSession(bus, _selection);
        return _catalogSession;
    }

    private void OnCatalogChanged(object? sender, CommandCatalogChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            HandleCatalogChanged(e);
            return;
        }

        Dispatcher.BeginInvoke(() => HandleCatalogChanged(e));
    }

    private void HandleCatalogChanged(CommandCatalogChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (e.Kind == CommandCatalogChangeKind.Invalidated)
        {
            _ = RefreshAsync();
            return;
        }
        RefreshDomainFilter();
        ApplyFilter();
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_busAccessor() is not { } bus || EnsureSession() is not { } session)
            return;

        RefreshButton.IsEnabled = false;
        try
        {
            if (!await session.RefreshAsync(force))
            {
                StatusText.Text = "命令集加载失败，详见控制台";
                return;
            }

            var mcpEnabled = session.ContainsCommand("mcp.status");
            var governanceEnabled = session.ContainsCommand("prompt.get");
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

            if (_selection.CurrentCommandName is { } current
                && !session.AllRows.Any(row => row.CommandName.Equals(current, StringComparison.OrdinalIgnoreCase)))
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
        values.AddRange(_catalogSession?.Domains ?? []);
        DomainFilterBox.ItemsSource = values;
        DomainFilterBox.SelectedItem = values.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? values.First(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase))
            : "全部";
    }

    private void ApplyFilter()
    {
        if (!_initialLoadDone || _catalogSession == null)
            return;
        _catalogSession.SetFilter(new CommandCatalogFilter(
            _catalogSession.CurrentFilter.Query,
            DomainFilterBox.SelectedItem?.ToString() ?? "全部",
            McpFilterBox.SelectedIndex,
            CustomizedOnlyCheck.IsChecked == true,
            PendingOnlyCheck.IsChecked == true,
            IncidentOnlyCheck.IsChecked == true));

        var list = _catalogSession.VisibleRows;
        var selectedName = _catalogSession.SelectedCommandName;
        _updatingList = true;
        try
        {
            ToolList.ItemsSource = list;
            ToolList.SelectedItem = selectedName == null
                ? null
                : list.FirstOrDefault(row => row.CommandName.Equals(
                    selectedName,
                    StringComparison.OrdinalIgnoreCase));
            if (ToolList.SelectedItem != null)
                ToolList.ScrollIntoView(ToolList.SelectedItem);
        }
        finally
        {
            _updatingList = false;
        }

        var allRows = _catalogSession.AllRows;
        var hardExcluded = allRows.Count(row => row.McpState == "hidden");
        var readonlyCount = allRows.Count(row => row.McpState == "readonly");
        var standardCount = allRows.Count(row => row.McpState == "standard");
        var dangerous = allRows.Count(row => row.McpState == "dangerous");
        var modules = allRows.Count(row => row.Source == "module");
        var serviceState = McpStatusButton.IsEnabled ? "MCP 已装配" : "MCP 未启用";
        StatusText.Text = $"显示 {list.Count}/{allRows.Count} 条；{serviceState}；硬排除 {hardExcluded}，" +
                          $"readonly {readonlyCount}，standard {standardCount}，危险拒绝 {dangerous}，模块 {modules}";
    }

    private void OnFilterChanged(object sender, EventArgs e) => ApplyFilter();

    private void OnToolSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingList && ToolList.SelectedItem is CommandCatalogRow row)
            _catalogSession?.Select(row.CommandName);
    }
}
