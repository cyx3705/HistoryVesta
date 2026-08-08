using System.Reflection;
using AppShell.Core.Commands;
using HistoryJanus.Git;
using HistoryJanus.Views;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

internal static class ProjectOperationsSuite
{
    public static Task RunAsync(string[] args)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunRuleAutoSave();
                RunRuleEditingModel();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("project operations smoke did not complete within 15 seconds");
        if (failure != null)
            throw new InvalidOperationException("project operations smoke failed", failure);
        return Task.CompletedTask;
    }

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

    private static void RunRuleEditingModel()
    {
        var scannedFormat = new FormatRow(
            "*.cs", 12, 1, 12, 0, 12, 1024, 4096, "undecided", null, null, null);
        var scannedRow = new ProjectOperationsView.RuleEditRow(scannedFormat, null);
        True(scannedRow.Track == null && scannedRow.Lfs == null && scannedRow.Lf == null,
            "scanned undeclared format preserves nullable rule state");
        True(!scannedRow.IsDirty, "loaded scanned row starts clean");
        scannedRow.Track = true;
        True(scannedRow.Lfs == false && scannedRow.Lf == false,
            "choosing Git makes both storage attributes explicit false");
        True(scannedRow.IsDirty && scannedRow.IsValid,
            "editing a complete scanned rule marks it valid and dirty");
        scannedRow.ResetChanges();
        True(!scannedRow.IsDirty && scannedRow.Track == null,
            "reset restores the loaded snapshot and clears dirty state");

        var lfRule = new GitFileRuleInfo(
            "*.md", true, false, true, true, 3, 3, 0, 0, 0, 3, "3/3 LF");
        var lfRow = new ProjectOperationsView.RuleEditRow(
            new FormatRow("*.md", 3, 1, 3, 0, 0, 100, 200, "tracked", true, false, true),
            lfRule);
        True(lfRow.IsDeclared && lfRow.Lfs == false && lfRow.Lf == true,
            "declared LF row keeps explicit non-LFS state");
        lfRow.Lf = false;
        True(lfRow.IsDirty, "declared row change is tracked");
        lfRow.Lf = true;
        True(!lfRow.IsDirty, "restoring original values clears dirty state");

        var noExtensionRow = new ProjectOperationsView.RuleEditRow(
            new FormatRow(FormatInventoryService.NoExtension, 2, 1, 2, 0, 2, 10, 20,
                "undecided", null, null, null),
            null);
        True(!noExtensionRow.CanEdit,
            "no-extension aggregate remains visible but cannot become an invalid format rule");

        var mergeMethod = typeof(ProjectOperationsView).GetMethod(
            "MergeRows", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ProjectOperationsView.MergeRows not found");
        var report = new InventoryReport(
            1, 14, 14, 0, 14, 0, "default", 1, 10,
            [scannedFormat, new FormatRow(FormatInventoryService.NoExtension, 2, 1, 2, 0, 2, 10, 20,
                "undecided", null, null, null)], []);
        IReadOnlyList<GitFileRuleInfo> rules =
        [
            lfRule with { Pattern = "*.cs", FileCount = 12 },
            new GitFileRuleInfo("*.xlsx", true, true, false, true, 0, 0, 0, 0, 0, 0, "0 files"),
            new GitFileRuleInfo("Library/", false, false, false, true, 0, 0, 0, 0, 0, 0, "0 files"),
        ];
        var merged = ((IEnumerable<ProjectOperationsView.RuleEditRow>)mergeMethod.Invoke(
            null, [report, rules])!).ToList();
        Equal(4, merged.Count, "rule table is the de-duplicated union of scan and declarations");
        Equal(1, merged.Count(row => row.Pattern == "*.cs"),
            "declared rule overlays scanned format once");
        True(merged.Any(row => row.Pattern == "*.xlsx" && row.IsDeclared && row.FileCount == 0),
            "zero-file declaration remains visible");
        True(merged.Any(row => row.Pattern == "Library/" && row.IsDeclared),
            "directory declaration remains visible");
    }
}
