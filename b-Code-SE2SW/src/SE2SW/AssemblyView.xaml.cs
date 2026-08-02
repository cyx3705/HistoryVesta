using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SE2SW;

public partial class AssemblyView : UserControl, IDisposable
{
    private readonly AssemblyViewModel _viewModel = new();

    public AssemblyView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnChooseAssemblyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Solid Edge 装配体",
            Filter = "Solid Edge 装配体 (*.asm)|*.asm|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        var directory = Path.GetDirectoryName(_viewModel.SourceAssemblyPath);
        if (Directory.Exists(directory))
            dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _viewModel.SetSourceFile(dialog.FileName);
    }

    private async void OnProbeClick(object sender, RoutedEventArgs e)
        => await _viewModel.ProbeAsync();

    private async void OnConvertClick(object sender, RoutedEventArgs e)
        => await _viewModel.ConvertAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => _viewModel.Cancel();

    public void Dispose()
        => _viewModel.Dispose();
}
