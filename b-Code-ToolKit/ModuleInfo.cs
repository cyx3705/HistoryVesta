using BaseVariable;

namespace ToolKit;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "ToolKit";
    public override string Description => "Studio 工具箱:哈希/GUID/时间戳(飞轮第一圈产物)";
    public override string Version => "1.0.0";
    public override Type? MainClassType => typeof(Kit);
}
