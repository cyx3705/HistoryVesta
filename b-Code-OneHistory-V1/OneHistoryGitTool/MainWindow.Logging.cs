using System.Windows;
using System.Windows.Media;
using OneHistoryGitTool.Services;

namespace OneHistoryGitTool;

public partial class MainWindow
{
    // ==================== 日志相关功能（全局共享） ====================

    private void AppendLog(string text)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = string.IsNullOrWhiteSpace(text) ? "" : $"[{timestamp}] {text}";

        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText(line + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    /// <summary>
    /// 统一的 Git 命令执行方法（带日志输出）
    /// </summary>
    private async Task<GitResult> RunGitCommandAsync(string gitDir, string arguments)
    {
        var result = new GitResult();

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{gitDir}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            AppendLog($"$ git {psi.Arguments}");

            using var process = System.Diagnostics.Process.Start(psi)!;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string output = await outputTask;
            string error = await errorTask;

            result.ExitCode = process.ExitCode;
            result.Output = (output + "\n" + error).Trim();

            if (process.ExitCode != 0)
            {
                AppendLog($"[退出码 {process.ExitCode}]");
            }
        }
        catch (Exception ex)
        {
            result.ExitCode = -1;
            result.Output = $"执行 git 命令时发生异常：{ex.Message}";
            AppendLog(result.Output);
        }

        return result;
    }

    private async Task<GitCommandResult> RunGitForLfsAsync(string gitDir, string arguments)
    {
        var result = await RunGitCommandAsync(gitDir, arguments);
        return new GitCommandResult(result.ExitCode, result.Output);
    }

    public record GitResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
    }
}