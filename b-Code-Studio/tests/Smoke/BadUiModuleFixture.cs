using AppShell.Core.Docking;
using AppShell.Core.Modules;

namespace BaseVariable
{
    public abstract class ModuleInfoBase
    {
        public virtual string ModuleName => "bad-ui";
        public virtual string Description => "ALC cleanup fixture";
        public virtual string Author => "Smoke";
        public virtual string Version => "1.0.0";
        public virtual bool Enabled => true;
        public virtual bool Open => false;
        public virtual Type? MainClassType => null;
    }
}

namespace OneHistoryStudio.Smoke.Fixtures
{
    public sealed class BadModuleInfo : BaseVariable.ModuleInfoBase
    {
        public override Type? MainClassType => typeof(BadCommands);
    }

    public sealed class BadCommands
    {
        public string Ping() => "pong";
    }

    public sealed class BadUiModule : IUiModule, IShellUiAware
    {
        public IShellUiRegistrar ShellUi { private get; set; } = null!;

        public void CreateUi()
            => ShellUi.RegisterToolWindow(new ToolWindowDescriptor
            {
                Id = "bad-ui-window",
                Title = "Bad UI",
                ContentFactory = static () => new object(),
            }, "Smoke");

        // Deliberately omits unregister/dispose: ModuleHost must clean up by owner.
        public void DestroyUi()
        {
        }
    }
}
