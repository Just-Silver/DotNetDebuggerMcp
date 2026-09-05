using DotNetDebugger.Web.Services;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetDebugger.Web;

/// <summary>
/// 宿主 → Web 装配入口：宿主调 <see cref="Configure"/> 传入共享 DebugSessionManager 与 AgentViewContext，再经 <see cref="Build"/>
/// 得 WebApplication 启动入口。Web 只依赖 Session 库类型与自身 Services，不反引宿主（spec §9.1 显式注入）。
/// 组件经 <see cref="Manager"/> 访问共享调试会话、经 <see cref="AgentView"/> 订阅 agent 当前查看上下文。
/// </summary>
public static class WebHostBootstrap
{
    private static DotNetDebugger.Session.DebugSessionManager? _manager;
    private static AgentViewContext? _agentView;

    // 幂等启动状态（进程生命周期对齐：进程退出 Web host 随之消亡，无失效逻辑）。
    // _url 先于 _app 写入：快路径读到 _app 非 null 时 _url 必已就绪。
    private static readonly System.Threading.SemaphoreSlim _startGate = new(1, 1);
    private static WebApplication? _app;
    private static string? _url;

    /// <summary>首选端口（宿主解析 --web-port 显式指定时写入；0 = 自动选空闲端口，默认值）。</summary>
    public static int PreferredPort { get; set; }

    /// <summary>Web host 是否已启动（幂等启动的单例状态标记）。</summary>
    public static bool IsStarted => _app is not null;

    /// <summary>已启动的 WebApplication（未启动为 null；宿主 --web 分支据此等待停机）。</summary>
    public static WebApplication? CurrentApp => _app;

    /// <summary>实际监听地址（未启动为 null；port=0 自动选端口时以 StartAsync 后实测为准）。</summary>
    public static string? CurrentUrl => _url;

    /// <summary>宿主注入的共享调试会话管理器（Configure 未调用前访问抛异常）。</summary>
    public static DotNetDebugger.Session.DebugSessionManager Manager => _manager
        ?? throw new InvalidOperationException("WebHostBootstrap.Configure 未调用（宿主需先注入 DebugSessionManager）");

    /// <summary>宿主注入的「agent 当前查看上下文」共享状态（Configure 未调用前访问抛异常）。</summary>
    public static AgentViewContext AgentView => _agentView
        ?? throw new InvalidOperationException("WebHostBootstrap.Configure 未调用（宿主需先注入 AgentViewContext）");

    /// <summary>宿主装配时注入共享调试会话管理器与 agent 视图上下文。</summary>
    public static void Configure(DotNetDebugger.Session.DebugSessionManager manager, AgentViewContext agentView)
    {
        _manager = manager;
        _agentView = agentView;
    }

    /// <summary>
    /// 构建并配置 WebApplication。port=0 时 Kestrel 自动选空闲端口（防端口占用启动失败），
    /// 实际地址经 <see cref="RunWithBrowserAsync"/> 在 StartAsync 后读取。
    /// 日志纪律：Web host 独立于 MCP builder，默认 Console 写 stdout 会撕坏 MCP 协议帧——
    /// 必须显式 ClearProviders + AddConsole(LogToStandardErrorThreshold)，日志全走 stderr。
    /// </summary>
    public static WebApplication Build(int port, string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        // port 0 = 自动选空闲端口（Kestrel 官方机制，无占用冲突）；>0 = 显式指定
        builder.WebHost.UseUrls(port > 0 ? $"http://127.0.0.1:{port}" : "http://127.0.0.1:0");
        // 从 build output（dotnet build 产物，非 publish）运行时服务 RCL/框架静态资产：
        // 官方文档要求 UseStaticWebAssets 把 SWA 虚拟文件系统挂到 webroot（否则 RCL _content 资产 dev 不可用）
        builder.WebHost.UseStaticWebAssets();

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddBootstrapBlazor();
        // 反编译文档缓存单例：跨电路（浏览器刷新/重连）存活，页面初始化据此恢复代码视图
        builder.Services.AddSingleton<DocumentStore>();

        var app = builder.Build();
        app.UseAntiforgery();
        // Monaco 资产经 StaticWebAssetEndpointExclusionPattern 排除出 MapStaticAssets（免二次指纹冲突），
        // 由 UseStaticFiles 服务原文件（dev 走 UseStaticWebAssets 虚拟 / publish 走物理 wwwroot）。
        // MapStaticAssets 服务框架脚本(blazor.web.js)/BB/其余资产——不可省（.NET 10 框架脚本依赖它）。
        // no-cache：本地工具场景，静态文件改后浏览器必须重验证（防同端口重启后用到旧桥 JS——实测反复踩坑）
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
        });
        app.MapStaticAssets();
        // App 组件在 Web 库程序集（MapRazorComponents 已隐式包含该程序集）——勿 AddAdditionalAssemblies 重复加同程序集（Assembly already defined）
        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();
        return app;
    }

    /// <summary>
    /// 幂等启动 Web host：只 Build/Start 一次并拉起默认浏览器（直达 /debugger），重复调用直接返回已有地址（不再起第二个
    /// Kestrel、不重复开浏览器）——<c>--web</c> 与 web_open 工具经此收敛为同一启动入口。单例状态与进程生命周期对齐。
    /// 并发安全：SemaphoreSlim 守卫 + 双重检查，多路并发同时调用仅首个真正启动，其余等待后取得同一地址。
    /// port=0（默认）取 <see cref="PreferredPort"/>（默认亦 0）→ Kestrel 自动选空闲端口。启动失败不写状态（下次可重试），
    /// 异常由调用方转中文提示。调用前须已 <see cref="Configure"/>（未调用抛异常）。
    /// </summary>
    public static async Task<string> EnsureStartedAsync(int port = 0)
    {
        if (_app is not null) return _url!;
        await _startGate.WaitAsync();
        try
        {
            if (_app is not null) return _url!;
            if (_manager is null || _agentView is null)
                throw new InvalidOperationException("WebHostBootstrap.Configure 未调用（启动 Web 前宿主/工具需先注入共享状态）");
            var app = Build(port > 0 ? port : PreferredPort, Array.Empty<string>());
            await app.StartAsync();
            var url = GetActualAddress(app);
            TryOpenBrowser($"{url}/debugger");
            _url = url;
            _app = app;
            return url;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>读 Kestrel 实际监听地址（StartAsync 后有效；port=0 自动选时 Addresses 已含真实端口）。</summary>
    public static string GetActualAddress(WebApplication app)
    {
        try
        {
            var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var feature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
            return feature?.Addresses.FirstOrDefault() ?? "http://127.0.0.1:0";
        }
        catch
        {
            return "http://127.0.0.1:0";
        }
    }

    /// <summary>经系统 shell 打开默认浏览器（Windows .NET Core 标准做法：UseShellExecute）。失败静默。</summary>
    public static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 无默认浏览器/无头环境：静默不打扰
        }
    }
}
