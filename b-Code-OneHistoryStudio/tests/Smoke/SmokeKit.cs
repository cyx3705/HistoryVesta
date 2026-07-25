using AppShell.Core;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Smoke;

/// <summary>
/// V2.3.3 QC-02:五套冒烟工程合并后的共享断言与 Git 助手。
/// 合并前 Ensure/True/Equal/Contains/BytesEqual/Throws/DeleteTree/FirstLine 等 9 类助手
/// 在 3~5 个工程各写一遍;此处收敛为唯一实现,判据逐字保持不变。
/// </summary>
internal static class SmokeKit
{
    /// <summary>
    /// 仓库根(含 OneHistoryStudio.sln 的目录)。
    /// 合并前各工程用 Environment.CurrentDirectory 拼临时目录,导致 V213/PromptGovernance
    /// 必须在父目录下运行、V230~V232 必须在仓库根下运行,互相冲突。改为从程序集位置向上探测,
    /// 宿主再按每套用例的历史语义显式设置 CurrentDirectory(见 Program.cs),
    /// 单宿主因此可在任意工作目录启动。
    /// </summary>
    public static string RepoRoot { get; } = DiscoverRepoRoot();

    /// <summary>仓库根的父目录(2026-018-MyAPI);V213 与 PromptGovernance 的历史工作目录。</summary>
    public static string ParentDir { get; } = Directory.GetParent(RepoRoot)!.FullName;

    private static string DiscoverRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OneHistoryStudio.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"未能从 {AppContext.BaseDirectory} 向上找到 OneHistoryStudio.sln");
    }

    // ---------------------------------------------------------------- 断言

    public static void Ensure(GitResult result, string name)
    {
        if (!result.Success)
            throw new InvalidOperationException($"FAIL: {name}\n{result.Output}");
    }

    public static void True(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {name}");
    }

    public static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"FAIL: {name}; expected={expected}, actual={actual}");
    }

    public static void Contains(string actual, string expected, string name)
        => True(actual.Contains(expected, StringComparison.Ordinal), name);

    public static void BytesEqual(byte[] expected, byte[] actual, string name)
        => True(expected.AsSpan().SequenceEqual(actual), name);

    public static void Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"FAIL: {name}; expected {typeof(T).Name}");
    }

    // ---------------------------------------------------------------- Git 读取助手

    public static GitResult EnsureResult(GitResult result, string name)
    {
        Ensure(result, name);
        return result;
    }

    public static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

    public static string Run(string repository, IReadOnlyList<string> arguments)
    {
        var result = GitRunner.RunAsync(repository, arguments).GetAwaiter().GetResult();
        Ensure(result, string.Join(' ', arguments));
        return result.Output;
    }

    public static string Subject(string repository)
        => FirstLine(Run(repository, ["log", "-1", "--format=%s"]));

    public static string Sha(string repository)
        => FirstLine(Run(repository, ["rev-parse", "HEAD"]));

    public static string RefSha(string repository, string branch)
        => FirstLine(Run(repository, ["rev-parse", $"refs/heads/{branch}"]));

    public static string GitlinkSha(string repository, string relativePath)
        => FirstLine(Run(repository, ["rev-parse", $"HEAD:{relativePath}"]));

    public static async Task ConfigureIdentity(string repository, string user, string email)
    {
        Ensure(await GitRunner.RunAsync(repository, ["config", "user.name", user]), "git user name");
        Ensure(await GitRunner.RunAsync(repository, ["config", "user.email", email]), "git user email");
    }

    public static async Task CommitFile(
        string repository, string relativePath, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repository, relativePath), content);
        Ensure(await GitRunner.RunAsync(repository, ["add", "--", relativePath]), $"add {relativePath}");
        Ensure(await GitRunner.RunAsync(repository, ["commit", "-m", message]), $"commit {message}");
    }

    // ---------------------------------------------------------------- 清理

    public static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}

/// <summary>内存设置服务;合并前在 V213/V230/V231/V232 四处各写一遍。</summary>
internal sealed class MemorySettings : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
    public void Set(string key, string value) => _values[key] = value;
    public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
}

/// <summary>内存日志;供需要检查回显与结果日志的用例使用。</summary>
internal sealed class MemoryLog : IShellLog
{
    private readonly List<ShellLogEntry> _entries = [];

    public event EventHandler<ShellLogEntry>? EntryAdded;

    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(DateTime.UtcNow, level, category, message);
        _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
}
