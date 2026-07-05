namespace b_Code_三维坐标机器人前端设计.Models;

public sealed class FrequencyCommand
{
    public int Index { get; init; }
    public double DeltaX { get; init; }
    public double DeltaY { get; init; }
    public double DeltaZ { get; init; }
    public double Length { get; init; }

    public double VelocityX { get; init; }
    public double VelocityY { get; init; }
    public double VelocityZ { get; init; }

    public double FreqX { get; init; }
    public double FreqY { get; init; }
    public double FreqZ { get; init; }

    public int DirectionX { get; init; }
    public int DirectionY { get; init; }
    public int DirectionZ { get; init; }

    public override string ToString() =>
        $"#{Index}: Fx={FreqX:F2} Fy={FreqY:F2} Fz={FreqZ:F2} Dir=({DirectionX},{DirectionY},{DirectionZ})";
}
