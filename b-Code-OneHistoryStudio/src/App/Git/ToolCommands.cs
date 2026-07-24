using AppShell.Core.Commands;

namespace OneHistoryStudio.Git;

/// <summary>
/// tool.* 指令域(V2.2 自扩展飞轮)。
/// M1 = tool.scan(只读盘点,readonly 档暴露);M2 追加 sync / remove / list。
/// </summary>
public static class ToolCommands
{
    public static void RegisterAll(CommandRegistry registry, ToolSyncService tools, string source = "tool")
    {
        registry.Register(new CommandDescriptor
        {
            Name = "tool.scan",
            Summary = "扫描项目库全部工具清单(z 级元文件夹的 module.manifest.json),标注部署状态",
            Example = "tool.scan",
            Handler = async _ =>
            {
                var (success, message, rows) = await tools.ScanAsync();
                return success ? CommandResult.Ok(message, rows) : CommandResult.Fail(message);
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "tool.sync",
            Summary = "把清单声明的工具产物同步入模块槽(复制+SHA-256 溯源,热重载自动接手)",
            Example = "tool.sync name=ToolDemo",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "工具名(tool.scan 所列);与 all 二选一",
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "all",
                    Description = "true 时同步全部「未同步/已过期」项",
                    Type = ParamType.Bool,
                    Default = "false",
                },
            ],
            Handler = async ctx =>
            {
                var (success, message) = await tools.SyncAsync(
                    ctx.GetString("name"), ctx.GetBool("all"), ctx.Progress);
                return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "tool.remove",
            Summary = "移除已同步工具:删除模块槽 + 注销溯源(指令随热重载注销)",
            Example = "tool.remove name=ToolDemo",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "工具名(tool.list 所列)",
                    Required = true,
                    Position = 0,
                },
            ],
            ConfirmPrompt = ctx =>
                $"将移除工具模块 {ctx.RequireString("name")}:\n\n" +
                $"• 删除模块槽 Modules\\{ctx.RequireString("name")}\\(含依赖与面板)\n" +
                $"• 注销溯源记录;其指令域随热重载消失\n\n" +
                $"来源项目中的产物不受影响,可随时 tool.sync 重新入库。确定?",
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var (success, message) = tools.Remove(ctx.RequireString("name"));
                return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "tool.list",
            Summary = "列出已同步工具的溯源(来源分支/版本/哈希/时间/槽状态)",
            Example = "tool.list",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var (success, message, rows) = tools.ListRegistered();
                return success ? CommandResult.Ok(message, rows) : CommandResult.Fail(message);
            }),
        }, source);
    }
}
