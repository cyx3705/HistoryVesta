using AppShell.Core.Mcp;

namespace OneHistoryStudio;

/// <summary>
/// 本应用向框架登记的 MCP 暴露策略(模板 0.4.4 起)。
///
/// 0.4.4 把 McpExposurePolicy 上抛到框架层时,同时清空了其中 14 条 OneHistoryStudio
/// 专有的只读指令名——框架基线只登记框架自己注册的指令,否则每个派生项目都背着别人的指令表。
/// 本类是这 14 条的**单一真值**:正式装配点(App.xaml.cs)与冒烟宿主(tests\Smoke)都调用它,
/// 避免两处各写一份而悄悄漂移。
/// </summary>
public static class AppMcpPolicy
{
    /// <summary>
    /// 登记本应用的只读指令。幂等,可重复调用。
    /// 判据:只读 = 不改变任何持久状态,可安全暴露给 readonly 档的 MCP 客户端。
    /// </summary>
    public static void RegisterReadonlyCommands()
        => McpExposurePolicy.RegisterReadonly(
            // 项目库浏览(PJ-01/04/10/12 与 V2.0.1 Meta)
            "proj.list", "proj.tree", "proj.scan", "proj.config", "proj.metalist",
            // 分支历史只读面(V2.3.0)
            "proj.history", "proj.history.show", "proj.history.diff",
            // Git 文件规则与格式台账的只读面(V2.1.3 / V2.2.1)
            "git.rule.list", "git.rule.scan", "git.rule.gaps", "git.rule.suggest",
            // 自扩展飞轮的只读面(V2.2)
            "tool.scan", "tool.list");
}
