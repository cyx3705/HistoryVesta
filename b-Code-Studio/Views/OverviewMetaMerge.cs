using HistoryVulcan.Core.Commands;
using HistoryJanus.Git;

namespace HistoryJanus.Views;

/// <summary>
/// 项目总览的项目/Meta 合并与关键字匹配。
/// 纯函数独立成件：Smoke 不构造 WPF 视图也能验证合并、搜索和打开命令投影。
/// </summary>
public static class OverviewMetaMerge
{
    /// <summary>按项目名(忽略大小写)把 Meta 文件夹并入工作树行；组内按 Meta 名不区分大小写排序。</summary>
    public static List<OverviewView.WorktreeRow> Merge(
        IReadOnlyList<WorktreeInfo> projects,
        IReadOnlyList<MetaFolderInfo> metas)
    {
        var metasByProject = metas
            .GroupBy(meta => meta.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MetaFolderInfo>)group
                    .OrderBy(meta => meta.MetaName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        return projects.Select((item, index) => new OverviewView.WorktreeRow(
            index + 1,
            item.BranchName,
            metasByProject.GetValueOrDefault(item.BranchName) ?? [])).ToList();
    }

    /// <summary>关键字同时匹配项目名、Meta 名和 Meta 路径(包含匹配,忽略大小写)。</summary>
    public static bool MatchesKeyword(OverviewView.WorktreeRow row, string keyword)
        => row.BranchName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
           row.MetaFolders.Any(meta =>
               meta.MetaName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               meta.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>点击 Meta 名执行既有的 proj.metaopen 命令,不改变项目选择。</summary>
    public static string BuildOpenCommand(MetaFolderInfo meta)
        => $"proj.metaopen name={CommandParser.QuoteArg(meta.ProjectName)} " +
           $"meta={CommandParser.QuoteArg(meta.MetaName)}";
}
