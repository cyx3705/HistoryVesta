namespace AppShell.Core.Commands;

/// <summary>
/// 一条指令的注册模型(§5.3):名称、参数定义、执行体、帮助文本、
/// 二次确认与撤销能力位(撤销为 Q6 预留,首版不实现)。
/// </summary>
public sealed class CommandDescriptor
{
    /// <summary>完整指令名,小写,如 "win.dock"、"help"。</summary>
    public required string Name { get; init; }

    /// <summary>一句话说明(help 列表用)。</summary>
    public required string Summary { get; init; }

    /// <summary>示例行(help 详情用),如 "win.dock name=console pos=bottom ratio=0.25"。</summary>
    public string? Example { get; init; }

    public IReadOnlyList<ParameterSpec> Parameters { get; init; } = [];

    /// <summary>执行前需二次确认时,返回确认提示文本;null 表示无需确认(§5.2 拦截器)。</summary>
    public Func<CommandContext, string?>? ConfirmPrompt { get; init; }

    /// <summary>撤销能力位(§5.4,Q6:首版只预留)。</summary>
    public bool SupportsUndo { get; init; }

    /// <summary>
    /// 只读声明：命令不改变持久状态（不写库、文件或 Git 状态）。
    /// MCP 暴露策略优先读取此字段；外部名称白名单仅保留为兼容层。
    /// </summary>
    public bool Readonly { get; init; }

    /// <summary>true 时总线把执行体编组到 UI 线程(win.*/layout.* 等操作窗口的指令)。</summary>
    public bool RequiresUiThread { get; init; }

    /// <summary>命令的执行位置；默认在当前宿主执行。</summary>
    public CommandExecutionSite ExecutionSite { get; init; }

    /// <summary>代理描述符可接受任意参数并原样转发；本地业务命令不应开启。</summary>
    public bool AllowUnspecifiedParameters { get; init; }

    /// <summary>执行体。长任务应内部 await 后台工作并经 Progress 上报(§5.2 约束)。</summary>
    public required Func<CommandContext, Task<CommandResult>> Handler { get; init; }

    /// <summary>同步执行体的便捷包装。</summary>
    public static Func<CommandContext, Task<CommandResult>> Sync(Func<CommandContext, CommandResult> handler)
        => ctx => Task.FromResult(handler(ctx));
}
