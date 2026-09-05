using DotNetDebugger.Engine;
using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Session;

/// <summary>活动调试会话 = Engine DebugSession + 事件缓冲。</summary>
public sealed class ActiveDebugSession : IAsyncDisposable
{
    public DebugSession Session { get; }
    public SessionEventBuffer Buffer { get; }
    public AgentActionLog Actions { get; }

    internal ActiveDebugSession(DebugSession session, SessionEventBuffer buffer, AgentActionLog actions)
    {
        Session = session;
        Buffer = buffer;
        Actions = actions;
    }

    public async ValueTask DisposeAsync()
    {
        await Buffer.DisposeAsync();
        await Session.DisposeAsync();
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
        };
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动目标进程：{exePath}");
        // 排空 stdout/stderr 防管道阻塞
        process.OutputDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 等目标进入 Main 稳定区（CLR 已加载、模块可枚举）——固定等 1s；期间退出则报错
        await Task.Delay(1000, ct);
        if (process.HasExited)
            throw new InvalidOperationException(
                $"目标进程过早退出（{exePath}）。调试目标需有启动延迟（attach 窗口），如 DebugTarget 可传 delay 参数。");

        return await AttachAsync(process.Id, ct);
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

    private ActiveDebugSession Activate(DebugSession session, string target)
    {
        var buffer = new SessionEventBuffer();
        buffer.Start(session);
        var active = new ActiveDebugSession(session, buffer, Actions);
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
        return active;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
