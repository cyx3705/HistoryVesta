using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using AppShell.Core.Commands;
using AppShell.Core.Data;
using AppShell.Core.Docking;
using AppShell.Core.Files;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using AppShell.Services;
using AppShell.Shell.Console;
using AppShell.Shell.Docking;
using AppShell.Shell.Panels;
using AppShell.Shell.Table;

namespace AppShell.Shell;

/// <summary>内置指令组的依赖集(注册时一次性提供)。</summary>
public sealed class ShellCommandServices
{
    public required ShellWindow Window { get; init; }

    public required IDockingService Docking { get; init; }

    public required ConsoleView Console { get; init; }

    public required CommandHistory History { get; init; }

    public required ISettingsService Settings { get; init; }

    public required IShellLog Log { get; init; }

    public required CommandBus Bus { get; init; }

    public required string DataDirectory { get; init; }

    /// <summary>数据服务(M3);null 时 db.* 指令组不注册。</summary>
    public IDataService? Data { get; init; }

    /// <summary>表窗口(M3);db.query 结果同步显示的落点。</summary>
    public TableView? Table { get; init; }

    /// <summary>工作区文件服务(M4);null 时 res.* 指令组不注册。</summary>
    public IWorkspaceService? Workspace { get; init; }

    /// <summary>控制窗口群管理器(M4);null 时 panel.* 指令组不注册。</summary>
    public PanelManager? Panels { get; init; }
}

/// <summary>
/// 框架内置指令组(§5.3 / 附录 B):help / cls / history / run /
/// app.* / log.level / win.* / layout.*。db.*(M3)、res.* 与 panel.*(M4)后续并入。
/// </summary>
public static class BuiltinCommands
{
    public static void Register(CommandRegistry r, ShellCommandServices s)
    {
        RegisterBasics(r, s);
        RegisterApp(r, s);
        RegisterLog(r, s);
        RegisterWin(r, s);
        RegisterLayout(r, s);
        if (s.Data != null)
            RegisterDb(r, s, s.Data);
        if (s.Workspace != null)
            RegisterRes(r, s, s.Workspace);
        if (s.Panels != null)
            RegisterPanel(r, s, s.Panels);
    }

    // ---------------------------------------------------------------- res.*(§4.6 / 附录 B,M4)

