using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services;
using HistoryVulcan.Shell;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Mcp;
using AvalonDock.Controls;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 3.1 外壳合同(UI-02 / UI-03 / UI-04 / UI-05 / UI-09):
/// 常驻菜单行与状态栏已取消,顶栏按钮组恒定,页面最大化为无壳专注态。
/// 这些断言针对真实可视树,不是对 XAML 文本的检查。
/// </summary>
public sealed class ShellChromeContractTests
{
    [Fact]
    public void ShellWindowHasNoMenuBarNoStatusBarAndNoTitleBarRow()
    {
        RunShell(window =>
        {
            // UI-03 / UI-05.1:两条常驻横带都不复存在
            Assert.Empty(FindVisualDescendants<Menu>(window));
            Assert.Empty(FindVisualDescendants<System.Windows.Controls.Primitives.StatusBar>(window));

            // 3.1 修订:独立标题栏那一层也没有了 —— 停靠区必须从窗体顶端起算,
            // 按钮组与最上一排页签同处一行。
            var chromeBar = RequireElement<FrameworkElement>(window, "ChromeBar");
            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var managerTop = manager.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            var chromeTop = chromeBar.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            Assert.True(managerTop <= chromeTop + 0.5,
                $"docking top={managerTop} must not be pushed below the chrome bar top={chromeTop}");
            Assert.True(chromeBar.ActualHeight <= 32.5,
                $"chrome bar height={chromeBar.ActualHeight} must match the tab row height");
        });
    }

    [Fact]
    public void ChromeBarReservesRoomSoTabsNeverHideUnderTheButtons()
    {
        RunShell(window =>
        {
            var chromeBar = RequireElement<FrameworkElement>(window, "ChromeBar");
            var panel = FindVisualDescendants<AvalonDock.Controls.DocumentPaneTabPanel>(window)
                .Single(item => item.IsVisible);

            // 页签面板落在按钮组正下方时,右边距必须把按钮宽度让出来
            var panelRight = panel.TransformToAncestor(window).Transform(new Point(panel.ActualWidth, 0)).X;
            var chromeLeft = chromeBar.TransformToAncestor(window).Transform(new Point(0, 0)).X;
            Assert.True(panelRight <= chromeLeft + 0.5,
                $"tab panel right={panelRight} overlaps chrome bar left={chromeLeft}");
        });
    }

    [Fact]
    public void WindowChromeBarBelongsToCentralDocumentPane()
    {
        RunShell(window =>
        {
            var chromeBar = RequireElement<Panel>(window, "ChromeBar");
            var host = FindAncestor<ContentControl>(
                chromeBar,
                control => Equals(control.Tag, "ShellChromeHost"));

            Assert.NotNull(host);
            Assert.NotNull(FindAncestor<LayoutDocumentPaneControl>(chromeBar, _ => true));
            Assert.DoesNotContain(
                FindVisualDescendants<LayoutAnchorableControl>(window)
                    .SelectMany(tool => FindVisualDescendants<ContentControl>(tool)),
                control => Equals(control.Tag, "ShellChromeHost"));
        });
    }

    [Fact]
    public void InvisibleCaptionDoesNotCoverToolPaneControls()
    {
        RunShell(window =>
        {
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.NotNull(chrome);
            Assert.Equal(0, chrome.CaptionHeight);

            var actions = FindVisualDescendants<AnchorablePaneTitle>(window)
                .First(item => item.IsVisible);
            Assert.All(
                FindVisualDescendants<ButtonBase>(actions),
                button => Assert.True(button.IsHitTestVisible));
        });
    }

    [Fact]
    public void CommandFailureOpensConsoleWithoutErrorFlyout()
    {
        RunShell(window =>
        {
            window.Docking.Hide(StandardWindowIds.Console);
            PumpDispatcher();
            Assert.False(window.Docking.ListWindows()
                .Single(item => item.Id == StandardWindowIds.Console).IsVisible);

            var result = window.Commands.ExecuteAsync("missing.command", "test")
                .GetAwaiter().GetResult();
            PumpDispatcher();

            Assert.False(result.Success);
            Assert.True(window.Docking.ListWindows()
                .Single(item => item.Id == StandardWindowIds.Console).IsVisible);
            Assert.Equal(Visibility.Collapsed, RequireElement<FrameworkElement>(window, "Toast").Visibility);
        });
    }

    [Fact]
    public void ConsoleEnterShowsTypedCommandAndFailureResult()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                var input = FindVisualDescendants<TextBox>(window)
                    .Single(item => item.Name == "Input");
                input.Focus();
                input.Text = "123";

                var key = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(input),
                    0,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                input.RaiseEvent(key);
                PumpDispatcher(250);

                var output = FindVisualDescendants<ListBox>(window)
                    .Single(list => list.Name == "Output");
                var rows = output.Items.Cast<HistoryVulcan.Shell.Console.ConsoleRow>()
                    .Where(item => item.Text.Contains("123", StringComparison.Ordinal))
                    .ToList();

                Assert.True(rows.Any(item => item.Text.Contains("> 123", StringComparison.Ordinal)),
                    "the command echo must be visible after pressing Enter");
                Assert.True(rows.Any(item => item.Level >= ShellLogLevel.Error),
                    "the unknown-command error must be visible after pressing Enter");

