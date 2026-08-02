using System.Collections.ObjectModel;
using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class AssemblyTreeNode
{
    public AssemblyTreeNode(string displayName, string sourcePath, string stateText)
    {
        DisplayName = displayName;
        SourcePath = sourcePath;
        StateText = stateText;
    }

    public string DisplayName { get; }
    public string SourcePath { get; }
    public string StateText { get; }
    public ObservableCollection<AssemblyTreeNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;

    public static AssemblyTreeNode Build(AssemblyProbeResult probe)
    {
        var root = new AssemblyTreeNode(
            Path.GetFileName(probe.SourceAssemblyPath),
            probe.SourceAssemblyPath,
            "源装配体");
        var byId = new Dictionary<string, AssemblyTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in probe.Occurrences)
        {
            var name = occurrence.OccurrenceId.Split('/').LastOrDefault() ?? occurrence.OccurrenceId;
            var states = new List<string>();
            states.Add(occurrence.IsSubAssembly ? "子装配" : "零件");
            if (occurrence.IsSuppressed)
                states.Add("抑制，跳过");
            if (occurrence.IsHidden)
                states.Add("隐藏，仍插入");
            if (!string.IsNullOrWhiteSpace(occurrence.Diagnostic))
                states.Add(occurrence.Diagnostic);
            var node = new AssemblyTreeNode(name, occurrence.SourcePath, string.Join("；", states));
            byId[occurrence.OccurrenceId] = node;
            if (occurrence.ParentId is not null && byId.TryGetValue(occurrence.ParentId, out var parent))
                parent.Children.Add(node);
            else
                root.Children.Add(node);
        }
        return root;
    }
}
