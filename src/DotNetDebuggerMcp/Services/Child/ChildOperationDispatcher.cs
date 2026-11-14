using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Processes;

namespace DotNetDebuggerMcp.Services.Child;

internal sealed class ChildOperationDispatcher(string instance, string root, int session) : IAsyncDisposable
{
    private readonly ChildTaskRunner _processes = new(instance, Path.Combine(root, instance), session);

    internal Task<ChildReply> ExecuteAsync(ChildRequest request, CancellationToken ct)
        => request.Operation switch
        {
            "child_process" => _processes.ExecuteAsync(request, ct),
            _ => Task.FromResult(ChildReply.Create(request, "not-executed", "该worker操作尚未接线。"))
        };

    public ValueTask DisposeAsync() => _processes.DisposeAsync();
}
