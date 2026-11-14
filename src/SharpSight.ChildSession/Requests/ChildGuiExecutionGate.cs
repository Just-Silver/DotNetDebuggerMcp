namespace SharpSight.ChildSession.Requests;

public sealed class ChildGuiExecutionGate(TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<Lease?> _context = new();
    private Lease? _current;
    private volatile bool _stopping;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> body,
        DateTimeOffset deadline, CancellationToken ct)
    {
        if (_stopping) throw new InvalidOperationException("GUI执行器正在停止。");
        var remaining = deadline - _time.GetUtcNow();
        if (deadline != DateTimeOffset.MaxValue && remaining <= TimeSpan.Zero)
            throw new OperationCanceledException("排队截止，未执行。", ct);
        using var deadlineBudget = deadline == DateTimeOffset.MaxValue
            ? new CancellationTokenSource() : new CancellationTokenSource(remaining, _time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineBudget.Token);
        var token = budget.Token;
        token.ThrowIfCancellationRequested();
        if (_context.Value is { Active: true } inherited && ReferenceEquals(inherited, _current))
            return await body(token);
        if (Volatile.Read(ref _current) is { Active: true, Token.IsCancellationRequested: true })
            throw new InvalidOperationException("GUI仍在完成先前操作，请使用child_session requests/result查询，不要重试变更。");
        await _gate.WaitAsync(token);
        var lease = new Lease(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_stopping) throw new InvalidOperationException("GUI执行器正在停止。");
            _context.Value = lease;
            Volatile.Write(ref _current, lease);
            return await body(token);
        }
        finally
        {
            lease.Active = false;
            Volatile.Write(ref _current, null);
            _context.Value = null;
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _gate.WaitAsync();
        _gate.Release();
    }

    private sealed class Lease(CancellationToken token)
    {
        internal CancellationToken Token { get; } = token;
        internal volatile bool Active = true;
    }
}
