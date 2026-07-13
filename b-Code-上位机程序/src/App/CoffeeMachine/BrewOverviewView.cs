using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AppShell.App.CoffeeMachine;

public sealed class BrewOverviewView : UserControl
{
    private readonly BrewMachineState _state;
    private readonly RowDefinition _upperEmpty;
    private readonly RowDefinition _upperFillRow;
    private readonly RowDefinition _lowerEmpty;
    private readonly RowDefinition _lowerFillRow;
    private TextBlock _status = null!;
    private TextBlock _stage = null!;
    private TextBlock _direction = null!;
    private readonly TextBlock _upperLevel;
    private readonly TextBlock _lowerLevel;
    private TextBlock _flowUp = null!;
    private TextBlock _flowDown = null!;
    private Border _pump = null!;

    public BrewOverviewView(BrewMachineState state)
    {
        _state = state;

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(247, 248, 249)),
            Margin = new Thickness(0),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var machine = new Grid
        {
            Margin = new Thickness(26, 10, 26, 24),
            MinWidth = 560,
        };
        machine.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.15, GridUnitType.Star) });
        machine.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.62, GridUnitType.Star) });
        machine.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.15, GridUnitType.Star) });

        var upper = BuildChamber("上舱体", out _upperEmpty, out _upperFillRow, out _upperLevel);
        Grid.SetRow(upper, 0);
        machine.Children.Add(upper);

        var bay = BuildElectricalBay();
        Grid.SetRow(bay, 1);
        machine.Children.Add(bay);

        var lower = BuildChamber("下舱体", out _lowerEmpty, out _lowerFillRow, out _lowerLevel);
        Grid.SetRow(lower, 2);
        machine.Children.Add(lower);

        Grid.SetRow(machine, 1);
        root.Children.Add(machine);

        Content = root;
        _state.PropertyChanged += OnStateChanged;
        Update();
    }

    private Border BuildHeader()
    {
        _status = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(36, 32, 30)),
        };
        _stage = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 92, 89)),
            Margin = new Thickness(0, 4, 0, 0),
        };
        _direction = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(128, 45, 38)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var text = new StackPanel();
        text.Children.Add(_status);
        text.Children.Add(_stage);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(_direction, 1);
        grid.Children.Add(_direction);

        return new Border
        {
            Padding = new Thickness(24, 18, 24, 16),
            BorderBrush = new SolidColorBrush(Color.FromRgb(222, 226, 230)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.White,
            Child = grid,
        };
    }

    private static Border BuildChamber(
        string title,
        out RowDefinition emptyRow,
        out RowDefinition fillRow,
        out TextBlock levelText)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });

        var label = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
        };
        label.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(42, 38, 35)),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        levelText = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(92, 38, 30)),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        label.Children.Add(levelText);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var fillGrid = new Grid();
        emptyRow = new RowDefinition();
        fillRow = new RowDefinition();
        fillGrid.RowDefinitions.Add(emptyRow);
        fillGrid.RowDefinitions.Add(fillRow);
        var liquid = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromRgb(111, 56, 37),
                Color.FromRgb(58, 27, 19),
                90),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
        };
        Grid.SetRow(liquid, 1);
        fillGrid.Children.Add(liquid);
        var glass = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            StrokeThickness = 2,
            RadiusX = 10,
            RadiusY = 10,
        };
        Grid.SetRowSpan(glass, 2);
        fillGrid.Children.Add(glass);

        var tank = new Border
        {
            Height = 190,
            MaxWidth = 360,
            BorderBrush = new SolidColorBrush(Color.FromRgb(125, 137, 148)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(86, 220, 232, 238)),
            Child = fillGrid,
        };
        Grid.SetColumn(tank, 1);
        grid.Children.Add(tank);

        var status = new Border
        {
            Width = 120,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(238, 241, 244)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(213, 219, 225)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(18, 0, 0, 0),
            Child = new TextBlock
            {
                Text = "液位监测",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 79, 82)),
            },
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 228, 232)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Child = grid,
        };
    }

    private Border BuildElectricalBay()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _flowUp = BuildFlowText("↑ 上液");
        _flowDown = BuildFlowText("↓ 降液");

        var flowStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        flowStack.Children.Add(_flowUp);
        flowStack.Children.Add(_flowDown);
        Grid.SetColumn(flowStack, 0);
        grid.Children.Add(flowStack);

        _pump = new Border
        {
            Width = 210,
            Height = 88,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "电气仓",
                        FontSize = 18,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = "GPIO15 / GPIO16 互锁输出",
                        Margin = new Thickness(0, 6, 0, 0),
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(92, 96, 100)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        Grid.SetColumn(_pump, 1);
        grid.Children.Add(_pump);

        var hint = new TextBlock
        {
            Text = "模拟模式",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 92, 89)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        };
        Grid.SetColumn(hint, 2);
        grid.Children.Add(hint);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(232, 235, 238)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(208, 214, 220)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 4, 0, 4),
            Child = grid,
        };
    }

    private static TextBlock BuildFlowText(string text) => new()
    {
        Text = text,
        Width = 96,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(128, 45, 38)),
        Opacity = 0.25,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        _status.Text = _state.StatusText;
        _stage.Text = $"{_state.StageText} | {_state.RuntimeModeText} | {_state.ConnectionText} | 最后变更 {_state.LastChangedText}";
        _direction.Text = _state.DirectionText;
        _upperLevel.Text = _state.UpperLevelText;
        _lowerLevel.Text = _state.LowerLevelText;

        ApplyLevel(_upperEmpty, _upperFillRow, _state.UpperLevel);
        ApplyLevel(_lowerEmpty, _lowerFillRow, _state.LowerLevel);

        _flowUp.Opacity = _state.Mode == BrewMode.Up ? 1 : 0.22;
        _flowDown.Opacity = _state.Mode == BrewMode.Down ? 1 : 0.22;
        _pump.Background = new SolidColorBrush(_state.IsRunning
            ? Color.FromRgb(255, 245, 226)
            : Color.FromRgb(246, 247, 248));
        _pump.BorderBrush = new SolidColorBrush(_state.IsRunning
            ? Color.FromRgb(201, 112, 56)
            : Color.FromRgb(198, 205, 212));
    }

    private static void ApplyLevel(RowDefinition emptyRow, RowDefinition fillRow, double level)
    {
        emptyRow.Height = new GridLength(Math.Max(0.1, 100 - level), GridUnitType.Star);
        fillRow.Height = new GridLength(Math.Max(0.1, level), GridUnitType.Star);
    }
}
