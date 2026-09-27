using System.Drawing;
using System.Runtime.InteropServices;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// 窗口寻址扩展单测（screenshot 计划 T5；spec §3.3/§4.2/§5、spike-2026-09-27.md Spike A）：
/// FindWindowByHwnd/FindForegroundWindow 定位、GetWindowBounds 几何（DWM 去阴影/客户区）、
/// CaptureWindow(hwnd, clientArea) 客户区抓取与整窗 origin（WGC 帧 == 扩展边框）。
/// 真实起 UiSampleApp；抓屏失败（锁屏/无头/遮挡）按环境 Skip，不红（spec §6.4）。
/// </summary>
public sealed class CaptureWindowBoundsTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    // ===== FindWindowByHwnd =====

    [Fact]
    public void FindWindowByHwnd_UiSample_ReturnsVisibleRootWindow()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        var h = ScreenCapture.FindWindowByHwnd(w!.Hwnd);

        Assert.NotNull(h);
        Assert.Equal(w.Hwnd, h!.Hwnd);
        Assert.Equal(app.Process.Id, h.Pid);
        Assert.Equal("UiSample", h.Title);
    }

    [Fact]
    public void FindWindowByHwnd_InvalidHandle_ReturnsNull()
    {
        Assert.Null(ScreenCapture.FindWindowByHwnd(IntPtr.Zero));
        Assert.Null(ScreenCapture.FindWindowByHwnd(unchecked((IntPtr)0xDEAD0000)));
    }

    // ===== FindForegroundWindow =====

    [Fact]
    public void FindForegroundWindow_ReturnsForeground()
    {
        var fg = ScreenCapture.FindForegroundWindow();
        if (fg is null)
            Assert.Skip("本环境 GetForegroundWindow 返回 NULL（无头/服务会话），spec §6.4 预案");

        Assert.True(fg!.Hwnd != IntPtr.Zero);
        Assert.True(fg.Pid > 0);
        // 与 GetForegroundWindow 直接对照（容忍两次调用之间的前台切换）
        var raw = GetForegroundWindow();
        if (raw != IntPtr.Zero) Assert.Equal(raw, fg.Hwnd);
    }

    // ===== GetWindowBounds：DWM 扩展边框不含阴影，客户区落在扩展边框内 =====

    [Fact]
    public void WindowBounds_Relation()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        var h = ScreenCapture.FindWindowByHwnd(w!.Hwnd);
        Assert.NotNull(h);
        var b = ScreenCapture.GetWindowBounds(h!.Hwnd);
        Assert.NotNull(b);

        // 可见帧（去 DWM 阴影/不可见 resize 边框）不会大于含边框的 GetWindowRect
        Assert.True(b!.ExtendedFrame.Width > 0 && b.ExtendedFrame.Height > 0);
        Assert.True(b.ExtendedFrame.Width <= b.WindowRect.Width,
            $"扩展边框 {b.ExtendedFrame} 不应宽于窗口矩形 {b.WindowRect}");
        Assert.True(b.ExtendedFrame.Height <= b.WindowRect.Height,
            $"扩展边框 {b.ExtendedFrame} 不应高于窗口矩形 {b.WindowRect}");
        Assert.True(b.ExtendedFrame.Contains(b.ClientArea),
            $"扩展边框 {b.ExtendedFrame} 应包含客户区 {b.ClientArea}");
        Assert.True(b.ClientArea.Width > 0 && b.ClientArea.Height > 0);
    }

    // ===== CaptureWindow(hwnd, clientArea=true)：BitBlt 客户区屏幕矩形 =====

    [Fact]
    public void CaptureWindow_ClientArea_SizeAndOriginMatchClientBounds()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);
        var h = ScreenCapture.FindWindowByHwnd(w!.Hwnd);
        Assert.NotNull(h);
        var b = ScreenCapture.GetWindowBounds(h!.Hwnd);
        Assert.NotNull(b);

        CaptureResult r;
        try { r = ScreenCapture.CaptureWindow(h!.Hwnd, clientArea: true); }
        catch (CaptureException ex)
        {
            Assert.Skip("客户区 BitBlt 不可用（锁屏/安全桌面/无头环境），spec §6.4 预案：" + ex.Message);
            return;
        }

        Assert.True(r.IsClientArea);
        Assert.Equal("BitBlt", r.Source);
        Assert.Equal("UiSample", r.WindowTitle);
        Assert.Equal(b!.ClientArea.Width, r.NativeWidth);
        Assert.Equal(b.ClientArea.Height, r.NativeHeight);
        Assert.Equal(b.ClientArea.Width, r.Width);    // 默认 maxW/H=0 ⇒ 不缩放
        Assert.Equal(b.ClientArea.Height, r.Height);
        Assert.Equal(b.ClientArea.X, r.OriginX);      // origin=客户区屏幕左上（spec §5）
        Assert.Equal(b.ClientArea.Y, r.OriginY);
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47];
        Assert.True(r.Image.AsSpan(0, 4).SequenceEqual(png));
    }

    // ===== Spike A：整窗 origin 用扩展边框（WGC 帧 == EXTENDED_FRAME_BOUNDS）=====

    [Fact]
    public void CaptureWindow_WholeWindow_Wgc_OriginAndSizeUseExtendedFrame()
    {
        if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
            Assert.Skip("本环境不支持 Windows Graphics Capture（spec §6.4 预案）");

        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);
        var h = ScreenCapture.FindWindowByHwnd(w!.Hwnd);
        Assert.NotNull(h);
        var b = ScreenCapture.GetWindowBounds(h!.Hwnd);
        Assert.NotNull(b);

        var r = ScreenCapture.CaptureWindow(h!.Hwnd);
        if (r.Source != "WGC")
            Assert.Skip("WGC 本环境未走通（发生回退）——编排回退语义由 CaptureWindowWgcTests 承担");

        Assert.False(r.IsClientArea);
        Assert.Equal(b!.ExtendedFrame.X, r.OriginX);
        Assert.Equal(b.ExtendedFrame.Y, r.OriginY);
        Assert.Equal(b.ExtendedFrame.Width, r.NativeWidth);
        Assert.Equal(b.ExtendedFrame.Height, r.NativeHeight);
    }
}
