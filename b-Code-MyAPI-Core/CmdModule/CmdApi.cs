using BaseVariable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
namespace CmdModule;

/// <summary>
/// 动态API类（自动生成接口：/api/CmdApi/execute）
/// </summary>
public class CmdApi
{
    private static bool _browserOpened = false;
    private static bool _initialized = false;
    // ==================== Windows API ====================
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    private const int SW_HIDE = 0;


    /// <summary>
    /// 初始化控制台重定向 + 隐藏窗口 + 点击关闭按钮时隐藏而不是退出程序
    /// </summary>
    public static void InitConsoleRedirect()
    {
        if (_initialized) return;

        TextWriter? originalOut = null;

        try
        {
            // 1. 分配控制台窗口
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                AllocConsole();
            }

            var consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
            {
                // 立即隐藏窗口
                ShowWindow(consoleWindow, SW_HIDE);

                // 或者你可以改成点击关闭时隐藏窗口（更友好）
                IntPtr hMenu = GetSystemMenu(consoleWindow, false);
            }

            // 2. 设置编码
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            originalOut = Console.Out;
            var originalError = Console.Error;

            // 3. 重定向输出
            var dualOutput = new DualOutputWriter(originalOut, msg =>
            {
                if (string.IsNullOrWhiteSpace(msg)) return;
                var lines = msg.TrimEnd('\r', '\n').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        ConsoleLogBuffer.WriteLine(line.Trim());
                }
            });

            var dualError = new DualOutputWriter(originalError, msg =>
            {
                if (string.IsNullOrWhiteSpace(msg)) return;
                var lines = msg.TrimEnd('\r', '\n').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                    ConsoleLogBuffer.WriteLine("[ERROR] " + line.Trim());
            });

            Console.SetOut(dualOutput);
            Console.SetError(dualError);

            _initialized = true;

            // 5. 自动打开浏览器
            OpenHomePageInBrowser();
        }
        catch (Exception ex)
        {
            try { ConsoleLogBuffer.WriteLine($"[ERROR] 控制台重定向初始化失败: {ex.Message}"); } catch { }
            originalOut?.WriteLine($"控制台重定向初始化失败: {ex.Message}");
        }
    }
    /// <summary>
    /// 打开浏览器页面
    /// </summary>
    public static void OpenHomePageInBrowser()
    {
        if (_browserOpened)
        {
            Console.WriteLine("[Browser] 浏览器已经打开过了，跳过");
            return;
        }
        try
        {
            string homeUrl = $"http://localhost:{BSV.mstPort}/{BSV.startPage}";
            Process.Start(new ProcessStartInfo
            {
                FileName = homeUrl,
                UseShellExecute = true
            });
            Console.WriteLine("=== CMD Terminal 已启动 ===");
            Console.WriteLine("多页面同步模式已开启");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"自动打开浏览器失败: {ex.Message}");
        }
    }


    /// <summary>
    /// 网页控制台命令执行入口（合并版）
    /// 支持 GET 查询参数调用
    /// </summary>
    public static async Task Execute(string command)
    {
        try
        {

            // 解析命令
            var (method, path, body) = CmdMain.ParseInputCommand(command);

            if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(path))
            {
                Console.WriteLine($"[解析失败] 指令格式错误: {command}");
                return;
            }
            // 执行实际请求
            await CmdMain.SendHttpRequestAsync(method, path, body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[执行异常] {ex.Message}");
        }
    }
}

public static class ConsoleLogBuffer
{
    // 改用 List 保存所有历史日志（不删除）
    private static readonly List<string> _allLogs = new();
    private static readonly object _lock = new();

    // 新增：事件，当有新日志时通知所有页面
    public static event Action<string>? OnNewLog;

    public static void WriteLine(string message)
    {
        string log = $"[{DateTime.Now:HH:mm:ss}] {message}";

        lock (_lock)
        {
            _allLogs.Add(log);
        }

        // 通知所有打开的页面
        OnNewLog?.Invoke(log);
    }

    // 获取当前所有历史日志（供新页面加载使用）
    public static List<string> GetAllLogs()
    {
        lock (_lock)
        {
            return _allLogs.ToList(); // 返回副本
        }
    }

    // 清屏用（可选）
    public static void Clear()
    {
        lock (_lock)
        {
            _allLogs.Clear();
        }
    }
}


public class DualOutputWriter : TextWriter
{
    private readonly TextWriter _original;
    private readonly Action<string> _logCallback;

    public DualOutputWriter(TextWriter original, Action<string> logCallback)
    {
        _original = original ?? throw new ArgumentNullException(nameof(original));
        _logCallback = logCallback ?? throw new ArgumentNullException(nameof(logCallback));
    }

    public override Encoding Encoding => _original.Encoding;

    public override void Write(char value)
    {
        _original.Write(value);
        _logCallback(value.ToString());
    }

    public override void Write(string? value)
    {
        _original.Write(value);
        if (!string.IsNullOrEmpty(value))
            _logCallback(value);
    }

    public override void WriteLine(string? value)
    {
        _original.WriteLine(value);
        _logCallback(value ?? string.Empty);
    }

    public override void WriteLine()
    {
        _original.WriteLine();
        _logCallback(string.Empty);
    }
}

