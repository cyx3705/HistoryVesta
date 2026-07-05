using b_Code_三维坐标机器人前端设计.Models;

namespace b_Code_三维坐标机器人前端设计.Utils;

public static class FrequencyConverter
{
    /// <summary>X 轴同步带导程 (mm/转)</summary>
    public const double XLeadMmPerRev = 95.0;

    /// <summary>Y/Z 轴滚珠丝杆导程 (mm/转)</summary>
    public const double YzLeadMmPerRev = 5.0;

    /// <summary>步进 200 步/转 × 4 细分</summary>
    public const double PulsesPerRev = 800.0;
    public const double DefaultAxisRatio = 1.0;

    public static double AxisRatioX { get; private set; } = DefaultAxisRatio;
    public static double AxisRatioY { get; private set; } = DefaultAxisRatio;
    public static double AxisRatioZ { get; private set; } = DefaultAxisRatio;

    public static FrequencyCommand Convert(PathSegment segment, double pathSpeedMmPerSec)
    {
        double length = segment.Length;
        if (length < 1e-9)
        {
            return new FrequencyCommand
            {
                Index = segment.Index,
                DeltaX = segment.DeltaX,
                DeltaY = segment.DeltaY,
                DeltaZ = segment.DeltaZ,
                Length = length
            };
        }

        double vx = pathSpeedMmPerSec * (segment.DeltaX / length);
        double vy = pathSpeedMmPerSec * (segment.DeltaY / length);
        double vz = pathSpeedMmPerSec * (segment.DeltaZ / length);

        double freqX = VelocityToFrequency(vx, XLeadMmPerRev) * AxisRatioX;
        double freqY = VelocityToFrequency(vy, YzLeadMmPerRev) * AxisRatioY;
        double freqZ = VelocityToFrequency(vz, YzLeadMmPerRev) * AxisRatioZ;

        return new FrequencyCommand
        {
            Index = segment.Index,
            DeltaX = segment.DeltaX,
            DeltaY = segment.DeltaY,
            DeltaZ = segment.DeltaZ,
            Length = length,
            VelocityX = vx,
            VelocityY = vy,
            VelocityZ = vz,
            FreqX = freqX,
            FreqY = freqY,
            FreqZ = freqZ,
            DirectionX = segment.DirectionX,
            DirectionY = segment.DirectionY,
            DirectionZ = segment.DirectionZ
        };
    }

    public static double VelocityToFrequency(double velocityMmPerSec, double leadMmPerRev) =>
        velocityMmPerSec / leadMmPerRev * PulsesPerRev;

    public static void SetAxisRatios(double x, double y, double z)
    {
        ValidatePositive(x, nameof(x));
        ValidatePositive(y, nameof(y));
        ValidatePositive(z, nameof(z));

        AxisRatioX = x;
        AxisRatioY = y;
        AxisRatioZ = z;
    }

    public static void ResetAxisRatios() =>
        SetAxisRatios(DefaultAxisRatio, DefaultAxisRatio, DefaultAxisRatio);

    private static void ValidatePositive(double value, string paramName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            throw new ArgumentOutOfRangeException(paramName, "Axis ratio must be a finite number > 0.");
    }
}
