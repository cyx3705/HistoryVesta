using BaseVariable;

namespace SE2SW;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "se2sw";
    public override string Description => "Solid Edge 零件与展平装配体转换为 SolidWorks";
    public override string Author => "OneHistory";
    public override string Version => "3.1.2";
    public override Type? MainClassType => typeof(SE2SWCommands);
}
