namespace OneHistoryStudio.Views;

/// <summary>进程内共享的命令集选择，只传递稳定的指令名。</summary>
public sealed class CommandSelectionState
{
    private string? _currentCommandName;

    public string? CurrentCommandName
    {
        get => _currentCommandName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_currentCommandName, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _currentCommandName = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;
}
