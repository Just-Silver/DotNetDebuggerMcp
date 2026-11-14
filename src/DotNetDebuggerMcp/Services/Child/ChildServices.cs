using SharpSight.ChildSession.Lifecycle;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Encodings.Web;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services.Gui;
using ModelContextProtocol.Protocol;
using SharpSight.ChildSession.Contracts;

namespace DotNetDebuggerMcp.Services.Child;

internal static class ChildServices
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static FeatureFamily EnabledFeatures { get; set; } = FeatureFamily.All;
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotNetDebuggerMcp", "child");
    internal static ChildHostCommand Command
    {
        get
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前宿主。");
            var prefix = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? new[] { typeof(DotNetDebuggerMcpCmd).Assembly.Location } : Array.Empty<string>();
            return new ChildHostCommand(executable, prefix);
        }
    }
    internal static ChildSessionClient CreateClient() => new(new ChildInstanceStore(Root), Command);

    internal static string Serialize(ChildReply reply) => JsonSerializer.Serialize(reply, Json);

    internal static async Task<string> CallTextAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        var reply = await CreateMcpCaller().CallAsync(operation, arguments, ct);
        if (operation == "child_session" && reply.Data.TryGetProperty("artifact", out _))
            reply = await MaterializeScreenshotAsync(reply, "", ct);
        return Serialize(reply);
    }

    internal static async Task<CallToolResult> CallScreenshotAsync(JsonElement arguments, string filePath, CancellationToken ct)
    {
        var reply = await CreateMcpCaller().CallAsync("screenshot", arguments, ct);
        return await ScreenshotPresenter.FromChildAsync(reply, new(Root), filePath, ct);
    }

    private static ChildCallPolicy CreateMcpCaller()
        => new(EnabledFeatures, CreateClient().SendAsync, IsDebugAttachedAsync);

    internal static async Task<ChildReply> MaterializeScreenshotAsync(ChildReply reply, string filePath, CancellationToken ct)
    {
        try
        {
            if (!reply.Data.TryGetProperty("artifact", out var value)) return reply;
            var artifact = value.Deserialize<ChildArtifact>(Json) ?? throw new InvalidOperationException("缺少截图产物。");
            var capture = await ScreenshotPresenter.ReadChildAsync(reply, new(Root), ct);
            var path = string.IsNullOrWhiteSpace(filePath)
                ? Path.GetFullPath(Path.Combine(Root, reply.Instance, artifact.RelativePath))
                : ScreenshotCaptureService.ResolveScreenshotPath(filePath, capture.Mode, capture.ProcessId);
            if (!string.IsNullOrWhiteSpace(filePath)) await File.WriteAllBytesAsync(path, capture.Image!.Image, ct);
            var data = reply.Data.Deserialize<Dictionary<string, JsonElement>>(Json)!;
            data["path"] = JsonSerializer.SerializeToElement(path);
            return reply with { Message = $"{reply.Message}\n已落盘: {path}", Data = JsonSerializer.SerializeToElement(data) };
        }
        catch (Exception ex) { return reply with { Status = "failed", Message = $"截图产物读取/物化失败：{ex.Message}，不返回旧图。" }; }
    }

    internal static async Task<ChildReply> ChangeSettingAsync(bool enabled, CancellationToken ct)
    {
        var request = new ChildRequest("", Guid.NewGuid().ToString("N"), "child_session",
            JsonSerializer.SerializeToElement(new { action = enabled ? "enable" : "disable" }), DateTimeOffset.MaxValue);
        try
        {
            var info = Command.StartInfo("--child-internal", "setting", Root, enabled ? "enable" : "disable");
            info.UseShellExecute = true;
            info.Verb = "runas";
            using var process = Process.Start(info) ?? throw new InvalidOperationException("管理员设置进程未启动。");
            await process.WaitForExitAsync(ct);
            return ChildReply.Create(request, process.ExitCode == 0 ? "completed" : "not-executed",
                process.ExitCode == 0 ? "系统子会话开关已修改；可用child_session probe核对。" :
                    "系统设置未确认成功；可能存在活动child或未获得管理员授权，请probe核对。");
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "unknown", "已放弃等待设置进程，请probe核对实际开关；未自动撤销已提交的修改。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", $"设置未执行：{ex.Message}"); }
    }

    private static async Task<bool> IsDebugAttachedAsync(string instance, CancellationToken ct)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return false;
        var state = await new ChildInstanceStore(Root).ReadAsync(instance, ct);
        try
        {
            using var process = Process.GetProcessById(active.ProcessId);
            return process.SessionId == state.Identity.ChildSessionId;
        }
        catch (ArgumentException) { return false; }
    }
}
