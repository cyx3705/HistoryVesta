using AppShell.Core.Modules;

namespace OneHistoryStudio.Module;

/// <summary>Initial host probe; business commands remain in the pending service-module migration.</summary>
public sealed class OneHistoryStudioCommands
{
    [ModuleCommand(Readonly = true)]
    public string Status()
        => "OneHistoryStudio 3.0.0-preview.1 已由 AppShell 3.1.7 加载;已注册项目总览、继承树、Meta、项目操作和分支历史页面;业务总线接入待服务模块合同冻结";
}
