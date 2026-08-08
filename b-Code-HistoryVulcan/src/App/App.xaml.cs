using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Input;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Input;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Services.Web;
using HistoryVulcan.Shell;

namespace HistoryVulcan.App;

/// <summary>
/// HistoryVulcan 独立演示宿主(§9):用于验证框架脱离 Janus 仍可构建和运行。
/// M2:控制台窗口由 Shell 提供真实实现;本层注册自定义指令示范
/// (debug.logflood,兼作验收 8 的承压测试入口)。
/// 控制面板与资源窗口由 Shell 提供，派生应用可继续注册自己的业务窗口。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;
    private ShellServiceClient? _serviceClient;
    private Mutex? _frontendMutex;

    private const string FrontendMutexName = "Local\\OneHistory.HistoryVulcan.Frontend";

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _frontendMutex = new Mutex(initiallyOwned: true, FrontendMutexName, out var created);
        if (!created)
        {
            ActivateExistingFrontend();
            Shutdown();
            return;
        }

        AppIdentity.Use(typeof(App).Assembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(identity.Name);
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;
        RemoveLegacyDemoPanel(paths, log);

        // N-05:全局未处理异常捕获 → 落日志并由 Shell 自动打开控制台,不弹错误框
        DispatcherUnhandledException += (_, args) =>
        {
            // AvalonDock can raise a stale visual-tree mouse-leave exception while
            // a pane template is replaced during focus/maximize. It is harmless
            // after the layout has been rebuilt, but must not look like an HistoryVulcan
            // fatal error in the console.
            if (IsTransientAvalonDockMouseLeave(args.Exception))
            {
                log.Log(ShellLogLevel.Debug, "shell.chrome", "Ignored transient AvalonDock mouse-leave exception during pane rebuild");
                args.Handled = true;
                return;
            }

            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常(非 UI 线程): {args.ExceptionObject}");

        var config = new ShellConfig
        {
            AppName = identity.Name,
            AppVersion = identity.Version,
            EnableModules = true,
            EnableUiModules = true,
            ModuleDirectory = ResolvePackagedModuleDirectory(paths.ModulesDir),
            EnableRemoteManagementViews = true,
            CloseBehavior = ShellCloseBehavior.Hide,
            // HistoryVulcan 独立宿主是模块生命周期的最终所有者；Janus 等产品只声明
            // 自己的业务窗口与模块，不再包装第二套 ModuleHost/ModulesView。
        };

        // 默认布局保留控制台底部停靠位置；业务页面由模块提供。
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "console",
            Title = "控制台",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.28,
            // 控制台内容由 Shell 提供(§4.4);此处只声明停靠位置
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.Modules,
            Title = "模块管理",
            DefaultSide = DockSide.Right,
            DefaultRatio = 0.32,
            // 内容由 Shell 的 ModulesView 接管；这里只声明独立宿主的默认位置。
        });
        // 派生应用自定义指令示范(§5.3):与内置指令同表、help 自动收录
        config.ConfigureCommands = registry =>
        {
            registry.Register(BuildLogFloodCommand(log));
        };

        var window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var executablePath = ResolveExecutablePath();
        var endpointPath = Path.Combine(paths.Root, "service", "endpoint.json");
        var service = ConnectToService(endpointPath, executablePath, log);
        if (service != null)
        {
            _serviceClient = service;
            service.LogReceived += (_, entry) => window.AddTransientLog(entry);
            service.ModuleRevisionReceived += revision =>
            {
                var modules = window.Modules;
                if (modules != null)
                    _ = Task.Run(modules.Reload);
            };
            window.Commands.RemoteExecutor = service.ExecuteAsync;
            window.Commands.ShouldUseRemoteCommand = (text, source) =>
                !text.TrimStart().StartsWith("app.frontend.", StringComparison.OrdinalIgnoreCase)
                && !source.Equals("Service:Relay", StringComparison.OrdinalIgnoreCase);
            _ = service.RunEventLoopAsync(window.Commands);
        }
        window.Show();

        log.Info("app", $"{identity.Name} {identity.Version} 启动完成,数据目录: {paths.Root}");

        // --exec "指令":启动后顺序执行(自动化/自测入口)
        var startupCommands = new List<string>();
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] != "--exec")
                continue;
            if (i + 1 >= e.Args.Length)
            {
                log.Warn("app", "启动参数 --exec 缺少后续指令，已忽略");
                break;
            }
            startupCommands.Add(e.Args[++i]);
        }

        if (e.Args.Any(arg => arg.Equals("--focus-console", StringComparison.OrdinalIgnoreCase)))
            startupCommands.Add("app.frontend.focus-console");

        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
    }

    private static async Task RunStartupCommandsAsync(ShellWindow window, List<string> commands)
    {
        foreach (var command in commands)
            await window.Commands.ExecuteAsync(command, "脚本:startup");
    }

    internal static ServiceComposition BuildServiceComposition(string executablePath)
    {
        AppIdentity.Use(typeof(App).Assembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(identity.Name);
        var servicePaths = new AppPaths(identity.Name, Path.Combine(paths.Root, "service"));
        var log = new ShellLog(servicePaths);
        var settings = new SettingsService(servicePaths);
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        var shortcuts = new GlobalShortcutService(bus, log);
        var moduleDirectory = ResolvePackagedModuleDirectory(
            settings.Get("module.dir") ?? paths.ModulesDir);
        var modules = new ModuleHost(moduleDirectory, log)
        {
            EnableCommands = true,
            EnableUiModules = false,
            GlobalShortcuts = shortcuts,
        };
        var web = new WebGateway(() => bus, settings, log)
        {
            ServerId = identity.Name + ".service",
        };
        long moduleRevision = 0;
        modules.ReloadCompleted += () =>
            web.PublishModuleRevision(Interlocked.Increment(ref moduleRevision));

        modules.Attach(registry, bus, settings, servicePaths.Root);
        RegisterServiceModuleCommands(registry, modules, settings);

        // Two physical presses on the slash key are intentionally non-suppressing.
        shortcuts.Register(new GlobalShortcutDescriptor(
            "focus-console",
            [
                new GlobalShortcutStroke(0xBF),
                new GlobalShortcutStroke(0xBF),
            ],
            "app.frontend.focus-console"),
            "framework");
        return new ServiceComposition
        {
            ServiceName = identity.Name + ".Backend",
            Registry = registry,
            Bus = bus,
            Settings = settings,
            Log = log,
            Modules = modules,
            GlobalShortcuts = shortcuts,
            Web = web,
            EndpointFile = Path.Combine(servicePaths.Root, "endpoint.json"),
            RegisterAutostartOnFirstRun = true,
            Autostart = new WindowsRunAutostartManager(),
        };
    }

    private static void RegisterServiceModuleCommands(
        CommandRegistry registry,
        ModuleHost host,
        SettingsService settings)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "module.list",
            Summary = "列出已加载模块",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
                CommandResult.Ok(
                    host.Modules.Count == 0
                        ? $"当前无已加载模块。模块目录: {host.ModulesDirectory}"
                        : string.Join('\n', host.Modules.Select(module =>
                            $"{module.ModuleName} {module.Version} ({module.CommandCount} 条指令)")),
                    host.Modules)),
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "module.reload",
            Summary = "重载全部后台模块",
            Handler = async _ =>
            {
                await Task.Run(host.Reload).ConfigureAwait(false);
                return CommandResult.Ok($"重载完成: {host.Modules.Count} 个模块");
            },
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "module.dir",
            Summary = "查看或切换后台模块目录",
            Parameters = [new ParameterSpec
            {
                Name = "path",
                Description = "模块目录绝对路径;省略时查看当前值",
                Position = 0,
            }],
            Handler = async ctx =>
            {
                var path = ctx.GetString("path");
                if (string.IsNullOrWhiteSpace(path))
                    return CommandResult.Ok($"当前模块目录: {host.ModulesDirectory}");
                path = Path.GetFullPath(path.Trim());
                settings.Set("module.dir", path);
                await Task.Run(() => host.ChangeDirectory(path)).ConfigureAwait(false);
                return CommandResult.Ok($"模块目录已切换并重载: {path}");
            },
        }, "framework:service");
    }

    private static string ResolvePackagedModuleDirectory(string fallback)
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Modules");
        return Directory.Exists(packaged) ? packaged : fallback;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceClient?.Dispose();
        _log?.Dispose(); // 冲刷文件写入队列
        try { _frontendMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _frontendMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool IsTransientAvalonDockMouseLeave(Exception exception)
    {
        if (exception is not NullReferenceException)
            return false;

        return exception.StackTrace?.Contains(
            "AvalonDock.Controls.AnchorablePaneTabPanel.OnMouseLeave",
            StringComparison.Ordinal) == true;
    }

    private static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetExtension(processPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return processPath;
        return Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
    }

    private static ShellServiceClient? ConnectToService(
        string endpointPath,
        string executablePath,
        IShellLog log)
    {
        try
        {
            var endpoint = ReadEndpoint(endpointPath);
            if (endpoint is { Port: > 0 })
            {
                var existing = CreateServiceClient(endpoint);
                if (existing.WaitForReadyAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult())
                    return existing;
                existing.Dispose();
            }

            endpoint = null;
            if (endpoint == null)
            {
                var start = new ProcessStartInfo(executablePath, "--service")
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(start);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                while (DateTime.UtcNow < deadline && (endpoint = ReadEndpoint(endpointPath)) == null)
                    Thread.Sleep(100);
            }

            if (endpoint == null || endpoint.Port <= 0)
            {
                log.Warn("service", "后台服务端点不可用，前端以本地模式继续运行");
                return null;
            }

            var client = CreateServiceClient(endpoint);
            if (!client.WaitForReadyAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult())
            {
                client.Dispose();
                log.Warn("service", "后台服务未在期限内就绪，前端以本地模式继续运行");
                return null;
            }
            return client;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            log.Warn("service", $"后台连接失败，前端以本地模式继续运行: {ex.Message}");
            return null;
        }
    }

    private static ShellServiceClient CreateServiceClient(ServiceEndpoint endpoint)
    {
        var profile = new ShellEndpointProfile(
            new Uri($"http://127.0.0.1:{endpoint.Port}/"),
            Guid.NewGuid().ToString("N"),
            ServerId: endpoint.ServerId,
            ConnectTimeout: TimeSpan.FromSeconds(5));
        return new ShellServiceClient(profile, "HistoryVulcan.Frontend");
    }

    private static ServiceEndpoint? ReadEndpoint(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ServiceEndpoint>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void ActivateExistingFrontend()
    {
        try
        {
            var current = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("HistoryVulcan"))
            {
                try
                {
                    if (process.Id == current || process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    ShowWindowAsync(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // A duplicate launch must never surface a dialog or crash the existing frontend.
        }
    }

    private sealed record ServiceEndpoint(int Port, string ServerId, int ProcessId);

    private static void RemoveLegacyDemoPanel(AppPaths paths, IShellLog log)
    {
        var legacyPanel = Path.Combine(paths.PanelsDir, "motor.json");
        try
        {
            if (!File.Exists(legacyPanel))
                return;
            File.Delete(legacyPanel);
            log.Info("migration", $"已删除旧版演示电机面板: {legacyPanel}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("migration", $"删除旧版演示电机面板失败: {ex.Message}");
        }
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

    // ---------------------------------------------------------------- 演示面板与指令(M4)

}
