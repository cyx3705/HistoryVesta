namespace b_Code_三维坐标机器人前端设计;

public sealed class GraphicalPanel : UserControl
{
    private readonly NumericUpDown _numX;
    private readonly NumericUpDown _numY;
    private readonly NumericUpDown _numZ;
    private readonly ComboBox _cmbInterpolateType;
    private readonly NumericUpDown _numSteps;
    private readonly NumericUpDown _numDistance;

    public event Action<string>? CommandRequested;
    public event Action? RunExampleRequested;

    public GraphicalPanel()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _numX = CreateNumeric(0, 500, 0);
        _numY = CreateNumeric(0, 500, 0);
        _numZ = CreateNumeric(0, 500, 0);
        _cmbInterpolateType = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbInterpolateType.Items.AddRange(["linear", "bezier", "spline"]);
        _cmbInterpolateType.SelectedIndex = 2;

        _numSteps = CreateNumeric(2, 500, 50);
        _numDistance = CreateNumeric(0.1m, 50, 2, 1);

        AddRow(layout, "控制点 X (mm)", _numX);
        AddRow(layout, "控制点 Y (mm)", _numY);
        AddRow(layout, "控制点 Z (mm)", _numZ);
        AddRow(layout, "插值类型", _cmbInterpolateType);
        AddRow(layout, "插值步数", _numSteps);
        AddRow(layout, "采样间距 (mm)", _numDistance);

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 12, 0, 0)
        };

        flow.Controls.Add(CreateButton("复位 Reset", () => Request("reset")));
        flow.Controls.Add(CreateButton("回零 Home", () => Request("home")));
        flow.Controls.Add(CreateButton("添加控制点", () =>
            Request($"add_point {_numX.Value} {_numY.Value} {_numZ.Value}")));
        flow.Controls.Add(CreateButton("清空控制点", () => Request("clear_points")));
        flow.Controls.Add(CreateButton("执行插值", () =>
            Request($"interpolate {_cmbInterpolateType.SelectedItem} {(int)_numSteps.Value}")));
        flow.Controls.Add(CreateButton("等距采样", () =>
            Request($"generate_points {_numDistance.Value}")));
        flow.Controls.Add(CreateButton("转频率 (20mm/s)", () => Request("convert_to_freq 20")));
        flow.Controls.Add(CreateButton("运行完整示例", () => RunExampleRequested?.Invoke()));

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 12, 0, 0),
            Text = "提示：图形操作与控制台指令等效，执行结果请查看「控制台」页。"
        };

        Controls.Add(hint);
        Controls.Add(flow);
        Controls.Add(layout);
    }

    private void Request(string command) => CommandRequested?.Invoke(command);

    private static NumericUpDown CreateNumeric(decimal min, decimal max, decimal value, int decimals = 0)
    {
        return new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = min,
            Maximum = max,
            Value = value,
            DecimalPlaces = decimals,
            Increment = decimals > 0 ? 0.5m : 1
        };
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        int row = layout.RowCount;
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 0, 6)
        }, 0, row);

        control.Margin = new Padding(0, 4, 0, 4);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        layout.Controls.Add(control, 1, row);
    }

    private static Button CreateButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 8),
            Padding = new Padding(12, 6, 12, 6)
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}