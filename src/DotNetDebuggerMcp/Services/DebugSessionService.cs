using DotNetDebugger.Session;

namespace DotNetDebuggerMcp.Services;

/// <summary>宿主侧共享调试会话服务（Session 库 DebugSessionManager 单例）。工具经它路由所有调试操作。</summary>
internal static class DebugSessionService
{
    /// <summary>全局调试会话管理器（v1 单活动会话；agent 经 debug_* 工具操作）。</summary>
    public static DebugSessionManager Manager { get; } = new();

    /// <summary>停点上下文反编译文档缓存（P4；按 模块路径+类型 缓存，debug_wait/debug_state 共享）。</summary>
    public static DebugDocumentCache Documents { get; } = new();
}
