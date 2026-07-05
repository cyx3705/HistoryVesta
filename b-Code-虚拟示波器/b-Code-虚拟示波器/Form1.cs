using System.IO.Ports;
using System.Text;

namespace b_Code_虚拟示波器
{
    public partial class Form1 : Form
    {
        private const int MaxSamples = 4000;
        private const double TimeWindowMs = 5000.0;
        private const double DemoFrequencyHz = 1.0;

        private readonly List<ScopeSample> _samples = new();
        private readonly object _sampleLock = new();
        private readonly StringBuilder _serialBuffer = new();

        private SerialPort? _serialPort;
        private bool _demoMode;

        private int _lastLevel = -1;
        private DateTime _lastEdgeTime = DateTime.MinValue;
        private double _measuredFrequency;

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            RefreshPortList();
            timerRefresh.Start();
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            StopAcquisition();
        }

        private void RefreshPortList()
        {
            var selected = cmbPorts.SelectedItem as string;
            cmbPorts.Items.Clear();
            foreach (var port in SerialPort.GetPortNames().OrderBy(p => p))
            {
                cmbPorts.Items.Add(port);
            }

            if (!string.IsNullOrEmpty(selected) && cmbPorts.Items.Contains(selected))
            {
                cmbPorts.SelectedItem = selected;
            }
            else if (cmbPorts.Items.Count > 0)
            {
                cmbPorts.SelectedIndex = 0;
            }
        }

        private void BtnRefreshPorts_Click(object? sender, EventArgs e)
        {
            RefreshPortList();
        }

