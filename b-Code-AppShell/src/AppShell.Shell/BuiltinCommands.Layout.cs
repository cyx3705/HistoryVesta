using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using AppShell.Services;
using AppShell.Shell.Console;
using AppShell.Shell.Docking;
using AppShell.Shell.Panels;

namespace AppShell.Shell;

public static partial class BuiltinCommands
{
    private static void RegisterLayout(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "layout.save",
            Summary = "把当前布局保存为命名方案",
            Example = "layout.save name=调试布局",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "方案名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                s.Docking.SaveLayout(name);
                return CommandResult.Ok($"布局方案 [{name}] 已保存");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "layout.load",
            Summary = "加载命名布局方案",
            Example = "layout.load name=调试布局",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "方案名(layout.list 可查)", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                return s.Docking.LoadLayout(name)
                    ? CommandResult.Ok($"布局方案 [{name}] 已加载")
                    : CommandResult.Fail($"布局方案 [{name}] 不存在或加载失败");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "layout.list",
            Summary = "列出全部命名布局方案",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var names = s.Docking.ListLayouts();
                return names.Count == 0
                    ? CommandResult.Ok("(暂无命名布局方案,layout.save name=xxx 可保存)")
                    : CommandResult.Ok($"共 {names.Count} 个方案:" + string.Concat(names.Select(n => $"\n  {n}")));
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "layout.reset",
            Summary = "重置为默认布局",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Docking.ResetLayout();
                return CommandResult.Ok("已重置为默认布局");
            }),
        });
    }

    private static void RegisterFrontend(CommandRegistry registry, CommandDescriptor descriptor)
        => registry.Register(descriptor, FrontendCommandCatalog.Source);
    // ---------------------------------------------------------------- log.*

}

