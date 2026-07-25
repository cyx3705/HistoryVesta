namespace BaseVariable;

/// <summary>
/// 模块契约副本(与 b-Code-MyAPI-Lite\BaseVariable 同源,MD-02):
/// 宿主按基类全名 "BaseVariable.ModuleInfoBase" 做鸭子类型识别,
/// 契约类编译进模块自身或独立 BaseVariable.dll 均可被装载。
/// </summary>
public abstract class ModuleInfoBase
{
    /// <summary>模块名称(默认取程序集名);同时是指令域名。</summary>
    public virtual string ModuleName => GetType().Assembly.GetName().Name ?? "UnknownModule";

    /// <summary>模块描述</summary>
    public virtual string Description => "";

    /// <summary>作者</summary>
    public virtual string Author => "";

    /// <summary>版本号</summary>
    public virtual string Version => "v1.0.0";

    /// <summary>true = 全暴露:程序集内所有公共类的公共方法都托管;false = 精准暴露 MainClassType。</summary>
    public virtual bool Open => false;

    /// <summary>精准暴露模式下要托管的核心业务类;为 null 则不注册任何指令。</summary>
    public virtual Type? MainClassType => null;

    /// <summary>是否启用该模块(false 时整个模块不注册)。</summary>
    public virtual bool Enabled => true;
}
