using Owin;
using System.Web.Http;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;

namespace MyAPIFramework
{
    public class Startup
    {
        // OWIN 启动入口，必须命名为 Configuration
        public void Configuration(IAppBuilder app)
        {
            // 1. 创建 WebAPI 配置
            HttpConfiguration config = new HttpConfiguration();

            // 2. 注册 WebAPI 路由（核心必须保留）
            WebApiConfig.Register(config);

            // 3. 注册全局过滤器（如需要异常/授权过滤器）
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);

            // 4. 将 WebAPI 接入 OWIN 管道（核心必须保留）
            app.UseWebApi(config);

            // 6. 你项目自带的程序集预加载（保留）
            PreloadAssembliesOnStartup();
        }
        private void PreloadAssembliesOnStartup()
        {
            var scanAssemblyNames = new List<string> { "MyAPIFramework", "TestModule" };
            foreach (var assemblyName in scanAssemblyNames)
            {
                try
                {
                    bool isLoaded = AppDomain.CurrentDomain.GetAssemblies()
                        .Any(a => a.GetName().Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

                    if (!isLoaded)
                    {
                        string binPath = AppDomain.CurrentDomain.BaseDirectory;

                        // 优先加载 exe（你现在是控制台程序）
                        string assemblyPath = Path.Combine(binPath, $"{assemblyName}.exe");
                        if (!File.Exists(assemblyPath))
                        {
                            // 不存在 exe 再尝试加载 dll
                            assemblyPath = Path.Combine(binPath, $"{assemblyName}.dll");
                        }

                        if (File.Exists(assemblyPath))
                        {
                            Assembly.LoadFrom(assemblyPath);
                            System.Diagnostics.Trace.WriteLine($"已预加载程序集：{assemblyPath}");
                        }
                        else
                        {
                            System.Diagnostics.Trace.WriteLine($"未找到程序集文件：{assemblyPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"预加载 {assemblyName} 失败：{ex.Message}");
                }
            }
        }
    }
}