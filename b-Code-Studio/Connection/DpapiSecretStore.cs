using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace OneHistoryStudio.Connection;

/// <summary>客户端设备 token 的当前用户 DPAPI 存储；bootstrap.json 只保存公开标识。</summary>
public sealed class DpapiSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OneHistoryStudio.LanDeviceToken.v1");
    private readonly string _directory;

    public DpapiSecretStore(string root)
    {
        _directory = Path.Combine(root, "secrets");
    }

    public string? Read(string serverId)
    {
        var path = Resolve(serverId);
        if (!File.Exists(path))
            return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Write(string serverId, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token 不能为空", nameof(token));
        Directory.CreateDirectory(_directory);
        var plain = Encoding.UTF8.GetBytes(token);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Resolve(serverId), protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete(string serverId)
    {
        var path = Resolve(serverId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string Resolve(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)
            || serverId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("serverId 无效", nameof(serverId));
        return Path.Combine(_directory, serverId + ".bin");
    }
}
