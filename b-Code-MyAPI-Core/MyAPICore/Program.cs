using BaseRegister;
using BaseVariable;
using CmdModule;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using MudBlazor.Services;
using ReverseProxyModule;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
Console.WriteLine($"Environment: {builder.Environment.EnvironmentName}");
// 固定端口
string finalApiUrl = $"http://0.0.0.0:{BSV.mstPort}";
builder.WebHost.UseUrls(finalApiUrl);
StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

// 服务注册
builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();   // 关键
builder.Services.AddScoped<CmdMain>();
builder.Services.AddMudServices();   // MudBlazor

// 模块加载
await InternalRegistration.OpenModuleAsync();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

//暂时先使用直接调用的方式注册 AST 反向代理服务，后续可以改为更灵活的配置方式
builder.Services.AddAstReverseProxy(BSV.astPort);

var app = builder.Build();

// 中间件
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapReverseProxy();
app.MapControllers();

//新增RuntimeRegistrationService 中间件
app.UseEndpoints(endpoints =>
{
    // 把 endpoints 传递给 RuntimeRegistrationService
});

// 新式映射（替换原来的 MapRazorComponents）
app.MapRazorComponents<MyAPICore.Components.App>()
   .AddInteractiveServerRenderMode();


try
{
    using var scope = app.Services.CreateScope();                    // ← 必须加这行
    //把 RunAsync 放到后台运行
    var runTask = app.RunAsync();
    // 等待服务真正启动（Kestrel 开始监听端口）
    Console.WriteLine("等待服务启动中...");
    await Task.Delay(2000);   // 先给 2 秒，如果还不行可以改成 3000
    // 执行模块初始化地址自调用
    Console.WriteLine("=== 开始执行模块初始化地址调用 ===");
    await Initialize.InitializeAllModulesAsync();
    Console.WriteLine("=== 所有初始化完成，服务正常运行 ===");
    // 最后等待主运行任务完成（程序退出时才会结束）
    await runTask;

}
catch (Exception ex)
{
    Console.WriteLine($"启动异常：{ex.Message}");
}


