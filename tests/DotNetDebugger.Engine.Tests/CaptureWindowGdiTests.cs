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
    public void PrintWindow_UiSample_NonBlack_MatchesWindowRect()
    {
        // T5 接入 WGC 后 CaptureWindow 编排的 Source 恒为 WGC（成功时），GDI 路径改为直测
        // GdiCapture.TryPrintWindow 保持独立覆盖；编排/来源语义由 CaptureWindowWgcTests 承担。
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        using var bmp = GdiCapture.TryPrintWindow(w!.Hwnd);
        Assert.NotNull(bmp);
        Assert.False(ImagePipeline.IsAllBlack(bmp));
        // PrintWindow 按 GetWindowRect 精确作画（含边框带），±2 容差仅留取整余量
        Assert.True(Math.Abs(bmp.Width - w.Rect.Width) <= 2 && Math.Abs(bmp.Height - w.Rect.Height) <= 2,
            $"尺寸 {bmp.Width}x{bmp.Height} vs 窗口 {w.Rect.Width}x{w.Rect.Height}");
    }
}
