using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Web;

/// <summary>
/// Web 展示面工具：agent 按需打开 Web 调试监视器（Blazor 展示面，浏览器实时观看断点/单步/变量与 agent 操作时间线）。
/// 启动收敛到 WebHostBootstrap 幂等入口：进程内只起一个 Kestrel，与宿主 --web 分支共享同一单例状态。
/// </summary>
[McpServerToolType]
public static class WebOpenTool
{
    /// <summary>
    /// 打开 Web 调试监视器。幂等：已启动时直接返回现有地址（不重复起 Kestrel、不重复拉浏览器）；
    /// 首次启动成功后自动尝试拉起默认浏览器（失败静默，不影响返回地址）。
    /// </summary>
    /// <param name="port">监听端口；缺省 0 = 自动选空闲端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>监视器地址文本或错误提示。</returns>
    [McpServerTool]
    [Description("打开 Web 调试监视器（浏览器实时观看调试现场：断点/单步/变量/agent 操作时间线）。幂等：已启动时直接返回现有地址，不重复启动；首次启动会自动拉起默认浏览器。")]
    public static async Task<string> WebOpen(
        [Description("Web 监视器监听端口；缺省 0 = 自动选空闲端口（命令行 --web-port 显式指定过则优先用后者）。")] int port = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var alreadyUp = DotNetDebugger.Web.WebHostBootstrap.IsStarted;
            // 注入共享状态（幂等字段赋值；宿主 --web 分支若已注入则为重复赋同值）
            DotNetDebugger.Web.WebHostBootstrap.Configure(DebugSessionService.Manager, AgentViewService.Context);
            var url = await DotNetDebugger.Web.WebHostBootstrap.EnsureStartedAsync(port);
            var result = alreadyUp
                ? $"Web 监视器已在运行：{url} （幂等命中，未重复启动）。浏览器访问 {url}/debugger 查看。"
                : $"Web 监视器已启动：{url} （已尝试拉起默认浏览器；也可手动访问 {url}/debugger）。";
            DebugSessionService.Manager.Actions.Log("web_open", $"port={port}", alreadyUp ? "幂等命中（已在运行）" : "已启动");
            return result;
        }
        catch (Exception ex)
        {
            return $"打开 Web 监视器失败：{ex.Message}";
        }
    }
}
