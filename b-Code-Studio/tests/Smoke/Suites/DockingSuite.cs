using System.Windows.Controls;
using AppShell.Core.Docking;
using AppShell.Core.Storage;
using AppShell.Shell.Docking;
using AppShell.Core.Modules;
using AppShell.Services.Modules;
using AvalonDock;
using AvalonDock.Layout;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

internal static class DockingSuite
{
    public static Task RunAsync(string[] args)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run();
                RunModuleReload();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw failure;
        Console.WriteLine("DockingSmoke: PASS");
        return Task.CompletedTask;
    }

    private static void Run()
    {
        var store = new MemoryLayoutStore();
        var log = new MemoryLog();
        var manager = new DockingManager();
        var host = new DockingHost(
            manager,
            [new ToolWindowDescriptor
            {
                Id = "console",
                Title = "Console",
                DefaultSide = DockSide.Bottom,
                ContentFactory = static () => new Border(),
            }],
            store,
            log,
            new MemorySettings());
        host.Initialize();

        var generated = 0;
        host.CommandGenerated += (_, _) => generated++;
        var counter = new Counter();
        host.RegisterWindow(new ToolWindowDescriptor
        {
            Id = "tool-a",
            Title = "Tool A",
            DefaultSide = DockSide.Right,
            ContentFactory = () => counter.Create(),
        }, "tools");
        host.RegisterWindow(Tool("tool-b", DockSide.Left, counter), "tools");
        host.RegisterWindow(Tool("tool-c", DockSide.Top, counter), "tools");

        Equal(3, counter.Created, "three tool contents created");
        Equal("tools", host.ListWindows().Single(item => item.Id == "tool-a").Owner, "dynamic owner");
        Throws<InvalidOperationException>(() => host.RegisterWindow(Tool("tool-a", DockSide.Bottom, counter), "tools"),
            "duplicate tool id collision");
        True(!manager.Layout.Descendents().OfType<LayoutDocument>().Any(),
            "default layout has no work-page documents");
        var background = manager.Layout.Descendents().OfType<LayoutDocumentPane>().Single();
        Equal(0, background.ChildrenCount, "central background has no tab or content");
        True(manager.Layout.Descendents().OfType<LayoutAnchorable>().All(item => item.CanFloat),
            "all tool windows can float");

        host.SaveLayout("three-tools");
        host.MaximizeWindow("tool-b");
        Equal("tool-b", host.MaximizedId, "tool maximized");
        Equal(4, host.ListWindows().Count, "maximize keeps tool registry");
        host.RestoreLayoutFromMaximized();
        Equal<string?>(null, host.MaximizedId, "max restored");
        Equal(4, host.ListWindows().Count, "restore keeps all tools");
        Equal(3, counter.Created, "restore reuses tool content");
        Equal(0, generated, "maximize restore emits no layout commands");

        True(host.LoadLayout("three-tools"), "load named layout");
        True(!manager.Layout.Descendents().OfType<LayoutDocument>().Any(),
            "loaded layout still has no documents");
        Equal(3, counter.Created, "layout load reuses tool content");

        host.MaximizeWindow("tool-a");
        host.UnregisterWindow("tool-a");
        Equal<string?>(null, host.MaximizedId, "unregister maximized target restores first");
        Equal(1, counter.Disposed, "unregistered tool content disposed once");

        host.UnregisterOwner("tools");
        Equal(3, counter.Disposed, "owner cleanup disposes remaining tools");
        Equal(1, host.ListWindows().Count, "owner cleanup keeps framework tools only");
    }

    private static ToolWindowDescriptor Tool(string id, DockSide side, Counter counter) => new()
    {
        Id = id,
        Title = id,
        DefaultSide = side,
        ContentFactory = () => counter.Create(),
    };

    private static void RunModuleReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "appshell-bad-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = typeof(Fixtures.BadUiModule).Assembly.Location;
            File.Copy(fixture, Path.Combine(root, "Smoke.dll"));
            File.WriteAllText(Path.Combine(root, "module.manifest.json"), "{\"ui\":true}");

            var registrar = new FakeRegistrar();
            var host = new ModuleHost(root, new MemoryLog())
            {
                UiContext = new ImmediateSynchronizationContext(),
                ShellUi = registrar,
                EnableCommands = false,
                EnableUiModules = true,
            };
            host.Start();
            Equal(1, registrar.ToolCount, "bad module initial UI");

            var oldContexts = new List<WeakReference>();
            for (var i = 0; i < 10; i++)
            {
                oldContexts.Add(CurrentContext(host));
                host.Reload();
                Equal(1, registrar.ToolCount, $"bad module reload {i + 1} has no duplicate UI");
            }
            True(registrar.OwnerCleanupCount >= 10, "owner cleanup called for every reload");
            ForceCollection();
            True(oldContexts.All(reference => !reference.IsAlive), "old collectible ALCs released");

            host.Dispose();
            Equal(0, registrar.ToolCount, "bad module dispose cleanup");
            File.Delete(Path.Combine(root, "Smoke.dll"));
            True(!File.Exists(Path.Combine(root, "Smoke.dll")), "module DLL remains deletable");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CurrentContext(ModuleHost host)
    {
        var snapshot = typeof(ModuleHost)
            .GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;
        var contexts = (IEnumerable<AssemblyLoadContext>)snapshot.GetType()
            .GetProperty("Contexts")!
            .GetValue(snapshot)!;
        return new WeakReference(contexts.Single());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class Counter
    {
        public int Created { get; private set; }
        public int Disposed { get; private set; }

        public DisposableContent Create()
        {
            Created++;
            return new DisposableContent(() => Disposed++);
        }
    }

    private sealed class DisposableContent(Action? onDispose = null) : Border, IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose()
        {
            Disposed++;
            onDispose?.Invoke();
        }
    }

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
        private string? _current;

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Send(SendOrPostCallback callback, object? state) => callback(state);
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeRegistrar : IShellUiRegistrar
    {
        private readonly Dictionary<string, string> _tools = new(StringComparer.OrdinalIgnoreCase);

        public bool IsUiThread => true;
        public int ToolCount => _tools.Count;
        public int OwnerCleanupCount { get; private set; }
        public void Invoke(Action action) => action();

        public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
        {
            _tools.Add(descriptor.Id, owner);
            return NoopDisposable.Instance;
        }

        public void UnregisterToolWindow(string id) => _tools.Remove(id);

        public void UnregisterOwner(string owner)
        {
            OwnerCleanupCount++;
            foreach (var id in _tools.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToArray())
                _tools.Remove(id);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
