using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Modules;

namespace GitHubConnection;

public sealed class GitHubConnectionUiModule : IUiModule, IShellUiAware
{
    private IDisposable? _window;
    private IShellUiRegistrar? _shellUi;

    IShellUiRegistrar IShellUiAware.ShellUi { set => _shellUi = value; }

    public void CreateUi()
    {
        // 无窗服务宿主不注入注册器。此时没有可停靠的宿主窗口，弃权而不是空引用。
        if (_shellUi == null)
            return;
        _window ??= _shellUi.RegisterToolWindow(CreateDescriptor(), "GitHubConnection");
    }

    public void DestroyUi()
    {
        _window?.Dispose();
        _window = null;
    }

    public static ToolWindowDescriptor CreateDescriptor() => new()
    {
        Id = "github.account",
        Title = "GitHub 连接",
        DefaultSide = DockSide.Right,
        DefaultRatio = 0.38,
        IsSingleton = true,
        ContentFactory = static () => new Views.GitHubConnectionView(
            GitHubRuntime.CreateService()),
    };
}
