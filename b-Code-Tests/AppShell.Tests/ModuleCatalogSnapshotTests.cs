using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Services.Modules;
using AppShell.Shell.Mcp;
using AppShell.Shell.Views;
using System.Text.Json;
using Xunit;

namespace AppShell.Tests;

public sealed class ModuleCatalogSnapshotTests
{
    [Fact]
    public void SnapshotFiltersBySourceInsteadOfCommandPrefix()
    {
        var modules = new[] { Module("Math", 2) };
        var commands = new[]
        {
            Command("calc.add", "Math"),
            Command("calc.subtract", "Math"),
            new ModuleCommandInfo("Math.localOnly", "wrong source", "", "app", null),
        };

        Assert.True(ModuleCatalogSnapshot.TryCreate(
            modules, commands, out var snapshot, out var error), error);
        Assert.Equal(
            ["calc.add", "calc.subtract"],
            snapshot!.CommandsFor("math").Select(command => command.Name));
    }

    [Fact]
    public void SnapshotRejectsCrossGenerationCounts()
    {
        var modules = new[] { Module("Math", 2) };

        Assert.False(ModuleCatalogSnapshot.TryCreate(
            modules, [Command("calc.add", "Math")], out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.Contains("刷新期间发生变化", error);
    }

    [Fact]
    public async Task ReaderUsesRemoteModuleAndCommandCatalogsTogether()
    {
        var calls = new List<string>();
        var bus = new CommandBus(new CommandRegistry(), new TestLog())
        {
            RemoteExecutor = (text, _, _) =>
            {
                calls.Add(text);
                return Task.FromResult(text == "module.list"
                    ? CommandResult.Ok("modules", new List<ModuleMeta> { Module("Math", 1) })
                    : CommandResult.Ok("commands", new List<CommandCatalogRow>
                    {
                        CatalogRow("calc.add", "Math"),
                    }));
            },
        };

        var result = await ModuleCatalogReader.LoadAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["module.list", "command.list"], calls);
        Assert.Equal("calc.add", Assert.Single(result.Snapshot!.CommandsFor("Math")).Name);
    }

    [Fact]
    public async Task ReaderDeserializesJsonDataReturnedAcrossProcessBoundary()
    {
        var modules = new List<ModuleMeta> { Module("Math", 1) };
        var commands = new List<CommandCatalogRow> { CatalogRow("calc.add", "Math") };
        var bus = new CommandBus(new CommandRegistry(), new TestLog())
        {
            RemoteExecutor = (text, _, _) => Task.FromResult(text == "module.list"
                ? CommandResult.Ok("modules", JsonSerializer.SerializeToElement(modules))
                : CommandResult.Ok("commands", JsonSerializer.SerializeToElement(commands))),
        };

        var result = await ModuleCatalogReader.LoadAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal("calc.add", Assert.Single(result.Snapshot!.CommandsFor("Math")).Name);
    }

    private static ModuleMeta Module(string name, int commandCount)
        => new(name, "", "", "1.0.0", false, name + ".dll", commandCount);

    private static ModuleCommandInfo Command(string name, string module)
        => new(name, name, "", "module", module);

    private static CommandCatalogRow CatalogRow(string name, string module)
        => new(
            name, "calc", name, null, 0, "module", module,
            false, false, null, "standard", false,
            false, null, 0, 0, null);

    private sealed class TestLog : IShellLog
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
