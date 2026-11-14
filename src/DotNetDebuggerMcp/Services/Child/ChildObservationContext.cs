using System.Diagnostics;
using System.Text.Json;
using SharpSight.Capture;
using SharpSight.ChildSession.Input;
using SharpSight.ChildSession.Observations;

namespace DotNetDebuggerMcp.Services.Child;

internal sealed class ChildObservationContext(int session)
{
    internal ObservationRegistry Registry { get; } = new();
    internal ChildTargetResolver Targets { get; } = new(session);

    internal JsonElement Source(string instance, string kind, ResolvedChildTarget? target, int frame)
    {
        using var worker = Process.GetCurrentProcess();
        return JsonSerializer.SerializeToElement(new
        {
            instance, kind, workerPid = worker.Id, workerStartedTicks = worker.StartTime.ToUniversalTime().Ticks,
            sessionId = worker.SessionId, processId = target?.ProcessId ?? 0,
            startedTicks = target?.StartedTicks ?? 0, hwnd = target?.Hwnd ?? "",
            geometry = target?.Bounds.ToString() ?? "", frameId = frame,
            layout = string.Join("|", ScreenCapture.EnumerateDisplays().Select(display => display.ToString()))
        });
    }

    internal async Task<ResolvedChildTarget> UiTargetAsync(int pid, string observationId, CancellationToken ct)
    {
        var oldHwnd = observationId.Length == 0 ? "" : Registry.SourceFor(observationId).GetProperty("hwnd").GetString() ?? "";
        if (oldHwnd.Length != 0) return await Targets.ResolveAsync(pid, oldHwnd, ct);
        var window = ScreenCapture.FindMainWindow(pid, "");
        return window is null ? Targets.ReadProcess(pid) :
            await Targets.ResolveAsync(pid, window.Hwnd.ToInt64().ToString(), ct);
    }
}
