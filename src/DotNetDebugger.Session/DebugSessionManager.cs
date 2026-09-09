using ClrDebug;
using DotNetDebugger.Engine.Engine;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session.Models;

namespace DotNetDebugger.Session;

/// <summary>活动调试会话 = Engine DebugSession + 事件缓冲。</summary>
public sealed class ActiveDebugSession : IAsyncDisposable
{
    public DebugSession Session { get; }
    public SessionEventBuffer Buffer { get; }
    public AgentActionLog Actions { get; }

    /// <summary>目标进程输出捕获（仅 launch 启动的会话非 null；attach 会话无法重定向已运行进程的输出）。</summary>
    public ProcessOutputCapture? Output { get; }

    /// <summary>launch 启动的目标进程句柄（会话释放时 Dispose 停止输出读取；不杀进程，目标继续独立运行）。</summary>
    private readonly System.Diagnostics.Process? _process;

    /// <summary>目标进程 pid（launch=自起进程 Id；attach=入参 pid）。供宿主显示「目标 pid」。</summary>
    public int ProcessId { get; }

    /// <summary>目标进程实际生效的工作目录（launch 会话：启动时解析的 wd，空默认=exe 所在目录；attach 会话无此概念=null）。供宿主报告。</summary>
    public string? WorkingDirectory { get; }

    /// <summary>是否 attach 建立（attach 会话拿不到退出码、无输出捕获）。</summary>
    public bool IsAttach { get; }

    /// <summary>launch 会话的目标退出码（进程退出后由 process.Exited 捕获）；attach 会话恒 null。</summary>
    public int? ExitCode { get; internal set; }

    internal ActiveDebugSession(DebugSession session, SessionEventBuffer buffer, AgentActionLog actions,
        ProcessOutputCapture? output = null, System.Diagnostics.Process? process = null, int processId = 0,
        string? workingDirectory = null, bool isAttach = false)
    {
        Session = session;
        Buffer = buffer;
        Actions = actions;
        Output = output;
        _process = process;
        ProcessId = processId > 0 ? processId : (process?.Id ?? 0);
        WorkingDirectory = workingDirectory;
        IsAttach = isAttach;
    }

    public async ValueTask DisposeAsync()
    {
        await Buffer.DisposeAsync();
        await Session.DisposeAsync();
        _process?.Dispose();
    }
}

