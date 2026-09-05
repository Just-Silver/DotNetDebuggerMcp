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

    /// <summary>启动新进程并附加（早期断点能力）。commandLine 为目标可执行文件路径（可带参数）。</summary>
    public static Task<DebugSession> LaunchAsync(string commandLine, int timeoutMs = 15000, string? workingDirectory = null,
        CancellationToken ct = default)
        => CreateAsync(core => core.LaunchAsync(commandLine, timeoutMs, workingDirectory, ct), ct);

    /// <summary>附加到已运行进程。</summary>
    public static Task<DebugSession> AttachAsync(int processId, CancellationToken ct = default)
        => CreateAsync(core => core.AttachAsync(processId, ct), ct);

    private static async Task<DebugSession> CreateAsync(Func<DebugEngineCore, Task> start, CancellationToken ct)
    {
        var core = new DebugEngineCore();
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

    /// <summary>设置断点（模块名须与运行时模块一致；token 取 signature 行尾或 #MEMBER 的 token）。</summary>
    public Task<DebugBreakpoint> SetBreakpointAsync(string moduleName, int methodToken, int ilOffset, CancellationToken ct = default)
        => _core.SetBreakpointAsync(moduleName, methodToken, ilOffset, ct);

    /// <summary>当前登记断点快照（含未绑定模块的；Web 监视器红点渲染用）。</summary>
    public Task<IReadOnlyList<DebugBreakpoint>> GetBreakpointsAsync(CancellationToken ct = default)
        => _core.GetBreakpointsAsync(ct);

    /// <summary>模块短名（或全路径）→ 模块全路径（磁盘文件定位，停点无条件跟随用；未登记返回 null）。</summary>
    public Task<string?> GetModulePathAsync(string moduleName, CancellationToken ct = default)
        => _core.GetModulePathAsync(moduleName, ct);

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

    // ---- 异常断点 ----

    /// <summary>设置 first-chance 异常断点（typeName 空 = 全部异常停下）。</summary>
    public Task SetExceptionBreakpointAsync(string? typeName = null, CancellationToken ct = default)
        => _core.SetExceptionFilterAsync(new ExceptionBreakpointFilter(typeName), ct);

    /// <summary>清除异常断点（全部异常放行）。</summary>
    public Task ClearExceptionBreakpointsAsync(CancellationToken ct = default)
        => _core.SetExceptionFilterAsync(null, ct);

    public ValueTask DisposeAsync() => _core.DisposeAsync();
}
