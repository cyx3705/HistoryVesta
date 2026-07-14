namespace AppShell.Core.Logging;

/// <summary>日志级别(C-03 六级)。</summary>
public enum ShellLogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}

/// <summary>一条日志/指令回显记录。</summary>
public sealed record ShellLogEntry(
    DateTime Time,
    ShellLogLevel Level,
    string Category,
    string Message);

/// <summary>
/// M1 阶段的最小日志汇聚点:内存环形缓冲 + 事件推送 + 文件落盘。
/// M2 的正式日志服务(§6.2)将实现/替换本接口,控制台窗口从这里补读历史。
/// </summary>
public interface IShellLog
{
    void Log(ShellLogLevel level, string category, string message);

    /// <summary>新纪录到达事件(控制台窗口 / 占位页订阅)。</summary>
    event EventHandler<ShellLogEntry>? EntryAdded;

    /// <summary>当前缓冲内容快照。</summary>
    IReadOnlyList<ShellLogEntry> Snapshot();
}

public static class ShellLogExtensions
{
    public static void Info(this IShellLog log, string category, string message)
        => log.Log(ShellLogLevel.Info, category, message);

    public static void Warn(this IShellLog log, string category, string message)
        => log.Log(ShellLogLevel.Warn, category, message);

    public static void Error(this IShellLog log, string category, string message)
        => log.Log(ShellLogLevel.Error, category, message);
}
