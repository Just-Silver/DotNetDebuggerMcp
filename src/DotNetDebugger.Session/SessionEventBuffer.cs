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

    /// <summary>当前断点快照（引擎事件驱动更新；无会话/未变更过为空）。</summary>
    public IReadOnlyList<BreakpointSnapshot> CurrentBreakpoints { get { lock (_gate) return _breakpoints; } }

    /// <summary>会话快照变化事件（状态或停点变化后触发；订阅方自行切线程）。UI 推送通道，替代轮询。</summary>
    public event Action<DebugSessionState, StopContext?>? SnapshotChanged;

    /// <summary>断点集合变化事件（快照全量；引擎 BreakpointsChanged 事件驱动，替代轮询）。</summary>
    public event Action<IReadOnlyList<BreakpointSnapshot>>? BreakpointsChanged;

    private IReadOnlyList<BreakpointSnapshot> _breakpoints = [];

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
        DebugSessionState prevState;
        StopContext? prevStop;
        lock (_gate) { prevState = _state; prevStop = _lastStop; }

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
            case DebugEventKind.BreakpointsChanged:
                if (e.Payload is BreakpointsChangedPayload bpc)
                {
                    lock (_gate) _breakpoints = bpc.Breakpoints;
                    BreakpointsChanged?.Invoke(bpc.Breakpoints);
                }
                break;
        }

        DebugSessionState nextState;
        StopContext? nextStop;
        lock (_gate) { nextState = _state; nextStop = _lastStop; }
        if (nextState != prevState || !ReferenceEquals(nextStop, prevStop))
            SnapshotChanged?.Invoke(nextState, nextStop);
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
