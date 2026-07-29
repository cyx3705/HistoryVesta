namespace AppShell.Core.Docking;

/// <summary>
/// 工具窗口相对主程序窗体的停靠方位(§4.1 W-03 五种落点)。
/// </summary>
public enum DockSide
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
    /// <summary>并入目标标签组(win.dock pos=tab target=...)。</summary>
    Tab = 4,
    /// <summary>占据中央工作区；多个中央窗口组成标签组。</summary>
    Center = 5,
}
