using System.IO;
using AppShell.ServiceHost;
using AppShell.Core.Mcp;
using OneHistoryStudio.Service;

namespace OneHistoryStudio.LegacyServiceHost;

internal static class LegacyServiceProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        var manualIndex = Array.FindIndex(args, value =>
            value.Equals("--generate-manual", StringComparison.OrdinalIgnoreCase));
        if (manualIndex >= 0)
        {
            if (manualIndex + 1 >= args.Length)
                return 64;
            return GenerateManual(args[manualIndex + 1]);
        }

        var registerAutostart = !args.Contains("--no-autostart", StringComparer.OrdinalIgnoreCase);
        var composition = StudioServiceCompositionFactory.Create(registerAutostart);
        return ServiceHost.Run(composition);
    }

    private static int GenerateManual(string outputPath)
    {
        var temporaryName = "OneHistoryStudio.Manual." + Guid.NewGuid().ToString("N");
        var temporaryRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            temporaryName);
        var composition = StudioServiceCompositionFactory.Create(false, temporaryName);
        try
        {
            ServiceCommands.RegisterAll(
                composition.Registry,
                composition,
                static () => { },
                "OneHistoryStudio.Service.exe");
            var exporter = new CommandSchemaExporter(composition.Registry);
            var markdown = CommandManualGenerator.Render(composition.Registry, exporter, "readonly");
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, markdown, new System.Text.UTF8Encoding(false));
            Console.WriteLine(
                $"command-count={composition.Registry.All().Count} sha256={CommandManualGenerator.Sha256(markdown)}");
            return 0;
        }
        finally
        {
            composition.Dispose();
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}
