using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session.Models;

namespace DotNetDebugger.Session;

/// <summary>
/// 消费 DebugSession 的 DebugEvent 流，维护线程安全的最新状态快照（供 debug_state/debug_stack 等查询工具读取，
/// 不等停）。后台任务从 session.Events 读取，更新 CurrentState/LastStop。
/// </summary>
public sealed class SessionEventBuffer : IAsyncDisposable
{
    private readonly object _gate = new();
    private DebugSessionState _state = DebugSessionState.None;
    private StopContext? _lastStop;
    private CancellationTokenSource _cts = new();
    private Task? _consumer;
    private int _disposed;

    public DebugSessionState CurrentState { get { lock (_gate) return _state; } }
    public StopContext? LastStop { get { lock (_gate) return _lastStop; } }
    public int StoppedThreadId => LastStop?.ThreadId ?? -1;

    /// <summary>开始消费事件流（Session 创建后立即调用，避免错过停点）。</summary>
    public void Start(DebugSession session)
    {
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in session.Events.WithCancellation(_cts.Token))
                {
                    OnEvent(e);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* 会话关闭后事件流结束 */ }
        });
    }

    private void OnEvent(DebugEvent e)
    {
        switch (e.Kind)
        {
            case DebugEventKind.SessionStateChanged:
                if (e.Payload is SessionStateChangedPayload sp)
                    lock (_gate) _state = sp.State;
                break;
            case DebugEventKind.BreakpointHit:
                if (e.Payload is BreakpointHitPayload bp)
                {
                    lock (_gate)
                    {
                        _state = DebugSessionState.Stopped;
                        _lastStop = new StopContext(e.UtcTimestamp, e.Kind, bp.ThreadId, bp.TopFrame,
                            $"breakpoint {bp.BreakpointId}", bp.BreakpointId);
                    }
                }
                break;
            case DebugEventKind.StepCompleted:
                if (e.Payload is StepCompletedPayload sc)
                {
                    lock (_gate)
                    {
                        _state = DebugSessionState.Stopped;
                        _lastStop = new StopContext(e.UtcTimestamp, e.Kind, sc.ThreadId, sc.TopFrame, sc.Reason);
                    }
                }
                break;
            case DebugEventKind.ExceptionHit:
                if (e.Payload is ExceptionHitPayload ex)
                {
                    lock (_gate)
                    {
                        _state = DebugSessionState.Stopped;
                        _lastStop = new StopContext(e.UtcTimestamp, e.Kind, ex.ThreadId, ex.TopFrame,
                            $"exception {ex.ExceptionType}");
                    }
                }
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        if (_consumer is not null)
        {
            try { await _consumer; } catch { }
        }
        _cts.Dispose();
    }
}
