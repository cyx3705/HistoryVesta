using System.IO;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Modules;
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

        foreach (var descriptor in CreateDescriptors(busAccessor, _selection, isProtected))
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
        Func<string, bool> isProtected)
    {
        ArgumentNullException.ThrowIfNull(busAccessor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(isProtected);

        return
        [
            new ToolWindowDescriptor
            {
                Id = "overview",
                Title = "项目总览",
                DefaultSide = DockSide.Left,
                DefaultRatio = 0.20,
                IsSingleton = true,
                ContentFactory = () => new OverviewView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "tree",
                Title = "继承树",
                DefaultSide = DockSide.Center,
                DefaultRatio = 0.55,
                IsSingleton = true,
                ContentFactory = () => new BranchTreeView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "meta",
                Title = "Meta文件",
                DefaultSide = DockSide.Center,
                DefaultRatio = 0.55,
                IsSingleton = true,
                ContentFactory = () => new MetaView(busAccessor),
            },
            new ToolWindowDescriptor
            {
                Id = "projops",
                Title = "项目操作",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.28,
                IsSingleton = true,
                ContentFactory = () => new ProjectOperationsView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "history",
                Title = "分支历史",
                DefaultSide = DockSide.Center,
                DefaultRatio = 0.55,
                IsSingleton = true,
                ContentFactory = () => new BranchHistoryView(busAccessor, selection, isProtected),
            },
        ];
    }
}
