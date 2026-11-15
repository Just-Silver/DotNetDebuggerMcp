using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using SharpSight.ChildSession.Contracts;

namespace SharpSight.ChildSession.Ipc;

public sealed class ChildPipeServer(string pipeName, string instance, string secret, ChildPeer owner,
    ChildPeer? expectedClient = null)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => _ready.Task;

    public async Task RunAsync(Func<ChildRequest, CancellationToken, Task<ChildReply>> handler, CancellationToken ct)
    {
        var pending = new ConcurrentDictionary<int, Task>();
        var next = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    _ready.TrySetResult();
                    await pipe.WaitForConnectionAsync(ct);
                    var id = Interlocked.Increment(ref next);
                    var task = ServeAsync(pipe, handler, ct);
                    pending[id] = task;
                    _ = task.ContinueWith(_ => pending.TryRemove(id, out var removed),
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch { await pipe.DisposeAsync(); throw; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(pending.Values); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe,
        Func<ChildRequest, CancellationToken, Task<ChildReply>> handler, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                var envelope = await ReadEnvelopeAsync(pipe, ct);
                if (envelope is null) return;
                var error = Validate(envelope, PipePeerVerifier.Read(pipe, true));
                if (error is not null)
                {
                    await ChildWire.WriteAsync(pipe, ChildReply.Create(envelope.Request, "not-executed", error), ct);
                    return;
                }
                await ExecuteAsync(pipe, envelope.Request, handler, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"子会话通信失败：{ex.GetType().Name}：{ex.Message}");
            }
        }
    }

    private static async Task<ChildEnvelope?> ReadEnvelopeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receiving.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await ChildWire.ReadAsync<ChildEnvelope>(pipe, receiving.Token); }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task ExecuteAsync(NamedPipeServerStream pipe, ChildRequest request,
        Func<ChildRequest, CancellationToken, Task<ChildReply>> handler, CancellationToken ct)
    {
        // 执行令牌会被 handler 传给已启动的任务，只能在真实对端断开时取消；停止监视用独立令牌。
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = 0;
        var monitor = MonitorDisconnectAsync(pipe, execution, monitorStop.Token,
            () => Volatile.Read(ref finished) != 0);
        try
        {
            var reply = await handler(request, execution.Token);
            Interlocked.Exchange(ref finished, 1);
            await ChildWire.WriteAsync(pipe, reply, ct);
            await monitorStop.CancelAsync();
            await monitor;
        }
        finally
        {
            Interlocked.Exchange(ref finished, 1);
            await monitorStop.CancelAsync();
            await monitor;
        }
    }

    private static async Task MonitorDisconnectAsync(NamedPipeServerStream pipe,
        CancellationTokenSource execution, CancellationToken monitorToken, Func<bool> handlerFinished)
    {
        var probe = new byte[1];
        try
        {
            while (await pipe.ReadAsync(probe, monitorToken) > 0)
            {
                // 一次连接只承载一个请求；额外数据视为异常连接，取消本连接执行而不是当作第二请求。
                await CancelIfRunningAsync(execution, handlerFinished);
                return;
            }
            await CancelIfRunningAsync(execution, handlerFinished);
        }
        catch (OperationCanceledException) when (monitorToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ex is not (IOException or ObjectDisposedException or OperationCanceledException))
                Console.Error.WriteLine($"子会话断开监视失败：{ex.GetType().Name}：{ex.Message}");
            await CancelIfRunningAsync(execution, handlerFinished);
        }
    }

    private static async Task CancelIfRunningAsync(CancellationTokenSource execution, Func<bool> handlerFinished)
    {
        if (!handlerFinished()) await execution.CancelAsync();
    }

    private string? Validate(ChildEnvelope envelope, ChildPeer actual)
    {
        // owner 只约束实例所属用户/父会话；客户端可经 apphost 或 dotnet 宿主、不同目录启动，
        // 真实映像已由 actual.Matches 与信封自报值交叉核对，不能再要求与创建者映像相同。
        if (!actual.Matches(envelope.Peer) || actual.Sid != owner.Sid || actual.SessionId !=
            (expectedClient?.SessionId ?? owner.SessionId))
            return "管道客户端身份不符。";
        if (expectedClient is not null && !expectedClient.Matches(actual)) return "非本实例连接持有器。";
        if (envelope.Version != ChildWire.Version || envelope.Request.Instance != instance)
            return "协议版本或实例身份不符。";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(envelope.Secret)))
            return "实例凭据不符。";
        return ChildWire.IsOperation(envelope.Request.Operation) ? null : "未知操作，拒绝执行。";
    }
}
