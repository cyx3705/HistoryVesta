using System.Runtime.InteropServices;
using AppShell.Core.Files;

namespace AppShell.Services;

/// <summary>
/// 工作区文件服务实现(§6.4):
/// - 根目录边界校验统一在 ResolveFull(R-06),UI 与指令两条路径共用
/// - 删除经 SHFileOperation 进回收站,不做永久删除
/// - FileSystemWatcher 感知外部变更,500ms 去抖后广播(R-04)
/// </summary>
public sealed class WorkspaceService : IWorkspaceService, IDisposable
{
    public const string KeyRoot = "workspace.root";

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string _root;

    public WorkspaceService(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
        StartWatcher();
    }

    public event Action? Changed;

    public string Root
    {
        get
        {
            lock (_gate)
            {
                return _root;
            }
        }
    }

    public void SetRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new InvalidOperationException($"目录不存在: {full}");

        lock (_gate)
        {
            _root = full;
        }

        StartWatcher();
        Changed?.Invoke();
    }

    public IReadOnlyList<WorkspaceEntry> List(string? relativePath = null)
    {
        var dir = ResolveFull(relativePath ?? "");
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"目录不存在: {relativePath}");

        var root = Root;
        var entries = new List<WorkspaceEntry>();
        foreach (var path in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var info = new DirectoryInfo(path);
            entries.Add(new WorkspaceEntry(info.Name, Relative(root, path), true, 0, info.LastWriteTime));
        }

        foreach (var path in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            entries.Add(new WorkspaceEntry(info.Name, Relative(root, path), false, info.Length, info.LastWriteTime));
        }

        return entries;
    }

    public void CreateDirectory(string relativePath)
    {
        var full = ResolveFull(relativePath);
        if (Directory.Exists(full) || File.Exists(full))
            throw new InvalidOperationException($"已存在: {relativePath}");
        Directory.CreateDirectory(full);
    }

    public void Rename(string relativePath, string newName)
    {
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"名字含非法字符: {newName}");

        var full = ResolveFull(relativePath);
        var target = Path.Combine(Path.GetDirectoryName(full)!, newName);
        ResolveFull(Relative(Root, target)); // 目标同样过边界校验

        if (Directory.Exists(full))
            Directory.Move(full, target);
        else if (File.Exists(full))
            File.Move(full, target);
        else
            throw new InvalidOperationException($"不存在: {relativePath}");
    }

    public void DeleteToRecycleBin(string relativePath)
    {
        var full = ResolveFull(relativePath);
        if (full.Equals(Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能删除工作区根目录");
        if (!Directory.Exists(full) && !File.Exists(full))
            throw new InvalidOperationException($"不存在: {relativePath}");

        RecycleBin.Delete(full);
    }

    public string ResolveFull(string relativePath)
    {
        var root = Root;
        var full = Path.GetFullPath(Path.Combine(root, relativePath ?? ""));

        // R-06:一切操作限制在根目录以内,拒绝越界(../、绝对路径逃逸等)
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"路径越出工作区根目录,已拒绝: {relativePath}");
        }

        return full;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    private string Relative(string root, string full)
        => Path.GetRelativePath(root, full);

    private void StartWatcher()
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler handler = (_, _) => Debounced();
            _watcher.Created += handler;
            _watcher.Deleted += handler;
            _watcher.Changed += handler;
            _watcher.Renamed += (_, _) => Debounced();
        }
        catch (Exception)
        {
            // 监控起不来不影响功能,仅失去自动刷新(R-04 为 P1)
            _watcher = null;
        }
    }

    private void Debounced()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Changed?.Invoke(), null, 500, Timeout.Infinite);
        }
    }

    /// <summary>回收站删除(SHFileOperation,FOF_ALLOWUNDO)。</summary>
    private static class RecycleBin
    {
        // 注意:x64 上本结构必须用平台默认对齐。历史上广泛流传的 Pack = 1 写法
        // 仅对 32 位正确,在 x64 会使 pFrom 之后字段错位 → Shell 读到错误标志、
        // 回写越界 → 栈损坏进程闪退(AccessViolation 不可被 .NET 捕获)。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        private const uint FO_DELETE = 3;
        private const ushort FOF_ALLOWUNDO = 0x0040;      // 进回收站
        private const ushort FOF_NOCONFIRMATION = 0x0010; // 确认由指令总线/UI 负责
        private const ushort FOF_SILENT = 0x0004;

        public static void Delete(string fullPath)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + "\0\0", // 双空结尾
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
            };
            var result = SHFileOperation(ref op);
            if (result != 0 || op.fAnyOperationsAborted)
                throw new InvalidOperationException($"回收站删除失败(代码 {result}): {fullPath}");
        }
    }
}
