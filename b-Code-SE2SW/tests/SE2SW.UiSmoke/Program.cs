using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using SE2SW;

namespace SE2SW.UiSmoke;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new Application();
        var showAssembly = args.Contains("--assembly", StringComparer.OrdinalIgnoreCase);
        var showOhs = args.Contains("--ohs", StringComparer.OrdinalIgnoreCase);
        var compact = args.Contains("--compact", StringComparer.OrdinalIgnoreCase);
        var selectedMode = showOhs ? 2 : showAssembly ? 1 : 0;
        var workspace = new SE2SWWorkspaceView
        {
            SelectedPageIndex = selectedMode,
        };
        var window = new Window
        {
            Title = selectedMode switch
            {
                1 => "SE2SW UI Smoke · 装配转换",
                2 => "SE2SW UI Smoke · OHS 兼容",
                _ => "SE2SW UI Smoke · 零件转换",
            },
            Width = compact ? 820 : 1280,
            Height = compact ? 620 : 820,
            Content = workspace,
        };
        var captureIndex = Array.FindIndex(args, item =>
            string.Equals(item, "--capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0 && captureIndex + 1 < args.Length)
        {
            window.Show();
            workspace.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(workspace);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(workspace.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(workspace.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(workspace);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.GetFullPath(args[captureIndex + 1])))
                encoder.Save(stream);
            window.Close();
            workspace.Dispose();
            return;
        }
        application.Run(window);
    }
}
