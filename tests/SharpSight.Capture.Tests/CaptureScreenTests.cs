using System.Drawing;
using System.Runtime.InteropServices;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// screen/region 抓取与换算单测（spec §4.2 CaptureScreen、§5.2 屏外文案；**不缩放**，图像空间与原生空间仅差 origin 平移）。
/// 分两层：① 真实抓取用例——屏幕 BitBlt 在锁屏/安全桌面/无头环境会被系统拒绝（实测 err=6，
/// 而 PrintWindow/WGC 不读屏幕 DC 不受影响），按 spec §6.4「探测失败即 Skip 不红」预案动态跳过；
/// ② ResolveRegionClip 换算纯函数用例——不碰 GDI，任何环境恒跑（锁屏下仍保证换算覆盖）。
/// </summary>
public sealed class CaptureScreenTests
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private static bool? _screenAvailable;

    /// <summary>探测屏幕可抓取性（一次缓存）；不可用即 Skip（spec §6.4）。</summary>
    private static void SkipIfScreenUnavailable()
    {
        if (_screenAvailable is null)
        {
            try { _ = ScreenCapture.CaptureScreen(new CaptureOptions()); _screenAvailable = true; }
            catch (CaptureException) { _screenAvailable = false; }
        }
        if (_screenAvailable == false)
            Assert.Skip("屏幕不可用（锁屏/安全桌面/无头环境拒绝 BitBlt），spec §6.4 预案——换算逻辑由 ResolveRegionClip 纯函数用例覆盖");
    }

    // ===== ① 真实抓取（锁屏自动 Skip）=====

    [Fact]
    public void Screen_Full_NoClip_MatchesVirtualScreenNativeSize()
    {
        SkipIfScreenUnavailable();
        var vw = GetSystemMetrics(78);   // SM_CXVIRTUALSCREEN
        var vh = GetSystemMetrics(79);   // SM_CYVIRTUALSCREEN
        var r = ScreenCapture.CaptureScreen(new CaptureOptions());
        Assert.Equal(vw, r.Width);                        // 不缩放：图像=虚拟屏原生尺寸
        Assert.Equal(vh, r.Height);
        Assert.Equal("BitBlt", r.Source);                 // screen/region 恒 GDI（spec §4.3-3）
        Assert.False(r.ClippedToScreen);
        Assert.Null(r.WindowTitle);
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47];
        Assert.True(r.Image.AsSpan(0, 4).SequenceEqual(png));
    }

    [Fact]
    public void Region_FullyInside_RealCapture_ReturnsClipSize_NotClipped()
    {
        SkipIfScreenUnavailable();
        var r = ScreenCapture.CaptureScreen(new CaptureOptions(new Rectangle(10, 10, 80, 60)));
        Assert.Equal(80, r.Width);
        Assert.Equal(60, r.Height);
        Assert.False(r.ClippedToScreen);
    }

    [Fact]
    public void Region_PartiallyOutside_RealCapture_ClipsIntersection_MarksClipped()
    {
        SkipIfScreenUnavailable();
        // 先取 screen 图像宽度作越界构造基准（图像=原生，故即虚拟屏宽）
        var imgW = ScreenCapture.CaptureScreen(new CaptureOptions()).Width;
        var r = ScreenCapture.CaptureScreen(new CaptureOptions(new Rectangle(imgW - 40, 0, 200, 100)));
        Assert.True(r.ClippedToScreen);                   // 部分越界=裁交集+头部注明（spec §5.2）
        Assert.True(r.Width > 0);
        Assert.True(r.Width <= imgW);                     // 交集不越出屏幕
    }

    [Fact]
    public void Region_FullyOutside_RealCapture_Throws_CaptureException_WithSpecMessage()
    {
        // 屏外判定在 GetDC 之前（纯换算），锁屏下也恒可跑
        var ex = Assert.Throws<CaptureException>(() =>
            ScreenCapture.CaptureScreen(new CaptureOptions(new Rectangle(99999, 99999, 10, 10))));
        Assert.Contains("完全在屏幕范围", ex.Message);   // spec §5.2 约定文案（库生成、宿主透传）
        Assert.Contains("之外", ex.Message);
    }

    // ===== ①b 坐标模型（spec §5：screen_x = 原点x + 图像x）=====

    [Fact]
    public void Region_ReportsNativeOrigin_AndClipSize()
    {
        SkipIfScreenUnavailable();
        var nx = GetSystemMetrics(76);   // SM_XVIRTUALSCREEN（多屏可为负）
        var ny = GetSystemMetrics(77);
        var r = ScreenCapture.CaptureScreen(new CaptureOptions { Clip = new Rectangle(100, 100, 400, 300) });
        Assert.Equal(nx + 100, r.OriginX);   // origin=抓取矩形左上（虚拟屏物理像素）
        Assert.Equal(ny + 100, r.OriginY);
        Assert.Equal(400, r.Width);          // 不缩放
        Assert.Equal(300, r.Height);
    }

    [Fact]
    public void FullScreen_ReportsVirtualScreenOriginAndSize()
    {
        SkipIfScreenUnavailable();
        var nx = GetSystemMetrics(76);
        var ny = GetSystemMetrics(77);
        var vw = GetSystemMetrics(78);
        var vh = GetSystemMetrics(79);
        var r = ScreenCapture.CaptureScreen(new CaptureOptions());
        Assert.Equal(nx, r.OriginX);         // 全屏 origin=虚拟屏左上
        Assert.Equal(ny, r.OriginY);
        Assert.Equal(vw, r.Width);
        Assert.Equal(vh, r.Height);
    }

    // ===== ② 换算纯函数（不碰 GDI，任何环境恒跑；固定假屏幕：原点可为负的双屏纵排形态）=====

    private static readonly Rectangle FakeNative = new(0, -1080, 1920, 2160);

    [Fact]
    public void Region_Resolve_Inside_NoClip_ExactNativeRect()
    {
        // 图像空间 (0,0,1920,2160) → 平移 origin(0,-1080)：clip=(100,200,80,60) → 原生 (100,-880,80,60)
        var (nativeClip, clipped) = ScreenCapture.ResolveRegionClip(
            new Rectangle(100, 200, 80, 60), FakeNative);
        Assert.False(clipped);
        Assert.Equal(new Rectangle(100, -880, 80, 60), nativeClip);
    }

    [Fact]
    public void Region_Resolve_Partial_ClipsToImageBounds_MarksClipped()
    {
        // clip 右侧越界（1900+200 > 1920）→ 交集宽 20 → 原生 (1900,-1080,20,100)，紧贴虚拟屏右缘
        var (nativeClip, clipped) = ScreenCapture.ResolveRegionClip(
            new Rectangle(1900, 0, 200, 100), FakeNative);
        Assert.True(clipped);
        Assert.Equal(new Rectangle(1900, -1080, 20, 100), nativeClip);
    }

    [Fact]
    public void Region_Resolve_Outside_Throws_WithImageSpaceDimensionsInMessage()
    {
        var ex = Assert.Throws<CaptureException>(() =>
            ScreenCapture.ResolveRegionClip(new Rectangle(99999, 0, 10, 10), FakeNative));
        // 文案中的 W/H=图像空间尺寸（agent 传参的同一坐标系，自查口径，spec §5.2）
        Assert.Equal("region (99999,0,10,10) 完全在屏幕范围 (1920x2160) 之外。", ex.Message);
    }
}
