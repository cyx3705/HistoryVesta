using System.IO;
using SE2SW.Contracts;

namespace SE2SW;

public static class FileScanner
{
    public static bool HasPartFiles(string selectedDirectory)
    {
        var workingDirectory = NormalizeExistingDirectory(selectedDirectory);
        return Directory.EnumerateFiles(workingDirectory, "*", SearchOption.TopDirectoryOnly)
            .Any(path => string.Equals(Path.GetExtension(path), ".par", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<ScanCandidate> Scan(
        ConversionMode mode,
        string selectedDirectory,
        ProjectLayout? layout = null)
    {
        var workingDirectory = NormalizeExistingDirectory(selectedDirectory);
        var sourceDirectory = mode == ConversionMode.Ohs
            ? layout?.SourceDirectory ?? throw new ArgumentNullException(nameof(layout))
            : workingDirectory;
        var xtDirectory = mode == ConversionMode.Ohs ? layout!.XtDirectory : Path.Combine(workingDirectory, "XT");
        var swDirectory = mode == ConversionMode.Ohs ? layout!.SolidWorksDirectory : Path.Combine(workingDirectory, "SW");

        return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".par", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var xtPath = Path.Combine(xtDirectory, name + ".x_t");
                var swPath = Path.Combine(swDirectory, name + ".SLDPRT");
                var legacyXt = Path.Combine(workingDirectory, name + ".x_t");
                var legacySw = Path.Combine(workingDirectory, name + ".SLDPRT");
                var exists = File.Exists(xtPath) || File.Exists(swPath)
                    || mode == ConversionMode.External && (File.Exists(legacyXt) || File.Exists(legacySw));
                return new ScanCandidate(path, xtPath, swPath, exists);
            })
            .ToArray();
    }

    private static string NormalizeExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请选择文件夹。", nameof(path));
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"文件夹不存在：{fullPath}");
        return fullPath;
    }
}
