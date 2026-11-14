using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;

namespace SharpSight.ChildSession.Connection;

public sealed class ChildHolderRuntime(ChildInstanceStore store, ChildHostCommand command)
{
    public async Task RunAsync(string instance, CancellationToken ct)
    {
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
        catch (Exception ex) { await UpdateAsync(instance, s => s with { State = "Faulted", Reason = ex.Message }, ct); }
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
        while (!ct.IsCancellationRequested)
        {
            var current = await store.ReadAsync(instance, ct);
            if (current.State is "Stopping" or "Stopped") return;
            var connected = await form.IsConnectedAsync().WaitAsync(TimeSpan.FromSeconds(2), ct);
            if (!connected)
            {
                var state = launched || form.WasClosed ? "Disconnected" : "AwaitingAuthentication";
                if (current.State != state)
                    await UpdateAsync(instance, s => s with { State = state, Reason =
                        state == "Disconnected" ? "连接已断开，拒绝GUI操作；请显式停止实例。" : "等待系统认证或连接完成。" }, ct);
            }
            else if (!launched && WtsNative.ChildId() is int id && WtsNative.Active(id))
            {
                await UpdateAsync(instance, s => s with { Identity = s.Identity with { ChildSessionId = id } }, ct);
                current = await store.ReadAsync(instance, ct);
                launched = true;
                await ChildWorkerLauncher.LaunchAsync(store, current, command, ct);
            }
            else if (current.Worker is not null && !new ChildSessionPlatform(command).IsAlive(current.Worker))
            {
                await UpdateAsync(instance, s => s with { State = "Faulted", Reason = "worker已退出，不自动重启。" }, ct);
            }
            await Task.Delay(200, ct);
        }
    }

    private async Task<ChildReply> ForwardAsync(ChildConnectionWindow form, ChildRequest request, CancellationToken ct)
    {
        var state = await store.ReadAsync(request.Instance, ct);
        var control = request.Operation == "child_session";
        if (state.Worker is null) return ChildReply.Create(request, "not-executed", "worker尚未就绪。");
        if (!control && (state.State != "Ready" || !await form.IsConnectedAsync() ||
            WtsNative.ChildId() != state.Identity.ChildSessionId || !WtsNative.Active(state.Identity.ChildSessionId)))
            return ChildReply.Create(request, "not-executed", "子会话未连接或尚不可交互，不回退主会话。");
        return await new ChildPipeClient(state.WorkerPipe, state.Secret, state.Worker).SendAsync(request, ct);
    }

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