        private void BtnConnect_Click(object? sender, EventArgs e)
        {
            if (cmbPorts.SelectedItem is not string portName)
            {
                MessageBox.Show("请先选择串口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                StopAcquisition();

                _serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    Encoding = Encoding.ASCII
                };
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();

                _demoMode = false;
                ResetSamples();
                UpdateUiState(true, $"已连接 {portName}");
            }
            catch (Exception ex)
            {
                _serialPort?.Dispose();
                _serialPort = null;
                MessageBox.Show($"连接失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateUiState(false, "连接失败");
            }
        }

        private void BtnDisconnect_Click(object? sender, EventArgs e)
        {
            StopAcquisition();
            UpdateUiState(false, "未连接");
        }

        private void BtnDemo_Click(object? sender, EventArgs e)
        {
            StopAcquisition();
            _demoMode = true;
            ResetSamples();
            UpdateUiState(true, "演示模式（1Hz 方波）");
        }

        private void StopAcquisition()
        {
            _demoMode = false;

            if (_serialPort != null)
            {
                _serialPort.DataReceived -= SerialPort_DataReceived;
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        private void ResetSamples()
        {
            lock (_sampleLock)
            {
                _samples.Clear();
            }

            _lastLevel = -1;
            _lastEdgeTime = DateTime.MinValue;
            _measuredFrequency = 0;

            _serialBuffer.Clear();
            lblFrequency.Text = "频率：-- Hz";
        }

        private void UpdateUiState(bool active, string status)
        {
            btnConnect.Enabled = !active;
            btnDisconnect.Enabled = active || _demoMode;
            btnDemo.Enabled = !active;
            cmbPorts.Enabled = !active;
            btnRefreshPorts.Enabled = !active;
            lblStatus.Text = $"状态：{status}";
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                return;
            }

            try
            {
                var data = _serialPort.ReadExisting();
                ProcessSerialData(data);
            }
            catch
            {
                // 忽略读取超时等瞬时错误
            }
        }

        private void ProcessSerialData(string data)
        {
            _serialBuffer.Append(data);

            while (_serialBuffer.Length > 0)
            {
                var ch = _serialBuffer[0];
                _serialBuffer.Remove(0, 1);

                switch (ch)
                {
                    case 'H':
                        AddSample(1);
                        break;
                    case 'L':
                        AddSample(0);
                        break;
                    case 'R':
                        AddSample(1);
                        break;
                    case 'S':
                        AddSample(0);
                        break;
                }
            }
        }

        private void AddSample(int level)
        {
            var now = DateTime.UtcNow;

            lock (_sampleLock)
            {
                if (_lastLevel >= 0 && _lastLevel != level)
                {
                    _samples.Add(new ScopeSample(now, _lastLevel));
                }

                _samples.Add(new ScopeSample(now, level));
                TrimSamples(now);
            }

            if (_lastLevel >= 0 && _lastLevel != level)
            {
                if (_lastEdgeTime != DateTime.MinValue)
                {
                    var halfPeriodMs = (now - _lastEdgeTime).TotalMilliseconds;
                    if (halfPeriodMs > 200.0 && halfPeriodMs < 1200.0)
                    {
                        _measuredFrequency = 1000.0 / (halfPeriodMs * 2.0);
                    }
                }

                _lastEdgeTime = now;
            }

            _lastLevel = level;
        }

        private void TrimSamples(DateTime now)
        {
            var cutoff = now.AddMilliseconds(-TimeWindowMs * 1.5);
            while (_samples.Count > 0 && _samples[0].Time < cutoff)
            {
                _samples.RemoveAt(0);
            }

            while (_samples.Count > MaxSamples)
            {
                _samples.RemoveAt(0);
            }
        }

        private void TimerRefresh_Tick(object? sender, EventArgs e)
        {
            if (_demoMode)
            {
                GenerateDemoSamples();
            }

            if (_measuredFrequency > 0)
            {
                lblFrequency.Text = $"频率：{_measuredFrequency:F1} Hz";
            }

            pnlScope.Invalidate();
        }

        private void GenerateDemoSamples()
        {
            var now = DateTime.UtcNow;
            var halfPeriodMs = 1000.0 / (DemoFrequencyHz * 2.0);
            var cycleMs = (now.TimeOfDay.TotalMilliseconds) % (halfPeriodMs * 2.0);
            var level = cycleMs < halfPeriodMs ? 1 : 0;

            lock (_sampleLock)
            {
                if (_samples.Count == 0 || _samples[^1].Level != level)
                {
                    if (_samples.Count > 0)
                    {
                        _samples.Add(new ScopeSample(now, _samples[^1].Level));
                    }

                    _samples.Add(new ScopeSample(now, level));
                    TrimSamples(now);
                }
            }

            _measuredFrequency = DemoFrequencyHz;
            _lastLevel = level;
        }

        private void PnlScope_Resize(object? sender, EventArgs e)
        {
            pnlScope.Invalidate();
        }

        private void PnlScope_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var rect = pnlScope.ClientRectangle;
            g.Clear(Color.FromArgb(10, 14, 20));

            DrawGrid(g, rect);
            DrawWaveform(g, rect);
            DrawLabels(g, rect, Font);
        }

        private static void DrawGrid(Graphics g, Rectangle rect)
        {
            using var gridPen = new Pen(Color.FromArgb(35, 55, 75), 1);
            using var majorPen = new Pen(Color.FromArgb(55, 85, 110), 1);

            const int divX = 10;
            const int divY = 8;
            var stepX = rect.Width / (double)divX;
            var stepY = rect.Height / (double)divY;
            var midY = rect.Height / 2.0;

            for (var i = 0; i <= divX; i++)
            {
                var x = (float)(i * stepX);
                g.DrawLine(i % 5 == 0 ? majorPen : gridPen, x, 0, x, rect.Height);
            }

            for (var i = 0; i <= divY; i++)
            {
                var y = (float)(i * stepY);
                g.DrawLine(i == divY / 2 ? majorPen : gridPen, 0, y, rect.Width, y);
            }

            g.DrawLine(majorPen, 0, (float)midY, rect.Width, (float)midY);
        }

        private void DrawWaveform(Graphics g, Rectangle rect)
        {
            ScopeSample[] snapshot;
            lock (_sampleLock)
            {
                snapshot = _samples.ToArray();
            }

            if (snapshot.Length < 2)
            {
                using var hintBrush = new SolidBrush(Color.FromArgb(120, 160, 190));
                g.DrawString("等待方波数据…", Font, hintBrush, 20, 20);
                return;
            }

            var now = DateTime.UtcNow;
            var windowStart = now.AddMilliseconds(-TimeWindowMs);
            var highY = rect.Height * 0.2f;
            var lowY = rect.Height * 0.8f;

            using var wavePen = new Pen(Color.FromArgb(0, 220, 130), 2);
            using var fillBrush = new SolidBrush(Color.FromArgb(30, 0, 220, 130));

            var points = new List<PointF>();
            var firstVisible = snapshot.FirstOrDefault(s => s.Time >= windowStart);
            if (firstVisible.Time == default)
            {
                firstVisible = snapshot[0];
            }

            points.Add(new PointF(
                TimeToX(firstVisible.Time, windowStart, rect.Width),
                firstVisible.Level == 1 ? highY : lowY));

            foreach (var sample in snapshot.Where(s => s.Time >= windowStart))
            {
                points.Add(new PointF(
                    TimeToX(sample.Time, windowStart, rect.Width),
                    sample.Level == 1 ? highY : lowY));
            }

            points.Add(new PointF(rect.Width, points[^1].Y));

            if (points.Count >= 2)
            {
                g.DrawLines(wavePen, points.ToArray());

                var fillPoints = new List<PointF>(points)
                {
                    new(rect.Width, lowY),
                    new(points[0].X, lowY)
                };
                g.FillPolygon(fillBrush, fillPoints.ToArray());
            }
        }

        private static float TimeToX(DateTime time, DateTime windowStart, int width)
        {
            var elapsed = (time - windowStart).TotalMilliseconds;
            return (float)Math.Clamp(elapsed / TimeWindowMs * width, 0, width);
        }

        private static void DrawLabels(Graphics g, Rectangle rect, Font font)
        {
            using var brush = new SolidBrush(Color.FromArgb(150, 180, 200));
            g.DrawString("500 ms/div", font, brush, rect.Width - 110, 8);
            g.DrawString("高", font, brush, 8, rect.Height * 0.2f - 20);
            g.DrawString("低", font, brush, 8, rect.Height * 0.8f - 20);
        }

        private readonly record struct ScopeSample(DateTime Time, int Level);
    }
}