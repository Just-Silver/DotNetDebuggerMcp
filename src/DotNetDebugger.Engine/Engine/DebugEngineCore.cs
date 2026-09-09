using System.Numerics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ClrDebug;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
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

    /// <summary>P7：条件求值器由 Session 经构造注入（null=会话不支持条件断点）。
    /// R6：源行断点解析器由 Session 经构造注入（null=不支持 sourcePath+line 延迟断点）。</summary>
    internal DebugEngineCore(IBreakpointConditionEvaluator? conditionEvaluator = null, ISourceLineBreakpointResolver? sourceLineResolver = null)
    {
        _conditionEvaluator = conditionEvaluator;
        _breakpoints = new BreakpointManager(sourceLineResolver);
    }

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
    private readonly BreakpointManager _breakpoints;

    // P7 条件断点：Session 注入的求值器（依赖倒置，见 IBreakpointConditionEvaluator；null=不支持条件断点）
    private readonly IBreakpointConditionEvaluator? _conditionEvaluator;

    /// <summary>停点状态：最近停住的线程（断点/步/异常命中时由回调记录）。</summary>
    private volatile int _stoppedThreadId = -1;

    private readonly Channel<(Func<Task> Body, TaskCompletionSource Completion)> _commandChannel
        = Channel.CreateUnbounded<(Func<Task>, TaskCompletionSource)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // 回调入队 Channel：回调线程只入队，命令泵线程消费处理（单线程模型，避免并发 Continue）
    private readonly Channel<CorDebugManagedCallbackEventArgs> _eventChannel
        = Channel.CreateUnbounded<CorDebugManagedCallbackEventArgs>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>回调事件 Channel（CallbackHandler 入队目标）。</summary>
    public ChannelWriter<CorDebugManagedCallbackEventArgs> EventChannelWriter => _eventChannel.Writer;

    /// <summary>回调事件读端（命令泵线程消费）。</summary>
    public ChannelReader<CorDebugManagedCallbackEventArgs> EventChannel => _eventChannel.Reader;

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
        // .NET Core 起新线程默认即 MTA（满足 ClrDebug 硬性要求；显式 SetApartmentState 仅 STA 需要且触发 CA1416）
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
        // 泵尚未启动：先同步处理已入队事件直到 _process 就绪（CreateProcess 回调）——最多等 3s
        var procDeadline = DateTime.UtcNow.AddSeconds(3);
        while (_process is null && DateTime.UtcNow < procDeadline)
        {
            if (_eventChannel.Reader.TryRead(out var evt)) _handler.HandleEvent(evt);
            else Thread.Sleep(10);
        }
        // attach 后枚举已加载模块登记（attach 已运行进程不补发 LoadModule——API 参考 §9.5）
        if (_process is not null) SyncLoadedModules();
        PublishState(state, state == DebugSessionState.Launching ? "launched" : "attached");
    }

    /// <summary>
    /// 枚举进程已加载模块登记到 BreakpointManager，返回本次重绑成功的断点数。
    /// 时机① attach 引导完成（attach 已运行进程不补发 LoadModule——API 参考 §9.5）；
    /// ② 首次 Continue 前（CI 实录：attach 窗口竞速——快照跑在入口模块加载完成之前时，
    /// 该模块既不在快照里也不会再发 LoadModule 回调，登记表永久缺失、pending 断点永不绑定；
    /// 进程仍冻结时重扫是完备快照，可在恢复运行前补绑断点）。
    /// </summary>
    private int SyncLoadedModules()
    {
        var rebound = 0;
        try
        {
            foreach (var ad in _process!.AppDomains)
            {
                foreach (var asm in ad.Assemblies)
                {
                    foreach (var mod in asm.Modules)
                    {
                        try { rebound += _breakpoints.TrackModule(mod); }
                        catch { /* 单个模块登记失败忽略 */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("warn", $"枚举已加载模块失败: {ex.Message}");
        }
        return rebound;
    }

    // ---- 命令泵（单线程：事件处理 + 命令执行，避免并发 Continue）----

    private void RunCommandPump()
    {
        try
        {
            while (!_disposed)
            {
                var didWork = false;
                // 1. 先处理已排队事件（停点事件在此停住进程并发布）
                while (_eventChannel.Reader.TryRead(out var evt))
                {
                    _handler?.HandleEvent(evt);
                    didWork = true;
                }
                // 2. 执行一个命令（Continue/断点/单步/读状态）
                if (_commandChannel.Reader.TryRead(out var cmd))
                {
                    try { cmd.Item1().GetAwaiter().GetResult(); cmd.Item2.TrySetResult(); }
                    catch (Exception ex) { cmd.Item2.TrySetException(ex); }
                    didWork = true;
                }
                if (!didWork) Thread.Sleep(5); // 都空闲：短暂等待
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

    /// <summary>
    /// 继续执行（进程停在断点/步/异常后调用）。
    /// ICorDebug stop-counter 语义：每次回调派发 +1、每次 Continue -1，且每次 Continue 只派发一个排队回调；
    /// 因此需循环 Continue 直到进程真正 running（IsRunning=true），否则停在断点旁的排队事件会让进程不恢复
    /// （官方文档 HasQueuedCallbacks/stop-counter）。上限防异常情况死循环。
    /// </summary>
    public Task ContinueAsync(CancellationToken ct = default)
        => PostAsync(() =>
        {
            _handler?.ReleaseInitialHold(); // 清停点挂起，允许 OnAnyEvent 对后续排队事件继续 Continue
            _stoppedThreadId = -1;
            // attach 窗口竞速自愈（见 SyncLoadedModules 注释）：有未绑定断点时，趁进程仍冻结补扫模块并补绑
            if (_breakpoints.Breakpoints.Any(b => !b.IsBound))
            {
                var rebound = SyncLoadedModules();
                if (rebound > 0) PublishBreakpointsChanged();
            }
            if (_process is not null)
            {
                const int maxContinues = 100;
                for (var i = 0; i < maxContinues; i++)
                {
                    var hr = _process.TryIsRunning(out var running);
                    if (hr == HRESULT.S_OK && running) break; // 进程已在运行
                    try { _process.Continue(false); }
                    catch (Exception ex) when (ex.Message.Contains("SUPERFLOUS_CONTINUE", StringComparison.OrdinalIgnoreCase))
                    {
                        break; // 已在运行
                    }
                }
            }
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

    /// <summary>设置断点（模块未加载时登记为 pending，LoadModule 自动重绑；方法 token 无效抛错）。
    /// P5：hitCount=第 N 次起生效（默认 1）；mode=Stop 命中停 / Trace 命中不停记轨迹。
    /// P7：condition=P6 表达式子集条件（非空时须已注入求值器；条件先于计数，false/求值失败放行）。</summary>
    public Task<DebugBreakpoint> SetBreakpointAsync(string moduleName, int methodToken, int ilOffset, int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop, string? condition = null, CancellationToken ct = default)
        => PostAsyncResult(() =>
        {
            if (!string.IsNullOrWhiteSpace(condition) && _conditionEvaluator is null)
                throw new InvalidOperationException("当前会话无条件求值器，条件断点不可用（会话创建须注入求值器）。");
            var bp = _breakpoints.Add(moduleName, methodToken, ilOffset, hitCount, mode, condition);
            PublishBreakpointsChanged();
            return bp;
        }, ct);

    /// <summary>
    /// 设置源行型断点（R6）：sourcePath+line 按 PDB 解析绑定。模块未加载/未命中时登记为 pending，
    /// 后续模块加载（TrackModule）自动解析补设。moduleName 非空=限该模块（未加载也登记 pending）；
    /// 空=任意模块（当前已加载模块立即尝试，未命中则等后续模块）。无源行解析器（会话创建未注入）抛中文提示。
    /// </summary>
    public Task<DebugBreakpoint> SetSourceLineBreakpointAsync(string sourcePath, int line, string moduleName = "", int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop, string? condition = null, CancellationToken ct = default)
        => PostAsyncResult(() =>
        {
            if (_breakpoints.HasNoSourceLineResolver)
                throw new InvalidOperationException("当前会话无源行解析器，sourcePath+line 断点不可用（会话创建须注入 ISourceLineBreakpointResolver）。");
            if (string.IsNullOrWhiteSpace(sourcePath) || line <= 0)
                throw new InvalidOperationException("源行断点须提供 sourcePath 与 line（1-based）。");
            var bp = _breakpoints.AddSourceLine(sourcePath.Trim(), line, moduleName, hitCount, mode, condition);
            // 模块已加载（attach 已运行进程 / launch 后模块已就绪）时立即尝试解析绑定
            TryBindSourceLineNow(bp);
            PublishBreakpointsChanged();
            return bp;
        }, ct);

    /// <summary>源行 pending 在已登记模块上立即尝试解析绑定（模块加载时 TrackModule 已自动做；此处兜 attach 后快照已登记模块）。命令泵内调用。</summary>
    private void TryBindSourceLineNow(DebugBreakpoint bp)
    {
        if (bp.IsBound || !bp.IsSourceLine || bp.MethodToken != 0) return;
        try
        {
            var modules = _breakpoints.GetModules();
            foreach (var (name, path) in modules)
            {
                if (!string.IsNullOrEmpty(bp.ModuleName) && !BreakpointManager.ModuleMatches(bp.ModuleName, name)
                    && !BreakpointManager.ModuleMatches(bp.ModuleName, path)) continue;
                if (_breakpoints.TryBindSourceLine(bp, path)) return; // 绑上即止
            }
        }
        catch { /* 解析失败保持 pending */ }
    }

    /// <summary>当前登记断点快照（经命令泵读，与增删互斥；Web 监视器红点渲染数据源）。</summary>
    public Task<IReadOnlyList<DebugBreakpoint>> GetBreakpointsAsync(CancellationToken ct = default)
        => PostAsyncResult(() => (IReadOnlyList<DebugBreakpoint>)_breakpoints.Breakpoints.ToList(), ct);

    /// <summary>模块短名/全路径 → 模块全路径（磁盘文件定位；未登记返回 null）。</summary>
    public Task<string?> GetModulePathAsync(string moduleName, CancellationToken ct = default)
        => PostAsyncResult(() => _breakpoints.GetModulePath(moduleName), ct);

    /// <summary>已加载模块快照（短名 → 磁盘路径；行断点跨模块解析用）。</summary>
    public Task<IReadOnlyList<(string Name, string Path)>> GetModulesAsync(CancellationToken ct = default)
        => PostAsyncResult(() => (IReadOnlyList<(string Name, string Path)>)_breakpoints.GetModules(), ct);

    public Task<bool> RemoveBreakpointAsync(int id, CancellationToken ct = default)
        => PostAsyncResult(() =>
        {
            var removed = _breakpoints.Remove(id);
            if (removed) PublishBreakpointsChanged();
            return removed;
        }, ct);

    public Task ClearBreakpointsAsync(CancellationToken ct = default)
        => PostAsync(() =>
        {
            _breakpoints.Clear();
            PublishBreakpointsChanged();
            return Task.CompletedTask;
        }, ct);

    /// <summary>设置 first-chance 异常过滤器（null=全部放行）。</summary>
    public Task SetExceptionFilterAsync(ExceptionBreakpointFilter? filter, CancellationToken ct = default)
        => PostAsync(() => { _handler?.SetExceptionFilter(filter); return Task.CompletedTask; }, ct);

    /// <summary>单步：stepIn=true into / false over / null = out。</summary>
    public Task StepAsync(bool? stepIn, CancellationToken ct = default)
        => PostAsync(() =>
        {
            var thread = GetStoppedThread()
                ?? throw new InvalidOperationException("无停住的线程（先让进程停在断点/异常/步完成再单步）");
            if (thread.ActiveFrame is not CorDebugILFrame ilf)
                throw new InvalidOperationException("当前帧非 IL 帧，无法单步");
            // 清停点挂起 + 停住线程标记：步进命令本身要恢复执行
            _handler?.ReleaseInitialHold();
            _stoppedThreadId = -1;

            // 帧级 stepper + 掩码 + 语句 IL 区间（PDB 序列点）——参考 sharpdbg。
            // 坑：线程级裸 CreateStepper().Step() 会立即完成、StepCompleted 落回同一 IP（实测原地 +0x0 不推进）。
            var stepper = ilf.CreateStepper();
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL
                & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            if (stepIn is null)
            {
                stepper.StepOut();
            }
            else
            {
                var range = TryGetStatementRange(ilf);
                if (range is { } r)
                    stepper.StepRange(stepIn.Value, new[] { new COR_DEBUG_STEP_RANGE { startOffset = r.Start, endOffset = r.End } }, 1);
                else
                {
                    // 无 PDB 回退：单条 IL 指令区间 [ip, ip+1)（dnSpy 无符号时同款）。
                    // 坑：裸 Step(bStepIn) 无序列点会在原地完成（实测 +0x0 不推进），必须用 StepRange。
                    var ip = ilf.IP.pnOffset;
                    stepper.StepRange(stepIn.Value, new[] { new COR_DEBUG_STEP_RANGE { startOffset = ip, endOffset = ip + 1 } }, 1);
                }
            }
            SafeContinue(_process);
            PublishState(DebugSessionState.Running, stepIn is null ? "step out" : stepIn.Value ? "step into" : "step over");
            return Task.CompletedTask;
        }, ct);

    /// <summary>当前 IP 所在语句的 IL 区间（模块旁 PDB 序列点；无 PDB/未命中返回 null → 回退裸 Step）。命令泵内调用。</summary>
    private (int Start, int End)? TryGetStatementRange(CorDebugILFrame ilf)
    {
        try
        {
            var modulePath = ilf.Function?.Module?.Name;
            var ilSize = ilf.Function?.ILCode?.Size;
            if (string.IsNullOrEmpty(modulePath) || ilSize is not int size || size <= 0) return null;
            return SymbolNameResolver.GetStatementIlRange(modulePath, (int)ilf.FunctionToken.Value, ilf.IP.pnOffset, size);
        }
        catch { return null; }
    }

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
                        var rawModule = ilf.Function?.Module?.Name ?? "<unknown>";
                        var moduleName = Path.GetFileName(rawModule); // CorDebugModule.Name 返回全路径，归一化为文件名
                        var token = ilf.FunctionToken.Value;
                        var ip = ilf.IP.pnOffset;
                        frames.Add(new DebugStackFrame(new FrameLocation(moduleName, (int)token, ip), idx++)
                        {
                            // rawModule 为 "<unknown>"（模块不可达）时 resolver 读文件失败返回 null → 展示端降级位置文本
                            TypeName = TryGetTypeName(ilf, rawModule),
                            MethodName = TryGetMethodName(ilf, rawModule),
                        });
                    }
                    catch { /* 帧读取失败跳过 */ }
                }
                if (walk.TryNext() != HRESULT.S_OK) break;
            }
            return (IReadOnlyList<DebugStackFrame>)frames;
        }, ct);

    /// <summary>
    /// 读取指定线程栈顶 IL 帧的局部变量与参数（停顿时调用）。v1：标量/字符串直接渲染；
    /// 对象降级为摘要；名字为空用 slotN。返回 { "exception": [...], "locals": [...], "arguments": [...] }——
    /// exception 节仅在当前线程有在抛异常（first-chance 停点）时存在，合成 $exception 伪变量。
    /// </summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>> GetVariablesAsync(int threadId, CancellationToken ct = default)
        => PostAsyncResult(() => (IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>)ReadVariablesForThread(threadId), ct);

    /// <summary>
    /// 按路径读值（P6 表达式读值子集的引擎底座，纯读、无 FuncEval）：rootName 为栈顶帧局部/参数名
    /// （+$exception 伪根，与 GetVariablesAsync 同一来源），segments 逐段字段/索引解引用——
    /// 引擎按段直读绕开 MaxChildren 截断（数组任意下标、深层链都可靠）。失败抛中文提示异常（附段号/类型/可用字段）。
    /// </summary>
    public Task<DebugEvalResult> EvaluatePathAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, CancellationToken ct = default)
        => PostAsyncResult(() => ReadPathValue(threadId, rootName, segments), ct);

    /// <summary>
    /// 按路径写值（W1 debug_set 引擎底座，停顿时有效，命令泵内同步执行）：与 EvaluatePathAsync 同款路径解析，
    /// 定位到末段值对象后按 DebugWriteValue 分派——Null=引用置空 / Scalar=值类型目标按目标元素类型转换写 /
    /// CopyPath=引用重定向到源路径对象。返回写前/写后回显；失败抛中文提示异常（含 readonly/类型/降级）。
    /// 写目标进程内存有崩目标风险（宿主工具面明示），只写读链路已证明可定位的目标。
    /// </summary>
    public Task<DebugWriteResult> SetPathValueAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, DebugWriteValue value, CancellationToken ct = default)
        => PostAsyncResult(() => WritePathValue(threadId, rootName, segments, value), ct);

    /// <summary>GetVariablesAsync 的同步实现（命令泵 MTA 线程内调用；P5 trace 快照路径复用）。</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>> ReadVariablesForThread(int threadId)
    {
        var result = new Dictionary<string, IReadOnlyList<DebugVariable>>();
        if (_process is null) return result;

        CorDebugThread? thread = null;
        foreach (var t in _process.Threads) { if (t.Id == threadId) { thread = t; break; } }
        if (thread is null) return result;

            // 异常停点：合成 $exception 首节（类型全名+Message 展示；children 走现有一级展开，读不出的成员诚实标注）
            var exceptionSection = TryReadExceptionVariable(thread);
            if (exceptionSection is not null)
                result["exception"] = exceptionSection;

            result["locals"] = new List<DebugVariable>();
            result["arguments"] = new List<DebugVariable>();

            if (thread.ActiveFrame is not CorDebugILFrame ilf) return result;

            // 符号名解析：参数名取 DLL 元数据 Param 表，局部名取模块旁 PDB（缺失则保持 slot 展示）
            string?[] argNames = [], localNames = [];
            var top = ReadTopFrame(thread);
            if (top is not null)
            {
                var modulePath = _breakpoints.GetModulePath(top.ModuleName);
                if (modulePath is not null)
                {
                    var names = SymbolNameResolver.Resolve(modulePath, top.MethodToken);
                    argNames = names.ArgNames;
                    localNames = names.LocalNames;
                }
            }

            try
            {
                var locals = new List<DebugVariable>();
                var localValues = ilf.LocalVariables;
                for (var i = 0; i < localValues.Length; i++)
                {
                    try
                    {
                        var name = i < localNames.Length ? localNames[i] : null;
                        locals.Add(new DebugVariable(name, i, ReadValue(localValues[i], expand: true), IsArgument: false));
                    }
                    catch { /* 单变量读取失败跳过 */ }
                }
                result["locals"] = locals;
            }
            catch { /* 局部变量读取失败 */ }

            try
            {
                var args = new List<DebugVariable>();
                var argValues = ilf.Arguments;
                for (var i = 0; i < argValues.Length; i++)
                {
                    try
                    {
                        var name = i < argNames.Length ? argNames[i] : null;
                        args.Add(new DebugVariable(name, i, ReadValue(argValues[i], expand: true), IsArgument: true));
                    }
                    catch { /* 单参数读取失败跳过 */ }
                }
                result["arguments"] = args;
            }
            catch { /* 参数读取失败 */ }

        return result;
    }

    /// <summary>停点变量上限条数（对象/数组展开 children 的截断阈值）。</summary>
    private const int MaxChildren = 32;

    /// <summary>
    /// 合成 $exception 伪变量（异常停点专有）：展示「类型全名: Message」（Message 读不出则只给类型名，诚实降级），
    /// children 走现有一级字段展开。当前线程无在抛异常（CurrentException 为空）返回 null。
    /// </summary>
    private List<DebugVariable>? TryReadExceptionVariable(CorDebugThread thread)
    {
        try
        {
            var excValue = thread.CurrentException;
            if (excValue is null) return null;
            var info = ReadCurrentExceptionInfo(excValue);
            var expanded = ReadValue(excValue, expand: true);
            var display = info?.TypeName ?? "<unknown>";
            var message = info?.Message;
            if (!string.IsNullOrEmpty(message)) display += $": {message}";
            return [new DebugVariable("$exception", -1, expanded with { Display = display }, IsArgument: false)];
        }
        catch { return null; }
    }

    /// <summary>
    /// 读在抛异常的概况（类型全名 + Message）。类型名经 TypeNameResolver（解析失败降级 token 文本）；
    /// Message 取 _message 字段字符串（私有字段经元数据 token + GetFieldValue，读不出返回 null）。
    /// 进程同步态调用。
    /// </summary>
    internal (string TypeName, string? Message)? ReadCurrentExceptionInfo(CorDebugValue excValue)
    {
        try
        {
            var typeName = "<unknown>";
            try
            {
                var cls = excValue.ExactType?.Class;
                if (cls is not null && cls.Module?.Name is { } modulePath)
                    typeName = TypeNameResolver.Resolve(modulePath, (int)cls.Token.Value) ?? $"token 0x{cls.Token.Value:x8}";
            }
            catch { /* 类型名解析失败保持 <unknown> */ }
            return (typeName, ReadExceptionMessage(excValue));
        }
        catch { return null; }
    }

    /// <summary>
    /// 读异常对象的 _message 字段字符串。_message 声明在 System.Exception，派生异常类自身通常无此字段——
    /// 沿运行时基类链（CorDebugType.Base）逐层找声明类再 GetFieldValue；任一层失败即停止（不虚报）。
    /// </summary>
    /// <summary>
    /// 读异常对象的 _message 字段字符串。_message 声明在 System.Exception，派生异常类自身通常无此字段——
    /// 沿运行时基类链（CorDebugType.Base）逐层找声明类再 GetFieldValue；字段值是字符串引用，解引用读内容；
    /// 任一层失败即停止（不虚报）。
    /// </summary>
    private static string? ReadExceptionMessage(CorDebugValue excValue)
    {
        try
        {
            if (excValue is not CorDebugReferenceValue r) return null;
            if (r.Dereference() is not CorDebugObjectValue obj) return null;
            for (var t = obj.ExactType; t is not null; t = t.Base)
            {
                try
                {
                    var cls = t.Class;
                    var modulePath = cls.Module?.Name;
                    if (string.IsNullOrEmpty(modulePath)) continue;
                    var field = ReadFieldTokens(modulePath!, (int)cls.Token.Value).FirstOrDefault(f => f.Name == "_message");
                    if (field.Name is null) continue;
                    // 字段值是 string 引用（CorDebugReferenceValue），解引用才是 CorDebugStringValue
                    if (obj.GetFieldValue(cls.Raw, new mdFieldDef((uint)field.Token)) is CorDebugReferenceValue fr
                        && fr.Dereference() is CorDebugStringValue s)
                        return s.GetString(s.Length);
                    return null; // 字段在但不是字符串引用：不向上继续
                }
                catch { /* 本层读取失败，向基类继续 */ }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>CorDebugValue → DebugValue（顶层变量：展开对象/数组一级成员）。进程同步态调用。</summary>
    private static DebugValue ReadValue(CorDebugValue value)
        => ReadValue(value, expand: false);

    /// <summary>CorDebugValue → DebugValue。expand=true 时对象/数组展开一级 children（不再递归，天然防环）。</summary>
    private static DebugValue ReadValue(CorDebugValue value, bool expand)
    {
        try
        {
            switch (value)
            {
                case CorDebugStringValue s:
                    return DebugValue.Scalar($"\"{s.GetString(s.Length)}\"");
                case CorDebugGenericValue g:
                    return DebugValue.Scalar(ReadGeneric(g));
                case CorDebugReferenceValue r when r.IsNull:
                    return DebugValue.Scalar("null");
                case CorDebugArrayValue a when expand:
                    return ReadArrayValue(a);
                case CorDebugReferenceValue r when expand:
                    return ReadReferenceExpanded(r);
                case CorDebugReferenceValue r:
                    return ReadReferenceShallow(r);
                default:
                    return DebugValue.Summary("value", value.Type.ToString());
            }
        }
        catch (Exception ex)
        {
            return DebugValue.Summary("error", $"<读取失败:{ex.Message}>");
        }
    }

    /// <summary>引用浅读（children 内不再展开）：指向字符串则读出内容（字符串便宜且无递归风险），其余引用给摘要。</summary>
    private static DebugValue ReadReferenceShallow(CorDebugReferenceValue r)
    {
        try
        {
            if (r.Dereference() is CorDebugStringValue s)
                return DebugValue.Scalar($"\"{s.GetString(s.Length)}\"");
        }
        catch { /* 解引用失败走摘要 */ }
        return DebugValue.Summary("reference", $"0x{r.Value.Value:x} → <object>");
    }

    /// <summary>引用展开一级：解引用后按 数组/对象 呈现成员（children 不再递归）。</summary>
    private static DebugValue ReadReferenceExpanded(CorDebugReferenceValue r)
    {
        var deref = r.Dereference();
        if (deref is null) return DebugValue.Scalar("null");
        if (deref is CorDebugArrayValue arr) return ReadArrayValue(arr);
        if (deref is CorDebugObjectValue obj) return ReadObjectValue(obj);
        return ReadValue(deref); // 装箱标量等
    }

    /// <summary>对象展开：字段清单取自模块元数据（同名一级），字段值经 GetFieldValue；静态字段跳过。</summary>
    private static DebugValue ReadObjectValue(CorDebugObjectValue obj)
    {
        var cls = obj.Class;
        var modulePath = cls.Module?.Name;
        if (string.IsNullOrEmpty(modulePath))
            return DebugValue.Summary("object", "<unknown>");
        var fields = ReadFieldTokens(modulePath!, (int)cls.Token.Value);
        var children = new List<DebugVariable>();
        foreach (var (name, fieldToken) in fields.Take(MaxChildren))
        {
            try
            {
                var fv = obj.GetFieldValue(cls.Raw, new mdFieldDef((uint)fieldToken));
                children.Add(new DebugVariable(name, -1, ReadValue(fv), IsArgument: false));
            }
            catch (Exception ex)
            {
                children.Add(new DebugVariable(name, -1, DebugValue.Summary("error", $"<读取失败:{ex.Message}>"), IsArgument: false));
            }
        }
        var display = fields.Count > MaxChildren
            ? $"字段 {fields.Count} 个（前 {MaxChildren}）"
            : $"{fields.Count} 字段";
        return DebugValue.Object(display, children);
    }

    /// <summary>数组展开：按线性位置取前 N 个元素。</summary>
    private static DebugValue ReadArrayValue(CorDebugArrayValue arr)
    {
        var total = arr.Count;
        var n = Math.Min(total, MaxChildren);
        var children = new List<DebugVariable>();
        for (var i = 0; i < n; i++)
        {
            try
            {
                children.Add(new DebugVariable($"[{i}]", -1, ReadValue(arr.GetElementAtPosition(i)), IsArgument: false));
            }
            catch (Exception ex)
            {
                children.Add(new DebugVariable($"[{i}]", -1, DebugValue.Summary("error", $"<读取失败:{ex.Message}>"), IsArgument: false));
            }
        }
        var display = $"长度 {total}" + (total > n ? $"（前 {n}）" : "");
        return DebugValue.Object(display, children);
    }

    /// <summary>实例字段清单（名字 + mdFieldDef token）：模块元数据 TypeDefinition → Fields（静态字段跳过）。</summary>
    private static List<(string Name, int Token)> ReadFieldTokens(string modulePath, int classToken)
    {
        using var fs = File.OpenRead(modulePath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        var td = mr.GetTypeDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.TypeDefinitionHandle(classToken));
        var list = new List<(string Name, int Token)>();
        foreach (var fh in td.GetFields())
        {
            var f = mr.GetFieldDefinition(fh);
            if (!f.Attributes.HasFlag(System.Reflection.FieldAttributes.Static))
                list.Add((mr.GetString(f.Name), System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(fh)));
        }
        return list;
    }

    private static string ReadGeneric(CorDebugGenericValue g)
    {
        var size = g.Size;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            g.GetValue(buf);
            var bytes = new byte[size];
            Marshal.Copy(buf, bytes, 0, size);
            return g.Type switch
            {
                CorElementType.I1 => ((sbyte)bytes[0]).ToString(),
                CorElementType.U1 or CorElementType.Boolean => bytes[0].ToString(),
                CorElementType.I2 => BitConverter.ToInt16(bytes).ToString(),
                CorElementType.U2 or CorElementType.Char => BitConverter.ToUInt16(bytes).ToString(),
                CorElementType.I4 => BitConverter.ToInt32(bytes).ToString(),
                CorElementType.U4 => BitConverter.ToUInt32(bytes).ToString(),
                CorElementType.I8 => BitConverter.ToInt64(bytes).ToString(),
                CorElementType.U8 => BitConverter.ToUInt64(bytes).ToString(),
                CorElementType.R4 => BitConverter.ToSingle(bytes).ToString(),
                CorElementType.R8 => BitConverter.ToDouble(bytes).ToString(),
                _ => $"<{size}字节>",
            };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- 表达式路径求值（P6；命令泵内纯读，全部异常带中文可诊断提示） ----

    /// <summary>求值中间态：进程内原始值，或引擎合成的标量（如字符串索引出的单字符字符串）。</summary>
    private abstract record EvalValue
    {
        public sealed record Raw(CorDebugValue Value) : EvalValue;
        public sealed record ScalarObj(object Value, string Display, string TypeName) : EvalValue;
    }

    /// <summary>路径求值主体：线程查找 + 解析到末段 + 转结果。</summary>
    private DebugEvalResult ReadPathValue(int threadId, string rootName, IReadOnlyList<PathSegment> segments)
    {
        if (_process is null) throw new InvalidOperationException("无被调试进程。");
        var thread = FindThread(threadId) ?? throw new InvalidOperationException($"找不到线程 threadId={threadId}。");
        return ToEvalResult(ResolvePathValue(thread, rootName, segments));
    }

    /// <summary>按线程 id 找线程（命令泵/回调线程共用；读/写路径复用的线程定位）。</summary>
    private CorDebugThread? FindThread(int threadId)
    {
        if (_process is null) return null;
        foreach (var t in _process.Threads) { if (t.Id == threadId) return t; }
        return null;
    }

    /// <summary>
    /// 路径解析到末段求值中间态（读/写共用底座，不含 ToEvalResult 转换）：根解析 + 逐段解引用
    /// （段号从 1 计，报错定位到段）。进程须已停住（调用方保证：停点态 / 泵内同一停住现场）。
    /// </summary>
    private EvalValue ResolvePathValue(CorDebugThread thread, string rootName, IReadOnlyList<PathSegment> segments)
    {
        if (segments.Count > PathSegment.MaxSegments)
            throw new InvalidOperationException($"路径段数 {segments.Count} 超上限 {PathSegment.MaxSegments}（防失控长链）。");
        EvalValue current = new EvalValue.Raw(FindRootValue(thread, rootName));
        for (var i = 0; i < segments.Count; i++)
        {
            current = segments[i] switch
            {
                PathSegment.Field f => ReadFieldSegment(current, i + 1, f.Name),
                PathSegment.Index idx => ReadIndexSegment(current, i + 1, idx.Position),
                _ => throw new InvalidOperationException("不支持的路径段类型。"),
            };
        }
        return current;
    }

    /// <summary>根解析：$exception 伪根 → locals/arguments 按名匹配（与 GetVariablesAsync 同源）→ slotN 回退。</summary>
    private CorDebugValue FindRootValue(CorDebugThread thread, string rootName)
    {
        if (rootName.Equals("$exception", StringComparison.OrdinalIgnoreCase))
        {
            // CurrentException 无在抛异常时返回 S_FALSE（ClrDebug 抛 DebugException），与 P2 TryReadExceptionVariable 同款兜住
            CorDebugValue? exc = null;
            try { exc = thread.CurrentException; } catch { /* 无在抛异常 */ }
            if (exc is null) throw new InvalidOperationException("当前线程无在抛异常，$exception 不可用（仅在异常停点有效）。");
            return exc;
        }
        if (thread.ActiveFrame is not CorDebugILFrame ilf)
            throw new InvalidOperationException($"栈顶非 IL 帧，无法解析变量「{rootName}」。");

        // 名字来源与 ReadVariablesForThread 同一管线：参数名取元数据、局部名取 PDB（缺失时 debug_variables 以 slotN 展示）
        string?[] argNames = [], localNames = [];
        var top = ReadTopFrame(thread);
        if (top is not null)
        {
            var modulePath = _breakpoints.GetModulePath(top.ModuleName);
            if (modulePath is not null)
            {
                var names = SymbolNameResolver.Resolve(modulePath, top.MethodToken);
                argNames = names.ArgNames;
                localNames = names.LocalNames;
            }
        }

        var args = ilf.Arguments;
        var locals = ilf.LocalVariables;
        var hit = MatchNamed(args, argNames, rootName) ?? MatchNamed(locals, localNames, rootName);
        if (hit is null && rootName.StartsWith("slot", StringComparison.OrdinalIgnoreCase) && int.TryParse(rootName[4..], out var slot))
        {
            if (slot >= 0 && slot < locals.Length) hit = locals[slot];
            else if (slot >= 0 && slot < args.Length) hit = args[slot];
        }
        if (hit is not null) return hit;

        var available = new List<string>();
        for (var i = 0; i < argNames.Length; i++) available.Add(argNames[i] ?? $"slot{i}");
        for (var i = 0; i < localNames.Length; i++) available.Add(localNames[i] ?? $"slot{i}");
        available.Add("$exception");
        throw new InvalidOperationException($"栈顶帧无变量「{rootName}」（可用：{string.Join(", ", available)}）。");
    }

    /// <summary>按名匹配槽位值：先精确后忽略大小写（两轮），防错名大小写抢命中。</summary>
    private static CorDebugValue? MatchNamed(CorDebugValue[] values, string?[] names, string rootName)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < values.Length && i < names.Length; i++)
            {
                var name = names[i];
                if (name is null) continue;
                var equals = pass == 0
                    ? string.Equals(name, rootName, StringComparison.Ordinal)
                    : string.Equals(name, rootName, StringComparison.OrdinalIgnoreCase);
                if (equals) return values[i];
            }
        }
        return null;
    }

    /// <summary>字段段：解引用定位对象 → 全基类链实例字段中按 约定降级候选 找名 → GetFieldValue。</summary>
    private static EvalValue ReadFieldSegment(EvalValue current, int segNo, string fieldName)
    {
        var segText = $".{fieldName}";
        var raw = current switch
        {
            EvalValue.Raw r => r.Value,
            _ => throw new InvalidOperationException($"第 {segNo} 段 {segText}：标量值无字段可取。"),
        };
        var obj = DerefToObject(raw, segNo, segText);
        var fields = EnumerateInstanceFields(obj);
        foreach (var candidate in FieldCandidateNames(fieldName))
        {
            var hit = fields.FirstOrDefault(f => string.Equals(f.Name, candidate, StringComparison.Ordinal));
            if (hit.Name is not null)
                return new EvalValue.Raw(obj.GetFieldValue(hit.DeclaringClass.Raw, new mdFieldDef((uint)hit.Token)));
        }
        var names = string.Join(", ", fields.Select(f => f.Name).Distinct());
        throw new InvalidOperationException(
            $"第 {segNo} 段 {segText}：{TypeNameOfObject(obj)} 无此字段（属性不可直接读；可用字段：{names}）。");
    }

    /// <summary>属性约定降级候选：X → _x → _X → &lt;X&gt;k__BackingField（spec §4，顺序固定）。</summary>
    private static IEnumerable<string> FieldCandidateNames(string name)
    {
        yield return name;
        if (name.Length > 0)
        {
            yield return "_" + char.ToLowerInvariant(name[0]) + name[1..];
            yield return "_" + name;
        }
        yield return $"<{name}>k__BackingField";
    }

    /// <summary>实例字段全链清单（ExactType → Base 逐层，同名以最派生为准；静态字段 ReadFieldTokens 已排除）。</summary>
    private static List<(CorDebugClass DeclaringClass, string Name, int Token)> EnumerateInstanceFields(CorDebugObjectValue obj)
    {
        var list = new List<(CorDebugClass, string, int)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = obj.ExactType; t is not null; t = t.Base)
        {
            try
            {
                var cls = t.Class;
                var modulePath = cls.Module?.Name;
                if (string.IsNullOrEmpty(modulePath)) continue;
                foreach (var f in ReadFieldTokens(modulePath!, (int)cls.Token.Value))
                    if (seen.Add(f.Name))
                        list.Add((cls, f.Name, f.Token));
            }
            catch { /* 本层元数据读取失败：向基类继续 */ }
        }
        return list;
    }

    /// <summary>索引段：数组任意下标（GetElementAtPosition，绕开 MaxChildren 截断）；字符串索引得单字符字符串。</summary>
    private static EvalValue ReadIndexSegment(EvalValue current, int segNo, int index)
    {
        // 引擎合成的标量（单字符字符串）也可继续索引
        if (current is EvalValue.ScalarObj s)
        {
            if (s.Value is string text)
            {
                if (index < 0 || index >= text.Length)
                    throw new InvalidOperationException($"第 {segNo} 段 [{index}]：字符串索引越界（长度 {text.Length}，有效 0-{text.Length - 1}）。");
                var next = text[index].ToString();
                return new EvalValue.ScalarObj(next, $"\"{next}\"", "System.String");
            }
            throw new InvalidOperationException($"第 {segNo} 段 [{index}]：仅数组/字符串支持索引。");
        }

        var v = ((EvalValue.Raw)current).Value;
        if (v is CorDebugReferenceValue r)
        {
            if (r.IsNull) throw new InvalidOperationException($"第 {segNo} 段 [{index}]：对象为 null，无法索引。");
            v = r.Dereference() ?? throw new InvalidOperationException($"第 {segNo} 段 [{index}]：解引用失败。");
        }
        switch (v)
        {
            case CorDebugArrayValue arr:
            {
                if (arr.Rank != 1)
                    throw new InvalidOperationException($"第 {segNo} 段 [{index}]：多维数组 v1 不支持（Rank={arr.Rank}），建议取一维数组或对象字段。");
                var total = arr.Count;
                if (index < 0 || index >= total)
                    throw new InvalidOperationException($"第 {segNo} 段 [{index}]：索引越界（长度 {total}，有效 0-{total - 1}）。");
                return new EvalValue.Raw(arr.GetElementAtPosition(index));
            }
            case CorDebugStringValue str:
            {
                var text = str.GetString(str.Length);
                if (index < 0 || index >= text.Length)
                    throw new InvalidOperationException($"第 {segNo} 段 [{index}]：字符串索引越界（长度 {text.Length}，有效 0-{text.Length - 1}）。");
                var ch = text[index].ToString();
                return new EvalValue.ScalarObj(ch, $"\"{ch}\"", "System.String");
            }
            default:
                throw new InvalidOperationException(
                    $"第 {segNo} 段 [{index}]：仅数组/字符串支持索引（当前 {ResolveValueTypeName(v) ?? "<未知类型>"}）；List 等集合请取内部字段再索引（如 _items[0]）。");
        }
    }

    /// <summary>取字段前定位对象值：解引用 + null/数组/字符串/标量的诚实诊断（segNo/segText 供报错定位到段）。</summary>
    private static CorDebugObjectValue DerefToObject(CorDebugValue current, int segNo, string segText)
    {
        var v = current;
        if (v is CorDebugReferenceValue r)
        {
            if (r.IsNull) throw new InvalidOperationException($"第 {segNo} 段 {segText}：对象为 null，无法取成员。");
            v = r.Dereference() ?? throw new InvalidOperationException($"第 {segNo} 段 {segText}：解引用失败。");
        }
        switch (v)
        {
            case CorDebugObjectValue obj:
                return obj;
            case CorDebugArrayValue:
                throw new InvalidOperationException($"第 {segNo} 段 {segText}：数组无字段，取元素请用 [n] 索引。");
            case CorDebugStringValue:
                throw new InvalidOperationException(
                    $"第 {segNo} 段 {segText}：字符串不支持成员访问（Length 亦不支持），可用 [n] 取单字符或经 debug_variables 查看整体。");
            default:
                throw new InvalidOperationException($"第 {segNo} 段 {segText}：{ResolveValueTypeName(v) ?? "<未知类型>"} 为标量值，无字段可取。");
        }
    }

    /// <summary>终值 → DebugEvalResult：Display/Children 复用 ReadValue(expand:true)（与 debug_variables 同款），Kind/ScalarValue 供比较与布尔判定。</summary>
    private static DebugEvalResult ToEvalResult(EvalValue value)
        => value switch
        {
            EvalValue.ScalarObj s => new DebugEvalResult(s.Display, s.TypeName, DebugEvalKind.Scalar, null, s.Value),
            EvalValue.Raw raw => RawToResult(raw.Value),
            _ => new DebugEvalResult("<未知>", null, DebugEvalKind.Object, null, null),
        };

    private static DebugEvalResult RawToResult(CorDebugValue value)
    {
        var rendered = ReadValue(value, expand: true);
        switch (value)
        {
            case CorDebugStringValue s:
                return new DebugEvalResult(rendered.Display, "System.String", DebugEvalKind.Scalar, null, s.GetString(s.Length));
            case CorDebugGenericValue g:
                return new DebugEvalResult(rendered.Display, ResolveValueTypeName(value) ?? MapElementTypeName(g.Type), DebugEvalKind.Scalar, null, ReadScalarRaw(g));
            case CorDebugReferenceValue r when r.IsNull:
                return new DebugEvalResult("null", ResolveValueTypeName(value), DebugEvalKind.Null, null, null);
            case CorDebugReferenceValue r:
                return DerefToResult(r);
            case CorDebugArrayValue:
                return new DebugEvalResult(rendered.Display, null, DebugEvalKind.Array, rendered.Children, null);
            default: // 对象/结构体终值
                return new DebugEvalResult(rendered.Display, ResolveValueTypeName(value), DebugEvalKind.Object, rendered.Children, null);
        }
    }

    private static DebugEvalResult DerefToResult(CorDebugReferenceValue r)
    {
        var rendered = ReadValue(r, expand: true); // 引用展开：字符串引号内容 / 数组 长度+children / 对象 字段+children
        var deref = r.Dereference();
        if (deref is CorDebugStringValue s)
            return new DebugEvalResult(rendered.Display, "System.String", DebugEvalKind.Scalar, null, s.GetString(s.Length));
        if (deref is CorDebugArrayValue)
            return new DebugEvalResult(rendered.Display, null, DebugEvalKind.Array, rendered.Children, null);
        if (deref is CorDebugGenericValue boxed) // 装箱标量
            return new DebugEvalResult(rendered.Display, ResolveValueTypeName(deref) ?? MapElementTypeName(boxed.Type), DebugEvalKind.Scalar, null, ReadScalarRaw(boxed));
        return new DebugEvalResult(rendered.Display, ResolveValueTypeName(r) ?? ResolveValueTypeName(deref), DebugEvalKind.Object, rendered.Children, null);
    }

    /// <summary>泛型值的标量原始值（bool/char/整型/浮点；非基元类型返回 null）。</summary>
    private static object? ReadScalarRaw(CorDebugGenericValue g)
    {
        var size = g.Size;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            g.GetValue(buf);
            var bytes = new byte[size];
            Marshal.Copy(buf, bytes, 0, size);
            return g.Type switch
            {
                CorElementType.Boolean => bytes[0] != 0,
                CorElementType.Char => (char)BitConverter.ToUInt16(bytes),
                CorElementType.I1 => (sbyte)bytes[0],
                CorElementType.U1 => bytes[0],
                CorElementType.I2 => BitConverter.ToInt16(bytes),
                CorElementType.U2 => BitConverter.ToUInt16(bytes),
                CorElementType.I4 => BitConverter.ToInt32(bytes),
                CorElementType.U4 => BitConverter.ToUInt32(bytes),
                CorElementType.I8 => BitConverter.ToInt64(bytes),
                CorElementType.U8 => BitConverter.ToUInt64(bytes),
                CorElementType.R4 => BitConverter.ToSingle(bytes),
                CorElementType.R8 => BitConverter.ToDouble(bytes),
                _ => null,
            };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>值 → 类型全名（ExactType 经 TypeNameResolver；失败返回 null 由调用方降级）。</summary>
    private static string? ResolveValueTypeName(CorDebugValue value)
    {
        try
        {
            var cls = value.ExactType?.Class;
            if (cls is null) return null;
            var modulePath = cls.Module?.Name;
            if (string.IsNullOrEmpty(modulePath)) return null;
            return TypeNameResolver.Resolve(modulePath!, (int)cls.Token.Value);
        }
        catch { return null; }
    }

    private static string TypeNameOfObject(CorDebugObjectValue obj) => ResolveValueTypeName(obj) ?? "<未知类型>";

    /// <summary>CorElementType → BCL 类型名（TypeNameResolver 失败时的降级映射）。</summary>
    private static string? MapElementTypeName(CorElementType t) => t switch
    {
        CorElementType.Boolean => "System.Boolean",
        CorElementType.Char => "System.Char",
        CorElementType.I1 => "System.SByte",
        CorElementType.U1 => "System.Byte",
        CorElementType.I2 => "System.Int16",
        CorElementType.U2 => "System.UInt16",
        CorElementType.I4 => "System.Int32",
        CorElementType.U4 => "System.UInt32",
        CorElementType.I8 => "System.Int64",
        CorElementType.U8 => "System.UInt64",
        CorElementType.R4 => "System.Single",
        CorElementType.R8 => "System.Double",
        _ => null,
    };

    // ---- 路径写值（W1 debug_set；命令泵内同步写，进程停住态。写进程内存有崩目标风险——调用方明示，只写读链路已证明可定位的目标） ----

    /// <summary>写路径主体：线程查找 → readonly 前置拒绝 → 解析到末段（读语义）→ 写前原值回显 → 分派写 → 写后重读回显。</summary>
    private DebugWriteResult WritePathValue(int threadId, string rootName, IReadOnlyList<PathSegment> segments, DebugWriteValue value)
    {
        if (_process is null) throw new InvalidOperationException("无被调试进程。");
        if (segments.Count > PathSegment.MaxSegments)
            throw new InvalidOperationException($"路径段数 {segments.Count} 超上限 {PathSegment.MaxSegments}（防失控长链）。");
        var thread = FindThread(threadId) ?? throw new InvalidOperationException($"找不到线程 threadId={threadId}。");

        // 字段段末段先查 readonly/const（局部/参数/数组元素无 readonly 概念，不查）
        EnforceFieldWritable(thread, rootName, segments);

        var target = ResolvePathValue(thread, rootName, segments); // 定位到末段（读语义）
        var old = ToEvalResult(target);                            // 写前原值回显
        var raw = target switch
        {
            EvalValue.Raw r => r.Value,
            EvalValue.ScalarObj s => throw new InvalidOperationException(
                $"路径 {Describe(rootName, segments)} 末段是引擎合成标量（字符串索引单字符），不可写；请改写到数组元素或字段目标。"),
            _ => throw new InvalidOperationException("不支持的写目标。"),
        };
        WriteTerminal(thread, raw, value, rootName, segments);     // 末段分派
        var after = ToEvalResult(ResolvePathValue(thread, rootName, segments)); // 写后重读（校验 + 新值回显）
        return new DebugWriteResult(old.Display, after.Display, old.TypeName);
    }

    /// <summary>路径展示（报错/回显用）：root.field[0]…</summary>
    private static string Describe(string root, IReadOnlyList<PathSegment> segments)
        => root + string.Concat(segments.Select(s => s switch
        {
            PathSegment.Field f => "." + f.Name,
            PathSegment.Index i => $"[{i.Position}]",
            _ => "",
        }));

    /// <summary>
    /// readonly/const 拒绝：末段为字段时，重解析字段声明者并从模块元数据读 FieldAttributes.InitOnly。
    /// 命中 → 抛中文拒绝（v1 不做 EnC 式忽略只读写）。
    /// </summary>
    private void EnforceFieldWritable(CorDebugThread thread, string rootName, IReadOnlyList<PathSegment> segments)
    {
        if (segments.Count == 0 || segments[^1] is not PathSegment.Field f) return;
        var segNo = segments.Count;
        var segText = $".{f.Name}";
        EvalValue current = new EvalValue.Raw(FindRootValue(thread, rootName));
        for (var i = 0; i < segments.Count - 1; i++)
        {
            current = segments[i] switch
            {
                PathSegment.Field p => ReadFieldSegment(current, i + 1, p.Name),
                PathSegment.Index idx => ReadIndexSegment(current, i + 1, idx.Position),
                _ => throw new InvalidOperationException("不支持的路径段类型。"),
            };
        }
        var obj = DerefToObject(current is EvalValue.Raw r ? r.Value
            : throw new InvalidOperationException($"第 {segNo} 段 {segText}：标量值无字段可取。"), segNo, segText);
        // 与 ReadFieldSegment 同款候选匹配（属性约定降级），命中同一字段即查其声明模块
        foreach (var candidate in FieldCandidateNames(f.Name))
        {
            var hit = EnumerateInstanceFields(obj).FirstOrDefault(x => string.Equals(x.Name, candidate, StringComparison.Ordinal));
            if (hit.Name is null) continue;
            var modulePath = hit.DeclaringClass.Module?.Name;
            if (!string.IsNullOrEmpty(modulePath) && IsFieldInitOnly(modulePath, hit.Token))
                throw new InvalidOperationException($"字段 {Describe(rootName, segments)} 是 readonly/const，不可改写（v1 拒绝）。");
            return;
        }
        // 未命中：不重复报「无此字段」——ResolvePathValue 会给出带可用字段清单的错误
    }

    /// <summary>模块元数据 FieldAttributes.InitOnly（readonly 实例字段 / const）；读失败按可写处理（不误伤）。</summary>
    private static bool IsFieldInitOnly(string modulePath, int fieldToken)
    {
        try
        {
            using var fs = File.OpenRead(modulePath);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            var mr = pe.GetMetadataReader();
            var fh = System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(fieldToken);
            var f = mr.GetFieldDefinition(fh);
            return f.Attributes.HasFlag(System.Reflection.FieldAttributes.InitOnly);
        }
        catch { return false; }
    }

    /// <summary>末段分派（支持矩阵 = spec §7 spike 定案）。线程经参数传入（重定向源须在同一停点/线程解析）。</summary>
    private void WriteTerminal(CorDebugThread thread, CorDebugValue target, DebugWriteValue value, string rootName, IReadOnlyList<PathSegment> segments)
    {
        switch (target)
        {
            case CorDebugReferenceValue r:                              // 引用目标（对象字段/局部/数组引用槽）
                switch (value)
                {
                    case DebugWriteValue.Null:
                        r.Value = 0;                                    // 置 null（CORDB_ADDRESS 0）
                        break;
                    case DebugWriteValue.CopyPath p:
                        WriteReferenceRedirect(thread, r, p, rootName, segments);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"目标 {Describe(rootName, segments)} 是引用类型，不能写标量字面量——请置 null 或给同帧对象路径（重定向）。");
                }
                break;
            case CorDebugGenericValue g:                                // 值类型目标（局部/参数/字段/数组元素/enum 底层）
                if (value is not DebugWriteValue.Scalar s)
                    throw new InvalidOperationException(
                        $"目标 {Describe(rootName, segments)} 是值类型，请给字面量数字/bool（枚举给底层整数值）。");
                WriteScalarToGeneric(g, s.Text, Describe(rootName, segments));
                break;
            case CorDebugStringValue:
                throw new InvalidOperationException(
                    "字符串内容不可改（构造新字符串需 func-eval，已关）；请置 null 或重定向到已有字符串对象。");
            default:
                // spike 定案：enum 等带元数据类型的值字段终端为对象值（非 GenericValue）→ v1 降级提示
                throw new InvalidOperationException($"目标类型暂不支持写（{ResolveValueTypeName(target) ?? "<未知>"}）。");
        }
    }

    /// <summary>
    /// 引用重定向：源路径在「同一停点、同一线程」解析为引用值，取源引用地址回写（spec 拍板：重定向进 v1）。
    /// 类型兼容保守校验：两端 deref 后 ExactType 全名一致才放行；**目标当前为 null 时无法 deref 比对——
    /// v1 未实现「声明字段类型」解码校验，仅校验源为非 null**（边界见 spec §7/README 风险：目标为 null 的重定向由 agent 保证同型）。
    /// </summary>
    private void WriteReferenceRedirect(CorDebugThread thread, CorDebugReferenceValue target, DebugWriteValue.CopyPath p, string rootName, IReadOnlyList<PathSegment> segments)
    {
        var srcDesc = Describe(p.Root, p.Segments);
        var src = ResolvePathValue(thread, p.Root, p.Segments);
        if (src is not EvalValue.Raw { Value: CorDebugReferenceValue srcRef })
            throw new InvalidOperationException($"重定向源 {srcDesc} 不是指向对象的引用（目标仍在原值，未改动）。");
        if (srcRef.IsNull)
            throw new InvalidOperationException($"重定向源 {srcDesc} 是 null 引用（目标仍在原值，未改动）；置空请用 value=null。");

        // 类型兼容保守校验：两端 deref 对象全名一致才放行（v1 宁缺毋滥）；解析失败按放行（诚实降级为不校验）
        try
        {
            var targetDeref = target.IsNull ? null : target.Dereference();
            var srcDeref = srcRef.Dereference();
            if (targetDeref is not null && srcDeref is not null)
            {
                var targetType = ResolveValueTypeName(targetDeref);
                var srcType = ResolveValueTypeName(srcDeref);
                if (targetType is not null && srcType is not null
                    && !string.Equals(targetType, srcType, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"重定向源 {srcDesc} 类型 {srcType} 与目标 {Describe(rootName, segments)} 类型 {targetType} 不一致（v1 要求同型）；目标仍在原值，未改动。");
            }
        }
        catch (InvalidOperationException) { throw; }
        catch { /* deref/类型解析失败：诚实降级为不校验 */ }

        target.Value = srcRef.Value; // CorDebugReferenceValue.Value = 被引用对象地址
    }

    /// <summary>
    /// 标量文本 → 目标元素类型字节（写回 GenericValue）。数值允许 0x 前缀/负号/小数/科学计数；m/f/d 后缀在转换前剥离，
    /// 是否接受由目标类型决定（整型拒后缀、浮点接受）。**decimal 无 GenericValue 形态**（字段/栈值以对象值呈现，实测
    /// System.Decimal 终端为 CorDebugObjectValue，见 spec §7）——故此处不设 Decimal case，整值写走末段对象值降级口。
    /// </summary>
    private static void WriteScalarToGeneric(CorDebugGenericValue g, string text, string targetDesc)
    {
        byte[] bytes;
        var kind = g.Type;
        try
        {
            bytes = kind switch
            {
                CorElementType.Boolean => text.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" => [1],
                    "false" or "0" => [0],
                    _ => throw new InvalidOperationException($"目标 {targetDesc} 是 bool，新值须 true/false/1/0（当前「{text}」）。"),
                },
                CorElementType.Char => ParseCharBytes(text, targetDesc),
                CorElementType.I1 => ScalarIntBytes(text, targetDesc, "SByte", sbyte.MinValue, sbyte.MaxValue, v => new[] { (byte)checked((sbyte)v) }),
                CorElementType.U1 => ScalarIntBytes(text, targetDesc, "Byte", byte.MinValue, byte.MaxValue, v => [(byte)v]),
                CorElementType.I2 => ScalarIntBytes(text, targetDesc, "Int16", short.MinValue, short.MaxValue, v => BitConverter.GetBytes(checked((short)v))),
                CorElementType.U2 => ScalarIntBytes(text, targetDesc, "UInt16", ushort.MinValue, ushort.MaxValue, v => BitConverter.GetBytes(checked((ushort)v))),
                CorElementType.I4 => ScalarIntBytes(text, targetDesc, "Int32", int.MinValue, int.MaxValue, v => BitConverter.GetBytes(checked((int)v))),
                CorElementType.U4 => ScalarIntBytes(text, targetDesc, "UInt32", uint.MinValue, uint.MaxValue, v => BitConverter.GetBytes(checked((uint)v))),
                CorElementType.I8 => ScalarIntBytes(text, targetDesc, "Int64", long.MinValue, long.MaxValue, v => BitConverter.GetBytes(checked((long)v))),
                CorElementType.U8 => ScalarIntBytes(text, targetDesc, "UInt64", ulong.MinValue, ulong.MaxValue, v => BitConverter.GetBytes(checked((ulong)v))),
                CorElementType.R4 or CorElementType.R8 => ParseFloatBytes(text, targetDesc, kind),
                _ => throw new InvalidOperationException($"目标 {targetDesc} 类型 {MapElementTypeName(kind) ?? kind.ToString()} v1 不支持写（该目标为非标量值类型，请改写到其标量字段或对象外层字段）。"),
            };
        }
        catch (InvalidOperationException) { throw; }
        catch (FormatException) { throw new InvalidOperationException($"目标 {targetDesc}：文本「{text}」不是合法的 {MapElementTypeName(kind) ?? kind.ToString()} 值。"); }
        catch (OverflowException) { throw new InvalidOperationException($"目标 {targetDesc}：文本「{text}」超出 {MapElementTypeName(kind) ?? kind.ToString()} 取值范围。"); }

        var buf = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, buf, bytes.Length); g.SetValue(buf); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>char：单引号字符 'x' 或 0-65535 码点。</summary>
    private static byte[] ParseCharBytes(string text, string targetDesc)
    {
        var t = text.Trim();
        if (t.Length == 3 && t[0] == '\'' && t[2] == '\'')
            return BitConverter.GetBytes((ushort)t[1]);
        if (TryParseIntegral(t, out var v) && v >= 0 && v <= ushort.MaxValue)
            return BitConverter.GetBytes((ushort)v);
        throw new InvalidOperationException($"目标 {targetDesc} 是 char，新值须单引号字符（'x'）或 0-65535 整数码点（当前「{text}」）。");
    }

    /// <summary>整型目标：拒绝小数/科学计数/带后缀文本（拍板：是否接受后缀由目标类型决定——整型不收；0x 十六进制不受此限）。</summary>
    private static byte[] ScalarIntBytes(string text, string targetDesc, string typeName, BigInteger min, BigInteger max, Func<BigInteger, byte[]> toBytes)
    {
        var t = text.Trim();
        // 0x 判定剥掉可选符号位（parser 文法只产无符号 0x，引擎防御性接受带符号；hex 数字位 a-f 永不当作 m/f/d 后缀）
        var body = t.Length > 0 && (t[0] == '-' || t[0] == '+') ? t[1..] : t;
        var isHex = body.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!isHex)
        {
            if (t.Length > 0 && "mMfFdD".Contains(t[^1]))
                throw new InvalidOperationException($"目标 {targetDesc} 是整型 {typeName}，不能写带 {t[^1]} 后缀的文本「{text}」；请给整数。");
            if (t.Contains('.') || t.Contains('e') || t.Contains('E'))
                throw new InvalidOperationException($"目标 {targetDesc} 是整型 {typeName}，不能写小数/科学计数文本「{text}」；请给整数。");
        }
        if (!TryParseIntegral(t, out var v))
            throw new InvalidOperationException($"目标 {targetDesc}：文本「{text}」不是合法的 {typeName} 整数值。");
        if (v < min || v > max)
            throw new InvalidOperationException($"目标 {targetDesc}：文本「{text}」超出 {typeName} 取值范围（{min}~{max}）。");
        return toBytes(v);
    }

    /// <summary>浮点目标：允许 m/f/d 后缀剥离；m 后缀文本虽是 decimal 标记，按浮点解析其数字部分。</summary>
    private static byte[] ParseFloatBytes(string text, string targetDesc, CorElementType kind)
    {
        var t = text.Trim();
        if (t.Length > 0 && "fFdDmM".Contains(t[^1])) t = t[..^1];
        var isDouble = kind == CorElementType.R8;
        if (isDouble)
        {
            var d = double.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
            return BitConverter.GetBytes(d);
        }
        var f = float.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        return BitConverter.GetBytes(f);
    }

    /// <summary>整型文本 → BigInteger：十进制（可带 +/-）或无符号/带符号 0x 十六进制；非整型文本返回 false。</summary>
    private static bool TryParseIntegral(string t, out BigInteger value)
    {
        value = default;
        t = t.Trim();
        if (t.Length == 0) return false;
        var negative = t[0] == '-';
        var body = negative || t[0] == '+' ? t[1..] : t;
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = body[2..];
            if (hex.Length == 0) return false;
            foreach (var c in hex) if (!Uri.IsHexDigit(c)) return false;
            if (!BigInteger.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var abs)) return false;
            value = negative ? -abs : abs;
            return true;
        }
        var digits = body;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
        if (!BigInteger.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var absD)) return false;
        value = negative ? -absD : absD;
        return true;
    }

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

    /// <summary>异常被过滤器跳过（不停进程、不改状态；Session 计数给 debug_wait/debug_state 不命中反馈）。</summary>
    internal void PublishExceptionSkipped(int threadId, string type, string? message)
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.ExceptionSkipped,
            new ExceptionSkippedPayload(threadId, type, message)));

    /// <summary>trace 断点命中（不停进程；快照已在此前于命令泵内同步读取）。</summary>
    internal void PublishTraceHit(int breakpointId, int threadId, FrameLocation? top, IReadOnlyList<TraceVariable> variables)
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.TraceHit,
            new TraceHitPayload(breakpointId, threadId, DateTimeOffset.UtcNow, top, variables)));

    /// <summary>断点条件求值失败（P7：不停进程；Session consume 式计数，debug_wait/debug_state 反馈防静默空等）。</summary>
    internal void PublishConditionFailed(int breakpointId, int threadId, string error)
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.BreakpointConditionFailed,
            new BreakpointConditionFailedPayload(breakpointId, threadId, error)));

    /// <summary>
    /// 断点条件求值（P7，命令泵线程内、进程停住态）：求值器由 Session 注入，pathResolver 直通
    /// ReadPathValue（同步直读）。true=条件为真（继续命中流程）；false/异常=放行——异常发
    /// BreakpointConditionFailed 事件供 Session 计数反馈（防「条件写错永不命中」静默空等）。
    /// </summary>
    internal bool EvaluateBreakpointCondition(DebugBreakpoint breakpoint, CorDebugThread thread)
    {
        var evaluator = _conditionEvaluator;
        if (evaluator is null)
        {
            // set 时已拦截；兜底放行不卡进程
            PublishConditionFailed(breakpoint.Id, thread.Id, "会话无条件求值器");
            return false;
        }
        try
        {
            return evaluator.Evaluate(thread.Id, breakpoint.Condition!, ReadPathValue);
        }
        catch (Exception ex)
        {
            PublishConditionFailed(breakpoint.Id, thread.Id, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// trace 快照（P5）：命令泵内读取栈顶帧 locals/arguments，展平为单行摘要（scope/name/display）。
    /// 不展开 children（token 可控）；异常停点的 $exception 节若存在亦纳入。仅供 trace 路径调用（进程同步态）。
    /// </summary>
    internal IReadOnlyList<TraceVariable> CaptureTraceVariables(int threadId)
    {
        var list = new List<TraceVariable>();
        try
        {
            foreach (var (scope, vars) in ReadVariablesForThread(threadId))
            foreach (var v in vars)
                list.Add(new TraceVariable(scope, v.Name, v.Slot, v.Value.Display));
        }
        catch { /* 快照失败：返回已读部分（可能为空），不阻塞 trace 继续 */ }
        return list;
    }

    internal void PublishState(DebugSessionState state, string? reason)
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.SessionStateChanged,
            new SessionStateChangedPayload(state, reason)));

    internal void Log(string level, string message)
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.EngineLog,
            new EngineLogPayload(level, message)));

    /// <summary>断点集合变更事件（设/删/清/重绑后发布；快照全量，UI 推送替代轮询）。</summary>
    internal void PublishBreakpointsChanged()
        => Publish(new DebugEvent("session", NextSeq(), DateTimeOffset.UtcNow, DebugEventKind.BreakpointsChanged,
            new BreakpointsChangedPayload(_breakpoints.Breakpoints
                .Select(b => new BreakpointSnapshot(b.Id, b.ModuleName, b.MethodToken, b.IlOffset, b.SourcePath, b.SourceLine)).ToList())));

    /// <summary>读线程栈顶 IL 帧位置（供断点/步/异常事件附 top frame）。回调线程调用。</summary>
    internal FrameLocation? ReadTopFrame(CorDebugThread thread)
    {
        try
        {
            if (thread.ActiveFrame is not CorDebugILFrame ilf) return null;
            var rawModule = ilf.Function?.Module?.Name ?? "<unknown>";
            var module = Path.GetFileName(rawModule); // 归一化为文件名
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

    /// <summary>栈帧类名真名化：TypeDef/TypeRef token 经 TypeNameResolver 解全名（失败返回 null → 展示端降级 token）。</summary>
    private static string? TryGetTypeName(CorDebugILFrame ilf, string modulePath)
    {
        try
        {
            var cls = ilf.Function?.Class;
            return cls is null ? null : TypeNameResolver.Resolve(modulePath, (int)cls.Token.Value);
        }
        catch { return null; }
    }

    /// <summary>栈帧方法名真名化：MethodDef token 经 SymbolNameResolver 解名（失败返回 null → 展示端降级 token）。</summary>
    private static string? TryGetMethodName(CorDebugILFrame ilf, string modulePath)
    {
        try
        {
            var token = ilf.FunctionToken;
            return token.IsNil ? null : SymbolNameResolver.ReadMethodName(modulePath, (int)token.Value);
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

