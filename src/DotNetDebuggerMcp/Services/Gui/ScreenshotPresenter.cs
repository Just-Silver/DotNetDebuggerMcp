using DotNetDebuggerMcp.Configuration;
using ModelContextProtocol.Protocol;
using System.Drawing;
using System.Text.Json;
using SharpSight.Capture;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Lifecycle;

namespace DotNetDebuggerMcp.Services.Gui;

internal static class ScreenshotPresenter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<CallToolResult> FromChildAsync(ChildReply reply, ChildInstanceStore store,
        string filePath, CancellationToken ct)
    {
        try
        {
            if (!reply.Data.TryGetProperty("artifact", out var value))
                return ToMcp(new(null, "", "", [], 0, $"{reply.Message}\nrequestId={reply.RequestId}"), "");
            var artifact = value.Deserialize<ChildArtifact>(Json) ?? throw new InvalidOperationException("产物描述无效。");
            if (artifact.MediaType != "image/png") throw new InvalidOperationException("产物不是PNG。");
            var bytes = await store.ReadArtifactAsync(reply.Instance, artifact, ct);
            if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidOperationException("产物内容不是PNG。");
            var meta = artifact.Metadata;
            using var stream = new MemoryStream(bytes);
            using var image = new Bitmap(stream);
            if (image.Width != meta.GetProperty("width").GetInt32() || image.Height != meta.GetProperty("height").GetInt32())
                throw new InvalidOperationException("产物尺寸与采集元数据不符。");
            var result = new CaptureResult(bytes, image.Width, image.Height, meta.GetProperty("windowTitle").GetString(),
                meta.GetProperty("source").GetString()!, meta.GetProperty("wasAllBlack").GetBoolean(),
                meta.GetProperty("clippedToScreen").GetBoolean(), meta.GetProperty("originX").GetInt32(),
                meta.GetProperty("originY").GetInt32(), meta.GetProperty("frameId").GetInt32(),
                meta.GetProperty("displayIndex").GetInt32(), meta.GetProperty("isClientArea").GetBoolean());
            var notes = meta.GetProperty("notes").Deserialize<string[]>() ?? [];
            var target = meta.GetProperty("target");
            var detail = $"实例: {reply.Instance}  child SessionId={meta.GetProperty("sessionId").GetInt32()}  " +
                $"pid={target.GetProperty("processId").GetInt32()}  startedTicks={target.GetProperty("startedTicks").GetInt64()}\n" +
                $"observationId={meta.GetProperty("observationId").GetString()}  requestId={reply.RequestId}";
            return ToMcp(new(result, meta.GetProperty("targetLine").GetString()!, meta.GetProperty("mode").GetString()!,
                [detail, .. notes], target.GetProperty("processId").GetInt32()), filePath);
        }
        catch (Exception ex)
        {
            return ToMcp(new(null, "", "", [], 0,
                $"截图产物读取失败：{ex.Message}；不返回旧图。requestId={reply.RequestId}"), "");
        }
    }

    internal static CallToolResult ToMcp(ScreenshotExecution capture, string filePath)
    {
        if (capture.Text is not null)
            return new() { Content = [new TextContentBlock { Text = capture.Text }] };
        var result = capture.Image!;
        var header = ScreenshotCaptureService.BuildHeader(capture.TargetLine, capture.Mode, result, capture.Notes);
        var b64Len = ((long)result.Image.Length + 2) / 3 * 4;
        var wantFile = !string.IsNullOrWhiteSpace(filePath) || b64Len >= AppConfig.InlineImageBase64Bytes;
        var attachImage = true;
        if (wantFile)
        {
            try
            {
                var full = ScreenshotCaptureService.ResolveScreenshotPath(filePath, capture.Mode, capture.ProcessId);
                File.WriteAllBytes(full, result.Image);
                header.AppendLine($"已落盘: {full}");
                attachImage = false;
            }
            catch (Exception ex)
            {
                header.AppendLine($"落盘失败（{ex.Message}）；已改为内联返回图片，可改用可写路径或去掉 filePath 重试。");
            }
        }
        header.AppendLine("---");
        var content = new List<ContentBlock> { new TextContentBlock { Text = header.ToString() } };
        if (attachImage) content.Add(ImageContentBlock.FromBytes(result.Image, "image/png"));
        return new CallToolResult { Content = content };
    }
}
