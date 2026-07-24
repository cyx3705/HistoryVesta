using Microsoft.AspNetCore.Mvc;
using BaseRegister;

namespace Myapi.Controller;

[ApiController]
[Route("api/meta")]
public class MetaController : ControllerBase
{
    /// <summary>
    /// 获取所有动态注册的接口（前端菜单、API文档、权限系统推荐使用）
    /// </summary>
    [HttpGet("endpoints")]
    public IActionResult GetEndpoints()
    {
        var list = DynamicEndpointRegistry.AllEndpoints
            .OrderBy(e => e.Namespace)
            .ThenBy(e => e.Class)
            .ThenBy(e => e.Method)
            .ToList();

        return Ok(new
        {
            success = true,
            total = list.Count,
            data = list
        });
    }

    /// <summary>
    /// 按模块分组返回（前端更友好）
    /// </summary>
    [HttpGet("endpoints/grouped")]
    public IActionResult GetGroupedEndpoints()
    {
        var grouped = DynamicEndpointRegistry.AllEndpoints
            .GroupBy(e => $"{e.Namespace}.{e.Class}")
            .Select(g => new
            {
                GroupName = g.Key,
                Namespace = g.First().Namespace,
                Class = g.First().Class,
                Count = g.Count(),
                Endpoints = g.OrderBy(e => e.Method).ToList()
            })
            .OrderBy(g => g.GroupName)
            .ToList();

        return Ok(new { success = true, data = grouped });
    }
}
