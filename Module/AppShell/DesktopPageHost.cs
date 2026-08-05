using System.ComponentModel;
using System.Windows;
using AvalonDock;
using AvalonDock.Layout;

namespace OneHistory.AppShell.Desktop;

internal sealed class DesktopPageHost
{
    private readonly DockingManager _manager;
    private readonly DesktopPageCatalog _catalog;
    private readonly LayoutDocumentPane _mainPane = new();
    private readonly Dictionary<string, LayoutDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastState;

    public DesktopPageHost(DockingManager manager, DesktopPageCatalog catalog)
    {
        _manager = manager;
        _catalog = catalog;
        _manager.Layout = new LayoutRoot
        {
            RootPanel = new LayoutPanel(_mainPane),
        };
        _manager.ActiveContentChanged += (_, _) => PublishStateIfChanged();
        _manager.LayoutUpdated += (_, _) => PublishStateIfChanged();
    }

    public event EventHandler? StateChanged;

    public DesktopPageInfo? ActivePage
        => ListPages().FirstOrDefault(page => page.IsActive);

    public IReadOnlyList<DesktopPageInfo> ListPages()
        => _catalog.Pages.Select(page =>
        {
            var document = FindDocument(page.Id);
            return new DesktopPageInfo(
                page.Id,
                page.Title,
                page.Owner,
                document?.Parent != null,
                document?.IsActive == true,
                document != null && IsFloating(document),
                page.CanClose);
        }).ToList();

    public bool Activate(string id)
    {
        var page = _catalog.Find(id);
        if (page is null)
            return false;

        var document = GetOrCreateDocument(page);
        if (document.Parent is null)
            _mainPane.Children.Add(document);

        document.IsSelected = true;
        document.IsActive = true;
        _manager.ActiveContent = document.Content;
        _manager.UpdateLayout();
        PublishStateIfChanged();
        return true;
    }

    public bool Float(string id)
    {
        if (!Activate(id))
            return false;

        var document = FindDocument(id)!;
        if (!IsFloating(document))
            document.Float();
        document.IsSelected = true;
        document.IsActive = true;
        _manager.UpdateLayout();
        PublishStateIfChanged();
        return true;
    }

    public bool Dock(string id)
    {
        var document = FindDocument(id);
        if (document is null || document.Parent is null)
            return Activate(id);

        if (!ReferenceEquals(document.Parent, _mainPane))
        {
            Detach(document);
            _mainPane.Children.Add(document);
        }

        document.IsSelected = true;
        document.IsActive = true;
        _manager.ActiveContent = document.Content;
        _manager.UpdateLayout();
        PublishStateIfChanged();
        return true;
    }

    public bool Close(string id)
    {
        var page = _catalog.Find(id);
        var document = FindDocument(id);
        if (page is null || document is null || document.Parent is null || !page.CanClose)
            return false;

        document.Close();
        PublishStateIfChanged();
        return true;
    }

    public bool Unregister(string id)
    {
        if (_catalog.Find(id) is null)
            return false;

        if (_documents.Remove(id, out var document))
        {
            if (document.Parent != null)
                Detach(document);
            document.Content = null;
        }

        var removed = _catalog.Unregister(id);
        PublishStateIfChanged();
        return removed;
    }

    private LayoutDocument GetOrCreateDocument(DesktopPageDefinition page)
    {
        if (_documents.TryGetValue(page.Id, out var existing))
            return existing;

        FrameworkElement view;
        try
        {
            view = page.CreateView();
        }
        catch (Exception exception)
        {
            view = PageViewFactory.CreateInfo(
                "页面加载失败",
                page.Title,
                exception.Message);
        }

        var document = new LayoutDocument
        {
            ContentId = page.Id,
            Title = page.Title,
            Content = view,
            CanClose = page.CanClose,
            CanFloat = true,
        };
        document.PropertyChanged += OnDocumentPropertyChanged;
        document.Closed += OnDocumentClosed;
        _documents.Add(page.Id, document);
        return document;
    }

    private LayoutDocument? FindDocument(string id)
        => _documents.GetValueOrDefault(id);

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutContent.IsActive) or nameof(LayoutContent.IsSelected) or nameof(LayoutContent.Parent))
            PublishStateIfChanged();
    }

    private void OnDocumentClosed(object? sender, EventArgs e)
    {
        if (sender is not LayoutDocument document || document.ContentId is not { } id)
            return;

        if (_documents.TryGetValue(id, out var current) && ReferenceEquals(current, document))
            _documents.Remove(id);
        document.Content = null;
        PublishStateIfChanged();
    }

    private void PublishStateIfChanged()
    {
        var state = string.Join('|', ListPages().Select(page =>
            $"{page.Id}:{page.IsOpen}:{page.IsActive}:{page.IsFloating}"));
        if (state == _lastState)
            return;

        _lastState = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Detach(LayoutDocument document)
    {
        if (document.Parent is ILayoutContainer container)
            container.RemoveChild(document);
    }

    private static bool IsFloating(ILayoutElement element)
    {
        for (var current = element.Parent; current != null; current = current.Parent)
        {
            if (current is LayoutFloatingWindow)
                return true;
        }
        return false;
    }
}
