
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ManagerModule;

/// <summary>
/// 基于进程名的窗口控制器（极简版）
/// </summary>
public static class WindowController
{
    // ==================== 新增：枚举窗口的Windows API ====================
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ==================== 原有API和常量不变 ====================
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public const int ACTION_HIDE = 0;
    public const int ACTION_SHOW = 1;
    public const int ACTION_MINIMIZE = 2;
    public const int ACTION_MAXIMIZE = 3;
    public const int ACTION_RESTORE = 9;
    public const int ACTION_CLOSE = 16;
    public const int ACTION_BRING_TO_FRONT = 10;

    private const uint WM_CLOSE = 0x0010;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_RESTORE = 9;

    // ==================== 核心修复：替换原有的ControlWindowByProcessName ====================
    public static bool ControlWindowByProcessName(string processName, int action)
    {
        try
        {
            Process[] targetProcesses = Process.GetProcessesByName(processName);
            if (targetProcesses.Length == 0)
            {
                Console.WriteLine($"未找到进程：{processName}");
                return false;
            }

            bool isSuccess = false;
            foreach (Process process in targetProcesses)
            {
                // 关键：不再用Process.MainWindowHandle，而是枚举该进程的所有窗口句柄
                List<IntPtr> windowHandles = GetWindowHandlesByProcessId(process.Id);
                if (windowHandles.Count == 0)
                {
                    Console.WriteLine($"进程 {process.Id} 无任何窗口，跳过");
                    continue;
                }

                // 操作该进程的所有窗口（通常第一个就是主窗口）
                foreach (IntPtr hWnd in windowHandles)
                {
                    ExecuteWindowAction(hWnd, action, process.Id);
                    isSuccess = true;
                }
            }
            return isSuccess;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"操作失败：{ex.Message}");
            return false;
        }
    }

    // ==================== 新增：根据进程ID获取所有窗口句柄（包括隐藏窗口） ====================
    private static List<IntPtr> GetWindowHandlesByProcessId(int processId)
    {
        List<IntPtr> handles = new List<IntPtr>();
        EnumWindows((hWnd, lParam) =>
        {
            // 获取当前窗口所属的进程ID
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            // 如果进程ID匹配，加入列表
            if (windowPid == processId)
            {
                handles.Add(hWnd);
            }
            return true; // 继续枚举下一个窗口
        }, IntPtr.Zero);
        return handles;
    }

    // ==================== 新增：抽离的窗口操作逻辑 ====================
    private static void ExecuteWindowAction(IntPtr hWnd, int action, int processId)
    {
        switch (action)
        {
            case ACTION_HIDE:
                ShowWindow(hWnd, SW_HIDE);
                Console.WriteLine($"隐藏进程 {processId} 的窗口（句柄：{hWnd}）");
                break;
            case ACTION_SHOW:
            case ACTION_RESTORE: // 显示和恢复用同一个逻辑
                ShowWindow(hWnd, SW_RESTORE);
                Console.WriteLine($"恢复进程 {processId} 的窗口（句柄：{hWnd}）");
                break;
            case ACTION_MINIMIZE:
                ShowWindow(hWnd, SW_SHOWMINIMIZED);
                Console.WriteLine($"最小化进程 {processId} 的窗口（句柄：{hWnd}）");
                break;
            case ACTION_MAXIMIZE:
                ShowWindow(hWnd, SW_SHOWMAXIMIZED);
                Console.WriteLine($"最大化进程 {processId} 的窗口（句柄：{hWnd}）");
                break;
            case ACTION_CLOSE:
                SendMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Console.WriteLine($"关闭进程 {processId} 的窗口（句柄：{hWnd}）");
                break;
            case ACTION_BRING_TO_FRONT:
                SetForegroundWindow(hWnd);
                Console.WriteLine($"置顶进程 {processId} 的窗口（句柄：{hWnd}）");
                break;
            default:
                Console.WriteLine($"无效操作码：{action}");
                break;
        }
    }

    // 原有 KillProcess 方法不变
    public static bool KillProcess(string processName)
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
            {
                Console.WriteLine($"未找到进程：{processName}");
                return false;
            }
            foreach (Process process in processes)
            {
                process.Kill();
                Console.WriteLine($"强制结束进程 {process.Id} 成功");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"强制结束进程失败：{ex.Message}");
            return false;
        }
    }
}

