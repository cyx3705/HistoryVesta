using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Services.Modules;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ModuleSmoke <module-directory>");
    return 2;
}

var moduleDirectory = Path.GetFullPath(args[0]);
if (!Directory.Exists(moduleDirectory))
{
    Console.Error.WriteLine($"module directory not found: {moduleDirectory}");
    return 2;
}

AppIdentity.Use(typeof(Program).Assembly);
var log = new MemoryLog();
var registry = new CommandRegistry();
var bus = new CommandBus(registry, log);
using var host = new ModuleHost(moduleDirectory, log)
{
    EnableCommands = true,
    EnableUiModules = true,
    EnableFileWatching = false,
    // ModuleHost commits its snapshot through the host UI synchronization context.
    // The real AppShell supplies WPF's DispatcherSynchronizationContext; this
    // synchronous context keeps the smoke deterministic without creating WPF UI.
    UiContext = new ImmediateSynchronizationContext(),
};

host.Attach(registry);
host.Start();

if (host.Modules.Count != 1)
{
    foreach (var entry in log.Snapshot())
        Console.Error.WriteLine($"[{entry.Level}] [{entry.Category}] {entry.Message}");
    throw new InvalidOperationException($"expected one module, got {host.Modules.Count}");
}

var meta = host.Modules[0];
if (!meta.ModuleName.Equals("OneHistoryStudio", StringComparison.Ordinal)
    || !meta.Version.Equals("3.0.0-preview.1", StringComparison.Ordinal)
    || !meta.Ui
    || meta.CommandCount != 1)
{
    throw new InvalidOperationException(
        $"unexpected module metadata: {meta.ModuleName} {meta.Version} ui={meta.Ui} commands={meta.CommandCount}");
}

if (!registry.TryGet("OneHistoryStudio.Status", out var descriptor)
    || !descriptor.Readonly
    || !registry.GetSource("OneHistoryStudio.Status")
        .Equals("module:OneHistoryStudio", StringComparison.Ordinal))
{
    throw new InvalidOperationException("module command contract is not projected correctly");
}

var result = await bus.ExecuteAsync("OneHistoryStudio.Status", "ModuleSmoke");
if (!result.Success || !result.Message.Contains("3.0.0-preview.1", StringComparison.Ordinal))
    throw new InvalidOperationException($"module command failed: {result.Message}");

Console.WriteLine($"PASS module={meta.ModuleName} version={meta.Version} commands={meta.CommandCount} source={registry.GetSource(descriptor.Name)}");

return 0;

sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    public override void Send(SendOrPostCallback callback, object? state) => callback(state);

    public override void Post(SendOrPostCallback callback, object? state) => callback(state);
}

sealed class MemoryLog : IShellLog
{
    private readonly List<ShellLogEntry> _entries = [];

    public event EventHandler<ShellLogEntry>? EntryAdded;

    public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;

    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(DateTime.Now, level, category, message);
        _entries.Add(entry);
        EntryAdded?.Invoke(this, entry);
    }
}
