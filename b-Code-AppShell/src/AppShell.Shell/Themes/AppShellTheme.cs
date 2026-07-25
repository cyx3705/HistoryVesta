using AvalonDock.Themes;

namespace AppShell.Shell.Themes;

/// <summary>
/// Shell 统一主题(§14.2 清单 7 主题挂钩):
/// 以 VS2013 浅色为基底,叠加标签条置顶等 Shell 侧改造。
/// 深色主题(S-04,P2)将来在此处扩展切换。
/// 通过 Theme 机制下发可确保浮动窗口同样套用改造样式。
/// </summary>
public sealed class AppShellTheme : Theme
{
    public override Uri GetResourceUri()
        => new("/AppShell.Shell;component/Themes/AppShellTheme.xaml", UriKind.Relative);
}
