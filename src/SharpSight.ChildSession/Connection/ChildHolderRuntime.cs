using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;

namespace SharpSight.ChildSession.Connection;

/// <summary>一轮轮询的结论；状态写入与worker投递由持有器执行。</summary>
internal enum ConnectionRound
{
    Hold,
    AwaitingAuthentication,
    Disconnected,
    LaunchWorker,
}

public sealed class ChildHolderRuntime(ChildInstanceStore store, ChildHostCommand command)
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(2);

    public async Task RunAsync(string instance, CancellationToken ct)
    {
        ChildDiagnosticLog.Attach(instance, "holder");
        var state = await WaitForRegistrationAsync(instance, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var created = new TaskCompletionSource<ChildConnectionWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new ChildConnectionWindow();
                created.SetResult(window);
                Application.Run(window);
                uiDone.TrySetResult();
            }
            catch (Exception ex) { created.TrySetException(ex); uiDone.TrySetException(ex); }
        }) { IsBackground = true, Name = "Child-RDP-STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var form = await created.Task;
        var server = new ChildPipeServer(state.HolderPipe, instance, state.Secret, ChildPeer.Current);
        var serving = server.RunAsync((request, token) => ForwardAsync(form, request, token), stop.Token);
        try
        {
            await form.ShownAndConnected.Task.WaitAsync(ct);
            await MonitorAsync(form, instance, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var reason = ex is System.ComponentModel.Win32Exception native
                ? $"{ex.Message}（Win32={native.NativeErrorCode}，HRESULT=0x{native.HResult:X8}）"
                : ex.ToString();
            await UpdateAsync(instance, s => s with { State = "Faulted", Reason = reason }, ct);
        }
        finally
        {
            form.RequestClose();
            await stop.CancelAsync();
            await serving;
            await uiDone.Task;
        }
    }

    private async Task MonitorAsync(ChildConnectionWindow form, string instance, CancellationToken ct)
    {
        var launched = false;
        var promptAfter = DateTimeOffset.UtcNow.AddSeconds(12);
        Task<RdpReadiness>? pendingRead = null;
        while (!ct.IsCancellationRequested)
        {
            var current = await store.ReadAsync(instance, ct);
            if (current.State is "Stopping" or "Stopped") return;
            // 关窗后不再等未完成的读取，也不能把关闭前的旧快照当依据。
            var readiness = form.WasClosed
                ? RdpReadiness.Unconfirmed
                : await AwaitReadinessAsync(pendingRead ??= form.ReadReadinessAsync(), ReadinessTimeout, ct);
            if (form.WasClosed || readiness.Observed) pendingRead = null;
            var round = await EvaluateConnectionRoundAsync(store, instance, readiness,
                WtsNative.ChildId, WtsNative.Active, launched, form.WasClosed, ct);
            switch (round)
            {
                case ConnectionRound.AwaitingAuthentication or ConnectionRound.Disconnected:
                    await SetStateAsync(instance, current, round, form, ct);
                    if (ShouldPromptCredential(round, form.CredentialPrompted, form.WasClosed,
                        promptAfter, DateTimeOffset.UtcNow))
                        await PromptCredentialAsync(form);
                    break;
                case ConnectionRound.LaunchWorker:
                    launched = true;
                    await ChildWorkerLauncher.LaunchAsync(store, await store.ReadAsync(instance, ct), command, ct);
                    break;
                // 投递当轮不查存活：worker记录刚写下，与启动中的进程存在竞态。
                case ConnectionRound.Hold when current.Worker is not null &&
                    !new ChildSessionPlatform(command).IsAlive(current.Worker):
                    await UpdateAsync(instance, s => s with { State = "Faulted", Reason = "worker已退出，不自动重启。" }, ct);
                    break;
            }
            await Task.Delay(200, ct);
        }
    }

    /// <summary>
    /// 持有器每轮真正使用的决策入口；登录快照由调用方在同一轮读取，测试也从这里驱动。
    /// </summary>
    internal static async Task<ConnectionRound> EvaluateConnectionRoundAsync(ChildInstanceStore store, string instance,
        RdpReadiness readiness, Func<int?> getChild, Func<int, bool> active, bool launched, bool windowClosed,
        CancellationToken ct)
    {
        // 关窗后旧快照与未完成读取都不可信，且关窗本身已确定断连。
        if (windowClosed) return ConnectionRound.Disconnected;
        if (!readiness.Observed) return ConnectionRound.Hold;
        if (readiness.LoginEventError is { } failure)
            throw new InvalidOperationException($"登录事件订阅失败，无法确认登录状态：{failure}");
        if (!readiness.Connected)
            return launched ? ConnectionRound.Disconnected : ConnectionRound.AwaitingAuthentication;
        if (launched) return ConnectionRound.Hold;
        return await ObserveConnectedChildAsync(store, instance, getChild, active, ct,
            loginCompleted: readiness.LoginCompleted) is null ? ConnectionRound.Hold : ConnectionRound.LaunchWorker;
    }

    // 认证期间STA最忙：单轮读取超时只代表本轮未确认，判故障会销毁正在认证的窗口。
    internal static async Task<RdpReadiness> AwaitReadinessAsync(Task<RdpReadiness> read, TimeSpan timeout, CancellationToken ct)
    {
        try { return await read.WaitAsync(timeout, ct); }
        catch (TimeoutException) { return RdpReadiness.Unconfirmed; }
    }

    private static async Task<int?> ObserveConnectedChildAsync(ChildInstanceStore store, string instance,
        Func<int?> getChild, Func<int, bool> active, CancellationToken ct, bool loginCompleted)
    {
        if (getChild() is not int id) return null;
        await using var lease = await store.AcquireLifecycleAsync(ChildPeer.Current.SessionId, ct);
        var current = await store.ReadAsync(instance, ct);
        if (current.State is "Stopping" or "Stopped") return null;
        if (id <= 0 || id == current.Identity.ParentSessionId ||
            current.Identity.ChildSessionId != 0 && current.Identity.ChildSessionId != id)
            throw new InvalidOperationException("连接的子会话身份已变化，拒绝接管其他会话。");
        if (current.Identity.ChildSessionId == 0)
            await store.WriteAsync(current with { Identity = current.Identity with { ChildSessionId = id } }, ct);
        if (!loginCompleted) return null;
        // ActiveX已连接与WTS会话注册并非同一时刻；尚不存在的连接态不能触发销毁认证窗口。
        try { return active(id) ? id : null; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2) { return null; }
    }

    private Task SetStateAsync(string instance, ChildBootstrap current, ConnectionRound round,
        ChildConnectionWindow form, CancellationToken ct)
    {
        var (state, reason) = round == ConnectionRound.Disconnected
            ? ("Disconnected", "连接已断开，拒绝GUI操作；请显式停止实例。")
            : ("AwaitingAuthentication", AuthenticationReason(form));
        return current.State == state
            ? Task.CompletedTask
            : UpdateAsync(instance, s => s with { State = state, Reason = reason }, ct);
    }

    private static string AuthenticationReason(ChildConnectionWindow form)
    {
        if (form.CredentialApplied)
            return "已用保存凭据自动登录，仍在等待系统认证；若持续未完成，稍后会提示更新凭据（或运行 child login）。";
        if (form.CredentialError is { } error)
            return $"自动登录凭据注入失败（{error}），改由系统连接窗口认证；可执行 child login 更新凭据。";
        if (form.CredentialPrompted)
            return "已取消自动登录凭据；可在系统连接窗口手工认证，或运行 child login 保存一次。";
        return "正在使用系统自动登录；持续等待时将弹出一次 Windows 安全凭据窗口（也可先运行 child login）。";
    }

    private static async Task PromptCredentialAsync(ChildConnectionWindow form)
    {
        try { await form.PromptCredentialAsync(); }
        catch (Exception ex) { Console.Error.WriteLine($"凭据配置失败，继续使用系统连接窗口认证：{ex.Message}"); }
    }

    /// <summary>
    /// 只在「确实停在等待认证」且本持有器尚未询问过时补配一次；断连/关窗/已询问都不再打断用户。
    /// </summary>
    internal static bool ShouldPromptCredential(ConnectionRound round, bool prompted, bool windowClosed,
        DateTimeOffset promptAfter, DateTimeOffset now)
        => round == ConnectionRound.AwaitingAuthentication && !prompted && !windowClosed && now >= promptAfter;

    private Task<ChildReply> ForwardAsync(ChildConnectionWindow form, ChildRequest request, CancellationToken ct)
        => new ChildForwarder(store, _ => form.IsConnectedAsync(), WtsNative.ChildId, WtsNative.Active,
                form.EnsureVisibleWithoutActivationAsync)
            .ForwardAsync(request, ct);

    private async Task<ChildBootstrap> WaitForRegistrationAsync(string instance, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await store.ReadAsync(instance, ct);
            if (state.Holder.Matches(ChildPeer.Current) && state.Identity.HolderPid == Environment.ProcessId) return state;
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException("未收到匹配当前进程的持有器登记。");
    }

    private async Task UpdateAsync(string instance, Func<ChildBootstrap, ChildBootstrap> change, CancellationToken ct)
    {
        await using var lease = await store.AcquireLifecycleAsync(ChildPeer.Current.SessionId, ct);
        var state = await store.ReadAsync(instance, ct);
        if (state.State is not ("Stopping" or "Stopped"))
            await store.WriteAsync(change(state), ct);
    }
}
