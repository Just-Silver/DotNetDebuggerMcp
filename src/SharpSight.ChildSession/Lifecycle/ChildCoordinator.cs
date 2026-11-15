using System.Security.Cryptography;
using System.Security.Principal;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Security;

namespace SharpSight.ChildSession.Lifecycle;

public sealed class ChildCoordinator(ChildInstanceStore store, IChildSessionPlatform platform)
{
    private const string LegalActions = "合法action：probe/start/status/stop/login/logout/requests/result。";

    public async Task<ChildReply> HandleAsync(ChildRequest request, CancellationToken ct)
    {
        try
        {
            var action = request.Arguments.TryGetProperty("action", out var value) ? value.GetString() : "status";
            return action switch
            {
                "probe" => Probe(request),
                "start" => await StartAsync(request, ct),
                "status" => await StatusAsync(request, ct),
                "stop" => await StopAsync(request, ct),
                "login" => await LoginAsync(request, ct),
                "logout" => Logout(request),
                _ => ChildReply.Create(request, "not-executed", LegalActions)
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

    private ChildReply Probe(ChildRequest request)
    {
        var system = platform.Probe();
        return ChildReply.Create(request, "completed", "系统只读查询。", new
        {
            system.Enabled,
            system.ParentSessionId,
            system.ChildSessionId,
            system.ActiveXAvailable,
            CredentialSaved = TryHasCredential(),
        });
    }

    private async Task<ChildReply> StartAsync(ChildRequest request, CancellationToken ct)
    {
        await using var lease = await store.AcquireLifecycleAsync(platform.Current.SessionId, ct);
        var existing = await FindCurrentAsync(ct);
        if (existing is null) return await CreateAsync(request, ct);
        if (existing.State == "Stopping") return State(request, existing);
        if (platform.IsAlive(existing.Holder)) return State(request, existing);
        return await RecoverAsync(request, existing, ct);
    }

    /// <summary>
    /// 持有器失效后的有界恢复：只有当前 WTS child 编号与实例登记一致才重连原实例；
    /// 没有任何 child 时结束旧实例并重建；编号不一致或无法核实则拒绝接管。
    /// </summary>
    private async Task<ChildReply> RecoverAsync(ChildRequest request, ChildBootstrap existing, CancellationToken ct)
    {
        var system = platform.Probe();
        if (!system.Enabled) throw new InvalidOperationException("子会话功能未启用，请显式运行 child enable。");
        if (!system.ActiveXAvailable) throw new InvalidOperationException("RDP ActiveX不可用。");
        if (system.ChildSessionId is int child)
        {
            if (existing.Identity.ChildSessionId != 0 && existing.Identity.ChildSessionId == child)
                return await ReconnectAsync(request, existing, ct);
            throw new InvalidOperationException("旧持有器已失效，当前存在归属不明的子会话，拒绝自动接管；请人工核对并显式停止外部会话。");
        }
        await store.WriteAsync(existing with
        {
            State = "Stopped",
            Reason = "持有器已失效且没有可复用的子会话；已结束旧实例并重建。"
        }, ct);
        return await CreateAsync(request, ct);
    }

    private async Task<ChildReply> CreateAsync(ChildRequest request, CancellationToken ct)
    {
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

    private async Task<ChildReply> ReconnectAsync(ChildRequest request, ChildBootstrap existing, CancellationToken ct)
    {
        if (existing.Worker is { } worker) await WaitForWorkerExitAsync(worker, ct);
        var holder = platform.StartHolder(store.Root, existing.Identity.Instance);
        var resumed = existing with
        {
            Holder = holder,
            Worker = null,
            State = "Starting",
            Reason = "旧持有器已失效，正在重连原自有子会话并尝试自动认证。",
            Identity = existing.Identity with
            {
                HolderPid = holder.ProcessId,
                HolderStartedTicks = holder.StartedTicks,
                WorkerPid = 0,
                WorkerStartedTicks = 0,
            }
        };
        await store.WriteAsync(resumed, ct);
        return State(request, resumed);
    }

    private async Task WaitForWorkerExitAsync(ChildPeer worker, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline && platform.IsAlive(worker))
            await Task.Delay(100, ct);
    }

    private async Task<ChildReply> StatusAsync(ChildRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Instance))
        {
            var current = await FindCurrentAsync(ct);
            return current is null
                ? ChildReply.Create(request, "completed", "当前无本产品活动实例。")
                : StatusState(request, WithHolderLiveness(current));
        }
        var state = await store.ReadAsync(request.Instance, ct);
        RequireOwner(state);
        return StatusState(request, WithHolderLiveness(state));
    }

    /// <summary>
    /// 持有器已退出时状态不能继续报 Ready；只在返回值上标记，不回写实例记录，
    /// 以免查询动作改变生命周期（恢复仍由 start 显式触发）。
    /// </summary>
    private ChildBootstrap WithHolderLiveness(ChildBootstrap state)
        => state.State is not ("Stopped" or "Stopping") && !platform.IsAlive(state.Holder)
            ? state with { State = "Faulted", Reason = "持有器已退出，结果需核对；可重新 start 触发有界恢复。" }
            : state;

    /// <summary>把会话存活、连接可交互、worker 存活拆开报告，避免把「窗口还在」当成 GUI 可用。</summary>
    private ChildReply StatusState(ChildRequest request, ChildBootstrap state)
    {
        var child = TryChildId();
        var active = child is int id ? platform.IsActive(id) : null;
        var holderAlive = platform.IsAlive(state.Holder);
        var workerAlive = state.Worker is { } worker && platform.IsAlive(worker);
        var matches = child is not null && state.Identity.ChildSessionId == child;
        var interactive = state.State == "Ready" && holderAlive && workerAlive && active == true && matches;
        return ChildReply.Create(request with { Instance = state.Identity.Instance }, "completed", state.Reason, new
        {
            state = state.State,
            identity = state.Identity,
            reason = state.Reason,
            health = new
            {
                holderAlive,
                workerAlive,
                childSessionId = child,
                childSessionMatches = matches,
                childSessionActive = active,
                guiInteractive = interactive,
                credentialSaved = TryHasCredential(),
            },
        });
    }

    private async Task<ChildReply> StopAsync(ChildRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Instance)) throw new InvalidOperationException("stop必须指定instance。");
        await using var lease = await store.AcquireLifecycleAsync(platform.Current.SessionId, ct);
        var state = await store.ReadAsync(request.Instance, ct);
        RequireOwner(state);
        if (state.State == "Stopped") return State(request, state);
        if (!platform.IsAlive(state.Holder))
        {
            if (platform.Probe().ChildSessionId is null)
            {
                state = state with { State = "Stopped", Reason = "持有器已失效且没有子会话；已结束实例记录。" };
                await store.WriteAsync(state, ct);
                return State(request, state);
            }
            throw new InvalidOperationException("无法核实持有器所有权，拒绝注销；可先用 start 触发有界重连。");
        }
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

