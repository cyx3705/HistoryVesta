using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Core.Mcp;
using AppShell.Services;
using AppShell.Services.Mcp;
using AppShell.Shell.Mcp;
using Xunit;

namespace AppShell.Tests;

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
    public void WorkspaceRejectsTraversalAndAbsoluteEscape()
    {
        var root = TemporaryDirectory();
        var outside = TemporaryDirectory();
        try
        {
            using var workspace = new WorkspaceService(root + Path.DirectorySeparatorChar);

            Assert.Equal(Path.GetFullPath(root), workspace.ResolveFull(""));
            Assert.Equal(Path.Combine(root, "child"), workspace.ResolveFull("child"));
            Assert.Throws<InvalidOperationException>(() => workspace.ResolveFull("..\\escape"));
            Assert.Throws<InvalidOperationException>(() => workspace.ResolveFull(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceRejectsReparsePointTraversalForEveryFileOperation()
    {
        var root = TemporaryDirectory();
        var outside = TemporaryDirectory();
        var link = Path.Combine(root, "outside-link");
        var victim = Path.Combine(outside, "victim.txt");
        File.WriteAllText(victim, "keep");
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            using var workspace = new WorkspaceService(root);

            Assert.Throws<InvalidOperationException>(() => workspace.ResolveFull("outside-link"));
            Assert.Throws<InvalidOperationException>(() => workspace.List("outside-link"));
            Assert.Throws<InvalidOperationException>(() => workspace.CreateDirectory("outside-link\\new\\nested"));
            Assert.Throws<InvalidOperationException>(() => workspace.Rename("outside-link\\victim.txt", "renamed.txt"));
            Assert.Throws<InvalidOperationException>(() => workspace.DeleteToRecycleBin("outside-link\\victim.txt"));

            Assert.True(File.Exists(victim));
            Assert.False(Directory.Exists(Path.Combine(outside, "new")));
            Assert.False(File.Exists(Path.Combine(outside, "renamed.txt")));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
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
                "prompt.propose name=sample.run text=???????? reason=encoding", "MCP:test");
            var direct = await bus.ExecuteAsync(
                "mcp.desc name=sample.run text=???????? reason=encoding", "UI");

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
        var path = Path.Combine(Path.GetTempPath(), "AppShell.Tests", Guid.NewGuid().ToString("N"));
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
}
