using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AppShell.Core.Modules;

namespace ActiveDock;

/// <summary>
/// 活动坞界面生命周期。窗口自持、不依赖宿主窗口，因此只在无窗服务宿主中建窗。
/// </summary>
/// <remarks>
/// 宿主判据：模块宿主只在 <c>ShellUi</c> 非空时给 <see cref="IShellUiAware"/> 实例注入注册器，
/// 而注入发生在实例化阶段、早于 <see cref="CreateUi"/>。桌面 Shell 恒注入，无窗服务宿主恒不注入，
/// 因此 setter 是否被调用可确定性地区分两侧，不依赖启动顺序。本模块不使用注册器本身。
/// </remarks>
public sealed class ActiveDockUiModule : IUiModule, IShellUiAware
{
    private static DockWindow? _window;
    private IShellUiRegistrar? _shellUi;
    private IDisposable? _managerWindow;

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    public void CreateUi()
    {
        ActiveDockState.StartWatching();

        // 桌面 Shell 侧承载扩展坞管理页面，服务宿主侧承载活动坞本体。
        if (_shellUi != null)
        {
            _managerWindow ??= _shellUi.RegisterToolWindow(DockManagerView.CreateDescriptor(), "ActiveDock");
            return;
        }

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
        _managerWindow?.Dispose();
        _managerWindow = null;
        _window?.Close();
        _window = null;
    }

    private sealed class DockWindow : Window
    {
        private readonly WrapPanel _items = new() { Orientation = Orientation.Horizontal };
        private HwndSource? _source;
        private bool _resizing;
        private bool _anchorPending;

        public DockWindow()
        {
            var (width, height) = ActiveDockState.Size;
            Width = width;
            Height = height;
            MinWidth = DockLayout.MinWidth;
            MaxWidth = DockLayout.MaxWidth;
            MinHeight = DockLayout.MinHeight;
            MaxHeight = DockLayout.MaxHeight;
            SizeToContent = SizeToContent.Manual;
            WindowStyle = WindowStyle.None;
            // 无边框分层窗口没有原生非客户区，调整边框由 WM_NCHITTEST 自行给出。
            ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;

            var border = new Border
            {
                Background = DockTheme.PanelBackground,
                BorderBrush = DockTheme.PanelBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _items,
                },
            };
            Content = border;

            SourceInitialized += OnSourceInitialized;
            Loaded += (_, _) => ApplyAnchor();
            SizeChanged += OnSizeChanged;
            ActiveDockState.Changed += OnChanged;
            Closed += (_, _) => ActiveDockState.Changed -= OnChanged;
            RefreshItems();
        }

        private void OnSourceInitialized(object? sender, EventArgs args)
        {
            _source = (HwndSource)PresentationSource.FromVisual(this)!;
            _source.AddHook(WindowProc);
            DesktopLayer.Prepare(_source.Handle);
            ApplyAnchor();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs args)
        {
            // 交互调整期间由系统的尺寸循环负责几何，结束时统一保存并贴合。
            if (_resizing)
                return;
            ActiveDockState.SaveSize(ActualWidth, ActualHeight);
            // 右下角是锚点：尺寸变了要按新尺寸重新贴合，左上角随之移动。
            ApplyAnchor();
        }

        private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmNcHitTest = 0x0084;
            const int WmDisplayChange = 0x007E;
            const int WmSettingChange = 0x001A;
            const int WmActivateApp = 0x001C;
            const int WmShowWindow = 0x0018;
            const int WmDpiChanged = 0x02E0;
            const int WmWindowPosChanging = 0x0046;
            const int WmEnterSizeMove = 0x0231;
            const int WmExitSizeMove = 0x0232;

