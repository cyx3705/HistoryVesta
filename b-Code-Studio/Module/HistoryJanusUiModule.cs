using System.IO;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Modules;
using HistoryJanus.GitHub;
using HistoryJanus.Views;

namespace HistoryJanus.Module;

/// <summary>Registers the existing Janus business pages into the AppShell host.</summary>
public sealed class HistoryJanusUiModule : IUiModule, IShellUiAware, IModuleContextAware
{
    private readonly List<IDisposable> _registrations = [];
    private readonly ProjectSelectionState _selection = new();
    private IShellUiRegistrar? _shellUi;
    private IModuleContext? _context;
    private StudioBusinessComposition? _business;

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context != null)
            throw new InvalidOperationException("HistoryJanus module context is already attached.");

        _context = context;
        context.RegisterCommands(registry =>
        {
            _business = StudioBusinessCompositionFactory.Register(
                registry,
                context.Bus,
                context.Settings,
                context.Log,
                Path.Combine(context.DataDirectory, "HistoryJanus"),
                "module:HistoryJanus");
        });
    }

    public void CreateUi()
    {
        if (_shellUi == null || _registrations.Count != 0)
            return;

        Func<CommandBus?> busAccessor = () => _context?.Bus;
        Func<string, bool> isProtected = branch => _business?.Projects.IsProtected(branch) == true;
        Func<GitHubConnectionService?> gitHubAccessor = () => _business?.GitHub;
        foreach (var descriptor in CreateDescriptors(busAccessor, _selection, isProtected, gitHubAccessor))
            _registrations.Add(_shellUi.RegisterToolWindow(descriptor, "HistoryJanus"));
    }

    public void DestroyUi()
    {
        for (var index = _registrations.Count - 1; index >= 0; index--)
            _registrations[index].Dispose();
        _registrations.Clear();
    }

    public static IReadOnlyList<ToolWindowDescriptor> CreateDescriptors(
        Func<CommandBus?> busAccessor,
        ProjectSelectionState selection,
        Func<string, bool> isProtected,
        Func<GitHubConnectionService?> gitHubAccessor)
    {
        ArgumentNullException.ThrowIfNull(busAccessor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(isProtected);
        ArgumentNullException.ThrowIfNull(gitHubAccessor);

        return
        [
            new ToolWindowDescriptor
            {
                Id = "overview",
                Title = "项目总览",
                // 中央文档标签组内的工具页（Anchorable），不是 LayoutDocument 主窗口。
                DefaultSide = DockSide.Tab,
                DefaultTabTarget = StandardWindowIds.Mcp,
                IsSingleton = true,
                ContentFactory = () => new OverviewView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "projops",
                Title = "项目操作",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.28,
                IsSingleton = true,
                // 底部同一行分段切换 Git 文件规则 / 分支历史 / GitHub，三者都内嵌于本页；
                // 没有独立 github 窗口。
                ContentFactory = () => new ProjectOperationsView(
                    busAccessor,
                    selection,
                    isProtected,
                    gitHubAccessor),
            },
        ];
    }
}
