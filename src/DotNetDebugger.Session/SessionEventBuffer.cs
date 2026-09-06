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
    private int _skippedExceptionCount;
    private string? _lastSkippedExceptionType;
    private readonly List<TraceHitPayload> _traces = new();
    private int _tracesDropped;

    /// <summary>trace 轨迹环形上限（条）：超出丢最旧（进程内轨迹，防 token 失控）。</summary>
    public const int MaxTraces = 100;

    public DebugSessionState CurrentState { get { lock (_gate) return _state; } }
    public StopContext? LastStop { get { lock (_gate) return _lastStop; } }
    public int StoppedThreadId => LastStop?.ThreadId ?? -1;

    /// <summary>当前断点快照（引擎事件驱动更新；无会话/未变更过为空）。</summary>
    public IReadOnlyList<BreakpointSnapshot> CurrentBreakpoints { get { lock (_gate) return _breakpoints; } }

    /// <summary>
    /// 取走自上次消费以来被异常过滤器跳过的异常统计（次数 + 最近类型），并清零。
    /// 「期间」语义由消费方驱动：debug_wait/debug_state 每次调用时取走，向 agent 反馈
    /// 「过滤器在工作、不命中」——防「设错类型名导致断点永不命中」的静默空等。
    /// </summary>
    public (int Count, string? LastType) ConsumeSkippedExceptions()
    {
        lock (_gate)
        {
            var result = (_skippedExceptionCount, _lastSkippedExceptionType);
            _skippedExceptionCount = 0;
            _lastSkippedExceptionType = null;
            return result;
        }
    }

    /// <summary>当前未读取的 trace 轨迹条数（debug_breakpoint_list 展示用，不消费）。</summary>
    public int PendingTraceCount { get { lock (_gate) return _traces.Count; } }

    /// <summary>
    /// 取走全部 trace 轨迹（旧→新）并清空（消费式，防重复吐给 agent）。
    /// 环形丢弃的条数附在 <paramref name="dropped"/>（0=无丢弃）。
    /// </summary>
    public IReadOnlyList<TraceHitPayload> ConsumeTraces(out int dropped)
    {
        lock (_gate)
        {
            var result = _traces.ToList();
            dropped = _tracesDropped;
            _traces.Clear();
            _tracesDropped = 0;
            return result;
        }
    }

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

    /// <summary>
    /// 等待进程到达停点（Stopped）或退出（Exited）。已处终态立即返回当前停点快照；
    /// 否则订阅 SnapshotChanged 等待，超时/取消按「放弃等待」返回 null（调用方读 CurrentState 给提示）。
    /// </summary>
    public async Task<StopContext?> WaitForStopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSnapshot(DebugSessionState state, StopContext? _)
        {
            if (state is DebugSessionState.Stopped or DebugSessionState.Exited)
                tcs.TrySetResult();
        }
        SnapshotChanged += OnSnapshot;
        try
        {
            lock (_gate)
            {
                if (_state is DebugSessionState.Stopped or DebugSessionState.Exited)
                    return _lastStop;
            }
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout, linkedCts.Token)).ConfigureAwait(false);
            if (winner != tcs.Task) return null; // 超时或外部取消：放弃等待
            lock (_gate)
                return _state is DebugSessionState.Stopped or DebugSessionState.Exited ? _lastStop : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            SnapshotChanged -= OnSnapshot;
        }
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
                            $"exception {ex.ExceptionType}", Message: ex.Message);
                    }
                }
                break;
            case DebugEventKind.ExceptionSkipped:
                // 过滤器跳过的异常：不计状态/停点，只累计给 debug_wait/debug_state 的不命中反馈
                if (e.Payload is ExceptionSkippedPayload sk)
                {
                    lock (_gate)
                    {
                        _skippedExceptionCount++;
                        _lastSkippedExceptionType = sk.ExceptionType;
                    }
                }
                break;
            case DebugEventKind.TraceHit:
                // trace 断点命中：折叠进环形轨迹（不停进程），debug_wait/debug_state 批量消费
                if (e.Payload is TraceHitPayload th)
                {
                    lock (_gate)
                    {
                        if (_traces.Count >= MaxTraces)
                        {
                            _traces.RemoveAt(0);
                            _tracesDropped++;
                        }
                        _traces.Add(th);
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
