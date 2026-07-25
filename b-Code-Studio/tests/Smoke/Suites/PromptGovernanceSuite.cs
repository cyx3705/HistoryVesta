using AppShell.Core;
using AppShell.Core.Mcp;
using AppShell.Services.Mcp;
using AppShell.Shell.Mcp;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AppShell.Core.Commands;
using AppShell.Services;
using Microsoft.Data.Sqlite;
using OneHistoryStudio.Git;
using OneHistoryStudio.Views;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

/// <summary>
/// MCP 提示词治理、命令目录、网关与迁移。断言逐条搬自原 tests\PromptGovernanceSmoke\Program.cs。
/// 本套用例同时是 SQLite 通路的回归(建表 / 插入 / 查询 / 架构迁移)。
/// </summary>
internal static class PromptGovernanceSuite
{
    public static async Task RunAsync(string[] args)
    {
        // 合并前本套用例在仓库父目录下运行;command.manual 的相对路径判据依赖该语义。
        Environment.CurrentDirectory = ParentDir;

        var testName = $"OneHistoryStudio.Tests.{Guid.NewGuid():N}";
        var paths = new AppPaths(testName);
        ShellLog? log = null;
        try
        {
            log = new ShellLog(paths);
            var settings = new SettingsService(paths);
            var data = new SqliteDataService(paths);
            data.RegisterConnection("main", "main.db");

            var panelsDirectory = Path.Combine(paths.Root, "panels");
            var retiredPanel = Path.Combine(panelsDirectory, "projpush.json");
            Directory.CreateDirectory(panelsDirectory);
            await File.WriteAllTextAsync(retiredPanel, "{}");
            settings.Set(StartupMigrations.KeyMigrated, "1");
            StartupMigrations.Run(settings, paths, data, log);
            True(!File.Exists(retiredPanel), "V2.1.7 migration removes projpush panel");
            Equal("2", settings.Get(StartupMigrations.KeyMigrated), "V2.1.7 migration version");

            data.ExecuteSql(
                "CREATE TABLE mcp_descriptions (command TEXT PRIMARY KEY,description TEXT NOT NULL,updated TEXT NOT NULL)");
            data.ExecuteSql(
                "INSERT INTO mcp_descriptions VALUES ('proj.list','legacy description','2026-07-16 00:00:00')");
            data.ExecuteSql(
                """
                CREATE TABLE mcp_prompt_proposals (
                    id TEXT PRIMARY KEY, command TEXT NOT NULL, base_revision TEXT,
                    old_text TEXT NOT NULL, proposed_text TEXT NOT NULL, reason TEXT NOT NULL,
                    evidence TEXT NOT NULL DEFAULT '', source_client TEXT NOT NULL, created TEXT NOT NULL,
                    status TEXT NOT NULL, reviewer TEXT, reviewed TEXT, applied_revision TEXT
                )
                """);

            var history = new HistoryRecorder(data, log);
            var store = new PromptGovernanceStore(data, log);
            Equal("legacy description", store.AllEffectiveDescriptions()["proj.list"], "legacy migration");
            True(data.GetSchema("mcp_prompt_proposals").Any(c => c.Name == "review_note"),
                "proposal schema migration");

            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "proj.list",
                Summary = "default description",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
            });
            var exporter = new CommandSchemaExporter(registry)
            {
                DescriptionsProvider = store.AllEffectiveDescriptions,
            };
            PromptGovernanceCommands.RegisterAll(registry, exporter, store);
            var bus = new CommandBus(registry, log);

            var corrupted = await bus.ExecuteAsync(
                "prompt.propose name=proj.list text=???????? reason=encoding", "MCP:broken-client");
            True(!corrupted.Success && corrupted.Message.Contains("编码损坏"),
                "corrupted proposal rejected at command boundary");
            Throws<InvalidOperationException>(
                () => store.CreateProposal(
                    "proj.list", "legacy description", "??? broken", "encoding", "", "MCP:broken-client"),
                "corrupted proposal rejected at store boundary");
            data.ExecuteSql(
                "INSERT INTO mcp_prompt_proposals " +
                "(id,command,old_text,proposed_text,reason,evidence,source_client,created,status) VALUES " +
                "('proposal_corrupt_approval','proj.list','legacy description','????','encoding','','legacy-client','2026-07-17T00:00:00+08:00','pending')");
            Throws<InvalidOperationException>(
                () => store.ApproveProposal("proposal_corrupt_approval", "smoke"),
                "legacy corrupted proposal rejected during approval");
            store.RejectProposal("proposal_corrupt_approval", "smoke", "encoding damage");

