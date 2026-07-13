using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace AppShell.App.CoffeeMachine;

public sealed class BrewControlView : UserControl
{
    private readonly BrewMachineState _state;
    private readonly DeviceSerialService _device;
    private readonly BrewPersistenceService _persistence;
    private TextBlock _status = null!;
    private TextBlock _direction = null!;
    private TextBlock _interlock = null!;
    private TextBlock _count = null!;
    private TextBlock _timeline = null!;
    private TextBlock _progressText = null!;
    private TextBlock _validation = null!;
    private ProgressBar _progress = null!;
    private Button _upButton = null!;
    private Button _downButton = null!;
    private Button _stopButton = null!;
    private Button _startButton = null!;
    private Button _pauseButton = null!;
    private Button _resumeButton = null!;
    private Button _abortButton = null!;
    private TextBox _upSeconds = null!;
    private TextBox _upDwellSeconds = null!;
    private TextBox _downSeconds = null!;
    private TextBox _downDwellSeconds = null!;
    private TextBox _cycles = null!;
    private TextBox _recipeName = null!;
    private ComboBox _recipes = null!;
    private ListBox _history = null!;
    private ComboBox _ports = null!;
    private CheckBox _deviceMode = null!;
    private TextBlock _connection = null!;
    private TextBlock _deviceMessage = null!;

    public BrewControlView(BrewMachineState state, DeviceSerialService device, BrewPersistenceService persistence)
    {
        _state = state;
        _device = device;
        _persistence = persistence;
        MinWidth = 300;

        var root = new DockPanel
        {
            LastChildFill = true,
            Background = Brushes.White,
            Margin = new Thickness(0),
        };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildBody(),
        };
        root.Children.Add(scroller);
        Content = root;

