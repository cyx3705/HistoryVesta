using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Docking;
using HistoryVulcan.Shell.Panels;

namespace HistoryVulcan.Shell;

public static partial class BuiltinCommands
{
    private static void RegisterLog(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.level",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "设置控制台显示级别",
            Example = "log.level level=warn",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "level", Description = "trace/debug/info/warn/error/fatal；省略时查询当前值", Position = 0, AllowedValues = ["trace", "debug", "info", "warn", "error", "fatal"] }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var text = ctx.GetString("level");
                if (text == null)
                    return CommandResult.Ok($"当前控制台级别: {s.Console.MinLevel.ToString().ToLowerInvariant()}");
                if (!Enum.TryParse<ShellLogLevel>(text, true, out var level))
                    return CommandResult.Fail($"未知日志级别: {text}");
                s.Console.SetMinLevel(level);
                return CommandResult.Ok($"控制台级别已设置为 {level.ToString().ToLowerInvariant()}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "app.window",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "设置主窗口状态",
            Example = "app.window state=toggle",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "state",
                    Description = "normal/minimized/maximized/toggle；省略时查询当前值",
                    Position = 0,
                    AllowedValues = ["normal", "minimized", "maximized", "toggle"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var state = ctx.GetString("state");
                if (state == null)
                    return CommandResult.Ok($"主窗口状态: {s.Window.WindowState.ToString().ToLowerInvariant()}");

                s.Window.WindowState = state.ToLowerInvariant() switch
                {
                    "normal" => WindowState.Normal,
                    "minimized" => WindowState.Minimized,
                    "maximized" => WindowState.Maximized,
                    _ => s.Window.WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized,
                };
                return CommandResult.Ok($"主窗口状态已设置为 {s.Window.WindowState.ToString().ToLowerInvariant()}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.source",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "设置控制台日志域过滤（兼容命令名）",
            Example = "log.source source=app",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "source", Description = "命令总线当前已注册的域；省略时查询当前值", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var source = ctx.GetString("source");
                if (source == null)
                    return CommandResult.Ok($"当前日志域: {s.Console.SourceFilterValue}");
                if (!s.Console.TrySetSource(source, out var available))
                    return CommandResult.Fail($"日志域不存在: {source}；可用域: {string.Join(" / ", available)}");
                return CommandResult.Ok($"日志域已设置为 {s.Console.SourceFilterValue}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.keyword",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "设置控制台关键字过滤",
            Example = "log.keyword text=timeout",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "text", Description = "关键字；省略时查询当前值", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var text = ctx.GetString("text");
                if (text == null)
                    return CommandResult.Ok(string.IsNullOrEmpty(s.Console.KeywordFilterValue) ? "当前关键字: (无)" : $"当前关键字: {s.Console.KeywordFilterValue}");
                s.Console.SetKeyword(text);
                return CommandResult.Ok(string.IsNullOrEmpty(text) ? "关键字过滤已清除" : $"关键字已设置为 {text}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.mute",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "屏蔽或恢复 layout 来源",
            Example = "log.mute layout=true",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "layout", Description = "true/false；省略时查询当前值", Type = ParamType.Bool, Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.Has("layout"))
                    return CommandResult.Ok($"layout 屏蔽: {s.Console.MuteLayoutEnabled}");
                var enabled = ctx.GetBool("layout");
                s.Console.SetMuteLayout(enabled);
                return CommandResult.Ok($"layout 屏蔽已设置为 {enabled}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.autoscroll",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "设置控制台自动滚动",
            Example = "log.autoscroll enabled=false",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "enabled", Description = "true/false；省略时查询当前值", Type = ParamType.Bool, Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.Has("enabled"))
                    return CommandResult.Ok($"自动滚动: {s.Console.AutoScrollEnabled}");
                var enabled = ctx.GetBool("enabled");
                s.Console.SetAutoScroll(enabled);
                return CommandResult.Ok($"自动滚动已设置为 {enabled}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.clear",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "清空控制台可见缓冲",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Console.Cls();
                return CommandResult.Ok("控制台已清空");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "cls",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "兼容别名，转发到 log.clear",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Console.Cls();
                return CommandResult.Ok("控制台已清空");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.export",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "导出控制台当前可见内容",
            Example = "log.export path=console.txt",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "path", Description = "目标文件路径；省略时打开保存对话框", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx => CommandResult.Ok(s.Console.ExportVisible(ctx.GetString("path")))),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.copy",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "复制控制台选中行",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(s.Console.CopySelected())),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "log.focus",
            Domain = "HistoryVulcan",
            CommandClass = "log",
            Summary = "聚焦控制台，可选仅显示错误",
            Example = "log.focus errors=true",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "errors", Description = "true 时切换到错误过滤", Type = ParamType.Bool, Default = "false", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var errors = ctx.GetBool("errors");
                if (errors)
                    s.Console.FilterErrorsOnly();
                s.Docking.Show(StandardWindowIds.Console);
                s.Console.FocusInput();
                return CommandResult.Ok(errors ? "已聚焦控制台错误" : "已聚焦控制台");
            }),
        });
    }
}

