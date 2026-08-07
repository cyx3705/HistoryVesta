using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Modules;
using OneHistoryStudio.Views;

namespace OneHistoryStudio.Module;

/// <summary>Registers the existing OHS business pages into the AppShell host.</summary>
public sealed class OneHistoryStudioUiModule : IUiModule, IShellUiAware
{
    private readonly List<IDisposable> _registrations = [];
    private readonly ProjectSelectionState _selection = new();
    private IShellUiRegistrar? _shellUi;

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    public void CreateUi()
    {
        if (_shellUi == null || _registrations.Count != 0)
            return;

        // AppShell 3.1.7 does not yet inject a host CommandBus into IUiModule.
        // Keep the page command boundary intact; service-contract migration will
        // replace this accessor with the host-owned bus without changing pages.
        Func<CommandBus?> busAccessor = static () => null;
        Func<string, bool> isProtected = static _ => false;

        foreach (var descriptor in CreateDescriptors(busAccessor, _selection, isProtected))
            _registrations.Add(_shellUi.RegisterToolWindow(descriptor, "OneHistoryStudio"));
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
                DefaultSide = DockSide.Center,
                DefaultRatio = 0.55,
                IsSingleton = true,
                ContentFactory = () => new OverviewView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "tree",
                Title = "继承树",
                DefaultSide = DockSide.Tab,
                DefaultTabTarget = "overview",
                DefaultRatio = 0.55,
                IsSingleton = true,
                ContentFactory = () => new BranchTreeView(busAccessor, selection),
            },
            new ToolWindowDescriptor
            {
                Id = "meta",
                Title = "Meta文件",
                DefaultSide = DockSide.Tab,
                DefaultTabTarget = "overview",
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
                DefaultSide = DockSide.Left,
                DefaultRatio = 0.26,
                IsSingleton = true,
                ContentFactory = () => new BranchHistoryView(busAccessor, selection, isProtected),
            },
        ];
    }
}
