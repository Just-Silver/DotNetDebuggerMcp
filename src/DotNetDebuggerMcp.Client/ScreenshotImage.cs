using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using ModelContextProtocol.Protocol;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// 把 MCP 截图结果转成可落盘、可解码的 PNG。SDK 的 <see cref="ImageContentBlock.Data"/> 是 base64 UTF-8 文本，
/// 原始图片字节在 <see cref="ImageContentBlock.DecodedData"/>；取错会把 iVBOR… 文本当成 PNG。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ScreenshotImage
{
    /// <summary>取唯一的 image 内容块并返回原始 PNG 字节；没有图片时带上文本内容报错，不把提示文本当截图。</summary>
    internal static byte[] DecodeSinglePng(CallToolResult result)
    {
        var images = result.Content.OfType<ImageContentBlock>().ToList();
        if (images.Count != 1)
            throw new InvalidOperationException($"预期恰好一张截图，实际 {images.Count} 张：{Text(result)}");
        return images[0].DecodedData.ToArray();
    }

    /// <summary>把 PNG 字节解码成独立位图（不依赖源流存活），失败时给出长度与头部字节便于定位。</summary>
    internal static Bitmap DecodePng(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        try
        {
            using var source = new Bitmap(stream);
            return new Bitmap(source);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"截图字节不是可解码的 PNG（{png.Length} 字节，头 {Head(png)}）：{ex.Message}", ex);
        }
    }

    private static string Text(CallToolResult result)
        => string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static string Head(byte[] bytes)
        => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 8)));
}
