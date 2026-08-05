using System.Globalization;
using System.Net;

namespace MyAPI.Host;

public sealed record MyApiHostOptions
{
    public bool EnableHttp { get; init; }
    public bool EnableMcp { get; init; }
    public bool EnableModules { get; init; }
    public bool EnableHotReload { get; init; }
    public bool AllowRemote { get; init; }
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5100;
    public string ModulesDirectory { get; init; } = string.Empty;

    public string ListenUrl
    {
        get
        {
            var host = IPAddress.TryParse(BindAddress, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{BindAddress}]"
                : BindAddress;
            return $"http://{host}:{Port}";
        }
    }

    public static MyApiHostOptions From(Func<string, string?> read, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var options = new MyApiHostOptions
        {
            EnableHttp = ReadBool(read, "EnableHttp"),
            EnableMcp = ReadBool(read, "EnableMcp"),
            EnableModules = ReadBool(read, "EnableModules"),
            EnableHotReload = ReadBool(read, "EnableHotReload"),
            AllowRemote = ReadBool(read, "AllowRemote"),
            BindAddress = ReadText(read, "BindAddress", "127.0.0.1"),
            Port = ReadPort(read("Port")),
            ModulesDirectory = Path.GetFullPath(ReadText(read, "ModulesDir", Path.Combine(baseDirectory, "Modules")))
        };

        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
        if (!IPAddress.TryParse(BindAddress, out var address)) throw new InvalidOperationException($"BindAddress '{BindAddress}' is not a valid IP address.");
        if (!AllowRemote && !IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Remote binding requires AllowRemote=true.");
        if (EnableMcp && !EnableHttp) throw new InvalidOperationException("EnableMcp requires EnableHttp=true.");
        if (EnableHotReload && !EnableModules) throw new InvalidOperationException("EnableHotReload requires EnableModules=true.");
    }

    private static bool ReadBool(Func<string, string?> read, string key)
    {
        var value = read(key);
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (value is "1" or "yes" or "on") return true;
        if (value is "0" or "no" or "off") return false;
        throw new InvalidOperationException($"{key} must be a boolean value.");
    }

    private static string ReadText(Func<string, string?> read, string key, string fallback)
    {
        var value = read(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int ReadPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 5100;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return port;
        throw new InvalidOperationException("Port must be an integer.");
    }
}
