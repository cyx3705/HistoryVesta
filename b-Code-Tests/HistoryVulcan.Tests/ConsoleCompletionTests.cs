extern alias mercury;

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.CommandSurface;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Shell.Console;
using CommandCatalogSession = mercury::Mercury.CommandSurface.CommandCatalogSession;
using CommandCompletionDefinition = mercury::Mercury.CommandSurface.CommandCompletionDefinition;
using CommandCompletionEngine = mercury::Mercury.CommandSurface.CommandCompletionEngine;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ConsoleCompletionTests
{
    [Fact]
    public void CommandPrefixReturnsStableCommandCandidates()
    {
        var registry = Registry(
            Command("vulcan.win.restore", "Restore the layout"),
            Command("vulcan.win.reset", "Reset the layout"),
            Command("app.show", "Show the frontend"));

        var result = Complete(registry, "win.", 4);

        Assert.Equal(new[] { "vulcan.win.reset", "vulcan.win.restore" },
            result.Candidates.Select(item => item.InsertText));
        Assert.Equal(ConsoleCompletionKind.Command, result.Candidates[0].Kind);
        Assert.Equal(0, result.ReplaceStart);
        Assert.Equal(4, result.ReplaceLength);
    }

    [Fact]
    public void ParameterPrefixReturnsKeyCandidatesAndSkipsUsedNames()
    {
        var registry = Registry(Command(
            "vulcan.win.dock",
            "Dock a window",
            new ParameterSpec { Name = "name", Description = "Window name" },
            new ParameterSpec { Name = "pos", Description = "Dock position" },
            new ParameterSpec { Name = "ratio", Description = "Dock ratio" }));

        var result = Complete(registry, "vulcan.win.dock name=console p", 30);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("pos=", candidate.InsertText);
        Assert.Equal(ConsoleCompletionKind.Parameter, candidate.Kind);
        Assert.Equal(29, result.ReplaceStart);
        Assert.Equal(1, result.ReplaceLength);
    }

    [Fact]
    public void NamedAllowedValuesSupportPartialAndQuotedInput()
    {
        var registry = Registry(Command(
            "vulcan.win.dock",
            "Dock a window",
            new ParameterSpec
            {
                Name = "pos",
                Description = "Dock position",
                AllowedValues = ["left", "right", "top", "bottom"],
            }));

        var result = Complete(registry, "vulcan.win.dock pos=\"t", 22);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("\"top\"", candidate.InsertText);
        Assert.Equal(ConsoleCompletionKind.Value, candidate.Kind);
        Assert.Equal(20, result.ReplaceStart);
        Assert.Equal(2, result.ReplaceLength);
    }

    [Fact]
    public void PositionalAllowedValuesAreSuggestedAfterCommand()
    {
        var registry = Registry(Command(
            "vulcan.log.level",
            "Set log level",
            new ParameterSpec
            {
                Name = "level",
                Description = "Log level",
                Position = 0,
                AllowedValues = ["trace", "debug", "info", "warn", "error", "fatal"],
            }));

        var result = Complete(registry, "vulcan.log.level er", 19);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("error", candidate.InsertText);
        Assert.Equal(ConsoleCompletionKind.Value, candidate.Kind);
    }

    [Fact]
    public void CompletionReplacesOnlyTheCurrentTokenWhenCaretIsInTheMiddle()
    {
        var registry = Registry(Command("vulcan.frontend.focusconsole", "Focus the console"));

        var result = Complete(registry, "vulcan.frontend.fo trailing", 18);

        var candidate = Assert.Single(result.Candidates);
        var completed = "vulcan.frontend.fo trailing"
            .Remove(result.ReplaceStart, result.ReplaceLength)
            .Insert(result.ReplaceStart, candidate.InsertText);

        Assert.Equal("vulcan.frontend.focusconsole trailing", completed);
    }

    [Fact]
    public void UnknownCommandOrValueProducesNoCandidates()
    {
        var registry = Registry(Command("app.show", "Show the frontend"));
        Assert.False(Complete(registry, "missing.", 8).HasCandidates);
        Assert.False(Complete(registry, "app.show mode=unknown", 21).HasCandidates);
        Assert.False(Complete(registry, "app.show", 8).HasCandidates);
    }

    [Fact]
    public void PopupOpensAboveInputAndShiftWOrSSelectsBeforeTabCommits()
    {
        RunSta(() =>
        {
            var registry = Registry(
                Command("vulcan.win.restore", "Restore the layout"),
                Command("vulcan.win.reset", "Reset the layout"));
            var log = new NullLog();
            var path = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-completion-{Guid.NewGuid():N}.txt");
            var bus = new CommandBus(registry, log);
            var selection = new CommandSelectionState();
            using var session = new CommandCatalogSession(bus, selection);
            var view = new ConsoleView(log, bus, new CommandHistory(path), session);
            var host = new Window
            {
                Content = view,
                Width = 360,
                Height = 420,
                ShowInTaskbar = false,
            };
            host.Resources.MergedDictionaries.Add(ThemeTokens("ShellTokens.xaml"));
            try
            {
                host.Show();
                host.Activate();
                PumpDispatcher();
                var input = Assert.IsType<TextBox>(view.FindName("Input"));
                Keyboard.Focus(input);
                input.Text = "win.";
                input.CaretIndex = input.Text.Length;
                PumpDispatcher();

                var popup = Assert.IsType<Popup>(view.FindName("CompletionPopup"));
                var list = Assert.IsType<ListBox>(view.FindName("CompletionList"));
                var completionBorder = Assert.IsType<Border>(view.FindName("CompletionBorder"));
                Assert.True(popup.IsOpen);
                Assert.Equal(PlacementMode.Top, popup.Placement);
                Assert.Same(input, popup.PlacementTarget);
                Assert.InRange(completionBorder.Width, 1, input.ActualWidth + 0.1);
                Assert.Equal(2, list.Items.Count);
                Assert.Equal(0, list.SelectedIndex);
                Assert.True(input.IsKeyboardFocusWithin);
                Assert.False(view.HandleCompletionKey(Key.W, ModifierKeys.None));

                var lightSurface = Assert.IsType<SolidColorBrush>(completionBorder.Background).Color;
                host.Resources.MergedDictionaries[0] = ThemeTokens("ShellTokens.Dark.xaml");
                PumpDispatcher();
                var darkSurface = Assert.IsType<SolidColorBrush>(completionBorder.Background).Color;
                Assert.NotEqual(lightSurface, darkSurface);

                Assert.True(view.HandleCompletionKey(Key.S, ModifierKeys.Shift));
                Assert.Equal(1, list.SelectedIndex);
                Assert.Equal("win.", input.Text);

                Assert.True(view.HandleCompletionKey(Key.W, ModifierKeys.Shift));
                Assert.Equal(0, list.SelectedIndex);
                Assert.False(view.HandleCompletionKey(Key.Tab, ModifierKeys.Shift));
                Assert.Equal(0, list.SelectedIndex);

                Assert.True(view.HandleCompletionKey(Key.S, ModifierKeys.Shift));
                Assert.Equal(1, list.SelectedIndex);

                Assert.True(view.HandleCompletionKey(Key.Tab, ModifierKeys.None));
                Assert.Equal("vulcan.win.restore", input.Text);
                Assert.False(popup.IsOpen);
                Assert.True(input.IsKeyboardFocusWithin);
            }
            finally
            {
                host.Close();
                if (File.Exists(path))
                    File.Delete(path);
            }
        });
    }

    [Fact]
    public void NonFocusedInputShowsCommandCatalogOnceAndFocusedModeRestoresCompletion()
    {
        RunSta(() =>
        {
            var registry = Registry(
                Command("vulcan.win.restore", "Restore the layout"),
                Command("vulcan.win.reset", "Reset the layout"));
            var log = new NullLog();
            var path = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-completion-{Guid.NewGuid():N}.txt");
            var bus = new CommandBus(registry, log);
            var selection = new CommandSelectionState();
            using var session = new CommandCatalogSession(bus, selection);
            var view = new ConsoleView(log, bus, new CommandHistory(path), session);
            var host = new Window
            {
                Content = view,
                Width = 360,
                Height = 420,
                ShowInTaskbar = false,
            };
            host.Resources.MergedDictionaries.Add(ThemeTokens("ShellTokens.xaml"));
            try
            {
                host.Show();
                host.Activate();
                PumpDispatcher();

                var input = Assert.IsType<TextBox>(view.FindName("Input"));
                var popup = Assert.IsType<Popup>(view.FindName("CompletionPopup"));
                Keyboard.Focus(input);
                var catalogRequests = 0;
                var completionFocused = false;
                view.ConfigureCompletionRouting(
                    () => completionFocused,
                    () => catalogRequests++);

                input.Text = "win.";
                input.CaretIndex = input.Text.Length;
                PumpDispatcher();

                Assert.Equal(1, catalogRequests);
                Assert.Equal("win.", input.Text);
                Assert.Equal("win.", session.CurrentFilter.Query);
                Assert.False(popup.IsOpen);

                input.Text += "r";
                input.CaretIndex = input.Text.Length;
                PumpDispatcher();
                Assert.Equal(1, catalogRequests);
                Assert.Equal("win.r", session.CurrentFilter.Query);
                Assert.False(popup.IsOpen);

                Assert.True(view.HandleCompletionKey(Key.S, ModifierKeys.Shift));
                Assert.Equal("vulcan.win.restore", selection.CurrentCommandName);
                Assert.True(view.HandleCompletionKey(Key.W, ModifierKeys.Shift));
                Assert.Equal("vulcan.win.reset", selection.CurrentCommandName);
                Assert.True(view.HandleCompletionKey(Key.S, ModifierKeys.Shift));
                Assert.Equal("vulcan.win.restore", selection.CurrentCommandName);
                Assert.True(view.HandleCompletionKey(Key.Tab, ModifierKeys.None));
                Assert.Equal("vulcan.win.restore", input.Text);
                Assert.Equal("vulcan.win.restore", session.CurrentFilter.Query);

                input.Text = "win.";
                input.CaretIndex = input.Text.Length;
                PumpDispatcher();

                completionFocused = true;
                view.RefreshCompletionFocus();
                PumpDispatcher();
                Assert.True(popup.IsOpen);
                Assert.Equal(string.Empty, session.CurrentFilter.Query);

                completionFocused = false;
                view.RefreshCompletionFocus();
                Assert.False(popup.IsOpen);
                Assert.Equal("win.", session.CurrentFilter.Query);
                Assert.Equal(2, catalogRequests);

                input.Text = "";
                input.Text = "win.";
                input.CaretIndex = input.Text.Length;
                PumpDispatcher();
                Assert.Equal(3, catalogRequests);
                Assert.Equal("win.", input.Text);
            }
            finally
            {
                host.Close();
                if (File.Exists(path))
                    File.Delete(path);
            }
        });
    }

    [Fact]
    public void OpenPopupRefreshesWhenRegistryChanges()
    {
        RunSta(() =>
        {
            var registry = Registry(Command("zeta.one", "First command"));
            var log = new NullLog();
            var path = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-completion-{Guid.NewGuid():N}.txt");
            var bus = new CommandBus(registry, log);
            var view = new ConsoleView(
                log,
                bus,
                new CommandHistory(path),
                new CommandCatalogSession(bus, new CommandSelectionState()));
            var host = new Window { Content = view, Width = 640, Height = 360, ShowInTaskbar = false };
            try
            {
                host.Show();
                host.Activate();
                PumpDispatcher();
                var input = Assert.IsType<TextBox>(view.FindName("Input"));
                Keyboard.Focus(input);
                input.Text = "zeta.";
                input.CaretIndex = input.Text.Length;
                PumpDispatcher();
                var list = Assert.IsType<ListBox>(view.FindName("CompletionList"));
                Assert.Single(list.Items);

                registry.Register(Command("zeta.two", "Second command"), "test");
                PumpDispatcher(450);

                Assert.Equal(new[] { "zeta.one", "zeta.two" },
                    list.Items.Cast<ConsoleCompletionCandidate>().Select(item => item.InsertText));
            }
            finally
            {
                host.Close();
                if (File.Exists(path))
                    File.Delete(path);
            }
        });
    }

    private static CommandRegistry Registry(params CommandDescriptor[] descriptors)
    {
        var registry = new CommandRegistry();
        foreach (var descriptor in descriptors)
            registry.Register(descriptor, "test");
        return registry;
    }

    private static ConsoleCompletionResult Complete(
        CommandRegistry registry,
        string text,
        int caretIndex)
    {
        var definitions = registry.All()
            .Select(command => new CommandCompletionDefinition(
                command.Name,
                command.Summary,
                command.Parameters))
            .ToList();
        return new CommandCompletionEngine().Complete(text, caretIndex, definitions);
    }

    private static CommandDescriptor Command(
        string name,
        string summary,
        params ParameterSpec[] parameters)
        => new()
        {
            Name = name,
            Summary = summary,
            Parameters = parameters,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };

    private static ResourceDictionary ThemeTokens(string fileName)
        => new()
        {
            Source = new Uri($"/HistoryVulcan.Shell;component/Themes/{fileName}", UriKind.Relative),
        };

    private static void PumpDispatcher(int milliseconds = 100)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
