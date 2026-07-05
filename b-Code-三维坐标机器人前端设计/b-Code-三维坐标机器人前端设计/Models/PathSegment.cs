namespace b_Code_三维坐标机器人前端设计.Models;

public sealed class PathSegment
{
    public int Index { get; init; }
    public Point3D Start { get; init; }
    public Point3D End { get; init; }

    public double SignedDeltaX { get; init; }
    public double SignedDeltaY { get; init; }
    public double SignedDeltaZ { get; init; }

    public double DeltaX { get; init; }
    public double DeltaY { get; init; }
    public double DeltaZ { get; init; }
    public double Length { get; init; }

    public int DirectionX => SignedDeltaX >= 0 ? 1 : 0;
    public int DirectionY => SignedDeltaY >= 0 ? 1 : 0;
    public int DirectionZ => SignedDeltaZ >= 0 ? 1 : 0;

    public override string ToString() =>
        $"#{Index}: {Start} -> {End} | dx={SignedDeltaX:F4} dy={SignedDeltaY:F4} dz={SignedDeltaZ:F4} L={Length:F4}";
}
