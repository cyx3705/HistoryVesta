using System.IO.Ports;
using System.Text;

namespace b_Code_三维坐标机器人前端设计;

public static class SerialPortCliTester
{
    private static readonly List<string> _logLines = [];
    private static Action<string> _log = Console.WriteLine;

    public static async Task<int> RunAsync(string[] args, Action<string>? log = null)
    {
        _logLines.Clear();
        _log = line =>
        {
            _logLines.Add(line);
            if (log != null)
                log(line);
            else
                Console.WriteLine(line);
        };

        string[] positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a != "-s" && a != "-n").ToArray();
        string port = positional.Length > 0 ? positional[0] : "COM3";

        int exitCode;
        if (args.Any(a => a is "--scan" or "-s"))
            exitCode = await ScanBaudRatesAsync(port);
        else
        {
            int baud = positional.Length > 1 && int.TryParse(positional[1], out int b) ? b : SerialDefaults.BaudRate;
            bool sendPing = !args.Any(a => a is "--no-ping" or "-n");
            _log($"=== 串口诊断 {port} @{baud}（### 帧协议）===");
            exitCode = await TestPortAsync(port, baud, sendPing);
        }

        string outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "serial_test_last.txt");
        await File.WriteAllLinesAsync(outPath, _logLines);
        _log($"日志已写入: {outPath}");

        return exitCode;
    }

    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    public static Task<int> DiagnoseAsync(string port, int baud, Action<string> log, bool scan = false)
        => scan ? RunAsync([port, "--scan"], log) : RunAsync([port, baud.ToString()], log);

    private static async Task<int> ScanBaudRatesAsync(string port)
    {
        int[] bauds = [9600, 115200, 57600, 38400, 230400, 172800, 74880, 28800];
        _log($"=== 波特率扫描 {port} ===");
        int bestScore = -1;
        int bestBaud = 0;

        foreach (int baud in bauds)
        {
            _log("");
            _log($"--- @{baud} ---");
            int score = await TestPortAsync(port, baud, sendPing: true, quiet: true);
            if (score > bestScore)
            {
                bestScore = score;
                bestBaud = baud;
            }
        }

        _log("");
        if (bestScore > 0)
            _log($"建议波特率: {bestBaud} (得分 {bestScore})");
        else
            _log("未找到可靠波特率，请检查接线/烧录/板子是否上电。");

        return bestScore > 0 ? 0 : 1;
    }

    private static async Task<int> TestPortAsync(string port, int baud, bool sendPing, bool quiet = false)
    {
        using var serial = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false
        };

        var buffer = new List<byte>(8192);
        try
        {
            serial.Open();
            serial.DiscardInBuffer();
            await Task.Delay(300);

            int score = 0;

            if (sendPing)
            {
                byte[] ping = SerialProtocol.PackFrame(SerialProtocol.HandshakeSend);
                serial.Write(ping, 0, ping.Length);
                if (!quiet)
                    _log($">> {SerialProtocol.HandshakeSend}{SerialProtocol.FrameSuffix}");

                await Task.Delay(1200);
                int after = serial.BytesToRead;
                if (after > 0)
                {
                    byte[] chunk = new byte[after];
                    serial.Read(chunk, 0, after);
                    buffer.AddRange(chunk);
                }

                if (!quiet)
                    PrintSample("PING后采样", buffer);
                score = ScoreBuffer(buffer);
            }

            if (!quiet)
            {
                string? noise = SerialDiagnostics.AnalyzeRawNoise(buffer, buffer.Count);
                if (noise != null)
                    _log($"[诊断] {noise}");

                _log($"得分: {score} (含 PONG{SerialProtocol.FrameSuffix}/OK{SerialProtocol.FrameSuffix})");
            }
            else
            {
                bool pong = SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.HandshakeReply + SerialProtocol.FrameSuffix)
                    || SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.HandshakeReply);
                bool ok = SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.StepDoneReply + SerialProtocol.FrameSuffix)
                    || SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.StepDoneReply);
                _log($"得分={score}, 字节={buffer.Count}, PONG={pong}, OK={ok}");
            }

            return score;
        }
        catch (Exception ex)
        {
            _log($"错误: {ex.Message}");
            return -1;
        }
        finally
        {
            if (serial.IsOpen)
                serial.Close();
        }
    }

    private static int ScoreBuffer(IReadOnlyList<byte> buffer)
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
        else if (SerialDiagnostics.ContainsAscii(buffer, SerialProtocol.StepDoneReply))
            score += 50;

        return score;
    }

    private static void PrintSample(string title, IReadOnlyList<byte> buffer)
    {
        _log($"[{title}] 共 {buffer.Count} 字节");
        if (buffer.Count == 0)
            return;

        int show = Math.Min(64, buffer.Count);
        var hex = new StringBuilder();
        var asc = new StringBuilder();
        for (int i = 0; i < show; i++)
        {
            hex.Append($"{buffer[i]:X2} ");
            char c = (char)buffer[i];
            asc.Append(c >= 32 && c < 127 ? c : '·');
        }

        _log($"HEX: {hex}");
        _log($"ASC: {asc}");

        var top = buffer.GroupBy(b => b).OrderByDescending(g => g.Count()).Take(5);
        _log("高频字节: " + string.Join(", ", top.Select(g => $"0x{g.Key:X2}({g.Count()})")));
    }
}