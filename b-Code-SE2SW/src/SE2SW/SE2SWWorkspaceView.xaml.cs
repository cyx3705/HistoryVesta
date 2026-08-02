using System.Windows.Controls;
using System.Windows;
using SE2SW.Contracts;

namespace SE2SW;

public partial class SE2SWWorkspaceView : UserControl, IDisposable
{
    private int _selectedPageIndex;

    public SE2SWWorkspaceView()
    {
        InitializeComponent();
        PartPage.ModeChanged += OnPartModeChanged;
        SelectMode(0);
    }

    public int PageCount => 3;

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (value < 0 || value >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(value));
            SelectMode(value);
        }
    }

    public ConversionMode ActivePartMode => PartPage.Mode;

    private void OnPartModeClick(object sender, RoutedEventArgs e) => SelectMode(0);

    private void OnAssemblyModeClick(object sender, RoutedEventArgs e) => SelectMode(1);

    private void OnOhsModeClick(object sender, RoutedEventArgs e) => SelectMode(2);

    private void OnPartModeChanged(object? sender, EventArgs e)
    {
        if (_selectedPageIndex == 1)
            return;
        UpdateModeChrome(PartPage.Mode == ConversionMode.Ohs ? 2 : 0);
    }

    private void SelectMode(int index)
    {
        UpdateModeChrome(index);
        if (index != 1)
            PartPage.SetMode(index == 2 ? ConversionMode.Ohs : ConversionMode.External);
    }

    private void UpdateModeChrome(int index)
    {
        _selectedPageIndex = index;
        PartModeButton.IsChecked = index == 0;
        AssemblyModeButton.IsChecked = index == 1;
        OhsModeButton.IsChecked = index == 2;
        PartPage.Visibility = index == 1 ? Visibility.Collapsed : Visibility.Visible;
        AssemblyPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        PartPage.ModeChanged -= OnPartModeChanged;
        PartPage.Dispose();
        AssemblyPage.Dispose();
    }
}