            var corruptBeforeApply = store.CreateProposal(
                "proj.list", "legacy description", "valid before tampering", "encoding", "", "legacy-client");
            store.ApproveProposal(corruptBeforeApply.Id, "smoke");
            data.ExecuteSql(
                $"UPDATE mcp_prompt_proposals SET proposed_text='????' WHERE id='{corruptBeforeApply.Id}'");
            Throws<InvalidOperationException>(
                () => store.ApplyProposal(corruptBeforeApply.Id, "smoke"),
                "legacy corrupted proposal rejected during apply");
            store.RejectProposal(corruptBeforeApply.Id, "smoke", "encoding damage");

            var proposed = await bus.ExecuteAsync(
                "prompt.propose name=proj.list text=\"reviewed description\" reason=smoke", "MCP:smoke");
            True(proposed.Success && proposed.Data is PromptProposal, "proposal created");
            var proposal = (PromptProposal)proposed.Data!;
            Equal("legacy description", exporter.Find("proj.list")!.Description, "proposal does not apply itself");

            var approved = await bus.ExecuteAsync($"mcp.approve id={proposal.Id} reviewer=smoke", "UI");
            True(approved.Success, $"proposal approved: {approved.Message}");
            Equal("legacy description", exporter.Find("proj.list")!.Description, "approval does not apply itself");
            var applied = await bus.ExecuteAsync($"mcp.apply id={proposal.Id} reviewer=smoke", "UI");
            True(applied.Success, $"proposal applied: {applied.Message}");
            Equal("reviewed description", exporter.Find("proj.list")!.Description, "applied description visible");

            var conflict = store.CreateProposal(
                "proj.list", "reviewed description", "stale description", "conflict", "", "MCP:smoke");
            store.ApproveProposal(conflict.Id, "smoke");
            store.ApplyDirect("proj.list", "newer description", "UI", "newer local edit");
            Throws<InvalidOperationException>(() => store.ApplyProposal(conflict.Id, "smoke"), "stale proposal rejected");

            var rejected = store.CreateProposal(
                "proj.list", "newer description", "rejected description", "reject", "", "MCP:smoke");
            var rejectedResult = store.RejectProposal(rejected.Id, "smoke-reviewer", "not accurate");
            Equal("not accurate", rejectedResult.ReviewNote, "rejection reason persisted");
            Equal("smoke-reviewer", rejectedResult.Reviewer, "rejection reviewer persisted");
            True(rejectedResult.Reviewed != null, "rejection timestamp persisted");

            var correction = store.CreateCorrection(
                "proj.list", "wrong", "right", "evidence", "MCP:smoke", rejected.Id);
            var incident = store.CreateIncident(
                "proj.list", "symptom", "expected", "actual", "evidence", "MCP:smoke", correction.Id);
            True(store.ListCorrections("proj.list").Any(x => x.Id == correction.Id), "correction persisted");
            True(store.ListIncidents("proj.list").Any(x => x.Id == incident.Id), "incident persisted");
            Equal(rejected.Id, correction.LinkedProposal, "correction linked to proposal");
            Equal(correction.Id, incident.LinkedCorrection, "incident linked to correction");

            var target = store.GetRevisions("proj.list").Last();
            var reverted = store.RevertToRevision(target.Id, "smoke-reviewer", "smoke revert");
            Equal("smoke-reviewer", reverted.CreatedBy, "revert reviewer persisted");
            Equal(target.Description!, exporter.Find("proj.list")!.Description, "revert applied");

