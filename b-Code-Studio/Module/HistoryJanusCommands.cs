using AppShell.Core.Modules;

namespace HistoryJanus.Module;

/// <summary>Reports the loaded Janus module identity and registered business surfaces.</summary>
public sealed class HistoryJanusCommands
{
    [ModuleCommand(Readonly = true)]
    public string Status()
        => "HistoryJanus 3.1.0 已由 AppShell 加载;已注册项目总览、继承树、Meta 文件、项目操作和分支历史页面;业务命令已接入宿主命令总线";
}
