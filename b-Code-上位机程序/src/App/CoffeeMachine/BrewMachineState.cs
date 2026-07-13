using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AppShell.Core.Logging;

namespace AppShell.App.CoffeeMachine;

public sealed class BrewMachineState : INotifyPropertyChanged
{
    private readonly IShellLog _log;
    private readonly BrewPersistenceService? _persistence;
    private readonly DispatcherTimer _timer;
    private BrewMode _mode = BrewMode.Idle;
    private BrewCyclePhase _phase = BrewCyclePhase.None;
    private BrewCyclePhase _pausedPhase = BrewCyclePhase.None;
    private BrewRecipe? _activeRecipe;
    private double _phaseElapsedSeconds;
    private double _sequenceElapsedSeconds;
    private double _upperLevel = 38;
    private double _lowerLevel = 62;
    private int _operationCount;
    private int _currentCycle;
    private int _completedSteps;
    private bool _isDeviceMode;
    private DeviceConnectionStatus _connectionStatus = DeviceConnectionStatus.Offline;
    private string _devicePortName = "";
    private string _deviceMessage = "模拟模式";
    private DateTime? _lastDeviceDataAt;
    private DateTime _lastChanged = DateTime.Now;

    public BrewMachineState(IShellLog log, BrewPersistenceService? persistence = null)
    {
        _log = log;
        _persistence = persistence;
        Gpios =
        [
            new GpioLine("GPIO15", "上液泵"),
            new GpioLine("GPIO16", "降液泵"),
        ];
        History = new ObservableCollection<BrewHistoryEntry>(
            _persistence?.ReadHistory(80) ?? Array.Empty<BrewHistoryEntry>());

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _timer.Tick += (_, _) => Tick(_timer.Interval.TotalSeconds);
        _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<string>? DeviceOutputRequested;

    public ObservableCollection<GpioLine> Gpios { get; }

    public ObservableCollection<BrewHistoryEntry> History { get; }

    public BrewMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value)
                return;

