using System.Reflection;
using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Mcp;
using AppShell.Services;
using AppShell.ServiceHost;
using AppShell.Shell;

var expected = new Version(0, 7, 2, 0);
var assemblies = new[]
{
    typeof(CommandBus).Assembly,
    typeof(SettingsService).Assembly,
    typeof(ShellWindow).Assembly,
    typeof(ServiceComposition).Assembly,
};

foreach (var assembly in assemblies)
{
    var actual = assembly.GetName().Version;
    if (actual != expected)
        throw new InvalidOperationException($"{assembly.GetName().Name} version {actual}, expected {expected}");
}

var config = new ShellConfig
{
    AppName = "AppShellPackageSmoke",
    AppVersion = "0.7.2",
    EnableModules = false,
    EnableMcp = false,
};

if (config.AppVersion != "0.7.2" || config.EnableModules || config.EnableMcp)
    throw new InvalidOperationException("ShellConfig package surface is not usable");

AppIdentity.Use(Assembly.GetExecutingAssembly());
var registry = new CommandRegistry();
registry.Register(new CommandDescriptor
{
    Name = "smoke.ping",
    Summary = "Package smoke command",
    Readonly = true,
    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
});
var manual = CommandManualGenerator.Render(registry, new CommandSchemaExporter(registry), "readonly");
if (!manual.StartsWith("# AppShellPackageSmoke 命令手册", StringComparison.Ordinal)
    || manual.Contains("OneHistoryStudio 命令手册", StringComparison.Ordinal)
    || manual.Contains("b-Office/", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Command manual still leaks a derived application identity or path");
}

Console.WriteLine("PackageSmoke: PASS");
Console.WriteLine(string.Join(Environment.NewLine,
    assemblies.Select(assembly => $"{assembly.GetName().Name} {assembly.GetName().Version}")));
