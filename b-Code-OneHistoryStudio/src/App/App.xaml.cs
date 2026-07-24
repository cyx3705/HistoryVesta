using System.Windows;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Services;
using AppShell.Shell;
using OneHistoryStudio.Git;

namespace OneHistoryStudio;

/// <summary>
/// OneHistoryStudio 装配点(派生自 z-APPShell,基线随 docs/TEMPLATE_VERSION.md):
/// 组装数据/留痕/提示词治理/项目库/模块托管/MCP 网关六大件,
/// 注册全部工具窗口与指令域;一次性升级动作统一走 StartupMigrations。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;
    private Modules.ModuleHost? _modules;
    private Mcp.McpGateway? _mcp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // 操作留痕与提示词治理存储
        var history = new HistoryRecorder(dataService, log);
        var prompts = new Mcp.PromptGovernanceStore(dataService, log);

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

        // V2.2.1:文件格式全覆盖台账(按格式聚合 + 缓存增量,详见 docs\20)
        var formatInventory = new FormatInventoryService(projects, log, paths.Root);

        // 工作区(§9 流程第 6 条 / DT-04):默认根 = 项目库根目录,可经 res.root 更改并持久化
        var workspace = new WorkspaceService(
            settings.Get("workspace.root") ?? projects.WorktreeRoot);

        // 模块托管(V2-M3,MD-01~07):Modules 目录热重载,DLL 即指令域
        var modulesDir = settings.Get(Modules.ModuleCommands.KeyModuleDir)
                         ?? System.IO.Path.Combine(paths.Root, "Modules");
        var moduleHost = new Modules.ModuleHost(modulesDir, log);
        _modules = moduleHost;

        // 自扩展飞轮(V2.2):清单发现/同步/溯源;槽路径跟随 module.dir 现值
        var tools = new ToolSyncService(projects, dataService, log, () => moduleHost.ModulesDirectory);

        // MD-08(V21-M4):窗口创建前先做一次文件级面板同步,
        // 上一会话遗留/预先放置的模块旁面板本次启动即成窗口
        var panelsDir = System.IO.Path.Combine(paths.Root, "panels");
        Modules.ModulePanelSync.SyncFiles(modulesDir, panelsDir, log);
        var commandSelection = new Views.CommandSelectionState();
        var projectSelection = new Views.ProjectSelectionState();

        var config = new ShellConfig
        {
            AppName = identity.Name,
            AppVersion = identity.Version,
            DataService = dataService,
            Workspace = workspace,
            // 中央区不注入内容,保留模板占位页(总览/继承树改为独立工具窗口)
        };

        // 工具窗口注册(表驱动):顶部标签组 = 项目总览系五页(0.55);右侧 = 指令详情/项目操作;
        // 资源/表/控制台内容由 Shell 提供(Workspace/DataService/§4.4),只声明停靠位置;
        // 控制窗口群面板由 panels/*.json 声明,无需在此登记。
        // commanddetail 的 V2.1.5 默认位由 StartupMigrations.InitCommandDetailLayoutOnce 二次调整。
        var busAccessor = () => window?.Commands;
        (string Id, string Title, DockSide Side, string? Target, double Ratio, Func<object>? Factory)[] toolWindows =
        [
            ("overview", "项目总览", DockSide.Top, null, 0.55, () => new Views.OverviewView(busAccessor, projectSelection)),
            ("tree", "继承树", DockSide.Tab, "overview", 0.55, () => new Views.BranchTreeView(busAccessor, projectSelection)),
            ("mcp", "命令集", DockSide.Tab, "overview", 0.55, () => new Views.McpToolsView(busAccessor, commandSelection)),
            ("commanddetail", "指令详情", DockSide.Right, null, 0.32, () => new Views.CommandDetailView(busAccessor, commandSelection)),
            ("modules", "模块管理", DockSide.Tab, "overview", 0.55, () => new Views.ModulesView(busAccessor)),
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

        // 派生应用自定义指令示范(§5.3):与内置指令同表、help 自动收录
        config.ConfigureCommands = registry =>
        {
            ProjectCommands.RegisterAll(registry, projects, history);
            BranchHistoryCommands.RegisterAll(registry, branchHistory, history);
            GitRuleCommands.RegisterAll(registry, gitRules, formatInventory, projects);
            ToolCommands.RegisterAll(registry, tools);
            Modules.ModuleCommands.RegisterAll(registry, moduleHost, settings);
            // V2.1:元数据自描述层、网关生命周期与 V2.1.2 提示词治理；
            // 网关实例在 window 之后创建
            Mcp.McpCommands.RegisterAll(registry, () => window?.Commands, () => _mcp, settings, prompts);
            DebugCommands.RegisterAll(registry, log);
        };

        window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;

        StartupMigrations.InitCommandDetailLayoutOnce(window, settings, log);

        // MD-08:模块热重载后同步模块旁面板;有变化时经总线 panel.reload
        // (既有面板原地刷新即时生效;全新面板按框架 P-08 约定重启后出现,提示见控制台)
        var capturedWindow = () => window;
        moduleHost.ReloadCompleted += () =>
        {
            if (Modules.ModulePanelSync.SyncFiles(moduleHost.ModulesDirectory, panelsDir, log))
                _ = capturedWindow()?.Commands.ExecuteAsync("panel.reload", "模块面板");
        };

        // mcpExposure 档位接线(V2.2 CX-01/Q211-2):策略层经注册来源识别模块指令,
        // 再查溯源表取清单声明档;根平铺模块无记录 → null = standard 现状
        Mcp.McpExposurePolicy.ModuleOfCommand = commandName =>
        {
            var src = window?.Commands.Registry.GetSource(commandName);
            return src != null && src.StartsWith("module:", StringComparison.OrdinalIgnoreCase)
                ? src["module:".Length..]
                : null;
        };
        Mcp.McpExposurePolicy.ModuleExposure = tools.GetExposure;

        // 模块宿主接入注册表并首次装载(此刻在 UI 线程,Dispatcher.Invoke 内联执行)
        moduleHost.Attach(window.Commands.Registry);
        moduleHost.Start();

        // 确认服务包装(V2.2 CX-03):MCP 中继预批准的执行直接放行,其余走 Shell 交互弹框;
        // MCP 危险调用的确认由网关独立完成,不经此路的自动确认,故 --yes 对 MCP 不生效
        window.Commands.Confirmation = new Mcp.GatewayAwareConfirmation(window.Commands.Confirmation);

        // MCP 网关(V21-M2/V2.2):默认关闭(MS-01),mcp.start 显式开启;mcp.autostart=true 随宿主启动。
        // remoteConfirm = 宿主确认中继对话框(CX-02):host 档下远程危险请求由人裁决,始终真实弹框
        _mcp = new Mcp.McpGateway(() => window?.Commands, settings, log, history, prompts, identity,
            (client, prompt, timeout) => Mcp.RemoteConfirmDialog.Ask(window, prompt, timeout));
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
            // 仍经 GatewayAwareConfirmation 包装:自动确认只对 UI/手动/脚本生效,MCP 危险调用不受其影响
            window.Commands.Confirmation = new Mcp.GatewayAwareConfirmation(new AutoConfirmation());
            log.Warn("app", "--yes 已启用:UI/手动/脚本二次确认自动通过(MCP 危险调用仍走中继/拒绝)");
        }

        log.Info("app", $"{identity.Name} {identity.Version} 启动完成,数据目录: {paths.Root}");

        // --exec "指令":启动后顺序执行(自动化/自测入口)
        var startupCommands = CollectExecCommands(e.Args);
        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
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
        _mcp?.Dispose(); // 停监听、释放端口
        _modules?.Dispose(); // 停掉文件监听与防抖定时器
        _log?.Dispose(); // 冲刷文件写入队列
        base.OnExit(e);
    }

}
