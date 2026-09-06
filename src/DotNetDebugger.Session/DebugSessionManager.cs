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

    internal ActiveDebugSession(DebugSession session, SessionEventBuffer buffer, AgentActionLog actions,
        ProcessOutputCapture? output = null, System.Diagnostics.Process? process = null)
    {
        Session = session;
        Buffer = buffer;
        Actions = actions;
        Output = output;
        _process = process;
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
        var session = await DebugSession.LaunchAsync(commandLine, timeoutSeconds * 1000, null, ct).ConfigureAwait(false);
        return Activate(session, $"launch {commandLine}");
    }

    /// <summary>附加到已运行进程。</summary>
    public async Task<ActiveDebugSession> AttachAsync(int processId, CancellationToken ct = default)
    {
        var session = await DebugSession.AttachAsync(processId, ct).ConfigureAwait(false);
        return Activate(session, $"attach pid={processId}");
    }

    /// <summary>
    /// v1 启动并附加：内部启动目标进程（等待进入 Main 稳定区让模块加载），再 attach——绕开引擎 launch 路径
    /// 「进程停初始点但目标模块未加载、断点设不了」的时序（引擎 launch 早期断点/pending 绑定列 v2）。
    /// 目标需有启动延迟（如 DebugTarget 的 delay 参数）提供 attach 窗口。
    /// </summary>
    public async Task<ActiveDebugSession> LaunchAndAttachAsync(string commandLine, CancellationToken ct = default)
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
        var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动目标进程：{exePath}");
        // 捕获目标输出到环形缓冲（供 debug_output / debug_wait 附带返回），同时保持续读排空防管道阻塞。
        // Process 挂到 ActiveDebugSession（会话释放时 Dispose 停止读取），不再 using var 提前断流。
        var output = new ProcessOutputCapture();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Append(ProcessOutputStream.Stdout, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Append(ProcessOutputStream.Stderr, e.Data); };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try { output.AppendSystem($"[进程已退出 exitCode={process.ExitCode}]"); } catch { /* 会话已释放等场景忽略 */ }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 等目标进入 Main 稳定区（CLR 已加载、模块可枚举）——固定等 1s；期间退出则报错
        await Task.Delay(1000, ct);
        if (process.HasExited)
        {
            var tailText = string.Join(" / ", output.Tail(5).Select(l => l.Text));
            throw new InvalidOperationException(
                $"目标进程过早退出（{exePath}）。调试目标需有启动延迟（attach 窗口），如 DebugTarget 可传 delay 参数。"
                + (tailText.Length > 0 ? $" 目标输出：{tailText}" : ""));
        }

        DebugSession engineSession;
        try
        {
            engineSession = await DebugSession.AttachAsync(process.Id, ct).ConfigureAwait(false);
        }
        catch
        {
            // attach 失败也须释放进程句柄（停止输出读取），目标进程本身继续运行
            process.Dispose();
            throw;
        }
        return Activate(engineSession, $"launch+attach {commandLine}", output, process);
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
        ProcessOutputCapture? output = null, System.Diagnostics.Process? process = null)
    {
        var buffer = new SessionEventBuffer();
        buffer.Start(session);
        var active = new ActiveDebugSession(session, buffer, Actions, output, process);
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
}
