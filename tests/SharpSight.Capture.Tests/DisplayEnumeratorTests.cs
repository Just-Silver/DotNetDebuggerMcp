using System.Drawing;
using System.Runtime.InteropServices;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// 显示器枚举与逐屏捕获单测（screenshot 计划 T4；spec §3.3 公开面、§4.2 mode=display）。
/// 索引语义：库侧 0 基、按 <c>EnumDisplayMonitors</c> 枚举序；1 基对外编号（display=1/2）与
/// primary/left/right 解析属 Task 10，本套不做。
/// 真实抓屏断言沿用 CaptureScreenTests 的探测 Skip 预案（锁屏/安全桌面/无头环境拒 BitBlt，spec §6.4）。
/// </summary>
public sealed class DisplayEnumeratorTests
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private static bool? _screenAvailable;

    /// <summary>探测逐屏 BitBlt 可抓取性（一次缓存）；不可用即 Skip（与 CaptureScreenTests 同款预案）。</summary>
    private static void SkipIfScreenUnavailable()
    {
        if (_screenAvailable is null)
        {
            try { _ = ScreenCapture.CaptureDisplay(0); _screenAvailable = true; }
            catch (CaptureException) { _screenAvailable = false; }
        }
        if (_screenAvailable == false)
            Assert.Skip("屏幕不可用（锁屏/安全桌面/无头环境拒绝 BitBlt），spec §6.4 预案——枚举与无缩放语义另有恒跑用例覆盖");
    }

    // ===== 枚举（仅 Win32 查询、不读屏幕 DC，任意桌面环境恒跑）=====

    [Fact]
    public void EnumerateDisplays_HasExactlyOnePrimary_AndSequentialZeroBasedIndices()
    {
        var ds = ScreenCapture.EnumerateDisplays();
        Assert.NotEmpty(ds);
        Assert.Equal(1, ds.Count(d => d.IsPrimary));
        Assert.All(ds, d => Assert.True(d.Bounds.Width > 0 && d.Bounds.Height > 0));
        for (var i = 0; i < ds.Length; i++) Assert.Equal(i, ds[i].Index);   // 0 基、按枚举序
    }

    [Fact]
    public void EnumerateDisplays_Count_MatchesSystemMonitorCount()
    {
        var ds = ScreenCapture.EnumerateDisplays();
        Assert.Equal(GetSystemMetrics(80) /*SM_CMONITORS*/, ds.Length);
    }

    [Fact]
    public void EnumerateDisplays_DeviceNamesUnique_WorkAreaInsideBounds_ScaleSane()
    {
        var ds = ScreenCapture.EnumerateDisplays();
        Assert.All(ds, d =>
        {
            Assert.False(string.IsNullOrEmpty(d.DeviceName));
            Assert.True(d.Bounds.Contains(d.WorkArea), $"工作区 {d.WorkArea} 应在显示器范围 {d.Bounds} 内");
            Assert.InRange(d.Scale, 1.0, 8.0);   // Windows 缩放 100%–500% → Scale=DPI/96 ∈ [1,5]
        });
        Assert.Equal(ds.Length, ds.Select(d => d.DeviceName).Distinct().Count());
    }

    // ===== 逐屏捕获（锁屏自动 Skip）=====

    [Fact]
    public void CaptureDisplay_Index0_BoundsSized_NoScale_WithOriginAndDisplayIndex()
    {
        SkipIfScreenUnavailable();
        var d0 = ScreenCapture.EnumerateDisplays()[0];
        var r = ScreenCapture.CaptureDisplay(0);
        Assert.Equal(0, r.DisplayIndex);
        Assert.Equal(d0.Bounds.X, r.OriginX);
        Assert.Equal(d0.Bounds.Y, r.OriginY);
        Assert.Equal(d0.Bounds.Width, r.Width);      // 无缩放（R10：两轴 0 ⇒ k=1）
        Assert.Equal(d0.Bounds.Height, r.Height);
        Assert.Equal(1.0, r.Scale, 3);
        Assert.Equal("BitBlt", r.Source);
        Assert.Null(r.WindowTitle);
        Assert.False(r.ClippedToScreen);
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47];
        Assert.True(r.Image.AsSpan(0, 4).SequenceEqual(png));
    }

    [Fact]
    public void CaptureDisplay_OutOfRange_Throws_CaptureException()
    {
        // 越界判定在 BitBlt 之前（纯校验），锁屏下亦恒跑
        var n = ScreenCapture.EnumerateDisplays().Length;
        var ex = Assert.Throws<CaptureException>(() => ScreenCapture.CaptureDisplay(n));
        Assert.Contains("不存在", ex.Message);
        Assert.Throws<CaptureException>(() => ScreenCapture.CaptureDisplay(-1));
    }

    // ===== R10：两轴皆 0/负 ⇒ 不缩放（k=1），去掉 Task 3 的「兜底 2000」=====

    [Fact]
    public void ResolveMaxDimension_BothAxesNonPositive_MeansUnbounded_NoScale()
    {
        Assert.Equal(int.MaxValue, ScreenCapture.ResolveMaxDimension(0, 0));
        Assert.Equal(int.MaxValue, ScreenCapture.ResolveMaxDimension(-1, -5));
        Assert.Equal(800, ScreenCapture.ResolveMaxDimension(800, 0));     // 单轴正值仍作单值上限
        Assert.Equal(600, ScreenCapture.ResolveMaxDimension(0, 600));
        Assert.Equal(800, ScreenCapture.ResolveMaxDimension(800, 900));   // R8 临时：双轴正值取 min
    }
}
