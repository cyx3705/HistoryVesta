// MyUtilsModule/ModuleInfo.cs
using BaseVariable;

namespace NodeRegistryModule;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "NodeRegistryModule";
    public override string Description => "提供网络配置和节点注册功能";
    public override string Author => "Pinavia";
    public override string Version => "v1.0.0";
    public override bool Open => true;

    public override int InitializeOrder => 5;
}
