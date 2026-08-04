using BaseVariable;

namespace ActiveDock;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "dock";
    public override string Description => "桌面右下角活动项目坞";
    public override string Author => "OneHistory";
    public override string Version => "2.0.1";
    public override Type? MainClassType => typeof(ActiveDockCommands);
}
