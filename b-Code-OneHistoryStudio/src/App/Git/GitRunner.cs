using System.Diagnostics;
using System.Text;

namespace OneHistoryStudio.Git;

public sealed record GitResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 统一 git 进程封装(需求 N-02):固定 -C 工作目录、UTF-8 输出、超时与异常兜底。
/// 全项目禁止在此之外散落 Process.Start("git")。
/// </summary>
public static class GitRunner
{
    public static async Task<GitResult> RunAsync(
        string gitDir,
        IReadOnlyList<string> arguments,
        int timeoutSeconds = 300)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(gitDir);
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 git 进程");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程可能已自行退出
                }

                return new GitResult(-1,
                    $"git 命令超时({timeoutSeconds}s): git -C {FormatArgument(gitDir)} {FormatArguments(arguments)}");
            }

            var output = (await outputTask.ConfigureAwait(false) + "\n"
                          + await errorTask.ConfigureAwait(false)).Trim();
            return new GitResult(process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return new GitResult(-1, $"执行 git 命令异常: {ex.Message}");
        }
    }

    private static string FormatArguments(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(FormatArgument));

    private static string FormatArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        return $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
