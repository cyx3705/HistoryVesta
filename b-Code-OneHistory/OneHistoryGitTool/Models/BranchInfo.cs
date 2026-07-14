namespace OneHistoryGitTool.Models;

/// <summary>
/// 内部使用的分支信息（用于构建继承树）
/// </summary>
internal class BranchInfo
{
    public string Name { get; set; } = "";
    public string TipSha { get; set; } = "";
    public string LastCommitTime { get; set; } = "";
    public string LastCommitMessage { get; set; } = "";
}