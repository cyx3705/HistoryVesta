using HistoryVulcan.Core.Commands;

namespace HistoryJanus.GitHub;

/// <summary>
/// github.* 指令域注册。三个命令全部只读并允许 MCP 投影；
/// 登录、注销、提交身份和 origin 修改仅限 github 页面内经确认执行，不进入命令总线。
/// </summary>
public static class GitHubCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        GitHubConnectionService service,
        string source = "app")
    {
        registry.Register(BuildStatus(service), source);
        registry.Register(BuildAccounts(service), source);
        registry.Register(BuildTest(service), source);
    }

    private static CommandDescriptor BuildStatus(GitHubConnectionService service) => new()
    {
        Name = "github.status",
        CommandClass = "connection",
        Summary = "查看服务器 Git、GCM、提交身份、origin 和 SSH 状态",
        Readonly = true,
        Example = "github.status",
        Handler = async _ =>
        {
            var overview = await service.GetOverviewAsync();
            return CommandResult.Ok(
                $"git={overview.GitVersion}; origin={overview.Origin.FetchUrl}; " +
                $"GCM 账号={overview.CredentialAccounts.Count} 个",
                overview);
        },
    };

    private static CommandDescriptor BuildAccounts(GitHubConnectionService service) => new()
    {
        Name = "github.accounts",
        CommandClass = "account",
        Summary = "列出服务器 GCM 中已知的 GitHub HTTPS 凭据账号",
        Readonly = true,
        Example = "github.accounts",
        Handler = async _ =>
        {
            var accounts = await service.GetAccountsAsync();
            return CommandResult.Ok($"GCM 账号 {accounts.Count} 个", accounts);
        },
    };

    private static CommandDescriptor BuildTest(GitHubConnectionService service) => new()
    {
        Name = "github.test",
        CommandClass = "connection",
        Summary = "只读检测服务器 GitHub SSH 或 HTTPS 连接，不执行 push",
        Readonly = true,
        Example = "github.test transport=auto timeout=15",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "transport",
                Description = "检测通道",
                Default = "auto",
                AllowedValues = ["auto", "ssh", "https"],
            },
            new ParameterSpec
            {
                Name = "timeout",
                Description = "超时秒数(1~120)",
                Type = ParamType.Int,
                Default = "15",
            },
        ],
        Handler = async ctx =>
        {
            var transport = (ctx.GetString("transport") ?? "auto").ToLowerInvariant();
            if (transport is not ("auto" or "ssh" or "https"))
                return CommandResult.Fail("transport 只允许 auto、ssh 或 https");
            var timeout = Math.Clamp(ctx.GetInt("timeout", 15), 1, 120);
            var status = await service.TestAsync(transport, timeout);
            return CommandResult.Ok($"GitHub 连接检测: {status.State}", status);
        },
    };
}
