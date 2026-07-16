using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Services;
using AppShell.Shell;
using OneHistoryStudio.Git;

namespace OneHistoryStudio;

/// <summary>
/// OneHistoryStudio 装配点(派生自 z-APPShell 基线 0.4.1-M4,§9 流程)。
/// V2-M0:仅完成身份派生(应用名/数据目录/版本),模板演示内容暂保留,
/// 将在 V2-M1(proj.* 指令域)与 V2-M2(主窗口内容)中逐步替换。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;
    private Modules.ModuleHost? _modules;
    private Mcp.McpGateway? _mcp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths("OneHistoryStudio");
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;

        // N-05:全局未处理异常捕获 → 落日志 → 友好提示,不崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常: {args.Exception}");
            MessageBox.Show($"发生未处理异常,已记录日志:\n{args.Exception.Message}",
                "OneHistoryStudio", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常(非 UI 线程): {args.ExceptionObject}");

        // 数据服务(§9 流程第 5 条):注册 main 连接并准备演示数据
        var dataService = new SqliteDataService(paths);
        dataService.RegisterConnection("main", "main.db");
        SeedDemoData(dataService, log);

        // 操作留痕与分支描述(V2-M4,DT-01/02):push_history / branch_notes 建表
        var history = new HistoryRecorder(dataService, log);

        // 控制面板(§9 流程第 4 条 / UI-10~12):首启写入面板 JSON,清理 M0 遗留的 motor 演示面板
        SeedProjectPanels(paths, log);

        // proj.* 指令域(V2-M1):Git 项目库管理。
        // 执行中途的确认(LFS 询问)复用总线确认通道,保持危险操作单闸口(N-04);
        // window 在下方创建,指令实际执行时必已就绪。
        ShellWindow? window = null;
        var projects = new ProjectService(settings, prompt =>
        {
            var confirmation = window?.Commands.Confirmation;
            if (confirmation == null)
                return false; // 无确认通道一律拒绝(与总线安全缺省一致)
            return Current.Dispatcher.Invoke(() => confirmation.Confirm(prompt));
        }, paths.Root);
        projects.EnsureDefaultSettings();
        projects.NotesProvider = history.AllNotes;

        // 工作区(§9 流程第 6 条 / DT-04):默认根 = 项目库根目录,可经 res.root 更改并持久化
        var workspace = new WorkspaceService(
            settings.Get("workspace.root") ?? projects.WorktreeRoot);

        // 模块托管(V2-M3,MD-01~07):Modules 目录热重载,DLL 即指令域
        var modulesDir = settings.Get(Modules.ModuleCommands.KeyModuleDir)
                         ?? System.IO.Path.Combine(paths.Root, "Modules");
        var moduleHost = new Modules.ModuleHost(modulesDir, log);
        _modules = moduleHost;

        // MD-08(V21-M4):窗口创建前先做一次文件级面板同步,
        // 上一会话遗留/预先放置的模块旁面板本次启动即成窗口
        var panelsDir = System.IO.Path.Combine(paths.Root, "panels");
        Modules.ModulePanelSync.SyncFiles(modulesDir, panelsDir, log);

        var config = new ShellConfig
        {
            AppName = "OneHistory 项目管理工具",
            AppVersion = "2.1.1",
            DataService = dataService,
            Workspace = workspace,
            // 中央区不注入内容,保留模板占位页(总览/继承树改为独立工具窗口)
        };

        // 项目总览与继承树:与其他工具窗口同级的可停靠窗口(顶部标签组,占 55%)
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "overview",
            Title = "项目总览",
            DefaultSide = DockSide.Top,
            DefaultRatio = 0.55,
            ContentFactory = () => new Views.OverviewView(() => window?.Commands),
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "tree",
            Title = "继承树",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "overview",
            DefaultRatio = 0.55,
            ContentFactory = () => new Views.BranchTreeView(() => window?.Commands),
        });

        // V2.1.1 管理页:MCP 工具(提示词可改可存)与模块清单,并入顶部标签组
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "mcp",
            Title = "MCP 工具",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "overview",
            DefaultRatio = 0.55,
            ContentFactory = () => new Views.McpToolsView(() => window?.Commands),
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "modules",
            Title = "模块管理",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "overview",
            DefaultRatio = 0.55,
            ContentFactory = () => new Views.ModulesView(() => window?.Commands),
        });
        // Meta文件(V2.0.1):与项目总览同组标签,汇总各项目 z/Z 一级元文件夹
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "meta",
            Title = "Meta文件",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "overview",
            DefaultRatio = 0.55,
            ContentFactory = () => new Views.MetaView(() => window?.Commands),
        });

        // 默认布局按附录 A:资源(左 18%)| 主窗口 | 控制面板(右 22%),底部表窗口+控制台标签组(28%)
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "resource",
            Title = "资源窗口",
            DefaultSide = DockSide.Left,
            DefaultRatio = 0.18,
            // 资源窗口内容由 Shell 提供(Workspace 已配置);此处只声明停靠位置
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "table",
            Title = "表窗口",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.28,
            // 表窗口内容由 Shell 提供(DataService 已配置);此处只声明停靠位置
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "console",
            Title = "控制台",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "table",
            DefaultRatio = 0.28,
            // 控制台内容由 Shell 提供(§4.4);此处只声明停靠位置
        });
        // 控制窗口群:面板由 panels/*.json 声明(motor 演示面板经 SeedDemoPanel 写入),
        // 每个面板自动注册为独立可停靠窗口,无需在此声明

        // 派生应用自定义指令示范(§5.3):与内置指令同表、help 自动收录
        config.ConfigureCommands = registry =>
        {
            ProjectCommands.RegisterAll(registry, projects, history);
            Modules.ModuleCommands.RegisterAll(registry, moduleHost, settings);
            // V2.1:元数据自描述层(M1)+ 网关生命周期指令(M2)+ 提示词管理(V2.1.1 mcp.desc);
            // 网关实例在 window 之后创建
            Mcp.McpCommands.RegisterAll(registry, () => window?.Commands, () => _mcp, settings, history);
            registry.Register(BuildLogFloodCommand(log));
            registry.Register(BuildSeedBenchCommand(dataService));
            registry.Register(BuildSleepCommand());
        };

        window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;

        // MD-08:模块热重载后同步模块旁面板;有变化时经总线 panel.reload
        // (既有面板原地刷新即时生效;全新面板按框架 P-08 约定重启后出现,提示见控制台)
        var capturedWindow = () => window;
        moduleHost.ReloadCompleted += () =>
        {
            if (Modules.ModulePanelSync.SyncFiles(moduleHost.ModulesDirectory, panelsDir, log))
                _ = capturedWindow()?.Commands.ExecuteAsync("panel.reload", "模块面板");
        };

        // 模块宿主接入注册表并首次装载(此刻在 UI 线程,Dispatcher.Invoke 内联执行)
        moduleHost.Attach(window.Commands.Registry);
        moduleHost.Start();

        // MCP 网关(V21-M2):默认关闭(MS-01),mcp.start 显式开启;mcp.autostart=true 时随宿主启动
        _mcp = new Mcp.McpGateway(() => window?.Commands, settings, log, history);
        if (settings.Get(Mcp.McpGateway.KeyAutostart) is { } auto
            && auto.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            var (ok, msg) = _mcp.Start(null);
            log.Log(ok ? ShellLogLevel.Info : ShellLogLevel.Warn, "mcp", $"自启动: {msg}");
        }

        window.Show();

        // --yes:确认通道自动通过(自动化回归/脚本用,IConfirmationService 注释预留的场景)。
        // 仅限 --exec 自测流程使用,日常交互禁止带此参数。
        if (e.Args.Contains("--yes"))
        {
            window.Commands.Confirmation = new AutoConfirmation();
            log.Warn("app", "--yes 已启用:全部二次确认将自动通过(仅限自动化场景)");
        }

        log.Info("app", $"OneHistoryStudio 启动完成,数据目录: {paths.Root}");

        // --exec "指令":启动后顺序执行(自动化/自测入口)
        var startupCommands = new List<string>();
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--exec")
                startupCommands.Add(e.Args[++i]);
        }

        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
    }

    private static async Task RunStartupCommandsAsync(ShellWindow window, List<string> commands)
    {
        foreach (var command in commands)
            await window.Commands.ExecuteAsync(command, "脚本:startup");
    }

    /// <summary>--yes 自动确认(仅自动化回归场景;见 IConfirmationService 注释)。</summary>
    private sealed class AutoConfirmation : AppShell.Core.Commands.IConfirmationService
    {
        public bool Confirm(string prompt) => true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mcp?.Dispose(); // 停监听、释放端口
        _modules?.Dispose(); // 停掉文件监听与防抖定时器
        _log?.Dispose(); // 冲刷文件写入队列
        base.OnExit(e);
    }

    /// <summary>
    /// debug.logflood:按指定速率注入日志(验收 8 / N-03 承压验证)。
    /// 异步长任务示范:后台线程产出、经 Progress 上报进度、全程不阻塞 UI(§5.2)。
    /// </summary>
    private static CommandDescriptor BuildLogFloodCommand(ShellLog log) => new()
    {
        Name = "debug.logflood",
        Summary = "日志承压测试:按指定速率注入日志",
        Example = "debug.logflood rate=1000 seconds=30",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "rate",
                Description = "每秒注入条数",
                Type = ParamType.Int,
                Default = "1000",
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "seconds",
                Description = "持续秒数",
                Type = ParamType.Int,
                Default = "30",
                Position = 1,
            },
        ],
        Handler = async ctx =>
        {
            var rate = Math.Clamp(ctx.GetInt("rate", 1000), 1, 100_000);
            var seconds = Math.Clamp(ctx.GetInt("seconds", 30), 1, 600);
            var total = 0L;
            var sw = Stopwatch.StartNew();

            await Task.Run(async () =>
            {
                // 每 100ms 一批;按“目标累计 = 速率 × 已流逝时间”补齐,睡眠误差不累积
                var lastProgress = 0L;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    var target = Math.Min(
                        (long)(sw.Elapsed.TotalSeconds * rate),
                        (long)rate * seconds);
                    while (total < target)
                    {
                        total++;
                        log.Log(ShellLogLevel.Debug, "flood",
                            $"承压测试消息 #{total} @{sw.ElapsedMilliseconds}ms");
                    }

                    if (sw.ElapsedMilliseconds - lastProgress >= 5000)
                    {
                        lastProgress = sw.ElapsedMilliseconds;
                        ctx.Progress?.Report($"{sw.Elapsed.TotalSeconds:0}s / {seconds}s,已注入 {total} 条");
                    }

                    await Task.Delay(100);
                }

                // 补齐尾差
                for (var expected = (long)rate * seconds; total < expected; total++)
                {
                    log.Log(ShellLogLevel.Debug, "flood",
                        $"承压测试消息 #{total + 1} @{sw.ElapsedMilliseconds}ms");
                }
            });

            return CommandResult.Ok(
                $"承压完成:{total} 条 / {sw.Elapsed.TotalSeconds:0.0}s,实际速率 {total / sw.Elapsed.TotalSeconds:0} 条/秒");
        },
    };

    /// <summary>debug.sleep:异步等待(自动化脚本用,如 --exec 序列中等待模块热重载生效)。</summary>
    private static CommandDescriptor BuildSleepCommand() => new()
    {
        Name = "debug.sleep",
        Summary = "等待指定秒数(自动化脚本用;异步等待,不阻塞 UI)",
        Example = "debug.sleep seconds=5",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "seconds",
                Description = "等待秒数(1~120)",
                Type = ParamType.Int,
                Default = "3",
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var s = Math.Clamp(ctx.GetInt("seconds", 3), 1, 120);
            await Task.Delay(TimeSpan.FromSeconds(s));
            return CommandResult.Ok($"等待 {s}s 完成");
        },
    };

    // ---------------------------------------------------------------- 控制面板(UI-10 / UI-11)

    /// <summary>
    /// 首启写入项目操作与提交推送两块面板 JSON(缺失才写,用户手改的保留);
    /// 并一次性清理 M0 遗留的 motor 演示面板。
    /// 指令模板中的占位值显式带引号,兼容含空格/中文的输入。
    /// </summary>
    private static void SeedProjectPanels(AppPaths paths, ShellLog log)
    {
        try
        {
            var panelsDir = System.IO.Path.Combine(paths.Root, "panels");
            System.IO.Directory.CreateDirectory(panelsDir);

            var stale = System.IO.Path.Combine(panelsDir, "motor.json");
            if (System.IO.File.Exists(stale))
            {
                System.IO.File.Delete(stale);
                log.Info("app", "已清理演示面板 panels/motor.json");
            }

            // V2.1.1:UI-12 的 projmod 按钮面板被「模块管理」页(modules)取代,一次性回收
            var staleProjmod = System.IO.Path.Combine(panelsDir, "projmod.json");
            if (System.IO.File.Exists(staleProjmod))
            {
                System.IO.File.Delete(staleProjmod);
                log.Info("app", "已回收 panels/projmod.json(功能并入「模块管理」页)");
            }

            SeedPanelIfMissing(panelsDir, log, "projops.json",
                """
                {
                  "id": "projops",
                  "title": "项目操作",
                  "visible": true,
                  "side": "right",
                  "ratio": 0.24,
                  "controls": [
                    { "type": "text",   "id": "name", "label": "项目名称" },
                    { "type": "text",   "id": "base", "label": "基础分支", "default": "0000-000-Template" },
                    { "type": "button", "label": "创建分支 + 工作树", "command": "proj.create name=\"{name}\" base=\"{base}\"" },
                    { "type": "button", "label": "打开工作树文件夹", "command": "proj.open name=\"{name}\"" },
                    { "type": "label",  "id": "hint", "label": "提示", "default": "删除项目请在控制台执行 proj.delete" }
                  ]
                }
                """);

            SeedPanelIfMissing(panelsDir, log, "projpush.json",
                """
                {
                  "id": "projpush",
                  "title": "提交推送",
                  "visible": true,
                  "side": "right",
                  "ratio": 0.24,
                  "controls": [
                    { "type": "text",   "id": "branch", "label": "分支名称" },
                    { "type": "text",   "id": "msg",    "label": "提交描述", "default": "一键推送更新", "required": true },
                    { "type": "button", "label": "提交到本地仓库", "command": "proj.commit name=\"{branch}\" msg=\"{msg}\"" },
                    { "type": "button", "label": "推送到 GitHub", "command": "proj.push name=\"{branch}\"" },
                    { "type": "button", "label": "一键提交全部工作树", "command": "proj.commitall msg=\"{msg}\"", "style": "danger" },
                    { "type": "button", "label": "一键推送全部分支", "command": "proj.pushall", "style": "danger" }
                  ]
                }
                """);
        }
        catch (Exception ex)
        {
            log.Error("app", $"面板初始化失败: {ex.Message}");
        }
    }

    private static void SeedPanelIfMissing(string panelsDir, ShellLog log, string fileName, string json)
    {
        var file = System.IO.Path.Combine(panelsDir, fileName);
        if (System.IO.File.Exists(file))
            return;
        System.IO.File.WriteAllText(file, json);
        log.Info("app", $"已写入面板 panels/{fileName}");
    }

    // ---------------------------------------------------------------- 演示数据

    private static readonly string[] DemoNames =
        ["张伟", "王芳", "李娜", "刘洋", "陈静", "杨磊", "赵敏", "黄强", "周杰", "吴丽"];

    private static readonly string[] DemoCities =
        ["北京", "上海", "广州", "深圳", "杭州", "成都", "武汉", "西安"];

    /// <summary>首次启动建 users 演示表(约 1200 行,覆盖 3 页,验收 4 / 9 用)。</summary>
    private static void SeedDemoData(SqliteDataService data, ShellLog log)
    {
        try
        {
            if (data.ListTables().Contains("users"))
                return;

            data.ExecuteSql(
                """
                CREATE TABLE users (
                    id    INTEGER PRIMARY KEY AUTOINCREMENT,
                    name  TEXT    NOT NULL,
                    city  TEXT,
                    age   INTEGER,
                    vip   INTEGER NOT NULL DEFAULT 0
                )
                """);

            var rng = new Random(42);
            var values = new List<string>();
            for (var i = 0; i < 1200; i++)
            {
                values.Add(
                    $"('{DemoNames[rng.Next(DemoNames.Length)]}{i:D4}'," +
                    $"'{DemoCities[rng.Next(DemoCities.Length)]}'," +
                    $"{rng.Next(18, 70)},{(rng.Next(10) == 0 ? 1 : 0)})");
                if (values.Count == 400)
                {
                    data.ExecuteSql($"INSERT INTO users (name, city, age, vip) VALUES {string.Join(",", values)}");
                    values.Clear();
                }
            }

            if (values.Count > 0)
                data.ExecuteSql($"INSERT INTO users (name, city, age, vip) VALUES {string.Join(",", values)}");

            log.Info("app", "已创建演示表 users(1200 行)");
        }
        catch (Exception ex)
        {
            log.Error("app", $"演示数据初始化失败: {ex.Message}");
        }
    }

    /// <summary>debug.seedbench:生成大表验证 10 万行分页流畅(N-04)。</summary>
    private static CommandDescriptor BuildSeedBenchCommand(SqliteDataService data) => new()
    {
        Name = "debug.seedbench",
        Summary = "生成 bench 大表(N-04 分页性能验证)",
        Example = "debug.seedbench rows=100000",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "rows",
                Description = "行数",
                Type = ParamType.Int,
                Default = "100000",
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var rows = Math.Clamp(ctx.GetInt("rows", 100_000), 1, 5_000_000);
            var sw = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                data.ExecuteSql("DROP TABLE IF EXISTS bench");
                data.ExecuteSql(
                    """
                    CREATE TABLE bench (
                        id      INTEGER PRIMARY KEY AUTOINCREMENT,
                        label   TEXT,
                        value   REAL,
                        stamp   TEXT
                    )
                    """);

                var rng = new Random(7);
                var values = new List<string>(1000);
                var inserted = 0;
                while (inserted < rows)
                {
                    values.Clear();
                    var batch = Math.Min(1000, rows - inserted);
                    for (var i = 0; i < batch; i++)
                    {
                        inserted++;
                        values.Add(
                            $"('条目-{inserted:D6}',{rng.NextDouble() * 1000:0.###}," +
                            $"'2026-{rng.Next(1, 13):D2}-{rng.Next(1, 29):D2}')");
                    }

                    data.ExecuteSql($"INSERT INTO bench (label, value, stamp) VALUES {string.Join(",", values)}");
                    if (inserted % 20_000 == 0)
                        ctx.Progress?.Report($"已写入 {inserted}/{rows} 行");
                }
            });

            return CommandResult.Ok($"bench 表已生成 {rows} 行,耗时 {sw.Elapsed.TotalSeconds:0.0}s");
        },
    };

    /// <summary>占位内容:说明该窗口的职责与到位里程碑。</summary>
    private static object Stub(string title, string body) => new Border
    {
        Padding = new Thickness(16),
        Background = Brushes.White,
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8),
                },
                new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    LineHeight = 20,
                },
            },
        },
    };
}
