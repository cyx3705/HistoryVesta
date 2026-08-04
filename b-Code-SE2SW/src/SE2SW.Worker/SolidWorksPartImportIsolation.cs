using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SE2SW.Contracts;

namespace SE2SW.Worker;

/// <summary>
/// FeatureWorks 的 COM 服务在同一 Worker 连续识别多个零件后会污染后续导入。
/// 启用识别时，每个零件使用一个短生命周期子 Worker；普通哑实体导入仍走原批量路径。
/// </summary>
internal static class SolidWorksPartImportIsolation
{
    private const int SessionFaultAttempts = 3;
    private static readonly JsonSerializerOptions JsonOptions = WorkerProtocol.CreateJsonOptions();

    public static int Import(
        BatchRequest request,
        IReadOnlyList<ConversionJob> jobs,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        if (!ShouldIsolate(request))
            return SolidWorksImporter.Import(request, jobs, reporter, cancellationToken);

        var failed = 0;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 1; attempt <= SessionFaultAttempts; attempt++)
            {
                var result = RunIsolated(request, job, reporter, cancellationToken);
                if (result == IsolatedImportResult.RecognitionTimedOut)
                {
                    var timeoutSeconds = FeatureRecognitionPolicy.NormalizeTimeoutSeconds(
                        request.FeatureRecognitionTimeoutSeconds);
                    var timeoutOutcome = new FeatureOutcome(
                        0, false, 0, 0, [], true, timeoutSeconds * 1000L,
                        $"特征识别连续 {timeoutSeconds} 秒无进度，已跳过并改用哑实体。",
                        SessionFaulted: false);
                    reporter.Report(
                        job.Id,
                        ConversionStage.FeatureRecognition,
                        "特征识别超时，正在使用普通导入管线重建哑实体。",
                        errorClass: ConversionErrorClass.FeatureRecognitionTimeout,
                        feature: timeoutOutcome,
                        artifact: ConversionArtifactKind.SolidWorksPart);

                    // 识别 Worker 被终止后，不能复用其中的 COM 文档；用独立的无 FeatureWorks
                    // 请求从同一 XT 重建完整 SLDPRT，再让装配构建器按普通组件路径处理。
                    var dumbRequest = request with
                    {
                        RecognizeFeatures = false,
                        FullyDefineSketches = false,
                        ContinueWhenRecognitionFails = true,
                    };
                    var dumbResult = RunIsolated(dumbRequest, job, reporter, cancellationToken);
                    if (dumbResult == IsolatedImportResult.Succeeded)
                    {
                        reporter.Report(
                            job.Id,
                            ConversionStage.Completed,
                            "已跳过无进度的特征识别，哑实体已生成并可参与装配。",
                            errorClass: ConversionErrorClass.FeatureRecognitionTimeout,
                            feature: timeoutOutcome,
                            artifact: ConversionArtifactKind.SolidWorksPart);
                        break;
                    }

                    failed++;
                    break;
                }
                if (result != IsolatedImportResult.SessionFaulted)
                {
                    failed += result == IsolatedImportResult.Failed ? 1 : 0;
                    break;
                }

                if (attempt == SessionFaultAttempts)
                {
                    reporter.Report(
                        job.Id,
                        ConversionStage.FeatureRecognition,
                        $"FeatureWorks 在 {SessionFaultAttempts} 次独立会话中均发生故障，保留最后一次生成的安全哑实体。",
                        errorClass: ConversionErrorClass.FeatureWorksUnavailable);
                    break;
                }

                reporter.Report(
                    job.Id,
                    ConversionStage.FeatureRecognition,
                    $"FeatureWorks 会话故障，正在创建新的单零件 Worker 重试（{attempt + 1}/{SessionFaultAttempts}）。",
                    errorClass: ConversionErrorClass.FeatureWorksUnavailable);
                try
                {
                    if (File.Exists(job.SolidWorksPath))
                        File.Delete(job.SolidWorksPath);
                }
                catch (Exception ex)
                {
                    reporter.Report(
                        job.Id,
                        ConversionStage.Failed,
                        "无法清理会话故障后的降级产物，停止隔离重试：" + ex.Message,
                        true,
                        ex.HResult,
                        errorClass: ConversionErrorClass.OutputNotWritable);
                    failed++;
                    break;
                }
            }
        }

        return failed;
    }

    internal static bool ShouldIsolate(BatchRequest request)
        => request.RecognizeFeatures;

    internal static bool HasRecognitionStalled(
        long lastProgressUtcTicks,
        DateTimeOffset observedAt,
        TimeSpan timeout)
        => lastProgressUtcTicks != 0
           && observedAt - new DateTimeOffset(lastProgressUtcTicks, TimeSpan.Zero) >= timeout;

    private static IsolatedImportResult RunIsolated(
        BatchRequest request,
        ConversionJob job,
        WorkerReporter reporter,
        CancellationToken cancellationToken)
    {
        var runDirectory = Path.Combine(
            Path.GetTempPath(),
            "SE2SW-PartImport",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var requestPath = Path.Combine(runDirectory, "request.json");
        var cancellationPath = Path.Combine(runDirectory, "cancel.signal");
        try
        {
            var childRequest = new PartImportRequest(
                request.BatchId,
                request.Mode,
                job,
                request.Overwrite,
                request.RecognizeFeatures,
                request.FullyDefineSketches,
                request.FeatureRecognitionTimeoutSeconds,
                request.ContinueWhenRecognitionFails);
            File.WriteAllText(requestPath, JsonSerializer.Serialize(childRequest, JsonOptions));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveWorkerExecutable(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            process.StartInfo.ArgumentList.Add(WorkerProtocol.PartImportVerb);
            process.StartInfo.ArgumentList.Add(requestPath);
            process.StartInfo.ArgumentList.Add(WorkerProtocol.CancellationArgument);
            process.StartInfo.ArgumentList.Add(cancellationPath);

            var childReportedError = 0;
            var childSessionFaulted = 0;
            long lastRecognitionProgressTicks = 0;
            var unparsedOutput = new List<string>();
            var standardError = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    return;
                try
                {
                    var workerEvent = JsonSerializer.Deserialize<WorkerEvent>(eventArgs.Data, JsonOptions)
                        ?? throw new JsonException("子 Worker 返回空事件。");
                    if (workerEvent.IsError)
                        Interlocked.Exchange(ref childReportedError, 1);
                    if (workerEvent.Feature is { SessionFaulted: true })
                        Interlocked.Exchange(ref childSessionFaulted, 1);
                    if (workerEvent.JobId == job.Id
                        && workerEvent.Stage == ConversionStage.FeatureRecognition)
                    {
                        Interlocked.Exchange(ref lastRecognitionProgressTicks, DateTimeOffset.UtcNow.Ticks);
                    }
                    reporter.Forward(workerEvent);
                }
                catch (JsonException)
                {
                    lock (unparsedOutput)
                        unparsedOutput.Add(eventArgs.Data);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                    return;
                lock (standardError)
                    standardError.AppendLine(eventArgs.Data);
            };

            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException("无法启动单零件导入子 Worker。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cancellationRegistration = cancellationToken.Register(
                static state => TrySignalCancellation((string)state!),
                cancellationPath);
            var recognitionTimeout = TimeSpan.FromSeconds(
                FeatureRecognitionPolicy.NormalizeTimeoutSeconds(request.FeatureRecognitionTimeoutSeconds));
            var recognitionTimedOut = false;
            var cancellationObservedAt = DateTimeOffset.MinValue;
            while (!process.WaitForExit(100))
            {
                var progressTicks = Volatile.Read(ref lastRecognitionProgressTicks);
                if (request.RecognizeFeatures
                    && HasRecognitionStalled(progressTicks, DateTimeOffset.UtcNow, recognitionTimeout))
                {
                    recognitionTimedOut = true;
                    TryKill(process);
                    process.WaitForExit();
                    break;
                }
                if (!cancellationToken.IsCancellationRequested)
                    continue;
                if (cancellationObservedAt == DateTimeOffset.MinValue)
                    cancellationObservedAt = DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - cancellationObservedAt <= TimeSpan.FromSeconds(15))
                    continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                break;
            }
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();

            if (recognitionTimedOut)
                return IsolatedImportResult.RecognitionTimedOut;

            if (process.ExitCode == 0)
            {
                return Volatile.Read(ref childSessionFaulted) == 0
                    ? IsolatedImportResult.Succeeded
                    : IsolatedImportResult.SessionFaulted;
            }

            if (Volatile.Read(ref childReportedError) == 0)
            {
                var diagnostic = DescribeChildFailure(process.ExitCode, standardError, unparsedOutput);
                reporter.Report(
                    job.Id,
                    ConversionStage.Failed,
                    diagnostic,
                    true,
                    errorClass: ConversionErrorClass.ImportFailed);
            }
            return IsolatedImportResult.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(
                job.Id,
                ConversionStage.Failed,
                "单零件隔离导入失败：" + ex.Message,
                true,
                ex.HResult,
                errorClass: ComErrorClassifier.Classify(ex, ConversionErrorClass.ImportFailed));
            return IsolatedImportResult.Failed;
        }
        finally
        {
            TryDeleteDirectory(runDirectory);
        }
    }

    private enum IsolatedImportResult
    {
        Succeeded,
        Failed,
        SessionFaulted,
        RecognitionTimedOut,
    }

    private static string ResolveWorkerExecutable()
    {
        var besideAssembly = Path.Combine(AppContext.BaseDirectory, "SE2SW.Worker.exe");
        if (File.Exists(besideAssembly))
            return besideAssembly;

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "SE2SW.Worker",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        throw new FileNotFoundException("无法定位 SE2SW.Worker.exe，不能启动单零件隔离导入。", besideAssembly);
    }

    private static string DescribeChildFailure(
        int exitCode,
        StringBuilder standardError,
        IReadOnlyCollection<string> unparsedOutput)
    {
        string error;
        lock (standardError)
            error = standardError.ToString().Trim();
        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error;
        if (unparsedOutput.Count > 0)
            suffix += $" 子 Worker 另有 {unparsedOutput.Count} 行无法解析的输出。";
        return $"单零件导入子 Worker 异常退出（代码 {exitCode}）。{suffix}".TrimEnd();
    }

    private static void TrySignalCancellation(string cancellationPath)
    {
        try
        {
            File.WriteAllText(cancellationPath, string.Empty);
        }
        catch
        {
            // 父 Worker仍会在宽限期后终止自己创建的子进程。
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时请求清理失败不覆盖真实的转换结果。
        }
    }
}
