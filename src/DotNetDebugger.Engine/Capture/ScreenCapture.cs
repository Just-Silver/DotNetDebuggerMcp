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
}
