using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OneHistory.AppShell.Desktop;

namespace OneHistory.AppShell.ContractTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("catalog rejects duplicate page ids", CatalogRejectsDuplicateIds),
            ("shell supports active floating docked and reopened pages", ShellSupportsPageLifecycle),
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void CatalogRejectsDuplicateIds()
    {
        var catalog = new DesktopPageCatalog();
        catalog.Register(Page("sample"));
        AssertThrows<InvalidOperationException>(() => catalog.Register(Page("SAMPLE")));
    }

    private static void ShellSupportsPageLifecycle()
    {
        var app = new App();
        app.InitializeComponent();
        var window = new ShellWindow
        {
            ShowActivated = false,
        };

        try
        {
            window.Show();
            PumpDispatcher();
            AssertState(window, "home", isOpen: true, isActive: true, isFloating: false);
            AssertState(window, "shell", isOpen: false, isActive: false, isFloating: false);

            window.RegisterPage(Page("module.page"));
            PumpDispatcher();
            AssertState(window, "module.page", isOpen: true, isActive: true, isFloating: false);

            Assert(window.FloatPage("module.page"), "FloatPage returned false.");
            PumpDispatcher();
            AssertState(window, "module.page", isOpen: true, isActive: true, isFloating: true);

            Assert(window.DockPage("module.page"), "DockPage returned false.");
            PumpDispatcher();
            AssertState(window, "module.page", isOpen: true, isActive: true, isFloating: false);

            Assert(window.ClosePage("module.page"), "ClosePage returned false.");
            PumpDispatcher();
            AssertState(window, "module.page", isOpen: false, isActive: false, isFloating: false);

            Assert(window.ActivatePage("module.page"), "ActivatePage did not reopen the page.");
            PumpDispatcher();
            AssertState(window, "module.page", isOpen: true, isActive: true, isFloating: false);

            Assert(!window.ClosePage("home"), "The non-closeable home page was closed.");
            Assert(window.UnregisterPage("module.page"), "UnregisterPage returned false.");
            Assert(window.ListPages().All(page => page.Id != "module.page"), "Unregistered page remains visible.");
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static DesktopPageDefinition Page(string id)
        => new(id, id, () => new Border(), "contract-test");

    private static void AssertState(
        ShellWindow window,
        string id,
        bool isOpen,
        bool isActive,
        bool isFloating)
    {
        var page = window.ListPages().Single(item => item.Id == id);
        Assert(page.IsOpen == isOpen, $"{id} IsOpen was {page.IsOpen}.");
        Assert(page.IsActive == isActive, $"{id} IsActive was {page.IsActive}.");
        Assert(page.IsFloating == isFloating, $"{id} IsFloating was {page.IsFloating}.");
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        catch (T)
        {
        }
    }
}
