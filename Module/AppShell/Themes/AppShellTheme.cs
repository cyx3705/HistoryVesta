using AvalonDock.Themes;

namespace OneHistory.AppShell.Desktop.Themes;

internal sealed class AppShellTheme : Theme
{
    public override Uri GetResourceUri()
        => new("/OneHistory.AppShell.Desktop;component/Themes/AppShellTheme.xaml", UriKind.Relative);
}
