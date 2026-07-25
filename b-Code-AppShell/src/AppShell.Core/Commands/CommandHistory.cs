namespace AppShell.Core.Commands;

/// <summary>
/// 手输指令历史(C-11):↑/↓ 翻阅 + 持久化(默认 500 条,可配)。
/// 线程安全;文件一行一条,追加式加载、整体重写保存。
/// </summary>
public sealed class CommandHistory
{
    private readonly object _gate = new();
    private readonly List<string> _items = new();
    private readonly string _filePath;
    private readonly int _capacity;

    public CommandHistory(string filePath, int capacity = 500)
    {
        _filePath = filePath;
        _capacity = Math.Max(10, capacity);
        try
        {
            if (File.Exists(filePath))
            {
                _items.AddRange(
                    File.ReadAllLines(filePath)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .TakeLast(_capacity));
            }
        }
        catch (IOException)
        {
            // 历史读不出来不影响使用
        }
    }

    /// <summary>追加一条(与上一条重复时不重复入表)。</summary>
    public void Add(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
            return;

        lock (_gate)
        {
            if (_items.Count > 0 && _items[^1] == text)
                return;
            _items.Add(text);
            while (_items.Count > _capacity)
                _items.RemoveAt(0);
        }
    }

    /// <summary>最新在末尾的快照。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToList();
        }
    }

    /// <summary>持久化到文件(退出时调用)。</summary>
    public void Save()
    {
        try
        {
            lock (_gate)
            {
                File.WriteAllLines(_filePath, _items);
            }
        }
        catch (IOException)
        {
        }
    }
}
