using AppShell.Core.Commands;

namespace OneHistoryStudio.Mcp;

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

    private static readonly HashSet<string> ReadonlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "history",
        "proj.list", "proj.tree", "proj.scan", "proj.config", "proj.metalist",
        "proj.history", "proj.history.show", "proj.history.diff",
        "db.query", "db.tables", "db.schema", "db.list",
        "module.list", "win.list", "layout.list", "app.get",
        "prompt.get", "prompt.history", "prompt.diff", "correction.list", "incident.list",
        "command.list", "command.show", "command.domains",
        "git.rule.list", "git.rule.scan", "git.rule.gaps", "git.rule.suggest",
        "tool.scan", "tool.list",
    };

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