    private static void RegisterRes(CommandRegistry r, ShellCommandServices s, IWorkspaceService ws)
    {
        var pathParam = new ParameterSpec
        {
            Name = "path",
            Description = "相对工作区根目录的路径",
            Required = true,
            Position = 0,
        };

        r.Register(new CommandDescriptor
        {
            Name = "res.root",
            Summary = "查看 / 切换工作区根目录",
            Example = "res.root path=D:\\我的工程",
            Parameters =
            [
                new ParameterSpec { Name = "path", Description = "新根目录(绝对路径);省略时查看当前值", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var path = ctx.GetString("path");
                if (path == null)
                    return CommandResult.Ok($"工作区根目录: {ws.Root}");

                ws.SetRoot(path);
                s.Settings.Set(WorkspaceService.KeyRoot, path);
                return CommandResult.Ok($"工作区根目录已切换为 {ws.Root}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "res.list",
            Summary = "列出目录内容",
            Example = "res.list path=配方",
            Parameters =
            [
                new ParameterSpec { Name = "path", Description = "目录(相对根);省略为根目录", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var entries = ws.List(ctx.GetString("path"));
                if (entries.Count == 0)
                    return CommandResult.Ok("(空目录)");

                var sb = new StringBuilder($"共 {entries.Count} 项:");
                foreach (var e in entries)
                {
                    sb.Append(e.IsDirectory
                        ? $"\n  [目录] {e.Name}"
                        : $"\n  {e.Name}  ({e.Size} B, {e.Modified:yyyy-MM-dd HH:mm})");
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "res.open",
            Summary = "用系统默认程序打开文件",
            Example = "res.open path=说明.txt",
            Parameters = [pathParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var full = ws.ResolveFull(ctx.RequireString("path"));
                Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
                return CommandResult.Ok($"已打开 {full}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "res.reveal",
            Summary = "在系统资源管理器中显示",
            Example = "res.reveal path=配方",
            Parameters = [pathParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var full = ws.ResolveFull(ctx.RequireString("path"));
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
                {
                    UseShellExecute = true,
                });
                return CommandResult.Ok($"已在资源管理器中显示 {full}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "res.mkdir",
            Summary = "新建文件夹(限工作区内)",
            Example = "res.mkdir path=新配方",
            Parameters = [pathParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var path = ctx.RequireString("path");
                ws.CreateDirectory(path);
                return CommandResult.Ok($"已创建文件夹 {path}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "res.rename",
            Summary = "重命名文件或文件夹",
            Example = "res.rename path=\"配方/旧.json\" to=\"新.json\"",
            Parameters =
            [
                pathParam,
                new ParameterSpec { Name = "to", Description = "新名字(不含路径)", Required = true, Position = 1 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var path = ctx.RequireString("path");
                var to = ctx.RequireString("to");
                ws.Rename(path, to);
                return CommandResult.Ok($"已把 {path} 改名为 {to}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "res.delete",
            Summary = "删除到回收站(需二次确认,不做永久删除)",
            Example = "res.delete path=旧配方",
            // R-06:删除必须二次确认——UI 与手输共用总线拦截器
            ConfirmPrompt = ctx => $"将把 {ctx.RequireString("path")} 删除到回收站,确认继续?",
            Parameters = [pathParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var path = ctx.RequireString("path");
                ws.DeleteToRecycleBin(path);
                return CommandResult.Ok($"已把 {path} 移入回收站");
            }),
        });
    }

    // ---------------------------------------------------------------- panel.*(§4.5 / 附录 B,M4)

    private static void RegisterPanel(CommandRegistry r, ShellCommandServices s, PanelManager panels)
    {
        r.Register(new CommandDescriptor
        {
            Name = "panel.list",
            Summary = "列出全部控制面板及其窗口状态",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (panels.Definitions.Count == 0)
                    return CommandResult.Ok("(未定义任何面板;把 JSON 放入数据目录 panels/ 下)");

                var states = s.Docking.ListWindows()
                    .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder($"共 {panels.Definitions.Count} 个面板:");
                foreach (var d in panels.Definitions)
                {
                    var state = states.TryGetValue(d.Id, out var w)
                        ? (w.IsVisible ? (w.IsFloating ? "浮动" : "停靠") : "隐藏")
                        : "未注册";
                    sb.Append($"\n  {d.Id,-12} {state}  {d.Title}({d.Controls.Count} 控件)");
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "panel.show",
            Summary = "显示控制面板(等价 win.show)",
            Example = "panel.show id=motor",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "面板 id(panel.list 可查)", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("id");
                if (!panels.Definitions.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    var known = string.Join(" / ", panels.Definitions.Select(d => d.Id));
                    return CommandResult.Fail(
                        $"没有名为 {id} 的面板。已定义: {(known.Length > 0 ? known : "(无)")}");
                }

                s.Docking.Show(id);
                return CommandResult.Ok($"面板 {id} 已显示");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "panel.set",
            Summary = "程序向面板控件回写值(P-07)",
            Example = "panel.set panel=motor control=speed value=800",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "panel", Description = "面板 id", Required = true, Position = 0 },
                new ParameterSpec { Name = "control", Description = "控件 id", Required = true, Position = 1 },
                new ParameterSpec { Name = "value", Description = "新值", Required = true, Position = 2 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var panel = ctx.RequireString("panel");
                var control = ctx.RequireString("control");
                var value = ctx.RequireString("value");

                // 面板窗口可能尚未实例化(隐藏且从未显示):先确保内容创建
                s.Docking.Show(panel);
                return panels.TrySetValue(panel, control, value, out var error)
                    ? CommandResult.Ok($"{panel}.{control} = {value}")
                    : CommandResult.Fail(error);
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "panel.reload",
            Summary = "重读面板 JSON 配置并原地重建(新增面板需重启)",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(panels.Reload())),
        });
    }

    // ---------------------------------------------------------------- help / cls / history / run

    private static void RegisterBasics(CommandRegistry r, ShellCommandServices s)
    {
        r.Register(new CommandDescriptor
        {
            Name = "help",
            Summary = "列出全部指令 / 显示某指令详情与示例",
            Readonly = true,
            Example = "help win.dock",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "command",
                    Description = "指令名;省略时列出全部指令",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.GetString("command");
                return name == null ? HelpList(s.Bus.Registry) : HelpDetail(s.Bus.Registry, name);
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "cls",
            Summary = "清空控制台显示(不清日志文件)",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Console.Cls();
                return CommandResult.Ok("控制台已清空");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "history",
            Summary = "查看指令历史",
            Readonly = true,
            Example = "history count=10",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "count",
                    Description = "显示条数",
                    Type = ParamType.Int,
                    Default = "20",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var count = Math.Max(1, ctx.GetInt("count", 20));
                var items = s.History.Snapshot();
                if (items.Count == 0)
                    return CommandResult.Ok("(历史为空)");

                var start = Math.Max(0, items.Count - count);
                var sb = new StringBuilder($"最近 {items.Count - start} 条(共 {items.Count} 条):");
                for (var i = start; i < items.Count; i++)
                    sb.Append($"\n{i + 1,4}  {items[i]}");
                return CommandResult.Ok(sb.ToString());
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "run",
            Summary = "逐行执行指令脚本文件(# 注释与空行忽略)",
            Example = "run file=每日巡检.txt continue=true",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "file",
                    Description = "脚本路径;相对路径基于工作区目录",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "continue",
                    Description = "出错时跳过继续(默认中断并报告行号)",
                    Type = ParamType.Bool,
                    Default = "false",
                },
            ],
            Handler = async ctx =>
            {
                var raw = ctx.RequireString("file");
                var path = Path.IsPathRooted(raw)
                    ? raw
                    : Path.Combine(AppPaths.GetWorkspaceDir(s.DataDirectory), raw);
                if (!File.Exists(path))
                    return CommandResult.Fail($"脚本不存在: {path}");

                var source = $"脚本:{Path.GetFileName(path)}";
                var keepGoing = ctx.GetBool("continue");
                var ok = 0;
                var failed = 0;

                var lines = await File.ReadAllLinesAsync(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (CommandParser.IsBlankOrComment(lines[i]))
                        continue;

                    var result = await s.Bus.ExecuteAsync(lines[i], source);
                    if (result.Success)
                    {
                        ok++;
                    }
                    else
                    {
                        failed++;
                        if (!keepGoing)
                            return CommandResult.Fail($"第 {i + 1} 行失败,脚本已中断(continue=true 可跳过错误): {lines[i]}");
                    }
                }

                return failed == 0
                    ? CommandResult.Ok($"脚本执行完成: {ok} 条成功")
                    : CommandResult.Ok($"脚本执行完成: {ok} 条成功,{failed} 条失败(已跳过)");
            },
        });
    }

    private static CommandResult HelpList(CommandRegistry registry)
    {
        var all = registry.All();
        var sb = new StringBuilder($"共 {all.Count} 条指令,help <指令名> 查看详情:");
        foreach (var group in all.GroupBy(d =>
                 {
                     var dot = d.Name.IndexOf('.');
                     return dot > 0 ? d.Name[..dot] : "基础";
                 }, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"\n[{group.Key}] ({group.Count()})");
            foreach (var d in group)
                sb.Append($"\n  {d.Name,-24} {d.Summary}");
        }

        return CommandResult.Ok(sb.ToString());
    }

    private static CommandResult HelpDetail(CommandRegistry registry, string name)
    {
        if (!registry.TryGet(name, out var d))
        {
            var suggestions = registry.Suggest(name);
            var hint = suggestions.Count > 0 ? $"\n相近指令: {string.Join(" / ", suggestions)}" : "";
            return CommandResult.Fail($"未知指令: {name}{hint}");
        }

        var sb = new StringBuilder($"{d.Name} —— {d.Summary}");

        if (d.Parameters.Count == 0)
        {
            sb.Append("\n参数: (无)");
        }
        else
        {
            sb.Append("\n参数:");
            foreach (var p in d.Parameters)
            {
                var attrs = new List<string>();
                if (p.Required)
                    attrs.Add("必填");
                if (p.AllowedValues is { Length: > 0 })
                    attrs.Add(string.Join("/", p.AllowedValues));
                if (p.Default != null)
                    attrs.Add($"默认{p.Default}");
                attrs.Add(p.Type.ToString().ToLowerInvariant());
                var suffix = attrs.Count > 0 ? $"({string.Join(",", attrs)})" : "";
                sb.Append($"\n  {p.Name + suffix,-28} {p.Description}");
            }
        }

        sb.Append($"\n{CommandBus.FormatUsage(d)}");
        if (d.Example != null)
            sb.Append($"\n示例: {d.Example}");
        if (d.ConfirmPrompt != null)
            sb.Append("\n安全: 执行动作可能要求本地二次确认");
        if (d.RequiresUiThread)
            sb.Append("\n线程: UI");
        return CommandResult.Ok(sb.ToString());
    }

    // ---------------------------------------------------------------- app.*

    private static void RegisterApp(CommandRegistry r, ShellCommandServices s)
    {
        r.Register(new CommandDescriptor
        {
            Name = "app.exit",
            Summary = "退出程序",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Window.Close();
                return CommandResult.Ok("正在退出");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "app.about",
            Summary = "显示关于对话框",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                MessageBox.Show(s.Window, s.Window.AboutText, "关于",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return CommandResult.Ok("已显示关于");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "app.opendata",
            Summary = "在系统资源管理器中打开应用数据目录",
            Handler = CommandDescriptor.Sync(_ =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", s.DataDirectory)
                {
                    UseShellExecute = true,
                });
                return CommandResult.Ok($"已打开 {s.DataDirectory}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "app.set",
            Summary = "写应用配置项",
            Example = "app.set key=console.history value=1000",
            Parameters =
            [
                new ParameterSpec { Name = "key", Description = "配置键", Required = true, Position = 0 },
                new ParameterSpec { Name = "value", Description = "配置值", Required = true, Position = 1 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var key = ctx.RequireString("key");
                var value = ctx.RequireString("value");
                s.Settings.Set(key, value);
                return CommandResult.Ok($"{key} = {value}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "app.get",
            Summary = "读应用配置项;不带参数列出全部",
            Readonly = true,
            Example = "app.get key=console.history",
            Parameters =
            [
                new ParameterSpec { Name = "key", Description = "配置键;省略列出全部", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var key = ctx.GetString("key");
                if (key != null)
                {
                    var value = s.Settings.Get(key);
                    return value == null
                        ? CommandResult.Ok($"{key} (未设置)")
                        : CommandResult.Ok($"{key} = {value}");
                }

                var all = s.Settings.All();
                if (all.Count == 0)
                    return CommandResult.Ok("(无配置项)");
                return CommandResult.Ok(
                    $"共 {all.Count} 项:" + string.Concat(all.Select(kv => $"\n  {kv.Key} = {kv.Value}")));
            }),
        });
    }

    // ---------------------------------------------------------------- log.*

    private static void RegisterLog(CommandRegistry r, ShellCommandServices s)
    {
        r.Register(new CommandDescriptor
        {
            Name = "log.level",
            Summary = "调整控制台日志显示级别(文件始终全量)",
            Example = "log.level warn",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "level",
                    Description = "显示级别;省略时查看当前值",
                    Position = 0,
                    AllowedValues = ["trace", "debug", "info", "warn", "error", "fatal"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var text = ctx.GetString("level");
                if (text == null)
                    return CommandResult.Ok($"当前控制台显示级别: {s.Console.MinLevel}");

                var level = Enum.Parse<ShellLogLevel>(text, ignoreCase: true);
                s.Console.SetMinLevel(level);
                return CommandResult.Ok($"控制台显示级别已调整为 {level}(文件始终全量)");
            }),
        });
    }

    // ---------------------------------------------------------------- win.*

    private static void RegisterWin(CommandRegistry r, ShellCommandServices s)
    {
        var nameParam = new ParameterSpec
        {
            Name = "name",
            Description = "窗口名(win.list 可查)",
            Required = true,
            Position = 0,
        };

        r.Register(new CommandDescriptor
        {
            Name = "win.list",
            Summary = "列出全部窗口及状态",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var sb = new StringBuilder("窗口清单:");
                foreach (var w in s.Docking.ListWindows())
                {
                    var state = !w.IsVisible ? "隐藏"
                        : w.IsFloating ? "浮动"
                        : w.Side switch
                        {
                            DockSide.Left => "停靠·左",
                            DockSide.Right => "停靠·右",
                            DockSide.Top => "停靠·上",
                            DockSide.Bottom => "停靠·下",
                            DockSide.Tab => "标签组",
                            _ => "停靠",
                        };
                    var ratio = w.Ratio is { } v and > 0 ? $" {v:P0}" : "";
                    var maximized = s.Docking.MaximizedId?.Equals(
                        w.Id, StringComparison.OrdinalIgnoreCase) == true ? " [最大化]" : "";
                    sb.Append($"\n  {w.Id,-12} {state}{ratio}{maximized}  {w.Title}  owner={w.Owner}");
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });

        RegisterWindowVerb(r, s, "win.show", "显示窗口(隐藏则唤出,已显示则激活)",
            (d, id) => { d.Show(id); return $"{id} 已显示"; });
        RegisterWindowVerb(r, s, "win.hide", "隐藏窗口(状态保留,可再唤出)",
            (d, id) => { d.Hide(id); return $"{id} 已隐藏"; });
        RegisterWindowVerb(r, s, "win.float", "把窗口浮动为独立顶层窗口",
            (d, id) => { d.Float(id); return $"{id} 已浮动"; });
        RegisterWindowVerb(r, s, "win.reset", "把窗口复位到注册时的默认位置",
            (d, id) => { d.ResetWindow(id); return $"{id} 已复位到默认位置"; });

        r.Register(new CommandDescriptor
        {
            Name = "win.max",
            Summary = "最大化指定工具窗口",
            Example = "win.max name=se2sw",
            RequiresUiThread = true,
            Parameters = [nameParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("name");
                var exists = s.Docking.ListWindows().Any(w =>
                    w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                    return CommandResult.Fail($"没有名为 {id} 的窗口");
                s.Docking.MaximizeWindow(id);
                return CommandResult.Ok($"{id} 已最大化");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "win.restore",
            Summary = "退出窗口最大化并恢复原布局",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Docking.RestoreLayoutFromMaximized();
                return CommandResult.Ok("已恢复原布局");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "win.dock",
            Summary = "停靠窗口到指定方位(pos=tab 时并入 target 所在标签组)",
            Example = "win.dock name=console pos=bottom ratio=0.3",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "pos",
                    Description = "停靠方位",
                    Required = true,
                    AllowedValues = ["left", "right", "top", "bottom", "tab"],
                },
                new ParameterSpec { Name = "target", Description = "pos=tab 时,并入哪个窗口所在的标签组" },
                new ParameterSpec { Name = "ratio", Description = "占主窗体比例 0~1", Type = ParamType.Double },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                var id = ctx.RequireString("name");
                var side = ParseSide(ctx.RequireString("pos"));
                var ratio = ctx.Has("ratio") ? ctx.GetDouble("ratio") : (double?)null;
                if (ratio is <= 0 or >= 1)
                    return CommandResult.Fail($"ratio 应在 0~1 之间,实际: {ratio}");

                var target = ctx.GetString("target");
                if (side == DockSide.Tab && target == null)
                    return CommandResult.Fail("pos=tab 时必须指定 target=(并入哪个窗口的标签组)");

                s.Docking.Dock(id, side, ratio, target);
                var where = side switch
                {
                    DockSide.Left => "左侧",
                    DockSide.Right => "右侧",
                    DockSide.Top => "顶部",
                    DockSide.Bottom => "底部",
                    _ => $"{target} 所在标签组",
                };
                var pct = ratio is { } rv ? $"({rv:P0})" : "";
                return CommandResult.Ok($"{id} 已停靠至{where}{pct}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "win.ratio",
            Summary = "调整窗口占主窗体的比例",
            Example = "win.ratio name=table value=0.3",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "value",
                    Description = "比例 0~1",
                    Required = true,
                    Type = ParamType.Double,
                    Position = 1,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                var value = ctx.GetDouble("value");
                if (value is <= 0 or >= 1)
                    return CommandResult.Fail($"value 应在 0~1 之间,实际: {value}");

                var id = ctx.RequireString("name");
                s.Docking.SetRatio(id, value);
                return CommandResult.Ok($"{id} 比例已调整为 {value:P0}");
            }),
        });
    }

    private static void RegisterWindowVerb(
        CommandRegistry r,
        ShellCommandServices s,
        string name,
        string summary,
        Func<IDockingService, string, string> action)
    {
        r.Register(new CommandDescriptor
        {
            Name = name,
            Summary = summary,
            Example = $"{name} name=console",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "窗口名(win.list 可查)",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;
                return CommandResult.Ok(action(s.Docking, ctx.RequireString("name")));
            }),
        });
    }

    /// <summary>窗口名存在性校验;返回 null 表示通过。</summary>
    private static CommandResult? ResolveWindow(ShellCommandServices s, CommandContext ctx)
    {
        var id = ctx.RequireString("name");
        if (s.Docking.ListWindows().Any(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return null;

        var known = string.Join(" / ", s.Docking.ListWindows().Select(w => w.Id));
        return CommandResult.Fail($"没有名为 {id} 的窗口。已注册: {known}");
    }

    private static DockSide ParseSide(string pos) => pos.ToLowerInvariant() switch
    {
        "left" => DockSide.Left,
        "right" => DockSide.Right,
        "top" => DockSide.Top,
        "bottom" => DockSide.Bottom,
        _ => DockSide.Tab,
    };

    // ---------------------------------------------------------------- db.*(§6.1 / 附录 B,M3)

    private static void RegisterDb(CommandRegistry r, ShellCommandServices s, IDataService data)
    {
        var connParam = new ParameterSpec
        {
            Name = "conn",
            Description = $"连接名,缺省 {IDataService.DefaultConnection}(db.list 可查)",
        };
        var tableParam = new ParameterSpec
        {
            Name = "table",
            Description = "表名(db.tables 可查)",
            Required = true,
            Position = 0,
        };

        r.Register(new CommandDescriptor
        {
            Name = "db.list",
            Summary = "列出全部命名数据库连接",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var names = data.ListConnections();
                return names.Count == 0
                    ? CommandResult.Ok("(未注册任何连接)")
                    : CommandResult.Ok($"共 {names.Count} 个连接:" + string.Concat(names.Select(n => $"\n  {n}")));
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.tables",
            Summary = "列出连接内全部表",
            Readonly = true,
            Example = "db.tables",
            Parameters = [new ParameterSpec
            {
                Name = "conn",
                Description = $"连接名,缺省 {IDataService.DefaultConnection}",
                Position = 0,
            }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var tables = data.ListTables(ctx.GetString("conn"));
                return tables.Count == 0
                    ? CommandResult.Ok("(库内没有表)")
                    : CommandResult.Ok($"共 {tables.Count} 张表:" + string.Concat(tables.Select(t => $"\n  {t}")));
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.schema",
            Summary = "查看表结构(字段名 / 类型 / 主键 / 非空)",
            Readonly = true,
            Example = "db.schema table=users",
            Parameters = [tableParam, connParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var columns = data.GetSchema(ctx.RequireString("table"), ctx.GetString("conn"));
                var sb = new StringBuilder($"{ctx.RequireString("table")} 共 {columns.Count} 列:");
                foreach (var c in columns)
                {
                    sb.Append($"\n  {c.Name,-20} {c.Type,-10}");
                    if (c.IsPrimaryKey)
                        sb.Append(" [主键]");
                    if (c.NotNull)
                        sb.Append(" [非空]");
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.query",
            Summary = "查询表数据(表窗口同步显示结果)",
            Readonly = true,
            Example = "db.query table=users where=\"age>30 and city='北京'\" limit=100",
            RequiresUiThread = true, // 结果要同步进表窗口
            Parameters =
            [
                tableParam,
                new ParameterSpec { Name = "where", Description = "SQL 条件片段(原样拼接)" },
                new ParameterSpec { Name = "order", Description = "SQL 排序片段,如 \"age desc\"" },
                new ParameterSpec { Name = "limit", Description = "每页行数", Type = ParamType.Int, Default = "500" },
                new ParameterSpec { Name = "page", Description = "页码(1 起)", Type = ParamType.Int, Default = "1" },
                connParam,
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var table = ctx.RequireString("table");
                var where = ctx.GetString("where");
                var conn = ctx.GetString("conn") ?? IDataService.DefaultConnection;
                var result = data.Query(
                    table, where, ctx.GetString("order"),
                    ctx.GetInt("limit", 500), ctx.GetInt("page", 1), conn);

                var synced = "";
                if (s.Table != null)
                {
                    s.Table.ShowResult(conn, table, where, result);
                    s.Docking.Show("table");
                    synced = "(表窗口已同步显示)";
                }

                return CommandResult.Ok(
                    $"{result.TotalRows} 行,第 {result.Page}/{result.TotalPages} 页{synced}",
                    result);
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.insert",
            Summary = "插入一行",
            Example = "db.insert table=users set=\"name='张三', age=30\"",
            Parameters =
            [
                tableParam,
                new ParameterSpec
                {
                    Name = "set",
                    Description = "列=值 列表,逗号分隔,字符串用单引号",
                    Required = true,
                },
                connParam,
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var n = data.Insert(
                    ctx.RequireString("table"), ctx.RequireString("set"), ctx.GetString("conn"));
                return CommandResult.Ok($"已插入 {n} 行");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.update",
            Summary = "更新行(无 where 将更新整表,需二次确认)",
            Example = "db.update table=users set=\"vip=1\" where=\"id=1032\"",
            Parameters =
            [
                tableParam,
                new ParameterSpec { Name = "set", Description = "列=值 列表,逗号分隔", Required = true },
                new ParameterSpec { Name = "where", Description = "SQL 条件;省略 = 整表更新(危险)" },
                connParam,
            ],
            // T-08 / 验收 9:手输路径的整表更新在总线拦截器统一二次确认
            ConfirmPrompt = ctx => ctx.GetString("where") == null
                ? $"db.update 未指定 where,将更新表 {ctx.GetString("table")} 的全部行!确认继续?"
                : null,
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var n = data.Update(
                    ctx.RequireString("table"), ctx.RequireString("set"),
                    ctx.GetString("where"), ctx.GetString("conn"));
                return CommandResult.Ok($"影响 {n} 行");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.delete",
            Summary = "删除行(无 where 将清空整表,需二次确认)",
            Example = "db.delete table=users where=\"id=1032\"",
            Parameters =
            [
                tableParam,
                new ParameterSpec { Name = "where", Description = "SQL 条件;省略 = 清空整表(危险)" },
                connParam,
            ],
            ConfirmPrompt = ctx => ctx.GetString("where") == null
                ? $"db.delete 未指定 where,将删除表 {ctx.GetString("table")} 的全部行!确认继续?"
                : null,
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var n = data.Delete(
                    ctx.RequireString("table"), ctx.GetString("where"), ctx.GetString("conn"));
                return CommandResult.Ok($"已删除 {n} 行");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.export",
            Summary = "导出查询结果为 CSV 文件",
            Example = "db.export table=users file=用户.csv where=\"vip=1\"",
            Parameters =
            [
                tableParam,
                new ParameterSpec
                {
                    Name = "file",
                    Description = "目标文件;相对路径落到工作区目录",
                    Required = true,
                },
                new ParameterSpec { Name = "where", Description = "SQL 条件片段" },
                connParam,
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var raw = ctx.RequireString("file");
                var path = Path.IsPathRooted(raw)
                    ? raw
                    : Path.Combine(AppPaths.GetWorkspaceDir(s.DataDirectory), raw);
                var n = data.ExportCsv(
                    ctx.RequireString("table"), path, ctx.GetString("where"), ctx.GetString("conn"));
                return CommandResult.Ok($"已导出 {n} 行到 {path}");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "db.sql",
            Summary = "SQL 直通(高级用户;执行前有风险确认)",
            Example = "db.sql \"SELECT city, COUNT(*) FROM users GROUP BY city\"",
            RequiresUiThread = true, // SELECT 结果进表窗口
            Parameters =
            [
                new ParameterSpec { Name = "sql", Description = "完整 SQL 语句", Required = true, Position = 0 },
                connParam,
            ],
            ConfirmPrompt = ctx =>
                $"SQL 直通绕过一切保护,确认执行?\n\n{ctx.RequireString("sql")}",
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var (result, affected) = data.ExecuteSql(
                    ctx.RequireString("sql"), ctx.GetString("conn"));
                if (result == null)
                    return CommandResult.Ok($"影响 {affected} 行");

                var synced = "";
                if (s.Table != null)
                {
                    s.Table.ShowAdhoc(result);
                    s.Docking.Show("table");
                    synced = "(表窗口已显示,只读)";
                }

                return CommandResult.Ok($"{result.Rows.Count} 行{synced}", result);
            }),
        });
    }

    // ---------------------------------------------------------------- layout.*

    private static void RegisterLayout(CommandRegistry r, ShellCommandServices s)
    {
        r.Register(new CommandDescriptor
        {
            Name = "layout.save",
            Summary = "把当前布局保存为命名方案",
            Example = "layout.save name=调试布局",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "方案名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                s.Docking.SaveLayout(name);
                return CommandResult.Ok($"布局方案 [{name}] 已保存");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "layout.load",
            Summary = "加载命名布局方案",
            Example = "layout.load name=调试布局",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "方案名(layout.list 可查)", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                return s.Docking.LoadLayout(name)
                    ? CommandResult.Ok($"布局方案 [{name}] 已加载")
                    : CommandResult.Fail($"布局方案 [{name}] 不存在或加载失败");
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "layout.list",
            Summary = "列出全部命名布局方案",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var names = s.Docking.ListLayouts();
                return names.Count == 0
                    ? CommandResult.Ok("(暂无命名布局方案,layout.save name=xxx 可保存)")
                    : CommandResult.Ok($"共 {names.Count} 个方案:" + string.Concat(names.Select(n => $"\n  {n}")));
            }),
        });

        r.Register(new CommandDescriptor
        {
            Name = "layout.reset",
            Summary = "重置为默认布局",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Docking.ResetLayout();
                return CommandResult.Ok("已重置为默认布局");
            }),
        });
    }
}
