using BaseVariable;

namespace HistoryJanus.Module;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "HistoryJanus";
    public override string Description => "HistoryJanus 项目与 Git 治理模块";
    public override string Author => "OneHistory";
    public override string Version =>
        typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public override Type? MainClassType => typeof(HistoryJanusCommands);
}
