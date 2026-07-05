namespace b_Code_三维坐标机器人前端设计;

public sealed class DataProcessingPanel : UserControl
{
    private readonly NumericUpDown _numDistance;
    private readonly NumericUpDown _numSpeed;

    public event Action<string>? CommandRequested;

    public DataProcessingPanel()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(12);

        var title = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Text = "数据处理 — 位置差 → 脉冲频率批处理"
        };

        var cmdBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 160,
            ReadOnly = true,
            BackColor = Color.FromArgb(248, 249, 252),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5F),
            Text =
                """
                【批处理指令集】
                generate_points distance   等距采样，计算小线段 Δx/Δy/Δz/L
                convert_to_freq speed      将当前小线段位置差转为三轴脉冲频率 (Hz)
                process_all distance speed 一键：等距采样 + 频率转换
                clear_freq                 清空频率数据（保留路径）
                show_tables                刷新右侧数据表格

                算法：Vx=V·Δx/L → 转速=Vx/导程 → 频率=转速×800
                X导程95mm/转  Y/Z导程5mm/转  800脉冲/转
                """
        };

        var paramLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            Padding = new Padding(0, 12, 0, 0)
        };
        paramLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        paramLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        paramLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        paramLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        _numDistance = CreateNumeric(0.1m, 50, 2, 1);
        _numSpeed = CreateNumeric(1, 200, 20, 0);

        paramLayout.Controls.Add(new Label { Text = "采样间距", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 0);
        paramLayout.Controls.Add(_numDistance, 1, 0);
        paramLayout.Controls.Add(new Label { Text = "路径速度", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 2, 0);
        paramLayout.Controls.Add(_numSpeed, 3, 0);

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 12, 0, 0)
        };

        flow.Controls.Add(CreateButton("等距采样", () =>
            Request($"generate_points {_numDistance.Value}")));
        flow.Controls.Add(CreateButton("位置差→频率", () =>
            Request($"convert_to_freq {_numSpeed.Value}")));
        flow.Controls.Add(CreateButton("一键批处理", () =>
            Request($"process_all {_numDistance.Value} {_numSpeed.Value}")));
        flow.Controls.Add(CreateButton("清空频率", () => Request("clear_freq")));
        flow.Controls.Add(CreateButton("刷新表格", () => Request("show_tables")));

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 12, 0, 0),
            Text = "执行后请查看上方右侧数据表格（插值线段 / 等距小线段 / 脉冲频率），详细日志在「控制台」。"
        };

        Controls.Add(hint);
        Controls.Add(flow);
        Controls.Add(paramLayout);
        Controls.Add(cmdBox);
        Controls.Add(title);
    }

    private void Request(string command) => CommandRequested?.Invoke(command);

    private static NumericUpDown CreateNumeric(decimal min, decimal max, decimal value, int decimals)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            DecimalPlaces = decimals,
            Increment = decimals > 0 ? 0.5m : 1,
            Width = 100
        };
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