using BaseVariable;

namespace ToolKit;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "ToolKit";
    public override string Description => "Studio 工具箱:哈希/Base64/GUID/时间戳";
    public override string Version => "1.0.1";
    public override Type? MainClassType => typeof(Kit);
}
