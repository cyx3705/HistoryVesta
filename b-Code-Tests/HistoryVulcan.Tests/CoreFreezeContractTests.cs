using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Mcp;
using HistoryVulcan.Shell.Mcp;
using System.Collections.Concurrent;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class CoreFreezeContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData("with spaces")]
    [InlineData("quote \" and slash \\")]
    [InlineData("left=right")]
    [InlineData("中文 值")]
    public void CommandParserQuoteArgRoundTripsNamedValues(string value)
    {
        var parsed = CommandParser.Parse($"sample.run value={CommandParser.QuoteArg(value)}");

        Assert.Equal("sample.run", parsed.Name);
        Assert.Equal(value, parsed.Named["value"]);
    }

    [Fact]
    public void CommandParserRejectsDuplicateArgumentsAndUnclosedQuotes()
    {
        Assert.Throws<CommandSyntaxException>(() => CommandParser.Parse("sample.run value=1 value=2"));
        Assert.Throws<CommandSyntaxException>(() => CommandParser.Parse("sample.run value=\"open"));
    }

    [Fact]
    public async Task CommandBusBindsTypesAndEnforcesConfirmationGate()
    {
        var registry = new CommandRegistry();
        var executed = 0;
        registry.Register(new CommandDescriptor
        {
            Name = "sample.change",
            Summary = "change",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "count",
                    Description = "count",
                    Type = ParamType.Int,
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "enabled",
                    Description = "enabled",
                    Type = ParamType.Bool,
                    Required = true,
                },
            ],
            ConfirmPrompt = context => $"change {context.GetInt("count")}",
            Handler = CommandDescriptor.Sync(context =>
            {
                executed++;
                return CommandResult.Ok($"{context.GetInt("count")}:{context.GetBool("enabled")}");
            }),
        });
        var confirmation = new ToggleConfirmation();
        var bus = new CommandBus(registry, new NullLog()) { Confirmation = confirmation };

        var invalid = await bus.ExecuteAsync("sample.change bad enabled=true", "Test");
        var rejected = await bus.ExecuteAsync("sample.change 4 enabled=true", "Test");
        Assert.Equal(0, executed);
        confirmation.Approve = true;
        var accepted = await bus.ExecuteAsync("sample.change 4 enabled=true", "Test");

        Assert.False(invalid.Success);
        Assert.Contains("整数", invalid.Message, StringComparison.Ordinal);
        Assert.False(rejected.Success);
        Assert.True(accepted.Success, accepted.Message);
        Assert.Equal("4:True", accepted.Message);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task CommandBusAddsCommandDomainToResultsAndProgressWithoutCrossingConcurrentRuns()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "alpha.work",
            Summary = "alpha",
            Handler = async context =>
            {
                context.Progress?.Report("alpha-step");
                await Task.Delay(25);
                return CommandResult.Ok("alpha-done");
            },
        });
        registry.Register(new CommandDescriptor
        {
            Name = "beta.work",
            Summary = "beta",
            Handler = async context =>
            {
                context.Progress?.Report("beta-step");
                await Task.Delay(5);
                return CommandResult.Ok("beta-done");
            },
        });
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);

        await Task.WhenAll(
            bus.ExecuteAsync("alpha.work", "Test"),
            bus.ExecuteAsync("beta.work", "Test"));
        Assert.True(SpinWait.SpinUntil(
            () => log.Entries.Count(entry => entry.Category.StartsWith(
                CommandBus.ProgressCategory, StringComparison.Ordinal)) == 2,
            TimeSpan.FromSeconds(2)));

        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:progress:alpha:alpha" && entry.Message == "alpha-step");
        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:progress:beta:beta" && entry.Message == "beta-step");
        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:result:alpha:alpha" && entry.Message.Contains("alpha-done", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:result:beta:beta" && entry.Message.Contains("beta-done", StringComparison.Ordinal));
        Assert.Equal("cmd:result", CommandBus.ResultCategory);
        Assert.Equal("cmd:progress", CommandBus.ProgressCategory);
    }

    [Fact]
    public async Task CommandBusUsesCoreForRootCommandsAndParsedPrefixForUnknownCommands()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "ping",
            Summary = "ping",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
        });
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);

        Assert.True((await bus.ExecuteAsync("ping", "Test")).Success);
        Assert.False((await bus.ExecuteAsync("missing.run", "Test")).Success);

        Assert.Contains(log.Entries, entry => entry.Category == "cmd:result:core:core");
        Assert.Contains(log.Entries, entry => entry.Category == "cmd:result:missing:core");
        Assert.Equal(2, log.Entries.Count(entry => entry.Category == "cmd:Test"));
    }

    [Fact]
    public void CommandRegistryResolvesExplicitAndModuleOwnedTaxonomy()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "window.inspect",
            Domain = "vulcan",
            CommandClass = "WIN",
            Summary = "inspect",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "fixture.run",
            Domain = "spoofed",
            Summary = "run",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        }, "module:FixtureModule");

        Assert.Equal("vulcan", registry.GetDomain("window.inspect"));
        Assert.Equal("win", registry.GetCommandClass("window.inspect"));
        // DEC-023:模块域由 owner 强制并去掉 History 品牌前缀；描述符自填的 Domain 被覆盖。
        Assert.Equal("fixturemodule", registry.GetDomain("fixture.run"));
        // 两段名回退到首段作为类；「无类」概念已废止。
        Assert.Equal("fixture", registry.GetCommandClass("fixture.run"));
    }



    [Fact]
    public async Task PromptCommandsReturnSafeIntegrityValidationErrors()
    {
        var root = TemporaryDirectory();
        try
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "sample.run",
                Summary = "sample",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
            });
            var store = new PromptGovernanceStore(root, new NullLog());
            var exporter = new CommandSchemaExporter(registry)
            {
                DescriptionsProvider = store.AllEffectiveDescriptions,
            };
            PromptGovernanceCommands.RegisterAll(registry, exporter, store);
            var bus = new CommandBus(registry, new NullLog());

            var proposal = await bus.ExecuteAsync(
                "vulcan.prompt.propose name=sample.run text=???????? reason=encoding", "MCP:test");
            var direct = await bus.ExecuteAsync(
                "vulcan.mcp.desc name=sample.run text=???????? reason=encoding", "UI");

            Assert.False(proposal.Success);
            Assert.Contains("编码损坏", proposal.Message, StringComparison.Ordinal);
            Assert.False(direct.Success);
            Assert.Contains("编码损坏", direct.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PromptGovernanceProposalApprovalApplyAndRevertAreStateful()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new PromptGovernanceStore(root, new NullLog());
            var original = store.ApplyDirect(
                "sample.run", "original description", "test", "seed");
            var proposal = store.CreateProposal(
                "sample.run", "original description", "improved description",
                "clarify", "review", "test-client");

            Assert.Throws<InvalidOperationException>(() => store.ApplyProposal(proposal.Id, "reviewer"));
            var approved = store.ApproveProposal(proposal.Id, "reviewer");
            var applied = store.ApplyProposal(approved.Id, "reviewer");
            var reverted = store.RevertToRevision(original.Id, "reviewer", "rollback");

            Assert.Equal("approved", approved.Status);
            Assert.Equal("improved description", applied.Description);
            Assert.Equal("original description", reverted.Description);
            Assert.Equal(original.Id, reverted.RevertedFrom);
            Assert.Equal(reverted.Id, store.GetCurrentRevision("sample.run")!.Id);
            var reloaded = new PromptGovernanceStore(root, new NullLog());
            Assert.Equal("original description", reloaded.GetCurrentRevision("sample.run")!.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ToggleConfirmation : IConfirmationService
    {
        public bool Approve { get; set; }
        public bool Confirm(string prompt) => Approve;
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    private sealed class RecordingLog : IShellLog
    {
        private readonly ConcurrentQueue<ShellLogEntry> _entries = new();

        public IReadOnlyList<ShellLogEntry> Entries => _entries.ToArray();

        public void Log(ShellLogLevel level, string category, string message)
            => _entries.Enqueue(new ShellLogEntry(DateTime.UtcNow, level, category, message));

        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }

        public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
    }
}
