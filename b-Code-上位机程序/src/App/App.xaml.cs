using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppShell.App.CoffeeMachine;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Services;
using AppShell.Shell;

namespace AppShell.App;

/// <summary>
/// 派生应用装配点(§9):本项目是派生应用唯一要改的项目。
/// M2:控制台窗口由 Shell 提供真实实现;本层注册自定义指令示范
/// (debug.logflood,兼作验收 8 的承压测试入口)。
/// 表窗口(M3)、控制面板与资源窗口(M4)的占位内容将逐步替换。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;
    private DeviceSerialService? _device;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths("ColdBrewMachine");
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;

        // N-05:全局未处理异常捕获 → 落日志 → 友好提示,不崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常: {args.Exception}");
            MessageBox.Show($"发生未处理异常,已记录日志:\n{args.Exception.Message}",
                "AppShell", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常(非 UI 线程): {args.ExceptionObject}");

        // 数据服务(§9 流程第 5 条):注册 main 连接并准备演示数据
        var dataService = new SqliteDataService(paths);
        dataService.RegisterConnection("main", "main.db");
        SeedDemoData(dataService, log);

        // 工作区(§9 流程第 6 条):根目录可经 res.root 指令更改并持久化
        var workspace = new WorkspaceService(
            settings.Get("workspace.root") ?? paths.WorkspaceDir);

        // 控制面板演示(§9 流程第 4 条):首启把 motor.json 写入 panels/ 目录
        var brewStore = new BrewPersistenceService(paths, log);
        SeedDefaultBrewRecipes(brewStore, log);
        var brew = new BrewMachineState(log, brewStore);
        var device = new DeviceSerialService(brew, log);
        brew.DeviceOutputRequested += (_, mode) => log.Info("device", device.SendBrewMode(mode));
        _device = device;

        var config = new ShellConfig
        {
            AppName = "咖啡冷萃机上位机",
            AppVersion = "0.4.0-M4",
            MainContent = new BrewOverviewView(brew),
            DataService = dataService,
            Workspace = workspace,
        };

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
            DefaultVisible = false,
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
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "brew-control",
            Title = "手动控制 / GPIO",
            DefaultSide = DockSide.Right,
            DefaultRatio = 0.26,
            ContentFactory = () => new BrewControlView(brew, device, brewStore),
        });
        // 控制窗口群:面板由 panels/*.json 声明(motor 演示面板经 SeedDemoPanel 写入),
        // 每个面板自动注册为独立可停靠窗口,无需在此声明

        // 派生应用自定义指令示范(§5.3):与内置指令同表、help 自动收录
        config.ConfigureCommands = registry =>
        {
            registry.Register(BuildLogFloodCommand(log));
            registry.Register(BuildSeedBenchCommand(dataService));
            RegisterBrewCommands(registry, brew, device, brewStore);
        };

        var window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;
        window.Show();

        log.Info("app", $"AppShell 启动完成,数据目录: {paths.Root}");

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

    private static void SeedDefaultBrewRecipes(BrewPersistenceService brewStore, ShellLog log)
    {
        try
        {
            if (brewStore.ReadRecipes().Count > 0)
                return;

            brewStore.SaveRecipe("默认冷萃", new BrewRecipe(30, 5, 30, 5, 3));
            brewStore.SaveRecipe("快速联调", new BrewRecipe(3, 1, 3, 1, 1));
            log.Info("brew", "已写入默认 M4 配方");
        }
        catch (Exception ex)
        {
            log.Warn("brew", $"默认配方初始化失败: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _device?.Dispose();
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

    private static void RegisterBrewCommands(
        CommandRegistry registry,
        BrewMachineState brew,
        DeviceSerialService device,
        BrewPersistenceService brewStore)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "brew.up",
            Summary = "咖啡冷萃机：手动上液",
            Example = "brew.up",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(ExecuteManual(brew, device, "up"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.down",
            Summary = "咖啡冷萃机：手动降液",
            Example = "brew.down",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(ExecuteManual(brew, device, "down"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.stop",
            Summary = "咖啡冷萃机：停止并关闭 GPIO15/GPIO16",
            Example = "brew.stop",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(ExecuteManual(brew, device, "stop"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "device.list",
            Summary = "列出可用串口",
            Example = "device.list",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var ports = device.ListPorts();
                return CommandResult.Ok(ports.Count == 0 ? "未发现串口" : string.Join("\n", ports));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "device.connect",
            Summary = "连接 ESP32 串口",
            Example = "device.connect port=COM5",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "port", Description = "串口名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx => CommandResult.Ok(device.Connect(ctx.RequireString("port")))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "device.disconnect",
            Summary = "断开 ESP32 串口",
            Example = "device.disconnect",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(device.Disconnect())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "device.mode",
            Summary = "切换模拟/设备模式",
            Example = "device.mode enabled=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "enabled", Description = "true=设备模式,false=模拟模式", Type = ParamType.Bool, Default = "true", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var enabled = ctx.GetBool("enabled", true);
                brew.SetDeviceMode(enabled);
                return CommandResult.Ok(enabled ? "已切换为设备模式" : "已切换为模拟模式");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "device.state",
            Summary = "请求 ESP32 状态快照",
            Example = "device.state",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(device.RequestState())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.run",
            Summary = "咖啡冷萃机：按固定循环编排运行",
            Example = "brew.run up=30 upHold=5 down=30 downHold=5 cycles=3",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "up", Description = "上液秒数", Type = ParamType.Int, Default = "30" },
                new ParameterSpec { Name = "upHold", Description = "上液停留秒数", Type = ParamType.Int, Default = "5" },
                new ParameterSpec { Name = "down", Description = "降液秒数", Type = ParamType.Int, Default = "30" },
                new ParameterSpec { Name = "downHold", Description = "降液停留秒数", Type = ParamType.Int, Default = "5" },
                new ParameterSpec { Name = "cycles", Description = "循环次数", Type = ParamType.Int, Default = "3" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var up = ctx.GetInt("up", 30);
                var upHold = ctx.GetInt("upHold", 5);
                var down = ctx.GetInt("down", 30);
                var downHold = ctx.GetInt("downHold", 5);
                var cycles = ctx.GetInt("cycles", 3);
                return BrewRecipe.TryCreate(up, upHold, down, downHold, cycles, out var recipe, out var error)
                    ? CommandResult.Ok(brew.StartSequence(recipe!))
                    : CommandResult.Fail(error);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "recipe.list",
            Summary = "列出已保存的冷萃配方",
            Example = "recipe.list",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var recipes = brewStore.ReadRecipes();
                return CommandResult.Ok(recipes.Count == 0
                    ? "暂无保存的配方"
                    : string.Join("\n", recipes.Select(p => p.Summary)));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "recipe.save",
            Summary = "保存命名冷萃配方",
            Example = "recipe.save name=浅烘测试 up=30 upHold=5 down=30 downHold=5 cycles=3",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "配方名称", Required = true, Position = 0 },
                new ParameterSpec { Name = "up", Description = "上液秒数", Type = ParamType.Int, Default = "30" },
                new ParameterSpec { Name = "upHold", Description = "上液停留秒数", Type = ParamType.Int, Default = "5" },
                new ParameterSpec { Name = "down", Description = "降液秒数", Type = ParamType.Int, Default = "30" },
                new ParameterSpec { Name = "downHold", Description = "降液停留秒数", Type = ParamType.Int, Default = "5" },
                new ParameterSpec { Name = "cycles", Description = "循环次数", Type = ParamType.Int, Default = "3" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var up = ctx.GetInt("up", 30);
                var upHold = ctx.GetInt("upHold", 5);
                var down = ctx.GetInt("down", 30);
                var downHold = ctx.GetInt("downHold", 5);
                var cycles = ctx.GetInt("cycles", 3);
                if (!BrewRecipe.TryCreate(up, upHold, down, downHold, cycles, out var recipe, out var error))
                    return CommandResult.Fail(error);

                try
                {
                    var preset = brewStore.SaveRecipe(ctx.RequireString("name"), recipe!);
                    return CommandResult.Ok($"已保存配方：{preset.Summary}");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "recipe.load",
            Summary = "查看已保存配方参数",
            Example = "recipe.load name=默认冷萃",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "配方名称", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                brewStore.TryGetRecipe(ctx.RequireString("name"), out var preset)
                    ? CommandResult.Ok(preset!.Summary, preset)
                    : CommandResult.Fail("未找到该配方")),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "recipe.delete",
            Summary = "删除已保存配方",
            Example = "recipe.delete name=默认冷萃",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "配方名称", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                brewStore.DeleteRecipe(ctx.RequireString("name"))
                    ? CommandResult.Ok("配方已删除")
                    : CommandResult.Fail("未找到该配方")),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "recipe.run",
            Summary = "按已保存配方启动固定循环",
            Example = "recipe.run name=默认冷萃",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "配方名称", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                brewStore.TryGetRecipe(ctx.RequireString("name"), out var preset)
                    ? CommandResult.Ok(brew.StartSequence(preset!.Recipe))
                    : CommandResult.Fail("未找到该配方")),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.history",
            Summary = "查看最近运行/GPIO/故障记录",
            Example = "brew.history limit=20",
            Parameters =
            [
                new ParameterSpec { Name = "limit", Description = "记录条数", Type = ParamType.Int, Default = "20", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var history = brewStore.ReadHistory(ctx.GetInt("limit", 20));
                return CommandResult.Ok(history.Count == 0
                    ? "暂无运行记录"
                    : string.Join("\n", history.Select(h => h.DisplayText)));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.pause",
            Summary = "咖啡冷萃机：暂停当前固定循环",
            Example = "brew.pause",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(brew.PauseSequence())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.resume",
            Summary = "咖啡冷萃机：继续当前固定循环",
            Example = "brew.resume",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(brew.ResumeSequence())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.abort",
            Summary = "咖啡冷萃机：终止当前固定循环并关闭输出",
            Example = "brew.abort",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(brew.AbortSequence())),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "brew.selftest",
            Summary = "咖啡冷萃机：快速切换互锁自检",
            Example = "brew.selftest count=100",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "count",
                    Description = "切换次数",
                    Type = ParamType.Int,
                    Default = "100",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var count = Math.Clamp(ctx.GetInt("count", 100), 1, 1000);
                for (var i = 0; i < count; i++)
                {
                    if (i % 2 == 0)
                        brew.StartUp();
                    else
                        brew.StartDown();

                    if (!brew.ValidateInterlock())
                    {
                        brew.Stop();
                        return CommandResult.Fail($"互锁自检失败：第 {i + 1} 次切换出现双路有效");
                    }
                }

                brew.Stop();
                return CommandResult.Ok($"互锁自检通过：连续切换 {count} 次，无 GPIO15/GPIO16 同时有效");
            }),
        });
    }

    private static string ExecuteManual(BrewMachineState brew, DeviceSerialService device, string mode)
    {
        if (brew.IsDeviceMode)
            return device.SendBrewMode(mode);

        return mode switch
        {
            "up" => brew.StartUp(),
            "down" => brew.StartDown(),
            _ => brew.Stop(),
        };
    }

    // ---------------------------------------------------------------- 演示面板与指令(M4)

    /// <summary>首启写入 motor 演示面板(§4.5 配置示例;验收 6 的载体)。</summary>
    private static void SeedDemoPanel(AppPaths paths, ShellLog log)
    {
        try
        {
            var panelsDir = System.IO.Path.Combine(paths.Root, "panels");
            System.IO.Directory.CreateDirectory(panelsDir);
            var file = System.IO.Path.Combine(panelsDir, "motor.json");
            if (System.IO.File.Exists(file))
                return;

            System.IO.File.WriteAllText(file,
                """
                {
                  "id": "motor",
                  "title": "电机控制",
                  "visible": true,
                  "side": "right",
                  "ratio": 0.22,
                  "controls": [
                    { "type": "combo",  "id": "axis",  "label": "轴",   "items": ["X", "Y", "Z"], "default": "X" },
                    { "type": "number", "id": "speed", "label": "速度", "min": 0, "max": 3000, "default": "100" },
                    { "type": "slider", "id": "accel", "label": "加速度", "min": 10, "max": 500, "step": 10, "default": "100" },
                    { "type": "check",  "id": "fine",  "label": "精细模式" },
                    { "type": "label",  "id": "status","label": "状态", "default": "就绪" },
                    { "type": "button", "label": "点动", "command": "motor.jog axis={axis} speed={speed} accel={accel} fine={fine}" },
                    { "type": "button", "label": "停止", "command": "motor.stop axis={axis}", "style": "danger" }
                  ]
                }
                """);
            log.Info("app", "已写入演示面板 panels/motor.json");
        }
        catch (Exception ex)
        {
            log.Error("app", $"演示面板初始化失败: {ex.Message}");
        }
    }

    /// <summary>motor.* 演示指令:模拟耗时动作 + panel.set 反向驱动状态灯(P-07 示范)。</summary>
    private static void RegisterMotorDemo(CommandRegistry registry, ShellLog log)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "motor.jog",
            Summary = "演示:模拟电机点动(异步长任务 + 进度上报)",
            Example = "motor.jog axis=X speed=200 accel=100 fine=false",
            Parameters =
            [
                new ParameterSpec { Name = "axis", Description = "轴", Required = true, Position = 0, AllowedValues = ["X", "Y", "Z"] },
                new ParameterSpec { Name = "speed", Description = "速度", Type = ParamType.Double, Default = "100" },
                new ParameterSpec { Name = "accel", Description = "加速度", Type = ParamType.Double, Default = "100" },
                new ParameterSpec { Name = "fine", Description = "精细模式", Type = ParamType.Bool, Default = "false" },
            ],
            Handler = async ctx =>
            {
                var axis = ctx.RequireString("axis");
                var speed = ctx.GetDouble("speed", 100);
                for (var pct = 25; pct <= 100; pct += 25)
                {
                    await Task.Delay(150);
                    ctx.Progress?.Report($"{pct}%");
                }

                return CommandResult.Ok($"{axis} 轴点动完成(speed={speed}, fine={ctx.GetBool("fine")})");
            },
        });

        registry.Register(new CommandDescriptor
        {
            Name = "motor.stop",
            Summary = "演示:停止电机",
            Example = "motor.stop axis=X",
            Parameters =
            [
                new ParameterSpec { Name = "axis", Description = "轴", Required = true, Position = 0, AllowedValues = ["X", "Y", "Z"] },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                CommandResult.Ok($"{ctx.RequireString("axis")} 轴已停止")),
        });
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