    /// <summary>
    /// 显式配置入口：当前用户的 Windows 安全凭据窗口只出现一次，结果写入凭据管理器。
    /// 窗口不能由取消令牌关闭；截止只放弃调用方等待，完成后仍会保存。
    /// </summary>
    private static async Task<ChildReply> LoginAsync(ChildRequest request, CancellationToken ct)
    {
        try
        {
            var prompt = ChildCredentialPrompt.AskAndSaveOnStaThreadAsync(0, WindowsIdentity.GetCurrent().Name);
            var remaining = request.Deadline - DateTimeOffset.UtcNow;
            var result = remaining > TimeSpan.Zero ? await prompt.WaitAsync(remaining, ct) : await prompt;
            return ChildReply.Create(request, result.Saved ? "completed" : "not-executed", result.Message, new
            {
                userName = result.Credential?.UserName ?? "",
                domain = result.Credential?.Domain ?? "",
                saved = result.Saved,
            });
        }
        catch (TimeoutException)
        {
            return ChildReply.Create(request, "unknown",
                "凭据窗口仍在等待输入，完成后用 probe 查看 credentialSaved；未确认前不会自动重试。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ChildReply.Create(request, "unknown", "已放弃等待；凭据窗口仍可能继续，完成后用 probe 核对。");
        }
        catch (InvalidOperationException ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", $"凭据配置失败：{ex.Message}"); }
    }

    private static ChildReply Logout(ChildRequest request)
    {
        var deleted = ChildCredentialStore.Delete();
        return ChildReply.Create(request, "completed",
            deleted
                ? "已删除本产品保存的子会话凭据；RDP 自己保存的 TERMSRV 凭据未动。正在连接的实例不受影响，下次 start 会重新询问。"
                : "没有找到本产品保存的子会话凭据；RDP 自己保存的 TERMSRV 凭据未动。",
            new { deleted });
    }

    private bool TryHasCredential()
    {
        try { return ChildCredentialStore.HasSavedCredential(); }
        catch (Exception ex) { Console.Error.WriteLine($"读取凭据配置状态失败：{ex.Message}"); return false; }
    }

    private int? TryChildId()
    {
        try { return platform.Probe().ChildSessionId; }
        catch (Exception ex) { Console.Error.WriteLine($"查询子会话编号失败：{ex.Message}"); return null; }
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
