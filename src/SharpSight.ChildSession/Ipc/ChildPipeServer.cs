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
                using var receiving = CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiving.CancelAfter(TimeSpan.FromSeconds(5));
                var envelope = await ChildWire.ReadAsync<ChildEnvelope>(pipe, receiving.Token);
                var actual = PipePeerVerifier.Read(pipe, true);
                var error = Validate(envelope, actual);
                var reply = error is null ? await handler(envelope.Request, ct) :
                    ChildReply.Create(envelope.Request, "not-executed", error);
                await ChildWire.WriteAsync(pipe, reply, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"子会话通信失败：{ex.GetType().Name}：{ex.Message}");
            }
        }
    }

    private string? Validate(ChildEnvelope envelope, ChildPeer actual)
    {
        if (!actual.Matches(envelope.Peer) || actual.Sid != owner.Sid || actual.SessionId !=
            (expectedClient?.SessionId ?? owner.SessionId) ||
            !string.Equals(actual.Image, owner.Image, StringComparison.OrdinalIgnoreCase))
            return "管道客户端身份不符。";
        if (expectedClient is not null && !expectedClient.Matches(actual)) return "非本实例连接持有器。";
        if (envelope.Version != ChildWire.Version || envelope.Request.Instance != instance)
            return "协议版本或实例身份不符。";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(envelope.Secret)))
            return "实例凭据不符。";
        return ChildWire.IsOperation(envelope.Request.Operation) ? null : "未知操作，拒绝执行。";
    }
}
