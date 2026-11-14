using System.IO.Pipes;
using SharpSight.ChildSession.Contracts;

namespace SharpSight.ChildSession.Ipc;

public sealed class ChildPipeClient(string pipeName, string secret, ChildPeer server)
{
    public async Task<ChildReply> SendAsync(ChildRequest request, CancellationToken ct = default)
    {
        var sent = false;
        try
        {
            var envelope = new ChildEnvelope(ChildWire.Version, secret, ChildPeer.Current, request);
            if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope, ChildWire.Json).Length > ChildWire.MaximumBytes)
                return ChildReply.Create(request, "not-executed", "请求超过1 MiB上限。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var budget = request.Deadline - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            timeout.CancelAfter(budget > TimeSpan.Zero ? budget : TimeSpan.FromMilliseconds(1));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000, timeout.Token);
            PipePeerVerifier.Require(server, PipePeerVerifier.Read(pipe, false));
            sent = true;
            await ChildWire.WriteAsync(pipe, envelope, timeout.Token);
            var reply = await ChildWire.ReadAsync<ChildReply>(pipe, timeout.Token);
            if (reply.RequestId != request.RequestId || reply.Instance != request.Instance)
                throw new InvalidOperationException("响应不属于本次请求。");
            return reply;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            return ChildReply.Create(request, sent ? "unknown" : "not-executed",
                sent ? $"请求结果未确认，请用requests/result核对：{ex.Message}" : $"请求未发送：{ex.Message}");
        }
    }
}
