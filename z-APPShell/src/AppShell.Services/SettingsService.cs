using System.Text.Json;
using AppShell.Core.Storage;

namespace AppShell.Services;

/// <summary>
/// 应用设置(F-01/F-03):%AppData%/&lt;应用名&gt;/settings.json,扁平键值对。
/// app.set / app.get 指令与派生应用共用;写入即落盘。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public SettingsService(AppPaths paths)
    {
        _filePath = Path.Combine(paths.Root, "settings.json");
        try
        {
            if (File.Exists(_filePath))
            {
                _values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                              File.ReadAllText(_filePath))
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _values = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            // 设置文件损坏时按空配置起步,不阻断启动
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) ? v : null;
        }
    }

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var v) ? v : fallback;

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            _values[key] = value;
            Persist();
        }
    }

    /// <summary>全部键(app.get 不带参数时列出)。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> All()
    {
        lock (_gate)
        {
            return _values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
    }
}
