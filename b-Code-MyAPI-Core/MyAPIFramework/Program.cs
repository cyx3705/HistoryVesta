using Microsoft.Owin.Hosting;
using System;

namespace MyAPIFramework
{
    class Program
    {
        static void Main(string[] args)
        {
            // 自托管地址（可自定义）
            string baseUrl = "http://localhost:5101";

            // 启动 OWIN 宿主
            using (WebApp.Start<Startup>(url: baseUrl))
            {
                Console.WriteLine($"OWIN 自托管已启动：{baseUrl}");
                Console.WriteLine("按任意键停止...");
                Console.ReadKey();
            }
        }
    }
}