using DotNetDebugger.Engine.Engine;
using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Engine.Session;

/// <summary>
/// 一个被调试目标的会话（v1 单活动会话；spec §4.1）。外部 API：Launch/Attach/Disconnect/Dispose +
/// 断点/执行控制/状态读取；事件经 <see cref="Events"/>（Channel 异步序列）订阅。
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly DebugEngineCore _core;

    private DebugSession(DebugEngineCore core) => _core = core;

    /// <summary>启动新进程并附加（早期断点能力）。commandLine 为目标可执行文件路径（可带参数）。
    /// P7：conditionEvaluator 为条件断点求值器（Session 注入 P6 表达式求值；null=会话不支持条件断点）。
    /// R6：sourceLineResolver 为源行断点解析器（Session 注入 PDB 源行解析；null=会话不支持 sourcePath+line 延迟断点）。</summary>
    public static Task<DebugSession> LaunchAsync(string commandLine, int timeoutMs = 15000, string? workingDirectory = null,
        IBreakpointConditionEvaluator? conditionEvaluator = null, CancellationToken ct = default,
        ISourceLineBreakpointResolver? sourceLineResolver = null)
        => CreateAsync(core => core.LaunchAsync(commandLine, timeoutMs, workingDirectory, ct), conditionEvaluator, sourceLineResolver, ct);

    /// <summary>附加到已运行进程。P7/R6：注入参数同 LaunchAsync。</summary>
    public static Task<DebugSession> AttachAsync(int processId, IBreakpointConditionEvaluator? conditionEvaluator = null, CancellationToken ct = default,
        ISourceLineBreakpointResolver? sourceLineResolver = null)
        => CreateAsync(core => core.AttachAsync(processId, ct), conditionEvaluator, sourceLineResolver, ct);

    private static async Task<DebugSession> CreateAsync(Func<DebugEngineCore, Task> start, IBreakpointConditionEvaluator? conditionEvaluator, ISourceLineBreakpointResolver? sourceLineResolver, CancellationToken ct)
    {
        var core = new DebugEngineCore(conditionEvaluator, sourceLineResolver);
        await start(core).ConfigureAwait(false);
        return new DebugSession(core);
    }

    /// <summary>会话 id（DebugEvent.SessionId）。</summary>
    public string SessionId => _sessionId;

    /// <summary>事件异步序列（Channel 读端，含缓冲事件）。</summary>
    public IAsyncEnumerable<DebugEvent> Events => _core.Events.ReadAllAsync();

    // ---- 执行控制 ----

    /// <summary>继续执行（进程停在断点/异常/步完成后调用）。</summary>
    public Task ContinueAsync(CancellationToken ct = default) => _core.ContinueAsync(ct);

    /// <summary>暂停/断开：detach 调试器（目标进程继续独立运行）。</summary>
    public Task DisconnectAsync(CancellationToken ct = default) => _core.DisconnectAsync(ct);

    // ---- 断点 ----

    /// <summary>设置断点（模块名须与运行时模块一致；token 取 signature 行尾或 #MEMBER 的 token）。
    /// P5：hitCount=第 N 次起生效（默认 1）；mode=Stop 命中停 / Trace 命中不停记轨迹。
    /// P7：condition=P6 表达式子集条件（非空要求会话已注入求值器，否则抛中文提示）。</summary>
    public Task<DebugBreakpoint> SetBreakpointAsync(string moduleName, int methodToken, int ilOffset, int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop, string? condition = null, CancellationToken ct = default)
        => _core.SetBreakpointAsync(moduleName, methodToken, ilOffset, hitCount, mode, condition, ct);

    /// <summary>设置源行型断点（R6，sourcePath+line 按 PDB 解析绑定）：模块未加载登记 pending、加载后自动补设。
    /// moduleName 非空=限该模块；空=任意模块。要求会话创建时注入 ISourceLineBreakpointResolver（Session 库工厂默认注入）。</summary>
    public Task<DebugBreakpoint> SetSourceLineBreakpointAsync(string sourcePath, int line, string moduleName = "", int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop, string? condition = null, CancellationToken ct = default)
        => _core.SetSourceLineBreakpointAsync(sourcePath, line, moduleName, hitCount, mode, condition, ct);

    /// <summary>当前登记断点快照（含未绑定模块的；Web 监视器红点渲染用）。</summary>
    public Task<IReadOnlyList<DebugBreakpoint>> GetBreakpointsAsync(CancellationToken ct = default)
        => _core.GetBreakpointsAsync(ct);

    /// <summary>模块短名（或全路径）→ 模块全路径（磁盘文件定位，停点无条件跟随用；未登记返回 null）。</summary>
    public Task<string?> GetModulePathAsync(string moduleName, CancellationToken ct = default)
        => _core.GetModulePathAsync(moduleName, ct);

    /// <summary>已加载模块快照（短名 → 磁盘路径；行断点跨模块解析用）。</summary>
    public Task<IReadOnlyList<(string Name, string Path)>> GetModulesAsync(CancellationToken ct = default)
        => _core.GetModulesAsync(ct);

    public Task<bool> RemoveBreakpointAsync(int id, CancellationToken ct = default)
        => _core.RemoveBreakpointAsync(id, ct);

    public Task ClearBreakpointsAsync(CancellationToken ct = default)
        => _core.ClearBreakpointsAsync(ct);

    // ---- 单步 ----

    /// <summary>step into（进入被调方法）。</summary>
    public Task StepIntoAsync(CancellationToken ct = default) => _core.StepAsync(stepIn: true, ct);

    /// <summary>step over（不进入被调方法）。</summary>
    public Task StepOverAsync(CancellationToken ct = default) => _core.StepAsync(stepIn: false, ct);

    /// <summary>step out（步出当前方法到调用方）。</summary>
    public Task StepOutAsync(CancellationToken ct = default) => _core.StepAsync(stepIn: null, ct);

    // ---- 状态读取（停顿时有效） ----

    /// <summary>线程列表。</summary>
    public Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
        => _core.GetThreadsAsync(ct);

    /// <summary>指定线程的调用栈。</summary>
    public Task<IReadOnlyList<DebugStackFrame>> GetStackFramesAsync(int threadId, CancellationToken ct = default)
        => _core.GetStackFramesAsync(threadId, ct);

    /// <summary>读取指定线程栈顶帧的局部变量与参数（停顿时调用；返回 { "locals", "arguments" } 分组）。</summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>> GetVariablesAsync(int threadId, CancellationToken ct = default)
        => _core.GetVariablesAsync(threadId, ct);

    /// <summary>
    /// 按路径读值（P6 表达式读值子集的引擎底座，纯读、停顿时有效）：rootName 为栈顶帧局部/参数名
    /// （+$exception 伪根），segments 逐段字段/索引解引用——引擎直读绕开 MaxChildren 截断。
    /// 失败抛中文提示异常（附段号/类型名/可用字段清单）。
    /// </summary>
    public Task<DebugEvalResult> EvaluatePathAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, CancellationToken ct = default)
        => _core.EvaluatePathAsync(threadId, rootName, segments, ct);

    /// <summary>
    /// 按路径写值（W1 debug_set 引擎底座，停顿时有效）：与 EvaluatePathAsync 同款路径解析，
    /// 末段按 DebugWriteValue 分派——Null=引用置空 / Scalar=值类型按目标元素类型转换写 /
    /// CopyPath=引用重定向到源路径对象。返回写前/写后回显；失败抛中文提示异常。
    /// 写进程内存有崩目标风险（调用方明示），只写读链路已证明可定位的目标。
    /// </summary>
    public Task<DebugWriteResult> SetPathValueAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, DebugWriteValue value, CancellationToken ct = default)
        => _core.SetPathValueAsync(threadId, rootName, segments, value, ct);

    /// <summary>
    /// 按路径定位对象/数组并做受控递归展开（D1 debug_object 引擎底座，停顿时有效）：路径复用 P6 文法
    /// （rootName 为栈顶帧局部/参数名 + $exception 伪根），终值必须是对象/数组——null 引用/标量/字符串抛中文
    /// 「不是对象/数组」提示；children 沿引用递归展开到 depth 层（1-MaxDrillDepth 钳制），每层字段/元素上限
    /// limit（1-128 钳制），同路径环输出 &lt;cyclic&gt; 占位。
    /// </summary>
    public Task<DebugValue> ReadObjectAtPathAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, int depth, int limit = 32, CancellationToken ct = default)
        => _core.ReadObjectAtPathAsync(threadId, rootName, segments, depth, limit, ct);

    // ---- 异常断点 ----

    /// <summary>设置 first-chance 异常断点（typeName 空 = 全部异常停下）。</summary>
    public Task SetExceptionBreakpointAsync(string? typeName = null, CancellationToken ct = default)
        => _core.SetExceptionFilterAsync(new ExceptionBreakpointFilter(typeName), ct);

    /// <summary>清除异常断点（全部异常放行）。</summary>
    public Task ClearExceptionBreakpointsAsync(CancellationToken ct = default)
        => _core.SetExceptionFilterAsync(null, ct);

    public ValueTask DisposeAsync() => _core.DisposeAsync();
}
