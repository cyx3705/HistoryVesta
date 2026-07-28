using System.IO;
using System.Windows;
using AppShell.Core;
using AppShell.Core.Mcp;
using AppShell.ServiceHost;
using Microsoft.Data.Sqlite;
using OneHistoryStudio.Connection;
using OneHistoryStudio.Service;

namespace OneHistoryStudio;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppIdentity.Use(typeof(Program).Assembly);
        if (args.Length > 0
            && (args[0].Equals("--lan-install", StringComparison.OrdinalIgnoreCase)
                || args[0].Equals("--lan-remove", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length != 2)
                return 64;
            var identity = AppIdentity.Current;
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                identity.Name);
            return LanMachineHelper.Run(args[0], args[1], root, identity.Version);
        }

        if (args.Length > 0 && args[0].Equals("--service-host", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1 && !args[1].Equals("--no-autostart", StringComparison.OrdinalIgnoreCase))
                return Usage("--service-host 只接受可选的 --no-autostart");
            var registerAutostart = !args.Contains("--no-autostart", StringComparer.OrdinalIgnoreCase);
            return ServiceHost.Run(
                StudioServiceCompositionFactory.Create(registerAutostart),
                Environment.ProcessPath,
                ["--service-host"]);
        }

        if (args.Length > 0 && args[0].Equals("--generate-manual", StringComparison.OrdinalIgnoreCase))
            return args.Length == 2 ? GenerateManual(args[1]) : Usage("--generate-manual 需要一个输出路径");

        if (args.Any(argument => argument.StartsWith("--", StringComparison.OrdinalIgnoreCase)
                                 && !argument.Equals("--exec", StringComparison.OrdinalIgnoreCase)))
            return Usage("未知启动参数");

        var application = new App();
        application.InitializeComponent();
        return application.Run();
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
                Environment.ProcessPath ?? "OneHistoryStudio.exe",
                serviceArguments: ["--service-host"]);
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
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static int Usage(string message)
    {
        MessageBox.Show(
            message + "\n\n允许：--service-host [--no-autostart]、--generate-manual <path>、--exec <command>",
            "OneHistoryStudio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return 64;
    }
}
