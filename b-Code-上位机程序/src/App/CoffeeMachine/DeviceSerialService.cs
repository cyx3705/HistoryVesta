using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using AppShell.Core.Logging;

namespace AppShell.App.CoffeeMachine;

public sealed class DeviceSerialService : IDisposable
{
    private const int BaudRate = 115200;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TimeoutInterval = TimeSpan.FromSeconds(3);

    private readonly BrewMachineState _state;
    private readonly IShellLog _log;
    private readonly SynchronizationContext? _ui;
    private readonly DispatcherTimer _heartbeat;
    private readonly StringBuilder _rx = new();
    private SerialPort? _port;
    private DateTime _lastSeenUtc;
    private int _nextId;
    private string _portName = "";

    public DeviceSerialService(BrewMachineState state, IShellLog log)
    {
        _state = state;
        _log = log;
        _ui = SynchronizationContext.Current;
        _heartbeat = new DispatcherTimer
        {
            Interval = HeartbeatInterval,
        };
        _heartbeat.Tick += (_, _) => OnHeartbeat();
    }

    public bool IsOpen => _port?.IsOpen == true;

    public IReadOnlyList<string> ListPorts()
        => SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    public string Connect(string portName)
    {
        Disconnect("reconnect");
        _portName = portName;
        _state.SetConnection(DeviceConnectionStatus.Connecting, portName, "正在打开串口");

        try
        {
            _port = new SerialPort(portName, BaudRate)
            {
                Encoding = Encoding.UTF8,
                NewLine = "\n",
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = false,
                RtsEnable = false,
            };
            _port.DataReceived += OnDataReceived;
            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            _lastSeenUtc = DateTime.UtcNow;
            _heartbeat.Start();
            SendHello();
            _log.Info("device", $"serial opened: {portName}");
            return $"串口已打开：{portName}，等待设备握手";
        }
        catch (Exception ex)
        {
            _state.SetConnection(DeviceConnectionStatus.Error, portName, ex.Message);
            CleanupPort();
            _log.Error("device", $"serial open failed {portName}: {ex.Message}");
            return $"串口打开失败：{ex.Message}";
        }
    }

    public string Disconnect(string reason = "user")
    {
        _heartbeat.Stop();
        CleanupPort();
        _state.SetConnection(DeviceConnectionStatus.Disconnected, _portName, $"已断开：{reason}");
        _log.Info("device", $"serial disconnected: {reason}");
        return "设备串口已断开";
    }

    public string SendBrewMode(string mode)
    {
        if (!IsOpen)
            return "设备未连接，命令未发送";

        WriteJson($$"""{"id":{{NextId()}},"cmd":"set","mode":"{{mode}}"}""");
        var message = $"device set {mode}: sent, waiting for ack/state";
        _log.Info("device", message);
        return message;
    }

    public string RequestState()
    {
        if (!IsOpen)
            return "设备未连接";

        WriteJson($$"""{"id":{{NextId()}},"cmd":"state"}""");
        return "已请求设备状态快照";
    }

    private void SendHello()
        => WriteJson($$"""{"id":{{NextId()}},"cmd":"hello","app":"cold-brew-host","proto":1}""");

    private void SendPing()
        => WriteJson($$"""{"id":{{NextId()}},"cmd":"ping"}""");

    private int NextId() => Interlocked.Increment(ref _nextId);

    private void WriteJson(string json)
    {
        try
        {
            _port?.Write(json + "\n");
            _log.Log(ShellLogLevel.Debug, "device.tx", json);
        }
        catch (Exception ex)
        {
            Post(() => _state.SetConnection(DeviceConnectionStatus.Error, _portName, ex.Message));
            _log.Error("device", $"serial write failed: {ex.Message}");
        }
    }

    private void OnHeartbeat()
    {
        if (!IsOpen)
            return;

        if (DateTime.UtcNow - _lastSeenUtc > TimeoutInterval)
        {
            _state.SetConnection(DeviceConnectionStatus.Timeout, _portName, "超过 3 秒未收到设备数据");
            _log.Warn("device", "serial heartbeat timeout");
            return;
        }

        SendPing();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var text = _port?.ReadExisting();
            if (string.IsNullOrEmpty(text))
                return;

            lock (_rx)
            {
                _rx.Append(text);
                while (true)
                {
                    var buffer = _rx.ToString();
                    var index = buffer.IndexOf('\n');
                    if (index < 0)
                        break;

                    var line = buffer[..index].Trim();
                    _rx.Remove(0, index + 1);
                    if (line.Length > 0)
                        HandleLine(line);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("device", $"serial receive failed: {ex.Message}");
            Post(() => _state.SetConnection(DeviceConnectionStatus.Error, _portName, ex.Message));
        }
    }

    private void HandleLine(string line)
    {
        _lastSeenUtc = DateTime.UtcNow;
        _log.Log(ShellLogLevel.Debug, "device.rx", line);

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "hello":
                    Post(() => _state.SetConnection(DeviceConnectionStatus.Online, _portName, "设备握手完成"));
                    RequestState();
                    break;
                case "pong":
                    Post(() => _state.SetConnection(DeviceConnectionStatus.Online, _portName, "心跳正常"));
                    break;
                case "ack":
                    var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                    Post(() => _state.SetConnection(ok ? DeviceConnectionStatus.Online : DeviceConnectionStatus.Error,
                        _portName, ok ? $"设备确认命令 #{id}" : $"设备拒绝命令 #{id}"));
                    break;
                case "state":
                    var snapshot = ParseSnapshot(root);
                    Post(() => _state.ApplyDeviceSnapshot(snapshot));
                    break;
                case "error":
                    var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "device error";
                    Post(() => _state.SetConnection(DeviceConnectionStatus.Error, _portName, message ?? "device error"));
                    break;
                default:
                    _log.Warn("device", $"unknown packet type: {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("device", $"drop malformed packet: {ex.Message}; line={line}");
        }
    }

    private static DeviceSnapshot ParseSnapshot(JsonElement root)
    {
        var rawMode = root.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() ?? "idle" : "idle";
        var mode = rawMode.ToLowerInvariant() switch
        {
            "up" => BrewMode.Up,
            "down" => BrewMode.Down,
            "fault" => BrewMode.Fault,
            _ => BrewMode.Idle,
        };

        return new DeviceSnapshot(
            mode,
            root.TryGetProperty("gpio15", out var gpio15) && gpio15.GetBoolean(),
            root.TryGetProperty("gpio16", out var gpio16) && gpio16.GetBoolean(),
            rawMode,
            root.TryGetProperty("error", out var err) ? err.GetString() : null,
            root.TryGetProperty("uptime", out var uptime) ? uptime.GetInt64() : 0,
            root.TryGetProperty("seq", out var seq) ? seq.GetInt32() : 0);
    }

    private void Post(Action action)
    {
        if (_ui == null)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private void CleanupPort()
    {
        if (_port == null)
            return;

        try
        {
            _port.DataReceived -= OnDataReceived;
            if (_port.IsOpen)
                _port.Close();
            _port.Dispose();
        }
        catch
        {
            // Best-effort cleanup; connection state is reported by caller.
        }
        finally
        {
            _port = null;
        }
    }

    public void Dispose()
    {
        _heartbeat.Stop();
        CleanupPort();
    }
}
