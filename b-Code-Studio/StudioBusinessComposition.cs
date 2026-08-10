using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryJanus.Git;
using HistoryJanus.GitHub;

namespace HistoryJanus;

/// <summary>
/// Janus business services registered into a host-owned command bus.
/// This boundary deliberately owns no process, window, module loader, MCP, Web, or LAN lifetime.
/// </summary>
public sealed class StudioBusinessComposition
{
    internal StudioBusinessComposition(
        ProjectService projects,
        HistoryRecorder history,
        GitFileRuleService gitRules,
        BranchHistoryService branchHistory,
        FormatInventoryService formatInventory,
        GitHubConnectionService gitHub)
    {
        Projects = projects;
        History = history;
        GitRules = gitRules;
        BranchHistory = branchHistory;
        FormatInventory = formatInventory;
        GitHub = gitHub;
    }

    public ProjectService Projects { get; }

    public HistoryRecorder History { get; }

    public GitFileRuleService GitRules { get; }

    public BranchHistoryService BranchHistory { get; }

    public FormatInventoryService FormatInventory { get; }

    public GitHubConnectionService GitHub { get; }
}

/// <summary>
/// Builds the Janus domain graph inside infrastructure supplied by the AppShell host.
/// </summary>
public static class StudioBusinessCompositionFactory
{
    public static StudioBusinessComposition Register(
        CommandRegistry registry,
        CommandBus bus,
        ISettingsService settings,
        IShellLog log,
        string dataDirectory,
        string commandSource = "app")
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandSource);

        var history = new HistoryRecorder(dataDirectory, log);
        var projects = new ProjectService(
            settings,
            prompt => bus.Confirmation?.Confirm(prompt) == true,
            dataDirectory);
        projects.EnsureDefaultSettings();
        projects.NotesProvider = history.AllNotes;

        var gitRules = new GitFileRuleService(projects);
        var branchHistory = new BranchHistoryService(projects);
        var formatInventory = new FormatInventoryService(projects, log, dataDirectory);
        // GitHub 事实读取与项目库共用同一 proj.barerepo 配置源，现读现生效
        var gitHub = new GitHubConnectionService(() => projects.BareRepo);

        ProjectCommands.RegisterAll(registry, projects, history, commandSource);
        BranchHistoryCommands.RegisterAll(registry, branchHistory, history, commandSource);
        GitRuleCommands.RegisterAll(registry, gitRules, formatInventory, projects, commandSource);
        GitHubCommands.RegisterAll(registry, gitHub, commandSource);
        // 业务模块不注册诊断或自动化辅助指令：日志承压由宿主 vulcan.log.flood 承担，
        // 不在此重复实现。

        return new StudioBusinessComposition(
            projects,
            history,
            gitRules,
            branchHistory,
            formatInventory,
            gitHub);
    }
}
