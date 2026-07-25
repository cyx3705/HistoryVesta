using System.Xml.Linq;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;
using OneHistoryStudio.Views;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

/// <summary>V2.3.2 提交操作归位与四级范围。断言逐条搬自原 tests\V232Smoke\Program.cs。</summary>
internal static class V232Suite
{
    private const string parentBranch = "2026-232-Parent";
    private const string noChildBranch = "2026-233-NoChild";
    private const string childBranch = "child-main";
    private const string childRelative = "child repo 中文";

    public static async Task RunAsync(string[] args)
    {
        Environment.CurrentDirectory = RepoRoot;

        var root = Path.Combine(Environment.CurrentDirectory, "tests", $".tmp-v232-{Guid.NewGuid():N}");
        var seed = Path.Combine(root, "seed");
        var bare = Path.Combine(root, "projects.git");
        var parentRemote = Path.Combine(root, "parent-remote.git");
        var parent = Path.Combine(root, parentBranch);
        var noChild = Path.Combine(root, noChildBranch);
        var child = Path.Combine(parent, childRelative);
        var childRemote = Path.Combine(root, "child-remote.git");
        Directory.CreateDirectory(root);

        try
        {
            Ensure(await GitRunner.RunAsync(root, ["init", "-b", parentBranch, seed]), "init seed");
            await ConfigureIdentity(seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed\n");
            Ensure(await GitRunner.RunAsync(seed, ["add", "."]), "add seed");
            Ensure(await GitRunner.RunAsync(seed, ["commit", "-m", "seed"]), "commit seed");
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", seed, bare]), "clone bare");
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", seed, parentRemote]), "clone parent remote");
            Ensure(await GitRunner.RunAsync(bare, ["remote", "set-url", "origin", parentRemote]), "set parent origin");
            Ensure(await GitRunner.RunAsync(bare, ["worktree", "add", parent, parentBranch]), "add parent worktree");
            Ensure(await GitRunner.RunAsync(bare,
                ["worktree", "add", "-b", noChildBranch, noChild, parentBranch]), "add no-child worktree");
            await ConfigureIdentity(parent);
            await ConfigureIdentity(noChild);

            await CreateChild(child, childBranch, childRemote);
            Ensure(await GitRunner.RunAsync(parent, ["add", "--", childRelative]), "stage gitlink");
            Ensure(await GitRunner.RunAsync(parent, ["commit", "-m", "register child"]), "commit gitlink");
            Ensure(await GitRunner.RunAsync(bare, ["push", "--all", "origin"]), "push parent baseline");

            var settings = new MemorySettings();
            settings.Set(ProjectService.KeyBareRepo, bare);
            settings.Set(ProjectService.KeyWorktreeRoot, root);
            settings.Set(ProjectService.KeyBaseBranch, parentBranch);
            var service = new ProjectService(settings, _ => true, root);

            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "child-only change\n");
            var parentBeforeChildOnly = Sha(parent);
            var parentRemoteBeforeChildOnly = RefSha(parentRemote, parentBranch);
            var childOnly = await service.CommitAsync(parentBranch, "统一描述", null,
                RepositoryTarget.Submodules);
            True(childOnly.Outcome == CommitOutcome.Success && !childOnly.ParentExecuted
                 && childOnly.ParentPointerPending, $"child-only commit report: {childOnly.Message}");
            Equal(parentBeforeChildOnly, Sha(parent), "child-only commit leaves parent HEAD unchanged");
            Equal("统一描述", Subject(child), "single message is reused by child");
            True(GitlinkSha(parent, childRelative) != Sha(child), "parent gitlink intentionally pending");

            var childOnlyPush = await service.PushAsync(parentBranch, RepositoryTarget.Submodules);
            True(childOnlyPush.Success && !childOnlyPush.ParentPushed && childOnlyPush.ParentPointerPending,
                $"child-only push succeeds with pending pointer: {childOnlyPush.Message}");
            Equal(Sha(child), RefSha(childRemote, childBranch), "child-only push updates child remote");
            Equal(parentRemoteBeforeChildOnly, RefSha(parentRemote, parentBranch),
                "child-only push leaves parent remote unchanged");

            var closePointer = await service.CommitAsync(parentBranch, "收口父指针", null, RepositoryTarget.Both);
            True(closePointer.Outcome == CommitOutcome.Success && closePointer.ParentExecuted
                 && !closePointer.ParentPointerPending, $"both commit closes pointer: {closePointer.Message}");
            Equal(Sha(child), GitlinkSha(parent, childRelative), "both commit records child HEAD");
            var bothPush = await service.PushAsync(parentBranch, RepositoryTarget.Both);
            True(bothPush.Success && bothPush.ParentPushed, "both push updates parent after child");
            Equal(Sha(parent), RefSha(parentRemote, parentBranch), "both push updates parent remote");

            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "batch child-only\n");
            var parentBeforeBatchChildren = Sha(parent);
            var noChildBeforeBatch = Sha(noChild);
            var parentRemoteBeforeBatch = RefSha(parentRemote, parentBranch);
            var allChildren = await service.CommitAllAsync(
                "全部子模块描述", null, null, RepositoryTarget.Submodules);
            True(allChildren.Success && allChildren.ParentPointerPendingCount == 1,
                $"all submodules commit: {allChildren.Message}");
            Equal(parentBeforeBatchChildren, Sha(parent), "all submodules does not commit parent");
            Equal(noChildBeforeBatch, Sha(noChild), "all submodules does not commit no-child branch");
            var allChildrenPush = await service.PushAllAsync(RepositoryTarget.Submodules);
            True(allChildrenPush.Success && !allChildrenPush.ParentPushed
                 && allChildrenPush.ParentPointerPendingCount == 1,
                $"all submodules push: {allChildrenPush.Message}");
            Equal(parentRemoteBeforeBatch, RefSha(parentRemote, parentBranch),
                "all submodules push leaves all parent refs unchanged");

            var allBoth = await service.CommitAllAsync("全部完整收口", null, null, RepositoryTarget.Both);
            True(allBoth.Success && allBoth.ParentPointerPendingCount == 0,
                $"all both commit closes all pointers: {allBoth.Message}");
            var allBothPush = await service.PushAllAsync(RepositoryTarget.Both);
            True(allBothPush.Success && allBothPush.ParentPushed, "all both pushes bare parent repo");
            Equal(Sha(parent), RefSha(parentRemote, parentBranch), "all both updates parent remote");

            var noChildren = await service.CommitAsync(
                noChildBranch, "no child", null, RepositoryTarget.Submodules);
            True(noChildren.Outcome == CommitOutcome.Skipped && !noChildren.ParentExecuted,
                "submodules target skips project without children");
            var noChildrenPush = await service.PushAsync(noChildBranch, RepositoryTarget.Submodules);
            True(noChildrenPush.Success && !noChildrenPush.ParentPushed,
                "submodules push skips project without children");

            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "legacy parent-only\n");
            var legacyChildHead = Sha(child);
            var legacyParentOnly = await service.CommitAsync(parentBranch, "legacy false", null,
                includeSubmodules: false);
            Equal(CommitOutcome.Skipped, legacyParentOnly.Outcome, "legacy false remains parent-only");
            Equal(legacyChildHead, Sha(child), "legacy false does not commit child");
            Ensure(await GitRunner.RunAsync(child, ["reset", "--hard", "HEAD"]), "clean legacy false change");
            await File.AppendAllTextAsync(Path.Combine(child, "tracked.txt"), "legacy both\n");
            var legacyBoth = await service.CommitAsync(parentBranch, "legacy parent", null,
                includeSubmodules: true, submoduleMessage: "legacy child");
            Equal(CommitOutcome.Success, legacyBoth.Outcome, "legacy true remains both");
            Equal("legacy child", Subject(child), "legacy submsg remains supported");

