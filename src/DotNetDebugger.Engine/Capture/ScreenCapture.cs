using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DotNetDebugger.Engine.Capture;

/// <summary>
/// 窗口/屏幕截图门面（spec：docs/planning/specs/2026-09-22-screenshot-tool-design.md §4.2）。
/// 截图与 ICorDebug/命令泵零交互（§4.4），工具线程直接同步调用。
/// 首次调用统一设进程 DPI 为 Per-Monitor V2（全链物理像素，与 region 坐标空间定义一致；
/// 运行时调用、不用 manifest，已设置时容忍 ERROR_ACCESS_DENIED）。
/// T2 范围：DPI + FindMainWindow + GetWindowInfo；CaptureScreen（T4）/CaptureWindow（T5）随后追加。
/// </summary>
public static class ScreenCapture
{
    private static int _dpiSet;   // 0=未设 1=已设（幂等）
    private const int ProcessPerMonitorDpiAwareV2 = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);   // 2=GA_ROOT
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    /// <summary>幂等设置进程 DPI 感知（首次调用生效；失败=已由系统/调用方设置，容忍）。</summary>
    internal static void EnsureDpi()
    {
        if (Interlocked.CompareExchange(ref _dpiSet, 1, 0) != 0) return;
        SetProcessDpiAwarenessContext(new IntPtr(ProcessPerMonitorDpiAwareV2));
        // ERROR_ACCESS_DENIED(5)=已设置、ERROR_INVALID_PARAMETER(87)=系统过旧——均容忍继续
    }

    /// <summary>
    /// 枚举顶层可见窗口定位目标主窗（spec §3.1 定位规则，语义钉死「pid 优先、标题兜底」的择一）：
    /// <c>processId&gt;0</c> → 仅按 pid 匹配（titleSubstring 被忽略）；
    /// <c>processId=0</c> 且标题非空 → 仅按标题子串（忽略大小写，跨进程搜索）；
    /// 双空 → null（宿主保证先做会话兜底、不传双空）。
    /// EnumWindows 顺序即 Z 序（顶→底），首个命中即 Z 序最前主窗；MatchCount=全部命中数。
    /// </summary>
    public static WindowHandleInfo? FindMainWindow(int processId, string titleSubstring)
    {
        EnsureDpi();
        if (processId <= 0 && string.IsNullOrWhiteSpace(titleSubstring)) return null;

        WindowHandleInfo? first = null;
        var count = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetAncestor(h, 2 /*GA_ROOT*/) != h) return true;
            // 注意：返回值是线程 id，进程 id 只能取 out 参数（spike 实录 bug：拿返回值比 pid 会漏窗口）
            GetWindowThreadProcessId(h, out var pid);
            if (processId > 0)
            {
                if (pid != (uint)processId) return true;
            }
            else
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) < 0) return true;
            }
            count++;
            if (first is null)
            {
                GetWindowRect(h, out var r);
                var tsb = new StringBuilder(256);
                GetWindowText(h, tsb, tsb.Capacity);
                first = new WindowHandleInfo(h, tsb.ToString(),
                    new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    IsIconic(h), (int)pid, count);
            }
            return true;   // 继续枚举以统计 MatchCount
        }, IntPtr.Zero);

        if (first is null) return null;
        return first with { MatchCount = count };   // 补全计数，免二次 Win32 调用
    }

    /// <summary>取窗口基础信息（定位/头部/抓取共用）；hwnd 无效返回 null。</summary>
    internal static WindowHandleInfo? GetWindowInfo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        var tsb = new StringBuilder(256);
        GetWindowText(hwnd, tsb, tsb.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowHandleInfo(hwnd, tsb.ToString(),
            new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            IsIconic(hwnd), (int)pid, MatchCount: 1);
    }

    /// <summary>
    /// screen/region：GDI BitBlt 虚拟屏（spec §4.3-3：screen/region 不开 WGC）。
    /// clipInImageSpace=「mode=screen 返回图像素空间」坐标（宿主仅解析字符串格式，换算唯一在此）：
    /// 图像空间求交——空交集抛 spec §5.2 屏外约定错误（W/H 用图像空间尺寸，agent 可自查口径，
    /// 由 Engine 回传、宿主不触碰坐标换算）；非空交 → round 换算原生空间裁剪（BitBlt 只抓交集）；
    /// 交 ≠ 原始输入即 ClippedToScreen（部分越界=已裁交集，头部注明）。
    /// </summary>
    public static CaptureResult CaptureScreen(Rectangle? clipInImageSpace, int maxDimension, string format, int quality)
    {
        EnsureDpi();
        var native = GdiCapture.VirtualScreenRect();
        var k = Math.Min(1.0, (double)maxDimension / Math.Max(native.Width, native.Height));
        var imgW = Math.Max(1, (int)Math.Round(native.Width * k));
        var imgH = Math.Max(1, (int)Math.Round(native.Height * k));

        Rectangle nativeClip;
        var clipped = false;
        if (clipInImageSpace is { } c)
        {
            (nativeClip, clipped) = ResolveRegionClip(c, native, k, imgW, imgH);
        }
        else
        {
            nativeClip = native;
        }

        using var bmp = GdiCapture.CaptureScreenBits(nativeClip);
        return ImagePipeline.Process(bmp, native.Size, maxDimension, format, quality,
            windowTitle: null, sourceName: "BitBlt", clippedToScreen: clipped);
    }

    /// <summary>
    /// region 换算纯函数（自 CaptureScreen 提取——锁屏/无桌面环境下屏幕 BitBlt 不可用时，
    /// 换算逻辑仍可经此单测覆盖；spec §3.1 k 换算、§5.2 屏外/交集语义）：
    /// 图像空间求交（空=屏外抛约定错误，W/H 用图像空间尺寸）→ round 换算原生空间 → 夹紧虚拟屏
    /// → 交 ≠ 原输入即部分越界（ClippedToScreen）。
    /// </summary>
    internal static (Rectangle NativeClip, bool Clipped) ResolveRegionClip(
        Rectangle clip, Rectangle native, double k, int imgW, int imgH)
    {
        var inter = Rectangle.Intersect(clip, new Rectangle(0, 0, imgW, imgH));
        if (inter.IsEmpty)
            throw new CaptureException($"region ({clip.X},{clip.Y},{clip.Width},{clip.Height}) 完全在屏幕范围 ({imgW}x{imgH}) 之外。");
        var clipped = inter != clip;
        var nativeClip = new Rectangle(
            native.X + (int)Math.Round(inter.X / k),
            native.Y + (int)Math.Round(inter.Y / k),
            Math.Max(1, (int)Math.Round(inter.Width / k)),
            Math.Max(1, (int)Math.Round(inter.Height / k)));
        nativeClip.Intersect(native);   // round 兜底夹紧（最多溢出 1px）
        if (nativeClip.Width <= 0 || nativeClip.Height <= 0)
            throw new CaptureException($"region ({clip.X},{clip.Y},{clip.Width},{clip.Height}) 完全在屏幕范围 ({imgW}x{imgH}) 之外。");
        return (nativeClip, clipped);
    }

    /// <summary>
    /// window 抓取编排（spec §4.3 三道回退链）：
    /// WGC（DWM 取帧不黑图、被遮挡可截、无需置顶；出图即用——黑=真黑由头部备注）
    /// → PrintWindow → 采样纯黑则继续回退 → BitBlt（最小化窗口屏幕无内容，不回退）
    /// → 全失败抛约定错误。
    /// </summary>
    public static CaptureResult CaptureWindow(IntPtr hwnd, int maxDimension, string format, int quality)
    {
        EnsureDpi();
        const string failMsg = "窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。";
        var info = GetWindowInfo(hwnd) ?? throw new CaptureException(failMsg);

        // 第 1 道：WGC（正确性主力）
        Bitmap? bmp = WgcCapture.TryCaptureWindow(hwnd);
        var source = "WGC";

        // 第 2 道：PrintWindow → 采样纯黑则继续回退（spec §4.3-2）
        if (bmp is null)
        {
            bmp = GdiCapture.TryPrintWindow(hwnd);
            source = "PrintWindow";
            if (bmp is not null && ImagePipeline.IsAllBlack(bmp)) { bmp.Dispose(); bmp = null; }
        }

        // 第 3 道：BitBlt（最小化窗口屏幕无内容，不回退——spec §4.2 IsIconic 规则）
        if (bmp is null && !info.IsIconic)
        {
            bmp = GdiCapture.TryBitBltWindow(hwnd);
            source = "BitBlt";
        }

        if (bmp is null) throw new CaptureException(failMsg);

        using (bmp)
            return ImagePipeline.Process(bmp, bmp.Size, maxDimension, format, quality, info.Title, source);
    }
}
