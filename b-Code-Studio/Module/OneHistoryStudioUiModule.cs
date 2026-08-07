using AppShell.Core.Docking;
using AppShell.Core.Modules;

namespace OneHistoryStudio.Module;

/// <summary>Initial UI-side module proof for the AppShell 3.1.7 host.</summary>
public sealed class OneHistoryStudioUiModule : IUiModule, IShellUiAware
{
    private IDisposable? _window;
    private IShellUiRegistrar? _shellUi;

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    public void CreateUi()
    {
        if (_shellUi == null)
            return;

        _window ??= _shellUi.RegisterToolWindow(
            CreateDescriptor(),
            "OneHistoryStudio");
    }

    public void DestroyUi()
    {
        _window?.Dispose();
        _window = null;
    }

    public static ToolWindowDescriptor CreateDescriptor() => new()
    {
        Id = "overview",
        Title = "OneHistoryStudio",
        DefaultSide = DockSide.Center,
        IsSingleton = true,
        ContentFactory = static () => new OneHistoryStudioModuleView(),
    };
}