            Equal(RepositoryTarget.Parent,
                ProjectCommands.ResolveRepositoryTarget(null, false), "resolver default parent");
            Equal(RepositoryTarget.Both,
                ProjectCommands.ResolveRepositoryTarget(null, true), "resolver legacy true both");
            Equal(RepositoryTarget.Submodules,
                ProjectCommands.ResolveRepositoryTarget("submodules", false), "target overrides false");
            Equal(RepositoryTarget.Parent,
                ProjectCommands.ResolveRepositoryTarget("parent", true), "target overrides true");

            Equal("proj.commit name=Demo msg=说明 target=submodules",
                ProjectOperationCommandBuilder.BuildCommit(
                    ProjectOperationMode.CurrentSubmodules, "Demo", "说明"), "current child command");
            Equal("proj.commit name=Demo msg=说明 target=both",
                ProjectOperationCommandBuilder.BuildCommit(
                    ProjectOperationMode.CurrentBoth, "Demo", "说明"), "current both command");
            Equal("proj.commitall msg=说明 target=submodules",
                ProjectOperationCommandBuilder.BuildCommit(
                    ProjectOperationMode.AllSubmodules, null, "说明"), "all child command");
            Equal("proj.pushall target=both",
                ProjectOperationCommandBuilder.BuildPush(ProjectOperationMode.AllBoth, null), "all both push command");

