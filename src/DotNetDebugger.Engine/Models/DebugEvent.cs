namespace DotNetDebugger.Engine.Models;

/// <summary>调试事件种类（spec §4.2）。</summary>
public enum DebugEventKind
{
    SessionStateChanged,
    BreakpointHit,
    StepCompleted,
    ExceptionHit,
    ExceptionSkipped,
    ThreadsChanged,
    EngineLog,
    BreakpointsChanged,
}

/// <summary>
/// 统一调试事件（spec §4.2 契约）：SessionId/Sequence/UtcTimestamp/Kind/Payload。
/// </summary>
public sealed record DebugEvent(
    string SessionId,
    long Sequence,
    DateTimeOffset UtcTimestamp,
    DebugEventKind Kind,
    object? Payload = null);

/// <summary>会话状态（spec §4.1 状态机）。</summary>
public enum DebugSessionState
{
    None,
    Launching,
    Attaching,
    Running,
    Stopped,
    Exited,
    Detached,
}

/// <summary>会话状态变更事件载荷。</summary>
public sealed record SessionStateChangedPayload(DebugSessionState State, string? Reason);

/// <summary>断点命中事件载荷（含栈顶帧位置）。</summary>
public sealed record BreakpointHitPayload(int BreakpointId, int ThreadId, FrameLocation? TopFrame);

/// <summary>单步完成事件载荷。</summary>
public sealed record StepCompletedPayload(int ThreadId, FrameLocation? TopFrame, string Reason);

/// <summary>first-chance 异常命中事件载荷。</summary>
public sealed record ExceptionHitPayload(int ThreadId, string ExceptionType, string? Message, FrameLocation? TopFrame);

/// <summary>异常被过滤器跳过事件载荷（不停进程；Session 计数后供 debug_wait/debug_state 给不命中反馈）。</summary>
public sealed record ExceptionSkippedPayload(int ThreadId, string ExceptionType, string? Message);

/// <summary>线程列表变化事件载荷。</summary>
public sealed record ThreadsChangedPayload(IReadOnlyList<DebugThreadInfo> Threads);

/// <summary>引擎日志事件载荷。</summary>
public sealed record EngineLogPayload(string Level, string Message);

/// <summary>断点集合变更事件载荷（快照全量；设/删/清后在命令泵内发布，UI 推送替代轮询）。</summary>
public sealed record BreakpointsChangedPayload(IReadOnlyList<BreakpointSnapshot> Breakpoints);

/// <summary>断点快照（不含运行时绑定信息）。</summary>
public sealed record BreakpointSnapshot(int Id, string ModuleName, int MethodToken, int IlOffset);
