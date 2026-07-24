//CmdModule/ModuleInfo.cs
namespace CmdModule;
using BaseModule;
public class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "启动模块";
    public override string Description => "帮助主函数瘦身";
    public override string Author => "Pinavia";
    public override string Version => "v1.0.0";

}
