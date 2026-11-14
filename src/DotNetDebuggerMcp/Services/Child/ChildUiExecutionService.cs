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
    private readonly int _sessionId;
    internal UiAutomationService Automation { get; }
    internal FrameRegistry Frames => Automation.Frames;

    internal ChildUiExecutionService(ChildGuiExecutionGate gate, int sessionId, IUiExecutionBoundary? boundary = null)
    {
        _gate = gate;
        _sessionId = sessionId;
        Automation = new(boundary ?? new UiExecutionBoundary(), async (body, ct) =>
            await _gate.RunAsync(async token => { await body(token); return true; }, DateTimeOffset.MaxValue, ct));
    }

    internal Task<T> RunGuiAsync<T>(Func<CancellationToken, Task<T>> body, DateTimeOffset deadline, CancellationToken ct)
        => _gate.RunAsync(body, deadline, ct);

    internal async Task<ChildReply> ExecuteAsync(ChildRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Operation == "ui_wait")
            {
                var arguments = await _gate.RunAsync(_ => Task.FromResult(BindProcess(request.Arguments)),
                    request.Deadline, ct);
                return Stamp(request, await UiOperationService.ExecuteAsync(request.Operation, arguments, ct, Automation));
            }
            return await _gate.RunAsync(async token =>
            {
                var arguments = BindProcess(request.Arguments);
                return Stamp(request, await UiOperationService.ExecuteAsync(request.Operation, arguments, token, Automation));
            }, request.Deadline, ct);
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "not-executed", "排队截止或取消，未执行。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
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
