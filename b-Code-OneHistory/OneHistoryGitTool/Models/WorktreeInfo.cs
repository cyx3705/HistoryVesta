namespace OneHistoryGitTool.Models;

/// <summary>
/// 工作树信息（用于删除列表）
/// </summary>
public record WorktreeInfo(string BranchName, string WorktreePath)
{
    public string Display => $"{BranchName,-35} →  {WorktreePath}";
}