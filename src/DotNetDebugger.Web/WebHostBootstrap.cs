using Microsoft.AspNetCore.Builder;
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

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddBootstrapBlazor();

        var app = builder.Build();
        app.UseAntiforgery();
        // 宿主（Microsoft.NET.Sdk tool）不生成自身静态资产清单，但 RCL（Web 库）的清单随构建复制到 bin——
        // MapStaticAssets 默认找 {启动程序集}.staticwebassets.endpoints.json 会失败，显式指到 Web 库清单。
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "DotNetDebugger.Web.staticwebassets.endpoints.json");
        app.MapStaticAssets(File.Exists(manifestPath) ? manifestPath : null);
        // App 组件在 Web 库程序集（MapRazorComponents 已隐式包含该程序集）——勿 AddAdditionalAssemblies 重复加同程序集（Assembly already defined）
        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();
        return app;
    }
}
