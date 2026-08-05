using AppShell.Core.Modules;

namespace ActiveDock;

public sealed class ActiveDockCommands
{
    /// <summary>返回 OHS 项目入口的当前注册状态。</summary>
    [ModuleCommand(Readonly = true)]
    public ExplorerEntryStatus explorer()
        => new(ExplorerNamespaceRegistration.IsRegistered(), ActiveDockState.WorktreeRoot);

    /// <summary>把当前工作树注册到资源管理器左侧。</summary>
    public string explorerRegister()
        => ExplorerNamespaceRegistration.RegisterOrUpdate(ActiveDockState.WorktreeRoot).Message;

    /// <summary>移除 ActiveDock 注册的资源管理器入口。</summary>
    public string explorerRemove()
        => ExplorerNamespaceRegistration.RemoveRegistration().Message;

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

    /// <summary>打开 OHS 主界面；已在运行则唤到前台。</summary>
    public string open()
        => OhsLauncher.Open() ? "已唤起正在运行的 OHS 主界面" : "已启动 OHS 主界面";

    /// <summary>列出使用记录与当前权重。</summary>
    [ModuleCommand(Readonly = true)]
    public IReadOnlyList<DockUsageRow> usage()
        => ActiveDockState.Projects
            .Select(item => new DockUsageRow(
                item.Name,
                Math.Round(item.Weight, 3),
                Math.Round(item.Clicks, 3),
                item.LastOpened,
                item.Pinned,
                item.Excluded))
            .ToList();

    /// <summary>清除使用记录；不给 name 表示全部清除。</summary>
    public string forget(string? name = null)
    {
        ActiveDockState.Forget(name);
        return string.IsNullOrWhiteSpace(name) ? "已清除全部使用记录" : $"已清除 {name} 的使用记录";
    }

    /// <summary>手动排除某个项目，不再收录进活动坞。</summary>
    public string exclude(string name)
    {
        ActiveDockState.Exclude(name, excluded: true);
        return $"已排除 {name}";
    }

    /// <summary>恢复收录某个被排除的项目。</summary>
    public string include(string name)
    {
        ActiveDockState.Exclude(name, excluded: false);
        return $"已恢复收录 {name}";
    }

    /// <summary>查看或设置收录策略：最低条数、最高条数与半衰期(天)。</summary>
    public string policy(int? min = null, int? max = null, double? halflife = null)
    {
        if (min != null || max != null || halflife != null)
            ActiveDockState.SetPolicy(min, max, halflife);
        var current = ActiveDockState.Policy;
        return $"收录策略: 最低 {current.MinItems} 条, 最高 {current.MaxItems} 条, 半衰期 {current.HalfLifeDays} 天";
    }
}

/// <summary>使用记录投影，供管理页面与 dock.usage 复用。</summary>
public sealed record DockUsageRow(
    string Name,
    double Weight,
    double Clicks,
    DateTimeOffset? LastOpened,
    bool Pinned,
    bool Excluded);

public sealed record ExplorerEntryStatus(bool Registered, string Path);
