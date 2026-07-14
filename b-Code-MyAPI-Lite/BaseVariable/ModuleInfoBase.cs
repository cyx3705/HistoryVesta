namespace BaseVariable;

/// <summary>
/// 模块描述基类：模块 DLL 中必须包含一个它的公共子类，宿主才会将其托管为 HTTP 接口。
/// 没有 ModuleInfoBase 子类的 DLL 会被当作依赖库加载，但不暴露任何接口。
/// </summary>
public abstract class ModuleInfoBase
{
    /// <summary>模块名称（默认取程序集名）</summary>
    public virtual string ModuleName => GetType().Assembly.GetName().Name ?? "UnknownModule";

    /// <summary>模块描述</summary>
    public virtual string Description => "";

    /// <summary>作者</summary>
    public virtual string Author => "";

    /// <summary>版本号</summary>
    public virtual string Version => "v1.0.0";

    /// <summary>
    /// true = 全暴露：程序集内所有公共类的公共方法都托管为接口；
    /// false = 精准暴露：只托管 MainClassType 指定的类。
    /// </summary>
    public virtual bool Open => false;

    /// <summary>精准暴露模式下要托管的核心业务类；为 null 则不注册任何接口</summary>
    public virtual Type? MainClassType => null;

    /// <summary>是否启用该模块（false 时整个模块不注册）</summary>
    public virtual bool Enabled => true;
}
