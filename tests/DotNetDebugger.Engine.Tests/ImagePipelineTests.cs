using System.Drawing;
using System.Drawing.Imaging;
using DotNetDebugger.Engine.Capture;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 图像管线上/下边界单测（screenshot 计划 T3；spec §4.2 缩放/编码/纯黑职责唯一归属 Engine）：
/// 纯逻辑合成图，无进程依赖、快跑。
/// </summary>
public sealed class ImagePipelineTests
{
    private static Bitmap Make(int w, int h, Color fill)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        using var br = new SolidBrush(fill);
        g.FillRectangle(br, 0, 0, w, h);
        return b;
    }

    [Fact]
    public void Scale_DownAboveMax_KeepsAspectRatio()
    {
        using var src = Make(4000, 2000, Color.SteelBlue);
        var r = ImagePipeline.Process(src, src.Size, maxDimension: 2000, "png", 80, "t", "WGC");
        Assert.Equal(2000, r.Width);
        Assert.Equal(1000, r.Height);           // 2:1 纵横比守恒
        Assert.Equal(4000, r.NativeWidth);      // 原生保留
        Assert.Equal("WGC", r.Source);
        Assert.False(r.WasAllBlack);
        // 注意不能用 "\x89PNG"u8：u8 字面量按 UTF-8 编码，\x89(>0x7F) 会变 C2 89 两字节
        ReadOnlySpan<byte> pngMagic = [0x89, 0x50, 0x4E, 0x47];
        Assert.True(r.Image.AsSpan(0, 4).SequenceEqual(pngMagic));
    }

    [Fact]
    public void Scale_BelowMax_NoScale_OutputEqualsNative()
    {
        using var src = Make(800, 600, Color.White);
        var r = ImagePipeline.Process(src, src.Size, 2000, "png", 80, "t", "WGC");
        Assert.Equal(800, r.Width);
        Assert.Equal(800, r.NativeWidth);
    }

    [Fact]
    public void Jpeg_Encodes_JpegMagic()
    {
        using var src = Make(300, 200, Color.Coral);
        var r = ImagePipeline.Process(src, src.Size, 2000, "jpeg", 50, null, "BitBlt");
        Assert.Equal(0xFF, r.Image[0]);
        Assert.Equal(0xD8, r.Image[1]);          // JPEG SOI
    }

    [Fact]
    public void AllBlack_Detected_True_OnPureBlack_False_OnAnyNonZeroPixel()
    {
        using (var black = Make(64, 64, Color.FromArgb(255, 0, 0, 0)))
        {
            var r = ImagePipeline.Process(black, black.Size, 2000, "png", 80, null, "BitBlt");
            Assert.True(r.WasAllBlack);
        }
        using (var nearly = Make(64, 64, Color.FromArgb(255, 1, 1, 1)))   // 任一通道非 0 即非黑
        {
            var r = ImagePipeline.Process(nearly, nearly.Size, 2000, "png", 80, null, "BitBlt");
            Assert.False(r.WasAllBlack);
        }
    }

    [Fact]
    public void WindowTitle_PassedThrough_And_ClippedDefault_False()
    {
        using var src = Make(50, 50, Color.Red);
        var r = ImagePipeline.Process(src, src.Size, 2000, "png", 80, "UiSample", "PrintWindow");
        Assert.Equal("UiSample", r.WindowTitle);
        Assert.False(r.ClippedToScreen);
    }

    [Fact]
    public void UnknownFormat_Throws_NotSilentlyFallsBack()
    {
        using var src = Make(10, 10, Color.Red);
        Assert.ThrowsAny<Exception>(() => ImagePipeline.Process(src, src.Size, 2000, "gif", 80, null, "WGC"));
    }
}
