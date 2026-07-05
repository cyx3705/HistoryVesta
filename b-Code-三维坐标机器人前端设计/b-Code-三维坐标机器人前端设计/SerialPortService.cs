using System.IO.Ports;
using System.Text;

namespace b_Code_三维坐标机器人前端设计;

public sealed class SerialPortService : IDisposable
{
    private const int RawSampleCap = 2048;
    private const int MaxRxBuffer = 512;

    private SerialPort? _port;
    private readonly List<byte> _rxBytes = [];
    private readonly List<string> _recentFrames = [];
    private readonly object _sync = new();
    private string _waitFrame = string.Empty;
    private TaskCompletionSource<string>? _frameWaiter;

    public bool IsOpen => _port?.IsOpen == true;
    public bool IsHandshaked { get; private set; }

    public event Action<SerialRxPacket>? RawDataReceived;
    public event Action<string>? FrameReceived;

    public void Open(string portName, int baudRate = SerialDefaults.BaudRate)
    {
        Close();
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false
        };
        _port.DataReceived += OnDataReceived;
        _port.Open();
        _port.DiscardOutBuffer();

        lock (_sync)
        {
            _rxBytes.Clear();
            _recentFrames.Clear();
        }

        IsHandshaked = false;
    }

    public async Task PerformHandshakeAsync(Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (_port?.IsOpen != true)
            throw new InvalidOperationException("串口未打开。");

        DiscardBufferedInput();
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        for (int attempt = 1; attempt <= SerialDefaults.HandshakeRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            log?.Invoke($">> {SerialProtocol.HandshakeSend}{SerialProtocol.FrameSuffix} ({attempt}/{SerialDefaults.HandshakeRetries})");
            SendFrame(SerialProtocol.HandshakeSend);

            try
            {
                string reply = await WaitForFrameAsync(
                    SerialProtocol.HandshakeReply,
                    cancellationToken,
                    SerialDefaults.HandshakeTimeoutMs).ConfigureAwait(false);

                log?.Invoke($"<< {reply}{SerialProtocol.FrameSuffix}");
                IsHandshaked = true;
                return;
            }
            catch (TimeoutException ex)
            {
                lastError = ex;
                log?.Invoke($"未收到 {SerialProtocol.HandshakeReply}{SerialProtocol.FrameSuffix}（第 {attempt} 次）");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }

        string recent;
        lock (_sync)
        {
            recent = _recentFrames.Count == 0
                ? "(无完整帧)"
                : string.Join(" | ", _recentFrames.TakeLast(8).Select(f => f + SerialProtocol.FrameSuffix));
        }

        throw new TimeoutException(
            $"握手失败：{SerialDefaults.HandshakeRetries} 次未收到 {SerialProtocol.HandshakeReply}{SerialProtocol.FrameSuffix}。最近帧: {recent}",
            lastError);
    }

    public void DiscardBufferedInput()
    {
        lock (_sync)
        {
            _rxBytes.Clear();
            _recentFrames.Clear();
            _frameWaiter?.TrySetCanceled();
            _frameWaiter = null;
            _waitFrame = string.Empty;
        }

        if (_port?.IsOpen == true)
            _port.DiscardInBuffer();
    }

    public void Close()
    {
        if (_port == null)
            return;

        if (_port.IsOpen)
            _port.Close();

        _port.DataReceived -= OnDataReceived;
        _port.Dispose();
        _port = null;
        IsHandshaked = false;

        lock (_sync)
        {
            _rxBytes.Clear();
            _recentFrames.Clear();
            _frameWaiter?.TrySetCanceled();
            _frameWaiter = null;
        }
    }

    public void SendFrame(string payload)
    {
        if (_port?.IsOpen != true)
            throw new InvalidOperationException("串口未打开。");

        byte[] frame = SerialProtocol.PackFrame(payload);
        _port.Write(frame, 0, frame.Length);
    }

    public void SendRaw(byte[] bytes)
    {
        if (_port?.IsOpen != true)
            throw new InvalidOperationException("串口未打开。");

        _port.Write(bytes, 0, bytes.Length);
    }

    public async Task<string> WaitForFrameAsync(string expectedPayload, CancellationToken cancellationToken, int timeoutMs = 10000)
    {
        lock (_sync)
        {
            _waitFrame = expectedPayload;
            if (TryTakeMatchedFrameLocked(out string immediate))
                return immediate;
            _frameWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            TaskCompletionSource<string> waiter;
            lock (_sync)
                waiter = _frameWaiter ?? throw new InvalidOperationException("等待状态已失效。");

            return await waiter.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
                _frameWaiter = null;
            throw new TimeoutException($"等待串口回应 '{expectedPayload}{SerialProtocol.FrameSuffix}' 超时。");
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port?.IsOpen != true)
            return;

        try
        {
            int count = _port.BytesToRead;
            if (count <= 0)
                return;

            byte[] chunk = new byte[count];
            _port.Read(chunk, 0, count);

            lock (_sync)
                ProcessChunkLocked(chunk);
        }
        catch
        {
            // 忽略偶发读取错误
        }
    }

    private void ProcessChunkLocked(byte[] chunk)
    {
        RawDataReceived?.Invoke(SerialRxPacket.FromBytes(chunk));

        foreach (byte b in chunk)
        {
            if (_rxBytes.Count >= MaxRxBuffer)
                _rxBytes.Clear();
            _rxBytes.Add(b);
        }

        if (!SerialProtocol.TryExtractFrames(_rxBytes, out List<string> frames))
            return;

        foreach (string frame in frames)
        {
            _recentFrames.Add(frame);
            if (_recentFrames.Count > 32)
                _recentFrames.RemoveAt(0);

            FrameReceived?.Invoke(frame);

            if (_frameWaiter != null &&
                string.Equals(frame, _waitFrame, StringComparison.OrdinalIgnoreCase))
            {
                _frameWaiter.TrySetResult(frame);
                _frameWaiter = null;
            }
        }
    }

    private bool TryTakeMatchedFrameLocked(out string frame)
    {
        for (int i = _recentFrames.Count - 1; i >= 0; i--)
        {
            string candidate = _recentFrames[i];
            if (!string.Equals(candidate, _waitFrame, StringComparison.OrdinalIgnoreCase))
                continue;

            frame = candidate;
            return true;
        }

        frame = string.Empty;
        return false;
    }

    public void Dispose() => Close();
}
