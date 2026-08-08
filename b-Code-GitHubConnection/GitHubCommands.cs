using HistoryVulcan.Core.Modules;

namespace GitHubConnection;

public sealed class GitHubCommands
{
    private readonly GitHubConnectionService _service;

    public GitHubCommands()
        : this(GitHubRuntime.CreateService())
    {
    }

    internal GitHubCommands(GitHubConnectionService service)
    {
        _service = service;
    }

    /// <summary>查看服务器 Git、GCM、提交身份、origin 和 SSH 状态。</summary>
    [ModuleCommand(Readonly = true, CommandClass = "connection")]
    public Task<GitHubAccountOverview> status()
        => _service.GetOverviewAsync();

    /// <summary>列出服务器 GCM 中已知的 GitHub HTTPS 凭据账号。</summary>
    [ModuleCommand(Readonly = true, CommandClass = "account")]
    public Task<IReadOnlyList<GitCredentialAccount>> accounts()
        => _service.GetAccountsAsync();

    /// <summary>只读检测服务器 GitHub SSH 或 HTTPS 连接，不执行 push。</summary>
    /// <param name="transport">检测通道：auto、ssh 或 https。</param>
    /// <param name="timeout">超时秒数，限制为 1 至 120。</param>
    [ModuleCommand(Readonly = true, CommandClass = "connection")]
    public Task<GitHubConnectionStatus> test(string transport = "auto", int timeout = 15)
    {
        var selected = transport.ToLowerInvariant();
        if (selected is not ("auto" or "ssh" or "https"))
            throw new ArgumentException("transport 只允许 auto、ssh 或 https");
        return _service.TestAsync(selected, Math.Clamp(timeout, 1, 120));
    }
}
