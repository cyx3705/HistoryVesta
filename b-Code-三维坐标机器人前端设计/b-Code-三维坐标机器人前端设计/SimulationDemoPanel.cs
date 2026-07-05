using System.IO.Ports;

namespace b_Code_三维坐标机器人前端设计;

public sealed class SimulationDemoPanel : UserControl
{
    private readonly ComboBox _cmbPort;
    private readonly ComboBox _cmbBaud;
    private readonly CheckBox _chkRawMonitor;
    private readonly Button _btnRefresh;
    private readonly Button _btnConnect;
    private readonly Button _btnSendTest;
    private readonly Button _btnScanBaud;
    private readonly Button _btnSimulate;
    private readonly Button _btnSerialDemo;
    private readonly Button _btnStop;
    private readonly Label _lblStatus;

    private readonly SerialPortService _serial = new();
    private RobotDemoRunner? _runner;

    public event Action? SceneRefreshRequested;
    public event Action<string>? LogProduced;

    public SimulationDemoPanel()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(12);

        var title = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Text = "Simulation / Serial / Diagnostics"
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 4, 0, 8),
            Text = "Logs are routed to the console panel."
        };

        var topFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true
        };

        _cmbPort = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbBaud = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbBaud.Items.AddRange(["9600", "115200", "57600", "38400"]);
        _cmbBaud.SelectedIndex = 0;
        _chkRawMonitor = new CheckBox
        {
            Text = "Raw Monitor",
            Checked = false,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };

        _btnRefresh = new Button { Text = "Refresh Ports", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _btnConnect = new Button { Text = "Connect", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _btnSendTest = new Button { Text = "Send Test", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _btnScanBaud = new Button { Text = "Scan Baud", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        var btnCliTest = new Button { Text = "CLI Diagnose", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _btnSimulate = new Button { Text = "Simulate", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _btnSerialDemo = new Button { Text = "Run With Serial", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _btnStop = new Button { Text = "Stop", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };

        topFlow.Controls.Add(new Label { Text = "COM:", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        topFlow.Controls.Add(_cmbPort);
        topFlow.Controls.Add(new Label { Text = "Baud:", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        topFlow.Controls.Add(_cmbBaud);
        topFlow.Controls.Add(_chkRawMonitor);
        topFlow.Controls.Add(_btnRefresh);
        topFlow.Controls.Add(_btnConnect);
        topFlow.Controls.Add(_btnSendTest);
        topFlow.Controls.Add(_btnScanBaud);
        topFlow.Controls.Add(btnCliTest);
        topFlow.Controls.Add(_btnSimulate);
        topFlow.Controls.Add(_btnSerialDemo);
        topFlow.Controls.Add(_btnStop);

        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 4),
            Text = "Status: disconnected"
        };

        Controls.Add(_lblStatus);
        Controls.Add(topFlow);
        Controls.Add(hint);
        Controls.Add(title);

        _btnRefresh.Click += (_, _) => RefreshPorts();
        _btnConnect.Click += async (_, _) => await ToggleConnectionAsync();
        _btnSendTest.Click += async (_, _) => await SendTestLineAsync();
        _btnScanBaud.Click += async (_, _) => await ScanBaudRatesAsync();
        btnCliTest.Click += async (_, _) => await RunCliSerialTestAsync();
        _btnSimulate.Click += async (_, _) => await StartDemoAsync(simulateOnly: true);
        _btnSerialDemo.Click += async (_, _) => await StartDemoAsync(simulateOnly: false);
        _btnStop.Click += (_, _) => _runner?.Stop();

        _serial.RawDataReceived += OnRawDataReceived;
        _serial.FrameReceived += OnFrameReceived;
        Disposed += (_, _) => _serial.Dispose();
        RefreshPorts();
    }

    public void Bind(RobotDemoRunner runner)
    {
        _runner = runner;
        _runner.Log += AppendLog;
        _runner.SceneChanged += () => SceneRefreshRequested?.Invoke();
    }

    private int _totalRawBytes;
    private int _totalOBytes;
    private string? _lastNoiseHint;

    private void OnFrameReceived(string frame)
    {
        AppendLog($"[FRAME] {frame}{SerialProtocol.FrameSuffix}");
    }

    private void OnRawDataReceived(SerialRxPacket packet)
    {
        _totalRawBytes += packet.Bytes.Length;
        _totalOBytes += packet.Bytes.Count(b => b == 0x4F);

        if (!_chkRawMonitor.Checked || packet.Bytes.Length < 4)
            return;

        AppendLog($"[RAW {packet.Bytes.Length}B] HEX: {packet.Hex}");
        AppendLog($"[RAW] ASCII: {packet.AsciiPreview}");

        if (packet.Bytes.Length > 0 && packet.Bytes.All(b => b == 0x4F))
            AppendLog($"[DIAG] {BaudRateScanner.ExplainRepeatedO(_totalOBytes, _totalRawBytes)}");

        string? noise = SerialDiagnostics.AnalyzeRawNoise(packet.Bytes, packet.Bytes.Length);
        if (noise != null && noise != _lastNoiseHint)
        {
            _lastNoiseHint = noise;
            AppendLog($"[DIAG] {noise}");
        }
    }

    private async Task RunCliSerialTestAsync()
    {
        if (_cmbPort.SelectedItem is not string port)
        {
            AppendLog("Please select COM port.");
            return;
        }

        if (_serial.IsOpen)
            _serial.Close();

        int baud = int.Parse(_cmbBaud.SelectedItem?.ToString() ?? SerialDefaults.BaudRateText);
        SetButtonsEnabled(false);
        try
        {
            AppendLog($"CLI diagnose {port} @{baud}...");
            await SerialPortCliTester.DiagnoseAsync(port, baud, AppendLog, scan: true);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task ScanBaudRatesAsync()
    {
        if (_cmbPort.SelectedItem is not string port)
        {
            AppendLog("Please select COM port.");
            return;
        }

        if (_serial.IsOpen)
            _serial.Close();

        AppendLog("Start baud scan...");
        SetButtonsEnabled(false);
        try
        {
            await BaudRateScanner.ScanAsync(port, AppendLog);
            AppendLog("Baud scan finished.");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task SendTestLineAsync()
    {
        if (!_serial.IsOpen || !_serial.IsHandshaked)
        {
            AppendLog("Please connect and handshake first.");
            return;
        }

        const string test = "100,500,600,1,1,1";
        SetButtonsEnabled(false);
        try
        {
            AppendLog($">> {test}{SerialProtocol.FrameSuffix}");
            _serial.SendFrame(test);
            string ok = await _serial.WaitForFrameAsync(
                SerialProtocol.StepDoneReply,
                CancellationToken.None,
                RobotDemoRunner.StepDurationMs + 3000);
            AppendLog($"<< {ok}{SerialProtocol.FrameSuffix}");
        }
        catch (Exception ex)
        {
            AppendLog($"Test failed: {ex.Message}");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void RefreshPorts()
    {
        string? selected = _cmbPort.SelectedItem as string;
        _cmbPort.Items.Clear();
        string[] ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
        foreach (string port in ports)
            _cmbPort.Items.Add(port);

        if (selected != null && _cmbPort.Items.Contains(selected))
            _cmbPort.SelectedItem = selected;
        else if (_cmbPort.Items.Count > 0)
            _cmbPort.SelectedIndex = 0;
    }

    private async Task ToggleConnectionAsync()
    {
        if (_serial.IsOpen)
        {
            _serial.Close();
            _btnConnect.Text = "Connect";
            _lblStatus.Text = "Status: disconnected";
            AppendLog("Serial closed.");
            return;
        }

        if (_cmbPort.SelectedItem is not string port)
        {
            AppendLog("Please select COM port.");
            return;
        }

        SetButtonsEnabled(false);
        try
        {
            _totalRawBytes = 0;
            _totalOBytes = 0;
            _lastNoiseHint = null;

            var (baud, score) = await SerialAutoBaud.DetectAsync(port, AppendLog);
            if (score <= 0)
                baud = int.Parse(_cmbBaud.SelectedItem?.ToString() ?? SerialDefaults.BaudRateText);

            _serial.Open(port, baud);
            AppendLog($"Open {port} @{baud}, handshaking...");
            await _serial.PerformHandshakeAsync(AppendLog);

            _btnConnect.Text = "Disconnect";
            _lblStatus.Text = $"Status: connected {port} @{baud}";
            AppendLog("Handshake OK.");
        }
        catch (Exception ex)
        {
            if (_serial.IsOpen)
                _serial.Close();
            _btnConnect.Text = "Connect";
            _lblStatus.Text = "Status: handshake failed";
            AppendLog($"Connect failed: {ex.Message}");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task StartDemoAsync(bool simulateOnly)
    {
        if (_runner == null)
            return;

        try
        {
            SetButtonsEnabled(false);
            if (simulateOnly)
                await _runner.RunSimulationAsync();
            else
                await _runner.RunWithSerialAsync(_serial);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Demo stopped.");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _btnSimulate.Enabled = enabled;
        _btnSerialDemo.Enabled = enabled;
        _btnRefresh.Enabled = enabled;
        _btnConnect.Enabled = enabled;
        _btnSendTest.Enabled = enabled;
        _btnScanBaud.Enabled = enabled;
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(text));
            return;
        }

        LogProduced?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
    }
}
