namespace b_Code_三维坐标机器人前端设计.Models;

public readonly struct Point3D
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public Point3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double DistanceTo(Point3D other)
    {
        double dx = other.X - X;
        double dy = other.Y - Y;
        double dz = other.Z - Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public Point3D Lerp(Point3D other, double t) =>
        new(
            X + (other.X - X) * t,
            Y + (other.Y - Y) * t,
            Z + (other.Z - Z) * t);

    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}