using b_Code_三维坐标机器人前端设计.Models;

namespace b_Code_三维坐标机器人前端设计;

public sealed class RobotDemoRunner
{
    public const int StepDurationMs = 1000;

    private readonly PathPlanner _planner;
    private CancellationTokenSource? _cts;

    public RobotDemoRunner(PathPlanner planner) => _planner = planner;

    public bool IsRunning { get; private set; }

    public event Action<string>? Log;
    public event Action? SceneChanged;

    public void Stop()
    {
        _cts?.Cancel();
    }

    public async Task RunSimulationAsync()
    {
        await RunInternalAsync(useSerial: false, serial: null).ConfigureAwait(true);
    }

    public async Task RunWithSerialAsync(SerialPortService serial)
    {
        await RunInternalAsync(useSerial: true, serial).ConfigureAwait(true);
    }

    private async Task RunInternalAsync(bool useSerial, SerialPortService? serial)
    {
        if (IsRunning)
            throw new InvalidOperationException("演示已在运行中。");

        ValidateReady(useSerial, serial);

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        IsRunning = true;

        try
        {
            IReadOnlyList<FrequencyCommand> commands = _planner.FrequencyCommands;
            IReadOnlyList<Point3D> points = _planner.SampledPoints;

            _planner.SetCurrentPosition(points[0]);
            SceneChanged?.Invoke();
            Log?.Invoke($"红点初始位置: {points[0]}");

            for (int i = 0; i < commands.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                FrequencyCommand cmd = commands[i];
                string hzPayload = SerialProtocol.FormatHzCommand(
                    (int)Math.Round(cmd.FreqX),
                    (int)Math.Round(cmd.FreqY),
                    (int)Math.Round(cmd.FreqZ),
                    cmd.DirectionX,
                    cmd.DirectionY,
                    cmd.DirectionZ);

                Log?.Invoke($"[{i + 1}/{commands.Count}] 发送 {hzPayload}{SerialProtocol.FrameSuffix}");

                if (useSerial && serial != null)
                {
                    serial.DiscardBufferedInput();
                    DateTime sendAt = DateTime.UtcNow;
                    serial.SendFrame(hzPayload);
                    string ok = await serial.WaitForFrameAsync(
                        SerialProtocol.StepDoneReply,
                        token,
                        StepDurationMs + 3000).ConfigureAwait(true);
                    int remainMs = StepDurationMs - (int)(DateTime.UtcNow - sendAt).TotalMilliseconds;
                    if (remainMs > 0)
                        await Task.Delay(remainMs, token).ConfigureAwait(true);
                    Log?.Invoke($"板子回应: {ok}{SerialProtocol.FrameSuffix}");
                }
                else
                {
                    await Task.Delay(StepDurationMs, token).ConfigureAwait(true);
                    Log?.Invoke("模拟：等待 1s（无串口）");
                }

                Point3D next = points[i + 1];
                _planner.SetCurrentPosition(next);
                SceneChanged?.Invoke();
                Log?.Invoke($"红点移动到: {next}");
            }

            Log?.Invoke("演示完成。");
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ValidateReady(bool useSerial, SerialPortService? serial)
    {
        if (_planner.FrequencyCommands.Count == 0)
            throw new InvalidOperationException("请先执行 convert_to_freq 生成脉冲频率数据。");

        if (_planner.SampledPoints.Count < 2)
            throw new InvalidOperationException("请先执行 generate_points 生成采样路径。");

        if (_planner.FrequencyCommands.Count != _planner.SampledPoints.Count - 1)
            throw new InvalidOperationException("频率段数与采样点数量不匹配，请重新规划路径。");

        if (useSerial && (serial == null || !serial.IsOpen))
            throw new InvalidOperationException("请先连接串口。");

        if (useSerial && serial != null && !serial.IsHandshaked)
            throw new InvalidOperationException("请先连接串口并完成 PING/PONG 握手。");
    }
}
