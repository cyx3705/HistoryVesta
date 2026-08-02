using System.Security.Cryptography;
using System.Text;

namespace ToolKit;

public class Kit
{
    /// <summary>计算文本的 SHA-256(十六进制大写)</summary>
    /// <param name="text">要哈希的文本</param>
    public string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>把 UTF-8 文本编码为 Base64</summary>
    /// <param name="text">要编码的文本</param>
    public string Base64Encode(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>生成一个新 GUID</summary>
    public string NewGuid() => Guid.NewGuid().ToString();

    /// <summary>当前本地时间与 Unix 秒</summary>
    public static object Now() => new
    {
        local = DateTime.Now,
        unix = DateTimeOffset.Now.ToUnixTimeSeconds(),
    };
}
