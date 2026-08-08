using HistoryVulcan.Core.Modules;

namespace HistoryJanus.Module;

/// <summary>Reports the loaded Janus module identity and registered business surfaces.</summary>
public sealed class HistoryJanusCommands
{
    [ModuleCommand(Readonly = true, CommandClass = "status")]
    public string Status()
        => "HistoryJanus 3.2.0 已由 HistoryVulcan 加载;已注册项目总览、项目操作和分支历史页面;Meta 文件夹已合并到项目总览;业务命令已接入宿主命令总线";
}
