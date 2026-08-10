using BaseVariable;

namespace HistoryDiana;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "HistoryDiana";
    public override string Description => "HistoryVesta 工作树只读巡检：概况、最近修改与大文件热点";
    public override string Author => "OneHistory";
    public override string Version => typeof(ModuleInfo).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("HistoryDiana 程序集未携带版本信息。");

    // 命令由 IModuleContext.RegisterCommands 显式登记，避免旧反射前缀形成两段式命令。
    public override Type? MainClassType => null;
}
