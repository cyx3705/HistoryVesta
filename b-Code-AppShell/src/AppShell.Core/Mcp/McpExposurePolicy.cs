using AppShell.Core.Commands;

namespace AppShell.Core.Mcp;

/// <summary>MCP 暴露规则的单一真值，供网关、command.* 和管理页共同解释。</summary>
public static class McpExposurePolicy
{
    /// <summary>V2.2 CX-01:命令名 → 所属模块名(注册来源 "module:&lt;name&gt;");装配点接 Registry.GetSource。</summary>
    public static Func<string, string?>? ModuleOfCommand { get; set; }

    /// <summary>V2.2 CX-01:模块名 → 清单声明的 mcpExposure;无清单(根平铺)返回 null = standard 现状。</summary>
    public static Func<string, string?>? ModuleExposure { get; set; }

    private static string? ExposureOf(string commandName)
    {
        var module = ModuleOfCommand?.Invoke(commandName);
        return module == null ? null : ModuleExposure?.Invoke(module);
    }

    /// <summary>
    /// 框架自有的只读指令基线(0.4.4)。
    /// **只登记框架自己注册的指令**——派生应用的只读指令一律由 RegisterReadonly 追加,
    /// 框架层不得写入任何派生应用专有的指令名(否则每个派生都背着别人的指令表)。
    /// </summary>
    private static readonly HashSet<string> ReadonlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "history",
        "db.query", "db.tables", "db.schema", "db.list",
        "module.list", "win.list", "layout.list", "app.get",
        "prompt.get", "prompt.history", "prompt.diff", "correction.list", "incident.list",
        "command.list", "command.show", "command.domains",
    };

    /// <summary>
    /// 派生应用登记自己的只读指令(0.4.4)。幂等、可重复调用;
    /// 应在指令注册之后、网关启动之前完成(装配点 ConfigureCommands 内或紧随其后)。
    /// </summary>
    public static void RegisterReadonly(params string[] commandNames)
    {
        foreach (var name in commandNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
                ReadonlyCommands.Add(name.Trim());
        }
    }

    /// <summary>当前生效的只读指令全集(框架基线 + 派生登记),供管理页与自检使用。</summary>
    public static IReadOnlyCollection<string> ReadonlyCommandNames => ReadonlyCommands;

    public static bool IsReadonlyAllowed(string commandName)
        => ReadonlyCommands.Contains(commandName)
           || string.Equals(ExposureOf(commandName), "readonly", StringComparison.OrdinalIgnoreCase);

    public static string? HardExclusionReason(string commandName)
    {
        if (commandName.Equals("app.exit", StringComparison.OrdinalIgnoreCase))
            return "远程客户端不得退出宿主";
        if (commandName.StartsWith("debug.", StringComparison.OrdinalIgnoreCase))
            return "调试与承压指令不对远程暴露";
        if (commandName.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase))
            return "防止远程递归管理或关闭 MCP 服务";
        if (string.Equals(ExposureOf(commandName), "hidden", StringComparison.OrdinalIgnoreCase))
            return "模块清单声明 mcpExposure=hidden,不对 MCP 暴露(Q211-2)";
        return null;
    }

    public static string State(CommandDescriptor descriptor)
    {
        if (HardExclusionReason(descriptor.Name) != null)
            return "hidden";
        if (descriptor.ConfirmPrompt != null)
            return "dangerous";
        return IsReadonlyAllowed(descriptor.Name) ? "readonly" : "standard";
    }

    public static bool IsVisible(CommandDescriptor descriptor, string policy)
        => HardExclusionReason(descriptor.Name) == null
           && descriptor.ConfirmPrompt == null
           && (policy.Equals("standard", StringComparison.OrdinalIgnoreCase)
               || IsReadonlyAllowed(descriptor.Name));
}
