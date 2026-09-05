using System.Threading.Channels;
using ClrDebug;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Stepping;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// 引擎核心：专用 MTA 线程 + 引导 + CorDebugManagedCallback 接线 + 命令泵 + DebugEvent 发布。
/// 引导与全部 ICorDebug 调用都发生在这条 MTA 线程（spec §5）；回调线程把停点/退出等状态变化发布为
/// DebugEvent；停点后进程保持停止，由外部命令（Continue/单步/读栈）经命令泵恢复。
/// </summary>
public sealed class DebugEngineCore : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<DebugEvent> _outbound = Channel.CreateUnbounded<DebugEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private Action<DebugEvent>? _sink;

    private Thread? _thread;
    private Exception? _startupError;
    private volatile bool _started;
    private volatile bool _disposed;

    // 调试对象（仅在 MTA 线程创建/使用；_process 跨回调线程写/命令泵线程读，volatile）
    private CorDebug? _corDebug;
    private volatile CorDebugProcess? _process;
    private DbgShim? _dbgshim;
    private CorDebugManagedCallback? _callback;
    private CallbackHandler? _handler;
    private readonly BreakpointManager _breakpoints = new();

    // 停点状态：最近停住的线程（断点/步/异常命中时由回调记录）
    private volatile int _stoppedThreadId = -1;

    private readonly Channel<(Func<Task> Body, TaskCompletionSource Completion)> _commandChannel
        = Channel.CreateUnbounded<(Func<Task>, TaskCompletionSource)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private long _seq;

    // ---- 启动/附加 ----

    /// <summary>启动新进程并附加。在专用 MTA 线程执行引导，完成后该线程转为命令泵。</summary>
    public Task LaunchAsync(string commandLine, int timeoutMs, string? workingDirectory, CancellationToken ct = default)
        => StartAsync(() => DoBootstrap(b => CorDebugBootstrap.Launch(b, commandLine, _callback!, timeoutMs, workingDirectory), DebugSessionState.Launching, ct), ct);

    /// <summary>附加到已运行进程。</summary>
    public Task AttachAsync(int processId, CancellationToken ct = default)
        => StartAsync(() => DoBootstrap(b => CorDebugBootstrap.Attach(b, processId, _callback!), DebugSessionState.Attaching, ct), ct);

    private Task StartAsync(Action work, CancellationToken ct)
    {
        EnsureNotStarted();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _thread = new Thread(() =>
        {
            try
            {
                work();
                _started = true;
                started.TrySetResult();
                RunCommandPump(); // 引导成功后本线程转命令泵，直到 dispose
            }
            catch (Exception ex)
            {
                _startupError = ex;
                started.TrySetException(ex);
            }
        });
        _thread.IsBackground = true;
        _thread.SetApartmentState(ApartmentState.MTA); // ClrDebug 硬性要求 MTA
        _thread.Name = "DebugEngineMTA";
        _thread.Start();

        return started.Task.WaitAsync(ct);
    }

    /// <summary>内部：CreateProcess 回调时记录进程对象（供 Continue/读线程用）。</summary>
    internal void SetProcess(CorDebugProcess process) => _process = process;

    private void DoBootstrap(Func<DbgShim, BootstrapResult> bootstrap, DebugSessionState state, CancellationToken ct)
    {
        _dbgshim = DbgShimLoader.Load(targetRuntimeDir: null);
        _callback = new CorDebugManagedCallback();
        _handler = new CallbackHandler(this, _callback, _breakpoints);
        var result = bootstrap(_dbgshim);
        _corDebug = result.CorDebug;
        PublishState(state, state == DebugSessionState.Launching ? "launched" : "attached");
    }

    // ---- 命令泵 ----

    private void RunCommandPump()
    {
        try
        {
            while (!_disposed)
            {
                if (!_commandChannel.Reader.TryRead(out var cmd))
                {
                    // 无命令时等待：用阻塞读（Channel 无阻塞 API，用 Task 轮询 + 短睡；或换 BlockingCollection）
                    Thread.Sleep(10);
                    continue;
                }
                try { cmd.Item1().GetAwaiter().GetResult(); cmd.Item2.TrySetResult(); }
                catch (Exception ex) { cmd.Item2.TrySetException(ex); }
            }
        }
        catch { /* dispose 时退出 */ }
    }

    private Task PostAsync(Func<Task> body, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(DebugEngineCore)));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commandChannel.Writer.TryWrite((body, completion));
        return completion.Task.WaitAsync(ct);
    }

    // ---- 公开命令（DebugSession 调） ----

    /// <summary>继续执行（进程停在断点/步/异常后调用）。</summary>
    public Task ContinueAsync(CancellationToken ct = default)
        => PostAsync(() =>
        {
            _handler?.ReleaseHold(); // 清停点挂起，允许 OnAnyEvent 后续 Continue
            SafeContinue(_process);
            _stoppedThreadId = -1;
            PublishState(DebugSessionState.Running, "continued");
            return Task.CompletedTask;
        }, ct);

    /// <summary>
    /// 安全 Continue：容忍 CORDBG_E_SUPERFLOUS_CONTINUE（注意微软拼写 SUPERFLOUS 少一 U；进程已在运行态时
    /// 再 Continue 会报此错，视为已运行即可）。其它异常向上抛。
    /// </summary>
    internal static void SafeContinue(CorDebugController? controller)
    {
        if (controller is null) return;
        try { controller.Continue(false); }
        catch (Exception ex) when (ex.Message.Contains("SUPERFLOUS_CONTINUE", StringComparison.OrdinalIgnoreCase))
        {
            // 进程已在运行：继续命令多余但无害
        }
    }

    /// <summary>断开调试（detach）。</summary>
    public Task DisconnectAsync(CancellationToken ct = default)
        => PostAsync(() =>
        {
            try { _process?.Detach(); } catch { /* 已退出则忽略 */ }
            _stoppedThreadId = -1;
            PublishState(DebugSessionState.Detached, "detached");
            return Task.CompletedTask;
        }, ct);

    /// <summary>设置断点（模块须已加载）。</summary>
    public Task<DebugBreakpoint> SetBreakpointAsync(string moduleName, int methodToken, int ilOffset, CancellationToken ct = default)
        => PostAsyncResult(() => _breakpoints.Add(moduleName, methodToken, ilOffset), ct);

    public Task<bool> RemoveBreakpointAsync(int id, CancellationToken ct = default)
        => PostAsyncResult(() => _breakpoints.Remove(id), ct);

    public Task ClearBreakpointsAsync(CancellationToken ct = default)
        => PostAsync(() => { _breakpoints.Clear(); return Task.CompletedTask; }, ct);

    /// <summary>设置 first-chance 异常过滤器（null=全部放行）。</summary>
    public Task SetExceptionFilterAsync(ExceptionBreakpointFilter? filter, CancellationToken ct = default)
        => PostAsync(() => { _handler?.SetExceptionFilter(filter); return Task.CompletedTask; }, ct);

    /// <summary>单步：stepIn=true into / false over / null = out。</summary>
    public Task StepAsync(bool? stepIn, CancellationToken ct = default)
        => PostAsync(() =>
        {
            var thread = GetStoppedThread()
                ?? throw new InvalidOperationException("无停住的线程（先让进程停在断点/异常/步完成再单步）");
            // 清停点挂起 + 停住线程标记：步进命令本身要恢复执行
            _handler?.ReleaseHold();
            _stoppedThreadId = -1;
            if (stepIn is bool b) { thread.CreateStepper().Step(b); }
            else { thread.CreateStepper().StepOut(); }
            SafeContinue(_process);
            PublishState(DebugSessionState.Running, stepIn is null ? "step out" : stepIn.Value ? "step into" : "step over");
            return Task.CompletedTask;
        }, ct);

    // ---- 状态读取（停顿时） ----

    /// <summary>线程列表。</summary>
    public Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
        => PostAsyncResult(() =>
        {
            var list = new List<DebugThreadInfo>();
            if (_process is not null)
            {
                foreach (var t in _process.Threads)
                {
                    list.Add(new DebugThreadInfo(t.Id, t.Id, null, 0));
                }
            }
            return (IReadOnlyList<DebugThreadInfo>)list;
        }, ct);

    /// <summary>指定线程的调用栈。</summary>
    public Task<IReadOnlyList<DebugStackFrame>> GetStackFramesAsync(int threadId, CancellationToken ct = default)
        => PostAsyncResult(() =>
        {
            var frames = new List<DebugStackFrame>();
            if (_process is null) return (IReadOnlyList<DebugStackFrame>)frames;
            CorDebugThread? thread = null;
            foreach (var t in _process.Threads) { if (t.Id == threadId) { thread = t; break; } }
            if (thread is null) return (IReadOnlyList<DebugStackFrame>)frames;

            var walk = thread.CreateStackWalk();
            var idx = 0;
            while (true)
            {
                var hr = walk.TryGetFrame(out var frame);
                if (hr != HRESULT.S_OK) break;
                if (frame is CorDebugILFrame ilf)
                {
                    try
                    {
                        var moduleName = ilf.Function?.Module?.Name ?? "<unknown>";
                        var token = ilf.FunctionToken.Value;
                        var ip = ilf.IP.pnOffset;
                        frames.Add(new DebugStackFrame(new FrameLocation(moduleName, (int)token, ip), idx++)
                        {
                            TypeName = TryGetTypeName(ilf),
                            MethodName = TryGetMethodName(ilf),
                        });
                    }
                    catch { /* 帧读取失败跳过 */ }
                }
                if (walk.TryNext() != HRESULT.S_OK) break;
            }
            return (IReadOnlyList<DebugStackFrame>)frames;
        }, ct);

    // ---- 事件发布（CallbackHandler 调，回调线程） ----

    internal void PublishBreakpointHit(int bpId, int threadId, FrameLocation? top)
    {
        _stoppedThreadId = threadId;
        Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.BreakpointHit,
            new BreakpointHitPayload(bpId, threadId, top)));
    }

    internal void PublishStepCompleted(int threadId, FrameLocation? top, string reason)
    {
        _stoppedThreadId = threadId;
        Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.StepCompleted,
            new StepCompletedPayload(threadId, top, reason)));
    }

    internal void PublishExceptionHit(int threadId, string type, string? message, FrameLocation? top)
    {
        _stoppedThreadId = threadId;
        Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.ExceptionHit,
            new ExceptionHitPayload(threadId, type, message, top)));
    }

    internal void PublishState(DebugSessionState state, string? reason)
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.SessionStateChanged,
            new SessionStateChangedPayload(state, reason)));

    internal void Log(string level, string message)
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.EngineLog,
            new EngineLogPayload(level, message)));

    /// <summary>读线程栈顶 IL 帧位置（供断点/步/异常事件附 top frame）。回调线程调用。</summary>
    internal FrameLocation? ReadTopFrame(CorDebugThread thread)
    {
        try
        {
            if (thread.ActiveFrame is not CorDebugILFrame ilf) return null;
            var module = ilf.Function?.Module?.Name ?? "<unknown>";
            var token = ilf.FunctionToken.Value;
            return new FrameLocation(module, (int)token, ilf.IP.pnOffset);
        }
        catch { return null; }
    }

    private void Publish(DebugEvent e)
    {
        _outbound.Writer.TryWrite(e);
        lock (_gate) _sink?.Invoke(e);
    }

    /// <summary>DebugSession 构造后接入事件汇（sink 建立前的缓冲事件由本方法回放）。</summary>
    internal void AttachEventSink(Action<DebugEvent> sink)
    {
        lock (_gate) _sink = sink;
        while (_outbound.Reader.TryRead(out var e)) sink(e);
    }

    /// <summary>事件流（Channel 读端，供 IAsyncEnumerable 消费）。</summary>
    public ChannelReader<DebugEvent> Events => _outbound.Reader;

    private CorDebugThread? GetStoppedThread()
    {
        if (_process is null || _stoppedThreadId < 0) return null;
        foreach (var t in _process.Threads) if (t.Id == _stoppedThreadId) return t;
        return null;
    }

    private void EnsureNotStarted()
    {
        if (_started || _thread is not null)
            throw new InvalidOperationException("会话已在运行");
    }

    private Task<T> PostAsyncResult<T>(Func<T> body, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostAsync(() =>
        {
            try { completion.TrySetResult(body()); }
            catch (Exception ex) { completion.TrySetException(ex); }
            return Task.CompletedTask;
        }, ct);
        return completion.Task.WaitAsync(ct);
    }

    private static string? TryGetTypeName(CorDebugILFrame ilf)
    {
        try
        {
            var cls = ilf.Function?.Class;
            return cls is null ? null : $"0x{cls.Token.Value:x8}";
        }
        catch { return null; }
    }

    private static string? TryGetMethodName(CorDebugILFrame ilf)
    {
        try
        {
            var token = ilf.FunctionToken;
            return token.IsNil ? null : $"0x{token.Value:x8}";
        }
        catch { return null; }
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    /// <summary>释放：结束命令泵并清理调试对象。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process?.Detach(); } catch { }
        try { _corDebug?.Terminate(); } catch { }
        _thread?.Join(2000);
        _outbound.Writer.TryComplete();
        await Task.CompletedTask;
    }
}
