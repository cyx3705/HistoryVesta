using b_Code_三维坐标机器人前端设计.Models;

namespace b_Code_三维坐标机器人前端设计.Utils;

public static class Interpolation
{
    public static List<Point3D> InterpolateLinear(IReadOnlyList<Point3D> controlPoints, int steps)
    {
        if (controlPoints.Count < 2)
            throw new InvalidOperationException("至少需要 2 个插值点。");

        if (steps < 2)
            throw new ArgumentOutOfRangeException(nameof(steps), "steps 必须 >= 2。");

        var segmentLengths = new double[controlPoints.Count - 1];
        double totalLength = 0;
        for (int i = 0; i < controlPoints.Count - 1; i++)
        {
            segmentLengths[i] = controlPoints[i].DistanceTo(controlPoints[i + 1]);
            totalLength += segmentLengths[i];
        }

        if (totalLength < 1e-9)
            return Enumerable.Repeat(controlPoints[0], steps).ToList();

        var result = new List<Point3D>(steps) { controlPoints[0] };
        int segmentIndex = 0;
        double segmentStart = 0;

        for (int i = 1; i < steps - 1; i++)
        {
            double target = totalLength * i / (steps - 1);
            while (segmentIndex < segmentLengths.Length - 1 &&
                   target > segmentStart + segmentLengths[segmentIndex])
            {
                segmentStart += segmentLengths[segmentIndex];
                segmentIndex++;
            }

            double localT = segmentLengths[segmentIndex] < 1e-9
                ? 0
                : (target - segmentStart) / segmentLengths[segmentIndex];

            result.Add(controlPoints[segmentIndex].Lerp(controlPoints[segmentIndex + 1], localT));
        }

        result.Add(controlPoints[^1]);
        return result;
    }

    public static List<Point3D> InterpolateBezier(IReadOnlyList<Point3D> controlPoints, int steps)
    {
        if (controlPoints.Count < 2)
            throw new InvalidOperationException("至少需要 2 个插值点。");

        if (steps < 2)
            throw new ArgumentOutOfRangeException(nameof(steps), "steps 必须 >= 2。");

        if (controlPoints.Count == 2)
            return InterpolateLinear(controlPoints, steps);

        if (controlPoints.Count == 3)
            return SampleCurve(t => QuadraticBezier(controlPoints[0], controlPoints[1], controlPoints[2], t), steps);

        return SampleCurve(t => PiecewiseCubicBezier(controlPoints, t), steps);
    }

    public static List<Point3D> InterpolateSpline(IReadOnlyList<Point3D> controlPoints, int steps)
    {
        if (controlPoints.Count < 2)
            throw new InvalidOperationException("至少需要 2 个插值点。");

        if (steps < 2)
            throw new ArgumentOutOfRangeException(nameof(steps), "steps 必须 >= 2。");

        if (controlPoints.Count == 2)
            return InterpolateLinear(controlPoints, steps);

        return SampleCurve(t => CatmullRomSpline(controlPoints, t), steps);
    }

    private static List<Point3D> SampleCurve(Func<double, Point3D> evaluator, int steps)
    {
        var result = new List<Point3D>(steps);
        for (int i = 0; i < steps; i++)
        {
            double t = i / (double)(steps - 1);
            result.Add(evaluator(t));
        }

        return result;
    }

    private static Point3D QuadraticBezier(Point3D p0, Point3D p1, Point3D p2, double t)
    {
        double u = 1 - t;
        return new Point3D(
            u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X,
            u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y,
            u * u * p0.Z + 2 * u * t * p1.Z + t * t * p2.Z);
    }

    private static Point3D PiecewiseCubicBezier(IReadOnlyList<Point3D> points, double t)
    {
        int segmentCount = points.Count - 1;
        double scaled = t * segmentCount;
        int index = Math.Min((int)Math.Floor(scaled), segmentCount - 1);
        double localT = scaled - index;

        Point3D p0 = points[index];
        Point3D p3 = points[Math.Min(index + 1, points.Count - 1)];
        Point3D p1 = index == 0
            ? p0
            : new Point3D(
                p0.X + (points[index + 1].X - points[index - 1].X) / 6,
                p0.Y + (points[index + 1].Y - points[index - 1].Y) / 6,
                p0.Z + (points[index + 1].Z - points[index - 1].Z) / 6);
        Point3D p2 = index >= points.Count - 2
            ? p3
            : new Point3D(
                p3.X - (points[index + 2].X - points[index].X) / 6,
                p3.Y - (points[index + 2].Y - points[index].Y) / 6,
                p3.Z - (points[index + 2].Z - points[index].Z) / 6);

        return CubicBezier(p0, p1, p2, p3, localT);
    }

    private static Point3D CubicBezier(Point3D p0, Point3D p1, Point3D p2, Point3D p3, double t)
    {
        double u = 1 - t;
        double u2 = u * u;
        double t2 = t * t;
        return new Point3D(
            u2 * u * p0.X + 3 * u2 * t * p1.X + 3 * u * t2 * p2.X + t2 * t * p3.X,
            u2 * u * p0.Y + 3 * u2 * t * p1.Y + 3 * u * t2 * p2.Y + t2 * t * p3.Y,
            u2 * u * p0.Z + 3 * u2 * t * p1.Z + 3 * u * t2 * p2.Z + t2 * t * p3.Z);
    }

    private static Point3D CatmullRomSpline(IReadOnlyList<Point3D> points, double t)
    {
        int segmentCount = points.Count - 1;
        double scaled = t * segmentCount;
        int index = Math.Min((int)Math.Floor(scaled), segmentCount - 1);
        double localT = scaled - index;

        Point3D p0 = points[Math.Max(index - 1, 0)];
        Point3D p1 = points[index];
        Point3D p2 = points[index + 1];
        Point3D p3 = points[Math.Min(index + 2, points.Count - 1)];

        double t2 = localT * localT;
        double t3 = t2 * localT;
        return new Point3D(
            0.5 * (2 * p1.X + (-p0.X + p2.X) * localT +
                   (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                   (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
            0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * localT +
                   (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                   (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3),
            0.5 * (2 * p1.Z + (-p0.Z + p2.Z) * localT +
                   (2 * p0.Z - 5 * p1.Z + 4 * p2.Z - p3.Z) * t2 +
                   (-p0.Z + 3 * p1.Z - 3 * p2.Z + p3.Z) * t3));
    }
}