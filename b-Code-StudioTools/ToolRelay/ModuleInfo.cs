using BaseVariable;

namespace ToolRelay;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "ToolRelay";
    public override string Description => "Codex 动态工具转发器：实时发现并调用 OHS MCP 工具";
    public override string Author => "Codex";
    public override string Version => "1.2.0";
    public override Type? MainClassType => typeof(RelayCommands);
}
