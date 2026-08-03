using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Threading;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class AssemblyViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<AssemblyProbeRequest, Action<WorkerEvent>, CancellationToken, Task<AssemblyProbeResult>> _probeWorker;
    private readonly Func<AssemblyBatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> _runWorker;
    private readonly Action _validateEnvironment;
    private readonly Dispatcher _uiDispatcher;
    private readonly object _lifecycleGate = new();
    private readonly object _dispatchGate = new();
    private readonly Queue<Action> _pendingUiUpdates = new();
    private string _sourceAssemblyPath = string.Empty;
    private string _xtDirectory = string.Empty;
    private string _solidWorksDirectory = string.Empty;
    private string _assemblyOutputPath = string.Empty;
    private string _statusText = "请选择 Solid Edge .asm 装配体";
    private string _warningSummary = "输出按源装配的层级生成嵌套装配体，全部组件固定，不含配合。";
    private bool _isBusy;
    private bool _isProbing;
    private bool _recognizeFeatures;
    private bool _fullyDefineSketches;
    private bool _continueWhenPartFails;
    private bool _conversionCompleted;
    private CancellationTokenSource? _operationCancellation;
    private Task? _activeOperation;
    private DispatcherOperation? _dispatchOperation;
    private AssemblyConversionPlan? _plan;
    private string? _sourceHashAfterProbe;
    private bool _disposed;

    public ObservableCollection<AssemblyTreeNode> AssemblyTree { get; } = [];
    public ObservableCollection<ConversionFileRow> Parts { get; } = [];

    public string SourceAssemblyPath
    {
        get => _sourceAssemblyPath;
        set
        {
            if (IsBusy || !SetField(ref _sourceAssemblyPath, value))
                return;
            UpdateOutputPaths();
            ClearProbeResult();
            StatusText = "源文件已改变，请重新解析装配体";
            OnPropertyChanged(nameof(CanProbe));
        }
    }

    public string XtDirectory
    {
        get => _xtDirectory;
        private set => SetField(ref _xtDirectory, value);
    }

    public string SolidWorksDirectory
    {
        get => _solidWorksDirectory;
        private set => SetField(ref _solidWorksDirectory, value);
    }

    public string AssemblyOutputPath
    {
        get => _assemblyOutputPath;
        private set => SetField(ref _assemblyOutputPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string WarningSummary
    {
        get => _warningSummary;
        private set => SetField(ref _warningSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanProbe));
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(CanFullyDefineSketches));
        }
    }

    public bool IsProbing
    {
        get => _isProbing;
        private set => SetField(ref _isProbing, value);
    }

    public bool RecognizeFeatures
    {
        get => _recognizeFeatures;
        set
        {
            if (!SetField(ref _recognizeFeatures, value))
                return;
            OnPropertyChanged(nameof(CanFullyDefineSketches));
        }
    }

    public bool FullyDefineSketches
    {
        get => _fullyDefineSketches;
        set => SetField(ref _fullyDefineSketches, value);
    }

    public bool ContinueWhenPartFails
    {
        get => _continueWhenPartFails;
        set => SetField(ref _continueWhenPartFails, value);
    }

    public bool IsExternalMode => true;
    public bool IsOhsModeAvailable => false;
    public bool CanEdit => !IsBusy;
    public bool CanProbe => CanEdit && File.Exists(SourceAssemblyPath)
        && ConversionPathLayout.HasExtension(SourceAssemblyPath, ConversionPathLayout.SolidEdgeAssemblyExtension);
    public bool CanConvert => CanEdit && !_conversionCompleted && _plan?.CanConvert == true;
    public bool CanFullyDefineSketches => CanEdit && RecognizeFeatures;
    public string OperationText => IsProbing ? "正在解析装配体" : "正在转换装配体";

    public event PropertyChangedEventHandler? PropertyChanged;

    public AssemblyViewModel()
        : this(
            (request, progress, cancellationToken) =>
                new WorkerClient().ProbeAssemblyAsync(request, progress, cancellationToken),
            (request, progress, cancellationToken) =>
                new WorkerClient().RunAssemblyAsync(request, progress, cancellationToken),
            () => PreflightValidator.ValidateEnvironment(WorkerClient.WorkerPath),
            Dispatcher.CurrentDispatcher)
    {
    }

    internal AssemblyViewModel(
        Func<AssemblyProbeRequest, Action<WorkerEvent>, CancellationToken, Task<AssemblyProbeResult>> probeWorker,
        Func<AssemblyBatchRequest, Action<WorkerEvent>, CancellationToken, Task<int>> runWorker,
        Action validateEnvironment,
        Dispatcher uiDispatcher)
    {
        _probeWorker = probeWorker ?? throw new ArgumentNullException(nameof(probeWorker));
        _runWorker = runWorker ?? throw new ArgumentNullException(nameof(runWorker));
        _validateEnvironment = validateEnvironment ?? throw new ArgumentNullException(nameof(validateEnvironment));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public void SetSourceFile(string path)
        => SourceAssemblyPath = path;

    public Task ProbeAsync()
        => StartOperationAsync(isProbe: true, ProbeCoreAsync);

    public Task ConvertAsync()
        => StartOperationAsync(isProbe: false, ConvertCoreAsync);

    public void Cancel()
    {
        if (!IsBusy)
            return;
        StatusText = "正在取消";
        lock (_lifecycleGate)
            _operationCancellation?.Cancel();
    }

    public void Dispose()
    {
        Task? activeOperation;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            activeOperation = _activeOperation;
            _operationCancellation?.Cancel();
        }

        lock (_dispatchGate)
        {
            _pendingUiUpdates.Clear();
            if (_dispatchOperation?.Status == DispatcherOperationStatus.Pending)
                _dispatchOperation.Abort();
            _dispatchOperation = null;
        }

        activeOperation?.GetAwaiter().GetResult();
        AssemblyTree.Clear();
        Parts.Clear();
    }

    private Task StartOperationAsync(bool isProbe, Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource cancellation;
        TaskCompletionSource completion;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeOperation is not null)
                return _activeOperation;
            cancellation = new CancellationTokenSource();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operationCancellation = cancellation;
            _activeOperation = completion.Task;
        }

        IsProbing = isProbe;
        IsBusy = true;
        OnPropertyChanged(nameof(OperationText));
        _ = RunOperationAndCompleteAsync(operation, cancellation, completion);
        return completion.Task;
    }

    private async Task RunOperationAndCompleteAsync(
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        try
        {
            await operation(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            QueueUiUpdate(() => StatusText = "操作已取消");
        }
        catch (Exception ex)
        {
            QueueUiUpdate(() => StatusText = ex.Message);
        }
        finally
        {
            QueueUiUpdate(() =>
            {
                IsBusy = false;
                IsProbing = false;
                OnPropertyChanged(nameof(OperationText));
                lock (_lifecycleGate)
                {
                    if (ReferenceEquals(_activeOperation, completion.Task))
                        _activeOperation = null;
                }
            });
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_operationCancellation, cancellation))
                    _operationCancellation = null;
            }
            cancellation.Dispose();
            completion.TrySetResult();
        }
    }

    private async Task ProbeCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(SourceAssemblyPath)
            || !ConversionPathLayout.HasExtension(SourceAssemblyPath, ConversionPathLayout.SolidEdgeAssemblyExtension))
            throw new InvalidOperationException("请选择存在的 Solid Edge .asm 文件。");
        _validateEnvironment();

        var batchId = Guid.NewGuid().ToString("N");
        var resultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SE2SWIdentity.HostApplicationDataDirectoryName,
            SE2SWIdentity.ModuleApplicationDataDirectoryName,
            SE2SWIdentity.ProbesDirectoryName);
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, batchId + ".result.json");
        try
        {
            var request = new AssemblyProbeRequest(batchId, Path.GetFullPath(SourceAssemblyPath), resultPath);
            QueueUiUpdate(() => StatusText = "正在只读解析装配树");
            var result = await _probeWorker(
                request,
                workerEvent => QueueUiUpdate(() => ApplyWorkerEvent(workerEvent)),
                cancellationToken).ConfigureAwait(false);
            var plan = AssemblyPlanner.Create(result);
            var sourceHash = ComputeSha256(result.SourceAssemblyPath);
            QueueUiUpdate(() => ApplyProbeResult(result, plan, sourceHash));
        }
        finally
        {
            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
        }
    }

    private async Task ConvertCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = _plan ?? throw new InvalidOperationException("请先成功解析装配体。");
        if (!plan.CanConvert)
            throw new InvalidOperationException("装配清单仍有前置错误，不能转换。");
        if (!string.Equals(_sourceHashAfterProbe, ComputeSha256(plan.SourceAssemblyPath), StringComparison.Ordinal))
            throw new InvalidDataException("源 .asm 在解析后发生变化，请重新解析后再转换。");

        _validateEnvironment();
        ExternalOutputLayout.EnsureDirectories(plan.SourceDirectory);
        var jobs = Parts.Select(row => new ConversionJob(
            row.Id,
            row.SourcePath,
            row.XtPath,
            row.SolidWorksPath)).ToArray();
        var request = new AssemblyBatchRequest(
            Guid.NewGuid().ToString("N"),
            ConversionMode.External,
            plan.SourceAssemblyPath,
            plan.AssemblyOutputPath,
            jobs,
            plan.Occurrences,
            Overwrite: false,
            RecognizeFeatures: RecognizeFeatures,
            FullyDefineSketches: RecognizeFeatures && FullyDefineSketches,
            ContinueWhenPartFails: ContinueWhenPartFails,
            Nodes: plan.Nodes);
        PreflightValidator.ValidateAssemblyRequest(request);

        foreach (var row in Parts)
        {
            row.Status = "排队";
            row.Detail = string.Empty;
            row.ResetFeatureResult();
            if (!RecognizeFeatures)
            {
                row.FeatureText = "—";
                row.SketchText = "—";
            }
        }
        QueueUiUpdate(() => StatusText = $"正在转换 {jobs.Length} 个唯一零件并组装");
        var exitCode = await _runWorker(
            request,
            workerEvent => QueueUiUpdate(() => ApplyWorkerEvent(workerEvent)),
            cancellationToken).ConfigureAwait(false);
        QueueUiUpdate(() =>
        {
            _conversionCompleted = File.Exists(plan.AssemblyOutputPath);
            StatusText = exitCode == 0
                ? $"装配转换完成：{plan.AssemblyOutputPath}"
                : _conversionCompleted
                    ? $"装配已生成，但有零件失败：{plan.AssemblyOutputPath}"
                    : "装配转换失败，未生成 SLDASM";
            OnPropertyChanged(nameof(CanConvert));
        });
    }

    private void ApplyProbeResult(
        AssemblyProbeResult result,
        AssemblyConversionPlan plan,
        string sourceHash)
    {
        ClearProbeResult();
        _plan = plan;
        _sourceHashAfterProbe = sourceHash;
        XtDirectory = plan.XtDirectory;
        SolidWorksDirectory = plan.SolidWorksDirectory;
        AssemblyOutputPath = plan.AssemblyOutputPath;
        AssemblyTree.Add(AssemblyTreeNode.Build(result, plan.Nodes));
        foreach (var candidate in plan.Parts)
            Parts.Add(new ConversionFileRow(candidate));

        var issueText = plan.BlockingIssues.Count == 0
            ? string.Empty
            : string.Join("；", plan.BlockingIssues.Select(issue => $"[{issue.ErrorClass}] {issue.Message}"));
        WarningSummary = string.Join("；", plan.Warnings.Concat(
            string.IsNullOrWhiteSpace(issueText) ? [] : new[] { issueText }));
        StatusText = plan.CanConvert
            ? plan.IsNested
                ? $"解析完成：{result.Occurrences.Count} 个实例、{plan.Parts.Count} 个唯一零件、"
                    + $"{plan.SubAssemblyCount} 个子装配，最大 {plan.MaxDepth} 层；按层级生成嵌套装配"
                : $"解析完成：{result.Occurrences.Count} 个实例、{plan.Parts.Count} 个唯一零件；最终输出会展平"
            : $"解析完成，但有 {plan.BlockingIssues.Count} 个前置错误";
        OnPropertyChanged(nameof(CanConvert));
    }

    private void ApplyWorkerEvent(WorkerEvent workerEvent)
    {
        if (workerEvent.JobId is null)
        {
            StatusText = ConversionProgressPresenter.FormatMessage(workerEvent);
            return;
        }
        var row = Parts.FirstOrDefault(item => item.Id == workerEvent.JobId);
        if (row is null)
            return;
        row.Status = ConversionProgressPresenter.GetRowStatus(workerEvent, row.Status);
        row.Detail = ConversionProgressPresenter.FormatMessage(workerEvent);
        ConversionProgressPresenter.ApplyFeatureOutcome(row, workerEvent.Feature);
    }

    private void UpdateOutputPaths()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SourceAssemblyPath))
                throw new InvalidDataException();
            var fullPath = Path.GetFullPath(SourceAssemblyPath.Trim());
            var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException();
            var directories = ConversionPathLayout.ResolveExternalDirectories(directory);
            XtDirectory = directories.XtDirectory;
            SolidWorksDirectory = directories.SolidWorksDirectory;
            AssemblyOutputPath = ConversionPathLayout.ResolveAssemblyOutputPath(fullPath, directories.SolidWorksDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            XtDirectory = string.Empty;
            SolidWorksDirectory = string.Empty;
            AssemblyOutputPath = string.Empty;
        }
    }

    private void ClearProbeResult()
    {
        _plan = null;
        _sourceHashAfterProbe = null;
        _conversionCompleted = false;
        AssemblyTree.Clear();
        Parts.Clear();
        WarningSummary = "输出按源装配的层级生成嵌套装配体，全部组件固定，不含配合。";
        OnPropertyChanged(nameof(CanConvert));
    }

    private void QueueUiUpdate(Action update)
    {
        lock (_dispatchGate)
        {
            if (_disposed)
                return;
            _pendingUiUpdates.Enqueue(update);
            if (_dispatchOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
                return;
            _dispatchOperation = _uiDispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(DrainUiUpdates));
        }
    }

    private void DrainUiUpdates()
    {
        while (true)
        {
            Action update;
            lock (_dispatchGate)
            {
                if (_disposed || _pendingUiUpdates.Count == 0)
                {
                    _pendingUiUpdates.Clear();
                    _dispatchOperation = null;
                    return;
                }
                update = _pendingUiUpdates.Dequeue();
            }
            update();
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
