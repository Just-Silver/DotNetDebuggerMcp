using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DotNetDebuggerMcp.Services.Gui;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Requests;
using SharpSight.UiAutomation;

namespace DotNetDebuggerMcp.Services.Child;

internal sealed class ChildUiExecutionService : IAsyncDisposable
{
    private readonly ChildGuiExecutionGate _gate;
    internal ChildGuiExecutionGate Gate => _gate;
    private readonly int _sessionId;
    private readonly AsyncLocal<BoundTarget?> _binding = new();
    internal UiAutomationService Automation { get; }
    internal FrameRegistry Frames => Automation.Frames;
    internal ChildObservationContext Observations { get; }

    internal ChildUiExecutionService(ChildGuiExecutionGate gate, int sessionId, IUiExecutionBoundary? boundary = null)
    {
        _gate = gate;
        _sessionId = sessionId;
        Observations = new(sessionId);
        Automation = new(boundary ?? new UiExecutionBoundary(), async (body, ct) =>
            await _gate.RunAsync(async token => { await body(token); return true; }, DateTimeOffset.MaxValue, ct),
            ValidateWindow);
    }

    internal Task<T> RunGuiAsync<T>(Func<CancellationToken, Task<T>> body, DateTimeOffset deadline, CancellationToken ct,
        bool waitForGate = true)
        => _gate.RunAsync(body, deadline, ct, waitForGate);

    internal async Task<ChildReply> ExecuteAsync(ChildRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Operation == "ui_wait")
                return await ExecuteWaitAsync(request, ct);
            return await _gate.RunAsync(async token =>
            {
                var arguments = BindProcess(request.Arguments);
                return await ExecuteBoundAsync(request, arguments, token);
            }, request.Deadline, ct);
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "not-executed", "排队截止或取消，未执行。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
    }

    private async Task<ChildReply> ExecuteWaitAsync(ChildRequest request, CancellationToken ct)
    {
        var arguments = await _gate.RunAsync(_ => Task.FromResult(BindProcess(request.Arguments)), request.Deadline, ct);
        var pid = int.Parse(arguments.GetProperty("process").GetString()!, CultureInfo.InvariantCulture);
        var target = Observations.Targets.ReadProcess(pid);
        _binding.Value = new(target, "");
        try
        {
            var reply = Stamp(request, await UiOperationService.ExecuteAsync(request.Operation, arguments, ct, Automation));
            return reply with
            {
                Message = $"实例: {request.Instance}  child SessionId={_sessionId}  pid={pid}  startedTicks={target.StartedTicks}\n{reply.Message}"
            };
        }
        finally { _binding.Value = null; }
    }

    private async Task<ChildReply> ExecuteBoundAsync(ChildRequest request, JsonElement arguments, CancellationToken ct)
    {
        var id = arguments.TryGetProperty("observationId", out var observation) ? observation.GetString() ?? "" : "";
        var pid = int.Parse(arguments.GetProperty("process").GetString()!, CultureInfo.InvariantCulture);
        var target = await Observations.UiTargetAsync(pid, id, ct);
        var frame = arguments.TryGetProperty("frameId", out var f) ? f.GetInt32() : 0;
        var kind = arguments.TryGetProperty("index", out var index) && index.GetInt32() >= 0 ? "uia-index" : "uia";
        Observations.Registry.Validate(id, Observations.Source(request.Instance, kind, target, Frames.Current), frame);
        Frames.Validate(frame);
        var binding = new BoundTarget(target, id.Length != 0 ? target.Hwnd : "");
        ChildReply reply;
        _binding.Value = binding;
        try { reply = Stamp(request, await UiOperationService.ExecuteAsync(request.Operation, arguments, ct, Automation)); }
        finally { _binding.Value = null; }
        if (reply.Status != "completed") return reply;
        if (Observations.Targets.ReadProcess(pid).StartedTicks != target.StartedTicks)
            return ChildReply.Create(request, "unknown", "目标进程在执行期间变化，不能确认业务结果。");
        if (binding.ActualHwnd != 0)
            target = await Observations.Targets.ResolveAsync(pid, binding.ActualHwnd.ToInt64().ToString(), CancellationToken.None);
        var source = Observations.Source(request.Instance, "uia", target, Frames.Current);
        var newId = request.Operation == "ui_find" ? Observations.Registry.Register(source) : id;
        return reply with
        {
            Data = JsonSerializer.SerializeToElement(new
            {
                frameId = Frames.Current, observationId = newId, target = source
            }),
            Message = $"实例: {request.Instance}  child SessionId={_sessionId}  pid={pid}  startedTicks={target.StartedTicks}\n{reply.Message}"
        };
    }

    private void ValidateWindow(nint hwnd)
    {
        var binding = _binding.Value;
        if (binding is null) return;
        var current = Observations.Targets.ReadProcess(binding.Target.ProcessId);
        if (current.StartedTicks != binding.Target.StartedTicks ||
            (binding.ExpectedHwnd.Length != 0 && hwnd.ToInt64().ToString() != binding.ExpectedHwnd))
            throw new UiException("UIA实际目标与绑定的进程或窗口不符，请重新观察。");
        binding.ActualHwnd = hwnd;
    }

    private sealed class BoundTarget(SharpSight.ChildSession.Input.ResolvedChildTarget target, string expectedHwnd)
    {
        internal SharpSight.ChildSession.Input.ResolvedChildTarget Target { get; } = target;
        internal string ExpectedHwnd { get; } = expectedHwnd;
        internal nint ActualHwnd;
    }

    private JsonElement BindProcess(JsonElement arguments)
    {
        if (Process.GetCurrentProcess().SessionId != _sessionId || _sessionId == 0)
            throw new InvalidOperationException("worker实际会话不符，拒绝GUI操作。");
        var query = arguments.TryGetProperty("process", out var value) ? value.GetString()?.Trim() ?? "" : "";
        if (query.Length == 0) throw new ArgumentException("子会话UIA必须显式指定process，不使用父侧调试目标。");
        var processes = Process.GetProcesses();
        try
        {
            var matches = processes.Where(p => Matches(p, query)).ToArray();
            if (matches.Length != 1) throw new ArgumentException("子会话内未找到唯一目标进程，请指定pid。");
            var pid = matches[0].Id;
            var fields = arguments.Deserialize<Dictionary<string, JsonElement>>()!;
            fields["process"] = JsonSerializer.SerializeToElement(pid.ToString(CultureInfo.InvariantCulture));
            return JsonSerializer.SerializeToElement(fields);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private bool Matches(Process process, string query)
    {
        try
        {
            return process.SessionId == _sessionId && (int.TryParse(query, out var pid)
                ? process.Id == pid : process.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static ChildReply Stamp(ChildRequest request, ChildReply reply)
        => reply with { Instance = request.Instance, RequestId = request.RequestId };

    public ValueTask DisposeAsync() => _gate.DisposeAsync();
}
