using System.Drawing;
using System.Drawing.Imaging;

namespace SharpSight.Capture;

/// <summary>
/// 截图后处理唯一归属（编码/纯黑检测职责在库，宿主不碰）：纯黑采样 → 固定 PNG 编码。
/// <b>不做任何缩放</b>（用户裁定 2026-09-28：尺寸处理交模型侧，避免"我们自己缩一次、模型侧再处理一次"
/// 造成坐标口径混乱）——输出恒为传入源图的 1:1 原生像素。裁剪（region / 元素换算）在各抓取入口完成，
/// 本类只消费裁好的源图。
/// </summary>
internal static class ImagePipeline
{
    /// <param name="windowTitle">window 模式标题；screen/region 为 null。</param>
    /// <param name="originX">抓取矩形左上角 X（虚拟屏物理像素，spec §5）；直接回填结果。</param>
    /// <param name="originY">抓取矩形左上角 Y；直接回填结果。</param>
    public static CaptureResult Process(Bitmap source, string? windowTitle, string sourceName,
        bool clippedToScreen = false, int originX = 0, int originY = 0)
        => new(Encode(source), source.Width, source.Height,
            windowTitle, sourceName, IsAllBlack(source), clippedToScreen, originX, originY);

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
