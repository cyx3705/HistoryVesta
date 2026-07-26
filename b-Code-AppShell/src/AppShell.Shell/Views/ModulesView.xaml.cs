using AppShell.Core;
using AppShell.Services.Modules;
using System.Windows.Controls;
using AppShell.Core.Commands;

namespace AppShell.Shell.Views;

/// <summary>
/// 模块管理页(V2.1.1):已装载模块清单 + 选中模块的注册指令明细。
/// 数据经 module.list 消费(Data = ModuleMeta 列表);动作按钮全部经总线
/// (module.reload / module.open);指令明细为注册表只读展示,双击行发 help。
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

    private async void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e)
        => await RefreshAsync();

    private async void OnReloadClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        await bus.ExecuteAsync("module.reload", "UI");
        await RefreshAsync();
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
            return;

        UpdateToolRegistryActions(bus.Registry);
        RefreshButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("module.list", "UI");
            if (result.Success && result.Data is IReadOnlyList<ModuleMeta> modules)
            {
                ModuleList.ItemsSource = modules.Select(m => new ModuleRow(
                        m.ModuleName, m.Version, m.Open ? "全暴露" : "精准暴露",
                        m.CommandCount, m.AssemblyFile, m.Description))
                    .ToList();
                StatusText.Text = modules.Count == 0
                    ? "当前无已装载模块;把模块 DLL 放入 Modules 目录即自动装载(约 1s)"
                    : $"已装载 {modules.Count} 个模块,共 {modules.Sum(m => m.CommandCount)} 条模块指令";
            }
            else if (result.Success)
            {
                ModuleList.ItemsSource = null;
                StatusText.Text = result.Message.Split('\n')[0];
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

    private void OnModuleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ModuleList.SelectedItem is not ModuleRow row)
        {
            CommandList.ItemsSource = null;
            CommandsTitle.Text = "(选中模块查看其注册的指令)";
            ToolRemoveButton.IsEnabled = false;
            return;
        }

        ToolRemoveButton.IsEnabled = true;

        // 注册表只读展示:该模块域下的全部指令(域名 = 模块名)
        var registry = _busAccessor()?.Registry;
        var commands = registry?.All()
            .Where(c => c.Name.StartsWith(row.ModuleName + ".", StringComparison.OrdinalIgnoreCase))
            .Select(c => new CommandRow(c.Name, c.Summary, c.Example ?? ""))
            .ToList() ?? new List<CommandRow>();

        CommandList.ItemsSource = commands;
        CommandsTitle.Text = $"{row.ModuleName} 注册的指令({commands.Count} 条):";
    }

    private void OnCommandDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CommandList.SelectedItem is CommandRow row)
            _ = _busAccessor()?.ExecuteAsync($"help {row.Name}", "UI");
    }
}
