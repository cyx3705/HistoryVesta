using BaseVariable;

namespace ToolKit;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "ToolKit";
    public override string Description => "Studio 工具箱：哈希、Base64、GUID 和时间戳";
    public override string Author => "OneHistory";
    public override string Version => "1.2.0";
    public override Type? MainClassType => typeof(Kit);
}
