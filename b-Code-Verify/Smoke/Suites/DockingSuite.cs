using System.Windows.Controls;
using AppShell.Core.Docking;
using AppShell.Core.Storage;
using AppShell.Shell.Docking;
using AppShell.Core.Modules;
using AppShell.Services.Modules;
using AppShell.Services.Web;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Microsoft.Windows.Shell;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OneHistoryStudio.Connection;
using OneHistoryStudio.Views;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

internal static partial class DockingSuite
{
    public static Task RunAsync(string[] args)
    {
        var realMouse = args.Any(arg => arg.Equals("--real-mouse", StringComparison.OrdinalIgnoreCase));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Run the interactive fixture before model-only tests enqueue floating-window
                // callbacks on a manager that is never attached to a visual host.
                if (realMouse)
                    RunRealMouseRoundTrip();
                Run();
                RunDocumentPanePersistence();
                RunLiveDocumentPaneEmbedding();
                RunFloatDockRoundTrip();
                RunModuleReload();
                RunOhsNarrowViewMeasure();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (realMouse && !thread.Join(TimeSpan.FromSeconds(60)))
        {
            RealMouseInput.ReleaseLeftButton();
            throw new TimeoutException("Docking real-mouse smoke exceeded 60 seconds");
        }
        if (!realMouse)
            thread.Join();
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return Task.CompletedTask;
    }

    private static void RunOhsNarrowViewMeasure()
    {
        using var client = new ShellServiceClient(
            new Uri("http://127.0.0.1:65534/"), "NarrowLayoutSmoke");
        var root = Path.Combine(Path.GetTempPath(), "ohs-narrow-layout-" + Guid.NewGuid().ToString("N"));
        var profiles = new ConnectionProfileService(
            new BootstrapProfileStore("NarrowLayoutSmoke", root),
            new DpapiSecretStore(root),
            client);

        AssertNarrowView(
            new ConnectionSettingsView(profiles, static () => null),
            "connection settings");
        AssertNarrowView(
            new GitHubAccountView(static () => null),
            "GitHub account");
    }

