using System.Windows.Threading;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Modules;

namespace AppShell.Shell.Modules;

/// <summary>把模块界面注册请求编组到 Shell UI 线程。</summary>
public sealed class ShellUiRegistrar : IShellUiRegistrar
{
    private readonly IDockingService _docking;
    private readonly Dispatcher _dispatcher;
    private readonly IShellLog _log;
    private readonly Dictionary<string, HashSet<string>> _byOwner = new(StringComparer.OrdinalIgnoreCase);

    public ShellUiRegistrar(IDockingService docking, Dispatcher dispatcher, IShellLog log)
    {
        _docking = docking;
        _dispatcher = dispatcher;
        _log = log;
    }

    public bool IsUiThread => _dispatcher.CheckAccess();

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsUiThread)
            action();
        else
            _dispatcher.Invoke(action);
    }

    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Invoke(() =>
        {
            _docking.RegisterWindow(descriptor, owner);
            Track(owner, descriptor.Id);
        });
        return new Registration(() => Invoke(() => UnregisterToolWindow(descriptor.Id)));
    }

    public void UnregisterToolWindow(string id)
        => Invoke(() =>
        {
            _docking.UnregisterWindow(id);
            Untrack(id);
        });

    public void UnregisterOwner(string owner)
        => Invoke(() =>
        {
            try
            {
                _docking.UnregisterOwner(owner);
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"回收模块界面失败 ({owner}): {ex.Message}");
            }
            finally
            {
                _byOwner.Remove(owner);
            }
        });

    private void Track(string owner, string id)
    {
        if (!_byOwner.TryGetValue(owner, out var ids))
            _byOwner[owner] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ids.Add(id);
    }

    private void Untrack(string id)
    {
        foreach (var owner in _byOwner.Keys.ToArray())
        {
            _byOwner[owner].Remove(id);
            if (_byOwner[owner].Count == 0)
                _byOwner.Remove(owner);
        }
    }

    private sealed class Registration(Action? dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
