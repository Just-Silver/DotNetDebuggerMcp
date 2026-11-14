using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;

namespace SharpSight.ChildSession.Lifecycle;

public sealed class ChildSessionClient(ChildInstanceStore store, ChildHostCommand command)
{
    private readonly ChildSessionPlatform _platform = new(command);

    public async Task<ChildReply> SendAsync(ChildRequest request, CancellationToken ct = default)
    {
        try
        {
            var action = request.Arguments.TryGetProperty("action", out var value) ? value.GetString() : "status";
            if (request.Operation == "child_session" && action is not ("requests" or "result"))
            {
                var result = await new ChildCoordinator(store, _platform).HandleAsync(request, ct);
                if (action == "start" && result.Status == "completed" && !string.IsNullOrEmpty(result.Instance))
                    return await WaitForStartAsync(request with { Instance = result.Instance }, ct);
                return result;
            }
            if (string.IsNullOrWhiteSpace(request.Instance))
                return ChildReply.Create(request, "not-executed", "必须显式指定instance，不会回退主会话。");
            var state = await store.ReadAsync(request.Instance, ct);
            if (state.Identity.ParentSessionId != _platform.Current.SessionId || state.Identity.Sid != _platform.Current.Sid ||
                !_platform.IsAlive(state.Holder))
                return ChildReply.Create(request, "not-executed", "实例归属或持有器身份失效。");
            return await new ChildPipeClient(state.HolderPipe, state.Secret, state.Holder).SendAsync(request, ct);
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "unknown", "等待已取消，请按实例查询状态。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", $"子会话调用失败：{ex.Message}"); }
    }

    private async Task<ChildReply> WaitForStartAsync(ChildRequest request, CancellationToken ct)
    {
        var coordinator = new ChildCoordinator(store, _platform);
        var statusRequest = request with { Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { action = "status" }) };
        while (true)
        {
            var result = await coordinator.HandleAsync(statusRequest, ct);
            if (result.Status != "completed" || !result.Data.TryGetProperty("state", out var state) ||
                state.GetString() is "Ready" or "Disconnected" or "Faulted" or "Stopped" ||
                DateTimeOffset.UtcNow >= request.Deadline) return result;
            await Task.Delay(200, ct);
        }
    }
}
