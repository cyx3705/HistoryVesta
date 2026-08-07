using BaseVariable;

namespace OneHistoryStudio.Module;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "OneHistoryStudio";
    public override string Description => "OneHistoryStudio V3 模块迁移骨架";
    public override string Author => "OneHistory";
    public override string Version => "3.0.0-preview.1";
    public override Type? MainClassType => typeof(OneHistoryStudioCommands);
}