    private static void AssertNarrowView(FrameworkElement view, string name)
    {
        var available = new Size(295, 500);
        Equal(0d, view.MinWidth, $"{name}: no host-forcing minimum width");
        Equal(0d, view.MinHeight, $"{name}: no host-forcing minimum height");
        view.Measure(available);
        True(view.DesiredSize.Width <= available.Width,
            $"{name}: desired width fits a restored-window right pane");
        view.Arrange(new Rect(available));
        Equal(available.Width, view.ActualWidth,
            $"{name}: arranged width follows the docking host");
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
        True(manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .All(item => item.CanDockAsTabbedDocument),
            "static and dynamically registered tools allow central page embedding");

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

    private static void RunDocumentPanePersistence()
    {
        var store = new MemoryLayoutStore();
        var firstManager = new DockingManager();
        var firstHost = new DockingHost(
            firstManager,
            RecoveryTools(new Counter()),
            store,
            new MemoryLog());
        firstHost.Initialize();

        True(
            firstManager.Layout.Descendents().OfType<LayoutAnchorable>()
                .All(item => item.CanDockAsTabbedDocument),
            "tool windows allow document-pane docking");

        var documentPane = firstManager.Layout.Descendents().OfType<LayoutDocumentPane>().Single();
        MoveIntoDocumentPane(firstManager, documentPane, "tool-main");
        MoveIntoDocumentPane(firstManager, documentPane, "tool-tab");
        Equal(2, documentPane.ChildrenCount, "fixture embeds tools in central document pane");
        store.WriteCurrent(Serialize(firstManager));

        var recoveryLog = new MemoryLog();
        var recoveredManager = new DockingManager();
        var recoveredHost = new DockingHost(
            recoveredManager,
            RecoveryTools(new Counter()),
            store,
            recoveryLog);
        recoveredHost.Initialize();

        var recoveredBackground = recoveredManager.Layout.Descendents().OfType<LayoutDocumentPane>().Single();
        Equal(2, recoveredBackground.ChildrenCount, "embedded central pages survive layout restore");
        True(
            recoveredManager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Where(item => item.ContentId is "tool-main" or "tool-tab")
                .All(item => item.CanDockAsTabbedDocument && IsInsideDocumentPane(item)),
            "restored tools remain embedded selectable pages");
        True(!recoveryLog.Snapshot().Any(entry => entry.Message.Contains("误入中央主文档区", StringComparison.Ordinal)),
            "valid central embedding emits no recovery warning");

        recoveredHost.SaveCurrentLayout();
        Equal(2, recoveredBackground.ChildrenCount,
            "save boundary preserves embedded pages");

        var persistedManager = new DockingManager();
        var persistedHost = new DockingHost(
            persistedManager,
            RecoveryTools(new Counter()),
            store,
            new MemoryLog());
        persistedHost.Initialize();
        Equal(2, persistedManager.Layout.Descendents().OfType<LayoutDocumentPane>()
            .Single().ChildrenCount,
            "embedded pages remain after a subsequent save and restart");
        True(persistedHost.ListWindows().Where(item => item.Id is "tool-main" or "tool-tab")
                .All(item => item.Side == DockSide.Center),
            "persisted embedded pages report the central side");
    }

    private static void RunLiveDocumentPaneEmbedding()
    {
        var manager = new DockingManager();
        var log = new MemoryLog();
        var host = new DockingHost(
            manager,
            RecoveryTools(new Counter()),
            new MemoryLayoutStore(),
            log);
        host.Initialize();
        PumpDispatcher(TimeSpan.FromMilliseconds(100));

        // Start from a user-customized layout, then model the result produced by an AvalonDock
        // drag into the main document pane.
        host.Dock("tool-main", DockSide.Left, 0.31);
        host.Dock("tool-tab", DockSide.Tab, targetId: "tool-main");
        host.Float("tool-right");
        PumpDispatcher(TimeSpan.FromMilliseconds(100));
        var preGestureStates = host.ListWindows();
        Equal(DockSide.Left, preGestureStates.Single(item => item.Id == "tool-main").Side,
            "custom fixture starts on the left");
        True(preGestureStates.Single(item => item.Id == "tool-right").IsFloating,
            "custom fixture starts with the right tool floating");

        var generated = new List<string>();
        host.CommandGenerated += (_, e) => generated.Add(e.CommandText);
        var documentPane = manager.Layout.Descendents().OfType<LayoutDocumentPane>().Single();
        MoveIntoDocumentPane(manager, documentPane, "tool-main");
        MoveIntoDocumentPane(manager, documentPane, "tool-tab");
        MoveIntoDocumentPane(manager, documentPane, "tool-right");

        PumpDispatcher(TimeSpan.FromMilliseconds(750));

        Equal(3, documentPane.ChildrenCount,
            "live drag embeds all selected tools in the central document pane");
        var main = manager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Single(item => item.ContentId == "tool-main");
        var tab = manager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Single(item => item.ContentId == "tool-tab");
        True(ReferenceEquals(main.Parent, tab.Parent) && ReferenceEquals(main.Parent, documentPane),
            "live drag joins the tools to one central page group");
        var restoredStates = host.ListWindows();
        True(restoredStates.Where(item => item.Id is "tool-main" or "tool-tab" or "tool-right")
                .All(item => !item.IsFloating && item.Side == DockSide.Center),
            "live embedded tools report the central side");
        True(manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .All(item => item.CanDockAsTabbedDocument && IsInsideDocumentPane(item)),
            "live embedding keeps pages draggable back out");
        True(!log.Snapshot().Any(entry => entry.Message.Contains("误入中央主文档区", StringComparison.Ordinal)),
            "live central embedding emits no recovery warning");
        True(generated.Count(command => command.EndsWith(" pos=center", StringComparison.Ordinal)) >= 3,
            "live embedding records replayable central docking commands");
    }

    private static ToolWindowDescriptor[] RecoveryTools(Counter counter) =>
    [
        Tool("tool-main", DockSide.Top, counter),
        new ToolWindowDescriptor
        {
            Id = "tool-tab",
            Title = "tool-tab",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "tool-main",
            ContentFactory = () => counter.Create(),
        },
        Tool("tool-right", DockSide.Right, counter),
    ];

    private static void RunFloatDockRoundTrip()
    {
        var store = new MemoryLayoutStore();
        var firstManager = new DockingManager();
        var firstHost = new DockingHost(
            firstManager,
            [Tool("roundtrip", DockSide.Bottom, new Counter())],
            store,
            new MemoryLog());
        firstHost.Initialize();

        firstHost.Float("roundtrip");
        True(firstHost.ListWindows().Single(item => item.Id == "roundtrip").IsFloating,
            "float transition marks the tool as floating");

        firstHost.Dock("roundtrip", DockSide.Right, 0.31);
        var docked = firstHost.ListWindows().Single(item => item.Id == "roundtrip");
        True(!docked.IsFloating && docked.Side == DockSide.Right,
            "dock transition returns the tool to a side pane");
        firstHost.SaveCurrentLayout();

        var recoveredManager = new DockingManager();
        var recoveredHost = new DockingHost(
            recoveredManager,
            [Tool("roundtrip", DockSide.Bottom, new Counter())],
            store,
            new MemoryLog());
        recoveredHost.Initialize();

        var recovered = recoveredHost.ListWindows().Single(item => item.Id == "roundtrip");
        True(!recovered.IsFloating && recovered.Side == DockSide.Right,
            "float-dock roundtrip survives layout reload");
        Equal(0, recoveredManager.Layout.Descendents().OfType<LayoutDocumentPane>()
            .Single().ChildrenCount,
            "float-dock roundtrip leaves central document pane empty");
    }

    private static void MoveIntoDocumentPane(
        DockingManager manager,
        LayoutDocumentPane documentPane,
        string id)
    {
        var anchorable = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                             .SingleOrDefault(item => item.ContentId == id)
                         ?? documentPane.Children.OfType<LayoutAnchorable>()
                             .Single(item => item.ContentId == id);
        if (ReferenceEquals(anchorable.Parent, documentPane))
            return;
        ((ILayoutContainer)anchorable.Parent!).RemoveChild(anchorable);
        documentPane.Children.Add(anchorable);
        manager.Layout.CollectGarbage();
    }

    private static bool IsInsideDocumentPane(LayoutAnchorable anchorable)
    {
        for (ILayoutContainer? parent = anchorable.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is LayoutDocumentPane)
                return true;
        }

        return false;
    }

    private static string Serialize(DockingManager manager)
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(manager).Serialize(writer);
        return writer.ToString();
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        // Stop below ApplicationIdle so production RebaseSoon callbacks are allowed to run;
        // a Background-priority stop would end the nested frame before the real idle queue.
        var timer = new DispatcherTimer(DispatcherPriority.SystemIdle)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

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
