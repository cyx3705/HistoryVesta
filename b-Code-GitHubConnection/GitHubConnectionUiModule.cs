using AppShell.Core.Docking;
using AppShell.Core.Modules;

namespace GitHubConnection;

public sealed class GitHubConnectionUiModule : IUiModule, IShellUiAware
{
    private IDisposable? _window;

    public IShellUiRegistrar ShellUi { private get; set; } = null!;

    public void CreateUi()
    {
        _window ??= ShellUi.RegisterToolWindow(CreateDescriptor(), "GitHubConnection");
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
