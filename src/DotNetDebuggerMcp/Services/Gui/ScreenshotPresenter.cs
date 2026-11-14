using DotNetDebuggerMcp.Configuration;
using ModelContextProtocol.Protocol;

namespace DotNetDebuggerMcp.Services.Gui;

internal static class ScreenshotPresenter
{
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
