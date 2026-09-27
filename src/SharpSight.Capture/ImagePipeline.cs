using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SharpSight.Capture;

/// <summary>
/// 截图后处理唯一归属（spec §4.2：缩放/编码/裁剪/纯黑检测职责在 Engine，宿主不碰）：
/// 双轴等比缩放 → 纯黑采样 → 格式编码。裁剪（region 换算）在 CaptureScreen 入口完成，本类只消费裁好的源图。
/// k = min(1, maxWidth/kBase 宽, maxHeight/kBase 高)（spec §6.2；某轴上界 ≤0 视为该轴不限制，
/// 两轴皆 ≤0 ⇒ k=1 不缩放）；kBase 由调用方给定——window 模式=源图（窗口）自身尺寸，
/// screen/region 模式=虚拟屏原生尺寸（region 与 screen 用同一 k，保证两图空间恒一致，spec §3.1）。
/// </summary>
internal static class ImagePipeline
{
    /// <summary>双轴降采样系数（spec §6.2，region 换算与 <see cref="Process"/> 共用同一来源避免漂移）：
    /// k = min(1, maxW/w, maxH/h)，maxW/maxH≤0 视为该轴不限制；两轴皆≤0 ⇒ k=1（不缩放）。</summary>
    internal static double ScaleFactor(Size kBase, int maxWidth, int maxHeight)
    {
        var k = 1.0;
        if (maxWidth > 0) k = Math.Min(k, (double)maxWidth / kBase.Width);
        if (maxHeight > 0) k = Math.Min(k, (double)maxHeight / kBase.Height);
        return k;
    }

    /// <param name="maxWidth">输出宽上限（原生物理像素）；≤0 视为该轴不限制。</param>
    /// <param name="maxHeight">输出高上限（原生物理像素）；≤0 视为该轴不限制。</param>
    /// <param name="originX">抓取矩形左上角 X（虚拟屏物理像素，spec §5）；直接回填结果。</param>
    /// <param name="originY">抓取矩形左上角 Y；直接回填结果。</param>
    public static CaptureResult Process(Bitmap source, Size kBase, int maxWidth, int maxHeight,
        string? windowTitle, string sourceName, bool clippedToScreen = false,
        int originX = 0, int originY = 0)
    {
        var k = ScaleFactor(kBase, maxWidth, maxHeight);
        var outW = Math.Max(1, (int)Math.Round(source.Width * k));
        var outH = Math.Max(1, (int)Math.Round(source.Height * k));

        using Bitmap scaled = k < 1.0 ? Resize(source, outW, outH) : CopyOf(source);
        var allBlack = IsAllBlack(scaled);
        var bytes = Encode(scaled);
        // Scale = 图像像素 / 原生物理像素（spec §5）；源图宽恒 >0，无需防零。
        return new CaptureResult(bytes, scaled.Width, scaled.Height,
            source.Width, source.Height, windowTitle, sourceName, allBlack, clippedToScreen,
            originX, originY, (double)scaled.Width / source.Width);
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

    /// <summary>输出**固定 PNG**（唯一格式；spec §6.1，用户裁定 2026-09-28）：无 format/quality，
    /// 不引入任何第三方图像编码库，PNG 由 <c>System.Drawing</c> 编码。</summary>
    private static byte[] Encode(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
