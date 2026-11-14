using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Processes;

namespace DotNetDebuggerMcp.Services.Child;

internal sealed class ChildOperationDispatcher(string instance, string root, int session) : IAsyncDisposable
{
    private readonly ChildTaskRunner _processes = new(instance, Path.Combine(root, instance), session);
    private readonly ChildUiExecutionService _ui = new(new(), session);

    internal Task<ChildReply> ExecuteAsync(ChildRequest request, CancellationToken ct)
        => request.Operation switch
        {
            "child_process" => _processes.ExecuteAsync(request, ct),
            "ui_find" or "ui_action" or "ui_input" or "ui_get" or "ui_wait" => _ui.ExecuteAsync(request, ct),
            "screenshot" => new ChildScreenshotService(_ui, new(root), instance, session).ExecuteAsync(request, ct),
            "child_pointer" or "child_keyboard" => new ChildInputService(_ui, new(root), instance, session).ExecuteAsync(request, ct),
            _ => Task.FromResult(ChildReply.Create(request, "not-executed", "该worker操作尚未接线。"))
        };

    public async ValueTask DisposeAsync()
    {
        await _processes.DisposeAsync();
        await _ui.DisposeAsync();
    }
}
