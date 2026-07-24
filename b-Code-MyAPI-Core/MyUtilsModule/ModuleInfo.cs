// MyUtilsModule/ModuleInfo.cs
using BaseVariable;

namespace MyUtilsModule;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "MyUtilsModule";
    public override string Description => "提供基础测试方法，用于框架学习和样板演示";
    public override string Author => "Pinavia";
    public override string Version => "v1.0.0";
    public override bool Open => true;

    /// <summary>
    /// 指定要暴露的核心业务类
    /// </summary>
    public override Type? MainClassType => typeof(One);


}
