using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Session.Models;

/// <summary>停点现场：进程停在何处/为何停（供 debug_state/debug_stack 等查询）。</summary>
public sealed record StopContext(
    DateTimeOffset UtcTimestamp,
    DebugEventKind Kind,          // BreakpointHit/StepCompleted/ExceptionHit
    int ThreadId,
    FrameLocation? TopFrame,
    string? Reason,               // 断点描述 / step reason / 异常类型
    int? BreakpointId = null,
    string? Message = null);      // 异常停点的异常 Message（其它停点为 null）

/// <summary>会话摘要（供 debug_state 返回与轨迹关联）。</summary>
public sealed record DebugSessionInfo(
    string SessionId,
    string TargetDescription,
    DebugSessionState State,
    DateTimeOffset StartedAt,
    StopContext? LastStop,
    int BreakpointCount);
