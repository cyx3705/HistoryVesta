using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SE2SW.Contracts;

namespace SE2SW;

public sealed class WorkerClient
{
    private static readonly TimeSpan StageInactivityTimeout = TimeSpan.FromSeconds(180);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string WorkerPath
        => WorkerLocator.Locate();

    public async Task<int> RunAsync(
        BatchRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
        => await RunWorkerAsync(
            "--request",
            request.BatchId,
            request,
            progress,
            cancellationToken).ConfigureAwait(false);

    public async Task<AssemblyProbeResult> ProbeAssemblyAsync(
        AssemblyProbeRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var exitCode = await RunWorkerAsync(
            "--probe-assembly",
            request.BatchId,
            request,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (exitCode is not (0 or 1))
            throw new InvalidOperationException($"装配探查工作进程失败，退出码 {exitCode}。");
        if (!File.Exists(request.ResultPath))
            throw new InvalidDataException("装配探查未生成结果清单。");

        await using var stream = File.OpenRead(request.ResultPath);
        return await JsonSerializer.DeserializeAsync<AssemblyProbeResult>(
                   stream,
                   JsonOptions,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("装配探查结果清单为空。");
    }

    public async Task<int> RunAssemblyAsync(
        AssemblyBatchRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
        => await RunWorkerAsync(
            "--assembly",
            request.BatchId,
            request,
            progress,
            cancellationToken).ConfigureAwait(false);

    private static async Task<int> RunWorkerAsync<TRequest>(
        string verb,
        string batchId,
        TRequest request,
        Action<WorkerEvent> progress,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneHistoryStudio",
            "SE2SW",
            "requests");
        Directory.CreateDirectory(requestDirectory);
        var requestPath = Path.Combine(requestDirectory, batchId + ".json");
        var cancellationPath = Path.Combine(requestDirectory, batchId + ".cancel");
        TryDelete(cancellationPath);
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = WorkerPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            process.StartInfo.ArgumentList.Add(verb);
            process.StartInfo.ArgumentList.Add(requestPath);
            process.StartInfo.ArgumentList.Add("--cancel");
            process.StartInfo.ArgumentList.Add(cancellationPath);
            if (!process.Start())
                throw new InvalidOperationException("无法启动 SE2SW 工作进程。");

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    File.WriteAllText(cancellationPath, "cancel");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            });

            var stderrTask = process.StandardError.ReadToEndAsync();
            var timedOut = false;
            while (true)
            {
                var lineTask = process.StandardOutput.ReadLineAsync(CancellationToken.None).AsTask();
                var timeoutTask = Task.Delay(StageInactivityTimeout);
                var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                var completed = await Task.WhenAny(lineTask, timeoutTask, cancellationTask).ConfigureAwait(false);
                if (completed != lineTask)
                {
                    TrySignalCancellation(cancellationPath);
                    timedOut = completed == timeoutTask;
                    if (timedOut)
                    {
                        progress(new WorkerEvent(
                            batchId,
                            null,
                            ConversionStage.Failed,
                            $"CAD 阶段连续 {StageInactivityTimeout.TotalSeconds:0} 秒无进度，工作进程已终止。",
                            IsError: true,
                            ErrorClass: ConversionErrorClass.Timeout));
                    }
                    await ForceTerminateAfterGracePeriodAsync(process).ConfigureAwait(false);
                }

                var line = await lineTask.ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var workerEvent = JsonSerializer.Deserialize<WorkerEvent>(line, JsonOptions);
                    if (workerEvent is not null)
                        progress(workerEvent);
                }
                catch (JsonException)
                {
                    progress(new WorkerEvent(batchId, null, ConversionStage.Failed, line, IsError: true));
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                progress(new WorkerEvent(batchId, null, ConversionStage.Failed, stderr.Trim(), IsError: true));
            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut)
                return 4;
            return process.ExitCode;
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(cancellationPath);
        }
    }

    private static void TrySignalCancellation(string cancellationPath)
    {
        try
        {
            File.WriteAllText(cancellationPath, "cancel");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task ForceTerminateAfterGracePeriodAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
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
}
