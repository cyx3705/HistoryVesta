using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ActiveDock;

/// <summary>
/// 把活动坞压到桌面层，使其永不遮挡其他窗口。
/// </summary>
/// <remarks>
/// 主路径是重定父到承载壁纸的 WorkerW，窗口因此成为桌面的一部分，Win+D 时随桌面一起露出。
/// WorkerW 的层次是 Explorer 的实现细节而非稳定契约，找不到时降级为"永远压在 z-order 最底"。
/// 两条路径都满足"不遮挡其他页面"，差别只在 Win+D 行为。
/// </remarks>
public static class DesktopLayer
{
    public enum Mode
    {
        /// <summary>尚未贴附。</summary>
        None,

        /// <summary>已重定父到桌面 WorkerW。</summary>
        WorkerW,

        /// <summary>未找到 WorkerW，降级为压底。</summary>
        BottomMost,
    }

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SpawnWorkerW = 0x052C;
    private const uint SendTimeoutAbortIfHung = 0x0002;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    private static readonly IntPtr HwndBottom = new(1);
    private static readonly object Gate = new();

    public static Mode Current { get; private set; } = Mode.None;

    /// <summary>贴附窗口到桌面层，返回实际生效的方式。</summary>
    public static Mode Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return Mode.None;

        ApplyNoActivate(hwnd);

        IntPtr workerW;
        try
        {
            workerW = FindDesktopWorkerW();
        }
        catch (Exception ex)
        {
            Log($"枚举 WorkerW 失败，降级压底: {ex.Message}");
            return Fallback(hwnd);
        }

        if (workerW == IntPtr.Zero)
        {
            Log("未找到承载壁纸的 WorkerW，降级压底");
            return Fallback(hwnd);
        }

        if (SetParent(hwnd, workerW) == IntPtr.Zero)
        {
            Log($"SetParent 到 WorkerW 失败(Win32 {Marshal.GetLastWin32Error()})，降级压底");
            return Fallback(hwnd);
        }

        Parent = workerW;
        Current = Mode.WorkerW;
        Log("已贴附到桌面 WorkerW");
        return Mode.WorkerW;
    }

    /// <summary>当前父窗口；未重定父时为 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr Parent { get; private set; }

    /// <summary>父窗口是否仍然有效。Explorer 重启会让旧 WorkerW 句柄失效。</summary>
    public static bool IsParentAlive()
        => Current != Mode.WorkerW || (Parent != IntPtr.Zero && IsWindow(Parent));

    /// <summary>取父窗口左上角的屏幕物理坐标；未重定父时为 (0,0)。</summary>
    public static (double X, double Y) ParentOrigin()
    {
        if (Current != Mode.WorkerW || Parent == IntPtr.Zero || !GetWindowRect(Parent, out var rect))
            return (0, 0);
        return (rect.Left, rect.Top);
    }

    /// <summary>
    /// 用物理像素直接设置位置。重定父之后 WPF 的 Left/Top 会被解释为相对父窗口客户区，
    /// 因此贴附路径下的落位必须走这里，不能沿用 Left/Top。
    /// </summary>
    public static void MoveTo(IntPtr hwnd, int x, int y)
    {
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoZOrder);
    }

    /// <summary>降级路径下把窗口压回 z-order 最底。</summary>
    public static void PushToBottom(IntPtr hwnd)
    {
        if (Current == Mode.BottomMost && hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static Mode Fallback(IntPtr hwnd)
    {
        Parent = IntPtr.Zero;
        Current = Mode.BottomMost;
        PushToBottom(hwnd);
        return Mode.BottomMost;
    }

    private static void ApplyNoActivate(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>
    /// 促使 Progman 生成 WorkerW，再找出那个**不含** SHELLDLL_DefView 子窗口的 WorkerW——
    /// 含 DefView 的那个承载图标，它的兄弟才是承载壁纸、可供贴附的一层。
    /// </summary>
    private static IntPtr FindDesktopWorkerW()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            SendMessageTimeout(progman, SpawnWorkerW, IntPtr.Zero, IntPtr.Zero, SendTimeoutAbortIfHung, 1000, out _);

        var target = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                return true;
            target = FindWindowEx(IntPtr.Zero, handle, "WorkerW", null);
            return target == IntPtr.Zero;
        }, IntPtr.Zero);

        return target;
    }

    private static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OneHistoryStudio",
                    "ActiveDock");
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "layer.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志失败不得影响界面。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    private static int GetWindowLong(IntPtr handle, int index) => (int)GetWindowLongPtr(handle, index);

    private static void SetWindowLong(IntPtr handle, int index, int value)
        => SetWindowLongPtr(handle, index, new IntPtr(value));
}