        _state.PropertyChanged += OnStateChanged;
        Update();
    }

    private StackPanel BuildBody()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(16),
        };

        body.Children.Add(new TextBlock
        {
            Text = "控制与编排",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(34, 32, 30)),
        });

        _status = BuildMetric("待机", 24, FontWeights.Bold);
        _direction = BuildMetric("无流动", 13, FontWeights.Normal);
        body.Children.Add(_status);
        body.Children.Add(_direction);

        body.Children.Add(BuildSectionTitle("设备连接"));
        var deviceGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        deviceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        deviceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ports = new ComboBox
        {
            Height = 32,
            Margin = new Thickness(0, 0, 8, 8),
        };
        Grid.SetColumn(_ports, 0);
        deviceGrid.Children.Add(_ports);
        var refresh = BuildSmallButton("刷新");
        refresh.Click += (_, _) => RefreshPorts();
        Grid.SetColumn(refresh, 1);
        deviceGrid.Children.Add(refresh);
        body.Children.Add(deviceGrid);

        var connectButtons = new UniformGrid { Columns = 2 };
        var connect = BuildButton("连接", Color.FromRgb(37, 117, 79));
        var disconnect = BuildButton("断开", Color.FromRgb(96, 96, 96));
        connect.Click += (_, _) => ConnectSelectedPort();
        disconnect.Click += (_, _) => _validation.Text = _device.Disconnect();
        connectButtons.Children.Add(connect);
        connectButtons.Children.Add(disconnect);
        body.Children.Add(connectButtons);

        _deviceMode = new CheckBox
        {
            Content = "设备模式",
            Margin = new Thickness(0, 0, 0, 6),
        };
        _deviceMode.Checked += (_, _) => _state.SetDeviceMode(true);
        _deviceMode.Unchecked += (_, _) => _state.SetDeviceMode(false);
        body.Children.Add(_deviceMode);

        _connection = BuildMetric("离线", 13, FontWeights.SemiBold);
        _deviceMessage = BuildMetric("模拟模式", 12, FontWeights.Normal);
        body.Children.Add(_connection);
        body.Children.Add(_deviceMessage);

        body.Children.Add(BuildSectionTitle("手动控制"));
        var manual = new UniformGrid { Columns = 1 };
        _upButton = BuildButton("上液", Color.FromRgb(128, 45, 38));
        _downButton = BuildButton("降液", Color.FromRgb(62, 92, 128));
        _stopButton = BuildButton("停止", Color.FromRgb(96, 96, 96));
        _upButton.Click += (_, _) => _validation.Text = ExecuteManual("up");
        _downButton.Click += (_, _) => _validation.Text = ExecuteManual("down");
        _stopButton.Click += (_, _) => _validation.Text = ExecuteManual("stop");
        manual.Children.Add(_upButton);
        manual.Children.Add(_downButton);
        manual.Children.Add(_stopButton);
        body.Children.Add(manual);

        body.Children.Add(BuildSectionTitle("固定循环"));
        var form = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        AddFormRow(form, 0, "上液秒数", _upSeconds = BuildInput("30"));
        AddFormRow(form, 1, "上液停留", _upDwellSeconds = BuildInput("5"));
        AddFormRow(form, 2, "降液秒数", _downSeconds = BuildInput("30"));
        AddFormRow(form, 3, "降液停留", _downDwellSeconds = BuildInput("5"));
        AddFormRow(form, 4, "循环次数", _cycles = BuildInput("3"));
        body.Children.Add(form);

        body.Children.Add(BuildSectionTitle("命名配方"));
        _recipeName = BuildInput("默认冷萃");
        _recipeName.HorizontalContentAlignment = HorizontalAlignment.Left;
        body.Children.Add(_recipeName);
        _recipes = new ComboBox
        {
            Height = 32,
            Margin = new Thickness(0, 0, 0, 8),
            DisplayMemberPath = nameof(BrewRecipePreset.Summary),
        };
        body.Children.Add(_recipes);
        var recipeButtons = new UniformGrid { Columns = 3 };
        var saveRecipe = BuildSmallButton("保存");
        var loadRecipe = BuildSmallButton("载入");
        var deleteRecipe = BuildSmallButton("删除");
        saveRecipe.Click += (_, _) => SaveCurrentRecipe();
        loadRecipe.Click += (_, _) => LoadSelectedRecipe();
        deleteRecipe.Click += (_, _) => DeleteSelectedRecipe();
        recipeButtons.Children.Add(saveRecipe);
        recipeButtons.Children.Add(loadRecipe);
        recipeButtons.Children.Add(deleteRecipe);
        body.Children.Add(recipeButtons);

        var sequenceButtons = new UniformGrid { Columns = 2 };
        _startButton = BuildButton("启动循环", Color.FromRgb(37, 117, 79));
        _pauseButton = BuildButton("暂停", Color.FromRgb(184, 124, 38));
        _resumeButton = BuildButton("继续", Color.FromRgb(62, 92, 128));
        _abortButton = BuildButton("终止", Color.FromRgb(150, 54, 54));
        _startButton.Click += (_, _) => StartSequenceFromInputs();
        _pauseButton.Click += (_, _) => _validation.Text = _state.PauseSequence();
        _resumeButton.Click += (_, _) => _validation.Text = _state.ResumeSequence();
        _abortButton.Click += (_, _) => _validation.Text = _state.AbortSequence();
        sequenceButtons.Children.Add(_startButton);
        sequenceButtons.Children.Add(_pauseButton);
        sequenceButtons.Children.Add(_resumeButton);
        sequenceButtons.Children.Add(_abortButton);
        body.Children.Add(sequenceButtons);

        _validation = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(94, 98, 102)),
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(_validation);

        body.Children.Add(BuildSectionTitle("时间线"));
        _timeline = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(_timeline);
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 14,
            Margin = new Thickness(0, 0, 0, 6),
        };
        body.Children.Add(_progress);
        _progressText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(94, 98, 102)),
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(_progressText);

        body.Children.Add(BuildSectionTitle("GPIO 状态"));
        _interlock = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 10),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(_interlock);
        body.Children.Add(BuildGpioTable());

        _count = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(94, 98, 102)),
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(_count);

        body.Children.Add(BuildSectionTitle("最近记录"));
        _history = new ListBox
        {
            MinHeight = 110,
            MaxHeight = 180,
            ItemsSource = _state.History,
            DisplayMemberPath = nameof(BrewHistoryEntry.DisplayText),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 223, 228)),
            BorderThickness = new Thickness(1),
        };
        body.Children.Add(_history);
        RefreshPorts();
        RefreshRecipes();
        return body;
    }

    private static TextBlock BuildSectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(48, 50, 52)),
        Margin = new Thickness(0, 18, 0, 8),
    };

    private static TextBlock BuildMetric(string text, double size, FontWeight weight) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = new SolidColorBrush(Color.FromRgb(54, 49, 45)),
        Margin = new Thickness(0, 8, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button BuildButton(string text, Color accent) => new()
    {
        Content = text,
        Height = 40,
        Margin = new Thickness(0, 0, 8, 8),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(accent),
        BorderBrush = new SolidColorBrush(accent),
        BorderThickness = new Thickness(1),
    };

    private static Button BuildSmallButton(string text) => new()
    {
        Content = text,
        Height = 32,
        MinWidth = 58,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static TextBox BuildInput(string text) => new()
    {
        Text = text,
        Height = 30,
        Margin = new Thickness(0, 0, 0, 6),
        HorizontalContentAlignment = HorizontalAlignment.Right,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static void AddFormRow(Grid form, int row, string label, TextBox input)
    {
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 75, 78)),
            Margin = new Thickness(0, 0, 12, 6),
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        form.Children.Add(text);

        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        form.Children.Add(input);
    }

    private DataGrid BuildGpioTable()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserResizeRows = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeaderWidth = 0,
            MinHeight = 120,
            MaxHeight = 170,
            ItemsSource = _state.Gpios,
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 223, 228)),
            BorderThickness = new Thickness(1),
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "引脚",
            Binding = new Binding(nameof(GpioLine.Pin)),
            Width = new DataGridLength(82),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "用途",
            Binding = new Binding(nameof(GpioLine.Purpose)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "状态",
            Binding = new Binding(nameof(GpioLine.StateText)),
            Width = new DataGridLength(74),
        });
        return grid;
    }

    private void StartSequenceFromInputs()
    {
        if (!TryBuildRecipe(out var recipe))
            return;

        _validation.Foreground = new SolidColorBrush(Color.FromRgb(94, 98, 102));
        _validation.Text = _state.StartSequence(recipe!);
    }

    private bool TryBuildRecipe(out BrewRecipe? recipe)
    {
        recipe = null;
        if (!TryReadInt(_upSeconds, out var up) ||
            !TryReadInt(_upDwellSeconds, out var upDwell) ||
            !TryReadInt(_downSeconds, out var down) ||
            !TryReadInt(_downDwellSeconds, out var downDwell) ||
            !TryReadInt(_cycles, out var cycles))
        {
            _validation.Foreground = new SolidColorBrush(Color.FromRgb(170, 35, 35));
            _validation.Text = "参数必须是整数秒数/次数";
            return false;
        }

        if (BrewRecipe.TryCreate(up, upDwell, down, downDwell, cycles, out recipe, out var error))
            return true;

        _validation.Foreground = new SolidColorBrush(Color.FromRgb(170, 35, 35));
        _validation.Text = error;
        return false;
    }

    private void SaveCurrentRecipe()
    {
        if (!TryBuildRecipe(out var recipe))
            return;

        try
        {
            var preset = _persistence.SaveRecipe(_recipeName.Text, recipe!);
            RefreshRecipes(preset.Name);
            _validation.Foreground = new SolidColorBrush(Color.FromRgb(36, 112, 75));
            _validation.Text = $"已保存配方：{preset.Name}";
        }
        catch (Exception ex)
        {
            _validation.Foreground = new SolidColorBrush(Color.FromRgb(170, 35, 35));
            _validation.Text = ex.Message;
        }
    }

    private void LoadSelectedRecipe()
    {
        if (_recipes.SelectedItem is not BrewRecipePreset preset)
        {
            _validation.Text = "请先选择一个配方";
            return;
        }

        ApplyRecipe(preset);
        _validation.Foreground = new SolidColorBrush(Color.FromRgb(94, 98, 102));
        _validation.Text = $"已载入配方：{preset.Name}";
    }

    private void DeleteSelectedRecipe()
    {
        if (_recipes.SelectedItem is not BrewRecipePreset preset)
        {
            _validation.Text = "请先选择一个配方";
            return;
        }

        _persistence.DeleteRecipe(preset.Name);
        RefreshRecipes();
        _validation.Foreground = new SolidColorBrush(Color.FromRgb(94, 98, 102));
        _validation.Text = $"已删除配方：{preset.Name}";
    }

    private void ApplyRecipe(BrewRecipePreset preset)
    {
        _recipeName.Text = preset.Name;
        _upSeconds.Text = preset.Recipe.UpSeconds.ToString(CultureInfo.InvariantCulture);
        _upDwellSeconds.Text = preset.Recipe.UpDwellSeconds.ToString(CultureInfo.InvariantCulture);
        _downSeconds.Text = preset.Recipe.DownSeconds.ToString(CultureInfo.InvariantCulture);
        _downDwellSeconds.Text = preset.Recipe.DownDwellSeconds.ToString(CultureInfo.InvariantCulture);
        _cycles.Text = preset.Recipe.Cycles.ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshRecipes(string? selectName = null)
    {
        var presets = _persistence.ReadRecipes();
        _recipes.ItemsSource = presets;
        if (presets.Count == 0)
            return;

        _recipes.SelectedItem = selectName == null
            ? presets[0]
            : presets.FirstOrDefault(p => p.Name.Equals(selectName, StringComparison.CurrentCultureIgnoreCase)) ?? presets[0];
    }

    private static bool TryReadInt(TextBox textBox, out int value)
        => int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private void RefreshPorts()
    {
        var selected = _ports.SelectedItem as string;
        _ports.ItemsSource = _device.ListPorts();
        if (selected != null && _ports.Items.Contains(selected))
            _ports.SelectedItem = selected;
        else if (_ports.Items.Count > 0)
            _ports.SelectedIndex = 0;
    }

    private void ConnectSelectedPort()
    {
        if (_ports.SelectedItem is not string portName || string.IsNullOrWhiteSpace(portName))
        {
            _validation.Text = "请选择串口";
            return;
        }

        _validation.Text = _device.Connect(portName);
    }

    private string ExecuteManual(string mode)
    {
        if (_state.IsDeviceMode)
            return _device.SendBrewMode(mode);

        return mode switch
        {
            "up" => _state.StartUp(),
            "down" => _state.StartDown(),
            _ => _state.Stop(),
        };
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        _status.Text = _state.StatusText;
        _direction.Text = _state.DirectionText;
        _connection.Text = $"{_state.RuntimeModeText} | {_state.ConnectionText} | 最近数据 {_state.LastDeviceDataText}";
        _deviceMessage.Text = _state.DeviceMessage;
        _deviceMode.IsChecked = _state.IsDeviceMode;
        _interlock.Text = _state.InterlockText;
        _interlock.Foreground = new SolidColorBrush(_state.Mode == BrewMode.Fault
            ? Color.FromRgb(170, 35, 35)
            : Color.FromRgb(36, 112, 75));
        _count.Text = $"切换次数：{_state.OperationCount} | 最近：{_state.LastChangedText}";
        _timeline.Text = _state.TimelineText;
        _progress.Value = _state.ProgressPercent;
        _progressText.Text = $"步骤 {_state.CompletedSteps}/{_state.TotalSteps} | 进度 {_state.ProgressPercent:0}% | 剩余 {_state.RemainingText}";

        var locked = _state.IsParameterLocked;
        _upSeconds.IsEnabled = !locked;
        _upDwellSeconds.IsEnabled = !locked;
        _downSeconds.IsEnabled = !locked;
        _downDwellSeconds.IsEnabled = !locked;
        _cycles.IsEnabled = !locked;
        _recipeName.IsEnabled = !locked;
        _recipes.IsEnabled = !locked;

        _upButton.IsEnabled = !_state.IsParameterLocked && _state.Mode != BrewMode.Up;
        _downButton.IsEnabled = !_state.IsParameterLocked && _state.Mode != BrewMode.Down;
        _stopButton.IsEnabled = _state.Mode != BrewMode.Idle || _state.IsSequenceActive;
        _startButton.IsEnabled = !_state.IsParameterLocked;
        _pauseButton.IsEnabled = _state.IsSequenceRunning;
        _resumeButton.IsEnabled = _state.IsSequencePaused;
        _abortButton.IsEnabled = _state.IsSequenceActive && !_state.IsSequencePaused || _state.IsSequencePaused;
    }
}
