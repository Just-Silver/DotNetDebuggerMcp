using System.Security.Cryptography;
using SharpSight.ChildSession.Contracts;

namespace SharpSight.ChildSession.Lifecycle;

public sealed class ChildCoordinator(ChildInstanceStore store, IChildSessionPlatform platform)
{
    public async Task<ChildReply> HandleAsync(ChildRequest request, CancellationToken ct)
    {
        try
        {
            var action = request.Arguments.TryGetProperty("action", out var value) ? value.GetString() : "status";
            return action switch
            {
                "probe" => ChildReply.Create(request, "completed", "系统只读查询。", platform.Probe()),
                "start" => await StartAsync(request, ct),
                "status" => await StatusAsync(request, ct),
                "stop" => await StopAsync(request, ct),
                _ => ChildReply.Create(request, "not-executed", "合法action：probe/start/status/stop/requests/result。")
            };
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "unknown", "已放弃等待，请查询实例状态。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
    }

    public async Task ChangeSettingAsync(bool enabled, CancellationToken ct)
    {
        await using var lease = await store.AcquireLifecycleAsync(platform.Current.SessionId, ct);
        if (platform.Probe().ChildSessionId is not null || await FindCurrentAsync(ct) is not null)
            throw new InvalidOperationException("仍存在子会话或建立中的实例；请先显式停止自有实例，外部会话由其所有者处理。");
        platform.SetEnabled(enabled);
    }

    private async Task<ChildReply> StartAsync(ChildRequest request, CancellationToken ct)
    {
        await using var lease = await store.AcquireLifecycleAsync(platform.Current.SessionId, ct);
        var existing = await FindCurrentAsync(ct);
        if (existing is not null)
        {
            if (!platform.IsAlive(existing.Holder))
                throw new InvalidOperationException("旧实例持有器失效，拒绝重建或接管；请先核对并清理旧实例。");
            return State(request, existing);
        }
        var system = platform.Probe();
        if (!system.Enabled) throw new InvalidOperationException("子会话功能未启用，请显式运行 child enable。");
        if (!system.ActiveXAvailable) throw new InvalidOperationException("RDP ActiveX不可用。");
        if (system.ChildSessionId is not null) throw new InvalidOperationException("已有非本产品子会话，拒绝接管。");
        var instance = Guid.NewGuid().ToString("N");
        var peer = platform.Current;
        var identity = new ChildIdentity(instance, peer.Sid, peer.SessionId, 0, 0, 0, 0, 0);
        var seed = new ChildBootstrap(identity, "SharpSight-holder-" + instance, "SharpSight-worker-" + instance,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), peer, null, "Starting", "连接建立中。");
        await store.WriteAsync(seed, ct);
        var holder = platform.StartHolder(store.Root, instance);
        seed = seed with { Holder = holder, Identity = identity with
            { HolderPid = holder.ProcessId, HolderStartedTicks = holder.StartedTicks } };
        await store.WriteAsync(seed, ct);
        return State(request, seed);
    }

    private async Task<ChildReply> StatusAsync(ChildRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Instance))
        {
            var current = await FindCurrentAsync(ct);
            return current is null ? ChildReply.Create(request, "completed", "当前无本产品活动实例。") : State(request, current);
        }
        var state = await store.ReadAsync(request.Instance, ct);
        RequireOwner(state);
        if (state.State is not ("Stopped" or "Stopping") && !platform.IsAlive(state.Holder))
            state = state with { State = "Faulted", Reason = "持有器已退出，结果需核对；不自动重建。" };
        return State(request, state);
    }

    private async Task<ChildReply> StopAsync(ChildRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Instance)) throw new InvalidOperationException("stop必须指定instance。");
        await using var lease = await store.AcquireLifecycleAsync(platform.Current.SessionId, ct);
        var state = await store.ReadAsync(request.Instance, ct);
        RequireOwner(state);
        if (state.State == "Stopped") return State(request, state);
        if (!platform.IsAlive(state.Holder)) throw new InvalidOperationException("无法核实持有器所有权，拒绝注销。");
        var child = platform.Probe().ChildSessionId;
        if (child is not null && (child != state.Identity.ChildSessionId || child == platform.Current.SessionId))
            throw new InvalidOperationException("当前child与本实例不符，拒绝注销。");
        state = state with { State = "Stopping", Reason = "正在停止输入并注销自有子会话；其中全部应用将结束。" };
        await store.WriteAsync(state, ct);
        if (child is not null) platform.RequestLogoff(child.Value);
        while (child is not null && platform.Probe().ChildSessionId == child)
        {
            if (DateTimeOffset.UtcNow >= request.Deadline) return State(request, state);
            await Task.Delay(100, ct);
        }
        platform.CloseHolder(state.Holder);
        state = state with { State = "Stopped", Reason = "自有子会话已注销，系统功能开关未修改。" };
        await store.WriteAsync(state, ct);
        return State(request, state);
    }

    private async Task<ChildBootstrap?> FindCurrentAsync(CancellationToken ct)
    {
        foreach (var state in await store.ListAsync(ct))
            if (state.Identity.Sid == platform.Current.Sid &&
                state.Identity.ParentSessionId == platform.Current.SessionId && state.State != "Stopped")
                return state;
        return null;
    }

    private void RequireOwner(ChildBootstrap state)
    {
        if (state.Identity.Sid != platform.Current.Sid || state.Identity.ParentSessionId != platform.Current.SessionId)
            throw new InvalidOperationException("实例不属于当前账户及父会话。");
    }

    private static ChildReply State(ChildRequest request, ChildBootstrap state)
        => ChildReply.Create(request with { Instance = state.Identity.Instance }, "completed", state.Reason,
            new { state = state.State, identity = state.Identity, reason = state.Reason });
}
