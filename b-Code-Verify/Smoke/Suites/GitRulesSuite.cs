using AppShell.Core;
using AppShell.Core.Mcp;
using System.Text;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

/// <summary>Git 文件规则、LFS/LF 规范化与格式台账。</summary>
internal static class GitRulesSuite
{
    public static async Task RunAsync(string[] args)
    {
        var root = TemporaryDirectory("git-rules");
        var seed = Path.Combine(root, "seed");
        var bare = Path.Combine(root, "projects.git");
        var worktree = Path.Combine(root, "sample");
        Directory.CreateDirectory(root);

        try
        {
            Ensure(await GitRunner.RunAsync(root, ["init", "-b", "main", seed]), "init seed");
            Ensure(await GitRunner.RunAsync(seed, ["config", "user.name", "Git Rules Smoke"]), "git user name");
            Ensure(await GitRunner.RunAsync(seed, ["config", "user.email", "git-rules@example.invalid"]), "git user email");
            Ensure(await GitRunner.RunAsync(seed, ["lfs", "install", "--local"]), "git lfs local install");

            var attributes =
                "# manual attributes before\r\n" +
                "*.bin filter=lfs diff=lfs merge=lfs -text\r\n" +
                "*.txt text eol=lf\r\n" +
                "# OneHistoryStudio managed begin\r\n" +
                "*.seed text eol=lf\r\n" +
                "# OneHistoryStudio managed end\r\n" +
                "# manual attributes after\r\n";
            var ignore =
                "# manual ignore before\r\n" +
                "manual-only/\r\n" +
                "# OneHistoryStudio managed begin\r\n" +
                "*.old\r\n" +
                "# OneHistoryStudio managed end\r\n" +
                "# manual ignore after\r\n";
            await File.WriteAllTextAsync(Path.Combine(seed, ".gitattributes"), attributes, new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(seed, ".gitignore"), ignore, new UTF8Encoding(true));
            await File.WriteAllBytesAsync(Path.Combine(seed, "asset.bin"), [1, 2, 3, 4, 5]);
            await File.WriteAllTextAsync(Path.Combine(seed, "readme.txt"), "hello\r\nworld\r\n", new UTF8Encoding(false));
            Ensure(await GitRunner.RunAsync(seed, ["add", "."]), "git add seed");
            Ensure(await GitRunner.RunAsync(seed, ["commit", "-m", "seed"]), "git commit seed");
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", seed, bare]), "clone bare");
            Ensure(await GitRunner.RunAsync(bare, ["worktree", "add", worktree, "main"]), "add worktree");
            Ensure(await GitRunner.RunAsync(worktree, ["lfs", "install", "--local"]), "worktree lfs install");
            Ensure(await GitRunner.RunAsync(worktree, ["config", "user.name", "Git Rules Smoke"]), "worktree user name");
            Ensure(await GitRunner.RunAsync(worktree, ["config", "user.email", "git-rules@example.invalid"]), "worktree user email");

            var settings = new MemorySettings();
            settings.Set(ProjectService.KeyBareRepo, bare);
            settings.Set(ProjectService.KeyWorktreeRoot, root);
            settings.Set(ProjectService.KeyBaseBranch, "main");
            var projects = new ProjectService(settings, _ => true, root);
            var service = new GitFileRuleService(projects);
            var headBefore = await GitRunner.RunAsync(worktree, ["rev-parse", "HEAD"]);
            Ensure(headBefore, "read initial HEAD");

            var outsideProject = await service.ListAsync("..");
            True(!outsideProject.Success, "unregistered project and boundary escape rejected");
            var invalidPattern = await service.SetAsync("main", "../*.txt", true, false, true, apply: false);
            True(!invalidPattern.Success, "path-bearing pattern rejected as a controlled failure");

            var batchA = Path.Combine(worktree, "first.batcha");
            var batchB = Path.Combine(worktree, "second.batchb");
            var batchC = Path.Combine(worktree, "third.batchc");
            await File.WriteAllTextAsync(batchA, "first\r\nline\r\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(batchB, "keep locally", new UTF8Encoding(false));
            await File.WriteAllTextAsync(batchC, "ordinary", new UTF8Encoding(false));
            Ensure(await GitRunner.RunAsync(worktree, ["add", "--", "second.batchb"]),
                "track batch ignore fixture before applying rules");
            IReadOnlyList<GitFileRuleChange> batchChanges =
            [
                new("*.batcha", true, false, true),
                new("*.batchb", false, false, false),
                new("*.batchc", true, true, false),
            ];
            var batchPreview = await service.BatchSetAsync("main", batchChanges, apply: false);
            True(batchPreview.Success && batchPreview.Preview is
                {
                    Changed: true,
                    Applied: false,
                    Items.Count: 3,
                    AddToIndex: 2,
                    RemoveFromIndex: 1,
                }, "three rule changes produce one complete batch preview");
            var invalidBatch = await service.BatchSetAsync("main",
            [
                new("*.never", true, false, false),
                new("*.NEVER", false, false, false),
            ], apply: true);
            True(!invalidBatch.Success && invalidBatch.Message.Contains("重复"),
                "duplicate batch pattern rejects the whole batch before writing");

            var batchApplied = await service.BatchSetAsync("main", batchChanges, apply: true);
            True(batchApplied.Success && batchApplied.Preview is { Applied: true, Items.Count: 3 },
                $"three rule changes apply together: {batchApplied.Message}");
            Ensure(await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "first.batcha"]),
                "batch LF file added to index");
            Ensure(await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "third.batchc"]),
                "batch ordinary file added to index");
            True(!(await GitRunner.RunAsync(
                    worktree, ["ls-files", "--error-unmatch", "--", "second.batchb"])).Success,
                "batch ignored file removed from index");
            True(File.Exists(batchB), "batch ignore preserves working-tree file");
            var batchRules = await service.ListAsync("main");
            True(batchRules.Success
                 && batchRules.Rules.Any(rule => rule.Pattern == "*.batcha" && rule.Track && rule.Lf)
                 && batchRules.Rules.Any(rule => rule.Pattern == "*.batchb" && !rule.Track)
                 && batchRules.Rules.Any(rule => rule.Pattern == "*.batchc" && rule.Track && rule.Lfs && !rule.Lf),
                "all three batch rules persist after one apply");
            var unchangedBatch = await service.BatchSetAsync("main", batchChanges, apply: false);
            True(unchangedBatch.Success && unchangedBatch.Preview is
                { Changed: false, AddToIndex: 0, RemoveFromIndex: 0, Renormalize: 0 },
                "repeating an already converged batch is a no-op");

            var initialRules = await service.ListAsync("main");
            True(initialRules.Success, $"initial list: {initialRules.Message}");
            True(initialRules.Rules.Any(rule =>
                    rule.Pattern == "*.bin" && rule.Track && rule.Lfs && rule.LfsPointerCount == 1),
                "existing canonical LFS rule and pointer imported");
            True(initialRules.Rules.Any(rule => rule.Pattern == "*.txt" && rule.Track && rule.Lf
                                              && rule.LfAttributeCount == 1),
                "existing canonical LF rule imported from check-attr truth");

            var attributePath = Path.Combine(worktree, ".gitattributes");
            var ignorePath = Path.Combine(worktree, ".gitignore");
            var originalAttributeBytes = await File.ReadAllBytesAsync(attributePath);
            var originalIgnoreBytes = await File.ReadAllBytesAsync(ignorePath);
            await File.WriteAllBytesAsync(Path.Combine(worktree, "workbook.xlsx"), [9, 8, 7, 6]);

            var preview = await service.SetAsync("main", "*.xlsx", track: true, lfs: true, lf: false, apply: false);
            True(preview.Success && preview.Preview is { Changed: true, Applied: false }, "LFS preview produced");
            BytesEqual(originalAttributeBytes, await File.ReadAllBytesAsync(attributePath), "preview preserves attributes");
            BytesEqual(originalIgnoreBytes, await File.ReadAllBytesAsync(ignorePath), "preview preserves ignore");

            var lfsApplied = await service.SetAsync("main", "*.xlsx", true, true, false, apply: true);
            True(lfsApplied.Success && lfsApplied.Preview is { Applied: true }, "LFS rule applied");
            Contains(await File.ReadAllTextAsync(attributePath),
                "*.xlsx filter=lfs diff=lfs merge=lfs -text", "canonical managed LFS line");
            var xlsxPointer = await GitRunner.RunAsync(worktree, ["show", ":workbook.xlsx"]);
            Ensure(xlsxPointer, "read staged xlsx");
            True(xlsxPointer.Output.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal),
                "LFS clean filter created index pointer");
            var afterLfs = await service.ListAsync("main");
            True(afterLfs.Success && afterLfs.Rules.Any(rule =>
                    rule.Pattern == "*.xlsx" && rule.LfsAttributeCount == 1 && rule.LfsPointerCount == 1),
                "LFS list verifies both final attribute and staged pointer");

            Directory.CreateDirectory(Path.Combine(worktree, "资料 目录"));
            var specialRelative = "资料 目录/数据[1].lfcase";
            await File.WriteAllTextAsync(
                Path.Combine(worktree, "资料 目录", "数据[1].lfcase"), "第一行\r\n第二行\r\n", new UTF8Encoding(false));
            var lfApplied = await service.SetAsync("main", "*.lfcase", true, false, true, apply: true);
            True(lfApplied.Success, $"LF rule applied: {lfApplied.Message}");
            var lfAttr = await GitRunner.RunAsync(
                worktree, ["check-attr", "text", "eol", "--", specialRelative.Replace('/', '\\')]);
            Ensure(lfAttr, "check special path attributes");
            Contains(lfAttr.Output, "text: set", "LF text attribute final truth");
            Contains(lfAttr.Output, "eol: lf", "LF eol attribute final truth");
            var indexText = await GitRunner.RunAsync(worktree, ["show", $":{specialRelative}"]);
            Ensure(indexText, "read staged LF file");
            True(!indexText.Output.Contains('\r'), "LF file normalized in index");

            var conflict = await service.SetAsync("main", "*.bad", true, true, true, apply: false);
            True(!conflict.Success && conflict.Message.Contains("互斥"), "LFS and LF rejected together");
            var invalidIgnored = await service.SetAsync("main", "*.bad", false, true, false, apply: false);
            True(!invalidIgnored.Success, "ignored format cannot enable LFS");

            var localTmp = Path.Combine(worktree, "保留 文件[1].tmp");
            await File.WriteAllTextAsync(localTmp, "keep locally", new UTF8Encoding(false));
            Ensure(await GitRunner.RunAsync(worktree, ["add", "--", "保留 文件[1].tmp"]), "track tmp before ignore");
            var ignoredRule = await service.SetAsync("main", "*.tmp", false, false, false, apply: true);
            True(ignoredRule.Success, $"ignore rule applied: {ignoredRule.Message}");
            True(File.Exists(localTmp), "track=false preserves working-tree file");
            Contains(await File.ReadAllTextAsync(ignorePath), "*.tmp", "ignore rule written");
            var tmpTracked = await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "保留 文件[1].tmp"]);
            True(!tmpTracked.Success, "track=false removes index entry");

            var trackedAgain = await service.SetAsync("main", "*.tmp", true, false, false, apply: true);
            True(trackedAgain.Success, $"track rule applied: {trackedAgain.Message}");
            Ensure(await GitRunner.RunAsync(worktree, ["ls-files", "--error-unmatch", "--", "保留 文件[1].tmp"]),
                "track=true restores index entry");
            var removed = await service.RemoveAsync("main", "*.tmp", apply: true);
            True(removed.Success, $"remove rule: {removed.Message}");
            True(File.Exists(localTmp), "removing rule preserves local file");

            BytesEqual(OutsideManagedBytes(originalAttributeBytes),
                OutsideManagedBytes(await File.ReadAllBytesAsync(attributePath)),
                "content outside managed attributes block byte-preserved");
            BytesEqual(OutsideManagedBytes(originalIgnoreBytes),
                OutsideManagedBytes(await File.ReadAllBytesAsync(ignorePath)),
                "content outside managed ignore block byte-preserved");
            True((await File.ReadAllBytesAsync(attributePath)).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()),
                "attributes BOM preserved");
            Contains(await File.ReadAllTextAsync(attributePath), "\r\n", "attributes CRLF preserved");

            var registry = new CommandRegistry();
            var inventory = new FormatInventoryService(projects, new MemoryLog(), root);
            GitRuleCommands.RegisterAll(registry, service, inventory, projects);
            var commandNames = registry.All().Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            True(commandNames.SetEquals([
                    "git.rule.list", "git.rule.set", "git.rule.batch-set", "git.rule.remove", "git.rule.scan",
                    "git.rule.review", "git.rule.sync",
                ]),
                "command catalog contains the V2.9 combined review command and no split legacy commands");
            True(!commandNames.Contains("git.rule.gaps") && !commandNames.Contains("git.rule.suggest"),
                "split gap and suggestion commands are no longer registered");
            True(!commandNames.Any(name => name.StartsWith("attr.", StringComparison.OrdinalIgnoreCase)),
                "old attr commands absent");
            // V2.4.4:只读性由描述符自描述,不再查名字白名单。判据升级为「真值 + 解释结果」。
            True(registry.TryGet("git.rule.list", out var ruleList) && ruleList.Readonly
                 && McpExposurePolicy.State(ruleList) == "readonly",
                "git.rule.list is readonly MCP projection");
            True(registry.TryGet("git.rule.review", out var ruleReview) && ruleReview.Readonly
                 && McpExposurePolicy.State(ruleReview) == "readonly",
                "git.rule.review is readonly across UI, Web and MCP projections");
            True(registry.TryGet("git.rule.set", out var ruleSet) && !ruleSet.Readonly
                 && McpExposurePolicy.State(ruleSet) != "readonly",
                "git.rule.set requires standard MCP policy");
            True(registry.TryGet("git.rule.batch-set", out var batchSet) && !batchSet.Readonly
                 && McpExposurePolicy.State(batchSet) != "readonly",
                "git.rule.batch-set requires standard MCP policy");
            var commandLog = new MemoryLog();
            var commandBus = new CommandBus(registry, commandLog);
            var executed = false;
            commandBus.Executed += (_, source, result) => executed = source == "UI" && result.Success;
            var listCommand = await commandBus.ExecuteAsync("git.rule.list name=main", "UI");
            True(listCommand.Success && executed,
                $"automatic rule load completes through CommandBus lifecycle; success={listCommand.Success}, " +
                $"executed={executed}, message={listCommand.Message}");

            var commandChanges = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new GitFileRuleChange("*.batcha", true, false, true),
                new GitFileRuleChange("*.batchc", true, true, false),
            });
            var batchCommand = await commandBus.ExecuteAsync(
                $"git.rule.batch-set name=main changes={CommandParser.QuoteArg(commandChanges)} apply=false", "UI");
            True(batchCommand.Success && batchCommand.Data is GitFileRuleBatchPreview { Items.Count: 2 },
                "batch JSON executes through CommandBus and returns typed preview");

            await File.WriteAllTextAsync(Path.Combine(worktree, "proposal.md"), "review me\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, "mystery.reviewunknown"), "unknown\n");
            var directoryCandidate = Path.Combine(worktree, "generated-review");
            Directory.CreateDirectory(directoryCandidate);
            for (var index = 0; index < 50; index++)
                await File.WriteAllTextAsync(Path.Combine(directoryCandidate, $"item-{index:00}"), "generated\n");

            var scanCommand = await commandBus.ExecuteAsync("git.rule.scan name=main refresh=true", "UI");
            True(scanCommand.Success && scanCommand.Data is InventoryReport
                {
                    ProjectCount: 1,
                    Formats.Count: > 0,
                }, "format inventory executes through CommandBus and returns structured data");
            var reviewCommand = await commandBus.ExecuteAsync("git.rule.review name=main", "UI");
            True(reviewCommand.Success && reviewCommand.Data is GitRuleReviewReport
                {
                    Gaps.Directories.Count: > 0,
                    Suggestions.Count: > 0,
                    UnknownFormats.Count: > 0,
                    SuggestedFileCount: > 0,
                } review && review.Gaps.UndecidedCount > 0 && review.SuggestedCoverageRate > 0,
                "one review returns suggestions, unknown formats and directory candidates from one scan");
            var reviewJson = System.Text.Json.JsonSerializer.SerializeToElement(reviewCommand.Data);
            True(System.Text.Json.JsonSerializer.Deserialize<GitRuleReviewReport>(
                        reviewJson.GetRawText(),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    is { Gaps.Formats.Count: > 0 },
                "the combined review contract survives JSON projection");

            var cachedReview = await commandBus.ExecuteAsync("git.rule.review name=main", "UI");
            True(cachedReview.Data is GitRuleReviewReport { Gaps.CachedProjects: 1 },
                "repeated review reuses the project inventory cache");
            var missingReview = await commandBus.ExecuteAsync("git.rule.review name=missing-project", "UI");
            True(!missingReview.Success, "review reports a controlled failure when no scan target exists");

            True((await service.SetAsync("main", "*.md", true, false, true, apply: true)).Success,
                "apply suggested text rule for zero-gap regression");
            True((await service.SetAsync("main", "*.reviewunknown", false, false, false, apply: true)).Success,
                "apply manual decision for unknown format");
            True((await service.SetAsync("main", "*.tmp", false, false, false, apply: true)).Success,
                "apply suggested temporary-file decision");
            True((await service.SetAsync("main", "generated-review/", false, false, false, apply: true)).Success,
                "apply directory decision for zero-gap regression");
            var resolvedReview = await commandBus.ExecuteAsync("git.rule.review name=main", "UI");
            True(resolvedReview.Success && resolvedReview.Data is GitRuleReviewReport resolved
                 && resolved.Gaps.UndecidedCount == 0
                 && resolved.Gaps.Formats.Count == 0
                 && resolved.Gaps.Directories.Count == 0
                 && resolved.Suggestions.Count == 0
                 && resolved.UnknownFormats.Count == 0
                 && resolved.SuggestedCoverageRate == 1d,
                "review reports zero undecided items after every decision is applied: " +
                resolvedReview.Message);

            Ensure(await GitRunner.RunAsync(worktree, ["rm", "-f", "--", "asset.bin", "workbook.xlsx"]),
                "remove all LFS pointers for empty JSON regression");
            var emptyLfsList = await service.ListAsync("main");
            True(emptyLfsList.Success, $"null Git LFS files list is treated as empty: {emptyLfsList.Message}");

            var headAfter = await GitRunner.RunAsync(worktree, ["rev-parse", "HEAD"]);
            Ensure(headAfter, "read final HEAD");
            Equal(headBefore.Output, headAfter.Output, "rule operations do not commit or rewrite history");

            const string realRoot = @"C:\OneHistory\OneHistory-Projects";
            const string realBare = @"C:\OneHistory\OneHistory-Projects\OneHistory-Projects.git";
            var template = Path.Combine(realRoot, "0000-000-Template");
            if (args.Contains("--real-template", StringComparer.OrdinalIgnoreCase)
                && Directory.Exists(template) && Directory.Exists(realBare))
            {
                var realSettings = new MemorySettings();
                realSettings.Set(ProjectService.KeyBareRepo, realBare);
                realSettings.Set(ProjectService.KeyWorktreeRoot, realRoot);
                realSettings.Set(ProjectService.KeyBaseBranch, "0000-000-Template");
                var realService = new GitFileRuleService(new ProjectService(realSettings, _ => false, root));
                var realAttributes = SnapshotOptional(Path.Combine(template, ".gitattributes"));
                var realIgnore = SnapshotOptional(Path.Combine(template, ".gitignore"));
                var templateRules = await realService.ListAsync("0000-000-Template");
                True(templateRules.Success, $"template readonly list: {templateRules.Message}");
                BytesEqual(realAttributes, SnapshotOptional(Path.Combine(template, ".gitattributes")),
                    "template attributes unchanged by list");
                BytesEqual(realIgnore, SnapshotOptional(Path.Combine(template, ".gitignore")),
                    "template ignore unchanged by list");
            }

        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static byte[] OutsideManagedBytes(byte[] bytes)
    {
        var begin = Encoding.ASCII.GetBytes("# OneHistoryStudio managed begin");
        var end = Encoding.ASCII.GetBytes("# OneHistoryStudio managed end");
        var beginIndex = bytes.AsSpan().IndexOf(begin);
        if (beginIndex < 0)
            return bytes;
        var relativeEnd = bytes.AsSpan(beginIndex).IndexOf(end);
        if (relativeEnd < 0)
            return bytes;
        var endIndex = beginIndex + relativeEnd + end.Length;
        if (endIndex < bytes.Length && bytes[endIndex] == (byte)'\r')
            endIndex++;
        if (endIndex < bytes.Length && bytes[endIndex] == (byte)'\n')
            endIndex++;
        return [.. bytes.AsSpan(0, beginIndex), .. bytes.AsSpan(endIndex)];
    }

    private static byte[] SnapshotOptional(string path) => File.Exists(path) ? File.ReadAllBytes(path) : [];
}
