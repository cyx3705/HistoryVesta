using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>
/// 提交图谱时间轴泳道布局。X 为提交时间（左旧右新），Y 按主线与平行分支动态分行。
/// 已合并平行行保留合并前节点，右侧不画 tip。纯计算：不引用 WPF，不发指令。
/// </summary>
internal static class GraphLayout
{
    internal const double NodeWidth = 148;
    internal const double NodeHeight = 40;
    internal const double LaneHeight = 88;
    internal const double PaddingX = 20;
    internal const double PaddingY = 16;
    internal const double MinGap = 18;

    internal sealed record LaneInfo(int Index, string Title, bool IsOpen);

    internal sealed record PlacedNode(
        GraphCommitNode Node,
        int LaneIndex,
        string LaneTitle,
        bool LaneIsOpen,
        bool IsOpenTip,
        double X,
        double Y);

    internal sealed record PlacedEdge(
        GraphEdge Edge,
        double X1,
        double Y1,
        double X2,
        double Y2);

    internal sealed record Result(
        IReadOnlyList<PlacedNode> Nodes,
        IReadOnlyList<PlacedEdge> Edges,
        IReadOnlyList<LaneInfo> Lanes,
        double Width,
        double Height);

    internal static double LaneTop(int index)
        => PaddingY + index * LaneHeight;

    internal static double LaneCenterY(int index)
        => LaneTop(index) + LaneHeight / 2;

    internal static Result Arrange(GraphCommitsReport report, string projectName)
    {
        var source = report.Nodes ?? [];
        var edges = report.Edges ?? [];
        var laneRefs = report.Lanes is { Count: > 0 }
            ? report.Lanes
            : [new GraphRef { Name = projectName, Kind = BranchKind.Mainline, IsOpen = true }];
        if (source.Count == 0)
        {
            var emptyLanes = laneRefs
                .Select((item, index) => ToLaneInfo(index, item, projectName))
                .ToList();
            var emptyHeight = PaddingY * 2 + LaneHeight * Math.Max(1, emptyLanes.Count);
            return new Result([], [], emptyLanes, PaddingX * 2 + 240, emptyHeight);
        }

        var bySha = new Dictionary<string, GraphCommitNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in source)
        {
            if (!string.IsNullOrWhiteSpace(node.Sha))
                bySha[node.Sha] = node;
        }

        var assignment = AssignLanes(source, bySha, laneRefs, projectName);
        var used = CompactLanes(assignment.IndexBySha, assignment.Meta);
        var min = source.Min(node => node.CommittedAt);
        var max = source.Max(node => node.CommittedAt);
        var span = Math.Max(1d, (max - min).TotalSeconds);
        var usable = Math.Max(source.Count * (NodeWidth + MinGap), 480d);

