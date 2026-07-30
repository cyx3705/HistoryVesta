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
}
