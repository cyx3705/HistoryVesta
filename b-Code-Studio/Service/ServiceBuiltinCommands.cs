using System.Diagnostics;
using System.IO;
using AppShell.Core.Commands;
using AppShell.Core.Storage;
using AppShell.Services;

namespace OneHistoryStudio.Service;

internal static class ServiceBuiltinCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        ISettingsService settings,
        string dataDirectory)
    {
        RegisterHelp(registry);
        RegisterApp(registry, settings, dataDirectory);
    }

    private static void RegisterHelp(CommandRegistry registry)
    {
        registry.Register(BuiltinCommandDefinitions.Bind(
            "help",
            CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.GetString("command");
                if (name == null)
                    return CommandResult.Ok("指令清单:" + string.Concat(registry.All().Select(item => $"\n  {item.Name,-24} {item.Summary}")));
                if (!registry.TryGet(name, out var descriptor))
                    return CommandResult.Fail($"未知指令: {name}");
                return CommandResult.Ok(
                    $"{descriptor.Name} - {descriptor.Summary}\n{CommandBus.FormatUsage(descriptor)}" +
                    (descriptor.Example == null ? "" : $"\n示例: {descriptor.Example}"));
            })));
    }

    private static void RegisterApp(
        CommandRegistry registry,
        ISettingsService settings,
        string dataDirectory)
    {
        registry.Register(BuiltinCommandDefinitions.Bind(
            "app.get",
            CommandDescriptor.Sync(ctx =>
            {
                var key = ctx.GetString("key");
                if (key != null)
                    return CommandResult.Ok($"{key}={settings.Get(key) ?? "(未配置)"}");
                return CommandResult.Ok("设置:" + string.Concat(settings.All().Select(item => $"\n  {item.Key} = {item.Value}")));
            })));
        registry.Register(BuiltinCommandDefinitions.Bind(
            "app.set",
            CommandDescriptor.Sync(ctx =>
            {
                settings.Set(ctx.RequireString("key"), ctx.RequireString("value"));
                return CommandResult.Ok("设置已保存");
            })));
        registry.Register(BuiltinCommandDefinitions.Bind(
            "app.opendata",
            CommandDescriptor.Sync(_ =>
            {
                Process.Start(new ProcessStartInfo(dataDirectory) { UseShellExecute = true });
                return CommandResult.Ok($"已打开 {dataDirectory}");
            })));
    }

}
