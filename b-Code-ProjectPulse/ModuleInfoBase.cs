namespace BaseVariable;

/// <summary>
/// 模块契约副本。宿主按基类全名 BaseVariable.ModuleInfoBase 做鸭子类型识别。
/// </summary>
public abstract class ModuleInfoBase
{
    public virtual string ModuleName => GetType().Assembly.GetName().Name ?? "UnknownModule";
    public virtual string Description => "";
    public virtual string Author => "";
    public virtual string Version => "v1.0.0";
    public virtual bool Open => false;
    public virtual Type? MainClassType => null;
    public virtual bool Enabled => true;
}
