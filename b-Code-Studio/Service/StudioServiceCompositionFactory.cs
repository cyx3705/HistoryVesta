using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Mcp;
using AppShell.ServiceHost;
using AppShell.Services;
using AppShell.Services.Mcp;
using AppShell.Services.Modules;
using AppShell.Services.Web;
using AppShell.Shell.Mcp;
using AppShell.Shell.Modules;
using OneHistoryStudio.Git;
using OneHistoryStudio.Connection;

namespace OneHistoryStudio.Service;

/// <summary>OneHistoryStudio 后台服务的唯一组合根。</summary>
public static class StudioServiceCompositionFactory
{
    public const int DefaultServicePort = 8738;

    public static ServiceComposition Create(bool registerAutostart, string? dataApplicationName = null)
    {
        AppIdentity.Use(typeof(StudioServiceCompositionFactory).Assembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(dataApplicationName ?? identity.Name);
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        if (string.IsNullOrWhiteSpace(settings.Get(WebGateway.KeyPort)))
        {
            settings.Set(
                WebGateway.KeyPort,
                DefaultServicePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        StartupMigrations.Run(settings, paths, log);

        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        var history = new HistoryRecorder(paths.Root, log);
        var projects = new ProjectService(
            settings,
            prompt => bus.Confirmation?.Confirm(prompt) == true,
            paths.Root);
        projects.EnsureDefaultSettings();
        projects.NotesProvider = history.AllNotes;
        var gitRules = new GitFileRuleService(projects);
        var branchHistory = new BranchHistoryService(projects);
        var formatInventory = new FormatInventoryService(projects, log, paths.Root);
        // 服务宿主承载自持窗口的模块界面(活动坞)。宿主不提供 ShellUi,停靠型模块据此自行弃权。
        var modules = new ModuleHost(paths.ModulesDir, log)
        {
            EnableUiModules = true,
        };
        var tools = new ToolSyncService(
            projects,
            paths.Root,
            log,
            () => modules.ModulesDirectory,
            () => registry.All().Select(command => command.Name));
        ServiceBuiltinCommands.RegisterAll(registry, settings, paths.Root);
        ModuleCommands.RegisterAll(registry, modules, settings);

        var prompts = new PromptGovernanceStore(paths.Root, log);
        var confirmation = new ServiceConfirmation();
        var mcp = new McpGateway(
            () => bus,
            settings,
            log,
            history,
            prompts,
            identity,
            confirmation.ConfirmRemote);
        McpCommands.RegisterAll(registry, () => bus, () => mcp, settings, prompts);

        var web = new WebGateway(() => bus, settings, log);
        var lanDevices = new LanDeviceStore(paths.Root);
        var lanPayloads = new LanMachinePayloadStore(paths.Root, identity.Version);
        var lanConfiguration = new LanConfigurationService(
            settings,
            web,
            new ElevatedLanMachineManager(
                Environment.ProcessPath ?? "OneHistoryStudio.exe",
                lanPayloads),
            identity.Version);
        web.ServerId = lanDevices.ServerId;
        web.DeviceAuthentication = lanDevices;
        web.DevicePairing = lanDevices;
        var lanDiscovery = new LanDiscoveryResponder(
            lanConfiguration,
            lanDevices,
            log,
            identity.Name);
        WebCommands.RegisterAll(registry, web, settings);
        LanCommands.RegisterAll(registry, lanDevices, web, lanConfiguration, lanDiscovery);

        ProjectCommands.RegisterAll(registry, projects, history);
        BranchHistoryCommands.RegisterAll(registry, branchHistory, history);
        GitRuleCommands.RegisterAll(registry, gitRules, formatInventory, projects);
        ToolCommands.RegisterAll(registry, tools);
        DebugCommands.RegisterAll(registry, log);

        McpExposurePolicy.ModuleOfCommand = commandName =>
        {
            var source = registry.GetSource(commandName);
            return source.StartsWith("module:", StringComparison.OrdinalIgnoreCase)
                ? source["module:".Length..]
                : null;
        };
        McpExposurePolicy.ModuleExposure = tools.GetExposure;

        return new ServiceComposition
        {
            ServiceName = identity.Name,
            Registry = registry,
            Bus = bus,
            Settings = settings,
            Log = log,
            Modules = modules,
            Mcp = mcp,
            Web = web,
            DeferredWork = [lanDiscovery],
            RegisterAutostartOnFirstRun = registerAutostart,
            Autostart = new WindowsRunAutostartManager(),
            DisposeApplicationServices = lanDiscovery.Dispose,
        };
    }
}
