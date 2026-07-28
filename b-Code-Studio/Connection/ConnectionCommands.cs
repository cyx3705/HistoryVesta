using AppShell.Core.Commands;

namespace OneHistoryStudio.Connection;

public static class ConnectionCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        ConnectionProfileService profiles,
        string source = "app:frontend")
    {
        registry.Register(new CommandDescriptor
        {
            Name = "conn.status",
            Summary = "查看当前设备角色、端点和连接状态",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var status = profiles.Status();
                return CommandResult.Ok(
                    $"{status.Role} · {status.ConnectionState} · {status.Endpoint}", status);
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "conn.role",
            Summary = "预览或保存当前设备的服务器/客户端角色",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "当前设备角色",
                    Required = true,
                    Position = 0,
                    AllowedValues = ["server", "client"],
                },
                Apply(),
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var role = ctx.RequireString("mode").Equals(
                        "client", StringComparison.OrdinalIgnoreCase)
                        ? NodeRole.Client : NodeRole.Server;
                    var applied = ctx.GetBool("apply");
                    var value = profiles.SetRole(role, applied);
                    return CommandResult.Ok(
                        applied ? "角色已保存，重启后生效" : "角色切换预览",
                        new { profile = value, applied, requiresRestart = applied });
                }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "conn.endpoint",
            Summary = "预览或保存客户端服务器地址和证书指纹",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "url",
                    Description = "https://host:port/",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "fingerprint",
                    Description = "服务器证书 SHA-256 指纹",
                    Required = true,
                },
                Apply(),
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                try
                {
                    var applied = ctx.GetBool("apply");
                    var value = profiles.SetEndpoint(
                        ctx.RequireString("url"),
                        ctx.RequireString("fingerprint"),
                        applied);
                    return CommandResult.Ok(
                        applied ? "服务器端点已保存，重启后生效" : "服务器端点预览",
                        new { profile = value, applied, requiresRestart = applied });
                }
                catch (Exception ex) { return CommandResult.Fail(ex.Message); }
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "conn.pair",
            Summary = "使用一次性配对码和已固定的证书指纹完成设备配对",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "code",
                    Description = "一次性配对码（回显自动掩码）",
                    Required = true,
                },
                new ParameterSpec
                {
                    Name = "fingerprint",
                    Description = "服务器证书 SHA-256 指纹",
                    Required = true,
                },
                new ParameterSpec
                {
                    Name = "name",
                    Description = "设备显示名称",
                    Required = true,
                },
            ],
            Handler = async ctx =>
            {
                var result = await profiles.PairAsync(
                    ctx.RequireString("code"),
                    ctx.RequireString("fingerprint"),
                    ctx.RequireString("name"),
                    ctx.Cancellation);
                return result.Success
                    ? CommandResult.Ok("设备配对成功；token 已用 DPAPI 保存", result)
                    : CommandResult.Fail(result.Failure ?? "设备配对失败");
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "conn.forget",
            Summary = "清除当前设备保存的 token 和服务器证书 pin",
            Parameters = [Apply()],
            ConfirmPrompt = ctx => ctx.GetBool("apply")
                ? "确认断开并清除本机设备 token 与服务器证书 pin？"
                : null,
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.GetBool("apply"))
                    return CommandResult.Ok("清除服务器配对预览");
                profiles.Forget();
                return CommandResult.Ok("本机服务器配对已清除");
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "conn.reconnect",
            Summary = "使用当前本机 profile 重连服务器",
            Handler = async ctx => await profiles.ReconnectAsync(ctx.Cancellation)
                ? CommandResult.Ok("服务器连接已恢复")
                : CommandResult.Fail("服务器仍未连接"),
        }, source);
    }

    private static ParameterSpec Apply() => new()
    {
        Name = "apply",
        Description = "false 预览；true 保存到当前设备",
        Type = ParamType.Bool,
        Default = "false",
    };
}