            _mode = value;
            NotifyStatus();
        }
    }

    public BrewCyclePhase Phase
    {
        get => _phase;
        private set
        {
            if (_phase == value)
                return;

            _phase = value;
            NotifyStatus();
        }
    }

    public double UpperLevel
    {
        get => _upperLevel;
        private set
        {
            var next = Math.Clamp(value, 5, 95);
            if (Math.Abs(_upperLevel - next) < 0.01)
                return;

            _upperLevel = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpperLevelText));
        }
    }

    public double LowerLevel
    {
        get => _lowerLevel;
        private set
        {
            var next = Math.Clamp(value, 5, 95);
            if (Math.Abs(_lowerLevel - next) < 0.01)
                return;

            _lowerLevel = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LowerLevelText));
        }
    }

    public string UpperLevelText => $"{UpperLevel:0}%";

    public string LowerLevelText => $"{LowerLevel:0}%";

    public string StatusText => Mode switch
    {
        BrewMode.Up => "上液中",
        BrewMode.Down => "降液中",
        BrewMode.Paused => "已暂停",
        BrewMode.Complete => "循环完成",
        BrewMode.Fault => "互锁故障",
        _ => "待机",
    };

    public string DirectionText => Mode switch
    {
        BrewMode.Up => "下舱体 -> 上舱体",
        BrewMode.Down => "上舱体 -> 下舱体",
        BrewMode.Paused => "动画冻结",
        BrewMode.Complete => "已完成",
        BrewMode.Fault => "输出已切断",
        _ => "无流动",
    };

    public string StageText
    {
        get
        {
            if (IsSequenceActive)
                return TimelineText;

            return Mode switch
            {
                BrewMode.Up => "手动上液",
                BrewMode.Down => "手动降液",
                BrewMode.Paused => "编排暂停",
                BrewMode.Complete => "编排完成",
                BrewMode.Fault => "等待人工复位",
                _ => "手动待机",
            };
        }
    }

    public string InterlockText => Gpios[0].IsActive && Gpios[1].IsActive
        ? "告警：GPIO15 / GPIO16 同时有效"
        : "互锁正常";

    public bool IsRunning => Mode is BrewMode.Up or BrewMode.Down;

    public bool IsSequenceRunning => IsSequenceActive && Mode is BrewMode.Up or BrewMode.Down;

    public bool IsSequencePaused => IsSequenceActive && Mode == BrewMode.Paused;

    public bool IsSequenceActive => _activeRecipe != null && Phase is not BrewCyclePhase.None and not BrewCyclePhase.Complete and not BrewCyclePhase.Aborted;

    public bool IsParameterLocked => IsSequenceActive;

    public int OperationCount
    {
        get => _operationCount;
        private set
        {
            if (_operationCount == value)
                return;

            _operationCount = value;
            OnPropertyChanged();
        }
    }

    public int CurrentCycle
    {
        get => _currentCycle;
        private set
        {
            if (_currentCycle == value)
                return;

            _currentCycle = value;
            NotifyProgress();
        }
    }

    public int TotalCycles => _activeRecipe?.Cycles ?? 0;

    public int CompletedSteps
    {
        get => _completedSteps;
        private set
        {
            if (_completedSteps == value)
                return;

            _completedSteps = value;
            NotifyProgress();
        }
    }

    public int TotalSteps => _activeRecipe?.StepCount ?? 0;

    public double ProgressPercent => _activeRecipe == null || _activeRecipe.TotalSeconds <= 0
        ? 0
        : Math.Clamp(_sequenceElapsedSeconds / _activeRecipe.TotalSeconds * 100, 0, 100);

    public string CurrentStepText => Phase switch
    {
        BrewCyclePhase.Up => "上液",
        BrewCyclePhase.UpDwell => "上液停留",
        BrewCyclePhase.Down => "降液",
        BrewCyclePhase.DownDwell => "降液停留",
        BrewCyclePhase.Complete => "完成",
        BrewCyclePhase.Aborted => "已终止",
        _ => "未启动",
    };

    public string StepElapsedText => _activeRecipe == null
        ? "00:00/00:00"
        : $"{FormatSeconds(_phaseElapsedSeconds)}/{FormatSeconds(CurrentPhaseDurationSeconds)}";

    public string RemainingText => _activeRecipe == null
        ? "00:00"
        : FormatSeconds(Math.Max(0, _activeRecipe.TotalSeconds - _sequenceElapsedSeconds));

    public string TimelineText
    {
        get
        {
            if (_activeRecipe == null)
                return "未启动";

            var cycle = Math.Clamp(CurrentCycle, 1, _activeRecipe.Cycles);
            return $"第 {cycle}/{_activeRecipe.Cycles} 循环 · {CurrentStepText} {StepElapsedText} · 剩余 {RemainingText}";
        }
    }

    public string LastChangedText => _lastChanged.ToString("HH:mm:ss");

    public bool IsDeviceMode
    {
        get => _isDeviceMode;
        private set
        {
            if (_isDeviceMode == value)
                return;

            _isDeviceMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RuntimeModeText));
        }
    }

    public DeviceConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (_connectionStatus == value)
                return;

            _connectionStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(DeviceMessage));
        }
    }

    public string RuntimeModeText => IsDeviceMode ? "设备" : "模拟";

    public string ConnectionText => ConnectionStatus switch
    {
        DeviceConnectionStatus.Connecting => $"连接中 {DevicePortName}",
        DeviceConnectionStatus.Online => $"在线 {DevicePortName}",
        DeviceConnectionStatus.Timeout => $"超时 {DevicePortName}",
        DeviceConnectionStatus.Disconnected => "已断开",
        DeviceConnectionStatus.Error => $"通讯异常 {DevicePortName}",
        _ => "离线",
    };

    public string DevicePortName
    {
        get => _devicePortName;
        private set
        {
            if (_devicePortName == value)
                return;

            _devicePortName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionText));
        }
    }

    public string DeviceMessage
    {
        get => _deviceMessage;
        private set
        {
            if (_deviceMessage == value)
                return;

            _deviceMessage = value;
            OnPropertyChanged();
        }
    }

    public string LastDeviceDataText => _lastDeviceDataAt?.ToString("HH:mm:ss") ?? "--:--:--";

    private double CurrentPhaseDurationSeconds => _activeRecipe == null
        ? 0
        : Phase switch
        {
            BrewCyclePhase.Up => _activeRecipe.UpSeconds,
            BrewCyclePhase.UpDwell => _activeRecipe.UpDwellSeconds,
            BrewCyclePhase.Down => _activeRecipe.DownSeconds,
            BrewCyclePhase.DownDwell => _activeRecipe.DownDwellSeconds,
            _ => 0,
        };

    public string StartUp()
    {
        if (IsParameterLocked)
            return "编排运行中，手动上液被锁定";

        return SetMode(BrewMode.Up);
    }

    public string StartDown()
    {
        if (IsParameterLocked)
            return "编排运行中，手动降液被锁定";

        return SetMode(BrewMode.Down);
    }

    public string Stop()
    {
        if (IsSequenceActive)
            return AbortSequence("手动停止");

        return SetMode(BrewMode.Idle);
    }

    public string StartSequence(BrewRecipe recipe)
    {
        if (IsParameterLocked)
            return "已有编排正在运行，请先终止";
        if (IsDeviceMode && ConnectionStatus != DeviceConnectionStatus.Online)
            return "设备模式下必须在线后才能启动固定循环";

        _activeRecipe = recipe;
        _phaseElapsedSeconds = 0;
        _sequenceElapsedSeconds = 0;
        CurrentCycle = 1;
        CompletedSteps = 0;
        BeginPhase(BrewCyclePhase.Up);
        Touch();
        NotifyProgress();

        var message = $"固定循环启动：{recipe.Cycles} 次，上液 {recipe.UpSeconds}s，停留 {recipe.UpDwellSeconds}s，降液 {recipe.DownSeconds}s，停留 {recipe.DownDwellSeconds}s";
        _log.Info("brew", message);
        RecordHistory("run", message);
        return message;
    }

    public string PauseSequence()
    {
        if (!IsSequenceActive)
            return "没有正在运行的编排";
        if (Mode == BrewMode.Paused)
            return "编排已处于暂停状态";

        _pausedPhase = Phase;
        Gpios[0].IsActive = false;
        Gpios[1].IsActive = false;
        Mode = BrewMode.Paused;
        Touch();
        NotifyProgress();

        var message = $"编排暂停：{TimelineText}";
        _log.Info("brew", message);
        RecordHistory("run", message);
        return message;
    }

    public string ResumeSequence()
    {
        if (!IsSequenceActive)
            return "没有可继续的编排";
        if (Mode != BrewMode.Paused)
            return "编排未暂停";

        BeginPhase(_pausedPhase == BrewCyclePhase.None ? Phase : _pausedPhase, resetElapsed: false);
        Touch();
        NotifyProgress();

        var message = $"编排继续：{TimelineText}";
        _log.Info("brew", message);
        RecordHistory("run", message);
        return message;
    }

    public string AbortSequence(string reason = "用户终止")
    {
        if (!IsSequenceActive)
            return SetMode(BrewMode.Idle);

        Gpios[0].IsActive = false;
        Gpios[1].IsActive = false;
        Phase = BrewCyclePhase.Aborted;
        Mode = BrewMode.Idle;
        _activeRecipe = null;
        _pausedPhase = BrewCyclePhase.None;
        Touch();
        NotifyProgress();

        var message = $"编排已终止：{reason}，输出已关闭";
        _log.Warn("brew", message);
        RecordHistory("run", message);
        return message;
    }

    public bool ValidateInterlock()
    {
        if (Gpios[0].IsActive && Gpios[1].IsActive)
        {
            Mode = BrewMode.Fault;
            Gpios[0].IsActive = false;
            Gpios[1].IsActive = false;
            OnPropertyChanged(nameof(InterlockText));
            _log.Error("brew", "GPIO interlock fault: GPIO15 and GPIO16 were active together.");
            RecordHistory("fault", "GPIO15/GPIO16 同时有效，已切断输出");
            return false;
        }

        OnPropertyChanged(nameof(InterlockText));
        return true;
    }

    public void SetDeviceMode(bool enabled)
    {
        IsDeviceMode = enabled;
        DeviceMessage = enabled ? "设备模式：等待设备快照" : "模拟模式";
    }

    public void SetConnection(DeviceConnectionStatus status, string portName, string message)
    {
        ConnectionStatus = status;
        DevicePortName = portName;
        DeviceMessage = message;
        if (status is DeviceConnectionStatus.Connecting or DeviceConnectionStatus.Online)
            IsDeviceMode = true;
        if (status is DeviceConnectionStatus.Offline or DeviceConnectionStatus.Disconnected)
            IsDeviceMode = false;
    }

    public void ApplyDeviceSnapshot(DeviceSnapshot snapshot)
    {
        var oldMode = Mode;
        var oldGpio15 = Gpios[0].IsActive;
        var oldGpio16 = Gpios[1].IsActive;

        ConnectionStatus = DeviceConnectionStatus.Online;
        _lastDeviceDataAt = DateTime.Now;
        OnPropertyChanged(nameof(LastDeviceDataText));

        if (!IsDeviceMode)
        {
            DeviceMessage = $"设备快照 #{snapshot.Sequence}: {snapshot.RawMode}（模拟模式未接管）";
            return;
        }

        if (snapshot.Gpio15 && snapshot.Gpio16)
        {
            Gpios[0].IsActive = false;
            Gpios[1].IsActive = false;
            Mode = BrewMode.Fault;
            DeviceMessage = "设备回报互锁故障：GPIO15/GPIO16 同时有效";
            _log.Error("device", DeviceMessage);
            RecordHistory("fault", DeviceMessage);
        }
        else
        {
            Gpios[0].IsActive = snapshot.Gpio15;
            Gpios[1].IsActive = snapshot.Gpio16;
            Mode = snapshot.Mode;
            DeviceMessage = snapshot.Error == null
                ? $"设备快照 #{snapshot.Sequence}: {snapshot.RawMode}"
                : $"设备故障 #{snapshot.Sequence}: {snapshot.Error}";
        }

        Touch();
        OnPropertyChanged(nameof(InterlockText));
        if (oldMode != Mode || oldGpio15 != Gpios[0].IsActive || oldGpio16 != Gpios[1].IsActive)
            RecordHistory("device", $"设备快照更新：{snapshot.RawMode}，GPIO15={Gpios[0].StateText}，GPIO16={Gpios[1].StateText}");
    }

    private string SetMode(BrewMode next)
    {
        if (IsDeviceMode)
        {
            RequestDeviceMode(next);
            OperationCount++;
            Touch();
            DeviceMessage = next switch
            {
                BrewMode.Up => "已请求设备上液，等待状态快照",
                BrewMode.Down => "已请求设备降液，等待状态快照",
                _ => "已请求设备停止，等待状态快照",
            };
            OnPropertyChanged(nameof(InterlockText));
            RecordHistory("gpio", DeviceMessage);
            return DeviceMessage;
        }

        if (next == BrewMode.Up)
        {
            Gpios[1].IsActive = false;
            Gpios[0].IsActive = true;
        }
        else if (next == BrewMode.Down)
        {
            Gpios[0].IsActive = false;
            Gpios[1].IsActive = true;
        }
        else
        {
            Gpios[0].IsActive = false;
            Gpios[1].IsActive = false;
        }

        Mode = next;
        OperationCount++;
        Touch();
        OnPropertyChanged(nameof(InterlockText));

        var message = $"{StatusText}，{DirectionText}，GPIO15={Gpios[0].StateText}，GPIO16={Gpios[1].StateText}";
        _log.Info("brew", message);
        RecordHistory("gpio", message);
        return message;
    }

    private void RequestDeviceMode(BrewMode next)
    {
        var mode = next switch
        {
            BrewMode.Up => "up",
            BrewMode.Down => "down",
            _ => "stop",
        };
        DeviceOutputRequested?.Invoke(this, mode);
    }

    private void Tick(double seconds)
    {
        if (Mode == BrewMode.Paused)
        {
            ValidateInterlock();
            return;
        }

        if (IsDeviceMode && IsSequenceActive && ConnectionStatus != DeviceConnectionStatus.Online)
        {
            AbortSequence("设备通讯异常");
            return;
        }

        if (IsSequenceActive && Mode != BrewMode.Complete)
            TickSequence(seconds);
        else
            TickManual();

        ValidateInterlock();
    }

    private void TickSequence(double seconds)
    {
        if (_activeRecipe == null)
            return;

        _phaseElapsedSeconds += seconds;
        _sequenceElapsedSeconds = Math.Min(_activeRecipe.TotalSeconds, _sequenceElapsedSeconds + seconds);

        if (Mode == BrewMode.Up)
            MoveLiquid(seconds / Math.Max(1, _activeRecipe.UpSeconds));
        else if (Mode == BrewMode.Down)
            MoveLiquid(-seconds / Math.Max(1, _activeRecipe.DownSeconds));

        if (_phaseElapsedSeconds + 0.0001 < CurrentPhaseDurationSeconds)
        {
            NotifyProgress();
            return;
        }

        CompletedSteps++;
        AdvancePhase();
    }

    private void TickManual()
    {
        const double step = 0.45;
        if (Mode == BrewMode.Up)
        {
            if (LowerLevel <= 8)
            {
                SetMode(BrewMode.Idle);
                return;
            }

            UpperLevel += step;
            LowerLevel -= step;
        }
        else if (Mode == BrewMode.Down)
        {
            if (UpperLevel <= 8)
            {
                SetMode(BrewMode.Idle);
                return;
            }

            UpperLevel -= step;
            LowerLevel += step;
        }
    }

    private void MoveLiquid(double signedRatio)
    {
        var amount = signedRatio * 54;
        if (amount > 0)
        {
            UpperLevel += amount;
            LowerLevel -= amount;
        }
        else if (amount < 0)
        {
            UpperLevel += amount;
            LowerLevel -= amount;
        }
    }

    private void AdvancePhase()
    {
        switch (Phase)
        {
            case BrewCyclePhase.Up:
                BeginPhase(BrewCyclePhase.UpDwell);
                break;
            case BrewCyclePhase.UpDwell:
                BeginPhase(BrewCyclePhase.Down);
                break;
            case BrewCyclePhase.Down:
                BeginPhase(BrewCyclePhase.DownDwell);
                break;
            case BrewCyclePhase.DownDwell:
                if (_activeRecipe != null && CurrentCycle < _activeRecipe.Cycles)
                {
                    CurrentCycle++;
                    BeginPhase(BrewCyclePhase.Up);
                }
                else
                {
                    CompleteSequence();
                }
                break;
        }
    }

    private void BeginPhase(BrewCyclePhase phase, bool resetElapsed = true)
    {
        Phase = phase;
        if (resetElapsed)
            _phaseElapsedSeconds = 0;

        switch (phase)
        {
            case BrewCyclePhase.Up:
                SetMode(BrewMode.Up);
                break;
            case BrewCyclePhase.Down:
                SetMode(BrewMode.Down);
                break;
            case BrewCyclePhase.UpDwell:
            case BrewCyclePhase.DownDwell:
                SetMode(BrewMode.Idle);
                break;
        }

        if (CurrentPhaseDurationSeconds <= 0 && phase is BrewCyclePhase.UpDwell or BrewCyclePhase.DownDwell)
            AdvancePhase();
    }

    private void CompleteSequence()
    {
        Gpios[0].IsActive = false;
        Gpios[1].IsActive = false;
        Phase = BrewCyclePhase.Complete;
        Mode = BrewMode.Complete;
        _phaseElapsedSeconds = 0;
        _sequenceElapsedSeconds = _activeRecipe?.TotalSeconds ?? _sequenceElapsedSeconds;
        Touch();
        NotifyProgress();

        var message = $"固定循环完成：{CompletedSteps}/{TotalSteps} 步，总时长 {FormatSeconds(_sequenceElapsedSeconds)}";
        _log.Info("brew", message);
        RecordHistory("run", message);
        NotifyStatus();
    }

    private void RecordHistory(string type, string message, string? recipeName = null)
    {
        var entry = new BrewHistoryEntry(
            DateTime.Now,
            type,
            message,
            StatusText,
            Gpios[0].IsActive,
            Gpios[1].IsActive,
            recipeName);
        History.Insert(0, entry);
        while (History.Count > 80)
            History.RemoveAt(History.Count - 1);
        _persistence?.AppendHistory(entry);
        OnPropertyChanged(nameof(History));
    }

    private void Touch()
    {
        _lastChanged = DateTime.Now;
        OnPropertyChanged(nameof(LastChangedText));
    }

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSequenceRunning));
        OnPropertyChanged(nameof(IsSequencePaused));
        OnPropertyChanged(nameof(IsSequenceActive));
        OnPropertyChanged(nameof(IsParameterLocked));
        OnPropertyChanged(nameof(InterlockText));
        NotifyProgress();
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(CurrentCycle));
        OnPropertyChanged(nameof(TotalCycles));
        OnPropertyChanged(nameof(CompletedSteps));
        OnPropertyChanged(nameof(TotalSteps));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(CurrentStepText));
        OnPropertyChanged(nameof(StepElapsedText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(TimelineText));
        OnPropertyChanged(nameof(StageText));
    }

    private static string FormatSeconds(double seconds)
    {
        var value = Math.Max(0, (int)Math.Ceiling(seconds));
        return $"{value / 60:00}:{value % 60:00}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
