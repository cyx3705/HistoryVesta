using AppShell.Core;
using AppShell.Services.Modules;
using System.Windows.Controls;
using AppShell.Core.Commands;

namespace AppShell.Shell.Views;

/// <summary>
/// 模块管理页(V2.1.1):已装载模块清单。
/// 数据经 module.list 消费(Data = ModuleMeta 列表);动作按钮全部经总线
/// (module.reload / module.open)。命令详情统一由命令集页面提供。
/// </summary>
public partial class ModulesView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private bool _initialLoadDone;

    public ModulesView(Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        // 0.4.4 上抛框架:内联首次加载守卫,不再依赖 App 层 ViewKit(停靠重排会反复触发 Loaded)
        Loaded += async (_, _) =>
        {
            if (_initialLoadDone)
                return;
            _initialLoadDone = true;
            await RefreshAsync();
        };
    }

    public sealed record ModuleRow(
        string ModuleName, string Version, string Mode, int CommandCount,
        string AssemblyFile, string Description);

    public sealed record CommandRow(string Name, string Summary, string Example);

    private async void OnReloadClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        RefreshButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("module.reload", "UI");
            if (!result.Success)
            {
                ClearModules("模块重载失败: " + FirstLine(result.Message));
                return;
            }
            await RefreshAsync();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnOpenDirClick(object sender, System.Windows.RoutedEventArgs e)
        => _ = _busAccessor()?.ExecuteAsync("module.open", "UI");

    // ---------------------------------------------------------------- 可选工具注册表入口

    private void OnToolScanClick(object sender, System.Windows.RoutedEventArgs e)
        => _ = _busAccessor()?.ExecuteAsync("tool.scan", "UI");

    private async void OnToolSyncClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_busAccessor() is not { } bus)
            return;
        ToolSyncButton.IsEnabled = false;
        try
        {
            await bus.ExecuteAsync("tool.sync all=true", "UI");
            await RefreshAsync();
        }
        finally
        {
            ToolSyncButton.IsEnabled = true;
        }
    }

    /// <summary>移除所选模块(tool.remove 自带确认闸口;非同步来源的模块不在溯源中会被指令拒绝)。</summary>
    private async void OnToolRemoveClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_busAccessor() is not { } bus || ModuleList.SelectedItem is not ModuleRow row)
            return;
        await bus.ExecuteAsync($"tool.remove name={CommandParser.QuoteArg(row.ModuleName)}", "UI");
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var bus = _busAccessor();
        if (bus == null)
        {
            ClearModules("命令总线尚未就绪");
            return;
        }

        UpdateToolRegistryActions(bus.Registry);
        RefreshButton.IsEnabled = false;
        ClearModules("正在加载模块目录...");
        try
        {
            var result = await ModuleCatalogReader.LoadModulesAsync(bus);
            if (result.Success && result.Snapshot is { } snapshot)
            {
                var rows = snapshot.Modules.Select(m => new ModuleRow(
                        m.ModuleName, m.Version, m.Open ? "全暴露" : "精准暴露",
                        m.CommandCount, m.AssemblyFile, m.Description))
                    .ToList();
                ModuleList.ItemsSource = rows;
                StatusText.Text = rows.Count == 0
                    ? "当前无已装载模块;把模块 DLL 放入 Modules 目录即自动装载(约 1s)"
                    : $"已装载 {rows.Count} 个模块,共 {snapshot.Modules.Sum(module => module.CommandCount)} 条模块指令";
            }
            else
            {
                ClearModules(result.Message);
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void UpdateToolRegistryActions(CommandRegistry registry)
    {
        var hasScan = registry.TryGet("tool.scan", out _);
        var hasSync = registry.TryGet("tool.sync", out _);
        var hasRemove = registry.TryGet("tool.remove", out _);

        ToolScanButton.Visibility = hasScan ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ToolSyncButton.Visibility = hasSync ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ToolRemoveButton.Visibility = hasRemove ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ToolRegistrySeparator.Visibility = hasScan || hasSync || hasRemove
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void OnModuleSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ToolRemoveButton.IsEnabled = ModuleList.SelectedItem is ModuleRow;

    private void ClearModules(string status)
    {
        ModuleList.ItemsSource = null;
        ToolRemoveButton.IsEnabled = false;
        StatusText.Text = status;
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value;
}
