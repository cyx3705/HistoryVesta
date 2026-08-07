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

    internal string DomainKey { get; init; } = "core";

    /// <summary>
    /// 一条日志记录 → 一到多个显示行:多行消息(如 proj.tree 的树形结果)逐行拆分。
    /// 单个超高项会让按项滚动永远看不到项的下半截,且拖累虚拟化,故在入口处拆平。
    /// </summary>
    public static IReadOnlyList<ConsoleRow> From(ShellLogEntry e)
    {
        string text;
        Brush foreground;
        string sourceKey;
        string domainKey;

        // 指令回显(cmd:来源):附录 C 样式 "[10:21:03] [手动] > help command.list"
        if (e.Category.StartsWith(CommandBus.EchoCategoryPrefix, StringComparison.Ordinal))
        {
            var source = e.Category[CommandBus.EchoCategoryPrefix.Length..];
            if (TryReadCommandOutput(source, CommandBus.ResultCategory, out var resultDomain))
            {
                text = Indent(e.Message);
                foreground = e.Level >= ShellLogLevel.Error ? ErrorBrush : ResultBrush;
                sourceKey = "result";
                domainKey = resultDomain;
            }
            else if (TryReadCommandOutput(source, CommandBus.ProgressCategory, out var progressDomain))
            {
                text = $"  ... {e.Message}";
                foreground = ProgressBrush;
                sourceKey = "result";
                domainKey = progressDomain;
            }
            else
            {
                text = $"[{e.Time:HH:mm:ss}] [{source}] > {e.Message}";
                foreground = EchoBrush;
                sourceKey = SourceKeyOf(source);
                domainKey = DomainOfCommandText(e.Message);
            }
        }
        else
        {
            // 普通日志(C-03 按级别着色)
            text = $"{e.Time:HH:mm:ss.fff} [{e.Level}] [{e.Category}] {e.Message}";
            foreground = LevelBrush(e.Level);
            sourceKey = e.Category;
            domainKey = DomainOfLogCategory(e.Category);
        }

        if (!text.Contains('\n'))
        {
            return [new ConsoleRow
            {
                Text = text,
                Foreground = foreground,
                Level = e.Level,
                SourceKey = sourceKey,
                DomainKey = domainKey,
            }];
        }

        return text.Split('\n')
            .Select(line => new ConsoleRow
            {
                Text = line.TrimEnd('\r'),
                Foreground = foreground,
                Level = e.Level,
                SourceKey = sourceKey,
                DomainKey = domainKey,
            })
            .ToList();
    }

    /// <summary>脚本来源统一归入 “脚本” 键(具体文件名保留在显示文本中)。</summary>
    private static string SourceKeyOf(string source)
        => source.StartsWith("脚本", StringComparison.Ordinal) ? "脚本" : source;

    private static bool TryReadCommandOutput(string source, string category, out string domain)
    {
        var prefix = category[CommandBus.EchoCategoryPrefix.Length..];
        if (source.Equals(prefix, StringComparison.Ordinal))
        {
            domain = "core";
            return true;
        }

        if (source.StartsWith(prefix + ":", StringComparison.Ordinal))
        {
            domain = DomainOfLogCategory(source[(prefix.Length + 1)..]);
            return true;
        }

        domain = "";
        return false;
    }

    private static string DomainOfCommandText(string text)
    {
        string name;
        try
        {
            name = CommandParser.Parse(text).Name;
        }
        catch (CommandSyntaxException)
        {
            var trimmed = text.TrimStart();
            var separator = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
            name = separator >= 0 ? trimmed[..separator] : trimmed;
        }

        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : "core";
    }

    private static string DomainOfLogCategory(string category)
    {
        var colon = category.IndexOf(':');
        var dot = category.IndexOf('.');
        var separator = colon < 0 ? dot : dot < 0 ? colon : Math.Min(colon, dot);
        return separator > 0 ? category[..separator] : category;
    }

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
