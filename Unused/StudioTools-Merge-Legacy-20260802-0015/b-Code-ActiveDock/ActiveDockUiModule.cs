using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppShell.Core.Modules;

namespace ActiveDock;

public sealed class ActiveDockUiModule : IUiModule
{
    private static DockWindow? _window;

    public void CreateUi()
    {
        if (_window != null)
            return;
        _window = new DockWindow();
        _window.Show();
        if (ActiveDockState.Hidden)
            _window.Hide();
        _ = ActiveDockState.RefreshAsync();
    }

    public void DestroyUi()
    {
        _window?.Close();
        _window = null;
    }

    private sealed class DockWindow : Window
    {
        private readonly WrapPanel _items = new() { Orientation = Orientation.Horizontal };

        public DockWindow()
        {
            Width = 360;
            MinHeight = 112;
            MaxHeight = 390;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(196, 201, 207)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Child = _items,
            };
            border.MouseLeftButtonDown += (_, args) =>
            {
                if (args.OriginalSource == border)
                    DragMove();
            };
            Content = border;

            Loaded += (_, _) => RestorePosition();
            LocationChanged += (_, _) => ActiveDockState.SavePosition(Left, Top);
            ActiveDockState.Changed += OnChanged;
            Closed += (_, _) => ActiveDockState.Changed -= OnChanged;
            RefreshItems();
        }

        private void OnChanged()
            => Dispatcher.BeginInvoke(() =>
            {
                if (ActiveDockState.Hidden)
                    Hide();
                else
                    Show();
                RefreshItems();
            });

        private void RefreshItems()
        {
            _items.Children.Clear();
            foreach (var project in ActiveDockState.Projects)
                _items.Children.Add(ProjectButton(project));
            if (_items.Children.Count == 0)
            {
                _items.Children.Add(new TextBlock
                {
                    Text = "暂无活动项目",
                    Foreground = Brushes.DimGray,
                    Margin = new Thickness(12),
                });
            }
        }

        private static Button ProjectButton(DockProject project)
        {
            var image = new Image
            {
                Source = ProjectIconGenerator.Create(project.Name),
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
            };
            var label = new TextBlock
            {
                Text = project.Number + (project.Pinned ? "  ·" : ""),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(42, 47, 52)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel { Width = 68 };
            stack.Children.Add(image);
            stack.Children.Add(label);
            var button = new Button
            {
                Content = stack,
                Width = 78,
                Height = 82,
                Margin = new Thickness(3),
                Padding = new Thickness(4),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                ToolTip = project.Name,
            };
            button.Click += (_, _) => Process.Start(new ProcessStartInfo(project.Path) { UseShellExecute = true });
            var menu = new ContextMenu();
            var pin = new MenuItem { Header = project.Pinned ? "取消置顶" : "置顶" };
            pin.Click += (_, _) => ActiveDockState.Pin(project.Name, !project.Pinned);
            var refresh = new MenuItem { Header = "刷新" };
            refresh.Click += async (_, _) => await ActiveDockState.RefreshAsync();
            menu.Items.Add(pin);
            menu.Items.Add(refresh);
            button.ContextMenu = menu;
            return button;
        }

        private void RestorePosition()
        {
            var (left, top) = ActiveDockState.Position;
            var work = SystemParameters.WorkArea;
            Left = left is { } x && x >= work.Left && x < work.Right - 40
                ? x
                : work.Right - ActualWidth - 16;
            Top = top is { } y && y >= work.Top && y < work.Bottom - 40
                ? y
                : work.Bottom - ActualHeight - 16;
        }
    }
}
