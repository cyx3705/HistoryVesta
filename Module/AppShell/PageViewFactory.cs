using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OneHistory.AppShell.Desktop;

internal static class PageViewFactory
{
    public static FrameworkElement CreateWelcome()
        => CreateInfo(
            "AppShell V4.0",
            "纯桌面 Shell 前端",
            "当前迁移只保留窗口、页面目录和页面承载。命令总线、HTTP、MCP 以及后端服务不属于这个内核。");

    public static FrameworkElement CreateInfo(string title, string subtitle, string body)
    {
        var content = new StackPanel
        {
            Margin = new Thickness(32),
            MaxWidth = 760,
        };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["ShellText"],
        });
        content.Children.Add(new TextBlock
        {
            Text = subtitle,
            Margin = new Thickness(0, 8, 0, 22),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
        });
        content.Children.Add(new Border
        {
            Padding = new Thickness(18),
            BorderBrush = (Brush)Application.Current.Resources["ShellBorder"],
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Child = new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 24,
                Foreground = (Brush)Application.Current.Resources["ShellText"],
            },
        });
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
}
