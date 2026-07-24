//ManagerModule/ModuleInfo.cs
namespace ManagerModule;
using BaseVariable;
public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "管理器模块";
    public override string Description => "简单的桌面管理工具";
    public override string Author => "Pinavia";
    public override string Version => "v1.0.0";
    public override bool Open => true;
}
