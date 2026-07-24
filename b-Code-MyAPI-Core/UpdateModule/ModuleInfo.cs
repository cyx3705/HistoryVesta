//CodePushModule/ModuleInfo.cs
using BaseVariable;

namespace CodePushModule;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "代码推送模块";
    public override string Description => "提供从本地推送拉取代码并发布局域网";
    public override string Author => "Pinavia";
    public override string Version => "v1.2.0";

    // 暴露核心类给动态控制器
    public override Type? MainClassType => typeof(CodePushService);
    public override bool Open => true;

    public override int InitializeOrder => 40;
}
