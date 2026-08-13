using System.Xml.Linq;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Mcp;
using HistoryJanus.Git;
using HistoryJanus.Views;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>只读提交 DAG：编号主线、平行泳道（含已合并历史）与独立图谱窗口。</summary>
internal static class BranchGraphSuite
{
    private const string baseBranch = "0000-000-Template";
    private const string parentBranch = "2026-001-Parent";
    private const string childBranch = "2026-002-Child";
    private const string aliasAiName = "ai/2026-002-Child/abcdef0-1-on-main";
    private const string mergedAiName = "ai/2026-002-Child/side-merged";
    private const string openAiName = "ai/2026-002-Child/open-fix";

    public static async Task RunAsync(string[] args)
    {
        VerifyGraphLayout();
        var root = TemporaryDirectory("branch-graph");
        var seed = Path.Combine(root, "seed");
        var bare = Path.Combine(root, "projects.git");
        var template = Path.Combine(root, baseBranch);
        var parent = Path.Combine(root, parentBranch);
        var child = Path.Combine(root, childBranch);
        Directory.CreateDirectory(root);

        try
        {
            Ensure(await GitRunner.RunAsync(root, ["init", "-b", baseBranch, seed]), "init seed");
            await ConfigureIdentity(seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "template\n");
            Ensure(await GitRunner.RunAsync(seed, ["add", "."]), "add template");
            Ensure(await GitRunner.RunAsync(seed, ["commit", "-m", "template root"]), "commit template");
            var templateSha = Sha(await GitRunner.RunAsync(seed, ["rev-parse", "HEAD"]));
            Ensure(await GitRunner.RunAsync(root, ["clone", "--bare", seed, bare]), "clone project bare");

            Ensure(await GitRunner.RunAsync(bare, ["worktree", "add", template, baseBranch]), "worktree template");
            await ConfigureIdentity(template);
            Ensure(await GitRunner.RunAsync(bare,
                ["worktree", "add", "-b", parentBranch, parent, baseBranch]), "worktree parent");
            await ConfigureIdentity(parent);
            await CommitFile(parent, "parent.txt", "parent one\n", "parent one");
            var parentSha = Sha(await GitRunner.RunAsync(parent, ["rev-parse", "HEAD"]));

            Ensure(await GitRunner.RunAsync(bare,
                ["worktree", "add", "-b", childBranch, child, parentBranch]), "worktree child");
            await ConfigureIdentity(child);
            await CommitFile(child, "child.txt", "child one\n", "child one");
            var childOneSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));

            var side = Path.Combine(root, "side");
            Ensure(await GitRunner.RunAsync(bare,
                ["worktree", "add", "-b", "side-merge", side, parentBranch]), "worktree side");
            await ConfigureIdentity(side);
            await CommitFile(side, "side.txt", "side change\n", "side change");
            var sideSha = Sha(await GitRunner.RunAsync(side, ["rev-parse", "HEAD"]));

            Ensure(await GitRunner.RunAsync(child, ["merge", "--no-ff", "side-merge", "-m", "merge side"]),
                "merge side into child");
            var mergeSha = Sha(await GitRunner.RunAsync(child, ["rev-parse", "HEAD"]));

            Ensure(await GitRunner.RunAsync(bare,
                ["update-ref", $"refs/heads/{aliasAiName}", childOneSha]), "create mainline-alias ai ref");
            Ensure(await GitRunner.RunAsync(bare,
                ["update-ref", $"refs/heads/{mergedAiName}", sideSha]), "create merged ai ref");

            var openWork = Path.Combine(root, "open-ai");
            Ensure(await GitRunner.RunAsync(bare,
                ["worktree", "add", "-b", openAiName, openWork, childBranch]), "worktree open ai");
            await ConfigureIdentity(openWork);
            await CommitFile(openWork, "open.txt", "still open\n", "open work");
            var openSha = Sha(await GitRunner.RunAsync(openWork, ["rev-parse", "HEAD"]));

            var settings = new MemorySettings();
            settings.Set(ProjectService.KeyBareRepo, bare);
            settings.Set(ProjectService.KeyWorktreeRoot, root);
            settings.Set(ProjectService.KeyBaseBranch, baseBranch);
            var projects = new ProjectService(settings, _ => true, root);
            var graph = new GraphService(projects);

            var missing = await graph.GetSummaryAsync("2026-999-Missing");
            True(!missing.Success && missing.Message.Contains("不存在", StringComparison.Ordinal),
                $"missing project fails clearly: {missing.Message}");

