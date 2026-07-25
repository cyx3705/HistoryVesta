namespace AppShell.Core.Storage;

/// <summary>
/// 应用设置读写(F-01/F-03):扁平键值对,app.set / app.get 指令与派生应用共用。
/// </summary>
public interface ISettingsService
{
    string? Get(string key);

    int GetInt(string key, int fallback);

    void Set(string key, string value);

    IReadOnlyList<KeyValuePair<string, string>> All();
}
