using AppShell.Core.Commands;
using AppShell.Services.Web;

namespace OneHistoryStudio.Connection;

public sealed record LanServerStatus(
    string ServerId,
    string Bind,
    int Port,
    bool Running,
    int Clients,
    int Shells,
    int DeviceCount,
    LanConfigurationStatus Configuration);

public static class LanCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        LanDeviceStore devices,
        WebGateway web,
        LanConfigurationService configuration,
        string source = "app")
    {
        registry.Register(new CommandDescriptor
        {
            Name = "lan.status",
            Summary = "查看服务器 LAN、TLS、设备和会话状态",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                "LAN 状态",
                new LanServerStatus(
                    devices.ServerId,
                    web.BindAddress,
                    web.Port,
                    web.IsRunning,
                    web.ConnectedClients,
                    web.ConnectedShells,
                    devices.List().Count,
                    configuration.Status()))),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "lan.configure",
            Summary = "预览或应用服务器 LAN 地址、端口和启用状态",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "enabled",
                    Description = "是否允许 Private LAN 连接",
                    Type = ParamType.Bool,
                    Default = "false",
                },
                new ParameterSpec
                {
                    Name = "bind",
                    Description = "本机活动 Private IPv4",
                    Required = true,
                },
                new ParameterSpec
                {
                    Name = "port",
                    Description = "HTTPS 端口",
                    Type = ParamType.Int,
                    Default = "8738",
                },
                BoolApply(),
            ],
            ConfirmPrompt = ctx => ctx.GetBool("apply")
                ? "确认请求 UAC 并变更 HTTPS、URLACL 和 Private 防火墙规则？"
                : null,
            Handler = async ctx =>
            {
                if (!IsLocal(ctx.Source))
                    return CommandResult.Fail("lan.configure 只允许服务器本机 Shell");
                try
                {
                    var result = await configuration.ConfigureAsync(
                        ctx.GetBool("enabled"),
                        ctx.RequireString("bind"),
                        ctx.GetInt("port", 8738),
                        ctx.GetBool("apply"),
                        ctx.Cancellation);
                    return result.Success
                        ? CommandResult.Ok(result.Message, result)
                        : CommandResult.Fail(result.Message);
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "lan.cert.rotate",
            Summary = "预览或轮换 LAN HTTPS 证书",
            Parameters = [BoolApply()],
            ConfirmPrompt = ctx => ctx.GetBool("apply")
                ? "确认请求 UAC 并轮换 LAN 证书？所有客户端必须重新核对指纹。"
                : null,
            Handler = async ctx =>
            {
                if (!IsLocal(ctx.Source))
                    return CommandResult.Fail("lan.cert.rotate 只允许服务器本机 Shell");
                var result = await configuration.RotateCertificateAsync(
                    ctx.GetBool("apply"), ctx.Cancellation);
                return result.Success
                    ? CommandResult.Ok(result.Message, result)
                    : CommandResult.Fail(result.Message);
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "lan.paircode",
            Summary = "在服务器本机生成一次性设备配对码",
            ConfirmPrompt = _ => "确认生成两分钟有效、使用一次即失效的设备配对码？",
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!IsLocal(ctx.Source))
                    return CommandResult.Fail("lan.paircode 只允许服务器本机 Shell");
                var value = devices.CreatePairCode();
                return CommandResult.Ok("配对码已生成；不会写入日志或设置", value);
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "lan.device.list",
            Summary = "列出已配对设备，不包含 token 或 token hash",
            Readonly = true,
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!IsLocal(ctx.Source))
                    return CommandResult.Fail("lan.device.list 只允许服务器本机 Shell");
                var values = devices.List();
                return CommandResult.Ok($"已配对设备: {values.Count}", values);
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "lan.device.scope",
            Summary = "修改已配对设备权限",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "device",
                    Description = "设备 ID",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "scope",
                    Description = "目标权限",
                    Required = true,
                    Position = 1,
                    AllowedValues = ["read", "operate", "admin"],
                },
                BoolApply(),
            ],
            ConfirmPrompt = ctx => ctx.GetBool("apply")
                ? $"确认把设备 {ctx.RequireString("device")} 权限改为 {ctx.RequireString("scope")}？"
                : null,
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!IsLocal(ctx.Source))
                    return CommandResult.Fail("lan.device.scope 只允许服务器本机 Shell");
                if (!ctx.GetBool("apply"))
                    return CommandResult.Ok("权限修改预览", new
                    {
                        deviceId = ctx.RequireString("device"),
                        scope = ctx.RequireString("scope"),
                        applied = false,
                    });
                var deviceId = ctx.RequireString("device");
                if (!devices.SetScope(deviceId, ctx.RequireString("scope")))
                    return CommandResult.Fail("设备不存在");
                var disconnected = web.DisconnectDevice(deviceId);
                return CommandResult.Ok($"设备权限已修改；已断开 {disconnected} 个现有会话以重新鉴权");
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "lan.device.revoke",
            Summary = "撤销已配对设备，现有 token 立即失效",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "device",
                    Description = "设备 ID",
                    Required = true,
                    Position = 0,
                },
                BoolApply(),
            ],
            ConfirmPrompt = ctx => ctx.GetBool("apply")
                ? $"确认撤销设备 {ctx.RequireString("device")}？"
                : null,
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!IsLocal(ctx.Source))
                    return CommandResult.Fail("lan.device.revoke 只允许服务器本机 Shell");
                if (!ctx.GetBool("apply"))
                    return CommandResult.Ok("设备撤销预览", new
                    {
                        deviceId = ctx.RequireString("device"),
                        applied = false,
                    });
                var deviceId = ctx.RequireString("device");
                if (!devices.Revoke(deviceId))
                    return CommandResult.Fail("设备不存在");
                var disconnected = web.DisconnectDevice(deviceId);
                return CommandResult.Ok($"设备已撤销；已断开 {disconnected} 个现有会话");
            }),
        }, source);
    }

    private static ParameterSpec BoolApply() => new()
    {
        Name = "apply",
        Type = ParamType.Bool,
        Default = "false",
        Description = "false 预览；true 确认后应用",
    };

    private static bool IsLocal(string source)
        => source.Equals("UI", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("Shell:", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("脚本:", StringComparison.OrdinalIgnoreCase);
}
