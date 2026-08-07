using AppShell.Core.Modules;

namespace OneHistoryStudio.Module;

/// <summary>Initial host probe; business commands remain in the pending service-module migration.</summary>
public sealed class OneHistoryStudioCommands
{
    [ModuleCommand(Readonly = true)]
    public string Status()
        => "OneHistoryStudio 3.0.0-preview.1 已由 AppShell 3.1.7 模块宿主加载;业务服务迁移待服务模块合同冻结";
}
