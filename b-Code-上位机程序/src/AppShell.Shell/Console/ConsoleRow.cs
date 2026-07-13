using System.Windows.Media;
using AppShell.Core.Commands;
using AppShell.Core.Logging;

namespace AppShell.Shell.Console;

/// <summary>控制台一行的显示模型:预先算好文本与颜色,渲染零逻辑。</summary>
public sealed class ConsoleRow
{
    public required string Text { get; init; }

    public required Brush Foreground { get; init; }

    public required ShellLogLevel Level { get; init; }

    /// <summary>来源键(过滤用):UI / 手动 / 脚本 / layout / result / 或日志类别。</summary>
    public required string SourceKey { get; init; }

    public static ConsoleRow From(ShellLogEntry e)
    {
        // 指令回显(cmd:来源):附录 C 样式 "[10:21:03] [手动] > help db.query"
        if (e.Category.StartsWith(CommandBus.EchoCategoryPrefix, StringComparison.Ordinal))
        {
            var source = e.Category[CommandBus.EchoCategoryPrefix.Length..];
            return source switch
            {
                "result" => new ConsoleRow
                {
                    Text = Indent(e.Message),
                    Foreground = e.Level >= ShellLogLevel.Error ? ErrorBrush : ResultBrush,
                    Level = e.Level,
                    SourceKey = "result",
                },
                "progress" => new ConsoleRow
                {
                    Text = $"  ... {e.Message}",
                    Foreground = ProgressBrush,
                    Level = e.Level,
                    SourceKey = "result",
                },
                _ => new ConsoleRow
                {
                    Text = $"[{e.Time:HH:mm:ss}] [{source}] > {e.Message}",
                    Foreground = EchoBrush,
                    Level = e.Level,
                    SourceKey = SourceKeyOf(source),
                },
            };
        }

        // 普通日志(C-03 按级别着色)
        return new ConsoleRow
        {
            Text = $"{e.Time:HH:mm:ss.fff} [{e.Level}] [{e.Category}] {e.Message}",
            Foreground = LevelBrush(e.Level),
            Level = e.Level,
            SourceKey = e.Category,
        };
    }

    /// <summary>脚本来源统一归入 “脚本” 键(具体文件名保留在显示文本中)。</summary>
    private static string SourceKeyOf(string source)
        => source.StartsWith("脚本", StringComparison.Ordinal) ? "脚本" : source;

    private static string Indent(string message)
        => "  " + message.Replace("\n", "\n  ");

    private static Brush LevelBrush(ShellLogLevel level) => level switch
    {
        ShellLogLevel.Trace => TraceBrush,
        ShellLogLevel.Debug => DebugBrush,
        ShellLogLevel.Info => InfoBrush,
        ShellLogLevel.Warn => WarnBrush,
        ShellLogLevel.Error => ErrorBrush,
        _ => FatalBrush,
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush TraceBrush = Frozen(0xB0, 0xB0, 0xB0);
    private static readonly Brush DebugBrush = Frozen(0x88, 0x88, 0x88);
    private static readonly Brush InfoBrush = Frozen(0x30, 0x30, 0x30);
    private static readonly Brush WarnBrush = Frozen(0xB8, 0x86, 0x0B);
    private static readonly Brush ErrorBrush = Frozen(0xC4, 0x25, 0x25);
    private static readonly Brush FatalBrush = Frozen(0x8B, 0x00, 0x00);
    private static readonly Brush EchoBrush = Frozen(0x00, 0x50, 0xA0);
    private static readonly Brush ResultBrush = Frozen(0x20, 0x70, 0x20);
    private static readonly Brush ProgressBrush = Frozen(0x60, 0x60, 0xA0);
}
