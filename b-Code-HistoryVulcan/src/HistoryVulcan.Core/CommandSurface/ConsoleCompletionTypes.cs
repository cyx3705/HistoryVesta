namespace HistoryVulcan.Core.CommandSurface;

/// <summary>控制台补全候选种类。</summary>
public enum ConsoleCompletionKind
{
    /// <summary>命令名。</summary>
    Command,
    /// <summary>参数名。</summary>
    Parameter,
    /// <summary>参数值。</summary>
    Value,
}

/// <summary>单条控制台补全候选。</summary>
public sealed class ConsoleCompletionCandidate
{
    /// <summary>写入输入框的文本。</summary>
    public required string InsertText { get; init; }

    /// <summary>列表显示文本。</summary>
    public required string DisplayText { get; init; }

    /// <summary>候选说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>候选种类。</summary>
    public required ConsoleCompletionKind Kind { get; init; }
}

/// <summary>一次补全计算结果。</summary>
public sealed class ConsoleCompletionResult
{
    /// <summary>无候选结果。</summary>
    public static ConsoleCompletionResult Empty { get; } = new()
    {
        Candidates = [],
        ReplaceStart = 0,
        ReplaceLength = 0,
    };

    /// <summary>候选列表。</summary>
    public required IReadOnlyList<ConsoleCompletionCandidate> Candidates { get; init; }

    /// <summary>替换起点。</summary>
    public required int ReplaceStart { get; init; }

    /// <summary>替换长度。</summary>
    public required int ReplaceLength { get; init; }

    /// <summary>是否有候选。</summary>
    public bool HasCandidates => Candidates.Count > 0;
}
