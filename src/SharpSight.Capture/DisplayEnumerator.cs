using System.Drawing;
using System.Runtime.InteropServices;

namespace SharpSight.Capture;

/// <summary>
/// 显示器信息（<see cref="ScreenCapture.EnumerateDisplays"/> 返回；spec §3.3 公开面）。
/// <paramref name="Index"/> 为库侧 0 基、按 <c>EnumDisplayMonitors</c> 枚举序——
/// 1 基对外编号（<c>display=1/2</c>）与 primary/left/right 解析属宿主（Task 10），库侧不参与。
/// </summary>
/// <param name="Index">0 基枚举序（返回数组内连续）。</param>
/// <param name="DeviceName">设备名（<c>MONITORINFOEXW.szDevice</c>，如 \\.\DISPLAY1）。</param>
/// <param name="IsPrimary">是否主显示器（<c>MONITORINFOF_PRIMARY</c>）。</param>
/// <param name="Bounds">显示器矩形（虚拟屏物理像素；多屏原点可为负）。</param>
/// <param name="WorkArea">工作区矩形（排除任务栏等；虚拟屏物理像素）。</param>
/// <param name="Scale">有效 DPI 缩放（dpiX/96；spec §5）。</param>
public sealed record DisplayInfo(
    int Index, string DeviceName, bool IsPrimary, Rectangle Bounds, Rectangle WorkArea,
    double Scale);

/// <summary>
/// 显示器枚举（spec §4.2 mode=display）：<c>EnumDisplayMonitors</c> 回调的 lprcMonitor 可能为 NULL，
/// 矩形一律在回调内自行 <c>GetMonitorInfoW(MONITORINFOEXW)</c> 获取（必须设 <c>cbSize</c>）；
/// DPI 走 <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>。返回数组下标即 <see cref="DisplayInfo.Index"/>（0 基枚举序）。
/// 仅 Win32 查询、不读屏幕 DC，锁屏/无头环境亦可用。
/// </summary>
internal static class DisplayEnumerator
{
    private const int MonitorinfofPrimary = 0x1;
    private const int MdtEffectiveDpi = 0;          // MDT_EFFECTIVE_DPI
    private const int CchDeviceName = 32;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect lprcMonitor, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx info);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>按枚举序返回全部显示器（0 基 Index；单项 GetMonitorInfoW 失败即跳过，不影响其余）。</summary>
    public static DisplayInfo[] Enumerate()
    {
        var list = new List<DisplayInfo>();
        MonitorEnumProc cb = (hMonitor, _, ref _, _) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>(), szDevice = "" };
            if (!GetMonitorInfoW(hMonitor, ref info)) return true;
            var dpi = GetEffectiveDpi(hMonitor);
            list.Add(new DisplayInfo(
                list.Count, info.szDevice ?? "",
                (info.dwFlags & MonitorinfofPrimary) != 0,
                ToRect(info.rcMonitor), ToRect(info.rcWork),
                dpi / 96.0));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        return list.ToArray();
    }

    /// <summary>有效 DPI（失败/非正值回退 96，缩放置 1.0）。shcore 由 Win8.1+ 提供，缺失时容忍。</summary>
    private static int GetEffectiveDpi(IntPtr hMonitor)
    {
        try
        {
            if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out var x, out _) == 0 && x > 0)
                return (int)x;
        }
        catch (Exception) { /* 回退 96 */ }
        return 96;
    }

    private static Rectangle ToRect(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