/// <summary>
/// 调试会话管理器：v1 单活动会话。Launch/Attach 建立会话并立即启动事件缓冲；
/// MCP 工具经 <see cref="Active"/> 操作当前会话（命令异步、状态查询走 Buffer，不等停）。
/// </summary>
public sealed class DebugSessionManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private ActiveDebugSession? _active;

    /// <summary>当前活动会话（无则 null）。</summary>
    public ActiveDebugSession? Active { get { lock (_gate) return _active; } }

    /// <summary>agent 轨迹日志（跨会话累积，P4 Web 回放源）。</summary>
    public AgentActionLog Actions { get; } = new();

    /// <summary>活动会话变更事件（新会话激活/关闭/替换后触发，参数为最新活动会话或 null）。
    /// Web 页面据此重订阅会话事件推送；订阅方自行切线程。</summary>
    public event Action<ActiveDebugSession?>? ActiveSessionChanged;

    /// <summary>启动新进程并附加（async 返回，不等停点；超时秒由调用方传）。</summary>
    public async Task<ActiveDebugSession> LaunchAsync(string commandLine, int timeoutSeconds, CancellationToken ct = default)
    {
        var session = await DebugSession.LaunchAsync(commandLine, timeoutSeconds * 1000, null,
            ExpressionConditionEvaluator.Instance, ct: ct,
            sourceLineResolver: SourceLineBreakpointResolver.Instance).ConfigureAwait(false);
        return Activate(session, $"launch {commandLine}");
    }

    /// <summary>附加到已运行进程。</summary>
    public async Task<ActiveDebugSession> AttachAsync(int processId, CancellationToken ct = default)
    {
        var session = await DebugSession.AttachAsync(processId, ExpressionConditionEvaluator.Instance, ct: ct,
            sourceLineResolver: SourceLineBreakpointResolver.Instance).ConfigureAwait(false);
        return Activate(session, $"attach pid={processId}", processId: processId, isAttach: true);
    }

    /// <summary>
    /// v1 启动并附加：内部启动目标进程（重定向并捕获输出），蹲守 CLR 启动（RegisterForRuntimeStartup，
    /// 回调时机=运行时初始化完成、Main 执行前）后立即 attach——进程停在 Main 前的初始同步点。
    /// 目标**无需自带启动延迟**（P9 以蹲守替换旧「固定等 1s」延迟窗口）；attach 后 agent 从容设断点
    /// （模块未加载登记 pending，加载后自动绑定）再 continue。
    /// workingDirectory：目标进程工作目录（默认空=目标 exe 所在目录，对齐手动启动 exe——目标产物写自己目录、
    /// 天然隔离；Web 应用等以 CWD 作 ContentRoot 的目标仍应显式传 bin/publish 目录以防静态资源定位错乱——R1）；
    /// environment：附加环境变量（KEY=VALUE 多行或分号组合，默认=继承 server 环境）。
    /// </summary>
    public async Task<ActiveDebugSession> LaunchAndAttachAsync(string commandLine, int timeoutSeconds = 30,
        string workingDirectory = "", string environment = "", CancellationToken ct = default)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exePath = Path.GetFullPath(parts[0]);
        var args = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";

        var psi = new System.Diagnostics.ProcessStartInfo(exePath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 调试目标后台运行：隐藏控制台窗口（避免每次启动弹命令框；输出已重定向不丢失）
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        var effectiveWd = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(exePath)!   // 空默认 = exe 所在目录（对齐手动启动 exe）
            : Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(effectiveWd))
            throw new InvalidOperationException($"工作目录不存在：{effectiveWd}");
        psi.WorkingDirectory = effectiveWd;
        if (!string.IsNullOrWhiteSpace(environment))
            ApplyEnvironment(psi, environment);
        var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动目标进程：{exePath}");
        // 捕获目标输出到环形缓冲（供 debug_output / debug_wait 附带返回），同时保持续读排空防管道阻塞。
        // Process 挂到 ActiveDebugSession（会话释放时 Dispose 停止读取），不再 using var 提前断流。
        var output = new ProcessOutputCapture();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Append(ProcessOutputStream.Stdout, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Append(ProcessOutputStream.Stderr, e.Data); };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 蹲守 CLR 启动（P9）：回调在运行时线程触发，经 TCS 切回调用方上下文。
        // dbgshim CreateProcessForLaunch 不支持输出重定向（会污染 MCP stdio），故原生 launch 路径不可用，
        // 必须自起进程 + 本蹲守机制。回调已触发后 unregister 属无害失败，统一吞掉。
        var shim = DbgShimLoader.Load();
        var runtimeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PSTARTUP_CALLBACK onStart = (_, _, _) => runtimeStarted.TrySetResult(true);
        var unregisterToken = shim.RegisterForRuntimeStartup(process.Id, onStart);
        try
        {
            await runtimeStarted.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { shim.TryUnregisterForRuntimeStartup(unregisterToken); } catch { /* 已触发/已退出：忽略 */ }
            var tailText = string.Join(" / ", output.Tail(5).Select(l => l.Text));
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"目标进程过早退出（{exePath}），未等到 CLR 启动。"
                    + (tailText.Length > 0 ? $" 目标输出：{tailText}" : ""));
            throw new TimeoutException($"等待目标进程 CLR 启动超时（{timeoutSeconds}s，pid={process.Id}）。");
        }
        try { shim.TryUnregisterForRuntimeStartup(unregisterToken); } catch { /* 已触发后再注销属预期失败：忽略 */ }

        DebugSession engineSession;
        try
        {
            engineSession = await DebugSession.AttachAsync(process.Id, ExpressionConditionEvaluator.Instance, ct: ct,
                sourceLineResolver: SourceLineBreakpointResolver.Instance).ConfigureAwait(false);
        }
        catch
        {
            // attach 失败也须释放进程句柄（停止输出读取），目标进程本身继续运行
            process.Dispose();
            throw;
        }
        // 退出码捕获（2026-09-09 审查修正）：Activate 返回后（目标冻结在 Main 前，进程不可能已退出）注册处理器，
        // 闭包捕获 active——不在早注册的处理器里经 Manager.Active（会话被替换后会把旧进程退出码写到新会话，竞态且语义错）。
        // 原 [进程已退出 exitCode=N] 标记逻辑随之下移此处（避免双写重复行）；输出读取排空保持原处。
        var active = Activate(engineSession, $"launch+attach {commandLine}", output, process, workingDirectory: effectiveWd);
        process.Exited += (_, _) =>
        {
            try { if (active.ExitCode is null) active.ExitCode = process.ExitCode; } catch { /* 会话已释放忽略 */ }
            try { output.AppendSystem($"[进程已退出 exitCode={process.ExitCode}]"); } catch { }
        };
        return active;
    }

    /// <summary>关闭活动会话（断开调试，进程继续独立运行）。</summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        ActiveDebugSession? toClose;
        lock (_gate) { toClose = _active; _active = null; }
        if (toClose is not null)
        {
            try { await toClose.Session.DisconnectAsync(ct); } catch { }
            await toClose.DisposeAsync();
        }
        ActiveSessionChanged?.Invoke(null);
    }

    /// <summary>会话摘要（供 debug_state）。无活动会话返回 null。</summary>
    public DebugSessionInfo? GetInfo()
    {
        var active = Active;
        if (active is null) return null;
        return new DebugSessionInfo(
            active.Session.SessionId,
            "active",
            active.Buffer.CurrentState,
            DateTimeOffset.UtcNow,
            active.Buffer.LastStop,
            BreakpointCount(active));
    }

    private static int BreakpointCount(ActiveDebugSession active) => 0; // v1 断点计数由工具层维护，此处占位

    private ActiveDebugSession Activate(DebugSession session, string target,
        ProcessOutputCapture? output = null, System.Diagnostics.Process? process = null, int processId = 0,
        string? workingDirectory = null, bool isAttach = false)
    {
        var buffer = new SessionEventBuffer();
        buffer.Start(session);
        var active = new ActiveDebugSession(session, buffer, Actions, output, process, processId, workingDirectory, isAttach);
        ActiveDebugSession? old;
        lock (_gate) { old = _active; _active = active; }
        // 替换旧活动会话：后台断开+释放，不阻塞新会话建立
        if (old is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await old.Session.DisconnectAsync(); } catch { }
                await old.DisposeAsync();
            });
        }
        ActiveSessionChanged?.Invoke(active);
        return active;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    /// <summary>把 KEY=VALUE 多行/分号组合的附加环境变量写入 psi（空行/无 = 的项忽略）。</summary>
    private static void ApplyEnvironment(System.Diagnostics.ProcessStartInfo psi, string environment)
    {
        foreach (var raw in environment.Replace(';', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue; // 无 KEY= 形式：忽略
            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            psi.Environment[key] = value;
        }
    }
}
