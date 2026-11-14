using SharpSight.ChildSession.Contracts;

namespace SharpSight.ChildSession.Requests;

public sealed class ChildRequestExecutor(RequestJournal journal, TimeProvider? timeProvider = null, bool serialize = true)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ChildReply> ExecuteAsync(ChildRequest request,
        Func<CancellationToken, Task<ChildReply>> body, CancellationToken cancellationToken = default)
    {
        RequestJournal.Entry entry;
        try
        {
            var accepted = journal.Accept(request);
            entry = accepted.Entry;
            if (accepted.Added) _ = RunAsync(entry, body, cancellationToken);
        }
        catch (Exception ex)
        {
            return ChildReply.Create(request, "not-executed", ex.Message);
        }
        try
        {
            var remaining = request.Deadline - _time.GetUtcNow() + TimeSpan.FromSeconds(5);
            if (remaining <= TimeSpan.Zero) return journal.Find(request.RequestId);
            return await entry.Completion.Task.WaitAsync(remaining, _time, cancellationToken);
        }
        catch (TimeoutException) { return journal.Find(request.RequestId); }
        catch (OperationCanceledException) { return journal.Find(request.RequestId); }
    }

    private async Task RunAsync(RequestJournal.Entry entry,
        Func<CancellationToken, Task<ChildReply>> body, CancellationToken callerToken)
    {
        var request = entry.Request;
        var remaining = request.Deadline - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            journal.Set(entry, ChildReply.Create(request, "not-executed", "排队截止，未执行。"), true);
            return;
        }
        using var deadline = new CancellationTokenSource(remaining, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, callerToken);
        var entered = false;
        var started = false;
        try
        {
            if (serialize)
            {
                await _gate.WaitAsync(linked.Token);
                entered = true;
            }
            linked.Token.ThrowIfCancellationRequested();
            started = true;
            journal.Set(entry, ChildReply.Create(request, "running", "已开始，最终结果尚未确认。"), false);
            var reply = await body(linked.Token);
            journal.Set(entry, reply, true);
        }
        catch (OperationCanceledException)
        {
            journal.Set(entry, ChildReply.Create(request, started ? "cancelled" : "not-executed",
                started ? "已中止后续操作，已有副作用不回滚。" : "排队已取消，未执行。"), true);
        }
        catch (Exception ex) { journal.Set(entry, ChildReply.Create(request, "failed", ex.Message), true); }
        finally { if (entered) _gate.Release(); }
    }
}
