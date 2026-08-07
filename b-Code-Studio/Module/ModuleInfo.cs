using BaseVariable;

namespace OneHistoryStudio.Module;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "OneHistoryStudio";
    public override string Description => "OneHistoryStudio 项目与 Git 治理模块";
    public override string Author => "OneHistory";
    public override string Version =>
        typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public override Type? MainClassType => typeof(OneHistoryStudioCommands);
}
