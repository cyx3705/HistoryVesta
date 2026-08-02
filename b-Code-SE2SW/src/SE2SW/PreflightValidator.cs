using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public static class PreflightValidator
{
    public static void ValidateEnvironment(string workerPath)
    {
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("未找到 SE2SW 工作进程，请重新构建或同步模块。", workerPath);
        if (Type.GetTypeFromProgID("SolidEdge.Application", throwOnError: false) is null)
            throw new InvalidOperationException("未检测到 Solid Edge COM 注册（SolidEdge.Application）。");
        if (Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false) is null)
            throw new InvalidOperationException("未检测到 SolidWorks COM 注册（SldWorks.Application）。");
    }

    public static void ValidateJobs(IEnumerable<ConversionJob> jobs, bool overwrite)
    {
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateJobs(jobs, overwrite, outputs);
    }

    public static void ValidateAssemblyRequest(AssemblyBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode != ConversionMode.External)
            throw new InvalidDataException("V3.0 装配转换仅支持外界模式。");
        if (!Path.IsPathFullyQualified(request.SourceAssemblyPath)
            || !File.Exists(request.SourceAssemblyPath)
            || !string.Equals(Path.GetExtension(request.SourceAssemblyPath), ".asm", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("源装配体不存在或不是绝对 .asm 路径。", request.SourceAssemblyPath);
        }

        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateJobs(request.PartJobs, request.Overwrite, outputs, allowExistingOutputs: true);
        ValidateOutput(request.AssemblyOutputPath, ".SLDASM", request.Overwrite, outputs);
        if (request.Occurrences.Count == 0)
            throw new InvalidDataException("装配实例清单为空。");

        var occurrenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in request.Occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId) || !occurrenceIds.Add(occurrence.OccurrenceId))
                throw new InvalidDataException($"装配实例编号为空或重复：{occurrence.OccurrenceId}");
            if (occurrence.WorldTransform.Length != 16
                || occurrence.WorldTransform.Any(value => !double.IsFinite(value)))
            {
                throw new InvalidDataException($"实例矩阵无效：{occurrence.OccurrenceId}");
            }
            if (!occurrence.IsSuppressed && occurrence.Diagnostic?.Contains("引用不存在", StringComparison.Ordinal) == true)
                throw new FileNotFoundException($"装配实例引用未解析：{occurrence.SourcePath}", occurrence.SourcePath);
        }
    }

    private static void ValidateJobs(
        IEnumerable<ConversionJob> jobs,
        bool overwrite,
        ISet<string> outputs,
        bool allowExistingOutputs = false)
    {
        var count = 0;
        foreach (var job in jobs)
        {
            count++;
            if (!Path.IsPathFullyQualified(job.SourcePath) || !File.Exists(job.SourcePath))
                throw new FileNotFoundException("源文件不存在或不是绝对路径。", job.SourcePath);
            if (!string.Equals(Path.GetExtension(job.SourcePath), ".par", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"输入不是 Solid Edge .par 文件：{job.SourcePath}");
            ValidateOutput(job.XtPath, ".x_t", overwrite, outputs, allowExistingOutputs);
            ValidateOutput(job.SolidWorksPath, ".SLDPRT", overwrite, outputs, allowExistingOutputs);
        }
        if (count == 0)
            throw new InvalidOperationException("没有选中可转换文件。");
    }

    private static void ValidateOutput(
        string path,
        string extension,
        bool overwrite,
        ISet<string> outputs,
        bool allowExisting = false)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"输出不是绝对路径：{path}");
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"输出扩展名必须是 {extension}：{path}");
        if (!outputs.Add(Path.GetFullPath(path)))
            throw new InvalidDataException($"批次中存在重复输出：{path}");
        if (!allowExisting && !overwrite && File.Exists(path))
            throw new IOException($"输出已经存在：{path}");

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"输出目录不存在：{parent}");

        var probe = Path.Combine(parent, $".se2sw-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex)
        {
            throw new IOException($"输出目录不可写：{parent}", ex);
        }
    }
}