                var echo = rows.First(item => item.Text.Contains("> 123", StringComparison.Ordinal));
                output.ScrollIntoView(echo);
                window.UpdateLayout();
                PumpDispatcher();
                Assert.True(output.IsVisible && output.ActualHeight > 0 && output.ActualWidth > 0,
                    $"console output is not laid out: visible={output.IsVisible}, " +
                    $"size={output.ActualWidth}x{output.ActualHeight}");
                var container = Assert.IsType<ListBoxItem>(
                    output.ItemContainerGenerator.ContainerFromItem(echo));
                container.ApplyTemplate();
                window.UpdateLayout();
                Assert.True(container.IsVisible && container.ActualHeight > 0,
                    "the command row exists but its ListBoxItem is not rendered");
                var descendants = FindVisualDescendants<DependencyObject>(container).ToList();
                var renderedTexts = descendants.OfType<TextBlock>().ToList();
                Assert.True(renderedTexts.Count == 1,
                    "unexpected row visual tree: " +
                    string.Join(", ", descendants.Select(item => item.GetType().Name)));
                var renderedText = renderedTexts[0];
                Assert.True(renderedText.IsVisible && renderedText.ActualWidth > 0,
                    "the command row exists but its TextBlock is not visible");
            },
            log: log);
    }

    [Fact]
    public void ConsoleRowsExtractDomainsFromCommandsOutputsAndLogCategories()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:UI", "app.open target=x"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:UI", "help"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:result:app", "done"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:progress:app", "step"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "module:loader.detail", "loaded"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "render.frame", "drawn"))[0],
        };

        Assert.Equal(new[] { "app", "core", "app", "app", "module", "render" },
            rows.Select(row => row.DomainKey));
        Assert.Equal("UI", rows[0].SourceKey);
        Assert.Equal("result", rows[2].SourceKey);
    }

    [Fact]
    public void FailedConsoleCommandDoesNotExitFocusedConsole()
    {
        RunShell(window =>
        {
            var maximize = window.Commands.ExecuteAsync(
                $"win.max name={StandardWindowIds.Console}", "test").GetAwaiter().GetResult();
            Assert.True(maximize.Success, maximize.Message);
            PumpDispatcher();
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);

            var input = FindVisualDescendants<TextBox>(window)
                .Single(item => item.Name == "Input");
            input.Focus();
            input.Text = "missing.command";
            input.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(input),
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            PumpDispatcher(400);

            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(input.IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public void FrontendFocusConsoleRaisesTheWindowAndRefocusesExistingConsole()
    {
        RunShell(window =>
        {
            Assert.True(window.Commands.ExecuteAsync("app.frontend.hide", "test")
                .GetAwaiter().GetResult().Success);
            PumpDispatcher();
            Assert.False(window.IsVisible);

            Assert.True(window.Commands.ExecuteAsync("app.frontend.focus-console", "test")
                .GetAwaiter().GetResult().Success);
            PumpDispatcher(500);

            var input = FindVisualDescendants<TextBox>(window)
                .Single(item => item.Name == "Input");
            Assert.True(window.IsVisible);
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(HasKeyboardOrLogicalFocus(input));
            Assert.False(window.Topmost);

            var layoutChanges = 0;
            window.Docking.WindowsChanged += (_, _) => layoutChanges++;
            RequireButton(window, "MenuButton").Focus();
            Assert.True(window.Commands.ExecuteAsync("app.frontend.focus-console", "test")
                .GetAwaiter().GetResult().Success);
            PumpDispatcher(500);

            Assert.True(HasKeyboardOrLogicalFocus(input));
            Assert.False(window.Topmost);
            Assert.Equal(0, layoutChanges);
        });
    }

    [Fact]
    public void DarkThemeUsesTokenizedCatalogControlsAndConsoleRows()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                window.Commands.ExecuteAsync("app.theme mode=dark", "test")
                    .GetAwaiter().GetResult();
                PumpDispatcher();

                var primary = ((SolidColorBrush)window.FindResource("Shell.Brush.TextPrimary")).Color;
                window.Docking.Show(StandardWindowIds.Mcp);
                PumpDispatcher(600);
                var catalog = Assert.Single(
                    FindVisualDescendants<HistoryVulcan.Shell.Views.McpToolsView>(window));
                foreach (var name in new[] { "DomainFilterBox", "ClassFilterBox", "McpFilterBox" })
                {
                    var combo = Assert.IsType<ComboBox>(catalog.FindName(name));
                    Assert.Equal(primary, ((SolidColorBrush)combo.Foreground).Color);
                }

                var console = Assert.Single(
                    FindVisualDescendants<HistoryVulcan.Shell.Console.ConsoleView>(window));
                console.FilterErrorsOnly();
                Assert.Equal(ShellLogLevel.Error, console.MinLevel);

                window.Commands.ExecuteAsync("missing.command", "test")
                    .GetAwaiter().GetResult();
                PumpDispatcher(700);
                Assert.Equal(ShellLogLevel.Trace, console.MinLevel);
                var output = FindVisualDescendants<ListBox>(window)
                    .Single(list => list.Name == "Output");
                var matchingRows = output.Items.Cast<HistoryVulcan.Shell.Console.ConsoleRow>()
                    .Where(item => item.Text.Contains("missing.command", StringComparison.Ordinal))
                    .ToList();
                Assert.True(matchingRows.Count >= 2, "input echo and error result must both be visible");
                var row = matchingRows[0];
                var text = Assert.IsType<TextBlock>(output.ItemTemplate.LoadContent());
                text.DataContext = row;
                var probe = new ContentControl { Content = text };
                var root = RequireElement<Grid>(window, "RootGrid");
                root.Children.Add(probe);
                PumpDispatcher();

                var consoleColor = ((SolidColorBrush)text.Foreground).Color;
                Assert.True(
                    consoleColor.R + consoleColor.G + consoleColor.B > 0x180,
                    $"console row remained too dark for the dark theme: {consoleColor}");
                root.Children.Remove(probe);
            },
            log: log);
    }

    [Fact]
    public void ToolPagesHaveNoCaptionBarAndActionsFloatInTheCorner()
    {
        RunShell(window =>
        {
            // R3-1:工具页原来的「蓝条」标题栏(标题 + 虚线 + ▼📌✕)已取消。
            // 判据是它不再占据布局高度 —— 内容必须从工具页顶端开始。
            var host = FindVisualDescendants<LayoutAnchorableControl>(window).First(item => item.IsVisible);
            var content = FindVisualDescendants<ContentPresenter>(host).First();
            var hostTop = host.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            var contentTop = content.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            Assert.True(contentTop - hostTop < 2,
                $"tool page content is pushed down by {contentTop - hostTop}px — a caption bar is back");

            // R4-1:内容宿主里不得再有任何标题条部件 —— 动作已搬到页签行
            Assert.Empty(FindVisualDescendants<AnchorablePaneTitle>(host));
        });
    }

    [Fact]
    public void DarkThemeRepaintsGridHeadersAndUsesPaleYellowText()
    {
        RunShell(window =>
        {
            window.Commands.ExecuteAsync("app.theme mode=dark", "test").GetAwaiter().GetResult();
            PumpDispatcher();

            // R3-3:表格必须跟着深色走 —— GridView 会给表头显式指定容器样式,
            // 隐式样式命不中,历史上这里留了一条系统渐变的浅色表头。
            var surfaceAlt = ((SolidColorBrush)window.FindResource("Shell.Brush.SurfaceAlt")).Color;
            var headers = FindVisualDescendants<GridViewColumnHeader>(window)
                .Where(header => header.IsVisible && header.Content != null)
                .ToList();
            Assert.NotEmpty(headers);
            foreach (var header in headers)
            {
                Assert.True(
                    FindVisualDescendants<Border>(header)
                        .Any(border => border.Background is SolidColorBrush brush && brush.Color == surfaceAlt),
                    $"grid header '{header.Content}' is not painted with the dark surface");
            }

            // R3-4 / R4-4:正文偏暖(淡黄向)但低饱和 —— 太艳会有玩具感
            var text = ((SolidColorBrush)window.FindResource("Shell.Brush.TextPrimary")).Color;
            Assert.True(text.R > 0xD0 && text.B < text.G && text.G < text.R,
                $"dark text should lean warm/pale yellow, got {text}");
            Assert.True(text.R - text.B <= 0x30,
                $"dark text is oversaturated (R-B={text.R - text.B:X}), got {text}");

            // R3-4 / R4-4:底色偏墨绿但同样低饱和,不是偏蓝的中性灰
            var canvas = ((SolidColorBrush)window.FindResource("Shell.Brush.Canvas")).Color;
            Assert.True(canvas.G > canvas.B && canvas.G > canvas.R,
                $"dark canvas should lean ink-green, got {canvas}");
            Assert.True(canvas.G - canvas.R <= 0x18,
                $"dark canvas is oversaturated (G-R={canvas.G - canvas.R:X}), got {canvas}");
        });
    }

    [Fact]
    public void DarkThemeLeavesNoLightPixelsInsideTables()
    {
        RunShell(window =>
        {
            window.Commands.ExecuteAsync("app.theme mode=dark", "test").GetAwaiter().GetResult();
            PumpDispatcher();

            // R4-5:按属性看颜色会骗人 —— 表格那块浅灰来自 WPF 默认模板的
            // 「禁用态」触发器,只有真正渲染出来采样才抓得住。
            foreach (var list in FindVisualDescendants<ListView>(window)
                         .Where(item => item.IsVisible && item.ActualWidth > 100 && item.ActualHeight > 60))
            {
                foreach (var (x, y) in new[] { (0.5, 0.3), (0.5, 0.6), (0.1, 0.6), (0.9, 0.45) })
                {
                    var pixel = SamplePixel(list, x, y);
                    if (pixel.A == 0)
                        continue;
                    var luminance = (pixel.R + pixel.G + pixel.B) / 3;
                    var spread = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))
                                 - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                    // 亮且中性 = 系统那块浅灰(#F4F4F4);淡黄文字同样亮但偏暖,不算
                    Assert.False(luminance > 0x60 && spread < 0x14,
                        $"table pixel @{x},{y} is neutral light ({pixel}) — dark theme did not reach the table body");
                }
            }
        });
    }

    [Fact]
    public void FloatingToolWindowFrameFollowsLightAndDarkThemes()
    {
        RunShell(window =>
        {
            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            window.Docking.Float(StandardWindowIds.Console);
            PumpDispatcher(900);

            // R4-3:浮动窗口自带一份主题字典,不单独换就一直是白的
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Surface")).Color,
                Assert.IsType<SolidColorBrush>(floating.Background).Color);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Hairline")).Color,
                Assert.IsType<SolidColorBrush>(floating.BorderBrush).Color);
            Assert.Equal(new Thickness(1), floating.BorderThickness);

            window.Commands.ExecuteAsync("app.theme mode=dark", "test").GetAwaiter().GetResult();
            PumpDispatcher();

            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Surface")).Color,
                Assert.IsType<SolidColorBrush>(floating.Background).Color);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Shell.Brush.Hairline")).Color,
                Assert.IsType<SolidColorBrush>(floating.BorderBrush).Color);
        });
    }

    [Fact]
    public void CentralTabsUseOneVisualSelectionSource()
    {
        RunShell(
            window =>
            {
                window.Docking.Show("center.one");
                window.Docking.Show("center.two");
                PumpDispatcher();

                var accent = ((SolidColorBrush)window.FindResource("Shell.Brush.AccentSoft")).Color;
                var active = FindVisualDescendants<LayoutDocumentTabItem>(window)
                    .Where(item => item.IsVisible)
                    .Where(item => FindVisualDescendants<Border>(item)
                        .Any(border => border.Background is SolidColorBrush brush && brush.Color == accent))
                    .ToList();

                Assert.Single(active);
            },
            configure: config =>
            {
                config.ToolWindows.Add(new ToolWindowDescriptor
                {
                    Id = "center.one",
                    Title = "Center One",
                    DefaultSide = DockSide.Center,
                    DefaultRatio = 1,
                    ContentFactory = () => new Border(),
                });
                config.ToolWindows.Add(new ToolWindowDescriptor
                {
                    Id = "center.two",
                    Title = "Center Two",
                    DefaultSide = DockSide.Center,
                    DefaultRatio = 1,
                    ContentFactory = () => new Border(),
                });
            });
    }

    [Fact]
    public void FocusedToolUsesTheSingleMainWindowChromeRow()
    {
        RunShell(
            window =>
            {
                var result = window.Commands.ExecuteAsync("win.max name=focus.tool", "test")
                    .GetAwaiter().GetResult();
                Assert.True(result.Success, result.Message);
                PumpDispatcher();

                var focusedHeader = FindVisualDescendants<Grid>(window)
                    .SingleOrDefault(item => item.IsVisible && Equals(item.Tag, "FocusedShellPaneHeader"));
                Assert.NotNull(focusedHeader);
                var tab = Assert.Single(FindVisualDescendants<LayoutAnchorableTabItem>(focusedHeader!));
                Assert.Equal("focus.tool", tab.Model.ContentId);
                Assert.DoesNotContain(
                    FindVisualDescendants<Button>(focusedHeader!),
                    button => Equals(button.CommandParameter, "restore") && button.IsVisible);

                var chromeBar = RequireElement<Panel>(window, "ChromeBar");
                Assert.NotNull(VisualTreeHelper.GetParent(chromeBar));
                Assert.Equal(Visibility.Visible, RequireButton(window, "ExitFocusButton").Visibility);
                Assert.All(
                    new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" },
                    name => Assert.Equal(Visibility.Visible, RequireButton(window, name).Visibility));
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FocusedToolContentStartsBelowItsChromeRow()
    {
        RunShell(
            window =>
            {
                var result = window.Commands.ExecuteAsync("win.max name=focus.tool", "test")
                    .GetAwaiter().GetResult();
                Assert.True(result.Success, result.Message);
                PumpDispatcher();

                var header = FindVisualDescendants<Grid>(window)
                    .Single(item => item.IsVisible && Equals(item.Tag, "FocusedShellPaneHeader"));
                var content = FindVisualDescendants<ContentPresenter>(window)
                    .Single(item => item.IsVisible && item.Name == "PART_SelectedContentHost");
                var root = Assert.IsType<Grid>(VisualTreeHelper.GetParent(header));

                Assert.Same(root, VisualTreeHelper.GetParent(content));
                Assert.Equal(2, root.RowDefinitions.Count);
                Assert.Equal(0, Grid.GetRow(header));
                Assert.Equal(1, Grid.GetRow(content));
                Assert.NotNull(content.Content);
                Assert.True(content.ActualHeight > 0, $"focused content height={content.ActualHeight}");
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FloatingToolWindowHasNoSecondOuterChromeRow()
    {
        RunShell(window =>
        {
            var content = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            var actions = FindVisualDescendants<AnchorablePaneTitle>(window)
                .Single(item => item.Model.ContentId == StandardWindowIds.Console);
            var paneHeader = FindAncestor<Grid>(actions, item => Equals(item.Tag, "ShellPaneHeader"));
            Assert.NotNull(paneHeader);

            window.Docking.Float(StandardWindowIds.Console);
            PumpDispatcher(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            var chrome = WindowChrome.GetWindowChrome(floating);
            Assert.NotNull(chrome);
            Assert.Equal(0, chrome.CaptionHeight);
            var root = Assert.IsType<Border>(floating.Template.LoadContent());
            var presenter = Assert.Single(FindLogicalDescendants<ContentPresenter>(root));
            Assert.Null(presenter.DataContext);
            Assert.DoesNotContain(
                FindLogicalDescendants<FrameworkElement>(root),
                item => item.GetType().Name.Contains("FloatingWindowControlChrome", StringComparison.Ordinal));
            Assert.DoesNotContain(
                FindVisualDescendants<FrameworkElement>(floating),
                item => Equals(item.Tag, "FloatingShellPaneHeader"));
            Assert.Equal("FloatingWindowContentHost", floating.Content.GetType().Name);
            Assert.True(paneHeader!.IsVisible);
            Assert.True(content.IsVisible);
            Assert.NotNull(PresentationSource.FromVisual(content));
            Assert.NotSame(PresentationSource.FromVisual(floating), PresentationSource.FromVisual(content));

            // 浮窗内容位于独立 PresentationSource，拖动事件必须随 Pane Style 下发，
            // 不能依赖主 DockingManager 的视觉树扫描。
            var floatingPane = FindAncestor<LayoutAnchorablePaneControl>(content, _ => true);
            Assert.NotNull(floatingPane);
            Assert.Contains(
                floatingPane!.Style.Setters.OfType<EventSetter>(),
                setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
        });
    }

    [Fact]
    public void ToolPaneHasNoFloatingMaximizeButtonButDocumentPaneKeepsItsOwn()
    {
        RunShell(window =>
        {
            var actions = FindVisualDescendants<AnchorablePaneTitle>(window)
                .Single(item => item.Model.ContentId == StandardWindowIds.Console);
            Assert.DoesNotContain(
                FindVisualDescendants<Button>(actions),
                button => Equals(button.Tag, "FloatingMaxRestore"));

            var documentMaxRestore = FindVisualDescendants<Button>(window)
                .Single(button => button.Name == "FloatingDocumentMaxRestore");
            Assert.Equal("toggle-floating", documentMaxRestore.CommandParameter);
            Assert.Equal("FloatingMaxRestore", documentMaxRestore.Tag);
        });
    }

    [Fact]
    public void EmbeddedMainAndToolHeadersShareTheMainWindowGesturePipeline()
    {
        RunShell(
            window =>
            {
                var mainSurface = Assert.IsAssignableFrom<FrameworkElement>(
                    GetPrivateField(window, "_chromeDragSurface"));
                Assert.NotNull(FindAncestor<LayoutDocumentPaneControl>(mainSurface, _ => true));

                var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
                var toolEvents = manager.AnchorablePaneControlStyle.Setters.OfType<EventSetter>().ToList();
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseMoveEvent);
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonUpEvent);
                Assert.Contains(toolEvents, setter => setter.Event == Mouse.LostMouseCaptureEvent);

                var documentEvents = manager.DocumentPaneControlStyle.Setters.OfType<EventSetter>().ToList();
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseMoveEvent);
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonUpEvent);
                Assert.Contains(documentEvents, setter => setter.Event == Mouse.LostMouseCaptureEvent);

                var result = window.Commands.ExecuteAsync("win.max name=focus.tool", "test")
                    .GetAwaiter().GetResult();
                Assert.True(result.Success, result.Message);
                PumpDispatcher();

                var focusedSurface = Assert.IsAssignableFrom<FrameworkElement>(
                    GetPrivateField(window, "_chromeDragSurface"));
                var focusedHeader = FindVisualDescendants<FrameworkElement>(window)
                    .Single(element => element.IsVisible && Equals(element.Tag, "FocusedShellPaneHeader"));
                Assert.Same(focusedHeader, focusedSurface);
                Assert.NotNull(FindAncestor<LayoutAnchorablePaneControl>(focusedHeader, _ => true));
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FloatingDocumentWindowKeepsItsContentHost()
    {
        Grid? content = null;
        RunShell(window =>
        {
            window.Docking.Show("center.float");
            PumpDispatcher();
            Assert.NotNull(content);
            window.Docking.Float("center.float");
            PumpDispatcher(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal("FloatingWindowContentHost", floating.Content.GetType().Name);
            Assert.True(content!.IsVisible);
            Assert.NotNull(PresentationSource.FromVisual(content));
            Assert.NotSame(PresentationSource.FromVisual(floating), PresentationSource.FromVisual(content));
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.float",
            Title = "Center Float",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => content = new Grid { Tag = "FloatingDocumentContent" },
        }));
    }

    [Fact]
    public void FloatingDocumentWindowActionRoutesThroughItsOwnPaneBinding()
    {
        Grid? content = null;
        RunShell(window =>
        {
            window.Docking.Show("center.float.actions");
            window.Docking.Float("center.float.actions");
            PumpDispatcher(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.NotNull(content);
            var pane = FindAncestor<LayoutDocumentPaneControl>(content!, _ => true);
            Assert.NotNull(pane);
            var button = FindVisualDescendants<Button>(pane!)
                .Single(item => item.Name == "FloatingDocumentMaxRestore");
            Assert.Equal(Visibility.Visible, button.Visibility);
            var command = Assert.IsType<RoutedCommand>(button.Command);

            Assert.True(command.CanExecute(button.CommandParameter, button.CommandTarget));
            command.Execute(button.CommandParameter, button.CommandTarget);
            PumpDispatcher();
            Assert.Equal(WindowState.Maximized, floating.WindowState);
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.float.actions",
            Title = "Center Float Actions",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => content = new Grid(),
        }));
    }

    [Fact]
    public void FloatingDocumentWindowStateIsOwnedByCommandBus()
    {
        RunShell(window =>
        {
            window.Docking.Show("center.state");
            window.Docking.Float("center.state");
            PumpDispatcher(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            var maximize = window.Commands.ExecuteAsync(
                "win.float-state name=center.state state=maximized", "Test").GetAwaiter().GetResult();
            Assert.True(maximize.Success, maximize.Message);
            PumpDispatcher();
            floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(WindowState.Maximized, floating.WindowState);

            var restore = window.Commands.ExecuteAsync(
                "win.float-state name=center.state state=toggle", "Test").GetAwaiter().GetResult();
            Assert.True(restore.Success, restore.Message);
            PumpDispatcher();
            floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(WindowState.Normal, floating.WindowState);
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.state",
            Title = "Center State",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => new Grid(),
        }));
    }

    [Fact]
    public void PaneActionsSitInTheTabRowWithoutTheAutoHideButton()
    {
        RunShell(window =>
        {
            var actions = FindVisualDescendants<AnchorablePaneTitle>(window).First(item => item.IsVisible);

            // R4-1:动作图标属于页签行,不再浮在内容之上
            var header = FindAncestor<Grid>(actions, grid => grid.Tag as string == "ShellPaneHeader");
            Assert.NotNull(header);

            // R4-2:📌 单独成键已取消,同一动作仍在 ▼ 菜单里。
            // 菜单挂在 Popup 上,只有真打开才进可视树 —— 顺带验证 ▼ 确实能弹出菜单。
            var buttons = FindVisualDescendants<ButtonBase>(actions).ToList();
            Assert.DoesNotContain(buttons, button => Equals(button.ToolTip, "自动隐藏"));

            Assert.Single(buttons.OfType<ToggleButton>());

            // 菜单项住在 Popup 里,活体可视树要等弹出才有,且是否弹出受焦点影响;
            // 直接把模板实例化一份来查,结果稳定且与运行顺序无关。
            var declared = (FrameworkElement)actions.Template.LoadContent();
            var menuItems = FindLogicalDescendants<MenuItem>(declared).ToList();
            Assert.Contains(menuItems, item => Equals(item.Header, "自动隐藏"));
            Assert.Contains(menuItems, item => Equals(item.Header, "浮动"));
        });
    }

    [Fact]
    public void ThemeCommandSwitchesTokensAndPersistsChoice()
    {
        var settings = new MemorySettings();
        RunShell(
            window =>
            {
                // UI-08:浅色是默认,深色经指令切换并落设置
                var light = (SolidColorBrush)window.FindResource("Shell.Brush.Canvas");
                Assert.Equal(Colors.White, ((SolidColorBrush)window.FindResource("Shell.Brush.Surface")).Color);

                window.Commands.ExecuteAsync("app.theme mode=dark", "test").GetAwaiter().GetResult();
                PumpDispatcher();

                var dark = (SolidColorBrush)window.FindResource("Shell.Brush.Canvas");
                Assert.NotEqual(light.Color, dark.Color);
                Assert.True(dark.Color.R < 0x40 && dark.Color.G < 0x40 && dark.Color.B < 0x40,
                    $"dark canvas should be dark, got {dark.Color}");
                Assert.Equal("dark", settings.Get("ui.theme"));

                // 主题色是暗黄:R > B 且明显偏暖
                var accent = ((SolidColorBrush)window.FindResource("Shell.Brush.Accent")).Color;
                Assert.True(accent.R > accent.B + 0x40, $"accent should be amber, got {accent}");

                window.Commands.ExecuteAsync("app.theme mode=light", "test").GetAwaiter().GetResult();
                PumpDispatcher();
                Assert.Equal(light.Color, ((SolidColorBrush)window.FindResource("Shell.Brush.Canvas")).Color);
                Assert.Equal("light", settings.Get("ui.theme"));
            },
            settings: settings);
    }

    [Fact]
    public void CommandDetailHasNoRedundantIdentityLine()
    {
        RunShell(window =>
        {
            var detail = Assert.Single(FindVisualDescendants<HistoryVulcan.Shell.Views.CommandDetailView>(window));
            Assert.Null(detail.FindName("DetailTitle"));
        });
    }

    [Fact]
    public void ThemeChoiceSurvivesSettingsAndWindowRecreation()
    {
        var appName = $"HistoryVulcan.Theme.Tests.{Guid.NewGuid():N}";
        var paths = new AppPaths(appName);
        try
        {
            var firstSettings = new SettingsService(paths);
            RunShell(
                window =>
                {
                    var result = window.Commands.ExecuteAsync("app.theme mode=dark", "test")
                        .GetAwaiter().GetResult();
                    Assert.True(result.Success, result.Message);
                },
                settings: firstSettings);

            var reloadedSettings = new SettingsService(paths);
            Assert.Equal("dark", reloadedSettings.Get("ui.theme"));
            RunShell(
                window =>
                {
                    var canvas = ((SolidColorBrush)window.FindResource("Shell.Brush.Canvas")).Color;
                    Assert.True(canvas.R < 0x40 && canvas.G < 0x40 && canvas.B < 0x40,
                        $"recreated window did not restore dark theme: {canvas}");
                },
                settings: reloadedSettings);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void TitleBarKeepsMenuButtonImmediatelyLeftOfThreeWindowButtons()
    {
        RunShell(window =>
        {
            // UI-02.3:菜单 + 最小化 + 最大化还原 + 关闭,次序固定且都可命中
            var menu = RequireButton(window, "MenuButton");
            var minimize = RequireButton(window, "MinimizeButton");
            var maximize = RequireButton(window, "MaximizeButton");
            var close = RequireButton(window, "CloseButton");

            foreach (var button in new[] { menu, minimize, maximize, close })
            {
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
                Assert.True(button.IsHitTestVisible);
                Assert.True(button.ActualWidth > 0, $"{button.Name} width={button.ActualWidth}");
            }

            // 次序:菜单紧贴最小化左侧,三个窗口按钮依次在右
            var bar = RequireElement<Panel>(window, "ChromeBar");
            var order = bar.Children.OfType<Button>().ToList();
            var menuIndex = order.IndexOf(menu);
            Assert.True(menuIndex >= 0);
            Assert.Equal(menuIndex + 1, order.IndexOf(minimize));
            Assert.Equal(menuIndex + 2, order.IndexOf(maximize));
            Assert.Equal(menuIndex + 3, order.IndexOf(close));
        });
    }

    [Fact]
    public void FoldedMenuKeepsAllGroupsAndConsumerToolActions()
    {
        RunShell(
            window =>
            {
                // UI-03.2:折叠后的菜单内容与 3.0.3 菜单栏一致
                var menu = RequireButton(window, "MenuButton").ContextMenu;
                Assert.NotNull(menu);
                var headers = menu!.Items.OfType<MenuItem>().Select(item => item.Header.ToString()).ToList();
                Assert.Equal(["文件(_F)", "编辑(_E)", "视图(_V)", "工具(_T)", "帮助(_H)"], headers);

                var tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "工具(_T)"));
                Assert.Contains(
                    tools.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, "消费方入口"));
            },
            configure: config => config.ToolMenuActions.Add(new ShellMenuAction("消费方入口", "app.about")));
    }

    [Fact]
    public void MaximizedPageMovesTheSingleMainChromeIntoTheFocusedHeader()
    {
        RunShell(window =>
        {
            var exitFocus = RequireButton(window, "ExitFocusButton");
            Assert.Equal(Visibility.Collapsed, exitFocus.Visibility);
            Assert.Equal(
                Visibility.Visible,
                Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(window)).Visibility);

            // Focused pages keep their real tab and host the one shared main-window chrome.
            window.Docking.MaximizeWindow(StandardWindowIds.Mcp);
            PumpDispatcher();

            Assert.Equal(
                Visibility.Visible,
                Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(window)).Visibility);
            Assert.Empty(FindVisualDescendants<AnchorablePaneTitle>(window));
            Assert.Equal(Visibility.Visible, exitFocus.Visibility);
            var chromeBar = RequireElement<Panel>(window, "ChromeBar");
            Assert.NotNull(VisualTreeHelper.GetParent(chromeBar));
            Assert.Single(FindVisualDescendants<Panel>(window), panel => panel.Name == "ChromeBar");
            Assert.All(
                new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" },
                name => Assert.Equal(Visibility.Visible, RequireButton(window, name).Visibility));

            Assert.True(window.Commands.ExecuteAsync("win.restore", "Test").GetAwaiter().GetResult().Success);
            PumpDispatcher();

            Assert.Null(window.Docking.MaximizedId);
            Assert.Equal(
                Visibility.Visible,
                Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(window)).Visibility);
            Assert.Equal(Visibility.Collapsed, exitFocus.Visibility);
        });
    }

    [Fact]
    public void NonStandardWindowStyleStillKeepsMenuAndWindowButtons()
    {
        // UI-09.1:宿主改过 WindowStyle 时只降级非客户区接管,顶栏内容不降级
        RunShell(
            window =>
            {
                Assert.Equal(WindowStyle.ToolWindow, window.WindowStyle);
                foreach (var name in new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" })
                {
                    var button = RequireButton(window, name);
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.True(button.IsEnabled);
                }
            },
            windowStyle: WindowStyle.ToolWindow);
    }

    [Fact]
    public void ErrorEntryRaisesTitleBarBadgeInsteadOfStatusBar()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                var badge = RequireButton(window, "ErrorBadge");
                Assert.Equal(Visibility.Collapsed, badge.Visibility);

                log.Raise(ShellLogLevel.Error, "test", "界面升级验证用错误");
                PumpDispatcher();

                // UI-05.3:计数与点击跳转从状态栏迁到顶栏徽章
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal("错误 1", badge.Content);
            },
            log: log);
    }

    [Fact]
    public void ConsoleToolbarOnlyShowsLevelAndDomain()
    {
        RunShell(window =>
        {
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            Assert.Equal(Visibility.Visible, Assert.IsType<ComboBox>(console.FindName("LevelFilter")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<ComboBox>(console.FindName("DomainFilter")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBox>(console.FindName("KeywordFilter")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<CheckBox>(console.FindName("MuteLayout")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<CheckBox>(console.FindName("AutoScroll")).Visibility);

            var status = window.Commands.ExecuteAsync("log.autoscroll", "Test").GetAwaiter().GetResult();
            Assert.True(status.Success, status.Message);
            Assert.Contains("True", status.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ConsoleAndCommandCatalogShareRegisteredDomainsAndRejectPrivateValues()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
                var consoleFilter = Assert.IsType<ComboBox>(console.FindName("DomainFilter"));
                window.Docking.Show(StandardWindowIds.Mcp);
                PumpDispatcher(600);
                var catalog = Assert.Single(FindVisualDescendants<HistoryVulcan.Shell.Views.McpToolsView>(window));
                var catalogFilter = Assert.IsType<ComboBox>(catalog.FindName("DomainFilterBox"));

                var consoleDomains = consoleFilter.Items.Cast<string>().ToList();
                var catalogDomains = catalogFilter.Items.Cast<string>().ToList();
                Assert.Equal(catalogDomains, consoleDomains);
                Assert.Contains("HistoryVulcan", consoleDomains);
                Assert.Equal(consoleDomains.Skip(1).OrderBy(value => value, StringComparer.Ordinal),
                    consoleDomains.Skip(1));
                Assert.Equal(consoleDomains.Count,
                    consoleDomains.Distinct(StringComparer.OrdinalIgnoreCase).Count());

                log.Raise(ShellLogLevel.Info, "private.worker", "private-log");
                PumpDispatcher();
                Assert.DoesNotContain("private", consoleFilter.Items.Cast<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var output = Assert.IsType<ListBox>(console.FindName("Output"));
                Assert.Contains(output.Items.Cast<ConsoleRow>(), row =>
                    row.Text.Contains("private-log", StringComparison.Ordinal));

                Assert.True(console.TrySetSource("HistoryVulcan", out _));
                catalogFilter.SelectedItem = "HistoryVulcan";
                window.Commands.Registry.Register(new CommandDescriptor
                {
                    Name = "zeta.sample",
                    Domain = "Fixture",
                    CommandClass = "sample",
                    Summary = "动态域同步测试",
                    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
                }, "test");
                PumpDispatcher(600);
                Assert.Contains("Fixture", consoleFilter.Items.Cast<string>());
                Assert.Contains("Fixture", catalogFilter.Items.Cast<string>());
                Assert.Equal("HistoryVulcan", consoleFilter.SelectedItem);
                Assert.Equal("HistoryVulcan", catalogFilter.SelectedItem);

                Assert.True(window.Commands.Registry.Unregister("zeta.sample"));
                PumpDispatcher(600);
                Assert.DoesNotContain("Fixture", consoleFilter.Items.Cast<string>());
                Assert.DoesNotContain("Fixture", catalogFilter.Items.Cast<string>());
                Assert.Equal("HistoryVulcan", consoleFilter.SelectedItem);
                Assert.Equal("HistoryVulcan", catalogFilter.SelectedItem);
                Assert.Equal(catalogFilter.Items.Cast<string>(), consoleFilter.Items.Cast<string>());

                var rejected = window.Commands.ExecuteAsync("log.source source=missing-domain", "Test")
                    .GetAwaiter().GetResult();
                Assert.False(rejected.Success);
                Assert.Contains("可用域", rejected.Message, StringComparison.Ordinal);
                var parameter = window.Commands.Registry.All()
                    .Single(command => command.Name == "log.source").Parameters.Single();
                Assert.Null(parameter.AllowedValues);
            },
            log: log);
    }

    [Fact]
    public void NonFocusedConsoleInputFiltersCentralCatalogWithoutSearchBoxOrMaximizing()
    {
        RunShell(window =>
        {
            window.Commands.Registry.Register(new CommandDescriptor
            {
                Name = "test.catalog.alpha",
                Summary = "catalog-choice summary-needle",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("alpha")),
            }, "test");
            window.Commands.Registry.Register(new CommandDescriptor
            {
                Name = "test.catalog.beta",
                Summary = "catalog-choice",
                Example = "example-needle",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("beta")),
            }, "test");
            PumpDispatcher(600);

            var console = Assert.Single(FindVisualDescendants<HistoryVulcan.Shell.Console.ConsoleView>(window));
            var input = Assert.Single(FindVisualDescendants<TextBox>(console), item => item.Name == "Input");
            var catalog = Assert.Single(FindVisualDescendants<HistoryVulcan.Shell.Views.McpToolsView>(window));
            var list = Assert.IsType<ListView>(catalog.FindName("ToolList"));
            Assert.Null(catalog.FindName("SearchBox"));
            Assert.DoesNotContain(FindVisualDescendants<TextBlock>(catalog), item => item.Text == "搜索");

            Keyboard.Focus(input);
            input.Text = "summary-needle";
            input.CaretIndex = input.Text.Length;
            PumpDispatcher(600);

            Assert.Null(window.Docking.MaximizedId);
            Assert.Equal("summary-needle", input.Text);
            Assert.False(list.IsKeyboardFocusWithin);
            Assert.Equal("test.catalog.alpha", Assert.Single(list.Items.Cast<CommandCatalogRow>()).CommandName);

            input.Text = "example-needle";
            input.CaretIndex = input.Text.Length;
            PumpDispatcher();
            Assert.Equal("test.catalog.beta", Assert.Single(list.Items.Cast<CommandCatalogRow>()).CommandName);

            input.Text = "catalog-choice";
            input.CaretIndex = input.Text.Length;
            PumpDispatcher();
            Assert.Equal(2, list.Items.Count);
            Assert.Equal("test.catalog.beta", ((CommandCatalogRow)list.SelectedItem).CommandName);
            Assert.True(console.HandleCompletionKey(Key.W, ModifierKeys.Shift));
            Assert.Equal("test.catalog.alpha", ((CommandCatalogRow)list.SelectedItem).CommandName);
            Assert.Equal("test.catalog.alpha", window.CommandSelection.CurrentCommandName);
            Assert.True(console.HandleCompletionKey(Key.S, ModifierKeys.Shift));
            Assert.Equal("test.catalog.beta", ((CommandCatalogRow)list.SelectedItem).CommandName);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.Equal("test.catalog.beta", input.Text);
            Assert.Equal("test.catalog.beta", Assert.Single(list.Items.Cast<CommandCatalogRow>()).CommandName);

            input.Text = "catalog-no-match";
            input.CaretIndex = input.Text.Length;
            PumpDispatcher();
            Assert.Empty(list.Items);
            Assert.False(console.HandleCompletionKey(Key.S, ModifierKeys.Shift));
            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.Equal("catalog-no-match", input.Text);

            input.Text = "";
            PumpDispatcher();
            Assert.NotEmpty(list.Items);
        });
    }

    [Fact]
    public void CommandCatalogUsesDomainAndClassFiltersWithoutRetiredControls()
    {
        RunShell(window =>
        {
            window.Docking.Show(StandardWindowIds.Mcp);
            PumpDispatcher(600);
            var view = Assert.Single(FindVisualDescendants<HistoryVulcan.Shell.Views.McpToolsView>(window));
            Assert.NotNull(view.FindName("DomainFilterBox"));
            Assert.NotNull(view.FindName("ClassFilterBox"));
            Assert.Null(view.FindName("SourceFilterBox"));
            Assert.Null(view.FindName("CustomizedOnlyCheck"));
            Assert.Null(view.FindName("PendingOnlyCheck"));
            Assert.Null(view.FindName("IncidentOnlyCheck"));
            Assert.Null(view.FindName("McpStatusButton"));

            var list = Assert.IsType<ListView>(view.FindName("ToolList"));
            var grid = Assert.IsType<GridView>(list.View);
            Assert.Equal(new[] { "指令", "域", "类", "MCP", "参数", "说明" },
                grid.Columns.Select(column => column.Header?.ToString()));

            var domainFilter = Assert.IsType<ComboBox>(view.FindName("DomainFilterBox"));
            var domains = domainFilter.Items.Cast<string>().ToList();
            Assert.Contains("HistoryVulcan", domains);
            Assert.Equal(domains.Count, domains.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            domainFilter.SelectedItem = "HistoryVulcan";
            PumpDispatcher();
            var classFilter = Assert.IsType<ComboBox>(view.FindName("ClassFilterBox"));
            Assert.Contains("win", classFilter.Items.Cast<string>());
            classFilter.SelectedItem = "win";
            PumpDispatcher();
            Assert.NotEmpty(list.Items);
            Assert.All(list.Items.Cast<HistoryVulcan.Shell.Mcp.CommandCatalogRow>(), row =>
            {
                Assert.Equal("HistoryVulcan", row.Domain, ignoreCase: true);
                Assert.Equal("win", row.CommandClass, ignoreCase: true);
            });
        });
    }

    [Fact]
    public void ConsoleLongLinesWrapAtCurrentWidthWithoutHorizontalExtentOrLogicalNewlines()
    {
        RunSta(() =>
        {
            var log = new RelayLog();
            var logicalText = new string('W', 320);
            log.Raise(ShellLogLevel.Info, "wrap.test", logicalText);
            var console = new ConsoleView(
                log,
                new CommandBus(new CommandRegistry(), log),
                new CommandHistory(Path.Combine(Path.GetTempPath(), $"HistoryVulcan-history-{Guid.NewGuid():N}.txt")));
            var host = new Window
            {
                Content = console,
                Width = 760,
                Height = 420,
                ShowInTaskbar = false,
            };
            var exportPath = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-console-{Guid.NewGuid():N}.txt");
            try
            {
                host.Show();
                PumpDispatcher();
                var output = Assert.IsType<ListBox>(console.FindName("Output"));
                var row = Assert.Single(output.Items.Cast<ConsoleRow>());
                output.ScrollIntoView(row);
                host.UpdateLayout();
                var container = Assert.IsType<ListBoxItem>(output.ItemContainerGenerator.ContainerFromItem(row));
                var text = Assert.Single(FindVisualDescendants<TextBlock>(container));
                var scroll = Assert.Single(FindVisualDescendants<ScrollViewer>(output));
                var horizontalBar = FindVisualDescendants<ScrollBar>(scroll)
                    .Single(bar => bar.Orientation == Orientation.Horizontal);
                var wideHeight = text.ActualHeight;

                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.True(scroll.CanContentScroll);
                Assert.Equal(ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(output));
                Assert.Equal(0, scroll.ScrollableWidth);
                Assert.Equal(Visibility.Collapsed, horizontalBar.Visibility);

                host.Width = 360;
                host.UpdateLayout();
                PumpDispatcher();
                var narrowHeight = text.ActualHeight;
                Assert.True(narrowHeight > wideHeight,
                    $"narrow={narrowHeight}, wide={wideHeight}");
                Assert.Equal(0, scroll.ScrollableWidth);

                host.Width = 760;
                host.UpdateLayout();
                PumpDispatcher();
                Assert.True(text.ActualHeight < narrowHeight,
                    $"rewidened={text.ActualHeight}, narrow={narrowHeight}");
                Assert.Equal(0, scroll.ScrollableWidth);
                Assert.DoesNotContain('\n', row.Text);

                output.SelectedItem = row;
                var copy = console.CopySelected();
                Assert.Contains("已复制", copy, StringComparison.Ordinal);
                Assert.Equal(row.Text, Clipboard.GetText());

                var export = console.ExportVisible(exportPath);
                Assert.Contains("已导出", export, StringComparison.Ordinal);
                var exported = Assert.Single(File.ReadAllLines(exportPath));
                Assert.Contains(logicalText, exported, StringComparison.Ordinal);
            }
            finally
            {
                host.Close();
                if (File.Exists(exportPath))
                    File.Delete(exportPath);
            }
        });
    }

    [Fact]
    public void CommandPipelineOwnsConsoleAndFormerDirectActions()
    {
        RunShell(window =>
        {
            var names = window.Commands.Registry.All().Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(
                new[]
                {
                    "log.level", "log.source", "log.keyword", "log.mute", "log.autoscroll",
                    "log.clear", "log.export", "log.copy", "log.focus", "cls",
                    "app.frontend.hide", "app.frontend.show", "app.frontend.focus-console", "app.frontend.exit",
                    "app.window", "win.autohide", "win.float-state", "command.copy-example",
                    "panel.select-file", "panel.select-directory",
                },
                name => Assert.Contains(name, names));
            Assert.DoesNotContain(names, name => name.StartsWith("res.", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.StartsWith("motor.", StringComparison.OrdinalIgnoreCase));

            Assert.True(window.Commands.ExecuteAsync("log.level level=error", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("log.source source=全部", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("log.keyword text=timeout", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("log.mute layout=true", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("log.autoscroll enabled=false", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("log.focus errors=true", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("log.clear", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("cls", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync($"win.autohide name={StandardWindowIds.Console}", "Test")
                .GetAwaiter().GetResult().Success);
        });
    }

    [Fact]
    public void HistoryVulcanOwnsTheSingleModulesManagementWindow()
    {
        RunShell(
            window => Assert.Single(window.Docking.ListWindows(), item => item.Id == StandardWindowIds.Modules),
            configure: config => config.EnableModules = true);
    }

    // ---------------------------------------------------------------- 宿主

    private static void RunShell(
        Action<ShellWindow> assert,
        Action<ShellConfig>? configure = null,
        WindowStyle windowStyle = WindowStyle.SingleBorderWindow,
        IShellLog? log = null,
        ISettingsService? settings = null)
    {
        RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-chrome-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var config = new ShellConfig
            {
                AppName = "HistoryVulcan Chrome Test",
                AppVersion = "3.1.1",
            };
            configure?.Invoke(config);

            var window = new ShellWindow(
                config,
                new MemoryLayoutStore(),
                log ?? new NullLog(),
                settings ?? new MemorySettings(),
                dataDirectory)
            {
                Width = 1000,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = windowStyle,
            };

            try
            {
                window.Show();
                PumpDispatcher();
                assert(window);
            }
            finally
            {
                window.Close();
                Directory.Delete(dataDirectory, recursive: true);
            }
        });
    }

    private static Button RequireButton(ShellWindow window, string name)
        => RequireElement<Button>(window, name);

    private static bool HasKeyboardOrLogicalFocus(FrameworkElement element)
        => element.IsKeyboardFocusWithin
           || ReferenceEquals(
               FocusManager.GetFocusedElement(FocusManager.GetFocusScope(element)),
               element);

    private static T RequireElement<T>(ShellWindow window, string name)
        where T : FrameworkElement
    {
        var element = window.FindName(name);
        Assert.NotNull(element);
        return Assert.IsAssignableFrom<T>(element);
    }

    private static object? GetPrivateField(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance);
    }

    private static T? FindAncestor<T>(DependencyObject element, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match && predicate(match))
                return match;
        }

        return null;
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
                yield return match;
            foreach (var descendant in FindLogicalDescendants<T>(child))
                yield return descendant;
        }
    }

    private static Color SamplePixel(FrameworkElement element, double relativeX, double relativeY)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var x = Math.Clamp((int)(width * relativeX), 0, width - 1);
        var y = Math.Clamp((int)(height * relativeY), 0, height - 1);
        var pixels = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static void PumpDispatcher(int milliseconds = 300)
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

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public void ReplaceCurrent(string oldValue, string newValue)
            => _current = _current?.Replace(oldValue, newValue, StringComparison.Ordinal);
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    /// <summary>可主动触发 EntryAdded 的日志,用于验证错误徽章。</summary>
    private sealed class RelayLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = new();

        public void Log(ShellLogLevel level, string category, string message)
            => Raise(level, category, message);

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;

        public void Raise(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
