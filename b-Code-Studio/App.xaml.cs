using System.Diagnostics;
using System.IO;
using System.Windows;
using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Files;
using AppShell.Core.Logging;
using AppShell.Core.Mcp;
using AppShell.Services;
using AppShell.Services.Web;
using AppShell.Shell;
using OneHistoryStudio.Connection;
using OneHistoryStudio.Git;
using OneHistoryStudio.Service;

namespace OneHistoryStudio;

/// <summary>
/// OneHistoryStudio 桌面装配点。AppShell 由固定版本包提供；业务命令、模块、MCP 与 Web
/// 统一由常驻的 OneHistoryStudio.exe --service-host 组合，桌面只装配视图、连接能力和前端命令代理。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;
    private ShellServiceClient? _serviceClient;
    private CancellationTokenSource? _serviceEvents;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppIdentity.Use(typeof(App).Assembly);
        var identity = AppIdentity.Current;
        var bootstrapStore = new BootstrapProfileStore(identity.Name);
        var bootstrap = bootstrapStore.Load();
        var isServer = bootstrap.Role == NodeRole.Server;
        var paths = new AppPaths(identity.Name, createBusinessDirectories: isServer);
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;
        RegisterGlobalExceptionHandlers(log, identity.Name);

        var webPort = settings.GetInt(
            WebGateway.KeyPort, StudioServiceCompositionFactory.DefaultServicePort);
        var endpoint = BootstrapProfileStore.ResolveEndpoint(bootstrap, webPort);
        var secrets = new DpapiSecretStore(paths.Root);
        _serviceClient = CreateServiceClient(
            bootstrapStore, secrets, bootstrap, endpoint, identity.Name);
        var serviceReady = EnsureServiceAsync(_serviceClient, log, isServer).GetAwaiter().GetResult();
        if (!isServer && !serviceReady
            && ConnectionProfileService.RefreshSavedEndpointAsync(bootstrapStore)
                .GetAwaiter().GetResult())
        {
            _serviceClient.Dispose();
            bootstrap = bootstrapStore.Load();
            endpoint = BootstrapProfileStore.ResolveEndpoint(bootstrap, webPort);
            _serviceClient = CreateServiceClient(
                bootstrapStore, secrets, bootstrap, endpoint, identity.Name);
            serviceReady = EnsureServiceAsync(_serviceClient, log, allowLocalStart: false)
                .GetAwaiter().GetResult();
        }
        var connectionProfiles = new ConnectionProfileService(
            bootstrapStore, secrets, _serviceClient);

        ShellWindow? window = null;
        ProjectService? projects = null;
        IWorkspaceService workspace;
        if (isServer)
        {
            projects = new ProjectService(settings, prompt =>
            {
                var confirmation = window?.Commands.Confirmation;
                if (confirmation == null)
                    return false;
                return Current.Dispatcher.Invoke(() => confirmation.Confirm(prompt));
            }, paths.Root);
            workspace = new WorkspaceService(
                settings.Get(WorkspaceService.KeyRoot) ?? projects.WorktreeRoot);
        }
        else
        {
            workspace = new RemoteWorkspaceService(_serviceClient);
        }
        var projectSelection = new Views.ProjectSelectionState();
        var commandSelection = new CommandSelectionState();
        var config = new ShellConfig
        {
            AppName = identity.Name,
            AppVersion = identity.Version,
            Workspace = workspace,
            Identity = identity,
            CommandSelection = commandSelection,
            EnableMcp = false,
            EnableModules = false,
            EnableUiModules = isServer,
            EnableRemoteManagementViews = true,
            ConfigureCommands = registry =>
                ConnectionCommands.RegisterAll(registry, connectionProfiles),
        };
        RegisterToolWindows(config, () => window?.Commands, projectSelection,
            branch => projects?.IsProtected(branch) == true,
            connectionProfiles);
        config.ToolMenuActions.Add(new ShellMenuAction(
            "连接与端口(_C)", "win.show name=connection.settings"));
        config.ToolMenuActions.Add(new ShellMenuAction(
            "GitHub 账号(_G)", "win.show name=github.account"));
        window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;
        window.Commands.RemoteExecutor = async (text, source, cancellation) =>
        {
            var result = await _serviceClient.ExecuteAsync(text, source, cancellation);
            return !isServer && !result.Success
                   && result.Message.Contains("HTTP 403", StringComparison.Ordinal)
                ? CommandResult.Fail("当前客户端为只读，等待服务器授权")
                : result;
        };
        window.Commands.ShouldUseRemote = source =>
            !source.StartsWith("Service:", StringComparison.OrdinalIgnoreCase);
        window.Commands.ShouldUseRemoteCommand = (text, source) =>
            !source.StartsWith("Service:", StringComparison.OrdinalIgnoreCase)
            && !IsConnectionCommand(text);
        _serviceEvents = new CancellationTokenSource();
        _ = _serviceClient.RunEventLoopAsync(window.Commands, _serviceEvents.Token);
        window.Show();
        if (serviceReady)
            log.Info("app", $"{identity.Name} {identity.Version} [{bootstrap.Role}] 已连接服务: {endpoint}");
        else
            log.Warn("app", $"{identity.Name} {identity.Version} [{bootstrap.Role}] 服务未连接: {endpoint}");
        var startupCommands = CollectExecCommands(e.Args);
        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
    }

    private static ShellServiceClient CreateServiceClient(
        BootstrapProfileStore bootstrapStore,
        DpapiSecretStore secrets,
        BootstrapProfile bootstrap,
        Uri endpoint,
        string applicationName)
    {
        var client = new ShellServiceClient(new ShellEndpointProfile(
            endpoint,
            bootstrap.DeviceId,
            () =>
            {
                var current = bootstrapStore.Load();
                return current.ServerId == null ? null : secrets.Read(current.ServerId);
            },
            bootstrap.CertificateFingerprint,
            TimeSpan.FromSeconds(5),
            bootstrap.ServerId), applicationName);
        client.DataDeserializer = Service.StudioCommandDataDeserializer.Deserialize;
        return client;
    }

    private static async Task<bool> EnsureServiceAsync(
        ShellServiceClient client,
        IShellLog log,
        bool allowLocalStart)
    {
        if (await client.WaitForReadyAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
            return true;

        if (!allowLocalStart)
            return false;

        var servicePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(servicePath))
        {
            log.Error("app", "无法确定 OneHistoryStudio.exe 路径");
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(servicePath) { UseShellExecute = true };
            start.ArgumentList.Add("--service-host");
            Process.Start(start);
        }
        catch (Exception ex)
        {
            log.Error("app", $"启动后台服务失败: {ex.Message}");
            return false;
        }

        return await client.WaitForReadyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    /// <summary>
    /// 工具窗口注册(表驱动)。顶部标签组 = 项目总览系五页(0.55);右侧 = 指令详情/项目操作;
    /// 资源/表/控制台内容由 Shell 提供(Workspace/DataService/§4.4),只声明停靠位置;
    /// 控制窗口群面板由 panels/*.json 声明,无需在此登记。
    /// commanddetail 的默认位由 StartupMigrations.InitCommandDetailLayoutOnce 二次调整。
    /// 描述符集中维护，避免把窗口装配细节重新塞回 OnStartup。
    /// </summary>
    private static void RegisterToolWindows(
        ShellConfig config,
        Func<CommandBus?> busAccessor,
        Views.ProjectSelectionState projectSelection,
        Func<string, bool> isProtected,
        ConnectionProfileService connectionProfiles)
    {
        // mcp / commanddetail / modules 三窗口内容由框架接管(工厂 null),故本表不再需要 commandSelection。
        // 联动实例经 ShellConfig.CommandSelection 传给框架(见上方 config 初始化)。
        (string Id, string Title, DockSide Side, string? Target, double Ratio, Func<object>? Factory)[] toolWindows =
        [
            ("overview", "项目总览", DockSide.Top, null, 0.55, () => new Views.OverviewView(busAccessor, projectSelection)),
            ("tree", "继承树", DockSide.Tab, "overview", 0.55, () => new Views.BranchTreeView(busAccessor, projectSelection)),
            // mcp / commanddetail / modules 窗口内容由框架 TakeOverDescriptor 注入（工厂为 null）；
            // 这里仅声明它们在本应用的停靠位,框架保留该布局。
            (StandardWindowIds.Mcp, "命令集", DockSide.Tab, "overview", 0.55, null),
            (StandardWindowIds.CommandDetail, "指令详情", DockSide.Right, null, 0.32, null),
            (StandardWindowIds.Modules, "模块管理", DockSide.Tab, "overview", 0.55, null),
            ("meta", "Meta文件", DockSide.Tab, "overview", 0.55, () => new Views.MetaView(busAccessor)),
            ("projops", "项目操作", DockSide.Right, null, 0.28, () => new Views.ProjectOperationsView(busAccessor, projectSelection)),
            ("history", "分支历史", DockSide.Left, null, 0.26,
                () => new Views.BranchHistoryView(busAccessor, projectSelection, isProtected)),
            (StandardWindowIds.Resource, "资源窗口", DockSide.Left, null, 0.18, null),
            (StandardWindowIds.Console, "控制台", DockSide.Bottom, null, 0.28, null),
            ("github.account", "GitHub 账号", DockSide.Right, null, 0.38,
                () => new Views.GitHubAccountView(busAccessor)),
            ("connection.settings", "连接与端口", DockSide.Right, null, 0.38,
                () => new Views.ConnectionSettingsView(connectionProfiles, busAccessor)),
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
    /// mcpExposure 档位接线：策略层经注册来源识别模块指令，
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

    /// <summary>全局未处理异常捕获：落日志并给出友好提示。</summary>
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

    private static bool IsConnectionCommand(string text)
    {
        try
        {
            return CommandParser.Parse(text).Name.StartsWith(
                "conn.", StringComparison.OrdinalIgnoreCase);
        }
        catch (CommandSyntaxException)
        {
            return false;
        }
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
        _serviceEvents?.Cancel();
        _serviceEvents?.Dispose();
        _serviceClient?.Dispose();
        _log?.Dispose(); // 冲刷文件写入队列
        base.OnExit(e);
    }

}
