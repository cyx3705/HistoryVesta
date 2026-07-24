using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodePushModule
{
    public class CodePushResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 文件相对路径 → 文件字节内容
        /// </summary>
        public Dictionary<string, byte[]> Files { get; set; } = new();
    }
}
