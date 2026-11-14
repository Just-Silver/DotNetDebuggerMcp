using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Input;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;

namespace DotNetDebuggerMcp.Services.Child;

internal sealed class ChildInputService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ChildUiExecutionService _ui;
    private readonly ChildInstanceStore _store;
    private readonly string _instance;
    private readonly int _session;
    private readonly ChildPeer _worker = ChildPeer.Current;
    private readonly ChildInputController _controller;

    internal ChildInputService(ChildUiExecutionService ui, ChildInstanceStore store, string instance, int session)
    {
        _ui = ui;
        _store = store;
        _instance = instance;
        _session = session;
        _controller = new(instance, session, ui.Gate, new ChildWindowsInputPlatform(session, IsOwnedReady), ValidateObservation);
    }

    internal Task<ChildReply> ExecuteAsync(ChildRequest request, CancellationToken ct)
        => request.Operation == "child_pointer" ? _controller.PointerAsync(request, ct) : _controller.KeyboardAsync(request, ct);

    private void ValidateObservation(ChildRequest request, ResolvedChildTarget target)
    {
        var id = request.Arguments.TryGetProperty("observationId", out var value) ? value.GetString() ?? "" : "";
        _ui.Observations.Registry.Validate(id,
            _ui.Observations.Source(_instance, "input", target, _ui.Frames.Current), 0);
    }

    private bool IsOwnedReady()
    {
        try
        {
            var state = JsonSerializer.Deserialize<ChildBootstrap>(
                File.ReadAllText(Path.Combine(_store.Root, _instance, "instance.json")), Json);
            return state is { State: "Ready", Worker: not null } && state.Identity.Instance == _instance &&
                state.Identity.ChildSessionId == _session && state.Worker.Matches(_worker) &&
                state.Holder.Matches(ChildPeer.Capture(state.Holder.ProcessId));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"子会话输入所有权检查失败：{ex.Message}");
            return false;
        }
    }
}
