using System.Diagnostics;
using System.Windows;
using AppShell.Core.Commands;

namespace AppShell.ServiceHost;

public static class ServiceCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        ServiceComposition composition,
        Action requestStop,
        string executablePath,
        string source = "framework:service")
    {
        registry.Register(new CommandDescriptor
        {
            Name = "svc.status",
            Summary = "查看服务进程状态",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("服务运行中", new
            {
                running = true,
                processId = Environment.ProcessId,
                mcp = composition.Mcp?.IsRunning ?? false,
                web = composition.Web?.IsRunning ?? false,
                modules = composition.Modules?.Modules.Count ?? 0,
            })),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "svc.stop",
            Summary = "停止服务进程",
            ConfirmPrompt = _ => "确认停止后台服务？前端和远程客户端会断开。",
            Handler = CommandDescriptor.Sync(_ =>
            {
                Application.Current.Dispatcher.BeginInvoke(requestStop);
                return CommandResult.Ok("服务正在停止");
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "svc.restart",
            Summary = "重启服务进程",
            ConfirmPrompt = _ => "确认重启后台服务？客户端会短暂断开。",
            Handler = CommandDescriptor.Sync(_ =>
            {
                Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
                Application.Current.Dispatcher.BeginInvoke(requestStop);
                return CommandResult.Ok("服务正在重启");
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "svc.autostart",
            Summary = "查看或设置用户级登录启动",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "登录启动开关",
                    Position = 0,
                    AllowedValues = ["on", "off"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var manager = composition.Autostart;
                if (manager == null)
                    return CommandResult.Fail("当前宿主未配置自启动管理器");
                var mode = ctx.GetString("mode");
                if (mode == null)
                    return CommandResult.Ok(manager.IsEnabled(composition.ServiceName)
                        ? "服务登录启动已开启"
                        : "服务登录启动已关闭");

                manager.SetEnabled(
                    composition.ServiceName,
                    executablePath,
                    mode.Equals("on", StringComparison.OrdinalIgnoreCase));
                return CommandResult.Ok($"服务登录启动已{(mode == "on" ? "开启" : "关闭")}");
            }),
        }, source);
    }
}
