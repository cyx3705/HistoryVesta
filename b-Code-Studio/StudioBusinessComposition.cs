using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using OneHistoryStudio.Git;

namespace OneHistoryStudio;

/// <summary>
/// OHS business services registered into a host-owned command bus.
/// This boundary deliberately owns no process, window, module loader, MCP, Web, or LAN lifetime.
/// </summary>
public sealed class StudioBusinessComposition
{
    internal StudioBusinessComposition(
        ProjectService projects,
        HistoryRecorder history,
        GitFileRuleService gitRules,
        BranchHistoryService branchHistory,
        FormatInventoryService formatInventory)
    {
        Projects = projects;
        History = history;
        GitRules = gitRules;
        BranchHistory = branchHistory;
        FormatInventory = formatInventory;
    }

    public ProjectService Projects { get; }

    public HistoryRecorder History { get; }

    public GitFileRuleService GitRules { get; }

    public BranchHistoryService BranchHistory { get; }

    public FormatInventoryService FormatInventory { get; }
}

/// <summary>
/// Builds the OHS domain graph inside infrastructure supplied by the AppShell host.
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

        ProjectCommands.RegisterAll(registry, projects, history, commandSource);
        BranchHistoryCommands.RegisterAll(registry, branchHistory, history, commandSource);
        GitRuleCommands.RegisterAll(registry, gitRules, formatInventory, projects, commandSource);
        DebugCommands.RegisterAll(registry, log, commandSource);

        return new StudioBusinessComposition(
            projects,
            history,
            gitRules,
            branchHistory,
            formatInventory);
    }
}
