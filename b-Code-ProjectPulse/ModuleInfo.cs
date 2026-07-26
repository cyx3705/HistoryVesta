using BaseVariable;

namespace ProjectPulse;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "ProjectPulse";
    public override string Description => "面向 Codex 的只读项目工作树巡检：概况、最近修改与大文件热点";
    public override string Author => "Codex";
    public override string Version => "1.0.0";
    public override Type? MainClassType => typeof(ProjectPulseCommands);
}
