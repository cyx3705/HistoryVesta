using System.IO;
using ActiveDock;

var cache = Path.Combine(Path.GetTempPath(), "activedock-icons-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cache);
try
{
    var names = new[]
    {
        "2026-020-中文项目",
        "2026-999-This-Is-A-Very-Long-Project-Name-For-Rendering",
        "2026-123-A_B [special] #1",
    };
    foreach (var name in names)
    {
        var bitmap = ProjectIconGenerator.Create(name, cache);
        if (bitmap.PixelWidth != 64 || bitmap.PixelHeight != 64)
            throw new InvalidOperationException($"Unexpected bitmap size for {name}");
    }

    var count = Directory.GetFiles(cache, "*.png").Length;
    if (count != names.Length)
        throw new InvalidOperationException($"Expected {names.Length} PNG files, got {count}");
    Console.WriteLine($"ActiveDockIconSmoke: PASS ({count} PNG)");
}
finally
{
    Directory.Delete(cache, recursive: true);
}
