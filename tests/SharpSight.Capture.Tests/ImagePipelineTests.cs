using System.Drawing;
using System.Drawing.Imaging;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// 图像管线单测（纯逻辑合成图，无进程依赖、快跑）：**输出恒为源图原生 1:1（不缩放）** +
/// 纯黑检测 + 固定 PNG 编码 + origin/元数据回填。缩放能力已于 2026-09-28 按用户裁定整条移除
/// （尺寸处理交模型侧，避免"我们自己缩一次、模型侧再处理一次"造成坐标口径混乱）。
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
    public void Process_LargeSource_OutputStaysOneToOne_WithPngMagic()
    {
        using var src = Make(4000, 2000, Color.SteelBlue);
        var r = ImagePipeline.Process(src, "t", "WGC");
        Assert.Equal(4000, r.Width);            // 不做任何缩放：输出=源图原生尺寸
        Assert.Equal(2000, r.Height);
        Assert.Equal("t", r.WindowTitle);
        Assert.Equal("WGC", r.Source);
        Assert.False(r.WasAllBlack);
        // 注意不能用 "\x89PNG"u8：u8 字面量按 UTF-8 编码，\x89(>0x7F) 会变 C2 89 两字节
        ReadOnlySpan<byte> pngMagic = [0x89, 0x50, 0x4E, 0x47];
        Assert.True(r.Image.AsSpan(0, 4).SequenceEqual(pngMagic));
    }

    [Fact]
    public void Process_SmallSource_NotUpscaled()
    {
        using var src = Make(800, 600, Color.White);
        var r = ImagePipeline.Process(src, "t", "WGC");
        Assert.Equal(800, r.Width);
        Assert.Equal(600, r.Height);
    }

    [Fact]
    public void AllBlack_Detected_True_OnPureBlack_False_OnAnyNonZeroPixel()
    {
        using (var black = Make(64, 64, Color.FromArgb(255, 0, 0, 0)))
        {
            var r = ImagePipeline.Process(black, null, "BitBlt");
            Assert.True(r.WasAllBlack);
        }
        using (var nearly = Make(64, 64, Color.FromArgb(255, 1, 1, 1)))   // 任一通道非 0 即非黑
        {
            var r = ImagePipeline.Process(nearly, null, "BitBlt");
            Assert.False(r.WasAllBlack);
        }
    }

    [Fact]
    public void WindowTitle_PassedThrough_And_ClippedDefault_False()
    {
        using var src = Make(50, 50, Color.Red);
        var r = ImagePipeline.Process(src, "UiSample", "PrintWindow");
        Assert.Equal("UiSample", r.WindowTitle);
        Assert.False(r.ClippedToScreen);
    }

    // ===== 坐标模型（spec §5：screen_x = 原点x + 图像x，图像为原生 1:1）=====

    [Fact]
    public void Origin_PassedThrough_OutputStaysOneToOne()
    {
        using var src = Make(400, 300, Color.SteelBlue);
        var r = ImagePipeline.Process(src, null, "BitBlt",
            clippedToScreen: false, originX: 120, originY: 45);
        Assert.Equal(120, r.OriginX);
        Assert.Equal(45, r.OriginY);
        Assert.Equal(400, r.Width);             // 不缩放：图像宽=源图宽
        Assert.Equal(300, r.Height);
    }

    [Fact]
    public void Origin_DefaultsZero_NewMetadataDefaults()
    {
        using var src = Make(50, 50, Color.Red);
        var r = ImagePipeline.Process(src, null, "WGC");
        Assert.Equal(0, r.OriginX);
        Assert.Equal(0, r.OriginY);
        Assert.Equal(0, r.FrameId);            // Task 10 宿主注入前恒 0
        Assert.Equal(-1, r.DisplayIndex);      // 未定位到具体显示器
        Assert.False(r.IsClientArea);
    }
}
