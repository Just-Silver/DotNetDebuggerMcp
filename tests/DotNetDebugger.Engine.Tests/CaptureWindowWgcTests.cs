using DotNetDebugger.Engine.Capture;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// WGC 抓取单测（screenshot 计划 T5；spec §4.3 第 1 道、§6.2/§6.4：IsSupported 探测，不支持即 Skip 不红）。
/// 支持环境断言 Source=="WGC"——WGC 出图即用、不判黑，回退发生即链路 bug（区别于 GDI 测试的宽松口径）。
/// </summary>
public sealed class CaptureWindowWgcTests
{
    [Fact]
    public void Wgc_UiSample_Supported_CapturesNonBlack()
    {
        if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
            Assert.Skip("本环境不支持 Windows Graphics Capture（CI 无 GPU/无头预案，spec §6.4）");

        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        var r = ScreenCapture.CaptureWindow(w!.Hwnd, 2000, "png", 80);

        Assert.False(r.WasAllBlack);
        Assert.Equal("WGC", r.Source);          // 支持环境 WGC 必走通；发生回退=链路 bug
        Assert.Equal(r.NativeWidth, r.Width);   // UiSampleApp ~762px < 2000，不缩放
        // WGC 帧=窗口真实可见表面（实测 UiSampleApp 762x552），GetWindowRect 含 FixedSingle 边框/阴影带
        //（实测 776x559，差 14x7）——spec §6.2「尺寸与 rect 一致」按边框带放宽为有界区间 [rect-20, rect]：
        // WGC 不应大于 rect（截大=错源），且高度差不会到客户区误截程度（552 vs 客户区 520 会挂）。
        Assert.InRange(r.Width, w.Rect.Width - 20, w.Rect.Width);
        Assert.InRange(r.Height, w.Rect.Height - 20, w.Rect.Height);
        Assert.Equal("UiSample", r.WindowTitle);
    }
}