            var branches = await graph.GetBranchesAsync(childBranch);
            True(branches.Success && branches.Report != null, $"branches loaded: {branches.Message}");
            var branchReport = branches.Report!;
            Equal(childBranch, branchReport.Mainline?.Name, "child is mainline");
            True(branchReport.Inheritance.Count == 0, "inheritance parent chain is not a graph lane");
            True(branchReport.Parallels.Any(item => item.Name == mergedAiName && !item.IsOpen),
                "merged ai/ ref keeps a closed parallel lane");
            True(branchReport.Parallels.Any(item => item.Name == openAiName && item.IsOpen),
                "unmerged ai/ ref keeps an open parallel lane");
            True(!branchReport.Parallels.Any(item => item.Name == aliasAiName),
                "ai/ tip that only aliases mainline first-parent is not a lane");
            True(branchReport.AiWork.All(item => item.Kind == BranchKind.AiWork),
                "ai refs carry AiWork kind");
            True(branchReport.Mainline?.Kind == BranchKind.Mainline, "mainline kind");

            var commits = await graph.GetCommitsAsync(childBranch, limit: 50);
            True(commits.Success && commits.Report != null, $"commits loaded: {commits.Message}");
            var report = commits.Report!;
            True(!report.Nodes.Any(node => node.Sha == templateSha),
                "template history is cut off from numbered-project graph");
            True(!report.Nodes.Any(node => node.Sha == parentSha),
                "parent-branch history is cut off from numbered-project graph");
            True(report.Nodes.Any(node => node.Sha == childOneSha),
                "numbered-branch unique commit stays on the graph");
            True(report.Nodes.Any(node => node.Sha == sideSha),
                "merged parallel keeps pre-merge unique commits");
            True(report.Nodes.Any(node => node.Sha == openSha),
                "unmerged parallel tip is on the graph");
            True(report.Lanes.Count >= 3 && report.Lanes[0].Name == childBranch,
                "lanes start with numbered mainline then parallels");
            True(report.Lanes.Any(item => item.Name == mergedAiName && !item.IsOpen),
                "commit lanes mark merged parallel closed");
            True(report.Lanes.Any(item => item.Name == openAiName && item.IsOpen),
                "commit lanes mark unmerged parallel open");
            True(report.Edges.Any(edge =>
                    edge.ToSha == mergeSha && edge.IsMergeParent && edge.FromSha == sideSha),
                "merge commit exposes merge-parent edge");
            True(report.Edges.Any(edge =>
                    edge.ToSha == mergeSha && !edge.IsMergeParent),
                "merge commit exposes first-parent edge");

            var layout = GraphLayout.Arrange(report, childBranch);
            True(layout.Lanes.Any(lane => !lane.IsOpen && lane.Index > 0),
                "merged parallel still occupies a historical row");
            True(layout.Nodes.Any(node => node.Node.Sha == sideSha && node.LaneIndex > 0 && !node.IsOpenTip),
                "merged parallel row ends without a right-side tip");
            True(layout.Nodes.Any(node => node.Node.Sha == openSha && node.IsOpenTip),
                "unmerged parallel draws a right-side tip");
            True(layout.Nodes.Any(node => node.Node.Sha == mergeSha && node.LaneIndex == 0),
                "merge commit stays on the mainline");

            var node = await graph.GetNodeAsync(childBranch, mergeSha);
            True(node.Success && node.Detail != null, $"node detail loaded: {node.Message}");
            True(node.Detail!.ParentEdges.Any(edge => edge.IsMergeParent && edge.FromSha == sideSha),
                "node detail lists merge parent");

            var outside = await graph.GetNodeAsync(childBranch, templateSha);
            True(!outside.Success && outside.Message.Contains("范围", StringComparison.Ordinal),
                $"template commit is out of numbered-project scope: {outside.Message}");

            Ensure(await GitRunner.RunAsync(bare, ["worktree", "remove", "--force", side]),
                "remove merged side worktree");
            Ensure(await GitRunner.RunAsync(bare, ["update-ref", "-d", "refs/heads/side-merge"]),
                "delete merged side branch");
            Ensure(await GitRunner.RunAsync(bare, ["update-ref", "-d", $"refs/heads/{mergedAiName}"]),
                "delete merged ai ref");

