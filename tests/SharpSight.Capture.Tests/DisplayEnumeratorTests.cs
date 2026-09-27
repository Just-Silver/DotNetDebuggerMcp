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
            try { _ = ScreenCapture.CaptureDisplay(0, new CaptureOptions()); _screenAvailable = true; }
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
        var r = ScreenCapture.CaptureDisplay(0, new CaptureOptions());
        Assert.Equal(0, r.DisplayIndex);
        Assert.Equal(d0.Bounds.X, r.OriginX);
        Assert.Equal(d0.Bounds.Y, r.OriginY);
        Assert.Equal(d0.Bounds.Width, r.Width);      // 不缩放：输出=该显示器原生尺寸
        Assert.Equal(d0.Bounds.Height, r.Height);
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
        var ex = Assert.Throws<CaptureException>(() => ScreenCapture.CaptureDisplay(n, new CaptureOptions()));
        Assert.Contains("不存在", ex.Message);
        Assert.Throws<CaptureException>(() => ScreenCapture.CaptureDisplay(-1, new CaptureOptions()));
    }

    // ===== R21 回归：display 抓取以「该显示器矩形」为基（非整虚拟屏）=====

    [Fact]
    public void CaptureDisplay_ReturnsThatDisplayBounds_NotVirtualScreen()
    {
        SkipIfScreenUnavailable();
        var ds = ScreenCapture.EnumerateDisplays();
        if (ds.Length == 0) Assert.Skip("未枚举到显示器");
        // 多屏优先取第二块：R21 缺陷（display 矩形被当虚拟屏 Clip）正在副屏路径复现（交集为空/夹回整屏）。
        var idx = ds.Length >= 2 ? 1 : 0;
        var d = ds[idx];

        var r = ScreenCapture.CaptureDisplay(idx, new CaptureOptions());

        Assert.Equal(d.Index, r.DisplayIndex);
        Assert.Equal(d.Bounds.X, r.OriginX);                 // origin=该显示器左上
        Assert.Equal(d.Bounds.Y, r.OriginY);
        Assert.Equal(d.Bounds.Width, r.Width);               // 尺寸=该显示器矩形（非整虚拟屏）
        Assert.Equal(d.Bounds.Height, r.Height);
    }

    [Fact]
    public void CaptureDisplay_SecondMonitor_DoesNotThrow_OriginIsThatMonitor()
    {
        SkipIfScreenUnavailable();
        var ds = ScreenCapture.EnumerateDisplays();
        if (ds.Length < 2)
            Assert.Skip("单显示器环境：display=2 的 R21 回归（旧实现会抛「完全在屏幕范围之外」）需多屏才可复现");
        var d = ds[1];
        var r = ScreenCapture.CaptureDisplay(1, new CaptureOptions());
        Assert.Equal(1, r.DisplayIndex);
        Assert.Equal(d.Bounds.X, r.OriginX);
        Assert.Equal(d.Bounds.Y, r.OriginY);
        Assert.Equal(d.Bounds.Width, r.Width);
        Assert.Equal(d.Bounds.Height, r.Height);
    }
}
