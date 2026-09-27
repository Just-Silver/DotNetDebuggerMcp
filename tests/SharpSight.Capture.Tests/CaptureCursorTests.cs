using System.Drawing;
using System.Runtime.InteropServices;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// includeCursor 单测（screenshot 计划 T9；spec §4.2/§6.1）：<see cref="CaptureOptions.IncludeCursor"/> 默认 false；
/// GDI 路径在缩放/编码前经 <see cref="GdiCapture.DrawCursor"/> 叠加——用已知系统光标做确定性验证
/// （屏幕点/热点/抓取原点换算，不依赖真实光标状态）；真实抓取走现有 helper，环境不可用按 spec §6.4 Skip 不红。
/// </summary>
public sealed class CaptureCursorTests
{
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr cursorName);

    private const int IdcArrow = 32512;   // IDC_ARROW（MAKEINTRESOURCE）

    [Fact]
    public void CaptureOptions_IncludeCursor_DefaultsFalse()
        => Assert.False(new CaptureOptions().IncludeCursor);

    /// <summary>确定性叠加：喂已知箭头光标，屏幕点 (200,150) − 原点 (100,100) − 热点 (4,4) = 位图局部 (96,46)；
    /// 断言该处出现非白像素、且靠左上空白区未被画到（热点/原点换算确实生效）。</summary>
    [Fact]
    public void DrawCursor_PlacesCursorAtScreenMinusOriginMinusHotspot()
    {
        var hCursor = LoadCursor(IntPtr.Zero, new IntPtr(IdcArrow));
        if (hCursor == IntPtr.Zero)
            Assert.Skip("系统箭头光标不可用（无桌面会话），spec §6.4 预案");

        using var bmp = new Bitmap(160, 160);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);

        Assert.True(GdiCapture.DrawCursor(bmp, hCursor, new Point(200, 150), new Point(4, 4), 100, 100));
        Assert.True(HasNonWhite(bmp, new Rectangle(96, 46, 40, 40)));
        Assert.False(HasNonWhite(bmp, new Rectangle(0, 0, 90, 90)));
    }

    private static bool HasNonWhite(Bitmap bmp, Rectangle r)
    {
        for (var y = r.Top; y < r.Bottom; y++)
            for (var x = r.Left; x < r.Right; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R != 255 || c.G != 255 || c.B != 255) return true;
            }
        return false;
    }

    // ===== 真实抓取（环境不可用即 Skip，spec §6.4）=====

    private static bool? _screenAvailable;

    private static void SkipIfScreenUnavailable()
    {
        if (_screenAvailable is null)
        {
            try { _ = ScreenCapture.CaptureScreen(new CaptureOptions()); _screenAvailable = true; }
            catch (CaptureException) { _screenAvailable = false; }
        }
        if (_screenAvailable == false)
            Assert.Skip("屏幕不可用（锁屏/安全桌面/无头环境拒绝 BitBlt），spec §6.4 预案");
    }

    [Fact]
    public void CaptureScreen_WithCursor_DoesNotThrow()
    {
        SkipIfScreenUnavailable();
        var r = ScreenCapture.CaptureScreen(new CaptureOptions { IncludeCursor = true });
        Assert.True(r.Width > 0 && r.Height > 0);
        Assert.StartsWith("BitBlt", r.Source);   // 叠加失败仅追加备注、来源前缀不变
    }

    [Fact]
    public void CaptureWindow_WithCursor_DoesNotThrow()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);

        CaptureResult r;
        try { r = ScreenCapture.CaptureWindow(w!.Hwnd, new CaptureOptions { IncludeCursor = true }); }
        catch (CaptureException ex)
        {
            Assert.Skip("窗口抓取不可用（锁屏/无头/回退链全失败），spec §6.4 预案：" + ex.Message);
            return;
        }
        Assert.True(r.Width > 0 && r.Height > 0);
    }
}
