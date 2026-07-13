using AppShell.Core.Docking;

namespace AppShell.Shell;

/// <summary>
/// 派生应用向 Shell 提交的装配清单(§9 开发流程第 2/7 条的入口)。
/// </summary>
public sealed class ShellConfig
{
    public required string AppName { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>
    /// 主窗口(中央设计器区)内容组件(M-01 注入点);
    /// null 时使用模板默认占位页(M-02)。
    /// </summary>
    public object? MainContent { get; set; }

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
}
