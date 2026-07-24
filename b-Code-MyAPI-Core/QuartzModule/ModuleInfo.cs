// QuartzModule/ModuleInfo.cs
using BaseVariable;

namespace QuartzModule
{
    public class ModuleInfo : ModuleInfoBase
    {
        public override string ModuleName => "QuartzModule";
        public override string Description => "提供 Quartz 定时任务、轮询任务和心跳管理功能。";
        public override string Author => "Pinavia";
        public override string Version => "v1.0.0";

        public override bool Open => true;

        // 因为是静态类，不需要注册实例到 DynamicController
        public override Type? MainClassType => null;

        // 通过地址列表声明需要自调用的初始化接口
        public override List<string> InitAddresses => new List<string>
        {
        "/api/QuartzModule/QuartzHeart/InitializeAsync"   // 把原来的方法改成接口地址
        };

        public override int InitializeOrder => 20;   // 定时任务建议较早启动
    }
}