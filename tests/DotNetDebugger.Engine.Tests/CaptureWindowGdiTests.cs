using DotNetDebugger.Engine.Capture;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// window 抓取（T4 阶段=GDI 两道；T5 接入 WGC 后本断言仍成立）单测
/// （screenshot 计划 T4；spec §6.2：非纯黑 + 尺寸与窗口 rect 一致，不断言精确像素）。
/// </summary>
public sealed class CaptureWindowGdiTests
{
    [Fact]
    public void CaptureWindow_UiSample_NonBlack_SizeMatchesWindowRect()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        var r = ScreenCapture.CaptureWindow(w!.Hwnd, 2000, "png", 80);

        Assert.False(r.WasAllBlack);
        Assert.Contains(r.Source, new[] { "PrintWindow", "BitBlt" });   // T4 尚无 WGC；不断言 WGC（spec §6.2）
        Assert.Equal(r.NativeWidth, r.Width);                            // UiSampleApp ~762px < 2000，不缩放
        Assert.True(Math.Abs(r.Width - w.Rect.Width) <= 2 && Math.Abs(r.Height - w.Rect.Height) <= 2,
            $"尺寸 {r.Width}x{r.Height} vs 窗口 {w.Rect.Width}x{w.Rect.Height}（±2 容差：DWM 阴影/边框取整）");
        Assert.Equal("UiSample", r.WindowTitle);
    }
}
