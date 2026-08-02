using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public sealed record AssemblyPlanIssue(
    ConversionErrorClass ErrorClass,
    string Message);

public sealed record AssemblyConversionPlan(
    string SourceAssemblyPath,
    string SourceDirectory,
    string XtDirectory,
    string SolidWorksDirectory,
    string AssemblyOutputPath,
    IReadOnlyList<ScanCandidate> Parts,
    IReadOnlyList<AssemblyOccurrence> Occurrences,
    IReadOnlyList<AssemblyPlanIssue> BlockingIssues,
    IReadOnlyList<string> Warnings)
{
    public bool CanConvert => BlockingIssues.Count == 0 && Parts.Count > 0;
}

public static class AssemblyPlanner
{
    public static AssemblyConversionPlan Create(AssemblyProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!Path.IsPathFullyQualified(probe.SourceAssemblyPath)
            || !string.Equals(Path.GetExtension(probe.SourceAssemblyPath), ".asm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("装配探查结果中的源路径不是绝对 .asm 路径。");
        }

        var sourceAssemblyPath = Path.GetFullPath(probe.SourceAssemblyPath);
        var sourceDirectory = Path.GetDirectoryName(sourceAssemblyPath)
            ?? throw new InvalidDataException("无法解析装配体所在目录。");
        var (xtDirectory, swDirectory) = ExternalOutputLayout.Resolve(sourceDirectory);
        var assemblyOutputPath = Path.Combine(
            swDirectory,
            Path.GetFileNameWithoutExtension(sourceAssemblyPath) + ".SLDASM");
        var issues = new List<AssemblyPlanIssue>();
        var warnings = probe.Warnings.Distinct(StringComparer.Ordinal).ToList();

        if (!File.Exists(sourceAssemblyPath))
            issues.Add(new AssemblyPlanIssue(ConversionErrorClass.InputMissing, $"源装配体不存在：{sourceAssemblyPath}"));
        if (probe.UnresolvedCount > 0)
            issues.Add(new AssemblyPlanIssue(
                ConversionErrorClass.OccurrenceUnresolved,
                $"装配体有 {probe.UnresolvedCount} 个未解析引用，转换已阻止。"));

        CheckDirectoryNameConflict(xtDirectory, issues);
        CheckDirectoryNameConflict(swDirectory, issues);

        var supportedParts = probe.Occurrences
            .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
            .Select(item => Path.GetFullPath(item.SourcePath))
            .Where(path => string.Equals(Path.GetExtension(path), ".par", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var unsupported = probe.Occurrences
            .Where(item => !item.IsSubAssembly && !item.IsSuppressed)
            .Select(item => item.SourcePath)
            .Where(path => !string.Equals(Path.GetExtension(path), ".par", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in unsupported)
            warnings.Add($"跳过 V3.0 不支持的引用：{path}");

        foreach (var group in supportedParts.GroupBy(
                     path => Path.GetFileNameWithoutExtension(path),
                     StringComparer.OrdinalIgnoreCase))
        {
            var paths = group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length < 2)
                continue;
            issues.Add(new AssemblyPlanIssue(
                ConversionErrorClass.DuplicateOutputName,
                $"同名不同路径零件会映射到同一输出“{group.Key}”：{string.Join("；", paths)}"));
        }

        var parts = supportedParts.Select(path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var xtPath = Path.Combine(xtDirectory, name + ".x_t");
            var swPath = Path.Combine(swDirectory, name + ".SLDPRT");
            var legacyXt = Path.Combine(sourceDirectory, name + ".x_t");
            var legacySw = Path.Combine(sourceDirectory, name + ".SLDPRT");
            var exists = File.Exists(xtPath) || File.Exists(swPath)
                || File.Exists(legacyXt) || File.Exists(legacySw);
            var reusableXt = File.Exists(xtPath)
                ? xtPath
                : File.Exists(legacyXt) ? legacyXt : xtPath;
            var reusableSw = File.Exists(swPath)
                ? swPath
                : File.Exists(legacySw) ? legacySw : swPath;
            return new ScanCandidate(path, reusableXt, reusableSw, exists);
        }).ToArray();

        if (parts.Any(item => item.HasExistingOutput))
            warnings.Add("检测到已有零件产物；转换时会校验时间与格式，安全复用有效的分层或旧平铺 XT/SLDPRT，不覆盖用户文件。");
        if (File.Exists(assemblyOutputPath))
            issues.Add(new AssemblyPlanIssue(ConversionErrorClass.OutputExists, $"装配输出已经存在：{assemblyOutputPath}"));
        if (parts.Length == 0)
            issues.Add(new AssemblyPlanIssue(ConversionErrorClass.InputInvalid, "装配体中没有可转换的 .par 零件。"));

        if (probe.SuppressedCount > 0)
            warnings.Add($"将跳过 {probe.SuppressedCount} 个抑制实例。");
        var hiddenCount = probe.Occurrences.Count(item => item.IsHidden && !item.IsSuppressed && !item.IsSubAssembly);
        if (hiddenCount > 0)
            warnings.Add($"{hiddenCount} 个隐藏实例仍会插入，并保持普通可见组件。");
        warnings.Add("V3.0 将展平装配树、固定全部组件，不翻译配合。");

        return new AssemblyConversionPlan(
            sourceAssemblyPath,
            sourceDirectory,
            xtDirectory,
            swDirectory,
            assemblyOutputPath,
            parts,
            probe.Occurrences,
            issues,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void CheckDirectoryNameConflict(string path, ICollection<AssemblyPlanIssue> issues)
    {
        if (!File.Exists(path))
            return;
        issues.Add(new AssemblyPlanIssue(
            ConversionErrorClass.OutputNotWritable,
            $"输出目录被同名文件占用：{path}"));
    }
}
