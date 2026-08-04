using System.Reflection;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;
using OneHistoryStudio.Views;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

internal static partial class DockingSuite
{
    private static void RunRuleAutoSave()
    {
        var executions = 0;
        string? changesJson = null;
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "git.rule.batch-set",
            Summary = "automatic save fixture",
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "project", Required = true },
                new ParameterSpec { Name = "changes", Description = "changes", Required = true },
                new ParameterSpec
                {
                    Name = "apply", Description = "apply", Type = ParamType.Bool, Default = "false",
                },
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                executions++;
                changesJson = context.RequireString("changes");
                var applied = context.GetBool("apply");
                return CommandResult.Ok("saved", new GitFileRuleBatchPreview(
                    context.RequireString("name"), [], "", "", 0, 0, 0,
                    Changed: true, Applied: applied));
            }),
        });
        var bus = new CommandBus(registry, new MemoryLog());
        var view = new ProjectOperationsView(() => bus, new ProjectSelectionState());
        var rules = (System.Collections.ObjectModel.ObservableCollection<ProjectOperationsView.RuleEditRow>)
            typeof(ProjectOperationsView).GetField("_rules", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(view)!;
        var row = new ProjectOperationsView.RuleEditRow("*.autosave");
        rules.Add(row);
        row.Lfs = true;
        row.Lf = true;
        var saveTask = (Task<bool>)typeof(ProjectOperationsView).GetMethod(
                "SaveDirtyRulesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, ["main"])!;
        True(saveTask.GetAwaiter().GetResult(), "automatic rule batch saves successfully");

        Equal(2, executions, "debounced rule edit executes one preview and one apply");
        var changes = System.Text.Json.JsonSerializer.Deserialize<List<GitFileRuleChange>>(
            changesJson!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        True(changes is [{ Pattern: "*.autosave", Track: true, Lfs: false, Lf: true }],
            "automatic save persists the final linked checkbox state");
    }
}
