using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using HistoryVulcan.Core.Docking;

namespace ActiveDock;

/// <summary>
/// 扩展坞管理页面，停靠在 OHS 主窗口右侧。
/// </summary>
/// <remarks>
/// 本页面运行在桌面 Shell 进程，活动坞本体运行在服务宿主进程，两者不共享内存。
/// 所有写操作落到 state.json，由 <see cref="ActiveDockState.StartWatching"/> 的文件监视完成跨进程同步；
/// 不得假设改完内存对面就能看到。
/// </remarks>
public static class DockManagerView
{
    public static ToolWindowDescriptor CreateDescriptor() => new()
    {
        Id = "dock.manager",
        Title = "扩展坞管理",
        DefaultSide = DockSide.Right,
        DefaultRatio = 0.38,
        IsSingleton = true,
        ContentFactory = static () => new ManagerPage(),
    };

    private sealed class ManagerPage : UserControl
    {
        private readonly ListView _list = new();
        private readonly TextBox _minItems = new() { Width = 56 };
        private readonly TextBox _maxItems = new() { Width = 56 };
        private readonly TextBox _halfLife = new() { Width = 56 };

        public ManagerPage()
        {
            var root = new DockPanel
            {
                Margin = new Thickness(12),
            };
            root.SetValue(TextElement.FontFamilyProperty, DockTheme.FontFamily);
            root.SetValue(TextElement.FontSizeProperty, DockTheme.BodyFontSize);
            ConfigureInput(_minItems);
            ConfigureInput(_maxItems);
            ConfigureInput(_halfLife);

            var policyBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            policyBar.Children.Add(new TextBlock
            {
                Text = "最少显示",
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.BodyFontSize,
                Foreground = DockTheme.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            policyBar.Children.Add(_minItems);
            policyBar.Children.Add(Gap("最多显示"));
            policyBar.Children.Add(_maxItems);
            policyBar.Children.Add(Gap("半衰期(天)"));
            policyBar.Children.Add(_halfLife);

            var apply = new Button { Content = "保存策略", Margin = new Thickness(10, 0, 0, 0) };
            DockTheme.StyleButton(apply, accent: true);
            apply.Click += (_, _) => ApplyPolicy();
            policyBar.Children.Add(apply);
            DockPanel.SetDock(policyBar, Dock.Top);
            root.Children.Add(policyBar);

            var actionBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            actionBar.Children.Add(Action("固定", () => Selected(row => ActiveDockState.Pin(row.Name, true))));
            actionBar.Children.Add(Action("取消固定", () => Selected(row => ActiveDockState.Pin(row.Name, false))));
            actionBar.Children.Add(Action("排除", () => Selected(row => ActiveDockState.Exclude(row.Name, true))));
            actionBar.Children.Add(Action("恢复收录", () => Selected(row => ActiveDockState.Exclude(row.Name, false))));
            actionBar.Children.Add(Action("清除该项记录", () => Selected(row => ActiveDockState.Forget(row.Name))));
            actionBar.Children.Add(Action("清除全部记录", () => ActiveDockState.Forget(null)));
            actionBar.Children.Add(Action("刷新", () => _ = ActiveDockState.RefreshAsync()));
            DockPanel.SetDock(actionBar, Dock.Top);
            root.Children.Add(actionBar);

            root.Children.Add(new TextBlock
            {
                Text = "固定项无论打开次数如何都会显示；权重按半衰期衰减，光圈亮度随权重变化。",
                TextWrapping = TextWrapping.Wrap,
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Foreground = DockTheme.Muted,
                Margin = new Thickness(0, 0, 0, 6),
            });
            DockPanel.SetDock(root.Children[^1], Dock.Bottom);

            _list.View = BuildColumns();
            _list.Background = DockTheme.SurfaceAlt;
            _list.BorderBrush = DockTheme.PanelBorder;
            _list.BorderThickness = new Thickness(1);
            _list.Foreground = DockTheme.Label;
            _list.FontFamily = DockTheme.FontFamily;
            _list.FontSize = DockTheme.BodyFontSize;
            _list.Resources[SystemColors.HighlightBrushKey] = DockTheme.AccentSoft;
            _list.Resources[SystemColors.HighlightTextBrushKey] = DockTheme.Label;
            root.Children.Add(_list);
            Content = root;

            ActiveDockState.StartWatching();
            ActiveDockState.Changed += OnChanged;
            Unloaded += (_, _) => ActiveDockState.Changed -= OnChanged;
            Loaded += (_, _) =>
            {
                LoadPolicy();
                Reload();
                _ = ActiveDockState.RefreshAsync();
            };
        }

        private static GridView BuildColumns()
        {
            var view = new GridView();
            view.Columns.Add(Column("项目", nameof(DockProject.Name), 150));
            view.Columns.Add(Column("权重", nameof(DockProject.Weight), 60, "F2"));
            view.Columns.Add(Column("点击", nameof(DockProject.Clicks), 50, "F1"));
            view.Columns.Add(Column("最近打开", nameof(DockProject.LastOpened), 120, "yyyy-MM-dd HH:mm"));
            view.Columns.Add(Column("固定", nameof(DockProject.Pinned), 44));
            view.Columns.Add(Column("排除", nameof(DockProject.Excluded), 44));
            return view;
        }

        private static GridViewColumn Column(string header, string path, double width, string? format = null)
            => new()
            {
                Header = header,
                Width = width,
                DisplayMemberBinding = new Binding(path)
                {
                    StringFormat = format,
                    ConverterCulture = CultureInfo.CurrentCulture,
                },
            };

        private static UIElement Gap(string text) => new TextBlock
        {
            Text = text,
            FontFamily = DockTheme.FontFamily,
            FontSize = DockTheme.BodyFontSize,
            Foreground = DockTheme.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
        };

        private static Button Action(string text, Action run)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
            DockTheme.StyleButton(button);
            button.Click += (_, _) => run();
            return button;
        }

        private static void ConfigureInput(TextBox input)
        {
            input.Height = 28;
            input.Padding = DockTheme.ControlPadding;
            input.FontFamily = DockTheme.FontFamily;
            input.FontSize = DockTheme.BodyFontSize;
            input.Foreground = DockTheme.Label;
            input.Background = DockTheme.SurfaceAlt;
            input.BorderBrush = DockTheme.PanelBorder;
            input.BorderThickness = new Thickness(1);
        }

        private void Selected(Action<DockProject> run)
        {
            if (_list.SelectedItem is DockProject row)
                run(row);
        }

        private void OnChanged() => Dispatcher.BeginInvoke(() =>
        {
            LoadPolicy();
            Reload();
        });

        private void Reload()
        {
            var selected = (_list.SelectedItem as DockProject)?.Name;
            _list.ItemsSource = ActiveDockState.Projects;
            if (selected == null)
                return;
            foreach (var item in _list.Items)
            {
                if (item is DockProject row && row.Name == selected)
                {
                    _list.SelectedItem = item;
                    break;
                }
            }
        }

        private void LoadPolicy()
        {
            var policy = ActiveDockState.Policy;
            _minItems.Text = policy.MinItems.ToString(CultureInfo.InvariantCulture);
            _maxItems.Text = policy.MaxItems.ToString(CultureInfo.InvariantCulture);
            _halfLife.Text = policy.HalfLifeDays.ToString("G", CultureInfo.InvariantCulture);
        }

        private void ApplyPolicy()
        {
            int? min = int.TryParse(_minItems.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
            int? max = int.TryParse(_maxItems.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : null;
            double? half = double.TryParse(_halfLife.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : null;
            ActiveDockState.SetPolicy(min, max, half);
            LoadPolicy();
        }
    }
}
