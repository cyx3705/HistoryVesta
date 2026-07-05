using b_Code_三维坐标机器人前端设计.Models;

namespace b_Code_三维坐标机器人前端设计;

public sealed class PathDataTablePanel : UserControl
{
    private readonly TabControl _tabs;
    private readonly DataGridView _gridInterp;
    private readonly DataGridView _gridSampled;
    private readonly DataGridView _gridFreq;
    private readonly Label _lblInterp;
    private readonly Label _lblSampled;
    private readonly Label _lblFreq;

    public PathDataTablePanel()
    {
        Dock = DockStyle.Fill;

        _tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9F) };

        (_gridInterp, _lblInterp) = CreateSegmentTab("插值线段", "插值点间线段长度与分量");
        (_gridSampled, _lblSampled) = CreateSegmentTab("等距小线段", "等距采样后小线段分量");
        (_gridFreq, _lblFreq) = CreateFrequencyTabPage();

        Controls.Add(_tabs);
    }

    public void Bind(PathPlanner planner)
    {
        FillSegmentGrid(_gridInterp, planner.InterpolatedSegments);
        FillSegmentGrid(_gridSampled, planner.Segments);
        FillFrequencyGrid(_gridFreq, planner.FrequencyCommands);

        _lblInterp.Text = BuildSegmentSummary("插值线段", planner.InterpolatedSegments);
        _lblSampled.Text = BuildSegmentSummary("等距小线段", planner.Segments);
        _lblFreq.Text = BuildFrequencySummary(planner.FrequencyCommands, planner.PathSpeed);
    }

    private (DataGridView Grid, Label Summary) CreateSegmentTab(string title, string hint)
    {
        var page = new TabPage(title);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(4)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var summary = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = $"{hint}：暂无数据"
        };

        var grid = CreateGrid();
        ConfigureSegmentColumns(grid);

        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        page.Controls.Add(layout);
        _tabs.TabPages.Add(page);
        return (grid, summary);
    }

    private (DataGridView Grid, Label Summary) CreateFrequencyTabPage()
    {
        var page = new TabPage("脉冲频率");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(4)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var summary = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "位置差→频率：请先执行 convert_to_freq"
        };

        var grid = CreateGrid();
        ConfigureFrequencyColumns(grid);

        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        page.Controls.Add(layout);
        _tabs.TabPages.Add(page);
        return (grid, summary);
    }

    private static DataGridView CreateGrid()
    {
        return new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
        };
    }

    private static void ConfigureSegmentColumns(DataGridView grid)
    {
        grid.Columns.Add("Index", "段号");
        grid.Columns.Add("Start", "起点");
        grid.Columns.Add("End", "终点");
        grid.Columns.Add("DeltaX", "Δx");
        grid.Columns.Add("DeltaY", "Δy");
        grid.Columns.Add("DeltaZ", "Δz");
        grid.Columns.Add("Length", "L (mm)");
    }

    private static void ConfigureFrequencyColumns(DataGridView grid)
    {
        grid.Columns.Add("Index", "段号");
        grid.Columns.Add("DeltaX", "Δx");
        grid.Columns.Add("DeltaY", "Δy");
        grid.Columns.Add("DeltaZ", "Δz");
        grid.Columns.Add("Length", "L");
        grid.Columns.Add("Vx", "Vx");
        grid.Columns.Add("Vy", "Vy");
        grid.Columns.Add("Vz", "Vz");
        grid.Columns.Add("FreqX", "Fx (Hz)");
        grid.Columns.Add("FreqY", "Fy (Hz)");
        grid.Columns.Add("FreqZ", "Fz (Hz)");
    }

    private static void FillSegmentGrid(DataGridView grid, IReadOnlyList<PathSegment> segments)
    {
        grid.Rows.Clear();
        foreach (PathSegment segment in segments)
        {
            grid.Rows.Add(
                segment.Index,
                segment.Start.ToString(),
                segment.End.ToString(),
                segment.DeltaX.ToString("F4"),
                segment.DeltaY.ToString("F4"),
                segment.DeltaZ.ToString("F4"),
                segment.Length.ToString("F4"));
        }
    }

    private static void FillFrequencyGrid(DataGridView grid, IReadOnlyList<FrequencyCommand> commands)
    {
        grid.Rows.Clear();
        foreach (FrequencyCommand cmd in commands)
        {
            grid.Rows.Add(
                cmd.Index,
                cmd.DeltaX.ToString("F4"),
                cmd.DeltaY.ToString("F4"),
                cmd.DeltaZ.ToString("F4"),
                cmd.Length.ToString("F4"),
                cmd.VelocityX.ToString("F4"),
                cmd.VelocityY.ToString("F4"),
                cmd.VelocityZ.ToString("F4"),
                cmd.FreqX.ToString("F2"),
                cmd.FreqY.ToString("F2"),
                cmd.FreqZ.ToString("F2"));
        }
    }

    private static string BuildSegmentSummary(string title, IReadOnlyList<PathSegment> segments)
    {
        if (segments.Count == 0)
            return $"{title}：暂无数据（请先执行 interpolate / generate_points）";

        double totalLength = segments.Sum(s => s.Length);
        double avgLength = totalLength / segments.Count;
        return $"{title}：共 {segments.Count} 段，总长度 {totalLength:F2} mm，平均 {avgLength:F4} mm";
    }

    private static string BuildFrequencySummary(IReadOnlyList<FrequencyCommand> commands, double pathSpeed)
    {
        if (commands.Count == 0)
            return "位置差→频率：暂无数据（请执行 convert_to_freq 或 process_all）";

        return $"脉冲频率：共 {commands.Count} 段，路径速度 V={pathSpeed:F2} mm/s";
    }
}