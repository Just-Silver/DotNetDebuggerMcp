using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Requests;

namespace SharpSight.ChildSession.Lifecycle;

public sealed class ChildWorkerRuntime(ChildInstanceStore store)
{
    private readonly RequestJournal _journal = new();

    public async Task RunAsync(string instance,
        Func<ChildBootstrap, ChildRequest, CancellationToken, Task<ChildReply>> dispatcher, CancellationToken ct)
    {
        var state = await store.ReadAsync(instance, ct);
        var peer = ChildPeer.Current;
        if (peer.SessionId == 0 || peer.SessionId == state.Identity.ParentSessionId ||
            peer.SessionId != state.Identity.ChildSessionId || peer.Sid != state.Identity.Sid ||
            !state.Holder.Matches(ChildPeer.Capture(state.Holder.ProcessId)))
            throw new InvalidOperationException("worker实际身份与已投送子会话不符。");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var executor = new ChildRequestExecutor(_journal, serialize: false);
        var server = new ChildPipeServer(state.WorkerPipe, instance, state.Secret, peer, state.Holder);
        var serving = server.RunAsync((request, token) => HandleAsync(state, request, dispatcher, executor, token), stop.Token);
        try
        {
            await server.Ready;
            await using (var lease = await store.AcquireLifecycleAsync(state.Identity.ParentSessionId, ct))
            {
                var latest = await store.ReadAsync(instance, ct);
                if (latest.State is "Stopping" or "Stopped") return;
                await store.WriteAsync(latest with { Worker = peer, State = "Ready", Reason = "子会话worker已就绪。",
                    Identity = latest.Identity with { WorkerPid = peer.ProcessId, WorkerStartedTicks = peer.StartedTicks } }, ct);
            }
            while (!stop.IsCancellationRequested)
            {
                var latest = await store.ReadAsync(instance, stop.Token);
                if (latest.State is "Stopping" or "Stopped" ||
                    !latest.Holder.Matches(ChildPeer.Capture(latest.Holder.ProcessId))) return;
                await Task.Delay(200, stop.Token);
            }
        }
        finally { await stop.CancelAsync(); await serving; }
    }

    private Task<ChildReply> HandleAsync(ChildBootstrap state, ChildRequest request,
        Func<ChildBootstrap, ChildRequest, CancellationToken, Task<ChildReply>> dispatcher,
        ChildRequestExecutor executor, CancellationToken ct)
    {
        if (request.Operation == "child_session")
        {
            var action = request.Arguments.TryGetProperty("action", out var value) ? value.GetString() : "";
            var data = action switch
            {
                "requests" => _journal.List(request.Arguments.TryGetProperty("cursor", out var cursor) ? cursor.GetInt64() : 0,
                    request.Arguments.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 20),
                "result" => System.Text.Json.JsonSerializer.SerializeToElement(_journal.Find(
                    request.Arguments.GetProperty("requestId").GetString() ?? ""), ChildWire.Json),
                _ => System.Text.Json.JsonSerializer.SerializeToElement(new { state = "Ready", process = ChildPeer.Current })
            };
            return Task.FromResult(ChildReply.Create(request, "completed", "子会话请求查询。", data));
        }
        if (request.Operation == "child_process" && request.Arguments.TryGetProperty("action", out var processAction) &&
            processAction.GetString() is "list" or "status" or "output")
            return dispatcher(state, request, ct);
        return executor.ExecuteAsync(request, token => dispatcher(state, request, token), ct);
    }
}
