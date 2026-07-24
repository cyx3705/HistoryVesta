using BaseVariable;        // Location 类
using MyAPIBuilder;        // 如果 OpenTool 和 Update 在同一个命名空间，可省略
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using UpdateModule;

namespace MyAPIBuilder;

[SupportedOSPlatform("windows")]
public class Program
{
    private static Process? _coreProcess;
    private static readonly string _mainExePath = Path.Combine(Location.InternalDir, "MyAPICore.exe");
    private static WatchDog? _watchDog;
    private static CancellationTokenSource? _cts;

    [STAThread]
    static async Task Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        Console.WriteLine("=========================================");
        Console.WriteLine("       MyAPIBuilder 启动（常驻模式）");
        Console.WriteLine("   角色：启动器 + 看门狗 + 更新器 + 安装器");
        Console.WriteLine("=========================================");

        _cts = new CancellationTokenSource();

        // 处理 Ctrl+C 优雅退出
        Console.CancelKeyPress += (sender, e) =>
        {
            Console.WriteLine("\n\n接收到 Ctrl+C，正在安全退出...");
            e.Cancel = true;           // 阻止立即终止
            _cts.Cancel();
        };

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // 1. 确保所有目录结构存在
                InstallTool.EnsureAllDirectories();

                // 2. 检查核心程序是否存在
                string mainExePath = Path.Combine(Location.InternalDir, "MyAPICore.exe");

                if (!File.Exists(mainExePath))
                {
                    Console.WriteLine("未检测到核心程序 (MyAPICore.exe)，开始执行首次安装...");
                    var installTool = new InstallTool();
                    var installResult = await installTool.ExecuteInstallAsync();

                    if (!installResult.Success)
                    {
                        Console.WriteLine($"安装失败：{installResult.Message}");
                        Console.WriteLine("安装失败，但程序将继续运行（可手动修复后重试）");
                        await Task.Delay(5000); // 等待5秒后继续循环
                        continue;
                    }

                    Console.WriteLine("首次安装成功！");
                }
                else
                {
                    Console.WriteLine($"检测到核心程序已存在 → {mainExePath}");
                }

                // 3. 初始化并启动看门狗
                _watchDog = new WatchDog(Location.InternalDir);
                _watchDog.Start();

                Console.WriteLine("看门狗已启动，将负责主程序的启动与监控...");

                Console.WriteLine("\nMyAPIBuilder 已进入常驻模式");
                Console.WriteLine("按Ctrl+C 可安全退出 Builder");

                // 保持常驻，直到取消
                await Task.Delay(Timeout.Infinite, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常取消（Ctrl+C），不视为错误
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine("MyAPIBuilder 发生严重错误（但程序不会退出）");
                Console.WriteLine($"错误信息：{ex.Message}");

                if (ex.StackTrace != null)
                    Console.WriteLine($"堆栈信息：{ex.StackTrace}");

                if (ex.InnerException != null)
                    Console.WriteLine($"内部异常：{ex.InnerException.Message}");

                Console.WriteLine("5秒后尝试重新启动看门狗和监控循环...");
                await Task.Delay(5000);   // 等待5秒后重新进入循环
            }
        }

        // 退出前的清理
        Console.WriteLine("\n正在执行清理工作...");
        await CleanupAsync();

        Console.WriteLine("MyAPIBuilder 已安全退出。");
    }
    /// <summary>
    /// 清理资源（退出时调用）
    /// </summary>
    private static async Task CleanupAsync()
    {
        try
        {
            if (_coreProcess != null && !_coreProcess.HasExited)
            {
                Console.WriteLine("正在关闭主程序...");
                _coreProcess.Kill();
                await _coreProcess.WaitForExitAsync();
            }
        }
        catch { }

        Console.WriteLine("MyAPIBuilder 已退出");
    }
}