namespace b_Code_三维坐标机器人前端设计;

public static class SerialAutoBaud
{
    private static readonly int[] CandidateBaudRates =
        [9600, 115200, 57600, 38400, 28800, 230400, 172800, 74880];

    public static async Task<(int Baud, int Score)> DetectAsync(string port, Action<string>? log = null)
    {
        int bestBaud = SerialDefaults.BaudRate;
        int bestScore = -1;

        log?.Invoke($"自动波特率检测 {port}（PING{SerialProtocol.FrameSuffix}）…");

        foreach (int baud in CandidateBaudRates)
        {
            int score = await ProbeScoreAsync(port, baud);
            log?.Invoke($"  @{baud} 得分={score}");
            if (score > bestScore)
            {
                bestScore = score;
                bestBaud = baud;
            }
        }

        log?.Invoke(bestScore > 0
            ? $"选用 @{bestBaud} (得分 {bestScore})"
            : $"未检测到 PONG{SerialProtocol.FrameSuffix}，仍将尝试默认 {SerialDefaults.BaudRateText}");

        return (bestBaud, Math.Max(bestScore, 0));
    }

    private static Task<int> ProbeScoreAsync(string port, int baud)
    {
        var buffer = new List<byte>(4096);
        using var serial = new System.IO.Ports.SerialPort(port, baud)
        {
            ReadTimeout = 200,
            WriteTimeout = 500,
            DtrEnable = false,
            RtsEnable = false
        };

        try
        {
            serial.Open();
            serial.DiscardInBuffer();
            Thread.Sleep(300);

            byte[] ping = SerialProtocol.PackFrame(SerialProtocol.HandshakeSend);
            serial.Write(ping, 0, ping.Length);
            Thread.Sleep(1200);

            int n = serial.BytesToRead;
            if (n > 0)
            {
                byte[] chunk = new byte[n];
                serial.Read(chunk, 0, n);
                buffer.AddRange(chunk);
            }
        }
        catch
        {
            return Task.FromResult(-1);
        }
        finally
        {
            if (serial.IsOpen)
                serial.Close();
        }

        return Task.FromResult(Score(buffer));
    }

    private static int Score(IReadOnlyList<byte> buffer)
    {
        if (buffer.Count == 0)
            return 0;

        int score = 0;
        if (SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.HandshakeReply + SerialProtocol.FrameSuffix))
            score += 200;
        else if (SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.HandshakeReply))
            score += 120;

        if (SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.StepDoneReply + SerialProtocol.FrameSuffix))
            score += 80;

        return score;
    }
}