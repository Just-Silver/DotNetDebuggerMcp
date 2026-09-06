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

    /// <summary>枚举进程已加载模块登记到 BreakpointManager（attach 已运行进程用）。</summary>
    private void SyncLoadedModules()
    {
        try
        {
            foreach (var ad in _process!.AppDomains)
            {
                foreach (var asm in ad.Assemblies)
                {
                    foreach (var mod in asm.Modules)
                    {
                        try { _breakpoints.TrackModule(mod); }
                        catch { /* 单个模块登记失败忽略 */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("warn", $"枚举已加载模块失败: {ex.Message}");
        }
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

    /// <summary>设置断点（模块未加载时登记为 pending，LoadModule 自动重绑；方法 token 无效抛错）。</summary>
    public Task<DebugBreakpoint> SetBreakpointAsync(string moduleName, int methodToken, int ilOffset, CancellationToken ct = default)
        => PostAsyncResult(() =>
        {
            var bp = _breakpoints.Add(moduleName, methodToken, ilOffset);
            PublishBreakpointsChanged();
            return bp;
        }, ct);

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

    /// <summary>
    /// 读取指定线程栈顶 IL 帧的局部变量与参数（停顿时调用）。v1：标量/字符串直接渲染；
    /// 对象降级为摘要；名字为空用 slotN。返回 { "exception": [...], "locals": [...], "arguments": [...] }——
    /// exception 节仅在当前线程有在抛异常（first-chance 停点）时存在，合成 $exception 伪变量。
    /// </summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>> GetVariablesAsync(int threadId, CancellationToken ct = default)
        => PostAsyncResult(() =>
        {
            var result = new Dictionary<string, IReadOnlyList<DebugVariable>>();
            if (_process is null) return (IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>)result;

            CorDebugThread? thread = null;
            foreach (var t in _process.Threads) { if (t.Id == threadId) { thread = t; break; } }
            if (thread is null) return (IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>)result;

            // 异常停点：合成 $exception 首节（类型全名+Message 展示；children 走现有一级展开，读不出的成员诚实标注）
            var exceptionSection = TryReadExceptionVariable(thread);
            if (exceptionSection is not null)
                result["exception"] = exceptionSection;

            result["locals"] = new List<DebugVariable>();
            result["arguments"] = new List<DebugVariable>();

            if (thread.ActiveFrame is not CorDebugILFrame ilf) return (IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>)result;

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

            return (IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>)result;
        }, ct);

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
                .Select(b => new BreakpointSnapshot(b.Id, b.ModuleName, b.MethodToken, b.IlOffset)).ToList())));

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

