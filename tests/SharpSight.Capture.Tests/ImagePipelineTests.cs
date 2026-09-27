using System.Drawing;
using System.Drawing.Imaging;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

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
        var r = ImagePipeline.Process(src, src.Size, 2000, 2000, "t", "WGC");
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
        var r = ImagePipeline.Process(src, src.Size, 2000, 2000, "t", "WGC");
        Assert.Equal(800, r.Width);
        Assert.Equal(800, r.NativeWidth);
    }

    [Fact]
    public void AllBlack_Detected_True_OnPureBlack_False_OnAnyNonZeroPixel()
    {
        using (var black = Make(64, 64, Color.FromArgb(255, 0, 0, 0)))
        {
            var r = ImagePipeline.Process(black, black.Size, 2000, 2000, null, "BitBlt");
            Assert.True(r.WasAllBlack);
        }
        using (var nearly = Make(64, 64, Color.FromArgb(255, 1, 1, 1)))   // 任一通道非 0 即非黑
        {
            var r = ImagePipeline.Process(nearly, nearly.Size, 2000, 2000, null, "BitBlt");
            Assert.False(r.WasAllBlack);
        }
    }

    [Fact]
    public void WindowTitle_PassedThrough_And_ClippedDefault_False()
    {
        using var src = Make(50, 50, Color.Red);
        var r = ImagePipeline.Process(src, src.Size, 2000, 2000, "UiSample", "PrintWindow");
        Assert.Equal("UiSample", r.WindowTitle);
        Assert.False(r.ClippedToScreen);
    }

    // ===== 双轴降采样（T7；spec §6.2：k = min(1, maxW/W, maxH/H)，W/H=原生物理像素）=====

    [Theory]
    [InlineData(4000, 3000, 1568, 1568, 1568, 1176)]   // 长边受限（宽受限，等比、纵横比守恒）
    [InlineData(1000, 500, 1568, 1568, 1000, 500)]     // 未超限不放大
    [InlineData(4000, 1000, 1568, 600, 1568, 392)]     // 不等两轴取 min：宽受限，高按同一 k
    [InlineData(1000, 4000, 600, 1568, 392, 1568)]     // 不等两轴取 min 镜像：高受限
    [InlineData(4000, 2000, 0, 1000, 2000, 1000)]      // maxWidth=0 视为该轴不限，仅高受限
    [InlineData(4000, 2000, 1000, 0, 1000, 500)]       // maxHeight=0 视为该轴不限，仅宽受限
    [InlineData(800, 600, 0, 0, 800, 600)]             // 两轴皆 0 ⇒ 不缩放（k=1）
    [InlineData(800, 600, -1, -5, 800, 600)]           // 两轴皆负 ⇒ 不缩放
    public void Process_DualAxis(int w, int h, int mw, int mh, int ew, int eh)
    {
        using var src = Make(w, h, Color.SteelBlue);
        var r = ImagePipeline.Process(src, src.Size, mw, mh, null, "test");
        Assert.Equal(ew, r.Width);
        Assert.Equal(eh, r.Height);
    }

    // ===== 坐标模型（T3；spec §5 screen = origin + image / scale）=====

    [Fact]
    public void Origin_PassedThrough_And_Scale_FilledFromOutputOverNative()
    {
        using var src = Make(400, 300, Color.SteelBlue);
        var r = ImagePipeline.Process(src, src.Size, 200, 200, null, "BitBlt",
            clippedToScreen: false, originX: 120, originY: 45);
        Assert.Equal(120, r.OriginX);
        Assert.Equal(45, r.OriginY);
        Assert.Equal(0.5, r.Scale, 3);          // 输出 200 / 原生 400
        Assert.Equal(200, r.Width);
    }

    [Fact]
    public void Origin_DefaultsZero_ScaleOne_NewMetadataDefaults()
    {
        using var src = Make(50, 50, Color.Red);
        var r = ImagePipeline.Process(src, src.Size, 2000, 2000, null, "WGC");
        Assert.Equal(0, r.OriginX);
        Assert.Equal(0, r.OriginY);
        Assert.Equal(1.0, r.Scale, 3);
        Assert.Equal(0, r.FrameId);            // Task 10 宿主注入前恒 0
        Assert.Equal(-1, r.DisplayIndex);      // 未定位到具体显示器
        Assert.False(r.IsClientArea);
    }
}
