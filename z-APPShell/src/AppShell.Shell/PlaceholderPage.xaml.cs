using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AppShell.Core.Logging;

namespace AppShell.Shell;

/// <summary>
/// 模板默认占位页(M-02):显示程序名、版本与入门提示;
/// 控制台窗口(M2)就位前,兼作 Warn 及以上框架消息的可见出口(N-06 告警落点)。
/// </summary>
public partial class PlaceholderPage : UserControl
{
    private readonly ObservableCollection<string> _notices = new();

    public PlaceholderPage(string appName, string appVersion, IShellLog log)
    {
        InitializeComponent();
        AppNameText.Text = appName;
        VersionText.Text = $"v{appVersion} · AppShell 通用窗口框架模板";
        NoticeList.ItemsSource = _notices;

        foreach (var entry in log.Snapshot())
            AddIfNotable(entry);

        log.EntryAdded += (_, entry) =>
        {
            if (Dispatcher.CheckAccess())
                AddIfNotable(entry);
            else
                Dispatcher.BeginInvoke(() => AddIfNotable(entry));
        };
    }

    private void AddIfNotable(ShellLogEntry entry)
    {
        if (entry.Level < ShellLogLevel.Warn)
            return;

        _notices.Add($"[{entry.Time:HH:mm:ss}] [{entry.Level}] {entry.Message}");
        NoticePanel.Visibility = Visibility.Visible;
    }
}
