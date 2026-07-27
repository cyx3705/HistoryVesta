using AppShell.Core.Docking;

namespace AppShell.Shell;

/// <summary>
/// 派生应用向 Shell 提交的装配清单(§9 开发流程第 2/7 条的入口)。
/// </summary>
public sealed class ShellConfig
{
    public required string AppName { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>要注册的工具窗口清单。</summary>
    public List<ToolWindowDescriptor> ToolWindows { get; } = new();

    /// <summary>
    /// 派生应用注册自定义指令的挂点(§5.3,§9 流程第 3 条):
    /// 在内置指令组注册完成后调用;指令名冲突会在此时抛出。
    /// </summary>
    public Action<Core.Commands.CommandRegistry>? ConfigureCommands { get; set; }

    /// <summary>
    /// 数据服务(§6.1,§9 流程第 5 条):派生应用注册连接后交给 Shell;
    /// 非 null 时 db.* 指令组注册、Id 为 "table" 的窗口内容由 Shell 的表窗口接管。
    /// </summary>
    public Core.Data.IDataService? DataService { get; set; }

    /// <summary>
    /// 工作区文件服务(§4.6,§9 流程第 6 条):非 null 时 res.* 指令组注册、
    /// Id 为 "resource" 的窗口内容由 Shell 的资源窗口接管。
    /// </summary>
    public Core.Files.IWorkspaceService? Workspace { get; set; }

    /// <summary>
    /// 资源窗口“双击打开”接管挂点(R-03):返回 true 表示派生应用已处理,
    /// 否则回落到 res.open(系统默认程序)。参数为文件绝对路径。
    /// </summary>
    public Func<string, bool>? OnResourceOpen { get; set; }

    /// <summary>
    /// C# 通道声明的控制面板(P-01;与数据目录 panels/*.json 合并,JSON 优先加载在后)。
    /// </summary>
    public List<Core.Panels.PanelDefinition> Panels { get; } = new();

    // ---------------------------------------------------------------- 0.4.4 反哺能力(默认启用)

    /// <summary>
    /// 模块托管(MD-01~08,0.4.4 由 OneHistoryStudio 反哺):
    /// &lt;数据目录&gt;\Modules 热重载,DLL 即指令域;module.* 指令组随之注册。
    /// 默认启用——派生应用开箱即有模块注册器,无需自行装配。
    /// 置 false 则完全不创建宿主、不注册 module.*。
    /// </summary>
    public bool EnableModules { get; set; } = true;

    /// <summary>
    /// 在本进程加载声明了 ui=true 的模块界面。默认由 EnableModules 隐式启用;
    /// 前端/服务分离应用可在 EnableModules=false 时单独置 true。
    /// </summary>
    public bool EnableUiModules { get; set; }

    /// <summary>双击工具窗口标题条时切换窗口最大化。</summary>
    public bool EnableMaximizeOnDoubleClick { get; set; } = true;

    /// <summary>
    /// MCP 服务(0.4.4 由 OneHistoryStudio 反哺):元数据自描述层、网关、提示词治理,
    /// 注册 mcp.* / command.* / prompt.* / correction.* / incident.* 指令组。
    /// 默认启用并随宿主自动监听；mcp.autostart=false 可关闭自动监听，之后仍可 mcp.start。
    /// 依赖 <see cref="DataService"/>:未配置数据服务时本项自动降级为关闭并告警。
    /// </summary>
    public bool EnableMcp { get; set; } = true;

    /// <summary>
    /// 客户端模式下只创建命令集、指令详情和模块管理视图，不在本进程创建 MCP 或 ModuleHost。
    /// 视图经 CommandBus.RemoteExecutor 读取服务端结构化结果。
    /// </summary>
    public bool EnableRemoteManagementViews { get; set; }

    /// <summary>
    /// MCP 调用留痕接管点:null 时框架用内置 McpAuditRecorder 写 mcp_history。
    /// 派生应用若已有自己的留痕器,实现 IMcpAuditLog 接进来即可共用同一张表。
    /// </summary>
    public Core.Mcp.IMcpAuditLog? McpAuditLog { get; set; }

    /// <summary>
    /// 应用身份:null 时取 <c>AppIdentity.Current</c>(入口程序集)。
    /// 测试宿主等入口程序集不是应用本体的场景应显式提供。
    /// </summary>
    public Core.ApplicationIdentity? Identity { get; set; }

    /// <summary>
    /// MCP 危险调用的宿主确认中继(CX-02,host 档使用):
    /// null 时框架用内置对话框 <c>RemoteConfirmDialog</c>。
    /// </summary>
    public Func<string, string, int, bool?>? McpRemoteConfirm { get; set; }

    /// <summary>
    /// 命令集选中状态(0.4.4):框架的命令集窗口(McpToolsView)写入选中的指令名。
    /// 派生应用若有指令详情窗口需与之联动,应在此传入**同一个实例**,并把详情窗口也接到它;
    /// null 时框架自建一个(此时派生侧无法与之联动)。
    /// 由派生应用创建并传入(而非框架创建后回取),是因为工具窗口内容工厂在 ShellWindow
    /// 构造期间(DockingHost 构建默认布局时)即被调用,那时派生应用尚拿不到 window 实例。
    /// </summary>
    public Core.Commands.CommandSelectionState? CommandSelection { get; set; }
}
