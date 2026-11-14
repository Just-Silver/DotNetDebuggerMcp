using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Requests;

namespace DotNetDebuggerMcp.Tests;

internal sealed class ChildTestHost : IAsyncDisposable
{
    internal const string Secret = "test-only-secret";
    private readonly CancellationTokenSource _stop = new();
    private Task _serverTask = Task.CompletedTask;
    internal string PipeName { get; } = "ChildTest-" + Guid.NewGuid().ToString("N");
    internal string OutputPath { get; } = Path.Combine(Path.GetTempPath(), "child-" + Guid.NewGuid().ToString("N"));
    internal ChildPipeClient Client { get; private set; } = null!;
    internal RequestJournal Journal { get; } = new();

    internal static async Task<ChildTestHost> StartAsync(CancellationToken ct = default,
        Func<ChildRequest, CancellationToken, Task<ChildReply>>? boundary = null)
    {
        var host = new ChildTestHost();
        var peer = ChildPeer.Current;
        var server = new ChildPipeServer(host.PipeName, "test-instance", Secret, peer);
        var executor = new ChildRequestExecutor(host.Journal, serialize: false);
        host._serverTask = server.RunAsync((request, callToken) =>
        {
            if (request.Operation == "child_session")
            {
                var action = request.Arguments.GetProperty("action").GetString();
                return Task.FromResult(ChildReply.Create(request, "completed", "查询",
                    action == "requests" ? host.Journal.List(0, 100) :
                        host.Journal.ReadResult(request.Arguments.GetProperty("requestId").GetString()!)));
            }
            return executor.ExecuteAsync(request, boundary is not null ? token => boundary(request, token) : async token =>
            {
                await File.WriteAllTextAsync(host.OutputPath, request.Arguments.GetProperty("value").GetString(), token);
                return ChildReply.Create(request, "completed", "已写入");
            }, callToken);
        }, host._stop.Token);
        host.Client = new ChildPipeClient(host.PipeName, Secret, peer);
        await server.Ready.WaitAsync(ct);
        return host;
    }

    internal ChildRequest Request(string operation, object arguments) => new("test-instance",
        Guid.NewGuid().ToString("N"), operation, JsonSerializer.SerializeToElement(arguments),
        DateTimeOffset.UtcNow.AddSeconds(10));

    internal Task<ChildReply> SendAsync(string operation, object arguments, CancellationToken ct = default)
        => Client.SendAsync(Request(operation, arguments), ct);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _serverTask;
        _stop.Dispose();
        File.Delete(OutputPath);
    }
}
