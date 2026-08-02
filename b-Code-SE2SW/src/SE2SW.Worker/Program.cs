using System.Text.Json;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        var paths = ReadPaths(args);
        if (paths is null)
        {
            Console.Error.WriteLine("Usage: SE2SW.Worker --request|--probe-assembly|--assembly <absolute-json-path> --cancel <absolute-signal-path>");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var cancellationMonitor = MonitorCancellationFileAsync(paths.Value.CancellationPath, cancellation);
        try
        {
            using var messageFilter = OleMessageFilter.Register(TimeSpan.FromSeconds(60), cancellation.Token);
            return paths.Value.Verb.ToLowerInvariant() switch
            {
                "--request" => RunParts(Read<BatchRequest>(paths.Value.RequestPath), cancellation.Token),
                "--probe-assembly" => RunProbe(Read<AssemblyProbeRequest>(paths.Value.RequestPath), cancellation.Token),
                "--assembly" => RunAssembly(Read<AssemblyBatchRequest>(paths.Value.RequestPath), cancellation.Token),
                _ => 2,
            };
        }
        catch (OperationCanceledException)
        {
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
        finally
        {
            cancellation.Cancel();
            cancellationMonitor.GetAwaiter().GetResult();
        }
    }

    private static int RunParts(BatchRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        try
        {
            var exported = SolidEdgeExporter.Export(request, reporter, cancellationToken);
            var failed = request.Jobs.Count - exported.Count;
            failed += SolidWorksImporter.Import(request, exported, reporter, cancellationToken);
            return failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "批处理已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, ex.Message, true, ex.HResult,
                errorClass: ComErrorClassifier.Classify(ex, ConversionErrorClass.Unknown));
            return 4;
        }
    }

    private static int RunProbe(AssemblyProbeRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        reporter.Report(null, ConversionStage.AssemblyProbe, "正在解析 Solid Edge 装配体。");
        try
        {
            var result = SolidEdgeAssemblyExplorer.Probe(request.SourceAssemblyPath, cancellationToken);
            var temporary = request.ResultPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(temporary, request.ResultPath, overwrite: true);
            reporter.Report(null, ConversionStage.Completed,
                $"装配解析完成：{result.Occurrences.Count} 个实例，{result.UniquePartPaths.Count} 个唯一零件。");
            return result.UnresolvedCount == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "装配解析已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, "装配解析失败：" + ex.Message, true, ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ConversionErrorClass.AssemblyOpenFailed);
            return 4;
        }
    }

    private static int RunAssembly(AssemblyBatchRequest request, CancellationToken cancellationToken)
    {
        WorkerRequestValidator.Validate(request);
        var reporter = new WorkerReporter(request.BatchId, JsonOptions);
        try
        {
            return AssemblyConverter.Convert(request, reporter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            reporter.Report(null, ConversionStage.Cancelled, "装配转换已取消。", errorClass: ConversionErrorClass.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            reporter.Report(null, ConversionStage.Failed, "装配转换失败：" + ex.Message, true, ex.HResult,
                errorClass: ex is ClassifiedConversionException classified
                    ? classified.ErrorClass
                    : ConversionErrorClass.Unknown);
            return 4;
        }
    }

    private static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Worker 请求为空。");

    private static (string Verb, string RequestPath, string CancellationPath)? ReadPaths(IReadOnlyList<string> args)
    {
        if (args.Count != 4 ||
            args[0] is not ("--request" or "--probe-assembly" or "--assembly") ||
            !string.Equals(args[2], "--cancel", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.IsPathFullyQualified(args[1]) && File.Exists(args[1]) && Path.IsPathFullyQualified(args[3])
            ? (args[0], args[1], args[3])
            : null;
    }

    private static async Task MonitorCancellationFileAsync(string cancellationPath, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (File.Exists(cancellationPath))
                {
                    cancellation.Cancel();
                    return;
                }
                await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
