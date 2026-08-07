using System.Windows;
using AppShell.ServiceHost;

namespace AppShell.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(argument => argument.Equals("--service", StringComparison.OrdinalIgnoreCase)))
        {
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("无法确定 AppShell 可执行文件路径");
            return global::AppShell.ServiceHost.ServiceHost.Run(
                App.BuildServiceComposition(executable),
                executable,
                ["--service"]);
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
