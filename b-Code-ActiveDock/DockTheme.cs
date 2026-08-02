using System.Windows.Media;

namespace ActiveDock;

/// <summary>
/// 活动坞配色。2.0.0 由浅底改为深色半透明底，前景色整体反相。
/// </summary>
/// <remarks>
/// 不做系统毛玻璃：那要求拆掉 AllowsTransparency，而 1.1.x 已在窗口基础样式上翻过两次车
/// （坐标基准错乱、鼠标输入丢失）。这里只换画刷，窗口结构一律不动。
/// </remarks>
public static class DockTheme
{
    /// <summary>面板深色半透明底，透出的是桌面壁纸。</summary>
    public static readonly SolidColorBrush PanelBackground = Frozen(Color.FromArgb(0xCC, 0x1E, 0x21, 0x26));

    /// <summary>浅色低对比描边，避免深底上边框突兀。</summary>
    public static readonly SolidColorBrush PanelBorder = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    /// <summary>项目编号文字。</summary>
    public static readonly SolidColorBrush Label = Frozen(Color.FromRgb(0xE6, 0xEA, 0xEE));

    /// <summary>空列表提示文字。</summary>
    public static readonly SolidColorBrush Muted = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));

    /// <summary>悬停态用浅色半透明叠加，深底上不会闪出白块。</summary>
    public static readonly SolidColorBrush Hover = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    /// <summary>权重光圈统一白色，不随项目主色变化。</summary>
    public static readonly Color Glow = Colors.White;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
