using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;

namespace SharpSight.ChildSession.Connection;

/// <summary>
/// 持有器侧请求转发：读取实例登记后经真实命名管道发往 child worker。
/// 连接态与 WTS 会话查询由调用方提供，控制通道（child_session 与 child_process 的查询/取消）旁路连接校验；
/// child_process start 仍走连接校验，但不触发连接窗还原。
/// GUI 操作在投递前确认本实例连接窗非最小化；未确认则不投递、不回退父会话。
/// </summary>
public sealed class ChildForwarder(
    ChildInstanceStore store,
    Func<CancellationToken, Task<bool>> connected,
    Func<int?> currentChild,
    Func<int, bool> active,
    Func<CancellationToken, Task<bool>> ensureVisibleWithoutActivation)
{
    private const string WindowNotRestored = "连接窗口未能自动还原为非最小化，未执行；不回退主会话。";
    private readonly Func<CancellationToken, Task<bool>> _ensureVisibleWithoutActivation = ensureVisibleWithoutActivation;

    public async Task<ChildReply> ForwardAsync(ChildRequest request, CancellationToken ct)
    {
        var state = await store.ReadAsync(request.Instance, ct);
        if (state.Worker is not { } worker) return ChildReply.Create(request, "not-executed", "worker尚未就绪。");
        if (IsControl(request)) return await SendAsync(state, worker, request, ct);
        if (state.State != "Ready" || !await connected(ct) ||
            currentChild() != state.Identity.ChildSessionId || !active(state.Identity.ChildSessionId))
            return ChildReply.Create(request, "not-executed", "子会话未连接或尚不可交互，不回退主会话。");
        if (ChildWire.NeedsInteractiveWindow(request.Operation) &&
            await WindowProblemAsync(request, ct) is { } problem)
            return ChildReply.Create(request, "not-executed", problem);
        return await SendAsync(state, worker, request, ct);
    }

    /// <summary>
    /// 还原等待只收紧到请求自身截止；2 秒上限由窗口层唯一预算（ChildConnectionWindowRestore.Budget）负责。
    /// 取消/超时只放弃等待并撤销 STA 侧待执行回调，使其不在放弃后才改窗口。过期请求不再花时间还原。
    /// 返回 null 表示已确认可用；否则为中文未执行原因（保留原生失败的可诊断信息）。
    /// </summary>
    private async Task<string?> WindowProblemAsync(ChildRequest request, CancellationToken ct)
    {
        var remaining = request.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "请求已超过截止时间，未尝试还原连接窗口，未执行；不回退主会话。";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(remaining);
        try
        {
            return await _ensureVisibleWithoutActivation(budget.Token) ? null : WindowNotRestored;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return "连接窗口还原等待超时，未执行；不回退主会话。"; }
        catch (Exception ex)
        {
            return $"连接窗口还原失败（{ex.GetType().Name}：{ex.Message}），未执行；不回退主会话。";
        }
    }

    private static Task<ChildReply> SendAsync(ChildBootstrap state, ChildPeer worker, ChildRequest request,
        CancellationToken ct)
        => new ChildPipeClient(state.WorkerPipe, state.Secret, worker).SendAsync(request, ct);

    private static bool IsControl(ChildRequest request)
    {
        if (request.Operation == "child_session") return true;
        if (request.Operation != "child_process") return false;
        var action = request.Arguments.TryGetProperty("action", out var value) ? value.GetString() : "";
        // 缺省与 worker 一致按 status 处理，否则改走 GUI 连接校验会拒绝控制查询。
        return action is null or "" or "list" or "status" or "output" or "cancel";
    }
}
