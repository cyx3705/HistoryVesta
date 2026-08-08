using System.Diagnostics;
using System.Windows;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.ServiceHost;

public static class ServiceCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        ServiceComposition composition,
        Action requestStop,
        string executablePath,
        string source = "framework:service",
        IReadOnlyList<string>? serviceArguments = null)
    {
        serviceArguments ??= [];
        registry.Register(new CommandDescriptor
        {
            Name = "svc.status",
            Domain = "HistoryVulcan",
            CommandClass = "svc",
            Summary = "查看服务进程状态",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("服务运行中", new
            {
                running = true,
                processId = Environment.ProcessId,
                mcp = composition.Mcp?.IsRunning ?? false,
                web = composition.Web?.IsRunning ?? false,
                modules = composition.Modules?.Modules.Count ?? 0,
                shortcuts = composition.GlobalShortcuts?.Registrations.Count ?? 0,
                shortcutsEnabled = composition.GlobalShortcuts?.IsEnabled ?? false,
            })),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "svc.stop",
            Domain = "HistoryVulcan",
            CommandClass = "svc",
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
            Name = "app.exit",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "退出 HistoryVulcan 前端与后台服务",
            ConfirmPrompt = _ => "确认退出 HistoryVulcan 前端和后台服务？",
            Handler = async ctx =>
            {
                var frontend = composition.Web?.ConnectedShells > 0
                    ? await composition.Web.RelayFrontendCommandAsync(
                        "app.frontend.exit", ctx.Source, ctx.Cancellation).ConfigureAwait(false)
                    : CommandResult.Ok("前端未连接");
                _ = Application.Current.Dispatcher.BeginInvoke(requestStop);
                return frontend.Success
                    ? CommandResult.Ok("HistoryVulcan 正在退出")
                    : CommandResult.Ok($"后台正在退出，前端回执: {frontend.Message}");
            },
        }, source);

        RegisterFrontendLifecycle(registry, composition, executablePath, source);

        registry.Register(new CommandDescriptor
        {
            Name = "shortcut.list",
            Domain = "HistoryVulcan",
            CommandClass = "shortcut",
            Summary = "列出已注册的全局快捷键",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var shortcuts = composition.GlobalShortcuts?.Registrations ?? [];
                return CommandResult.Ok(
                    shortcuts.Count == 0
                        ? "当前没有全局快捷键"
                        : string.Join('\n', shortcuts.Select(item =>
                            $"{item.Owner}/{item.Id}: {Format(item)} -> {item.CommandText}")),
                    shortcuts);
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "svc.restart",
            Domain = "HistoryVulcan",
            CommandClass = "svc",
            Summary = "重启服务进程",
            ConfirmPrompt = _ => "确认重启后台服务？客户端会短暂断开。",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var start = new ProcessStartInfo(executablePath) { UseShellExecute = true };
                foreach (var argument in serviceArguments)
                    start.ArgumentList.Add(argument);
                Process.Start(start);
                Application.Current.Dispatcher.BeginInvoke(requestStop);
                return CommandResult.Ok("服务正在重启");
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "svc.autostart",
            Domain = "HistoryVulcan",
            CommandClass = "svc",
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
                    serviceArguments,
                    mode.Equals("on", StringComparison.OrdinalIgnoreCase));
                return CommandResult.Ok($"服务登录启动已{(mode == "on" ? "开启" : "关闭")}");
            }),
        }, source);
    }

    private static string Format(HistoryVulcan.Core.Input.GlobalShortcutRegistrationInfo info)
        => string.Join(" ", info.Strokes.Select(stroke =>
            $"{stroke.Modifiers}+VK_{stroke.VirtualKey:X2}"));

    private static void RegisterFrontendLifecycle(
        CommandRegistry registry,
        ServiceComposition composition,
        string executablePath,
        string source)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.show",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "显示并激活前端窗口",
            Handler = ctx => RelayOrStartAsync(composition, executablePath, "app.frontend.show", "--show", ctx),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.focus-console",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "唤出并聚焦前端控制台",
            Handler = ctx => RelayOrStartAsync(
                composition, executablePath, "app.frontend.focus-console", "--focus-console", ctx),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.hide",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "隐藏前端窗口并保持后台运行",
            Handler = ctx => RelayOrStartAsync(composition, executablePath, "app.frontend.hide", null, ctx),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.exit",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "退出前端进程",
            Handler = async ctx =>
            {
                var web = composition.Web;
                if (web == null || web.ConnectedShells <= 0)
                    return CommandResult.Ok("前端未连接");
                return await web.RelayFrontendCommandAsync(
                    "app.frontend.exit", ctx.Source, ctx.Cancellation).ConfigureAwait(false);
            },
        }, source);
    }

    private static async Task<CommandResult> RelayOrStartAsync(
        ServiceComposition composition,
        string executablePath,
        string command,
        string? startupArgument,
        CommandContext context)
    {
        if (composition.Web?.ConnectedShells > 0)
            return await composition.Web.RelayFrontendCommandAsync(
                command, context.Source, context.Cancellation).ConfigureAwait(false);

        if (startupArgument == null)
            return CommandResult.Fail("前端未连接");

        try
        {
            Process.Start(new ProcessStartInfo(executablePath, startupArgument)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return CommandResult.Ok("前端正在启动");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.Fail($"前端启动失败: {ex.Message}");
        }
    }
}