            var registry = new CommandRegistry();
            ProjectCommands.RegisterAll(registry, service, null!);
            foreach (var commandName in new[] { "proj.commit", "proj.push", "proj.commitall", "proj.pushall" })
            {
                True(registry.TryGet(commandName, out var descriptor), $"{commandName} registered");
                var target = descriptor.Parameters.Single(parameter => parameter.Name == "target");
                True(target.Default == null && target.AllowedValues is ["parent", "submodules", "both"],
                    $"{commandName} target enum schema");
            }

            VerifyXamlLayout();
            Console.WriteLine("V232Smoke: PASS");
        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static void VerifyXamlLayout()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var projectPath = Path.Combine(Environment.CurrentDirectory,
            "Views", "ProjectOperationsView.xaml");
        var overviewPath = Path.Combine(Environment.CurrentDirectory,
            "Views", "OverviewView.xaml");
        var project = XDocument.Load(projectPath);
        var overview = XDocument.Load(overviewPath);
        var named = project.Descendants()
            .Select(element => element.Attribute(x + "Name")?.Value)
            .Where(name => name != null).ToList();
        Equal(1, named.Count(name => name == "SelectedCommitMessageBox"), "one commit message box");
        Equal(4, project.Descendants().Count(element => element.Name.LocalName == "RadioButton"
            && element.Attribute("GroupName")?.Value == "OperationMode"), "four operation segments");
        True(!overview.ToString().Contains("CommitAll", StringComparison.Ordinal)
             && !overview.ToString().Contains("PushAll", StringComparison.Ordinal)
             && !overview.ToString().Contains("Submodule", StringComparison.OrdinalIgnoreCase),
            "overview contains no git write controls");

        var groups = project.Descendants().Where(element => element.Name.LocalName == "GroupBox")
            .ToDictionary(element => element.Attribute("Header")?.Value ?? string.Empty);
        Equal(3, CountGridRows(groups["项目"]), "project group has three rows");
        Equal(4, CountGridRows(groups["所选项目提交与推送"]), "operation group has four rows");
    }

    private static int CountGridRows(XElement group)
        => group.Descendants().First(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements().Count(element => element.Name.LocalName == "RowDefinition");

    private static async Task CreateChild(string path, string branch, string remote)
    {
        Ensure(await GitRunner.RunAsync(Path.GetDirectoryName(path)!, ["init", "-b", branch, path]),
            "init child");
        await ConfigureIdentity(path);
        await File.WriteAllTextAsync(Path.Combine(path, "tracked.txt"), "tracked\n");
        Ensure(await GitRunner.RunAsync(path, ["add", "."]), "add child");
        Ensure(await GitRunner.RunAsync(path, ["commit", "-m", "child seed"]), "commit child");
        Ensure(await GitRunner.RunAsync(Path.GetDirectoryName(remote)!, ["init", "--bare", remote]),
            "init child remote");
        Ensure(await GitRunner.RunAsync(path, ["remote", "add", "origin", remote]), "add child origin");
        Ensure(await GitRunner.RunAsync(path, ["push", "origin", branch]), "push child baseline");
    }

    private static Task ConfigureIdentity(string repository)
        => SmokeKit.ConfigureIdentity(repository, "V232 Smoke", "v232@example.invalid");
}
