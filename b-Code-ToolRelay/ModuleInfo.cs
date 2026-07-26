using BaseVariable;

namespace ToolRelay;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "ToolRelay";
    public override string Description => "Codex 动态工具转发器：在当前任务内发现并调用 OHS 新注册的 MCP 工具";
    public override string Author => "Codex";
    public override string Version => "1.0.1";
    public override Type? MainClassType => typeof(RelayCommands);
}
