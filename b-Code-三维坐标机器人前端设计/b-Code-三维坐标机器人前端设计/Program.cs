using System.Runtime.InteropServices;

namespace b_Code_三维坐标机器人前端设计;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "serial-test" or "serialtest" or "--serial-test")
        {
            AllocConsole();
            string[] testArgs = args.Skip(1).ToArray();
            return SerialPortCliTester.Run(testArgs);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
        return 0;
    }
}