// ReverseProxyModule/ModuleInfo.cs
using BaseModule;

namespace ReverseProxyModule;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "ReverseProxyModule";
    public override string Description => "提供反向代理和启动功能。";
    public override string Author => "Pinavia";
    public override string Version => "v1.0.0";

    // 因为是静态类，不需要注册实例到 DynamicController
    public override Type? MainClassType => null;


    public override int InitializeOrder => 50;  
}