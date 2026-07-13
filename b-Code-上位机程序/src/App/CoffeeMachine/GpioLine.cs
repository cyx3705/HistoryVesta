using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AppShell.App.CoffeeMachine;

public sealed class GpioLine : INotifyPropertyChanged
{
    private bool _isActive;

    public GpioLine(string pin, string purpose)
    {
        Pin = pin;
        Purpose = purpose;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Pin { get; }

    public string Purpose { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;

            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => IsActive ? "激活" : "关闭";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
