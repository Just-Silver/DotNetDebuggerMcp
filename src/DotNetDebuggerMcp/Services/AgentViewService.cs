using DotNetDebugger.Web.Services;

namespace DotNetDebuggerMcp.Services;

/// <summary>
/// 宿主侧「agent 当前查看上下文」共享服务（AgentViewContext 单例）。反编译/调试工具执行时经它写入
/// agent 正在看的程序集/类型/成员，Web 启动时经 WebHostBootstrap.Configure 注入同一实例供页面订阅。
/// 非 --web 模式无订阅者，写入仅推进 Revision，成本可忽略（hook 点统一无分支）。
/// </summary>
internal static class AgentViewService
{
    /// <summary>全局 agent 视图上下文（与 MCP 工具执行同源，Web 监视器消费）。</summary>
    public static AgentViewContext Context { get; } = new();
}
