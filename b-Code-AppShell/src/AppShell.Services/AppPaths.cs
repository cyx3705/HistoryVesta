namespace AppShell.Services;

/// <summary>
/// 应用数据目录约定(F-02,Q9 已定):%AppData%/&lt;应用名&gt;/,
/// 布局 / 设置 / 历史 / data / workspace / logs 均置于其下。
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("应用名不能为空", nameof(appName));

        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            appName);

        LayoutDir = Path.Combine(Root, "layout");
        DataDir = Path.Combine(Root, "data");
        WorkspaceDir = Path.Combine(Root, "workspace");
        LogsDir = Path.Combine(Root, "logs");

        Directory.CreateDirectory(LayoutDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(WorkspaceDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>%AppData%/&lt;应用名&gt;/</summary>
    public string Root { get; }

    /// <summary>布局文件目录(W-07 / W-08)。</summary>
    public string LayoutDir { get; }

    /// <summary>SQLite 库文件目录(D-04,M3 使用)。</summary>
    public string DataDir { get; }

    /// <summary>资源窗口默认工作区根目录(R-08,M4 使用)。</summary>
    public string WorkspaceDir { get; }

    /// <summary>滚动日志目录(L-02,M2 正式接管)。</summary>
    public string LogsDir { get; }
}