            var tools = exporter.ExportTools();
            True(tools.Any(t => t.CommandName == "prompt.propose"), "proposal exported to MCP");
            True(tools.All(t => !t.CommandName.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)),
                "local review commands hard excluded");

            using var gateway = new McpGateway(() => bus, settings, log, history, store, AppIdentity.Current);
            True(gateway.VisibleTools().Any(t => t.CommandName == "prompt.get"), "readonly exposes prompt.get");
            True(gateway.VisibleTools().All(t => t.CommandName != "prompt.propose"),
                "readonly hides prompt.propose");
            settings.Set(McpGateway.KeyPolicy, "standard");
            True(gateway.VisibleTools().Any(t => t.CommandName == "prompt.propose"),
                "standard exposes prompt.propose");

            registry.Register(new CommandDescriptor
            {
                Name = "app.exit",
                Summary = "exit host",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            });
            registry.Register(new CommandDescriptor
            {
                Name = "debug.sample",
                Summary = "debug command",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            }, "app");
            registry.Register(new CommandDescriptor
            {
                Name = "sample.module",
                Summary = "module command",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            }, "module:DemoModule");
            registry.Register(new CommandDescriptor
            {
                Name = "test.cancel",
                Summary = "cancellable command",
                Handler = async ctx =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.Cancellation);
                    return CommandResult.Ok();
                },
            }, "app");
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                var cancelled = await bus.ExecuteAsync("test.cancel", "UI", cancel.Token);
                True(!cancelled.Success && cancelled.Message.Contains("取消"),
                    "command bus forwards cancellation");
            }
            CommandCatalogCommands.RegisterAll(registry, exporter, store, () => gateway);
            var catalogResult = await bus.ExecuteAsync("command.list", "UI");
            True(catalogResult.Success && catalogResult.Data is IReadOnlyList<CommandCatalogRow>,
                "command catalog list");
            var catalog = (IReadOnlyList<CommandCatalogRow>)catalogResult.Data!;
            Equal(registry.All().Count, catalog.Count, "catalog count equals registry");
            var exitCommand = catalog.Single(row => row.CommandName == "app.exit");
            Equal("hidden", exitCommand.McpState, "app.exit hard excluded");
            True(!exitCommand.PolicyVisible && exitCommand.HardExclusionReason != null,
                "hard exclusion explained");
            Equal("hidden", catalog.Single(row => row.CommandName == "debug.sample").McpState,
                "debug command hard excluded");
            Equal("hidden", catalog.Single(row => row.CommandName == "mcp.apply").McpState,
                "mcp review command hard excluded");
            var moduleCommand = catalog.Single(row => row.CommandName == "sample.module");
            Equal("module", moduleCommand.Source, "module source category");
            Equal("DemoModule", moduleCommand.SourceDetail, "module source detail");
            True((await bus.ExecuteAsync("command.show app.exit", "UI")).Success,
                "hard excluded command detail available");
            True((await bus.ExecuteAsync("command.domains", "UI")).Success,
                "command domains available");
            Equal(gateway.VisibleTools().Count, catalog.Count(row => row.PolicyVisible),
                "catalog MCP visible count matches gateway");
            var manual = CommandManualGenerator.Render(registry, exporter, gateway.Policy);
            Equal(manual, CommandManualGenerator.Render(registry, exporter, gateway.Policy),
                "command manual generation is deterministic");
            True(manual.Contains($"<!-- command-count: {registry.All().Count} -->")
                 && manual.Contains("### `command.manual`")
                 && manual.Contains("### `sample.module`"),
                "command manual matches registry and includes module source");
            var manualPreview = await bus.ExecuteAsync(
                "command.manual file=b-Code-Studio/tests/manual-preview.md apply=false", "UI");
            True(manualPreview.Success && manualPreview.Data is CommandManualPreview { Applied: false },
                "command manual preview does not write");
            True(!File.Exists(Path.Combine(Environment.CurrentDirectory,
                "b-Code-Studio", "tests", "manual-preview.md")),
                "command manual preview leaves filesystem unchanged");
            True(!(await bus.ExecuteAsync("command.manual file=../outside.md apply=false", "UI")).Success,
                "command manual rejects boundary escape");

            var port = Random.Shared.Next(20000, 50000);
            var started = gateway.Start(port);
            True(started.Success, $"gateway start: {started.Message}");
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
                var response = await client.PostAsJsonAsync(
                    $"http://127.0.0.1:{port}/mcp",
                    new
                    {
                        jsonrpc = "2.0",
                        id = 1,
                        method = "initialize",
                        @params = new
                        {
                            protocolVersion = "2025-03-26",
                            capabilities = new { },
                            clientInfo = new { name = "smoke", version = "1" },
                        },
                    });
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var serverInfo = json.RootElement.GetProperty("result").GetProperty("serverInfo");
                Equal(AppIdentity.Current.Name, serverInfo.GetProperty("name").GetString(), "MCP server name");
                Equal(AppIdentity.Current.Version, serverInfo.GetProperty("version").GetString(), "MCP server version");

                var beforeRemoteProposal = exporter.Find("proj.list")!.Description;
                const string remoteDescription = "通过 UTF-8 提交的中文描述";
                var callResponse = await client.PostAsJsonAsync(
                    $"http://127.0.0.1:{port}/mcp",
                    new
                    {
                        jsonrpc = "2.0",
                        id = 2,
                        method = "tools/call",
                        @params = new
                        {
                            name = "prompt_propose",
                            arguments = new
                            {
                                name = "proj.list",
                                text = remoteDescription,
                                reason = "验证中文往返",
                            },
                        },
                    });
                callResponse.EnsureSuccessStatusCode();
                using var callJson = JsonDocument.Parse(await callResponse.Content.ReadAsStringAsync());
                True(!callJson.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(),
                    "MCP prompt_propose call");
                Equal(beforeRemoteProposal, exporter.Find("proj.list")!.Description,
                    "MCP proposal does not apply itself");
                var remoteProposal = store.ListProposals("proj.list", openOnly: true)
                    .First(x => x.SourceClient == "MCP:smoke");
                Equal(remoteDescription, remoteProposal.ProposedText, "MCP UTF-8 Chinese round trip");

                using var invalidUtf8 = new ByteArrayContent(
                [
                    .. System.Text.Encoding.ASCII.GetBytes(
                        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"prompt_propose\",\"arguments\":{\"name\":\"proj.list\",\"text\":\""),
                    0xC3,
                    0x28,
                    .. System.Text.Encoding.ASCII.GetBytes("\",\"reason\":\"encoding\"}}}"),
                ]);
                invalidUtf8.Headers.ContentType = new("application/json");
                var invalidResponse = await client.PostAsync(
                    $"http://127.0.0.1:{port}/mcp", invalidUtf8);
                using var invalidJson = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
                Equal(-32700, invalidJson.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
                    "invalid UTF-8 rejected");
            }
            True(gateway.Stop().Success, "gateway stop");

            Equal("OneHistoryStudio", AppIdentity.Current.Name, "identity name");
            True(!string.IsNullOrWhiteSpace(AppIdentity.Current.Version), "identity version is populated");
            Equal(AppIdentity.Current.InformationalVersion.Split('+', 2)[0], AppIdentity.Current.Version,
                "identity version is derived from informational version");
            True(Version.TryParse(AppIdentity.Current.FileVersion, out _), "identity file version is valid");

            var selection = new CommandSelectionState();
            var selectionChanges = 0;
            selection.Changed += (_, _) => selectionChanges++;
            selection.CurrentCommandName = "  sample.module  ";
            Equal("sample.module", selection.CurrentCommandName, "command selection trims name");
            selection.CurrentCommandName = "SAMPLE.MODULE";
            Equal(1, selectionChanges, "command selection ignores casing-only duplicate");
            selection.CurrentCommandName = null;
            Equal(2, selectionChanges, "command selection clear notification");

            var projectSelection = new ProjectSelectionState();
            var projectSelectionChanges = 0;
            projectSelection.Changed += (_, _) => projectSelectionChanges++;
            projectSelection.CurrentProjectName = "  2026-018-MyAPI  ";
            Equal("2026-018-MyAPI", projectSelection.CurrentProjectName, "project selection trims name");
            projectSelection.CurrentProjectName = "2026-018-myapi";
            Equal(1, projectSelectionChanges, "project selection ignores casing-only duplicate");
            projectSelection.CurrentProjectName = null;
            Equal(2, projectSelectionChanges, "project selection clear notification");

            var scannedFormat = new FormatRow(
                "*.cs", 12, 1, 12, 0, 12, 1024, 4096, "未决", null, null, null);
            var scannedRow = new ProjectOperationsView.RuleEditRow(scannedFormat, null);
            True(scannedRow.Track == null && scannedRow.Lfs == null && scannedRow.Lf == null,
                "scanned undeclared format preserves nullable rule state");
            Equal("未决（扫描发现，尚未声明规则）", scannedRow.StorageResult,
                "scanned undeclared format is not disguised as ordinary Git");
            scannedRow.Track = true;
            True(scannedRow.Lfs == false && scannedRow.Lf == false,
                "choosing Git makes both storage attributes explicit false");
            scannedRow.Lf = true;
            Equal("Git + LF（文本，非 LFS 指针）", scannedRow.StorageResult,
                "LF storage result explicitly states that LFS pointers are not used");

            var lfRule = new GitFileRuleInfo(
                "*.md", true, false, true, true, 3, 3, 0, 0, 0, 3, "3/3 LF 规则生效");
            var lfRow = new ProjectOperationsView.RuleEditRow(
                new FormatRow("*.md", 3, 1, 3, 0, 0, 100, 200, "已跟踪管", true, false, true),
                lfRule);
            True(lfRow.IsDeclared && lfRow.Lfs == false && lfRow.Lf == true,
                "declared LF row keeps explicit non-LFS state");
            Equal("Git + LF（文本，非 LFS 指针）", lfRow.StorageResult, "declared LF storage result");

            var noExtensionRow = new ProjectOperationsView.RuleEditRow(
                new FormatRow(FormatInventoryService.NoExtension, 2, 1, 2, 0, 2, 10, 20, "未决", null, null, null),
                null);
            True(!noExtensionRow.CanEdit && noExtensionRow.StorageResult.Contains("按目录规则管理"),
                "no-extension aggregate is visible but cannot become an invalid format rule");

            var mergeMethod = typeof(ProjectOperationsView).GetMethod(
                "MergeRows", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("ProjectOperationsView.MergeRows not found");
            var mergeReport = new InventoryReport(
                1, 14, 14, 0, 14, 0, "default", 1, 10,
                [scannedFormat, new FormatRow(FormatInventoryService.NoExtension, 2, 1, 2, 0, 2, 10, 20,
                    "未决", null, null, null)], []);
            IReadOnlyList<GitFileRuleInfo> mergeRules =
            [
                lfRule with { Pattern = "*.cs", FileCount = 12 },
                new GitFileRuleInfo("*.xlsx", true, true, false, true, 0, 0, 0, 0, 0, 0, "当前 0 文件"),
                new GitFileRuleInfo("Library/", false, false, false, true, 0, 0, 0, 0, 0, 0, "当前 0 文件"),
            ];
            var mergedRows = ((IEnumerable<ProjectOperationsView.RuleEditRow>)mergeMethod.Invoke(
                null, [mergeReport, mergeRules])!).ToList();
            Equal(4, mergedRows.Count, "rule table is the de-duplicated union of scan formats and declarations");
            Equal(1, mergedRows.Count(row => row.Pattern == "*.cs"), "declared rule overlays scanned format once");
            True(mergedRows.Any(row => row.Pattern == "*.xlsx" && row.IsDeclared && row.FileCount == 0),
                "zero-file declaration remains visible");
            True(mergedRows.Any(row => row.Pattern == "Library/" && row.IsDeclared),
                "directory declaration remains visible");

            registry.Register(new CommandDescriptor
            {
                Name = "sample.dynamic",
                Summary = "dynamic module command",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            });
            var dynamicProposal = store.CreateProposal(
                "sample.dynamic", "dynamic module command", "better dynamic command", "module", "", "MCP:smoke");
            True(registry.Unregister("sample.dynamic"), "dynamic command unloaded");
            True(store.GetProposal(dynamicProposal.Id) != null, "unloaded command proposal history retained");

            Console.WriteLine("PromptGovernanceSmoke: PASS");
        }
        finally
        {
            log?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root))
                Directory.Delete(paths.Root, recursive: true);
        }
    }
}
