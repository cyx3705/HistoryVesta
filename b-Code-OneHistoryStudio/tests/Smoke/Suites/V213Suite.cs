using AppShell.Core;
using AppShell.Core.Mcp;
using System.Text;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

/// <summary>V2.1.3 Git 文件规则。断言逐条搬自原 tests\V213Smoke\Program.cs。</summary>
internal static class V213Suite
{
    public static async Task RunAsync(string[] args)
    {
        // 合并前本套用例在仓库父目录下运行,临时目录拼作 <父目录>\b-Code-OneHistoryStudio\tests\…
        Environment.CurrentDirectory = ParentDir;

        var root = Path.Combine(
            Environment.CurrentDirectory, "b-Code-OneHistoryStudio", "tests", $".tmp-v213-{Guid.NewGuid():N}");
        var seed = Path.Combine(root, "seed");
        var bare = Path.Combine(root, "projects.git");
        var worktree = Path.Combine(root, "sample");
        Directory.CreateDirectory(root);

        try
        {
            Ensure(await GitRunner.RunAsync(root, ["init", "-b", "main", seed]), "init seed");
            Ensure(await GitRunner.RunAsync(seed, ["config", "user.name", "V213 Smoke"]), "git user name");
            Ensure(await GitRunner.RunAsync(seed, ["config", "user.email", "v213@example.invalid"]), "git user email");
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
            Ensure(await GitRunner.RunAsync(worktree, ["config", "user.name", "V213 Smoke"]), "worktree user name");
            Ensure(await GitRunner.RunAsync(worktree, ["config", "user.email", "v213@example.invalid"]), "worktree user email");

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
                    "git.rule.list", "git.rule.set", "git.rule.remove", "git.rule.scan",
                    "git.rule.gaps", "git.rule.suggest", "git.rule.sync",
                ]),
                "command catalog contains the simplified rule commands and V2.2.1 inventory extensions");
            True(!commandNames.Any(name => name.StartsWith("attr.", StringComparison.OrdinalIgnoreCase)),
                "old attr commands absent");
            True(McpExposurePolicy.IsReadonlyAllowed("git.rule.list"), "git.rule.list is readonly MCP projection");
            True(!McpExposurePolicy.IsReadonlyAllowed("git.rule.set"), "git.rule.set requires standard MCP policy");
            var commandLog = new MemoryLog();
            var commandBus = new CommandBus(registry, commandLog);
            var executed = false;
            commandBus.Executed += (_, source, result) => executed = source == "UI" && result.Success;
            var listCommand = await commandBus.ExecuteAsync("git.rule.list name=main", "UI");
            True(listCommand.Success && executed,
                $"automatic rule load completes through CommandBus lifecycle; success={listCommand.Success}, " +
                $"executed={executed}, message={listCommand.Message}");
            True(commandLog.Snapshot().Any(entry => entry.Category == "cmd:UI")
                 && commandLog.Snapshot().Any(entry => entry.Category == CommandBus.ResultCategory),
                "automatic rule load produces normal echo and result logs");

            var scanCommand = await commandBus.ExecuteAsync("git.rule.scan name=main refresh=true", "UI");
            True(scanCommand.Success && scanCommand.Data is InventoryReport
                {
                    ProjectCount: 1,
                    Formats.Count: > 0,
                }, "V2.2.1 format inventory executes through CommandBus and returns structured data");
            var gapsCommand = await commandBus.ExecuteAsync("git.rule.gaps name=main", "UI");
            True(gapsCommand.Success && !gapsCommand.Message.Contains("git.rule.dir", StringComparison.Ordinal)
                && gapsCommand.Message.Contains("git.rule.set pattern=<目录>/ track=false", StringComparison.Ordinal),
                "directory gap guidance uses the unified git.rule.set command");

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

            Console.WriteLine("V213Smoke: PASS");
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
