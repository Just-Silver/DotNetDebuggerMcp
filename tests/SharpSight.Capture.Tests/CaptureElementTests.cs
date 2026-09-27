using System.Drawing;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// 元素级抓取单测（screenshot 计划 T6；spec §7.1）：<see cref="ScreenCapture.CaptureElement"/> 先取顶层窗口
/// 帧（WGC→PrintWindow→BitBlt），再把元素物理矩形换算为帧内坐标裁剪（不对屏幕直接 BitBlt 裁元素）。
/// 真实起 UiSampleApp；抓屏失败（锁屏/无头/遮挡/WGC 回退全失败）按环境 Skip，不红（spec §6.4）。
/// </summary>
public sealed class CaptureElementTests
{
    [Fact]
    public void CaptureElement_CropsElementRectFromWindowFrame()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        var bounds = ScreenCapture.GetWindowBounds(w!.Hwnd);
        Assert.NotNull(bounds);
        if (bounds!.ClientArea.Width < 40 || bounds.ClientArea.Height < 40)
            Assert.Skip("客户区几何不可得（环境），spec §6.4 预案");

        // 取客户区内一小块（必落在窗口帧内，不会触发裁剪）——确定性断言尺寸/原点。
        var elementRect = new Rectangle(bounds.ClientArea.X + 10, bounds.ClientArea.Y + 10, 40, 40);

        CaptureResult r;
        try { r = ScreenCapture.CaptureElement(w.Hwnd, elementRect, new CaptureOptions()); }
        catch (CaptureException ex)
        {
            Assert.Skip("元素抓取不可用（锁屏/无头/遮挡/回退链全失败），spec §6.4 预案：" + ex.Message);
            return;
        }

        Assert.Equal(40, r.NativeWidth);                        // 默认不缩放（两轴 maxW/H=0 ⇒ k=1）
        Assert.Equal(40, r.NativeHeight);
        Assert.Equal(40, r.Width);
        Assert.Equal(40, r.Height);
        Assert.Equal(elementRect.X, r.OriginX);                 // origin=实际抓取矩形左上（spec §5）
        Assert.Equal(elementRect.Y, r.OriginY);
        Assert.Equal("UiSample", r.WindowTitle);
        Assert.False(r.ClippedToScreen);
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47];
        Assert.True(r.Image.AsSpan(0, 4).SequenceEqual(png));
    }

    [Fact]
    public void CaptureElement_PartlyOutsideFrame_ClipsToFrameAndFlags()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        var bounds = ScreenCapture.GetWindowBounds(w!.Hwnd);
        Assert.NotNull(bounds);
        // 一个跨越窗口帧右下角的大矩形：交 = 帧右下角一小块 → 部分裁剪。
        var elementRect = new Rectangle(bounds!.WindowRect.Right - 20, bounds.WindowRect.Bottom - 20, 200, 200);

        CaptureResult r;
        try { r = ScreenCapture.CaptureElement(w.Hwnd, elementRect, new CaptureOptions()); }
        catch (CaptureException ex)
        {
            Assert.Skip("元素抓取不可用（锁屏/无头/遮挡/回退链全失败），spec §6.4 预案：" + ex.Message);
            return;
        }

        Assert.True(r.ClippedToScreen);
        Assert.True(r.NativeWidth <= 20 && r.NativeHeight <= 20);
        Assert.True(r.NativeWidth > 0 && r.NativeHeight > 0);
    }

    [Fact]
    public void CaptureElement_EmptyRect_Throws()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        // 空矩形在抓屏前即拒绝（确定性，不依赖环境）。
        Assert.Throws<CaptureException>(() => ScreenCapture.CaptureElement(w!.Hwnd, Rectangle.Empty, new CaptureOptions()));
    }

    [Fact]
    public void CaptureElement_InvalidHwnd_Throws()
    {
        Assert.Throws<CaptureException>(() =>
            ScreenCapture.CaptureElement(IntPtr.Zero, new Rectangle(0, 0, 10, 10), new CaptureOptions()));
        Assert.Throws<CaptureException>(() =>
            ScreenCapture.CaptureElement(unchecked((IntPtr)0xDEAD0000), new Rectangle(0, 0, 10, 10), new CaptureOptions()));
    }
}
