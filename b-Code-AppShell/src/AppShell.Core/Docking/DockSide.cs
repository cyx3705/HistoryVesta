namespace AppShell.Core.Docking;

/// <summary>
/// 工具窗口相对主程序窗体的停靠方位(§4.1 W-03 五种落点)。
/// </summary>
public enum DockSide
{
    Left,
    Right,
    Top,
    Bottom,
    /// <summary>并入目标标签组(win.dock pos=tab target=...)。</summary>
    Tab,
}
