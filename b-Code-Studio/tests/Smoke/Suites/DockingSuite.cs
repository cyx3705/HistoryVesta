using System.Windows.Controls;
using AppShell.Core.Docking;
using AppShell.Core.Storage;
using AppShell.Shell.Docking;
using AppShell.Core.Modules;
using AppShell.Services.Modules;
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
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

internal static class DockingSuite
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
        if (realMouse)
            Console.WriteLine("DockingRealMouseSmoke: PASS");
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

    private static void RunRealMouseRoundTrip()
    {
        True(Environment.UserInteractive, "real mouse smoke requires an interactive Windows desktop");

        var originalCursor = RealMouseInput.CursorPosition;
        var store = new MemoryLayoutStore();
        var manager = new DockingManager();
        var dragContent = ToolContent("Drag tool", Brushes.LightSteelBlue);
        var dragCompanionContent = ToolContent("Drag companion", Brushes.Lavender);
        var targetContent = ToolContent("Dock target", Brushes.Honeydew);
        var sentinelContent = ToolContent("Layout sentinel", Brushes.LemonChiffon);
        var host = new DockingHost(
            manager,
            RealMouseTools(dragContent, dragCompanionContent, targetContent, sentinelContent),
            store,
            new MemoryLog());
        host.Initialize();
        host.Dock("sentinel-tool", DockSide.Left, 0.21);
        host.Dock("target-tool", DockSide.Right, 0.27);
        host.Dock("drag-tool", DockSide.Bottom, 0.24);
        host.Dock("drag-companion", DockSide.Tab, targetId: "drag-tool");

        var commands = new List<string>();
        host.CommandGenerated += (_, eventArgs) => commands.Add(eventArgs.CommandText);

        var work = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = "OneHistoryStudio Docking Real-Mouse Smoke",
            Width = Math.Max(620, Math.Min(1000, work.Width * 0.66)),
            Height = Math.Max(480, Math.Min(720, work.Height * 0.76)),
            Left = work.Left + 24,
            Top = work.Top + 24,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Content = manager,
        };

        try
        {
            window.Show();
            window.Activate();
            manager.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(600));

            var dragModel = FindAnchorable(manager, "drag-tool");
            var targetModel = FindAnchorable(manager, "target-tool");
            var documentModel = manager.Layout.Descendents().OfType<LayoutDocumentPane>().Single();
            var dragTab = FindVisual<LayoutAnchorableTabItem>(manager,
                item => ReferenceEquals(item.Model, dragModel));
            var documentPane = FindVisual<LayoutDocumentPaneControl>(manager,
                item => ReferenceEquals(item.Model, documentModel));

            var tabPoint = ScreenCenter(dragTab);
            var documentPoint = ScreenCenter(documentPane);
            var outsidePoint = OutsideWindowPoint(window);
            AcquireForegroundInput(window, documentPoint);

            PerformRealMouseDrag(tabPoint, outsidePoint, "drag tool out of the main window");
            var floated = host.ListWindows().Single(item => item.Id == "drag-tool");
            True(floated.IsFloating, "real tab drag creates an AvalonDock floating tool window");
            True(commands.Contains("win.float name=drag-tool", StringComparer.Ordinal),
                "real tab drag emits the canonical float command");

            var floatingWindow = FindFloatingWindow(manager);
            var floatingCaption = FloatingCaptionPoint(floatingWindow);
            PerformRealMouseDrag(floatingCaption, documentPoint,
                "embed floating tool in the central document pane");
            var embedded = host.ListWindows().Single(item => item.Id == "drag-tool");
            True(!embedded.IsFloating && embedded.Side == DockSide.Center,
                "central document pane accepts a real tool-window drop");
            Equal(1, documentModel.ChildrenCount,
                "central document pane contains the embedded tool page");
            True(IsInsideDocumentPane(FindAnchorable(manager, "drag-tool")),
                "real central drop keeps the page in the main document pane");
            True(commands.Any(command => command == "win.dock name=drag-tool pos=center"),
                "real central drop emits a replayable center docking command");

            var embeddedTab = FindVisual<LayoutDocumentTabItem>(manager,
                item => ReferenceEquals(item.Model, FindAnchorable(manager, "drag-tool")));
            PerformRealMouseDrag(ScreenCenter(embeddedTab), outsidePoint,
                "drag embedded central page back out as a floating tool");
            True(host.ListWindows().Single(item => item.Id == "drag-tool").IsFloating,
                "embedded central page can be dragged back out");

            ToolWindowInfo docked = null!;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                // The central-tab tear-off replaces the native floating window and can also
                // re-template the target pane. Always reacquire both live visuals immediately
                // before the physical gesture instead of reusing pre-transition coordinates.
                PumpDispatcher(TimeSpan.FromMilliseconds(350));
                floatingWindow = FindFloatingWindow(manager);
                floatingCaption = FloatingCaptionPoint(floatingWindow);
                targetModel = FindAnchorable(manager, "target-tool");
                var targetTitle = FindVisual<AnchorablePaneTitle>(manager,
                    item => ReferenceEquals(item.Model, targetModel));
                var targetPoint = ScreenCenter(targetTitle);
                PerformRealMouseDrag(floatingCaption, targetPoint,
                    $"dock floating tool into the right-side tool pane (attempt {attempt})");

                docked = host.ListWindows().Single(item => item.Id == "drag-tool");
                if (!docked.IsFloating && docked.Side == DockSide.Right)
                    break;
            }

            if (docked.IsFloating || docked.Side != DockSide.Right)
            {
                throw new InvalidOperationException(
                    "FAIL: real mouse drag returns the tool to the main window right side; " +
                    $"floating={docked.IsFloating}, side={docked.Side}, " +
                    $"commands=[{string.Join(" | ", commands)}]");
            }
            True(ReferenceEquals(FindAnchorable(manager, "drag-tool").Parent, targetModel.Parent),
                "real mouse drop joins the intended tool tab group");
            Equal(DockSide.Left,
                host.ListWindows().Single(item => item.Id == "sentinel-tool").Side,
                "real mouse roundtrip preserves the unrelated custom left layout");
            Equal(0, documentModel.ChildrenCount,
                "real mouse roundtrip leaves the central document pane empty");
            True(manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .All(item => item.CanDockAsTabbedDocument && !IsInsideDocumentPane(item)),
                "real mouse roundtrip keeps every tool eligible for central embedding");
            var dragDockCommands = commands.Where(command =>
                    command.StartsWith("win.dock name=drag-tool ", StringComparison.Ordinal))
                .ToList();
            if (!dragDockCommands.Any(command =>
                    command.StartsWith("win.dock name=drag-tool pos=tab target=target-tool", StringComparison.Ordinal) ||
                    command.StartsWith("win.dock name=drag-tool pos=right", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "FAIL: real mouse return emits a canonical replayable docking command; " +
                    $"commands=[{string.Join(" | ", commands)}]");
            }

            host.SaveCurrentLayout();
            var recoveredManager = new DockingManager();
            var recoveredHost = new DockingHost(
                recoveredManager,
                RealMouseTools(ToolContent("Drag", Brushes.Transparent),
                    ToolContent("Companion", Brushes.Transparent),
                    ToolContent("Target", Brushes.Transparent),
                    ToolContent("Sentinel", Brushes.Transparent)),
                store,
                new MemoryLog());
            recoveredHost.Initialize();
            var recovered = recoveredHost.ListWindows();
            True(!recovered.Single(item => item.Id == "drag-tool").IsFloating &&
                 recovered.Single(item => item.Id == "drag-tool").Side == DockSide.Right,
                "real mouse roundtrip persists across layout reload");
            Equal(DockSide.Left, recovered.Single(item => item.Id == "sentinel-tool").Side,
                "layout reload keeps the unrelated custom side");
            Equal(0, recoveredManager.Layout.Descendents().OfType<LayoutDocumentPane>()
                .Single().ChildrenCount, "reloaded real-mouse layout keeps the center empty");
        }
        finally
        {
            try
            {
                RealMouseInput.ReleaseLeftButton();
            }
            catch (Exception)
            {
                // Preserve the original test failure when the desktop is being torn down.
            }

            if (window.IsVisible)
                window.Close();
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            try
            {
                RealMouseInput.Move(originalCursor);
            }
            catch (Exception)
            {
                // Cursor restoration is best effort after all assertions have completed.
            }
        }
    }

    private static ToolWindowDescriptor[] RealMouseTools(
        Border dragContent,
        Border dragCompanionContent,
        Border targetContent,
        Border sentinelContent) =>
    [
        new ToolWindowDescriptor
        {
            Id = "drag-tool",
            Title = "Drag tool",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.24,
            ContentFactory = () => dragContent,
        },
        new ToolWindowDescriptor
        {
            Id = "drag-companion",
            Title = "Drag companion",
            DefaultSide = DockSide.Tab,
            DefaultTabTarget = "drag-tool",
            ContentFactory = () => dragCompanionContent,
        },
        new ToolWindowDescriptor
        {
            Id = "target-tool",
            Title = "Dock target",
            DefaultSide = DockSide.Right,
            DefaultRatio = 0.27,
            ContentFactory = () => targetContent,
        },
        new ToolWindowDescriptor
        {
            Id = "sentinel-tool",
            Title = "Layout sentinel",
            DefaultSide = DockSide.Left,
            DefaultRatio = 0.21,
            ContentFactory = () => sentinelContent,
        },
    ];

    private static Border ToolContent(string text, Brush background) => new()
    {
        Background = background,
        Child = new TextBlock
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static LayoutAnchorable FindAnchorable(DockingManager manager, string id)
        => manager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Single(item => item.ContentId == id);

    private static T FindVisual<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
        => VisualDescendants<T>(root).Single(predicate);

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in VisualDescendants<T>(child))
                yield return nested;
        }
    }

    private static RealMouseInput.ScreenPoint ScreenCenter(FrameworkElement element)
    {
        True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0,
            $"rendered visual {element.GetType().Name}");
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new RealMouseInput.ScreenPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    private static RealMouseInput.ScreenPoint OutsideWindowPoint(Window window)
    {
        var topLeft = window.PointToScreen(new Point(0, 0));
        var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
        var screen = RealMouseInput.VirtualScreen;
        var y = (int)Math.Round(topLeft.Y + ((bottomRight.Y - topLeft.Y) * 0.45));
        if (screen.Right - bottomRight.X >= 140)
            return new RealMouseInput.ScreenPoint((int)Math.Round(bottomRight.X + 100), y);
        if (topLeft.X - screen.Left >= 140)
            return new RealMouseInput.ScreenPoint((int)Math.Round(topLeft.X - 100), y);
        return new RealMouseInput.ScreenPoint(
            (int)Math.Round(topLeft.X + ((bottomRight.X - topLeft.X) * 0.5)),
            Math.Min(screen.Bottom - 30, (int)Math.Round(bottomRight.Y + 80)));
    }

    private static Window FindFloatingWindow(DockingManager manager)
    {
        var field = typeof(DockingManager).GetField("_fwList", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("AvalonDock floating-window list was not found");
        var windows = ((System.Collections.IEnumerable)field.GetValue(manager)!)
            .Cast<object>()
            .OfType<Window>()
            .Where(item => item.IsVisible)
            .ToList();
        Equal(1, windows.Count, "one visible isolated floating tool window");
        return windows[0];
    }

    private static RealMouseInput.ScreenPoint FloatingCaptionPoint(Window window)
    {
        var chrome = WindowChrome.GetWindowChrome(window)
                     ?? throw new InvalidOperationException("AvalonDock floating window has no WindowChrome");
        var y = chrome.ResizeBorderThickness.Top + (chrome.CaptionHeight / 2);
        var point = window.PointToScreen(new Point(
            Math.Clamp(window.ActualWidth / 3, 48, 160),
            y));
        return new RealMouseInput.ScreenPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    private static void PerformRealMouseDrag(
        RealMouseInput.ScreenPoint start,
        RealMouseInput.ScreenPoint end,
        string name)
    {
        var input = Task.Run(() =>
        {
            // Let the STA enter its nested Dispatcher frame before physical input starts.
            // Otherwise optimized builds can enqueue and coalesce early move messages.
            Thread.Sleep(150);
            RealMouseInput.Drag(start, end, TimeSpan.FromMilliseconds(900));
        });
        PumpUntil(() => input.IsCompleted, TimeSpan.FromSeconds(12), name);
        input.GetAwaiter().GetResult();
        PumpDispatcher(TimeSpan.FromMilliseconds(900));
    }

    private static void AcquireForegroundInput(Window window, RealMouseInput.ScreenPoint clickPoint)
    {
        var handle = new WindowInteropHelper(window).Handle;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            window.Activate();
            RealMouseInput.BringToForeground(handle);
            RealMouseInput.Click(clickPoint);
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            if (RealMouseInput.ForegroundWindow == handle)
            {
                window.Topmost = false;
                return;
            }
        }

        throw new InvalidOperationException(
            "FAIL: isolated real-mouse window owns foreground input; " +
            $"expected={handle}, actual={RealMouseInput.ForegroundWindow}");
    }

    private static void PumpUntil(Func<bool> predicate, TimeSpan timeout, string name)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
                throw new TimeoutException($"Timed out while waiting for {name}");
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
        }
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
