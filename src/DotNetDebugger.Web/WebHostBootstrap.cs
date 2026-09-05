using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetDebugger.Web;

/// <summary>
/// 宿主 → Web 装配入口：宿主调 <see cref="Configure"/> 传入共享 DebugSessionManager，再经 <see cref="Build"/>
/// 得 WebApplication 启动入口。Web 只依赖 Session 库类型，不反引宿主（spec §9.1 显式注入）。
/// 组件经 <see cref="Manager"/> 访问共享调试会话。
/// </summary>
public static class WebHostBootstrap
{
    private static DotNetDebugger.Session.DebugSessionManager? _manager;

    /// <summary>宿主注入的共享调试会话管理器（Configure 未调用前访问抛异常）。</summary>
    public static DotNetDebugger.Session.DebugSessionManager Manager => _manager
        ?? throw new InvalidOperationException("WebHostBootstrap.Configure 未调用（宿主需先注入 DebugSessionManager）");

    /// <summary>宿主装配时注入共享调试会话管理器。</summary>
    public static void Configure(DotNetDebugger.Session.DebugSessionManager manager) => _manager = manager;

    /// <summary>
    /// 构建并配置 WebApplication（Kestrel 监听 127.0.0.1:port）。
    /// 日志纪律：Web host 独立于 MCP builder，默认 Console 写 stdout 会撕坏 MCP 协议帧——
    /// 必须显式 ClearProviders + AddConsole(LogToStandardErrorThreshold)，日志全走 stderr。
    /// </summary>
    public static WebApplication Build(int port, string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        // 从 build output（dotnet build 产物，非 publish）运行时服务 RCL/框架静态资产：
        // 官方文档要求 UseStaticWebAssets 把 SWA 虚拟文件系统挂到 webroot（否则 RCL _content 资产 dev 不可用）
        builder.WebHost.UseStaticWebAssets();

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddBootstrapBlazor();

        var app = builder.Build();
        app.UseAntiforgery();
        // Monaco 资产经 StaticWebAssetEndpointExclusionPattern 排除出 MapStaticAssets（免二次指纹冲突），
        // 由 UseStaticFiles 服务原文件（dev 走 UseStaticWebAssets 虚拟 / publish 走物理 wwwroot）。
        // MapStaticAssets 服务框架脚本(blazor.web.js)/BB/其余资产——不可省（.NET 10 框架脚本依赖它）。
        app.UseStaticFiles();
        app.MapStaticAssets();
        // App 组件在 Web 库程序集（MapRazorComponents 已隐式包含该程序集）——勿 AddAdditionalAssemblies 重复加同程序集（Assembly already defined）
        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();
        return app;
    }
}
