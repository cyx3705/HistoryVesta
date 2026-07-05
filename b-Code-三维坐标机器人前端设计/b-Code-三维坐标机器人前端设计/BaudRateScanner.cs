namespace b_Code_三维坐标机器人前端设计;

public static class BaudRateScanner
{
    private static readonly int[] CommonBaudRates =
        [SerialDefaults.BaudRate, 115200, 57600, 38400, 19200];

    public static async Task<IReadOnlyList<string>> ScanAsync(
        string portName,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        var results = new List<string>();

        foreach (int baud in CommonBaudRates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log($"--- 扫描 @{baud} ---");

            using var serial = new SerialPortService();
            int rawBytes = 0;
            var frames = new List<string>();

            serial.RawDataReceived += packet => rawBytes += packet.Bytes.Length;
            serial.FrameReceived += frame => frames.Add(frame);

            try
            {
                serial.Open(portName, baud);
                await Task.Delay(300, cancellationToken);

                serial.DiscardBufferedInput();
                rawBytes = 0;
                frames.Clear();

                const string testHz = "100,500,600";
                serial.SendFrame(testHz);
                log($">> {testHz}{SerialProtocol.FrameSuffix}");

                try
                {
                    string ok = await serial.WaitForFrameAsync(
                        SerialProtocol.StepDoneReply,
                        cancellationToken,
                        RobotDemoRunner.StepDurationMs + 3000);
                    frames.Add(ok);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(500, cancellationToken);
                }

                bool hasOk = frames.Any(f =>
                    string.Equals(f, SerialProtocol.StepDoneReply, StringComparison.OrdinalIgnoreCase));
                string frameText = frames.Count == 0
                    ? "(无完整帧)"
                    : string.Join(" | ", frames.Select(f => f + SerialProtocol.FrameSuffix));

                string summary = $"@{baud}: 收 {rawBytes}B, 帧=[{frameText}], OK={hasOk}";
                results.Add(summary);
                log(summary);

                if (hasOk)
                    log($"*** 建议波特率 {baud} ***");
            }
            catch (Exception ex)
            {
                log($"@{baud} 失败: {ex.Message}");
            }
        }

        return results;
    }

    public static string ExplainRepeatedO(int oCount, int totalBytes)
    {
        if (totalBytes == 0)
            return "未收到数据：板子空闲或接线/波特率可能正确。";

        if (oCount > totalBytes / 2)
            return "0x4F='O' 占多数：常见为波特率不匹配时只解出 OK### 中的 O，请用「扫描波特率」或确认 9600。";

        return $"收到 {totalBytes} 字节，其中 O={oCount}。请对比 HEX 判断是否为乱码。";
    }
}