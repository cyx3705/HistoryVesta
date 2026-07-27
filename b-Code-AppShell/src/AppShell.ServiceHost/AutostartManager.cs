using Microsoft.Win32;

namespace AppShell.ServiceHost;

/// <summary>用户级登录启动项；接口用于在测试中替换为内存实现。</summary>
public interface IAutostartManager
{
    bool IsEnabled(string name);

    void SetEnabled(string name, string executablePath, bool enabled);
}

public sealed class WindowsRunAutostartManager : IAutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(string name, string executablePath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(name, $"\"{executablePath}\"");
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }
}
