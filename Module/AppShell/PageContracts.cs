using System.Collections.ObjectModel;
using System.Windows;

namespace OneHistory.AppShell.Desktop;

public sealed record DesktopPageDefinition
{
    public DesktopPageDefinition(
        string id,
        string title,
        Func<FrameworkElement> createView,
        string owner = "AppShell",
        bool canClose = true)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Page id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Page title is required.", nameof(title));

        ArgumentNullException.ThrowIfNull(createView);
        Id = id;
        Title = title;
        CreateView = createView;
        Owner = string.IsNullOrWhiteSpace(owner) ? "AppShell" : owner;
        CanClose = canClose;
    }

    public string Id { get; }
    public string Title { get; }
    public string Owner { get; }
    public bool CanClose { get; }
    public Func<FrameworkElement> CreateView { get; }
}

public sealed record DesktopPageInfo(
    string Id,
    string Title,
    string Owner,
    bool IsOpen,
    bool IsActive,
    bool IsFloating,
    bool CanClose);

public sealed class DesktopPageCatalog
{
    private readonly ObservableCollection<DesktopPageDefinition> _pages = [];

    public DesktopPageCatalog()
        => Pages = new ReadOnlyObservableCollection<DesktopPageDefinition>(_pages);

    public ReadOnlyObservableCollection<DesktopPageDefinition> Pages { get; }

    public event EventHandler? Changed;

    public void Register(DesktopPageDefinition page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (_pages.Any(existing => existing.Id.Equals(page.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Page id already registered: {page.Id}");

        _pages.Add(page);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string id)
    {
        var page = Find(id);
        if (page is null)
            return false;

        _pages.Remove(page);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public DesktopPageDefinition? Find(string id)
        => _pages.FirstOrDefault(page => page.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
