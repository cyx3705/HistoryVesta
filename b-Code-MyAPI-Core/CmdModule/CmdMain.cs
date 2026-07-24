// CmdModule/CmdMain.cs
using BaseVariable;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CmdModule
{
    /// <summary>
    /// 控制台窗口管理 + 交互功能（简化版）
    /// 支持打开/隐藏控制台，并支持命令行输入 HTTP 请求
    /// </summary>
    public class CmdMain
    {
        private static IntPtr _consoleWindowHandle = IntPtr.Zero;
        private static Thread? _consoleThread;
        private static bool _isRunning = false;

        // ==================== Windows API ====================
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int GWL_STYLE = -16;
        private const int WS_VISIBLE = 0x10000000;

        /// <summary>
        /// 提供一个简单的接口来打开控制台窗口并启动交互循环
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void Open()
        {
            if (_isRunning)
            {
                ShowWindow(GetConsoleWindow(), SW_SHOW);
                Console.WriteLine("控制台已在运行！");
                return;
            }

            try
            {
                if (GetConsoleWindow() == IntPtr.Zero)
                {
                    AllocConsole();
                }

                _consoleWindowHandle = GetConsoleWindow();
                if (_consoleWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(_consoleWindowHandle, SW_SHOW);
                }

                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;

                Console.WriteLine("控制台窗口已成功打开！");

                _isRunning = true;
                _consoleThread = new Thread(RunConsoleLoop)
                {
                    IsBackground = true,
                    Name = "ConsoleInteractionThread"
                };
                _consoleThread.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"打开控制台失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 提供简单的方法隐藏控制台
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void Hide()
        {
            try
            {
                var hwnd = GetConsoleWindow();
                if (hwnd == IntPtr.Zero)
                {
                    Console.WriteLine("当前没有控制台窗口");
                    return;
                }

                // 方案1：普通隐藏
                ShowWindow(hwnd, SW_HIDE);
                ShowWindow(hwnd, SW_HIDE);

                // 方案2：更彻底 - 移除 Visible 样式
                int style = GetWindowLong(hwnd, GWL_STYLE);
                SetWindowLong(hwnd, GWL_STYLE, style & ~WS_VISIBLE);

                Console.WriteLine("控制台窗口已**彻底隐藏**（输入 Open() 可重新显示）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"隐藏控制台窗口失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 控制台交互主循环
        /// </summary>
        private static void RunConsoleLoop()
        {
            PrintWelcomeInfo();

            while (_isRunning)
            {
                try
                {
                    Console.Write("\n请输入指令 > ");
                    var input = Console.ReadLine()?.Trim();

                    if (string.IsNullOrEmpty(input)) continue;
                    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

                    HandleUserInput(input);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"输入处理异常: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 处理用户输入的指令
        /// </summary>
        private static void HandleUserInput(string input)
        {
            if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                ShowHelp();
                return;
            }

            var (method, path, body) = ParseInputCommand(input);

            if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(path))
            {
                Console.WriteLine("指令格式错误！输入 help 查看帮助");
                return;
            }

            try
            {
                // 这里使用同步等待，适合控制台交互
                var task = SendHttpRequestAsync(method, path, body);
                task.Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"请求失败: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        /// <summary>
        /// 发送 HTTP 请求
        /// </summary>
        public static async Task SendHttpRequestAsync(string method, string path, string body)
        {
            string url = $"http://localhost:{BSV.mstPort}{path}";   // 使用 BSV.mstPort，更灵活

            Console.WriteLine($"\n发送: {method} {url}");

            if (!string.IsNullOrEmpty(body))
                Console.WriteLine($"Body: {body}");

            try
            {
                var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (!string.IsNullOrEmpty(body) && (method == "POST" || method == "PUT"))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                var response = await BSV.httpClient.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"响应: {(int)response.StatusCode} {response.StatusCode}");
                Console.WriteLine(FormatJson(content));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"请求异常: {ex.Message}");
            }
        }

        #region 辅助方法

        private static void PrintWelcomeInfo()
        {
            Console.WriteLine("=====================================");
            Console.WriteLine("        MyAPI 控制台交互工具");
            Console.WriteLine("=====================================");
            Console.WriteLine("支持指令：get / post / put / delete");
            Console.WriteLine("示例：get /api/MyUtilsModule/One/one1");
            Console.WriteLine("输入 help 查看帮助，exit 退出交互");
            Console.WriteLine("=====================================\n");
        }

        private static void ShowHelp()
        {
            Console.WriteLine("\n帮助：");
            Console.WriteLine("  get  [路径]");
            Console.WriteLine("  post [路径] [json体]");
            Console.WriteLine("示例：");
            Console.WriteLine("  get /api/QuartzModule/QuartzHeart/StartAllJobsAsync");
            Console.WriteLine("  hide   ← 隐藏控制台");
        }

        public static (string method, string path, string body) ParseInputCommand(string input)
        {
            var parts = input.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string method = parts.Length > 0 ? parts[0].ToUpper() : "";
            string path = parts.Length > 1 ? parts[1] : "";
            string body = parts.Length > 2 ? parts[2] : "";

            return (method, path, body);
        }

        private static string FormatJson(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return "";
                var options = new JsonSerializerOptions { WriteIndented = true };
                var parsed = JsonSerializer.Deserialize<JsonElement>(json);
                return JsonSerializer.Serialize(parsed, options);
            }
            catch
            {
                return json;
            }
        }

        #endregion
    }
}