            switch (message)
            {
                case WmNcHitTest:
                    {
                        // 重定父之后 WPF 的 Left/Top 不再是屏幕坐标，PointFromScreen 会算错，
                        // 因此一律以窗口真实屏幕矩形为基准，全程用物理像素。
                        if (!DesktopLayer.TryGetWindowRect(hwnd, out var left, out var top, out var width, out var height))
                            return IntPtr.Zero;
                        var screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                        var screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                        var scale = _source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                        var code = DockLayout.HitTest(
                            screenX - left,
                            screenY - top,
                            width,
                            height,
                            DockLayout.BorderWidth * scale);
                        if (code == DockLayout.HitNone)
                            return IntPtr.Zero;
                        handled = true;
                        return new IntPtr(code);
                    }

                case WmEnterSizeMove:
                    // 调整期间交给系统的尺寸循环，自己不要再 SetWindowPos，否则会和拖拽打架。
                    _resizing = true;
                    return IntPtr.Zero;

                case WmExitSizeMove:
                    _resizing = false;
                    ActiveDockState.SaveSize(ActualWidth, ActualHeight);
                    ApplyAnchor();
                    return IntPtr.Zero;

                case WmDisplayChange:
                case WmSettingChange:
                case WmActivateApp:
                case WmShowWindow:
                case WmDpiChanged:
                    QueueAnchor();
                    return IntPtr.Zero;

                case WmWindowPosChanging:
                    // 任何 z-order 变动都改写为插到最底，窗口因此永不遮挡其他页面，
                    // 同时仍是普通顶层窗口，鼠标输入完整。
                    DesktopLayer.PinToBottom(lParam);
                    return IntPtr.Zero;

                default:
                    return IntPtr.Zero;
            }
        }

        /// <summary>把窗口吸附到主显示器工作区右下角。</summary>
        private void ApplyAnchor()
        {
            if (_source != null && DesktopLayer.AnchorToPrimaryBottomRight(_source.Handle, (int)DockLayout.Margin))
                return;

            // 极少数 Win32 查询失败时仍保留 WPF 的旧降级路径，避免窗口失去可见位置。
            var (left, top) = DockLayout.Anchor(SystemParameters.WorkArea, ActualWidth, ActualHeight);
            Left = left;
            Top = top;
        }

        private void QueueAnchor()
        {
            if (_resizing || _anchorPending)
                return;

            _anchorPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _anchorPending = false;
                if (!_resizing)
                    ApplyAnchor();
            });
        }

        private void OnChanged()
            => Dispatcher.BeginInvoke(() =>
            {
                if (ActiveDockState.Hidden)
                    Hide();
                else
                {
                    Show();
                    QueueAnchor();
                }
                RefreshItems();
            });

        private void RefreshItems()
        {
            _items.Children.Clear();
            _items.Children.Add(OhsButton());

            var projects = ActiveDockState.Projects;
            // 光圈亮度按当前列表最大权重归一化，保证任何时候都有对比度。
            var maximum = projects.Count == 0 ? 0 : projects.Max(item => item.Weight);
            foreach (var project in projects)
                _items.Children.Add(ProjectButton(project, maximum));
            if (projects.Count == 0)
            {
                _items.Children.Add(new TextBlock
                {
                    Text = "暂无活动项目",
                    Foreground = DockTheme.Muted,
                    Margin = new Thickness(12),
                });
            }
        }

        /// <summary>OHS 入口：不参与排序、不发光、不可取消。</summary>
        private static Button OhsButton()
        {
            var glyph = new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x35, 0x5B, 0x7E)),
                Child = new TextBlock
                {
                    Text = "OHS",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var stack = new StackPanel { Width = 68 };
            stack.Children.Add(glyph);
            stack.Children.Add(new TextBlock
            {
                Text = "主界面",
                FontSize = 11,
                Foreground = DockTheme.Label,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var button = NakedButton(stack, "打开 OHS 主界面");
            button.Click += (_, _) => OhsLauncher.Open();
            return button;
        }

        private static Button ProjectButton(DockProject project, double maximum)
        {
            var image = new Image
            {
                Source = ProjectIconGenerator.Create(project.Name),
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
            };

            var opacity = DockWeight.GlowOpacity(project.Weight, maximum);
            var host = new Border { Child = image };
            if (opacity > 0)
            {
                // ShadowDepth=0 即纯发光而非投影；权重越高越亮。
                host.Effect = new DropShadowEffect
                {
                    Color = DockTheme.Glow,
                    ShadowDepth = 0,
                    Opacity = opacity,
                    BlurRadius = DockWeight.GlowBlur(project.Weight, maximum),
                };
            }

            var label = new TextBlock
            {
                Text = project.Number + (project.Pinned ? "  ·" : ""),
                FontSize = 11,
                Foreground = DockTheme.Label,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel { Width = 68 };
            stack.Children.Add(host);
            stack.Children.Add(label);
            var button = NakedButton(stack, project.Name);
            button.Click += (_, _) =>
            {
                ActiveDockState.RecordOpen(project.Name);
                Process.Start(new ProcessStartInfo(project.Path) { UseShellExecute = true });
            };
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

        /// <summary>无边框透明按钮，悬停时叠一层浅色半透明，深底上不会闪出白块。</summary>
        private static Button NakedButton(object content, string tooltip)
        {
            var template = new ControlTemplate(typeof(Button));
            var presenterHost = new FrameworkElementFactory(typeof(Border), "Surface");
            presenterHost.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            presenterHost.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            presenterHost.SetValue(Border.PaddingProperty, new Thickness(4));
            presenterHost.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = presenterHost;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, DockTheme.Hover, "Surface"));
            template.Triggers.Add(hover);

            return new Button
            {
                Content = content,
                Width = 78,
                Height = 82,
                Margin = new Thickness(3),
                Template = template,
                ToolTip = tooltip,
            };
        }
    }
}
