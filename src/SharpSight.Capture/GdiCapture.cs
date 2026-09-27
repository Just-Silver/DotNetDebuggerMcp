using System.Drawing;
using System.Runtime.InteropServices;

namespace SharpSight.Capture;

/// <summary>
/// GDI 抓取（spec §4.3 回退链第 2/3 道 + screen/region 唯一路径）：
/// PrintWindow 让窗口把自己画出来（不怕遮挡，现代 DComposition 窗口可能画黑→调用方回退）；
/// BitBlt 从屏幕剪矩形（被遮挡处截到遮挡物，Source=BitBlt 即 best-effort 信号）。
/// </summary>
internal static class GdiCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const uint SrccopyWithCaptureBlt = 0x40CC0020;   // SRCCOPY | CAPTUREBLT
    private const uint CursorShowing = 0x0001;               // CURSORINFO.flags
    private const int DiNormal = 0x0003;                     // DrawIconEx：画图标+掩码（DI_IMAGE|DI_MASK）

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;   // 调用前必须填 Marshal.SizeOf<CursorInfo>()
        public uint flags;   // CURSOR_SHOWING=0x1
        public IntPtr hCursor;
        public Point ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
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

    /// <summary>
    /// 把光标图标画到 <paramref name="target"/>（Task 9，GDI 路径手动叠加光标，scale/encode 之前）：
    /// 屏幕点 <paramref name="screenPos"/> 减去热点与抓取原点（虚拟屏物理像素）得位图局部左上——热点对齐保证
    /// 与真实指针位置一致。供 <see cref="TryOverlayCursor"/> 与单测共用（单测传已知系统光标句柄，确定性、
    /// 不依赖真实光标状态）；GDI 异常吞掉返回 false（调用方仅备注、不报错）。
    /// </summary>
    internal static bool DrawCursor(Bitmap target, IntPtr hCursor, Point screenPos, Point hotspot,
        int originX, int originY)
    {
        try
        {
            using var g = Graphics.FromImage(target);
            var hdc = g.GetHdc();
            try
            {
                return DrawIconEx(hdc, screenPos.X - hotspot.X - originX, screenPos.Y - hotspot.Y - originY,
                    hCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
            }
            finally { g.ReleaseHdc(hdc); }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 取当前光标并叠加到 <paramref name="target"/>（Task 9）：<c>GetCursorInfo</c> → 无可见光标=无事可做
    /// （返回 true，不算失败）→ <c>GetIconInfo</c>（hbmColor/hbmMask 用完 <c>DeleteObject</c> 释放，防 GDI 句柄泄漏）
    /// → <see cref="DrawCursor"/>。查询/取图标失败返回 false，由调用方在 <see cref="CaptureResult.Source"/> 备注「光标未叠加」。
    /// </summary>
    public static bool TryOverlayCursor(Bitmap target, int originX, int originY)
    {
        var ci = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref ci)) return false;
        if ((ci.flags & CursorShowing) == 0) return true;   // 光标未显示=无需叠加（非失败）
        if (!GetIconInfo(ci.hCursor, out var ii)) return false;
        try
        {
            return DrawCursor(target, ci.hCursor, ci.ptScreenPos,
                new Point(ii.xHotspot, ii.yHotspot), originX, originY);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
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
