// CmdModule/ModuleInfo.cs
using BaseVariable;

namespace CmdModule;

public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "CmdModule";
    public override string Description => "提供命令行执行和控制台输出重定向到网页终端的功能。";
    public override string Author => "Pinavia";
    public override string Version => "v1.0.0";

    public override bool Open => true;
    public override Type? MainClassType => typeof(CmdApi);//由于全暴露所以这个无效了

    public override List<string> InitAddresses => new List<string>
    {
        "/api/CmdModule/CmdApi/InitConsoleRedirect"   // 把原来的方法改成接口地址
    };
    public override int InitializeOrder => 15;
}


