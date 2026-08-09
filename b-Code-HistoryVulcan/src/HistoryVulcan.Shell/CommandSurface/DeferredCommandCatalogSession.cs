using HistoryVulcan.Core.CommandSurface;

namespace HistoryVulcan.Shell.CommandSurface;

/// <summary>
/// Console-side proxy created before Mercury CreateUi. Forwards to the real session after Attach.
/// </summary>
internal sealed class DeferredCommandCatalogSession : ICommandCatalogSession
{
    private readonly object _gate = new();
    private ICommandCatalogSession? _inner;
    private EventHandler<CommandCatalogChangedEventArgs>? _changed;
    private bool _disposed;

    public event EventHandler<CommandCatalogChangedEventArgs>? Changed
    {
        add
        {
            _changed += value;
            var inner = Inner;
            if (inner != null)
                inner.Changed += value;
        }
        remove
        {
            _changed -= value;
            var inner = Inner;
            if (inner != null)
                inner.Changed -= value;
        }
    }

    public IReadOnlyList<string> Domains => Inner?.Domains ?? [];

    public IReadOnlyList<string> Classes => Inner?.Classes ?? [];

    public string? SelectedCommandName => Inner?.SelectedCommandName;

    public CommandCatalogFilter CurrentFilter => Inner?.CurrentFilter ?? new CommandCatalogFilter();

    private ICommandCatalogSession? Inner
    {
        get { lock (_gate) return _inner; }
    }

    public void Attach(ICommandCatalogSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        EventHandler<CommandCatalogChangedEventArgs>? handlers;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (ReferenceEquals(_inner, session))
                return;

            // do not dispose — owned by Mercury CommandSurfaceFeature
            if (_inner != null && _changed != null)
                _inner.Changed -= OnInnerChanged;

            _inner = session;
            handlers = _changed;
            if (handlers != null)
                _inner.Changed += OnInnerChanged;
        }

        handlers?.Invoke(this, new CommandCatalogChangedEventArgs(CommandCatalogChangeKind.Invalidated));
    }

    public Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
        => Inner?.RefreshAsync(force, cancellationToken) ?? Task.FromResult(false);

    public void SetFilter(CommandCatalogFilter filter) => Inner?.SetFilter(filter);

    public bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains)
    {
        var inner = Inner;
        if (inner == null)
        {
            availableDomains = ["全部"];
            return false;
        }

        return inner.TrySetDomain(domain, out availableDomains);
    }

    public bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses)
    {
        var inner = Inner;
        if (inner == null)
        {
            availableClasses = ["全部"];
            return false;
        }

        return inner.TrySetCommandClass(commandClass, out availableClasses);
    }

    public void SetConsoleQuery(string query) => Inner?.SetConsoleQuery(query);

    public bool MoveSelection(int direction) => Inner?.MoveSelection(direction) ?? false;

    public void Select(string? commandName) => Inner?.Select(commandName);

    public Task<ConsoleCompletionResult> CompleteAsync(
        string text,
        int caretIndex,
        CancellationToken cancellationToken = default)
        => Inner?.CompleteAsync(text, caretIndex, cancellationToken)
           ?? Task.FromResult(ConsoleCompletionResult.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_inner != null)
            {
                if (_changed != null)
                    _inner.Changed -= OnInnerChanged;
                // Session lifetime belongs to Mercury CommandSurfaceFeature.
                _inner = null;
            }
        }
    }

    private void OnInnerChanged(object? sender, CommandCatalogChangedEventArgs e)
        => _changed?.Invoke(this, e);
}
