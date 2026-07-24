using System.Reflection;

namespace OneHistoryStudio;

/// <summary>应用名称与版本的运行时单一真值，由 App.csproj 的程序集元数据生成。</summary>
public sealed record ApplicationIdentity(
    string Name,
    string Version,
    string InformationalVersion,
    string FileVersion);

public static class AppIdentity
{
    public static ApplicationIdentity Current { get; } = Load(typeof(App).Assembly);

    private static ApplicationIdentity Load(Assembly assembly)
    {
        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product?.Trim();
        var title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title?.Trim();
        if (string.IsNullOrWhiteSpace(product) || !string.Equals(product, title, StringComparison.Ordinal))
            throw new InvalidOperationException("程序集 Product 与 Title 必须是同一个非空应用名");

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Trim();
        if (string.IsNullOrWhiteSpace(informational))
            throw new InvalidOperationException("程序集 InformationalVersion 不能为空");

        var version = informational.Split('+', 2)[0];
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version?.Trim()
                          ?? assembly.GetName().Version?.ToString()
                          ?? version;

        return new ApplicationIdentity(product, version, informational, fileVersion);
    }
}
