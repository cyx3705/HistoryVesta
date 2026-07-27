using AppShell.Core.Docking;

namespace AppShell.Core.Modules;

/// <summary>模块向宿主注册内嵌界面的唯一门面。</summary>
public interface IShellUiRegistrar
{
    bool IsUiThread { get; }

    void Invoke(Action action);

    IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner);

    void UnregisterToolWindow(string id);

    void UnregisterOwner(string owner);
}
