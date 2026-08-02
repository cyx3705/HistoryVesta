using BaseVariable;

namespace GitHubConnection;

public sealed class ModuleInfo : ModuleInfoBase
{
    public override string ModuleName => "github";
    public override string Description => "服务器本机 GitHub、GCM、SSH 和 origin 连接治理";
    public override string Author => "OneHistory";
    public override string Version => "1.0.1";
    public override Type? MainClassType => typeof(GitHubCommands);
}
