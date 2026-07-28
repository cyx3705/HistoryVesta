namespace AppShell.Core.Files;

/// <summary>工作区内一个文件/目录条目(R-01 显示名称/大小/修改时间)。</summary>
public sealed record WorkspaceEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTime Modified);

/// <summary>工作区目录的一次结构化快照；远程 Shell 同时取得服务器根目录和条目。</summary>
public sealed record WorkspaceListing(
    string Root,
    IReadOnlyList<WorkspaceEntry> Entries);

/// <summary>
/// 工作区文件服务(§6.4):资源窗口与 res.* 指令的公共实现层。
/// 安全边界(R-06)在实现层统一保证——一切路径限定在根目录以内,
/// 删除仅进回收站;UI 与指令两条路径行为由此天然一致。
/// </summary>
public interface IWorkspaceService
{
    /// <summary>是否允许当前 UI 用本机文件夹选择器切换根目录。</summary>
    bool CanSelectLocalRoot => true;

    /// <summary>工作区根目录(R-08,默认 &lt;应用数据目录&gt;/workspace)。</summary>
    string Root { get; }

    /// <summary>切换根目录(res.root path=);目录必须已存在。</summary>
    void SetRoot(string path);

    /// <summary>列出目录内容(res.list);relativePath 为 null/空 表示根目录。目录在前,按名排序。</summary>
    IReadOnlyList<WorkspaceEntry> List(string? relativePath = null);

    /// <summary>新建文件夹(res.mkdir)。</summary>
    void CreateDirectory(string relativePath);

    /// <summary>重命名(res.rename);newName 为同目录内的新名字。</summary>
    void Rename(string relativePath, string newName);

    /// <summary>删除到回收站(res.delete;R-06 不做永久删除)。</summary>
    void DeleteToRecycleBin(string relativePath);

    /// <summary>把相对路径解析为绝对路径,并做根目录边界校验(越界抛异常)。</summary>
    string ResolveFull(string relativePath);

    /// <summary>文件系统外部变更(R-04,已去抖);资源窗口订阅后自动刷新。任意线程触发。</summary>
    event Action? Changed;
}
