using BaseVariable;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BaseRegister
{
    public static class DynamicEndpointRegistry
    {
        private static readonly List<DynamicEndpoint> _endpoints = new();

        public static IReadOnlyList<DynamicEndpoint> AllEndpoints => _endpoints.AsReadOnly();

        /// <summary>
        /// 注册一个动态接口（支持去重 + 自动读取 XML <summary> 注释）
        /// </summary>
        public static void Register(string ns, string cls, string method, string fallbackDescription = "")
        {
            var fullPath = $"/api/{ns}/{cls}/{method}";

            // 去重
            if (_endpoints.Any(e => e.FullPath == fullPath))
                return;

            // 优先尝试从 XML 中读取真实的 <summary> 注释
            string description = GetMethodSummaryFromXml(ns, cls, method)
                                 ?? fallbackDescription
                                 ?? $"模块自动注册 - {cls}";

            _endpoints.Add(new DynamicEndpoint
            {
                Namespace = ns,
                Class = cls,
                Method = method,
                FullPath = fullPath,
                Description = description,
                RegisteredAt = DateTime.Now
            });

            Console.WriteLine($"[EndpointRegistry] 注册接口 → {fullPath} | {description}");
        }

        /// <summary>
        /// 从固定目录读取方法的 <summary> 注释（推荐用于你的局域网 + 单文件发布场景）
        /// </summary>
        private static string? GetMethodSummaryFromXml(string ns, string cls, string method)
        {
            try
            {
                // === 关键修改：固定从 Internal 目录查找 XML ===
                string publishDir = BSV.pushDir;   // 你的固定发布目录 C:\MyAPI\Internal

                // 构造可能的 XML 文件名（通常和模块 DLL 同名）
                string xmlFileName = $"{ns}.xml";           // 最常见：CmdModule.xml、MyUtilsModule.xml 等
                string xmlPath = Path.Combine(publishDir, xmlFileName);

                // 如果按 ns 找不到，尝试按类所在程序集名称查找（备选）
                if (!File.Exists(xmlPath))
                {
                    xmlFileName = $"{cls}.xml";
                    xmlPath = Path.Combine(publishDir, xmlFileName);
                }

                if (!File.Exists(xmlPath))
                {
                    // 最后尝试在 BaseDirectory 找（兼容开发环境）
                    xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFileName);
                    if (!File.Exists(xmlPath))
                        return null;
                }

                XDocument doc = XDocument.Load(xmlPath);

                // 构造 member name（支持不带参数的简单情况）
                string memberName = $"M:{ns}.{cls}.{method}";

                var summaryElement = doc.Descendants("member")
                    .FirstOrDefault(m => m.Attribute("name")?.Value.StartsWith(memberName) == true);

                if (summaryElement?.Element("summary") is XElement summary)
                {
                    // 清理注释（去掉多余换行和空格）
                    return string.Join(" ", summary.Value
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        .Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] 读取 XML 注释失败 ({ns}.{cls}.{method}): {ex.Message}");
            }

            return null;
        }

        // 所有方法的元信息类
        public class DynamicEndpoint
        {
            public string Namespace { get; set; } = "";
            public string Class { get; set; } = "";
            public string Method { get; set; } = "";
            public string FullPath { get; set; } = "";
            public string Description { get; set; } = "";
            public DateTime RegisteredAt { get; set; }
        }
    }
}
