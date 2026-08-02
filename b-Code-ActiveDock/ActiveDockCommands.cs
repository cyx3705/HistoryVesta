using AppShell.Core.Modules;

namespace ActiveDock;

public sealed class ActiveDockCommands
{
    /// <summary>列出活动项目。</summary>
    [ModuleCommand(Readonly = true)]
    public IReadOnlyList<DockProject> list() => ActiveDockState.Projects;

    /// <summary>置顶活动项目。</summary>
    public string pin(string name)
        => ActiveDockState.Pin(name, pinned: true) ? $"已置顶 {name}" : $"未找到项目 {name}";

    /// <summary>取消置顶活动项目。</summary>
    public string unpin(string name)
        => ActiveDockState.Pin(name, pinned: false) ? $"已取消置顶 {name}" : $"未找到项目 {name}";

    /// <summary>重新扫描活动项目。</summary>
    public async Task<IReadOnlyList<DockProject>> refresh()
        => await ActiveDockState.RefreshAsync().ConfigureAwait(false);

    /// <summary>持久化隐藏活动坞。</summary>
    public string hide()
    {
        ActiveDockState.SetHidden(true);
        return "活动坞已隐藏";
    }

    /// <summary>显示活动坞。</summary>
    public string show()
    {
        ActiveDockState.SetHidden(false);
        return "活动坞已显示";
    }
}