            var recovered = await graph.GetCommitsAsync(childBranch, limit: 50);
            True(recovered.Success && recovered.Report != null,
                $"commits after deleting merged refs: {recovered.Message}");
            True(recovered.Report!.Nodes.Any(item => item.Sha == sideSha),
                "unique commits of a deleted merged branch remain reachable from the merge");
            True(recovered.Report.Lanes.Any(item => item.TargetSha == sideSha && !item.IsOpen),
                "merge second-parent recovers a closed historical lane after the branch is deleted");
            True(!recovered.Report.Lanes.Any(item => item.Name == mergedAiName),
                "deleted ai/ ref is no longer listed as a named lane");
            var recoveredLayout = GraphLayout.Arrange(recovered.Report, childBranch);
            True(recoveredLayout.Nodes.Any(item =>
                    item.Node.Sha == sideSha && item.LaneIndex > 0 && !item.IsOpenTip),
                "recovered merge history still occupies a row without a right-side tip");

            var registry = new CommandRegistry();
            GraphCommands.RegisterAll(registry, graph);
            var commands = registry.All().ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);
            True(commands.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([
                "janus.graph.summary", "janus.graph.branches", "janus.graph.commits", "janus.graph.node",
            ]), "graph command catalog complete");
            True(commands.Values.All(descriptor => descriptor.Readonly
                    && McpExposurePolicy.State(descriptor) == "readonly"
                    && descriptor.ConfirmPrompt == null),
                "graph commands are readonly with no confirmation");
            True(!registry.All().Any(descriptor => descriptor.Name.StartsWith("janus.proj.", StringComparison.Ordinal)),
                "graph suite registers no project mutation commands");
        }
        finally
        {
            if (Directory.Exists(root))
                DeleteTree(root);
        }
    }

    private static void VerifyGraphLayout()
    {
        True(File.Exists(Path.Combine(RepoRoot, "Views", "GraphView.xaml")),
            "GraphView.xaml exists");

        var graphView = File.ReadAllText(Path.Combine(RepoRoot, "Views", "GraphView.xaml"));
        Contains(graphView, "Shell.Brush.Surface",
            "GraphView uses host surface token");
        Contains(graphView, "Shell.Brush.Hairline",
            "GraphView uses host hairline token");

        True(!graphView.Contains("LaneLegend", StringComparison.Ordinal),
            "GraphView has no left-side lane legend");
        Contains(graphView, "HorizontalScrollBarVisibility=\"Hidden\"",
            "graph hides the horizontal scrollbar and pans by dragging");
        Contains(graphView, "VerticalScrollBarVisibility=\"Hidden\"",
            "graph hides the vertical scrollbar and pans by dragging");
        Contains(graphView, "PreviewMouseWheel",
            "graph swallows the mouse wheel instead of scrolling");

        var graphViewCode = File.ReadAllText(Path.Combine(RepoRoot, "Views", "GraphView.xaml.cs"));
        Contains(graphViewCode, "CaptureMouse",
            "graph pans the canvas by dragging the background");
        Contains(graphViewCode, "HitGraphNode",
            "graph drag-pan does not steal commit node clicks");

        var layoutSource = File.ReadAllText(Path.Combine(RepoRoot, "Views", "GraphLayout.cs"));
        Contains(layoutSource, "IsOpenTip",
            "layout distinguishes open parallel tips from merged historical rows");

        var overview = XDocument.Load(Path.Combine(RepoRoot, "Views", "OverviewView.xaml"));
        var overviewSource = overview.ToString();
        True(!overviewSource.Contains("GraphHost", StringComparison.Ordinal),
            "overview no longer embeds the graph");
        True(!overview.Descendants().Any(element => element.Name.LocalName == "GridSplitter"),
            "overview is a full-height project list");

        var module = File.ReadAllText(Path.Combine(RepoRoot, "Module", "HistoryJanusUiModule.cs"));
        var windowIds = System.Text.RegularExpressions.Regex.Matches(
                module, @"Id = ""(?<id>[a-z]+)""", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(match => match.Groups["id"].Value)
            .ToArray();
        True(windowIds.SequenceEqual(["overview", "graph", "projops"]),
            "module registers overview, graph, and projops");
        Contains(module, "DefaultSide = DockSide.Tab",
            "graph joins a host tab group instead of occupying the center document pane");
        Contains(module, "DefaultTabTarget = StandardWindowIds.Console",
            "graph default tab target is the host console");
    }

    private static string Sha(GitResult result)
    {
        Ensure(result, "resolve sha");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    private static Task ConfigureIdentity(string repository)
        => SmokeKit.ConfigureIdentity(repository, "Branch Graph Smoke", "branch-graph@example.invalid");
}
