//BaseVariable/BSV.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;

namespace BaseVariable
{
    public static class BSV
    {
        public static HttpClient httpClient;
        public static readonly string assemblyName = "MyAPI";
        public static readonly int mstPort = 5100;
        public static readonly int astPort = 5101;
        public static readonly string startPage = "";
        public static readonly string pushDir = @"C:\MyAPI-Push";

        //提供静态方法统一管理HttpClient的销毁（仅在程序退出时调用）
        static BSV()
        {
            httpClient = new HttpClient();
        }
        //全局HttpClient销毁方法，确保资源释放（在程序退出时调用）
        public static void DisposeGlobalHttpClient()
        {
            httpClient?.Dispose();
        }
    }
}
