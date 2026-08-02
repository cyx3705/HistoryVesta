namespace SE2SW.Worker;

internal static class TemporaryOutput
{
    public static string For(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidDataException($"无法解析输出目录：{finalPath}");
        var baseName = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(directory, $".{baseName}.{Guid.NewGuid():N}.tmp{extension}");
    }

    public static void Commit(string temporaryPath, string finalPath)
        => File.Move(temporaryPath, finalPath, overwrite: false);

    public static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
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
