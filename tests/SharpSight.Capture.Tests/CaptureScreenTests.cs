using System.Drawing;
using System.Runtime.InteropServices;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// screen/region 抓取与换算单测（screenshot 计划 T4；spec §4.2 CaptureScreen、§3.1 k 换算、§5.2 屏外文案）。
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
            try { _ = ScreenCapture.CaptureScreen(new CaptureOptions(MaxWidth: 2000, MaxHeight: 2000)); _screenAvailable = true; }
            catch (CaptureException) { _screenAvailable = false; }
        }
        if (_screenAvailable == false)
            Assert.Skip("屏幕不可用（锁屏/安全桌面/无头环境拒绝 BitBlt），spec §6.4 预案——换算逻辑由 ResolveRegionClip 纯函数用例覆盖");
    }

    // ===== ① 真实抓取（锁屏自动 Skip）=====

    [Fact]
    public void Screen_Full_NoClip_MatchesVirtualScreenScaled()
    {
        SkipIfScreenUnavailable();
        var vw = GetSystemMetrics(78);   // SM_CXVIRTUALSCREEN
        var vh = GetSystemMetrics(79);   // SM_CYVIRTUALSCREEN
        var r = ScreenCapture.CaptureScreen(new CaptureOptions(MaxWidth: 2000, MaxHeight: 2000));
        var k = Math.Min(1.0, 2000.0 / Math.Max(vw, vh));
        Assert.Equal((int)Math.Round(vw * k), r.Width);
        Assert.Equal((int)Math.Round(vh * k), r.Height);
        Assert.Equal(vw, r.NativeWidth);
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
        // maxDimension 极大→k=1（任何分辨率下成立），图像空间=原生空间，断言直白
        var r = ScreenCapture.CaptureScreen(new CaptureOptions(new Rectangle(10, 10, 80, 60), 10000, 10000));
        Assert.Equal(80, r.Width);
        Assert.Equal(60, r.Height);
        Assert.Equal(80, r.NativeWidth);
        Assert.False(r.ClippedToScreen);
    }

    [Fact]
    public void Region_PartiallyOutside_RealCapture_ClipsIntersection_MarksClipped()
    {
        SkipIfScreenUnavailable();
        // 同参数下 k 恒等：先取 screen 图像空间宽度作越界构造基准
        var imgW = ScreenCapture.CaptureScreen(new CaptureOptions(MaxWidth: 2000, MaxHeight: 2000)).Width;
        var r = ScreenCapture.CaptureScreen(new CaptureOptions(new Rectangle(imgW - 40, 0, 200, 100), 2000, 2000));
        Assert.True(r.ClippedToScreen);                   // 部分越界=裁交集+头部注明（spec §5.2）
        Assert.True(r.Width > 0);
        Assert.True(r.Width <= imgW);                     // 交集不越出图像空间
    }

    [Fact]
    public void Region_FullyOutside_RealCapture_Throws_CaptureException_WithSpecMessage()
    {
        // 屏外判定在 GetDC 之前（纯换算），锁屏下也恒可跑
        var ex = Assert.Throws<CaptureException>(() =>
            ScreenCapture.CaptureScreen(new CaptureOptions(new Rectangle(99999, 99999, 10, 10), 2000, 2000)));
        Assert.Contains("完全在屏幕范围", ex.Message);   // spec §5.2 约定文案（Engine 生成、宿主透传）
        Assert.Contains("之外", ex.Message);
    }

    // ===== ①b 坐标模型（T3；spec §5 screen = origin + image / scale）=====
    // T7 起 Process 为真双轴：MaxWidth/MaxHeight 各自限制（≤0=该轴不限），region 与 screen 共用同一 k。

    [Fact]
    public void Region_NoScale_ReportsNativeOriginAndUnitScale()
    {
        SkipIfScreenUnavailable();
        var nx = GetSystemMetrics(76);   // SM_XVIRTUALSCREEN（多屏可为负）
        var ny = GetSystemMetrics(77);
        // 两轴上界极大 → k=1，图像空间与原生空间重合，断言直白
        var r = ScreenCapture.CaptureScreen(new CaptureOptions
        {
            Clip = new Rectangle(100, 100, 400, 300), MaxWidth = 100000, MaxHeight = 100000 });
        Assert.Equal(nx + 100, r.OriginX);   // origin=抓取矩形左上（虚拟屏物理像素）
        Assert.Equal(ny + 100, r.OriginY);
        Assert.Equal(1.0, r.Scale, 3);
    }

    [Fact]
    public void FullScreen_EqualMaxWidthMaxHeight_ReportsScaleAndVirtualScreenOrigin()
    {
        SkipIfScreenUnavailable();
        var nx = GetSystemMetrics(76);
        var ny = GetSystemMetrics(77);
        var vw = GetSystemMetrics(78);
        var vh = GetSystemMetrics(79);
        const int maxDim = 800;
        var r = ScreenCapture.CaptureScreen(new CaptureOptions { MaxWidth = maxDim, MaxHeight = maxDim });
        var k = Math.Min(1.0, (double)maxDim / Math.Max(vw, vh));
        Assert.Equal(nx, r.OriginX);         // 全屏 origin=虚拟屏左上
        Assert.Equal(ny, r.OriginY);
        Assert.Equal(k, r.Scale, 3);
        Assert.Equal((int)Math.Round(vw * k), r.Width);
    }

    [Fact]
    public void LegacySignature_Delegates_AndFillsOriginToo()
    {
        SkipIfScreenUnavailable();
        var nx = GetSystemMetrics(76);
        var ny = GetSystemMetrics(77);
        // 旧签名（宿主当前调用点）仍可用，且同样回填 origin（全屏=虚拟屏左上）
        var r = ScreenCapture.CaptureScreen(new CaptureOptions(MaxWidth: 100000, MaxHeight: 100000));
        Assert.Equal(nx, r.OriginX);
        Assert.Equal(ny, r.OriginY);
        Assert.Equal(1.0, r.Scale, 3);
    }

    // ===== ② 换算纯函数（不碰 GDI，任何环境恒跑；固定假屏幕：原点可为负的双屏纵排形态）=====

    private static readonly Rectangle FakeNative = new(0, -1080, 1920, 2160);

    [Fact]
    public void Region_Resolve_Inside_NoClip_ExactNativeRect()
    {
        // k=0.5、图像空间 960x1080、clip=(100,200,80,60) → 原生 (200,-680,160,120)
        var (nativeClip, clipped) = ScreenCapture.ResolveRegionClip(
            new Rectangle(100, 200, 80, 60), FakeNative, 0.5, 960, 1080);
        Assert.False(clipped);
        Assert.Equal(new Rectangle(200, -680, 160, 120), nativeClip);
    }

    [Fact]
    public void Region_Resolve_Partial_ClipsToImageBounds_MarksClipped()
    {
        // clip 右侧越界（900+200 > 960）→ 交集宽 60 → 原生 (1800,-1080,120,200)，紧贴虚拟屏右缘
        var (nativeClip, clipped) = ScreenCapture.ResolveRegionClip(
            new Rectangle(900, 0, 200, 100), FakeNative, 0.5, 960, 1080);
        Assert.True(clipped);
        Assert.Equal(new Rectangle(1800, -1080, 120, 200), nativeClip);
    }

    [Fact]
    public void Region_Resolve_Outside_Throws_WithImageSpaceDimensionsInMessage()
    {
        var ex = Assert.Throws<CaptureException>(() =>
            ScreenCapture.ResolveRegionClip(new Rectangle(99999, 0, 10, 10), FakeNative, 0.5, 960, 1080));
        // 文案中的 W/H=图像空间尺寸（agent 传参的同一坐标系，自查口径，spec §5.2）
        Assert.Equal("region (99999,0,10,10) 完全在屏幕范围 (960x1080) 之外。", ex.Message);
    }
}
