using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DotNetDebugger.Engine.Capture;

/// <summary>
/// 截图后处理唯一归属（spec §4.2：缩放/编码/裁剪/纯黑检测职责在 Engine，宿主不碰）：
/// k 等比缩放 → 纯黑采样 → 格式编码。裁剪（region 换算）在 CaptureScreen 入口完成，本类只消费裁好的源图。
/// k = min(1, maxDimension / max(kBase 宽, 高))；kBase 由调用方给定——window 模式=源图（窗口）自身尺寸，
/// screen/region 模式=虚拟屏原生尺寸（region 与 screen 用同一 k，保证两图空间恒一致，spec §3.1）。
/// </summary>
internal static class ImagePipeline
{
    public static CaptureResult Process(Bitmap source, Size kBase, int maxDimension,
        string format, int quality, string? windowTitle, string sourceName, bool clippedToScreen = false)
    {
        var k = Math.Min(1.0, (double)maxDimension / Math.Max(kBase.Width, kBase.Height));
        var outW = Math.Max(1, (int)Math.Round(source.Width * k));
        var outH = Math.Max(1, (int)Math.Round(source.Height * k));

        using Bitmap scaled = k < 1.0 ? Resize(source, outW, outH) : CopyOf(source);
        var allBlack = IsAllBlack(scaled);
        var bytes = Encode(scaled, format, quality);
        return new CaptureResult(bytes, scaled.Width, scaled.Height,
            source.Width, source.Height, windowTitle, sourceName, allBlack, clippedToScreen);
    }

    private static Bitmap CopyOf(Bitmap src)
    {
        var b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.DrawImage(src, 0, 0, src.Width, src.Height);
        return b;
    }

    private static Bitmap Resize(Bitmap src, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return b;
    }

    /// <summary>纯黑判定=全像素 RGB 均为 0（spec：抓到纯黑不报错，只在头部加「备注」行）。
    /// LockBits + 指针逐字节扫描（比逐 GetPixel 快一个量级；A 通道不看——透明黑同样视为黑）。</summary>
    public static bool IsAllBlack(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (var y = 0; y < bd.Height; y++)
                {
                    var row = (byte*)bd.Scan0 + y * bd.Stride;
                    for (var x = 0; x < bd.Width; x++)
                    {
                        var p = row + x * 4;   // BGRA
                        if (p[0] != 0 || p[1] != 0 || p[2] != 0) return false;
                    }
                }
            }
            return true;
        }
        finally { bmp.UnlockBits(bd); }
    }

    private static byte[] Encode(Bitmap bmp, string format, int quality)
    {
        using var ms = new MemoryStream();
        switch (format.ToLowerInvariant())
        {
            case "png":
                bmp.Save(ms, ImageFormat.Png);
                break;
            case "jpeg":
            case "jpg":
                var codec = ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var ep = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 0, 100)))
                using (var eps = new EncoderParameters(1) { Param = { [0] = ep } })
                    bmp.Save(ms, codec, eps);
                break;
            default:
                throw new ArgumentException($"不支持的输出格式: {format}");
        }
        return ms.ToArray();
    }
}
