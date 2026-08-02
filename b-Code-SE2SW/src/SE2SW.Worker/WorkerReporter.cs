using System.Text.Json;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal sealed class WorkerReporter(string batchId, JsonSerializerOptions jsonOptions)
{
    private readonly object _gate = new();

    public void Report(
        string? jobId,
        ConversionStage stage,
        string message,
        bool isError = false,
        int? hResult = null,
        int? nativeError = null,
        int? nativeWarning = null,
        ConversionErrorClass errorClass = ConversionErrorClass.None,
        FeatureOutcome? feature = null,
        AssemblyOutcome? assembly = null)
    {
        var payload = new WorkerEvent(
            batchId,
            jobId,
            stage,
            message,
            isError,
            hResult,
            nativeError,
            nativeWarning,
            errorClass,
            feature,
            assembly);
        lock (_gate)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
            Console.Out.Flush();
        }
    }
}
