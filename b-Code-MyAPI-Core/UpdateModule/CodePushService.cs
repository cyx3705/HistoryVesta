//UpdateModule/Update.cs
using BaseVariable;
using CodePushModule;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace CodePushModule;

public class CodePushService
{
    /// <summary>
    /// 执行代码推送：只读取 InternalPush 文件夹中的文件并返回（手动推送模式）
    /// 不自动执行 dotnet publish，开发者需手动把最新文件放入 InternalPush 目录
    /// </summary>
    public static async Task<CodePushResult> ExecutePushAsync()
    {
        var result = new CodePushResult();
        string pushDir = BSV.pushDir;

        try
        {
            Console.WriteLine("=== CodePushService 开始执行（手动推送模式） ===");
            Console.WriteLine($"读取推送目录: {pushDir}");

            if (!Directory.Exists(pushDir))
            {
                throw new Exception($"推送目录不存在: {pushDir}。请手动将最新发布的文件放入此目录。");
            }

            var files = Directory.GetFiles(pushDir, "*.*", SearchOption.AllDirectories);

            if (files.Length == 0)
            {
                throw new Exception("推送目录为空。请手动执行发布并将文件复制到 InternalPush 目录。");
            }

            foreach (var file in files)
            {
                string relativePath = Path.GetRelativePath(pushDir, file);
                byte[] bytes = await File.ReadAllBytesAsync(file);

                result.Files[relativePath] = bytes;

                Console.WriteLine($"已读取文件: {relativePath} ({bytes.Length} bytes)");
            }

            result.Success = true;
            result.Message = $"手动推送读取成功，共 {result.Files.Count} 个文件。局域网其他机器可直接拉取 InternalPush 目录内容。";

            Console.WriteLine(result.Message);
            Console.WriteLine($"推送目录路径: {pushDir}");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"推送读取失败：{ex.Message}";
            Console.WriteLine(result.Message);
        }

        return result;
    }
}