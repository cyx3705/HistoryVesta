using System.Windows;
using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Mcp;
using AppShell.Services;
using AppShell.Shell;
using OneHistoryStudio.Git;

namespace OneHistoryStudio;

/// <summary>
/// OneHistoryStudio 装配点；V2.4.0 起直接引用 020 伞形项目中的 AppShell 唯一源码。
///
/// 模板 0.4.4 起,**模块托管与 MCP 网关(含提示词治理)已由框架自带并默认启用**,
/// 本装配点不再自行创建它们——只组装应用专有的六件:数据服务、操作留痕、项目库、
/// Git 文件规则、格式台账、自扩展飞轮,并登记应用专有的 MCP 只读指令。
/// 一次性升级动作统一走 StartupMigrations。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 身份取本程序集而非入口程序集:冒烟宿主(Smoke.exe)加载本程序集时,
        // 应用身份仍须是 OneHistoryStudio 本身。
        AppIdentity.Use(typeof(App).Assembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(identity.Name);
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;

        RegisterGlobalExceptionHandlers(log, identity.Name);

        // 数据服务(§9 流程第 5 条)
        var dataService = new SqliteDataService(paths);
        dataService.RegisterConnection("main", "main.db");

        // 一次性迁移(QC-03):须在留痕/提示词存储接触 main 库之前执行
        StartupMigrations.Run(settings, paths, dataService, log);

        // 操作留痕(提示词治理存储自 0.4.4 起由框架自带)
        var history = new HistoryRecorder(dataService, log);

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
        var gitRules = new GitFileRuleService(projects);
        var branchHistory = new BranchHistoryService(projects);

        // V2.2.1:文件格式全覆盖台账(按格式聚合 + 缓存增量,详见 versions\20)
        var formatInventory = new FormatInventoryService(projects, log, paths.Root);

        // 工作区(§9 流程第 6 条 / DT-04):默认根 = 项目库根目录,可经 res.root 更改并持久化
        var workspace = new WorkspaceService(
            settings.Get(WorkspaceService.KeyRoot) ?? projects.WorktreeRoot);

        // 自扩展飞轮(V2.2):清单发现/同步/溯源;槽路径跟随框架模块宿主的 module.dir 现值。
        // 0.4.4:模块宿主由 ShellWindow 自建,故以委托延迟取用——命令执行时窗口必已就绪。
        var defaultModulesDir = paths.ModulesDir;
        var panelsDir = paths.PanelsDir;
        var tools = new ToolSyncService(projects, dataService, log,
            () => window?.Modules?.ModulesDirectory ?? defaultModulesDir);

        var projectSelection = new Views.ProjectSelectionState();
        // 命令集选中状态由本应用创建:框架的命令集窗口(mcp)与本应用的指令详情窗口(commanddetail)
        // 共享同一实例;窗口工厂在 DockingHost 构建布局时即被调用,不能延迟到 window 就绪再取。
        var commandSelection = new CommandSelectionState();

        var config = new ShellConfig
        {
            AppName = identity.Name,
            AppVersion = identity.Version,
            DataService = dataService,
            Workspace = workspace,
            // 身份显式提供:冒烟宿主下入口程序集不是本应用
            Identity = identity,
            // 本应用已有留痕器(同时承载 push_history / branch_notes),
            // 接进框架复用同一张 mcp_history,避免框架再建一个并行的记录器
            McpAuditLog = history,
            // 命令集窗口与指令详情窗口的联动实例,交框架的 McpToolsView 使用
            CommandSelection = commandSelection,
            // 中央区不注入内容,保留模板占位页(总览/继承树改为独立工具窗口)
        };

        RegisterToolWindows(config, () => window?.Commands, projectSelection, projects);

        // 派生应用自定义指令示范(§5.3):与内置指令同表、help 自动收录
        config.ConfigureCommands = registry =>
        {
            ProjectCommands.RegisterAll(registry, projects, history);
            BranchHistoryCommands.RegisterAll(registry, branchHistory, history);
            GitRuleCommands.RegisterAll(registry, gitRules, formatInventory, projects);
            ToolCommands.RegisterAll(registry, tools);
            DebugCommands.RegisterAll(registry, log);

            var unreservedDomains = ToolManifestLoader.FindUnreservedBuiltinDomains(
                registry.All().Select(command => command.Name));
            if (unreservedDomains.Count > 0)
            {
                log.Warn("tool", "内置指令域未纳入模块名保留清单: " +
                                 string.Join(", ", unreservedDomains));
            }

            // 0.4.4:module.* / mcp.* / command.* / prompt.* 等已由框架在此之前注册完毕。
            // V2.4.4:本应用不再登记只读指令名单——每条命令在自己的注册处用
            // Readonly = true 自描述,新增只读指令只需改注册点一处。
        };

        window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;

        StartupMigrations.InitCommandDetailLayoutOnce(window, settings, log);

        // MD-08:模块热重载后同步模块旁面板;有变化时经总线 panel.reload
        // (既有面板原地刷新即时生效;全新面板按框架 P-08 约定重启后出现,提示见控制台)
        var capturedWindow = () => window;
        if (window.Modules is { } moduleHost)
        {
            moduleHost.ReloadCompleted += () =>
            {
                if (AppShell.Services.Modules.ModulePanelSync.SyncFiles(
                        moduleHost.ModulesDirectory, panelsDir, log))
                    _ = capturedWindow()?.Commands.ExecuteAsync("panel.reload", "模块面板");
            };
        }

        WireMcpExposurePolicy(capturedWindow, tools);

        // 0.4.4:模块宿主的 Attach/Start、确认服务包装(CX-03)、MCP 网关创建与自启动
        // 均已由 ShellWindow 完成,本装配点不再重复。

        window.Show();

        // --yes:确认通道自动通过(自动化回归/脚本用,IConfirmationService 注释预留的场景)。
        // 仅限 --exec 自测流程使用,日常交互禁止带此参数。
        if (e.Args.Contains("--yes"))
        {
            // 仍经 GatewayAwareConfirmation 包装:自动确认只对 UI/手动/脚本生效,MCP 危险调用不受其影响
            window.Commands.Confirmation = new GatewayAwareConfirmation(new AutoConfirmation());
            log.Warn("app", "--yes 已启用:UI/手动/脚本二次确认自动通过(MCP 危险调用仍走中继/拒绝)");
        }

        log.Info("app", $"{identity.Name} {identity.Version} 启动完成,数据目录: {paths.Root}");

        // --exec "指令":启动后顺序执行(自动化/自测入口)
        var startupCommands = CollectExecCommands(e.Args);
        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
    }

    /// <summary>
    /// 工具窗口注册(表驱动)。顶部标签组 = 项目总览系五页(0.55);右侧 = 指令详情/项目操作;
    /// 资源/表/控制台内容由 Shell 提供(Workspace/DataService/§4.4),只声明停靠位置;
    /// 控制窗口群面板由 panels/*.json 声明,无需在此登记。
    /// commanddetail 的 V2.1.5 默认位由 StartupMigrations.InitCommandDetailLayoutOnce 二次调整。
    /// V2.3.3 QC-07:本表自装配点外移,OnStartup 回到 ≤150 行预算内。
    /// </summary>
    private static void RegisterToolWindows(
        ShellConfig config,
        Func<CommandBus?> busAccessor,
        Views.ProjectSelectionState projectSelection,
        ProjectService projects)
    {
        // mcp / commanddetail / modules 三窗口内容由框架接管(工厂 null),故本表不再需要 commandSelection。
        // 联动实例经 ShellConfig.CommandSelection 传给框架(见上方 config 初始化)。
        (string Id, string Title, DockSide Side, string? Target, double Ratio, Func<object>? Factory)[] toolWindows =
        [
            ("overview", "项目总览", DockSide.Top, null, 0.55, () => new Views.OverviewView(busAccessor, projectSelection)),
            ("tree", "继承树", DockSide.Tab, "overview", 0.55, () => new Views.BranchTreeView(busAccessor, projectSelection)),
            // mcp / commanddetail / modules 窗口内容自 0.4.4 起由框架 TakeOverDescriptor 注入(工厂 null);
            // 这里仅声明它们在本应用的停靠位,框架保留该布局。
            ("mcp", "命令集", DockSide.Tab, "overview", 0.55, null),
            ("commanddetail", "指令详情", DockSide.Right, null, 0.32, null),
            ("modules", "模块管理", DockSide.Tab, "overview", 0.55, null),
            ("meta", "Meta文件", DockSide.Tab, "overview", 0.55, () => new Views.MetaView(busAccessor)),
            ("projops", "项目操作", DockSide.Right, null, 0.28, () => new Views.ProjectOperationsView(busAccessor, projectSelection)),
            ("history", "分支历史", DockSide.Left, null, 0.26, () => new Views.BranchHistoryView(busAccessor, projectSelection, projects)),
            ("resource", "资源窗口", DockSide.Left, null, 0.18, null),
            ("table", "表窗口", DockSide.Bottom, null, 0.28, null),
            ("console", "控制台", DockSide.Tab, "table", 0.28, null),
        ];
        foreach (var w in toolWindows)
        {
            config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = w.Id,
                Title = w.Title,
                DefaultSide = w.Side,
                DefaultTabTarget = w.Target,
                DefaultRatio = w.Ratio,
                ContentFactory = w.Factory,
            });
        }
    }

    /// <summary>
    /// mcpExposure 档位接线(V2.2 CX-01/Q211-2):策略层经注册来源识别模块指令,
    /// 再查溯源表取清单声明档;根平铺模块无记录 → null = standard 现状。
    /// </summary>
    private static void WireMcpExposurePolicy(Func<ShellWindow?> window, ToolSyncService tools)
    {
        McpExposurePolicy.ModuleOfCommand = commandName =>
        {
            var src = window()?.Commands.Registry.GetSource(commandName);
            return src != null && src.StartsWith("module:", StringComparison.OrdinalIgnoreCase)
                ? src["module:".Length..]
                : null;
        };
        McpExposurePolicy.ModuleExposure = tools.GetExposure;
    }

    /// <summary>N-05:全局未处理异常捕获 → 落日志 → 友好提示,不崩溃。</summary>
    private void RegisterGlobalExceptionHandlers(ShellLog log, string appName)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常: {args.Exception}");
            MessageBox.Show($"发生未处理异常,已记录日志:\n{args.Exception.Message}",
                appName, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常(非 UI 线程): {args.ExceptionObject}");
    }

    private static List<string> CollectExecCommands(string[] args)
    {
        var commands = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--exec")
                commands.Add(args[++i]);
        }

        return commands;
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
        // 0.4.4:MCP 网关与模块宿主由 ShellWindow 自建、并在 Closing 时自行释放,
        // 此处只收尾本应用自己创建的东西。
        _log?.Dispose(); // 冲刷文件写入队列
        base.OnExit(e);
    }

}
