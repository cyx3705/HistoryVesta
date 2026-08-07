using System.IO;
using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Modules;
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
var shellUi = new RecordingShellUiRegistrar();
using var host = new ModuleHost(moduleDirectory, log)
{
    EnableCommands = true,
    EnableUiModules = true,
    EnableFileWatching = false,
    // ModuleHost commits its snapshot through the host UI synchronization context.
    // The real AppShell supplies WPF's DispatcherSynchronizationContext; this
    // synchronous context keeps the smoke deterministic without creating WPF UI.
    UiContext = new ImmediateSynchronizationContext(),
    ShellUi = shellUi,
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

var expectedWindows = new[] { "overview", "tree", "meta", "projops", "history" };
var actualWindows = shellUi.Descriptors.Select(item => item.Id).ToArray();
if (!expectedWindows.SequenceEqual(actualWindows, StringComparer.Ordinal))
{
    throw new InvalidOperationException(
        $"unexpected module windows: [{string.Join(", ", actualWindows)}]");
}

if (shellUi.Descriptors.Any(item => item.Title.Equals("OneHistoryStudio", StringComparison.Ordinal)))
    throw new InvalidOperationException("placeholder main window is still registered");

var pageTypes = ConstructPages(shellUi.Descriptors);
var expectedPageTypes = new[]
{
    "OverviewView",
    "BranchTreeView",
    "MetaView",
    "ProjectOperationsView",
    "BranchHistoryView",
};
if (!expectedPageTypes.SequenceEqual(pageTypes, StringComparer.Ordinal))
    throw new InvalidOperationException($"unexpected page types: [{string.Join(", ", pageTypes)}]");

Console.WriteLine(
    $"PASS module={meta.ModuleName} version={meta.Version} commands={meta.CommandCount} "
    + $"source={registry.GetSource(descriptor.Name)} windows={string.Join(",", actualWindows)} "
    + $"pages={string.Join(",", pageTypes)}");

return 0;

static IReadOnlyList<string> ConstructPages(IReadOnlyList<ToolWindowDescriptor> descriptors)
{
    List<string>? pageTypes = null;
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            pageTypes = descriptors.Select(descriptor =>
            {
                var page = descriptor.ContentFactory?.Invoke()
                           ?? throw new InvalidOperationException(
                               $"window {descriptor.Id} has no content factory");
                return page.GetType().Name;
            }).ToList();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(15)))
        throw new TimeoutException("page construction did not complete within 15 seconds");
    if (failure != null)
        throw new InvalidOperationException("page construction failed", failure);
    return pageTypes ?? throw new InvalidOperationException("page construction produced no result");
}

sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    public override void Send(SendOrPostCallback callback, object? state) => callback(state);

    public override void Post(SendOrPostCallback callback, object? state) => callback(state);
}

sealed class RecordingShellUiRegistrar : IShellUiRegistrar
{
    public List<ToolWindowDescriptor> Descriptors { get; } = [];

    public bool IsUiThread => true;

    public void Invoke(Action action) => action();

    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Descriptors.Add(descriptor);
        return new Registration(() => Descriptors.Remove(descriptor));
    }

    public void UnregisterToolWindow(string id)
        => Descriptors.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void UnregisterOwner(string owner) => Descriptors.Clear();

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
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
