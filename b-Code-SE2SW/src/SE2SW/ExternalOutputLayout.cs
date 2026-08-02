using System.IO;

namespace SE2SW;

public static class ExternalOutputLayout
{
    public static (string XtDirectory, string SolidWorksDirectory) Resolve(string sourceDirectory)
    {
        var root = Path.GetFullPath(sourceDirectory);
        return (Path.Combine(root, "XT"), Path.Combine(root, "SW"));
    }

    public static void EnsureDirectories(string sourceDirectory)
    {
        var (xt, sw) = Resolve(sourceDirectory);
        var conflicts = new[] { xt, sw }.Where(File.Exists).ToArray();
        if (conflicts.Length > 0)
            throw new IOException($"输出目录被同名文件占用：{string.Join("；", conflicts)}");
        Directory.CreateDirectory(xt);
        Directory.CreateDirectory(sw);
    }
}
