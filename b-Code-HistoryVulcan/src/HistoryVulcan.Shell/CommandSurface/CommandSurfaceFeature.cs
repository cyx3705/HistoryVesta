using HistoryVulcan.Core.Commands;
using System.IO;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Views;

namespace HistoryVulcan.Shell.CommandSurface;

internal sealed record CommandSurfaceWindow(
    string Id,
    string Title,
    DockSide Side,
    double Ratio,
    Func<object> ContentFactory,
    bool ForcePlacement = false);

/// <summary>
/// Cohesive built-in feature for console input, completion, command catalog search and detail selection.
/// It is intentionally internal in 3.2.2 so the next release can move it without changing ShellWindow.
/// </summary>
internal sealed class CommandSurfaceFeature : IDisposable
{
    private readonly CommandCatalogSession _session;
    private readonly CommandHistory _history;

    public CommandSurfaceFeature(
        CommandBus bus,
        IShellLog log,
        ISettingsService settings,
        string dataDirectory,
        CommandSelectionState? selection)
    {
        Selection = selection ?? new CommandSelectionState();
        _history = new CommandHistory(
            Path.Combine(dataDirectory, "history.txt"),
            settings.GetInt(ConsoleView.KeyHistory, 500));
        _session = new CommandCatalogSession(bus, Selection);
        Console = new ConsoleView(
            log,
            bus,
            _history,
            _session,
            settings.GetInt(ConsoleView.KeyBuffer, 50_000));
        Catalog = new McpToolsView(() => bus, Selection, _session);

        Windows =
        [
            new CommandSurfaceWindow(
                StandardWindowIds.Console,
                "控制台",
                DockSide.Bottom,
                0.25,
                () => Console),
            new CommandSurfaceWindow(
                StandardWindowIds.Mcp,
                "命令集",
                DockSide.Center,
                1,
                () => Catalog,
                ForcePlacement: true),
            new CommandSurfaceWindow(
                StandardWindowIds.CommandDetail,
                "指令详情",
                DockSide.Right,
                0.32,
                () => new CommandDetailView(() => bus, Selection, _session)),
        ];
    }

    public ConsoleView Console { get; }

    public McpToolsView Catalog { get; }

    public CommandSelectionState Selection { get; }

    public CommandHistory History => _history;

    public IReadOnlyList<CommandSurfaceWindow> Windows { get; }

    public void ConfigureRouting(Func<bool> isConsoleFocused, Action showCommandCatalog)
        => Console.ConfigureCompletionRouting(isConsoleFocused, showCommandCatalog);

    public void RefreshFocusState() => Console.RefreshCompletionFocus();

    public void ResetFilters() => Console.ResetFilters();

    public void FocusInput(bool resetFilters = false)
    {
        if (resetFilters)
            Console.ResetFilters();
        Console.ActivateContent();
    }

    public void AddTransientEntry(ShellLogEntry entry) => Console.AddTransientEntry(entry);

    public void Dispose()
    {
        _history.Save();
        _session.Dispose();
    }
}
