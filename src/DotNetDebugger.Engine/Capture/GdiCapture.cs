using System.Drawing;
using System.Runtime.InteropServices;

namespace DotNetDebugger.Engine.Capture;

/// <summary>
/// GDI 抓取（spec §4.3 回退链第 2/3 道 + screen/region 唯一路径）：
/// PrintWindow 让窗口把自己画出来（不怕遮挡，现代 DComposition 窗口可能画黑→调用方回退）；
/// BitBlt 从屏幕剪矩形（被遮挡处截到遮挡物，Source=BitBlt 即 best-effort 信号）。
/// </summary>
internal static class GdiCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const uint SrccopyWithCaptureBlt = 0x40CC0020;   // SRCCOPY | CAPTUREBLT

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int srcX, int srcY, uint rop);   // GDI 函数在 gdi32（勿写 user32——实测 EntryPointNotFound）
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>第 2 道：PrintWindow(PW_RENDERFULLCONTENT) 让窗口自画。失败返回 null 由调用方回退。</summary>
    public static Bitmap? TryPrintWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        try
        {
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            var hdc = g.GetHdc();
            var ok = PrintWindow(hwnd, hdc, PwRenderFullContent);
            g.ReleaseHdc(hdc);
            if (!ok) { bmp.Dispose(); return null; }
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>第 3 道：BitBlt 屏幕上该窗口矩形（遮挡敏感；最小化窗口由调用方跳过）。</summary>
    public static Bitmap? TryBitBltWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            var dst = g.GetHdc();
            var ok = BitBlt(dst, 0, 0, w, h, screen, r.Left, r.Top, SrccopyWithCaptureBlt);
            g.ReleaseHdc(dst);
            if (!ok) { bmp.Dispose(); return null; }
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>screen/region：BitBlt 虚拟屏指定原生区域。GetDC/BitBlt 失败抛 spec §5.2 约定错误。</summary>
    public static Bitmap CaptureScreenBits(Rectangle nativeClip)
    {
        const string failMsg = "窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。";
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new CaptureException(failMsg);
        try
        {
            var bmp = new Bitmap(nativeClip.Width, nativeClip.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            var dst = g.GetHdc();
            var ok = BitBlt(dst, 0, 0, nativeClip.Width, nativeClip.Height,
                screen, nativeClip.X, nativeClip.Y, SrccopyWithCaptureBlt);
            g.ReleaseHdc(dst);
            if (!ok) { bmp.Dispose(); throw new CaptureException(failMsg); }
            return bmp;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>虚拟屏原生矩形（SM_X/Y/CX/CYVIRTUALSCREEN=76/77/78/79；原点可为负）。</summary>
    internal static Rectangle VirtualScreenRect()
    {
        var x = GetSystemMetrics(76);
        var y = GetSystemMetrics(77);
        var w = GetSystemMetrics(78);
        var h = GetSystemMetrics(79);
        return new Rectangle(x, y, w, h);
    }
}
