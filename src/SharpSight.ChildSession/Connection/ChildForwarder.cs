using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;

namespace SharpSight.ChildSession.Connection;

/// <summary>
/// 持有器侧请求转发：读取实例登记后经真实命名管道发往 child worker。
/// 连接态与 WTS 会话查询由调用方提供，控制通道（child_session 与 child_process 的查询/取消）旁路连接校验。
/// </summary>
public sealed class ChildForwarder(
    ChildInstanceStore store,
    Func<CancellationToken, Task<bool>> connected,
    Func<int?> currentChild,
    Func<int, bool> active)
{
    public async Task<ChildReply> ForwardAsync(ChildRequest request, CancellationToken ct)
    {
        var state = await store.ReadAsync(request.Instance, ct);
        if (state.Worker is null) return ChildReply.Create(request, "not-executed", "worker尚未就绪。");
        if (!IsControl(request) && (state.State != "Ready" || !await connected(ct) ||
            currentChild() != state.Identity.ChildSessionId || !active(state.Identity.ChildSessionId)))
            return ChildReply.Create(request, "not-executed", "子会话未连接或尚不可交互，不回退主会话。");
        return await new ChildPipeClient(state.WorkerPipe, state.Secret, state.Worker).SendAsync(request, ct);
    }

    private static bool IsControl(ChildRequest request)
    {
        if (request.Operation == "child_session") return true;
        if (request.Operation != "child_process") return false;
        var action = request.Arguments.TryGetProperty("action", out var value) ? value.GetString() : "";
        // 缺省与 worker 一致按 status 处理，否则改走 GUI 连接校验会拒绝控制查询。
        return action is null or "" or "list" or "status" or "output" or "cancel";
    }
}
