using System.Threading.Channels;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// 命令串行化队列：外部线程把命令投进来，调试线程逐个执行（spec §5 命令队列纪律）。
/// 每条命令带 TaskCompletionSource，执行结果回传调用方。
/// </summary>
public sealed class DebugCommandQueue : IDisposable
{
    private sealed record Command(Func<Task> Body, TaskCompletionSource Completion);

    private readonly Channel<Command> _channel = Channel.CreateUnbounded<Command>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _pump;

    public DebugCommandQueue()
    {
        _pump = Task.Run(async () =>
        {
            await foreach (var cmd in _channel.Reader.ReadAllAsync())
            {
                try { await cmd.Body(); cmd.Completion.TrySetResult(); }
                catch (Exception ex) { cmd.Completion.TrySetException(ex); }
            }
        });
    }

    /// <summary>投递命令并等待执行完成（在泵线程上执行 body）。</summary>
    public Task PostAsync(Func<Task> body, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new Command(body, completion)))
        {
            completion.TrySetException(new InvalidOperationException("命令队列已关闭"));
        }
        return completion.Task.WaitAsync(ct);
    }

    /// <summary>投递同步命令（包装为 Task）。</summary>
    public Task PostAsync(Action body, CancellationToken ct = default)
        => PostAsync(() => { body(); return Task.CompletedTask; }, ct);

    public void Dispose() => _channel.Writer.TryComplete();
}
