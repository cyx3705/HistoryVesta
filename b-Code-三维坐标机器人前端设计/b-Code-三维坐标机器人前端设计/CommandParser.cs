using System.Text;
using b_Code_三维坐标机器人前端设计.Models;
using b_Code_三维坐标机器人前端设计.Utils;

namespace b_Code_三维坐标机器人前端设计;

public sealed class CommandParser
{
    private readonly PathPlanner _planner;

    public CommandParser(PathPlanner planner) => _planner = planner;

    public string Execute(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string command = parts[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "reset" => ExecuteReset(),
                "home" => ExecuteHome(),
                "stop" => "已停止（仿真模式，无实际运动）。",
                "add_point" => ExecuteAddPoint(parts),
                "clear_points" => ExecuteClearPoints(),
                "interpolate" => ExecuteInterpolate(parts),
                "generate_points" => ExecuteGeneratePoints(parts),
                "convert_to_freq" => ExecuteConvertToFreq(parts),
                "process_all" => ExecuteProcessAll(parts),
                "set_freq_ratio" => ExecuteSetFreqRatio(parts),
                "show_freq_ratio" => ExecuteShowFreqRatio(),
                "clear_freq" => ExecuteClearFreq(),
                "show_tables" => ExecuteShowTables(),
                "help" => GetHelpText(),
                _ => $"未知指令: {command}。输入 help 查看可用指令。"
            };
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    public string ExecuteScript(string script)
    {
        var builder = new StringBuilder();
        foreach (string line in script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            builder.AppendLine($"> {trimmed}");
            string result = Execute(trimmed);
            if (!string.IsNullOrEmpty(result))
                builder.AppendLine(result);
        }

        return builder.ToString().TrimEnd();
    }

    private string ExecuteReset()
    {
        _planner.Reset();
        return "已复位：控制点、路径、表格与频率数据已清空，当前位置 (0, 0, 0)。";
    }

    private string ExecuteHome()
    {
        _planner.Home();
        return "已回到机械零点 (0, 0, 0)。";
    }

    private string ExecuteAddPoint(string[] parts)
    {
        if (parts.Length < 4)
            throw new ArgumentException("用法: add_point x y z");

        if (!double.TryParse(parts[1], out double x) ||
            !double.TryParse(parts[2], out double y) ||
            !double.TryParse(parts[3], out double z))
            throw new ArgumentException("坐标必须为数字。");

        _planner.AddPoint(x, y, z);
        return $"已添加控制点: ({x}, {y}, {z})，当前共 {_planner.ControlPoints.Count} 个点。";
    }

    private string ExecuteClearPoints()
    {
        _planner.ClearPoints();
        return "已清空控制点与路径数据。";
    }

    private string ExecuteInterpolate(string[] parts)
    {
        if (parts.Length < 3)
            throw new ArgumentException("用法: interpolate type steps  (type: linear / bezier / spline)");

        if (!int.TryParse(parts[2], out int steps) || steps < 2)
            throw new ArgumentException("steps 必须为 >= 2 的整数。");

        _planner.Interpolate(parts[1], steps);
        return FormatInterpResult(parts[1], steps);
    }

    private string ExecuteGeneratePoints(string[] parts)
    {
        if (parts.Length < 2)
            throw new ArgumentException("用法: generate_points distance");

        if (!double.TryParse(parts[1], out double distance) || distance <= 0)
            throw new ArgumentException("distance 必须为 > 0 的数字。");

        IReadOnlyList<PathSegment> segments = _planner.GeneratePoints(distance);
        return FormatSegmentTable($"等距采样完成，间距 {distance} mm", _planner.SampledPoints.Count, segments);
    }

    private string ExecuteConvertToFreq(string[] parts)
    {
        if (parts.Length < 2)
            throw new ArgumentException("用法: convert_to_freq speed  (路径速度 mm/s)");

        if (!double.TryParse(parts[1], out double speed) || speed <= 0)
            throw new ArgumentException("speed 必须为 > 0 的数字。");

        IReadOnlyList<FrequencyCommand> commands = _planner.ConvertToFreq(speed);
        return FormatFrequencyTable(speed, commands);
    }

    private string ExecuteProcessAll(string[] parts)
    {
        if (parts.Length < 3)
            throw new ArgumentException("用法: process_all distance speed");

        if (!double.TryParse(parts[1], out double distance) || distance <= 0)
            throw new ArgumentException("distance 必须为 > 0 的数字。");

        if (!double.TryParse(parts[2], out double speed) || speed <= 0)
            throw new ArgumentException("speed 必须为 > 0 的数字。");

        IReadOnlyList<FrequencyCommand> commands = _planner.ProcessAll(distance, speed);
        var builder = new StringBuilder();
        builder.AppendLine(FormatSegmentTable($"批处理：等距采样 {distance} mm", _planner.SampledPoints.Count, _planner.Segments));
        builder.AppendLine(FormatFrequencyTable(speed, commands));
        return builder.ToString().TrimEnd();
    }

    private string ExecuteClearFreq()
    {
        _planner.ClearFrequency();
        return "已清空脉冲频率数据（路径与小线段保留）。";
    }

    private static string ExecuteSetFreqRatio(string[] parts)
    {
        if (parts.Length == 2 && string.Equals(parts[1], "reset", StringComparison.OrdinalIgnoreCase))
        {
            FrequencyConverter.ResetAxisRatios();
            return $"频率比例已重置: X={FrequencyConverter.AxisRatioX:F4}, Y={FrequencyConverter.AxisRatioY:F4}, Z={FrequencyConverter.AxisRatioZ:F4}";
        }

        if (parts.Length < 4)
            throw new ArgumentException("用法: set_freq_ratio x y z  或  set_freq_ratio reset");

        if (!double.TryParse(parts[1], out double x) ||
            !double.TryParse(parts[2], out double y) ||
            !double.TryParse(parts[3], out double z))
            throw new ArgumentException("x/y/z 必须为数字。");

        FrequencyConverter.SetAxisRatios(x, y, z);
        return $"频率比例已设置: X={FrequencyConverter.AxisRatioX:F4}, Y={FrequencyConverter.AxisRatioY:F4}, Z={FrequencyConverter.AxisRatioZ:F4}";
    }

    private static string ExecuteShowFreqRatio() =>
        $"当前频率比例: X={FrequencyConverter.AxisRatioX:F4}, Y={FrequencyConverter.AxisRatioY:F4}, Z={FrequencyConverter.AxisRatioZ:F4}";

    private string ExecuteShowTables() =>
        $"""
        表格数据概览：
        插值线段：{_planner.InterpolatedSegments.Count} 段
        等距小线段：{_planner.Segments.Count} 段
        脉冲频率：{_planner.FrequencyCommands.Count} 段
        （详见上方右侧数据表格）
        """;

    private string FormatInterpResult(string type, int steps)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"插值完成 ({type}, {steps} 点)，共 {_planner.InterpolatedPoints.Count} 个插值点。");
        builder.Append(FormatSegmentTable("插值点间线段", _planner.InterpolatedPoints.Count, _planner.InterpolatedSegments));
        return builder.ToString().TrimEnd();
    }

    private static string FormatSegmentTable(string title, int pointCount, IReadOnlyList<PathSegment> segments)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{title}，共 {pointCount} 个点，{segments.Count} 段线段。");
        if (segments.Count == 0)
            return builder.ToString().TrimEnd();

        builder.AppendLine("Index\tΔx\t\tΔy\t\tΔz\t\tL (mm)");
        foreach (PathSegment segment in segments)
        {
            builder.AppendLine(
                $"{segment.Index}\t{segment.DeltaX:F4}\t{segment.DeltaY:F4}\t{segment.DeltaZ:F4}\t{segment.Length:F4}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatFrequencyTable(double speed, IReadOnlyList<FrequencyCommand> commands)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"频率转换完成，路径速度 V={speed:F2} mm/s，共 {commands.Count} 段。");
        if (commands.Count == 0)
            return builder.ToString().TrimEnd();

        builder.AppendLine("Index\tFx(Hz)\tFy(Hz)\tFz(Hz)\tVx\tVy\tVz");
        foreach (FrequencyCommand cmd in commands)
        {
            builder.AppendLine(
                $"{cmd.Index}\t{cmd.FreqX:F2}\t{cmd.FreqY:F2}\t{cmd.FreqZ:F2}\t{cmd.VelocityX:F4}\t{cmd.VelocityY:F4}\t{cmd.VelocityZ:F4}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string GetHelpText() =>
        """
        【路径规划指令】
          reset                     - 复位
          home                      - 回零点
          stop                      - 停止
          add_point x y z           - 添加控制点
          clear_points              - 清空控制点
          interpolate type steps    - 插值 (linear/bezier/spline)

        【数据处理 / 批处理指令】
          generate_points distance  - 等距采样，计算小线段分量
          convert_to_freq speed     - 位置差 → 三轴脉冲频率 (Hz)
          process_all distance speed- 一键：采样 + 频率转换
          set_freq_ratio x y z      - 设置 X/Y/Z 频率比例系数（>0）
          set_freq_ratio reset      - 频率比例恢复 1,1,1
          show_freq_ratio           - 查看当前频率比例
          clear_freq                - 清空频率数据
          show_tables               - 刷新数据表格概览

        示例流程:
          reset
          add_point 0 0 0
          add_point 100 50 30
          add_point 200 100 60
          interpolate spline 50
          generate_points 2.0
          convert_to_freq 20
        """;
}
