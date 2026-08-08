namespace HistoryVulcan.Core.Modules;

/// <summary>由服务宿主 UI 线程创建和销毁的模块能力。</summary>
public interface IUiModule
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void CreateUi();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void DestroyUi();
}

/// <summary>模块实现本接口后，宿主会在 CreateUi 前注入界面注册器。</summary>
public interface IShellUiAware
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    IShellUiRegistrar ShellUi { set; }
}

/// <summary>为反射模块方法补充命令元数据。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ModuleCommandAttribute : Attribute
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool Readonly { get; init; }
}

/// <summary>网关就绪后执行的非关键启动工作；失败不得阻断宿主。</summary>
public interface IDeferredStartupWork
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