        var placed = new Dictionary<string, PlacedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in source)
        {
            var laneIndex = used.IndexBySha.TryGetValue(node.Sha, out var index) ? index : 0;
            var meta = used.Meta[laneIndex];
            var x = PaddingX + (node.CommittedAt - min).TotalSeconds / span * usable;
            var y = LaneTop(laneIndex) + (LaneHeight - NodeHeight) / 2;
            placed[node.Sha] = new PlacedNode(
                node, laneIndex, meta.Title, meta.IsOpen, IsOpenTip: false, x, y);
        }

        foreach (var laneIndex in used.Meta.Keys.OrderBy(index => index))
        {
            var row = placed.Values
                .Where(item => item.LaneIndex == laneIndex)
                .OrderBy(item => item.X)
                .ThenBy(item => item.Node.Sha, StringComparer.Ordinal)
                .ToList();
            for (var i = 1; i < row.Count; i++)
            {
                var minX = row[i - 1].X + NodeWidth + MinGap;
                if (row[i].X < minX)
                    row[i] = row[i] with { X = minX };
            }

            foreach (var item in row)
                placed[item.Node.Sha] = item;
        }

        foreach (var lane in used.Meta.Values.Where(item => item.Index > 0 && item.IsOpen))
        {
            if (string.IsNullOrWhiteSpace(lane.TipSha) ||
                !placed.TryGetValue(lane.TipSha, out var tip))
                continue;
            placed[lane.TipSha] = tip with { IsOpenTip = true };
        }

        var width = PaddingX;
        foreach (var item in placed.Values)
            width = Math.Max(width, item.X + NodeWidth + PaddingX);

        var drawn = new List<PlacedEdge>(edges.Count);
        foreach (var edge in edges)
        {
            if (!placed.TryGetValue(edge.FromSha, out var from) ||
                !placed.TryGetValue(edge.ToSha, out var to))
                continue;
            drawn.Add(new PlacedEdge(
                edge,
                from.X + NodeWidth,
                from.Y + NodeHeight / 2,
                to.X,
                to.Y + NodeHeight / 2));
        }

        var lanes = used.Meta.Values.OrderBy(item => item.Index).Select(ToPublicLane).ToList();
        var height = PaddingY * 2 + LaneHeight * Math.Max(1, lanes.Count);
        return new Result([.. placed.Values], drawn, lanes, width, height);
    }

    internal static bool Intersects(
        double x,
        double y,
        double width,
        double height,
        double viewX,
        double viewY,
        double viewW,
        double viewH)
        => x < viewX + viewW && x + width > viewX && y < viewY + viewH && y + height > viewY;

    internal static string RefName(string raw)
    {
        var name = raw.Trim();
        const string heads = "refs/heads/";
        const string remotes = "refs/remotes/";
        if (name.StartsWith(heads, StringComparison.OrdinalIgnoreCase))
            return name[heads.Length..];
        if (name.StartsWith(remotes, StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[remotes.Length..];
            var slash = rest.IndexOf('/');
            return slash >= 0 ? rest[(slash + 1)..] : rest;
        }

        return name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
            ? name["origin/".Length..]
            : name;
    }

    private static (Dictionary<string, int> IndexBySha, List<LaneMeta> Meta) AssignLanes(
        IReadOnlyList<GraphCommitNode> nodes,
        IReadOnlyDictionary<string, GraphCommitNode> bySha,
        IReadOnlyList<GraphRef> laneRefs,
        string projectName)
    {
        var indexBySha = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var meta = new List<LaneMeta>
        {
            new(0, LaneTitle(laneRefs[0], projectName, mainline: true), true, laneRefs[0].TargetSha),
        };

        var mainTip = ResolveTip(nodes, laneRefs[0], projectName)
                      ?? nodes.MaxBy(node => node.CommittedAt);
        if (mainTip != null)
        {
            foreach (var sha in WalkFirstParent(mainTip.Sha, bySha))
                indexBySha.TryAdd(sha, 0);
        }

        for (var i = 1; i < laneRefs.Count; i++)
        {
            var lane = laneRefs[i];
            var laneIndex = meta.Count;
            var assigned = false;
            var tip = ResolveTip(nodes, lane, projectName);
            var start = tip?.Sha ?? lane.TargetSha;
            foreach (var sha in WalkFirstParent(start, bySha))
            {
                if (indexBySha.ContainsKey(sha))
                    break;
                indexBySha[sha] = laneIndex;
                assigned = true;
            }

            if (!assigned)
                continue;
            meta.Add(new LaneMeta(
                laneIndex,
                LaneTitle(lane, projectName, mainline: false),
                lane.IsOpen,
                lane.TargetSha));
        }

        foreach (var node in nodes)
        {
            if (node.Parents == null || node.Parents.Count < 2)
                continue;
            for (var i = 1; i < node.Parents.Count; i++)
            {
                var start = node.Parents[i];
                if (string.IsNullOrWhiteSpace(start) || !bySha.ContainsKey(start))
                    continue;
                if (indexBySha.TryGetValue(start, out var existing) && existing != 0)
                    continue;

                var laneIndex = meta.Count;
                var assigned = false;
                foreach (var sha in WalkFirstParent(start, bySha))
                {
                    if (indexBySha.TryGetValue(sha, out var occupied) && occupied == 0)
                        break;
                    if (indexBySha.ContainsKey(sha))
                        break;
                    indexBySha[sha] = laneIndex;
                    assigned = true;
                }

                if (!assigned)
                    continue;
                meta.Add(new LaneMeta(
                    laneIndex,
                    $"merged/{(start.Length <= 10 ? start : start[..10])}",
                    false,
                    start));
            }
        }

        foreach (var node in nodes)
            indexBySha.TryAdd(node.Sha, 0);

        return (indexBySha, meta);
    }

    private static (Dictionary<string, int> IndexBySha, Dictionary<int, LaneMeta> Meta) CompactLanes(
        Dictionary<string, int> indexBySha,
        List<LaneMeta> meta)
    {
        var remap = new Dictionary<int, int>();
        var compacted = new Dictionary<int, LaneMeta>();
        var next = 0;
        foreach (var lane in meta)
        {
            remap[lane.Index] = next;
            compacted[next] = lane with { Index = next };
            next++;
        }

        var mapped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in indexBySha)
            mapped[pair.Key] = remap.TryGetValue(pair.Value, out var index) ? index : 0;
        return (mapped, compacted);
    }

    private static GraphCommitNode? ResolveTip(
        IReadOnlyList<GraphCommitNode> nodes,
        GraphRef lane,
        string projectName)
    {
        if (!string.IsNullOrWhiteSpace(lane.TargetSha))
        {
            foreach (var node in nodes)
            {
                if (node.Sha.Equals(lane.TargetSha, StringComparison.OrdinalIgnoreCase))
                    return node;
            }
        }

        GraphCommitNode? tip = null;
        foreach (var node in nodes)
        {
            if (node.BranchRefs == null ||
                !node.BranchRefs.Any(raw =>
                    RefName(raw).Equals(lane.Name, StringComparison.OrdinalIgnoreCase) ||
                    (lane.Kind == BranchKind.Mainline &&
                     RefName(raw).Equals(projectName, StringComparison.OrdinalIgnoreCase))))
                continue;
            if (tip == null || node.CommittedAt > tip.CommittedAt)
                tip = node;
        }

        return tip;
    }

    private static IEnumerable<string> WalkFirstParent(
        string tipSha,
        IReadOnlyDictionary<string, GraphCommitNode> bySha)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = tipSha;
        while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
        {
            yield return current;
            if (!bySha.TryGetValue(current, out var node) ||
                node.Parents == null ||
                node.Parents.Count == 0)
                yield break;
            current = node.Parents[0];
        }
    }

    private static string LaneTitle(GraphRef lane, string projectName, bool mainline)
    {
        if (mainline || lane.Kind == BranchKind.Mainline)
            return "主线";
        var name = string.IsNullOrWhiteSpace(lane.Name) ? lane.FullName : lane.Name;
        var prefix = $"ai/{projectName}/";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : name;
    }

    private static LaneInfo ToLaneInfo(int index, GraphRef lane, string projectName)
        => new(index, LaneTitle(lane, projectName, index == 0), index == 0 || lane.IsOpen);

    private static LaneInfo ToPublicLane(LaneMeta meta)
        => new(meta.Index, meta.Title, meta.IsOpen);

    private sealed record LaneMeta(int Index, string Title, bool IsOpen, string TipSha);
}
