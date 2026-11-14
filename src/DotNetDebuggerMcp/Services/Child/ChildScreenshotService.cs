using System.Diagnostics;
using System.Text.Json;
using DotNetDebuggerMcp.Services.Gui;
using SharpSight.Capture;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Input;
using SharpSight.ChildSession.Lifecycle;
using SharpSight.UiAutomation;

namespace DotNetDebuggerMcp.Services.Child;

internal interface IChildCaptureBoundary
{
    Task<ScreenshotExecution> CaptureAsync(ScreenshotRequest request, UiAutomationService automation, CancellationToken ct);
}

internal sealed class ChildCaptureBoundary : IChildCaptureBoundary
{
    public Task<ScreenshotExecution> CaptureAsync(ScreenshotRequest request, UiAutomationService automation, CancellationToken ct)
        => Task.Run(() => ScreenshotCaptureService.ExecuteAsync(request, ct, automation), CancellationToken.None);
}

internal sealed class ChildScreenshotService(ChildUiExecutionService ui, ChildInstanceStore store,
    string instance, int sessionId, IChildCaptureBoundary? capture = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IChildCaptureBoundary _capture = capture ?? new ChildCaptureBoundary();

    internal async Task<ChildReply> ExecuteAsync(ChildRequest request, CancellationToken ct)
    {
        try
        {
            var immediate = request.Arguments.TryGetProperty("timeoutSeconds", out var timeout) && timeout.GetInt32() == 0;
            return await ui.RunGuiAsync(token => CaptureAsync(request, token), request.Deadline, ct, waitForGate: !immediate);
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "not-executed", "截图排队截止或取消，未执行。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", $"子会话截图失败：{ex.Message}"); }
    }

    private async Task<ChildReply> CaptureAsync(ChildRequest request, CancellationToken ct)
    {
        if (request.Instance != instance || Process.GetCurrentProcess().SessionId != sessionId || sessionId == 0)
            throw new InvalidOperationException("截图实例或worker实际会话不符。");
        var r = request.Arguments.Deserialize<ScreenshotRequest>(Json) ?? throw new ArgumentException("截图参数不能为空。");
        if (r.FilePath.Length != 0) throw new ArgumentException("worker不接受外部filePath，请由调用端物化截图。");
        var mode = ScreenshotCaptureService.ResolveMode(r.Mode, r.Display, r.Hwnd, r.WindowTitle, r.Region, r.Element, r.ProcessId);
        var compatibility = ScreenshotCaptureService.ValidateCompatibility(mode, r.Display, r.Hwnd,
            r.WindowTitle, r.Region, r.Element, r.ClientArea, r.ProcessId);
        if (compatibility is not null) throw new ArgumentException(compatibility);
        var target = await BindTargetAsync(r, mode, ct);
        var id = request.Arguments.TryGetProperty("observationId", out var v) ? v.GetString() ?? "" : "";
        var kind = mode == "element" ? "element" : target is null ? "screen" : "window";
        var before = ui.Observations.Source(instance, kind, target, ui.Frames.Current);
        ui.Frames.Validate(r.FrameId);
        ui.Observations.Registry.Validate(id, before, r.FrameId);
        if (target is not null && mode is "window" or "foreground")
            r = r with { Mode = "window", Hwnd = target.Hwnd, ProcessId = target.ProcessId, WindowTitle = "" };
        var result = await _capture.CaptureAsync(r, ui.Automation, ct);
        if (result.Text is not null)
            return ChildReply.Create(request, result.IsError ? "not-executed" : "completed",
                $"实例: {instance}  child SessionId={sessionId}\n{result.Text}");
        if (target is not null)
        {
            var actualHwnd = result.TargetHwnd.Length == 0 ? target.Hwnd : result.TargetHwnd;
            var current = await ui.Observations.Targets.ResolveAsync(target.ProcessId, actualHwnd, CancellationToken.None);
            if (current.StartedTicks != target.StartedTicks ||
                (current.Hwnd == target.Hwnd ? current.Bounds != target.Bounds : id.Length != 0))
                throw new InvalidOperationException("采集期间目标身份或窗口几何变化，请重新截图。");
            target = current;
        }
        if (mode == "foreground")
            result = result with { Mode = mode, TargetLine = $"目标:   前台窗口 \"{result.Image!.WindowTitle}\" (pid={target!.ProcessId})" };
        var source = ui.Observations.Source(instance, kind, target, ui.Frames.Current);
        if (before.GetProperty("layout").GetString() != source.GetProperty("layout").GetString())
            throw new InvalidOperationException("采集期间显示布局变化，请重新截图。");
        var observationId = ui.Observations.Registry.Register(source);
        var artifact = await SaveAsync(result, source, observationId);
        return ChildReply.Create(request, "completed", $"实例: {instance}  child SessionId={sessionId}  截图已保存。",
            new { artifact, observationId, frameId = result.Image!.FrameId, target = source });
    }

    private async Task<ResolvedChildTarget?> BindTargetAsync(ScreenshotRequest r, string mode, CancellationToken ct)
    {
        if (mode is not ("window" or "foreground" or "element")) return null;
        if (mode == "element" && r.ProcessId <= 0) throw new ArgumentException("子会话元素截图必须显式指定processId。");
        if (mode == "window" && r.Hwnd.Length == 0 && r.WindowTitle.Length == 0 && r.ProcessId <= 0)
            throw new ArgumentException("子会话窗口截图必须显式指定目标，不使用父侧调试进程。");
        if (r.Hwnd.Length != 0 && (!long.TryParse(r.Hwnd, out var parsed) || parsed == 0))
            throw new ArgumentException("hwnd必须是非零十进制窗口句柄。");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(r.TimeoutSeconds, 0, 30));
        do
        {
            ct.ThrowIfCancellationRequested();
            var window = r.Hwnd.Length != 0 && long.TryParse(r.Hwnd, out var raw)
                ? ScreenCapture.FindWindowByHwnd(new(raw))
                : mode == "foreground" || ScreenshotCaptureService.IsActiveSpecial(r.WindowTitle)
                    ? ScreenCapture.FindForegroundWindow() : ScreenCapture.FindMainWindow(r.ProcessId, r.WindowTitle);
            if (window is not null)
                return await ui.Observations.Targets.ResolveAsync(window.Pid, window.Hwnd.ToInt64().ToString(), ct);
            if (DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(50, ct);
        } while (true);
        throw new InvalidOperationException("未找到子会话目标窗口，未截图。");
    }

    private async Task<ChildArtifact> SaveAsync(ScreenshotExecution execution, JsonElement source, string observationId)
    {
        var result = execution.Image!;
        var directory = Path.Combine(store.Create(instance), "artifacts");
        Directory.CreateDirectory(directory);
        var name = Guid.NewGuid().ToString("N") + ".png";
        var full = Path.Combine(directory, name);
        var temp = full + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, result.Image, CancellationToken.None);
            File.Move(temp, full);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(Path.Combine("artifacts", name), "image/png", result.Image.LongLength,
            JsonSerializer.SerializeToElement(new
            {
                result.Width, result.Height, result.WindowTitle, result.Source, result.WasAllBlack,
                result.ClippedToScreen, result.OriginX, result.OriginY, result.FrameId, result.DisplayIndex, result.IsClientArea,
                execution.TargetLine, execution.Mode, execution.Notes, sessionId, observationId, target = source
            }, Json));
    }
}
