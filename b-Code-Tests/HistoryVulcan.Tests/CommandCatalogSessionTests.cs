using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Shell;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Mcp;
using HistoryVulcan.Shell.Views;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class CommandCatalogSessionTests
{
    [Fact]
    public async Task RemoteCatalogDrivesSearchSelectionAndParameterCompletion()
    {
        var registry = new CommandRegistry();
        var log = new NullLog();
        var bus = new CommandBus(registry, log);
        var row = Row("module.deploy", "Deploy the selected module", "module.deploy mode=fast");
        var detail = new CommandCatalogDetail(
            row,
            [
                new CommandParameterInfo(
                    "mode",
                    "string",
                    false,
                    null,
                    null,
                    ["fast", "safe"],
                    "Deployment mode"),
            ],
            null);
        var calls = new List<string>();
        bus.RemoteExecutor = (text, _, _) =>
        {
            calls.Add(text);
            return Task.FromResult(text switch
            {
                "command.list" => CommandResult.Ok("commands", new List<CommandCatalogRow> { row }),
                "command.domains" => CommandResult.Ok(
                    "domains",
                    new List<CommandDomainInfo> { new("module", 1) }),
                _ when text.StartsWith("command.show ", StringComparison.Ordinal) =>
                    CommandResult.Ok("detail", detail),
                _ => CommandResult.Fail("unexpected command"),
            });
        };
        bus.ShouldUseRemoteCommand = static (_, _) => true;
        var selection = new CommandSelectionState();
        using var session = new CommandCatalogSession(bus, selection);

        Assert.True(await session.RefreshAsync());
        session.SetConsoleQuery("selected module");

        Assert.Equal("module.deploy", Assert.Single(session.VisibleRows).CommandName);
        Assert.Equal("module.deploy", session.SelectedCommandName);

        var parameter = await session.CompleteAsync("module.deploy mo", 16);
        Assert.Equal("mode=", Assert.Single(parameter.Candidates).InsertText);

        var value = await session.CompleteAsync("module.deploy mode=f", 20);
        Assert.Equal("fast", Assert.Single(value.Candidates).InsertText);
        Assert.Equal(1, calls.Count(call => call.StartsWith("command.show ", StringComparison.Ordinal)));
        Assert.False(registry.TryGet("module.deploy", out _));
    }

    [Fact]
    public async Task ViewFiltersPreserveTheConsoleQueryAndSharedSelection()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("app.show", "Show frontend"));
        registry.Register(Command("app.hide", "Hide frontend"));
        var log = new NullLog();
        var selection = new CommandSelectionState();
        using var session = new CommandCatalogSession(new CommandBus(registry, log), selection);

        Assert.True(await session.RefreshAsync());
        session.SetConsoleQuery("app.");
        session.SetFilter(session.CurrentFilter with { Domain = "app", CommandClass = "app" });

        Assert.Equal("app.", session.CurrentFilter.Query);
        Assert.Equal(2, session.VisibleRows.Count);
        Assert.True(session.MoveSelection(+1));
        Assert.Equal(session.SelectedCommandName, selection.CurrentCommandName);
    }

    [Fact]
    public async Task CommandListFiltersByExplicitDomainAndClass()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "win.sample",
            Domain = "HistoryVulcan",
            CommandClass = "win",
            Summary = "Sample window command",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });
        CommandCatalogCommands.RegisterCore(registry);
        var bus = new CommandBus(registry, new NullLog());

        var result = await bus.ExecuteAsync(
            "command.list domain=HistoryVulcan class=win",
            "Test");

        Assert.True(result.Success, result.Message);
        Assert.True(CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(
            result.Data,
            out var rows));
        var row = Assert.Single(rows);
        Assert.Equal("win.sample", row.CommandName);
        Assert.Equal("HistoryVulcan", row.Domain);
        Assert.Equal("win", row.CommandClass);
    }

    private static CommandDescriptor Command(string name, string summary)
        => new()
        {
            Name = name,
            Summary = summary,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };

    private static CommandCatalogRow Row(string name, string summary, string example)
        => new(
            name,
            name.Split('.')[0],
            summary,
            example,
            1,
            "module",
            "fixture",
            false,
            false,
            null,
            "hidden",
            false,
            false,
            null,
            0,
            0,
            null)
        {
            CommandClass = "deploy",
        };

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
