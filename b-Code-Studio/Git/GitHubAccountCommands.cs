using System.Text;
using AppShell.Core.Commands;

namespace OneHistoryStudio.Git;

public static class GitHubAccountCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        GitHubAccountService service,
        string source = "app")
    {
        registry.Register(Status(service), source);
        registry.Register(Accounts(service), source);
        registry.Register(Test(service), source);
        registry.Register(Login(service), source);
        registry.Register(Logout(service), source);
        registry.Register(Identity(service), source);
        registry.Register(Remote(service), source);
    }

    private static CommandDescriptor Status(GitHubAccountService service) => new()
    {
        Name = "github.status",
        Summary = "查看服务器 Git 作者、GitHub 凭据、origin 与 SSH 状态",
        Readonly = true,
        Parameters = [Bool("refresh", "重新读取全部本机事实", "false")],
        Handler = async ctx =>
        {
            var value = await service.GetOverviewAsync(ctx.Cancellation);
            return CommandResult.Ok(Render(value), value);
        },
    };

    private static CommandDescriptor Accounts(GitHubAccountService service) => new()
    {
        Name = "github.accounts",
        Summary = "列出服务器 GCM 中已知的 GitHub HTTPS 凭据账号",
        Readonly = true,
        Handler = async ctx =>
        {
            var values = await service.GetAccountsAsync(ctx.Cancellation);
            return CommandResult.Ok(values.Count == 0
                ? "GCM 中没有 GitHub HTTPS 凭据账号"
                : "GCM 账号:" + string.Concat(values.Select(value => $"\n  {value.Account}")), values);
        },
    };

    private static CommandDescriptor Test(GitHubAccountService service) => new()
    {
        Name = "github.test",
        Summary = "只读检测服务器 GitHub SSH/HTTPS 连接，不执行 push",
        Readonly = true,
        Parameters =
        [
            new ParameterSpec { Name = "transport", Description = "检测通道", AllowedValues = ["auto", "ssh", "https"], Default = "auto" },
            new ParameterSpec { Name = "timeout", Description = "超时秒数", Type = ParamType.Int, Default = "15" },
        ],
        Handler = async ctx =>
        {
            var value = await service.TestAsync(ctx.GetString("transport") ?? "auto",
                Math.Clamp(ctx.GetInt("timeout", 15), 1, 120), ctx.Cancellation);
            return value.State == "ok"
                ? CommandResult.Ok($"GitHub {value.Transport} 检测通过", value)
                : new CommandResult { Success = false, Message = $"GitHub 检测: {value.State}", Data = value };
        },
    };

    private static CommandDescriptor Login(GitHubAccountService service) => new()
    {
        Name = "github.login",
        Summary = "在服务器本机会话中启动 GCM GitHub 登录",
        Parameters = [new ParameterSpec { Name = "account", Description = "可选 GitHub 用户名", Position = 0 }],
        ConfirmPrompt = _ => "确认在服务器本机启动 Git Credential Manager 登录？",
        Handler = async ctx =>
        {
            if (!IsLocalShell(ctx.Source))
                return CommandResult.Fail("github.login 只允许服务器本机 Shell 会话");
            var result = await service.LoginAsync(ctx.GetString("account"), ctx.Cancellation);
            return result.Success
                ? CommandResult.Ok("GCM 登录流程已完成")
                : CommandResult.Fail(GitHubRedactor.Redact(result.CombinedOutput));
        },
    };

    private static CommandDescriptor Logout(GitHubAccountService service) => new()
    {
        Name = "github.logout",
        Summary = "预览或注销服务器 GCM GitHub HTTPS 凭据账号",
        Parameters =
        [
            new ParameterSpec { Name = "account", Description = "GCM 账号", Required = true, Position = 0 },
            Bool("apply", "false 预览；true 确认后注销", "false"),
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply")
            ? $"确认注销服务器 GCM 账号 {ctx.RequireString("account")}？"
            : null,
        Handler = async ctx =>
        {
            if (!IsLocalShell(ctx.Source))
                return CommandResult.Fail("github.logout 只允许服务器本机 Shell 会话");
            try
            {
                var value = await service.LogoutAsync(ctx.RequireString("account"),
                    ctx.GetBool("apply"), ctx.Cancellation);
                return CommandResult.Ok(value.Action, value);
            }
            catch (Exception ex) { return CommandResult.Fail(GitHubRedactor.Redact(ex.Message)); }
        },
    };

    private static CommandDescriptor Identity(GitHubAccountService service) => new()
    {
        Name = "github.identity",
        Summary = "预览或修改服务器 Git 提交作者身份",
        Parameters =
        [
            new ParameterSpec { Name = "name", Description = "Git user.name", Required = true, Position = 0 },
            new ParameterSpec { Name = "email", Description = "Git user.email", Required = true, Position = 1 },
            new ParameterSpec { Name = "scope", Description = "写入作用域", AllowedValues = ["repository", "global"], Default = "repository" },
            Bool("apply", "false 预览；true 确认后修改", "false"),
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply")
            ? $"确认修改服务器 Git 提交身份（{ctx.GetString("scope")}）？"
            : null,
        Handler = async ctx =>
        {
            if (!IsLocalShell(ctx.Source))
                return CommandResult.Fail("github.identity 当前只允许服务器本机 Shell 会话");
            try
            {
                var value = await service.SetIdentityAsync(ctx.RequireString("name"), ctx.RequireString("email"),
                    ctx.GetString("scope") ?? "repository", ctx.GetBool("apply"), ctx.Cancellation);
                return CommandResult.Ok(value.Action, value);
            }
            catch (Exception ex) { return CommandResult.Fail(GitHubRedactor.Redact(ex.Message)); }
        },
    };

    private static CommandDescriptor Remote(GitHubAccountService service) => new()
    {
        Name = "github.remote",
        Summary = "预览或分别修改服务器 origin fetch/push URL",
        Parameters =
        [
            new ParameterSpec { Name = "fetch", Description = "新的 fetch URL" },
            new ParameterSpec { Name = "push", Description = "新的 push URL" },
            Bool("apply", "false 预览；true 确认后修改", "false"),
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply") ? "确认修改服务器仓库 origin？" : null,
        Handler = async ctx =>
        {
            if (!IsLocalShell(ctx.Source))
                return CommandResult.Fail("github.remote 当前只允许服务器本机 Shell 会话");
            try
            {
                var value = await service.SetRemoteAsync(ctx.GetString("fetch"), ctx.GetString("push"),
                    ctx.GetBool("apply"), ctx.Cancellation);
                return CommandResult.Ok(value.Action, value);
            }
            catch (Exception ex) { return CommandResult.Fail(GitHubRedactor.Redact(ex.Message)); }
        },
    };

    private static bool IsLocalShell(string source)
        => source.StartsWith("Shell:", StringComparison.OrdinalIgnoreCase)
           || source.Equals("UI", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("脚本:", StringComparison.OrdinalIgnoreCase);

    private static ParameterSpec Bool(string name, string description, string defaultValue) => new()
    {
        Name = name,
        Description = description,
        Type = ParamType.Bool,
        Default = defaultValue,
    };

    private static string Render(GitHubAccountOverview value)
    {
        var text = new StringBuilder("GitHub 账号状态:");
        text.Append($"\n  Git       : {value.GitVersion}");
        text.Append($"\n  GCM       : {value.CredentialManagerVersion}");
        text.Append($"\n  gh CLI    : {(value.GhCliAvailable ? "已安装（非必需）" : "未安装（非必需）")}");
        text.Append($"\n  提交身份  : {value.EffectiveIdentity.Name} <{value.EffectiveIdentity.Email}> [{value.EffectiveIdentity.Source}]");
        text.Append($"\n  origin    : {value.Origin.FetchUrl} [{value.Origin.Transport}]");
        text.Append($"\n  仓库      : {value.Origin.Owner}/{value.Origin.Repository}");
        text.Append($"\n  GCM 账号  : {value.CredentialAccounts.Count}");
        text.Append($"\n  SSH 公钥  : {value.Ssh.PublicKeys.Count}");
        return text.ToString();
    }
}
