using SE2SW.Contracts;

namespace SE2SW.Worker;

internal sealed record AssemblyPartReusePlan(
    IReadOnlyList<ConversionJob> ReusableSolidWorksParts,
    IReadOnlyList<ConversionJob> ImportFromExistingXt,
    IReadOnlyList<ConversionJob> NeedsExport);

/// <summary>
/// Plans a retry without overwriting any user file. Existing outputs are only reused when
/// they are non-empty, no older than their source part, and (for XT) valid Parasolid text.
/// </summary>
internal static class AssemblyPartReusePlanner
{
    public static AssemblyPartReusePlan Create(
        IReadOnlyList<ConversionJob> jobs,
        CancellationToken cancellationToken)
    {
        var reusableParts = new List<ConversionJob>();
        var importFromXt = new List<ConversionJob>();
        var needsExport = new List<ConversionJob>();

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new FileInfo(job.SourcePath);
            if (!source.Exists)
                throw new FileNotFoundException("装配零件源文件不存在。", job.SourcePath);

            if (File.Exists(job.SolidWorksPath))
            {
                ValidateReusableFile(job.SolidWorksPath, source, "SLDPRT");
                reusableParts.Add(job);
                continue;
            }

            if (File.Exists(job.XtPath))
            {
                ValidateReusableFile(job.XtPath, source, "XT");
                try
                {
                    _ = FileProbe.VerifyParasolidText(
                        job.XtPath,
                        cancellationToken,
                        TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ClassifiedConversionException(
                        ConversionErrorClass.OutputFormatInvalid,
                        $"已有 XT 无法安全复用：{job.XtPath}；{ex.Message}",
                        ex);
                }
                importFromXt.Add(job);
                continue;
            }

            needsExport.Add(job);
        }

        return new AssemblyPartReusePlan(reusableParts, importFromXt, needsExport);
    }

    private static void ValidateReusableFile(string path, FileInfo source, string kind)
    {
        var output = new FileInfo(path);
        output.Refresh();
        if (output.Length == 0)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.OutputEmpty,
                $"已有 {kind} 为空，拒绝覆盖或复用：{path}");
        }
        if (output.LastWriteTimeUtc < source.LastWriteTimeUtc)
        {
            throw new ClassifiedConversionException(
                ConversionErrorClass.OutputUnstable,
                $"已有 {kind} 早于源零件，拒绝复用：{path}；请移走旧产物后重试。");
        }
    }
}
