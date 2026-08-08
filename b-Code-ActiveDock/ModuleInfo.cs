using BaseVariable;

namespace ActiveDock;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "dock";
    public override string Description => "活动项目坞与资源管理器 OHS 项目入口";
    public override string Author => "OneHistory";
    public override string Version => "2.3.2";
    public override Type? MainClassType => typeof(ActiveDockCommands);
}
