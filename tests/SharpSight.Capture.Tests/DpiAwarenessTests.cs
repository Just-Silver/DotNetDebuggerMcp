using System.Runtime.InteropServices;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// DPI 感知回归（2026-09-28，batch3 F3）：<c>ProcessPerMonitorDpiAwareV2</c> 常量此前误写成 **+4**，
/// 而 <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c> 官方定义是 **-4**（负伪句柄）→
/// <c>SetProcessDpiAwarenessContext</c> 恒失败（ERROR_INVALID_PARAMETER 87），进程停留 DPI-UNAWARE；
/// 100% 缩放下二者无差别故一直未被发现，非 100% 时 GetMonitorInfo/截图全变「逻辑像素」。
/// 两条回归：① 常量必须是合法上下文（环境无关）；② 非 100% 显示器须按物理像素上报（无此环境则 Skip）。
/// </summary>
public sealed class DpiAwarenessTests
{
    private const int DpiAwarenessPerMonitorAware = 2;   // GetAwarenessFromDpiAwarenessContext 对 -3/-4 均返回 2

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TtOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [Fact]
    public void PerMonitorV2_ContextConstant_IsValid()
    {
        // +4（旧值）→ GetAwarenessFromDpiAwarenessContext 返回 -1（非法上下文）；
        // -4（正确值）→ 返回 2（DPI_AWARENESS_PER_MONITOR_AWARE）。
        var awareness = GetAwarenessFromDpiAwarenessContext(new IntPtr(ScreenCapture.ProcessPerMonitorDpiAwareV2));
        Assert.Equal(DpiAwarenessPerMonitorAware, awareness);
    }

    [Fact]
    public void EnumerateDisplays_Non100PercentMonitor_ReportsPhysicalPixels()
    {
        var displays = ScreenCapture.EnumerateDisplays();   // 入口先 EnsureDpi
        var scaled = displays.Where(d => Math.Abs(d.Scale - 1.0) > 0.001).ToArray();
        if (scaled.Length == 0)
            Assert.Skip("本机无 100% 以外缩放的显示器（常量用例已覆盖环境无关部分），spec §6.4 预案");
        // 若测试宿主已把进程 DPI 感知固定为其它值（EnsureDpi 无法覆盖），则不成立 → Skip 避免误红。
        if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) != DpiAwarenessPerMonitorAware)
            Assert.Skip("测试宿主已固定进程 DPI 感知、无法设为 PMv2（非本库可控），spec §6.4 预案");

        foreach (var d in scaled)
        {
            var dm = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsW(d.DeviceName, -1, ref dm))
                Assert.Skip($"EnumDisplaySettings 查询失败：{d.DeviceName}");
            // PMv2 下显示器矩形 = 物理像素（与真实显示模式一致）；UNAWARE 时会被系统按缩放缩小（F3 缺陷）。
            Assert.Equal(dm.PelsWidth, d.Bounds.Width);
            Assert.Equal(dm.PelsHeight, d.Bounds.Height);
        }
    }
}
