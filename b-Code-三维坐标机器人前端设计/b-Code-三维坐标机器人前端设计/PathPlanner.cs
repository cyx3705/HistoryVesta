using b_Code_三维坐标机器人前端设计.Models;
using b_Code_三维坐标机器人前端设计.Utils;

namespace b_Code_三维坐标机器人前端设计;

public sealed class PathPlanner
{
    private readonly List<Point3D> _controlPoints = [];
    private List<Point3D> _interpolatedPoints = [];
    private List<Point3D> _sampledPoints = [];
    private List<PathSegment> _interpolatedSegments = [];
    private List<PathSegment> _segments = [];
    private List<FrequencyCommand> _frequencyCommands = [];

    public IReadOnlyList<Point3D> ControlPoints => _controlPoints;
    public IReadOnlyList<Point3D> InterpolatedPoints => _interpolatedPoints;
    public IReadOnlyList<Point3D> SampledPoints => _sampledPoints;
    public IReadOnlyList<PathSegment> InterpolatedSegments => _interpolatedSegments;
    public IReadOnlyList<PathSegment> Segments => _segments;
    public IReadOnlyList<FrequencyCommand> FrequencyCommands => _frequencyCommands;
    public double PathSpeed { get; private set; }

    public Point3D CurrentPosition { get; private set; }

    public void Reset()
    {
        _controlPoints.Clear();
        ClearPathData();
        CurrentPosition = new Point3D(0, 0, 0);
    }

    public void Home() => CurrentPosition = new Point3D(0, 0, 0);

    public void SetCurrentPosition(Point3D position) => CurrentPosition = position;

    public void SetCurrentPosition(double x, double y, double z) =>
        CurrentPosition = new Point3D(x, y, z);

    public void AddPoint(double x, double y, double z) =>
        _controlPoints.Add(new Point3D(x, y, z));

    public void ClearPoints()
    {
        _controlPoints.Clear();
        ClearPathData();
    }

    public void ClearFrequency() => _frequencyCommands.Clear();

    public void Interpolate(string type, int steps)
    {
        if (_controlPoints.Count < 2)
            throw new InvalidOperationException("插值前至少需要 2 个控制点，请使用 add_point。");

        _interpolatedPoints = type.ToLowerInvariant() switch
        {
            "linear" => Interpolation.InterpolateLinear(_controlPoints, steps),
            "bezier" => Interpolation.InterpolateBezier(_controlPoints, steps),
            "spline" => Interpolation.InterpolateSpline(_controlPoints, steps),
            _ => throw new ArgumentException($"不支持的插值类型: {type}，可选 linear / bezier / spline。")
        };

        _interpolatedSegments = BuildSegmentComponents(_interpolatedPoints);
        _sampledPoints.Clear();
        _segments.Clear();
        _frequencyCommands.Clear();
    }

    /// <summary>
    /// 按固定距离等距采样，并计算每段小线段的三轴分量列表。
    /// </summary>
    public IReadOnlyList<PathSegment> GeneratePoints(double distance)
    {
        if (_interpolatedPoints.Count < 2)
            throw new InvalidOperationException("请先执行 interpolate 生成插值曲线。");

        if (distance <= 0)
            throw new ArgumentOutOfRangeException(nameof(distance), "distance 必须 > 0。");

        _sampledPoints = ResampleAtEqualDistance(_interpolatedPoints, distance);
        _segments = BuildSegmentComponents(_sampledPoints);
        _frequencyCommands.Clear();
        return _segments;
    }

    /// <summary>
    /// 将当前等距小线段的位置差批量转换为三轴脉冲频率。
    /// </summary>
    public IReadOnlyList<FrequencyCommand> ConvertToFreq(double speed)
    {
        if (_segments.Count == 0)
            throw new InvalidOperationException("请先执行 generate_points 生成等距小线段。");

        if (speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), "speed 必须 > 0。");

        PathSpeed = speed;
        _frequencyCommands = _segments
            .Select(segment => FrequencyConverter.Convert(segment, speed))
            .ToList();
        return _frequencyCommands;
    }

    /// <summary>
    /// 批处理：等距采样 + 频率转换。
    /// </summary>
    public IReadOnlyList<FrequencyCommand> ProcessAll(double distance, double speed)
    {
        GeneratePoints(distance);
        return ConvertToFreq(speed);
    }

    private void ClearPathData()
    {
        _interpolatedPoints.Clear();
        _sampledPoints.Clear();
        _interpolatedSegments.Clear();
        _segments.Clear();
        _frequencyCommands.Clear();
        PathSpeed = 0;
    }

    private static List<Point3D> ResampleAtEqualDistance(IReadOnlyList<Point3D> curve, double distance)
    {
        var result = new List<Point3D> { curve[0] };
        var cumulative = new double[curve.Count];
        for (int i = 1; i < curve.Count; i++)
            cumulative[i] = cumulative[i - 1] + curve[i - 1].DistanceTo(curve[i]);

        double totalLength = cumulative[^1];
        if (totalLength < 1e-9)
            return result;

        int segmentIndex = 0;
        for (double target = distance; target < totalLength; target += distance)
        {
            while (segmentIndex < curve.Count - 2 && cumulative[segmentIndex + 1] < target)
                segmentIndex++;

            double segStart = cumulative[segmentIndex];
            double segLength = cumulative[segmentIndex + 1] - segStart;
            double t = segLength < 1e-9 ? 0 : (target - segStart) / segLength;
            result.Add(curve[segmentIndex].Lerp(curve[segmentIndex + 1], t));
        }

        Point3D last = curve[^1];
        if (result[^1].DistanceTo(last) > 1e-6)
            result.Add(last);

        return result;
    }

    /// <summary>
    /// 对相邻点序列计算 Δx、Δy、Δz 及斜边长度 L。
    /// </summary>
    private static List<PathSegment> BuildSegmentComponents(IReadOnlyList<Point3D> points)
    {
        var segments = new List<PathSegment>(Math.Max(0, points.Count - 1));
        for (int i = 0; i < points.Count - 1; i++)
        {
            Point3D start = points[i];
            Point3D end = points[i + 1];
            double signedDeltaX = end.X - start.X;
            double signedDeltaY = end.Y - start.Y;
            double signedDeltaZ = end.Z - start.Z;
            double deltaX = Math.Abs(signedDeltaX);
            double deltaY = Math.Abs(signedDeltaY);
            double deltaZ = Math.Abs(signedDeltaZ);
            double length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);

            segments.Add(new PathSegment
            {
                Index = i,
                Start = start,
                End = end,
                SignedDeltaX = signedDeltaX,
                SignedDeltaY = signedDeltaY,
                SignedDeltaZ = signedDeltaZ,
                DeltaX = deltaX,
                DeltaY = deltaY,
                DeltaZ = deltaZ,
                Length = length
            });
        }

        return segments;
    }
}
