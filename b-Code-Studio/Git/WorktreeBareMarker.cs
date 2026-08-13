using System.IO;
using System.Text;

namespace HistoryJanus.Git;

/// <summary>
/// 工作树的裸标记覆盖：确保 <c>&lt;裸仓&gt;/worktrees/&lt;名&gt;/config.worktree</c> 里有
/// <c>[core] bare = false</c>。
/// </summary>
/// <remarks>
/// 本项目库的裸仓同时开着 <c>core.bare = true</c> 与 <c>extensions.worktreeConfig = true</c>。
/// 这种组合下每个工作树都必须自带一份 config.worktree 来覆盖 bare，否则 git 会沿用公共配置
/// 把工作树当成裸仓，<c>status</c>、<c>ls-files --others/--ignored</c> 等一律报
/// "this operation must be run in a work tree"，Janus 在该项目上的规则扫描与格式台账整片失效。
///
/// admin 目录不按工作树目录名推算，而是读工作树里的 <c>.git</c> 指针：目录改过名而 admin 目录
/// 未同步时，按名推算会写到不存在的路径上，看起来成功却什么也没修。
/// </remarks>
internal static class WorktreeBareMarker
{
    private const string MarkerContent = "[core]\n\tbare = false\n";

    /// <summary>
    /// 确保该工作树带有裸标记覆盖。已存在且正确时不写盘。
    /// </summary>
    /// <returns>
    /// Written 为 true 表示本次补写了标记；Message 在失败时说明原因，成功时为空。
    /// 找不到 admin 目录（例如传入的不是工作树）时返回 Success=false。
    /// </returns>
    public static (bool Success, bool Written, string Message) Ensure(string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(worktreeRoot) || !Directory.Exists(worktreeRoot))
            return (false, false, $"工作树路径不存在: {worktreeRoot}");

        var (adminDirectory, resolveError) = ResolveAdminDirectory(worktreeRoot);
        if (adminDirectory == null)
            return (false, false, resolveError);

        var markerPath = Path.Combine(adminDirectory, "config.worktree");
        if (File.Exists(markerPath))
        {
            var existing = File.ReadAllText(markerPath);
            // 已经声明 bare = false 就不动：这个文件也承载别的 per-worktree 配置。
            if (existing.Contains("bare", StringComparison.OrdinalIgnoreCase)
                && existing.Contains("false", StringComparison.OrdinalIgnoreCase))
            {
                return (true, false, string.Empty);
            }

            var appended = existing.TrimEnd('\n', '\r') + "\n" + MarkerContent;
            File.WriteAllText(markerPath, appended, new UTF8Encoding(false));
            return (true, true, string.Empty);
        }

        File.WriteAllText(markerPath, MarkerContent, new UTF8Encoding(false));
        return (true, true, string.Empty);
    }

    /// <summary>读取工作树 <c>.git</c> 文件里的 <c>gitdir:</c> 指针，得到 admin 目录绝对路径。</summary>
    private static (string? Directory, string Error) ResolveAdminDirectory(string worktreeRoot)
    {
        var pointer = Path.Combine(worktreeRoot, ".git");
        if (Directory.Exists(pointer))
            return (null, $"{worktreeRoot} 是独立仓库而非工作树，无需裸标记覆盖");
        if (!File.Exists(pointer))
            return (null, $"未找到工作树指针: {pointer}");

        const string prefix = "gitdir:";
        foreach (var line in File.ReadAllLines(pointer))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = trimmed[prefix.Length..].Trim();
            if (target.Length == 0)
                continue;

            var resolved = Path.IsPathRooted(target)
                ? target
                : Path.GetFullPath(Path.Combine(worktreeRoot, target));
            return Directory.Exists(resolved)
                ? (resolved, string.Empty)
                : (null, $"工作树指针指向的目录不存在: {resolved}");
        }

        return (null, $"工作树指针缺少 gitdir 行: {pointer}");
    }
}
