using AppShell.Services.Modules;
using System.Diagnostics;
using System.IO;
using System.Text;
using AppShell.Core.Commands;
using AppShell.Core.Storage;

namespace AppShell.Shell.Modules;

/// <summary>module.* 管理指令(MD-05):list / reload / dir / open。</summary>
public static class ModuleCommands
{
    public const string KeyModuleDir = "module.dir";

    public static void RegisterAll(
        CommandRegistry registry, ModuleHost host, ISettingsService settings, string source = "app")
    {
        registry.Register(new CommandDescriptor
        {
            Name = "module.list",
            Summary = "列出已加载模块(名称/版本/描述/指令数)",
            Readonly = true,
            Example = "module.list",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var modules = host.Modules;
                if (modules.Count == 0)
                    return CommandResult.Ok($"当前无已加载模块。把模块 DLL 放入 {host.ModulesDirectory} 即自动装载");

                var sb = new StringBuilder();
                sb.Append($"已加载 {modules.Count} 个模块(目录 {host.ModulesDirectory}):");
                foreach (var m in modules)
                {
                    sb.Append($"\n  {m.ModuleName} {m.Version}  [{(m.Open ? "全暴露" : "精准暴露")}]" +
                              $"  {m.CommandCount} 条指令  ← {(m.Slot.Length > 0 ? m.Slot + "/" : "")}{m.AssemblyFile}" +
                              $"{(m.Slot.Length > 0 ? "(槽)" : "(根)")}");
                    if (m.Description.Length > 0)
                        sb.Append($"\n      {m.Description}");
                }

                return CommandResult.Ok(sb.ToString(), modules);
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "module.reload",
            Summary = "手动整体重载全部模块(文件变化会自动热重载,通常无需手动)",
            Example = "module.reload",
            Handler = async _ =>
            {
                await Task.Run(host.Reload);
                return CommandResult.Ok(
                    $"重载完成: {host.Modules.Count} 个模块,{host.Modules.Sum(m => m.CommandCount)} 条指令");
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "module.dir",
            Summary = "查看/切换模块目录(切换后立即重载并持久化)",
            Example = "module.dir path=D:\\MyModules",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "path",
                    Description = "新模块目录(绝对路径);省略则只显示当前目录",
                    Position = 0,
                },
            ],
            Handler = async ctx =>
            {
                var path = ctx.GetString("path");
                if (string.IsNullOrWhiteSpace(path))
                    return CommandResult.Ok($"当前模块目录: {host.ModulesDirectory}");

                path = Path.GetFullPath(path.Trim());
                settings.Set(KeyModuleDir, path);
                await Task.Run(() => host.ChangeDirectory(path));
                return CommandResult.Ok(
                    $"模块目录已切换并重载: {path}({host.Modules.Count} 个模块)");
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "module.open",
            Summary = "在系统资源管理器中打开模块目录(UI-12 面板按钮落点)",
            Example = "module.open",
            Handler = CommandDescriptor.Sync(_ =>
            {
                Directory.CreateDirectory(host.ModulesDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{host.ModulesDirectory}\"")
                {
                    UseShellExecute = true,
                });
                return CommandResult.Ok($"已打开模块目录: {host.ModulesDirectory}");
            }),
        }, source);
    }
}
