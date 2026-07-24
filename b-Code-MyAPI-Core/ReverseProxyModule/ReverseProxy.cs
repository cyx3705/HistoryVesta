using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace ReverseProxyModule
{
    public static class ReverseProxy
    {
        //反向代理配置方法，专门用于将AST服务集成到主API中
        public static IServiceCollection AddAstReverseProxy(
            this IServiceCollection services,
            int astPort)
        {
            services.AddReverseProxy()
                .LoadFromMemory(
                    new RouteConfig[]
                    {
                        new RouteConfig
                        {
                            RouteId = "ast-route",
                            ClusterId = "ast-cluster",
                            Match = new RouteMatch { Path = "/ast/{**catch-all}" },
                            Transforms = new IReadOnlyDictionary<string, string>[]
                            {
                                new Dictionary<string, string>
                                {
                                    ["PathRemovePrefix"] = "/ast"
                                }
                            }
                        }
                    },
                    new ClusterConfig[]
                    {
                        new ClusterConfig
                        {
                            ClusterId = "ast-cluster",
                            Destinations = new Dictionary<string, DestinationConfig>
                            {
                                ["ast-server"] = new DestinationConfig
                                {
                                    Address = $"http://localhost:{astPort}"
                                }
                            }
                        }
                    });
            return services;
        }
    }
}
